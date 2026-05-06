using System.Collections.Generic;
using System.Dynamic;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Atlas.Internal;

/// <summary>
/// Codegen for dynamic TypeMaps (TypeMap.IsDynamic == true).
/// See docs/Atlas-Design-DynamicMapping.md §5 (dict→POCO) and §6 (POCO→dict).
/// </summary>
internal static class DynamicPlanBuilder
{
    // Cached reflection members reused across all emitted lambdas.
    private static readonly MethodInfo _convertObjectTo = typeof(MappingInvoker)
        .GetMethod(nameof(MappingInvoker.ConvertObjectTo), BindingFlags.Public | BindingFlags.Static)!;
    private static readonly MethodInfo _convertObjectToList = typeof(MappingInvoker)
        .GetMethod(nameof(MappingInvoker.ConvertObjectToList), BindingFlags.Public | BindingFlags.Static)!;
    private static readonly MethodInfo _convertObjectToArray = typeof(MappingInvoker)
        .GetMethod(nameof(MappingInvoker.ConvertObjectToArray), BindingFlags.Public | BindingFlags.Static)!;
    private static readonly MethodInfo _invokeMethod = typeof(MappingInvoker)
        .GetMethod(nameof(MappingInvoker.Invoke), BindingFlags.Public | BindingFlags.Static)!;
    private static readonly MethodInfo _invokeUpdateMethod = typeof(MappingInvoker)
        .GetMethod(nameof(MappingInvoker.InvokeUpdate), BindingFlags.Public | BindingFlags.Static)!;
    private static readonly MethodInfo _scanPrefix = typeof(MappingInvoker)
        .GetMethod(nameof(MappingInvoker.ScanPrefix), BindingFlags.Public | BindingFlags.Static)!;
    private static readonly ConstructorInfo _atlasMappingExceptionCtor =
        typeof(AtlasMappingException).GetConstructor(new[] { typeof(string) })!;
    private static readonly MethodInfo _serializeValue = typeof(MappingInvoker)
        .GetMethod(nameof(MappingInvoker.SerializeValue), BindingFlags.Public | BindingFlags.Static)!;
    private static readonly MethodInfo _serializeCollection = typeof(MappingInvoker)
        .GetMethod(nameof(MappingInvoker.SerializeCollection), BindingFlags.Public | BindingFlags.Static)!;
    private static readonly MethodInfo _serializeDictionary = typeof(MappingInvoker)
        .GetMethod(nameof(MappingInvoker.SerializeDictionary), BindingFlags.Public | BindingFlags.Static)!;

    private static readonly Type _dictType = typeof(IDictionary<string, object>);

    public static LambdaExpression Build(TypeMap typeMap, MapperRegistry registry)
    {
        if (DynamicShape.IsDynamicShape(typeMap.SourceType))
            return BuildDictToPocoLambda(typeMap, registry);
        else
            return BuildPocoToDictLambda(typeMap, registry);
    }

    public static LambdaExpression BuildUpdate(TypeMap typeMap, MapperRegistry registry)
    {
        if (DynamicShape.IsDynamicShape(typeMap.SourceType))
            return BuildDictToPocoUpdateLambda(typeMap, registry);
        else
            return BuildPocoToDictUpdateLambda(typeMap, registry);
    }

    /// <remarks>
    /// Case-sensitivity asymmetry (v1 documented behavior):
    /// Top-level dictionary lookups use <see cref="IDictionary{TKey,TValue}.TryGetValue"/>, which
    /// honors whatever <see cref="IEqualityComparer{T}"/> the source dictionary was constructed with
    /// (typically <see cref="StringComparer.Ordinal"/> for a plain <c>new Dictionary&lt;string,object&gt;</c>).
    /// The <c>CaseSensitive</c> convention setting only affects the dot-notation prefix scan
    /// (<see cref="MappingInvoker.ScanPrefix"/>). To get fully case-insensitive matching end-to-end
    /// (top-level AND dot-notation), users must both:
    ///   (1) construct their source dictionary with <c>StringComparer.OrdinalIgnoreCase</c>, AND
    ///   (2) configure <c>MapperConfigurationExpression.CaseSensitive = false</c>.
    /// </remarks>
    private static LambdaExpression BuildDictToPocoLambda(TypeMap typeMap, MapperRegistry registry)
    {
        // Route based on destination type characteristics.
        // 1. Types with required properties: use property-init with mandatory-key checks.
        // 2. Types with parameterless ctor (and no required props): standard property-init.
        // 3. Everything else (records, primary-ctor classes): ctor-init path.
        if (HasRequiredProperties(typeMap.DestinationType))
            return BuildDictToPocoLambda_RequiredInit(typeMap, registry);
        if (typeMap.DestinationType.GetConstructor(Type.EmptyTypes) is not null)
            return BuildDictToPocoLambda_PropertyInit(typeMap, registry);
        return BuildDictToPocoLambda_CtorInit(typeMap, registry);
    }

    /// <summary>
    /// Standard property-init path: allocates via parameterless ctor then assigns each property.
    /// Used for normal POCOs with parameterless constructors and no required properties.
    /// </summary>
    private static LambdaExpression BuildDictToPocoLambda_PropertyInit(TypeMap typeMap, MapperRegistry registry)
    {
        var srcParam = Expression.Parameter(typeMap.SourceType, "src");
        var srcAsDict = CoerceToDict(srcParam, typeMap.SourceType);

        var dst = Expression.Variable(typeMap.DestinationType, "dst");
        var body = new List<Expression> { Expression.Assign(dst, Expression.New(typeMap.DestinationType)) };

        var tryGetValue = _dictType.GetMethod(nameof(IDictionary<string, object>.TryGetValue))!;
        var registryConst = Expression.Constant(registry);
        var cmpConst = BuildCmpConst(registry);

        foreach (var pm in typeMap.PropertyMaps)
        {
            if (pm.DynamicKey is null || pm.DestinationProperty is null) continue;

            var propExpr = EmitPropertyAssign(
                pm, srcAsDict, dst, registryConst, cmpConst, tryGetValue, updateInPlace: false);
            if (propExpr is not null)
                body.Add(propExpr);
        }

        body.Add(dst);

        var block = Expression.Block(new[] { dst }, body);
        return Expression.Lambda(block, srcParam);
    }

    /// <summary>
    /// Required-property path: same as property-init but emits a runtime throw for required
    /// properties whose key is missing from the source dictionary.
    /// </summary>
    private static LambdaExpression BuildDictToPocoLambda_RequiredInit(TypeMap typeMap, MapperRegistry registry)
    {
        var srcParam = Expression.Parameter(typeMap.SourceType, "src");
        var srcAsDict = CoerceToDict(srcParam, typeMap.SourceType);

        var dst = Expression.Variable(typeMap.DestinationType, "dst");
        var body = new List<Expression> { Expression.Assign(dst, Expression.New(typeMap.DestinationType)) };

        var tryGetValue = _dictType.GetMethod(nameof(IDictionary<string, object>.TryGetValue))!;
        var registryConst = Expression.Constant(registry);
        var cmpConst = BuildCmpConst(registry);

        foreach (var pm in typeMap.PropertyMaps)
        {
            if (pm.DynamicKey is null || pm.DestinationProperty is null) continue;

            var propInfo = pm.DestinationProperty;
            var isRequired = propInfo.GetCustomAttribute<RequiredMemberAttribute>() is not null;

            if (isRequired)
            {
                // Emit: if (!src.TryGetValue(key, out v)) throw AtlasMappingException("'Name' is required...")
                //       else dst.Prop = ConvertObjectTo<T>(v, ...)
                var keyStr = pm.DynamicKey!;
                var keyExpr = Expression.Constant(keyStr, typeof(string));
                var valueVar = Expression.Variable(typeof(object), "v_" + keyStr);
                var hasValue = Expression.Variable(typeof(bool), "h_" + keyStr);
                var propType = propInfo.PropertyType;
                var dstPropExpr = Expression.Property(dst, propInfo);

                var convertCall = Expression.Call(
                    _convertObjectTo.MakeGenericMethod(propType),
                    valueVar, registryConst, keyExpr);
                var assign = Expression.Assign(dstPropExpr, convertCall);

                var throwExpr = Expression.Throw(
                    Expression.New(
                        _atlasMappingExceptionCtor,
                        Expression.Constant($"'{keyStr}' is required but missing from source dictionary.")));

                body.Add(Expression.Block(
                    new[] { valueVar, hasValue },
                    Expression.Assign(hasValue, Expression.Call(srcAsDict, tryGetValue, keyExpr, valueVar)),
                    Expression.IfThenElse(hasValue, assign, throwExpr)));
            }
            else
            {
                var propExpr = EmitPropertyAssign(
                    pm, srcAsDict, dst, registryConst, cmpConst, tryGetValue, updateInPlace: false);
                if (propExpr is not null)
                    body.Add(propExpr);
            }
        }

        body.Add(dst);

        var block = Expression.Block(new[] { dst }, body);
        return Expression.Lambda(block, srcParam);
    }

    /// <summary>
    /// Constructor-init path: picks the best public constructor (most parameters), maps dict
    /// keys to ctor params, then sets any remaining init-only/settable properties.
    /// Used for records, primary constructors, and other ctor-only types.
    /// </summary>
    private static LambdaExpression BuildDictToPocoLambda_CtorInit(TypeMap typeMap, MapperRegistry registry)
    {
        var srcParam = Expression.Parameter(typeMap.SourceType, "src");
        var srcAsDict = CoerceToDict(srcParam, typeMap.SourceType);

        var ctor = SelectBestConstructor(typeMap.DestinationType);
        var ctorParams = ctor.GetParameters();

        var tryGetValue = _dictType.GetMethod(nameof(IDictionary<string, object>.TryGetValue))!;
        var registryConst = Expression.Constant(registry);
        var cmpConst = BuildCmpConst(registry);

        var paramVars = new ParameterExpression[ctorParams.Length];
        var bodyStatements = new List<Expression>();
        var allLocals = new List<ParameterExpression>();

        for (int i = 0; i < ctorParams.Length; i++)
        {
            var p = ctorParams[i];
            var paramVar = Expression.Variable(p.ParameterType, "p_" + p.Name);
            paramVars[i] = paramVar;
            allLocals.Add(paramVar);

            // Use the parameter name as dict key (matches DynamicKey convention: PascalCase property name).
            // Records use PascalCase parameter names (e.g., record Foo(int Id, string Name) → params "Id", "Name").
            // We match case-insensitively against the TypeMap's PropertyMaps to find the DynamicKey.
            var dynamicKey = FindDynamicKeyForCtorParam(typeMap, p.Name!);
            var keyStr = dynamicKey ?? p.Name!;
            var keyExpr = Expression.Constant(keyStr, typeof(string));
            var valueVar = Expression.Variable(typeof(object), "v_" + p.Name);
            var hasValue = Expression.Variable(typeof(bool), "h_" + p.Name);

            var convertCall = Expression.Call(
                _convertObjectTo.MakeGenericMethod(p.ParameterType),
                valueVar, registryConst, keyExpr);
            var assignParam = Expression.Assign(paramVar, convertCall);

            Expression defaultOrThrow = p.HasDefaultValue
                ? (Expression)Expression.Assign(paramVar,
                    Expression.Constant(p.DefaultValue, p.ParameterType))
                : Expression.Throw(
                    Expression.New(
                        _atlasMappingExceptionCtor,
                        Expression.Constant($"'{keyStr}' is required but missing from source dictionary.")));

            bodyStatements.Add(Expression.Block(
                new[] { valueVar, hasValue },
                Expression.Assign(hasValue, Expression.Call(srcAsDict, tryGetValue, keyExpr, valueVar)),
                Expression.IfThenElse(hasValue, assignParam, defaultOrThrow)));
        }

        var dst = Expression.Variable(typeMap.DestinationType, "dst");
        allLocals.Add(dst);
        bodyStatements.Add(Expression.Assign(dst, Expression.New(ctor, paramVars.Cast<Expression>())));

        // After construction, populate any init-only / settable properties NOT covered by ctor params.
        var ctorParamNames = new HashSet<string>(
            ctorParams.Select(p => p.Name!), StringComparer.OrdinalIgnoreCase);

        foreach (var pm in typeMap.PropertyMaps)
        {
            if (pm.DynamicKey is null || pm.DestinationProperty is null) continue;
            // Skip if already covered by the ctor
            if (ctorParamNames.Contains(pm.DestinationProperty.Name)) continue;
            if (!pm.DestinationProperty.CanWrite) continue;

            var propExpr = EmitPropertyAssign(
                pm, srcAsDict, dst, registryConst, cmpConst, tryGetValue, updateInPlace: false);
            if (propExpr is not null)
                bodyStatements.Add(propExpr);
        }

        bodyStatements.Add(dst);

        var block = Expression.Block(allLocals, bodyStatements);
        return Expression.Lambda(block, srcParam);
    }

    /// <summary>
    /// Update-in-place: takes the existing destination as a parameter.
    /// Missing dict keys leave existing destination values untouched (IfThen, no else branch).
    /// </summary>
    private static LambdaExpression BuildDictToPocoUpdateLambda(TypeMap typeMap, MapperRegistry registry)
    {
        var srcParam = Expression.Parameter(typeMap.SourceType, "src");
        var dstParam = Expression.Parameter(typeMap.DestinationType, "dst");
        var srcAsDict = CoerceToDict(srcParam, typeMap.SourceType);

        var tryGetValue = _dictType.GetMethod(nameof(IDictionary<string, object>.TryGetValue))!;
        var registryConst = Expression.Constant(registry);
        var cmpConst = BuildCmpConst(registry);

        var statements = new List<Expression>();

        foreach (var pm in typeMap.PropertyMaps)
        {
            if (pm.DynamicKey is null || pm.DestinationProperty is null) continue;
            if (!pm.DestinationProperty.CanWrite) continue;

            var propExpr = EmitPropertyAssign(
                pm, srcAsDict, dstParam, registryConst, cmpConst, tryGetValue, updateInPlace: true);
            if (propExpr is not null)
                statements.Add(propExpr);
        }

        Expression body = statements.Count > 0
            ? Expression.Block(statements)
            : Expression.Empty();

        // Null-guard: only execute body if src is not null.
        if (typeMap.SourceType.IsClass)
        {
            body = Expression.IfThen(
                Expression.Not(Expression.ReferenceEqual(srcParam, Expression.Constant(null, typeMap.SourceType))),
                body);
        }

        return Expression.Lambda(body, srcParam, dstParam);
    }

    private static LambdaExpression BuildPocoToDictUpdateLambda(TypeMap typeMap, MapperRegistry registry)
    {
        var srcParam = Expression.Parameter(typeMap.SourceType, "src");
        var dstParam = Expression.Parameter(typeMap.DestinationType, "dst");

        // Coerce destination to IDictionary<string, object> for indexer assignment.
        var dstAsDict = Expression.Variable(typeof(IDictionary<string, object>), "dstDict");
        var indexer = typeof(IDictionary<string, object>).GetProperty("Item")!;
        var registryConst = Expression.Constant(registry);

        var body = new List<Expression>
        {
            Expression.Assign(dstAsDict,
                typeMap.DestinationType == typeof(IDictionary<string, object>)
                    ? (Expression)dstParam
                    : Expression.Convert(dstParam, typeof(IDictionary<string, object>)))
        };

        foreach (var pm in typeMap.PropertyMaps)
        {
            if (pm.DynamicKey is null || pm.SourcePath is null || pm.SourcePath.Members.Count == 0) continue;
            var serializeExpr = EmitSourceMemberSerialize(pm, srcParam, registryConst);
            var assign = Expression.Assign(
                Expression.MakeIndex(dstAsDict, indexer, new[] { Expression.Constant(pm.DynamicKey, typeof(string)) }),
                serializeExpr);
            body.Add(assign);
        }

        // Return type must be void for update lambdas; add Expression.Empty() as last stmt.
        body.Add(Expression.Empty());

        Expression blockExpr = Expression.Block(new[] { dstAsDict }, body);

        // Null-guard: only execute body if src is not null (symmetry with BuildDictToPocoUpdateLambda).
        if (typeMap.SourceType.IsClass)
        {
            blockExpr = Expression.IfThen(
                Expression.Not(Expression.ReferenceEqual(srcParam, Expression.Constant(null, typeMap.SourceType))),
                blockExpr);
        }

        return Expression.Lambda(blockExpr, srcParam, dstParam);
    }

    /// <summary>
    /// Emits the assignment expression for a single property.
    /// When <paramref name="updateInPlace"/> is true, wraps in IfThen (missing key preserves existing value).
    /// Returns null if the property should be skipped.
    /// </summary>
    private static Expression? EmitPropertyAssign(
        PropertyMap pm,
        Expression srcAsDict,
        Expression dst,
        Expression registryConst,
        Expression cmpConst,
        MethodInfo tryGetValue,
        bool updateInPlace)
    {
        var propInfo = pm.DestinationProperty!;
        var propType = propInfo.PropertyType;
        var keyStr = pm.DynamicKey!;
        var keyExpr = Expression.Constant(keyStr, typeof(string));
        var valueVar = Expression.Variable(typeof(object), "v_" + keyStr);
        var hasValue = Expression.Variable(typeof(bool), "h_" + keyStr);
        var dstPropExpr = Expression.Property(dst, propInfo);

        // Determine which branch to use based on property type
        var collectionElementType = GetCollectionElementType(propType, out var isArray);
        var isPocoLike = collectionElementType is null && DynamicShape.IsPocoLike(propType);

        if (isPocoLike)
        {
            // Nested POCO branch
            return EmitNestedPocoAssign(
                keyStr, keyExpr, propType, valueVar, hasValue,
                dstPropExpr, srcAsDict, registryConst, cmpConst, tryGetValue,
                updateInPlace);
        }
        else if (collectionElementType is not null)
        {
            // Collection branch: List<T>, T[], or IEnumerable<T>
            return EmitCollectionAssign(
                keyExpr, propType, collectionElementType, isArray, valueVar, hasValue,
                dstPropExpr, srcAsDict, registryConst, tryGetValue);
        }
        else
        {
            // Scalar/primitive branch: ConvertObjectTo<TProp>
            var convertCall = Expression.Call(
                _convertObjectTo.MakeGenericMethod(propType),
                valueVar, registryConst, keyExpr);
            var assign = Expression.Assign(dstPropExpr, convertCall);
            return Expression.Block(
                new[] { valueVar, hasValue },
                Expression.Assign(hasValue, Expression.Call(srcAsDict, tryGetValue, keyExpr, valueVar)),
                Expression.IfThen(hasValue, assign));
        }
    }

    /// <summary>
    /// Emits the nested-POCO assignment block with three sub-paths:
    /// 1. Top-level key is a nested dict   → recursive Invoke (create) or InvokeUpdate (update-in-place)
    /// 2. Top-level key is already the POCO type → direct assign (Assert.Same)
    /// 3. No top-level key, but dot-notation prefixed keys exist → ScanPrefix + recursive Invoke/InvokeUpdate
    ///
    /// When <paramref name="updateInPlace"/> is true and the property type has a parameterless
    /// constructor, the dict branch calls <see cref="MappingInvoker.InvokeUpdate{TSource,TDestination}"/>
    /// (merge semantics: existing sibling fields are preserved). If the property type has no
    /// parameterless ctor, it falls back to <see cref="MappingInvoker.Invoke{TSource,TDestination}"/>
    /// (overwrite). The ScanPrefix fallback branch follows the same logic.
    ///
    /// When <paramref name="updateInPlace"/> is false, both the dict branch and the ScanPrefix
    /// branch always use <see cref="MappingInvoker.Invoke{TSource,TDestination}"/> (create-new semantics).
    ///
    /// The else branch (TryGetValue miss with no dot-notation match) is always wired up; under
    /// update-in-place the ScanPrefix IfThen has no else, so a complete miss leaves the existing
    /// destination value untouched.
    /// </summary>
    private static Expression EmitNestedPocoAssign(
        string keyStr,
        Expression keyExpr,
        Type propType,
        ParameterExpression valueVar,
        ParameterExpression hasValue,
        Expression dstPropExpr,
        Expression srcAsDict,
        Expression registryConst,
        Expression cmpConst,
        MethodInfo tryGetValue,
        bool updateInPlace)
    {
        // Closed Invoke<IDictionary<string, object>, TProp>
        var closedInvoke = _invokeMethod.MakeGenericMethod(_dictType, propType);

        // Whether the property type supports merge semantics (has a parameterless ctor).
        var hasCtor = propType.GetConstructor(Type.EmptyTypes) is not null;

        // Nested dict variable for the scan-prefix fallback
        var prefixStr = keyStr + ".";
        var prefixExpr = Expression.Constant(prefixStr, typeof(string));
        var nestedDictVar = Expression.Variable(_dictType, "nd_" + keyStr);

        // Branch A (inside TryGetValue hit):
        //   if (valueVar is IDictionary<string, object> nd) dst.Prop = Invoke/InvokeUpdate(registry, nd, ...)
        //   else if (valueVar is TProp typed)              dst.Prop = typed
        //   else if (valueVar is null)                     dst.Prop = null / default
        //   else throw AtlasMappingException
        var nestedDictCastVar = Expression.Variable(_dictType, "ndc_" + keyStr);
        var typedVar = Expression.Variable(propType, "tc_" + keyStr);

        // null-assign: dst.Prop = default(TProp)  (null for reference types)
        var nullAssign = Expression.Assign(dstPropExpr, Expression.Default(propType));

        // dict branch: create path (non-update) → dst.Prop = Invoke(registry, nestedDict)
        // dict branch: update path with ctor   → dst.Prop ??= new TProp(); InvokeUpdate(registry, nestedDict, dst.Prop)
        // dict branch: update path without ctor → dst.Prop = Invoke(registry, nestedDict)  [fallback: overwrite]
        Expression dictBranchBody;
        if (updateInPlace && hasCtor)
        {
            // dst.Prop ??= new TPropType();
            // InvokeUpdate<IDictionary<string,object>, TPropType>(registry, nestedDictCastVar, dst.Prop)
            var closedInvokeUpdate = _invokeUpdateMethod.MakeGenericMethod(_dictType, propType);
            dictBranchBody = Expression.Block(
                Expression.IfThen(
                    Expression.Equal(dstPropExpr, Expression.Constant(null, propType)),
                    Expression.Assign(dstPropExpr, Expression.New(propType))),
                Expression.Call(closedInvokeUpdate, registryConst, nestedDictCastVar, dstPropExpr));
        }
        else
        {
            dictBranchBody = Expression.Assign(dstPropExpr,
                Expression.Call(closedInvoke, registryConst, nestedDictCastVar));
        }

        // typed branch: dst.Prop = (TProp)valueVar
        var typedBranchAssign = Expression.Assign(dstPropExpr, typedVar);

        // throw branch (I-2: use cached ctor; I-3: compile-time constant message)
        var throwExpr = Expression.Throw(
            Expression.New(
                _atlasMappingExceptionCtor,
                Expression.Constant($"Cannot convert value at key '{keyStr}' to '{propType.Name}'.")));

        // Build the if-else chain for when TryGetValue hits
        var ifNullBranch = Expression.IfThenElse(
            Expression.Equal(valueVar, Expression.Constant(null, typeof(object))),
            nullAssign,
            throwExpr);

        var ifTypedBranch = Expression.IfThenElse(
            Expression.TypeIs(valueVar, propType),
            Expression.Block(
                new[] { typedVar },
                Expression.Assign(typedVar, Expression.Convert(valueVar, propType)),
                typedBranchAssign),
            ifNullBranch);

        var ifDictBranch = Expression.IfThenElse(
            Expression.TypeIs(valueVar, _dictType),
            Expression.Block(
                new[] { nestedDictCastVar },
                Expression.Assign(nestedDictCastVar, Expression.Convert(valueVar, _dictType)),
                dictBranchBody),
            ifTypedBranch);

        // Else branch (TryGetValue missed): ScanPrefix dot-notation fallback
        //   var nestedDict = ScanPrefix(src, "Prop.", cmp);
        //   if (nestedDict != null)
        //     update path with ctor:    dst.Prop ??= new TProp(); InvokeUpdate(registry, nestedDict, dst.Prop)
        //     otherwise:                dst.Prop = Invoke(registry, nestedDict)
        var scanCall = Expression.Call(_scanPrefix, srcAsDict, prefixExpr, cmpConst);
        Expression scanFoundBody;
        if (updateInPlace && hasCtor)
        {
            var closedInvokeUpdate = _invokeUpdateMethod.MakeGenericMethod(_dictType, propType);
            scanFoundBody = Expression.Block(
                Expression.IfThen(
                    Expression.Equal(dstPropExpr, Expression.Constant(null, propType)),
                    Expression.Assign(dstPropExpr, Expression.New(propType))),
                Expression.Call(closedInvokeUpdate, registryConst, nestedDictVar, dstPropExpr));
        }
        else
        {
            scanFoundBody = Expression.Assign(dstPropExpr,
                Expression.Call(closedInvoke, registryConst, nestedDictVar));
        }

        var elseBranch = Expression.Block(
            new[] { nestedDictVar },
            Expression.Assign(nestedDictVar, scanCall),
            Expression.IfThen(
                Expression.NotEqual(nestedDictVar, Expression.Constant(null, _dictType)),
                scanFoundBody));

        return Expression.Block(
            new[] { valueVar, hasValue },
            Expression.Assign(hasValue, Expression.Call(srcAsDict, tryGetValue, keyExpr, valueVar)),
            Expression.IfThenElse(hasValue, ifDictBranch, elseBranch));
    }

    /// <summary>
    /// Emits the collection assignment block using ConvertObjectToList or ConvertObjectToArray.
    /// </summary>
    private static Expression EmitCollectionAssign(
        Expression keyExpr,
        Type propType,
        Type elementType,
        bool isArray,
        ParameterExpression valueVar,
        ParameterExpression hasValue,
        Expression dstPropExpr,
        Expression srcAsDict,
        Expression registryConst,
        MethodInfo tryGetValue)
    {
        MethodInfo closedMethod;
        if (isArray)
            closedMethod = _convertObjectToArray.MakeGenericMethod(elementType);
        else
            closedMethod = _convertObjectToList.MakeGenericMethod(elementType);

        var convertCall = Expression.Call(closedMethod, valueVar, registryConst, keyExpr);
        var assign = Expression.Assign(dstPropExpr, convertCall);

        return Expression.Block(
            new[] { valueVar, hasValue },
            Expression.Assign(hasValue, Expression.Call(srcAsDict, tryGetValue, keyExpr, valueVar)),
            Expression.IfThen(hasValue, assign));
    }

    private static LambdaExpression BuildPocoToDictLambda(TypeMap typeMap, MapperRegistry registry)
    {
        var srcParam = Expression.Parameter(typeMap.SourceType, "src");
        var dstType = typeMap.DestinationType;

        // Concrete-type contract per design §3.3:
        //   ExpandoObject              -> new ExpandoObject()
        //   Dictionary<string, object> -> new Dictionary<string, object>(capacity)
        //   IDictionary<string, object> -> new ExpandoObject() (declared as the abstraction)
        var dstConcreteType = dstType == typeof(Dictionary<string, object>)
            ? typeof(Dictionary<string, object>)
            : typeof(ExpandoObject);

        var dstAsConcrete = Expression.Variable(dstConcreteType, "dstConcrete");
        var dstAsDict = Expression.Variable(typeof(IDictionary<string, object>), "dst");

        Expression newDstExpr = dstConcreteType == typeof(Dictionary<string, object>)
            ? Expression.New(typeof(Dictionary<string, object>).GetConstructor(new[] { typeof(int) })!,
                  Expression.Constant(typeMap.PropertyMaps.Count))
            : Expression.New(typeof(ExpandoObject));

        var body = new List<Expression>
        {
            Expression.Assign(dstAsConcrete, newDstExpr),
            Expression.Assign(dstAsDict,
                dstConcreteType == typeof(Dictionary<string, object>)
                    ? (Expression)dstAsConcrete
                    : Expression.Convert(dstAsConcrete, typeof(IDictionary<string, object>)))
        };

        var indexer = typeof(IDictionary<string, object>).GetProperty("Item")!;
        var registryConst = Expression.Constant(registry);

        foreach (var pm in typeMap.PropertyMaps)
        {
            if (pm.DynamicKey is null || pm.SourcePath is null || pm.SourcePath.Members.Count == 0) continue;
            var serializeExpr = EmitSourceMemberSerialize(pm, srcParam, registryConst);
            var assign = Expression.Assign(
                Expression.MakeIndex(dstAsDict, indexer, new[] { Expression.Constant(pm.DynamicKey, typeof(string)) }),
                serializeExpr);
            body.Add(assign);
        }

        body.Add(dstType == dstConcreteType
            ? (Expression)dstAsConcrete
            : Expression.Convert(dstAsConcrete, dstType));

        var block = Expression.Block(new[] { dstAsConcrete, dstAsDict }, body);
        return Expression.Lambda(block, srcParam);
    }

    /// <summary>
    /// Returns an <c>Expression</c> of type <c>object?</c> that serializes the source member
    /// described by <paramref name="pm"/> to a dict-safe value:
    /// <list type="bullet">
    ///   <item>Generic collection (List&lt;T&gt;, T[], IEnumerable&lt;T&gt;, …) → SerializeCollection&lt;T&gt;</item>
    ///   <item>Typed dictionary (Dictionary&lt;TKey, TValue&gt; where TValue is not object) → SerializeDictionary&lt;TKey, TValue&gt;</item>
    ///   <item>Everything else (primitive, enum, nested POCO, string, …) → SerializeValue (Task 7 path)</item>
    /// </list>
    /// </summary>
    private static Expression EmitSourceMemberSerialize(
        PropertyMap pm,
        ParameterExpression srcParam,
        Expression registryConst)
    {
        var srcMember = pm.SourcePath!.Members[0];
        var srcMemberType = srcMember.PropertyType;
        var memberAccess = Expression.Property(srcParam, srcMember);

        // 1. Generic collection (but NOT Dictionary itself — that's handled separately below)?
        var collectionElementType = DynamicShape.GetCollectionElementType(srcMemberType);
        if (collectionElementType is not null)
        {
            // SerializeCollection<TElement>(src.Member, registry) returns List<object?>?
            // Cast result to object? for the dict indexer assignment.
            var closedMethod = _serializeCollection.MakeGenericMethod(collectionElementType);
            var call = Expression.Call(closedMethod, memberAccess, registryConst);
            return Expression.Convert(call, typeof(object));
        }

        // 2. Dictionary<TKey, TValue> where TValue is NOT object (avoid treating dynamic shapes as typed dicts)?
        if (IsTypedPocoDictionary(srcMemberType, out var keyType, out var valueType))
        {
            // SerializeDictionary<TKey, TValue>(src.Member, registry) returns IDictionary<string, object?>?
            // Cast to object? for the dict indexer.
            var closedMethod = _serializeDictionary.MakeGenericMethod(keyType!, valueType!);

            // The parameter type for SerializeDictionary is IDictionary<TKey, TValue>; cast if needed.
            var iDictType = typeof(IDictionary<,>).MakeGenericType(keyType!, valueType!);
            var argExpr = srcMemberType == iDictType
                ? (Expression)memberAccess
                : Expression.Convert(memberAccess, iDictType);

            var call = Expression.Call(closedMethod, argExpr, registryConst);
            return Expression.Convert(call, typeof(object));
        }

        // 3. Dynamic shape (Dictionary<string, object>, IDictionary<string, object>, ExpandoObject):
        //    pass through as-is — store the source instance directly under the destination key.
        if (DynamicShape.IsDynamicShape(srcMemberType))
        {
            return Expression.Convert(memberAccess, typeof(object));
        }

        // 4. Scalar / enum / nested POCO / string — existing Task 7 SerializeValue path.
        var boxed = Expression.Convert(memberAccess, typeof(object));
        return Expression.Call(_serializeValue, boxed,
            Expression.Constant(srcMemberType, typeof(Type)), registryConst);
    }

    /// <summary>
    /// Returns true if <paramref name="t"/> is a closed <c>Dictionary&lt;TKey, TValue&gt;</c>
    /// (or its IDictionary&lt;TKey, TValue&gt; interface variant) where TValue is not
    /// <c>object</c> (to avoid confusing it with the dynamic shapes themselves).
    /// Sets <paramref name="keyType"/> and <paramref name="valueType"/> on success.
    /// </summary>
    private static bool IsTypedPocoDictionary(Type t, out Type? keyType, out Type? valueType)
    {
        if (t.IsGenericType)
        {
            var def = t.GetGenericTypeDefinition();
            if (def == typeof(Dictionary<,>) || def == typeof(IDictionary<,>))
            {
                var args = t.GetGenericArguments();
                // Exclude Dictionary<string, object> — that is a dynamic shape, not a typed POCO dict.
                if (args[1] != typeof(object))
                {
                    keyType = args[0];
                    valueType = args[1];
                    return true;
                }
            }
        }
        keyType = null;
        valueType = null;
        return false;
    }

    // ---- Helpers ----

    /// <summary>Coerce source parameter to IDictionary&lt;string, object&gt; if not already that type.</summary>
    private static Expression CoerceToDict(ParameterExpression srcParam, Type sourceType)
        => sourceType == _dictType
            ? (Expression)srcParam
            : Expression.Convert(srcParam, _dictType);

    private static Expression BuildCmpConst(MapperRegistry registry)
        => Expression.Constant(
            registry.ConventionOptions.CaseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns true if the type has any public instance property decorated with RequiredMemberAttribute.</summary>
    private static bool HasRequiredProperties(Type t)
    {
        foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetCustomAttribute<RequiredMemberAttribute>() is not null)
                return true;
        }
        return false;
    }

    /// <summary>Returns the public constructor with the most parameters (for records and primary ctors).</summary>
    private static ConstructorInfo SelectBestConstructor(Type t)
    {
        var ctors = t.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        if (ctors.Length == 0)
            throw new AtlasMappingException(
                $"Type '{t.FullName}' has no public constructors; cannot map from dynamic source.");
        return ctors.OrderByDescending(c => c.GetParameters().Length).First();
    }

    /// <summary>
    /// Finds the DynamicKey in TypeMap.PropertyMaps for a given constructor parameter name
    /// (case-insensitive match against DestinationProperty.Name). Returns the DynamicKey if found,
    /// or null (caller falls back to the raw parameter name as key).
    /// </summary>
    private static string? FindDynamicKeyForCtorParam(TypeMap typeMap, string paramName)
    {
        foreach (var pm in typeMap.PropertyMaps)
        {
            if (pm.DestinationProperty is not null &&
                string.Equals(pm.DestinationProperty.Name, paramName, StringComparison.OrdinalIgnoreCase))
                return pm.DynamicKey;
        }
        return null;
    }

    /// <summary>
    /// Delegates to <see cref="DynamicShape.GetCollectionElementType"/> (the canonical widened version
    /// that recognizes T[], List&lt;T&gt;, IList&lt;T&gt;, ICollection&lt;T&gt;, IEnumerable&lt;T&gt;,
    /// IReadOnlyList&lt;T&gt;, and IReadOnlyCollection&lt;T&gt;) and sets <paramref name="isArray"/> for
    /// the array case. For all non-array collection interfaces (IList&lt;T&gt;, ICollection&lt;T&gt;,
    /// IReadOnlyList&lt;T&gt;, IReadOnlyCollection&lt;T&gt;, IEnumerable&lt;T&gt;) the codegen
    /// materializes a List&lt;T&gt;, which implements every one of these interfaces.
    /// </summary>
    private static Type? GetCollectionElementType(Type t, out bool isArray)
    {
        isArray = t.IsArray;
        return DynamicShape.GetCollectionElementType(t);
    }
}
