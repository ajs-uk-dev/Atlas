using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;

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
    private static readonly MethodInfo _scanPrefix = typeof(MappingInvoker)
        .GetMethod(nameof(MappingInvoker.ScanPrefix), BindingFlags.Public | BindingFlags.Static)!;

    private static readonly Type _dictType = typeof(IDictionary<string, object>);

    public static LambdaExpression Build(TypeMap typeMap, MapperRegistry registry)
    {
        if (DynamicShape.IsDynamicShape(typeMap.SourceType))
            return BuildDictToPocoLambda(typeMap, registry);
        else
            return BuildPocoToDictLambda(typeMap, registry);
    }

    private static LambdaExpression BuildDictToPocoLambda(TypeMap typeMap, MapperRegistry registry)
    {
        if (typeMap.DestinationType.GetConstructor(Type.EmptyTypes) is null)
            throw new AtlasMappingException(
                $"Dynamic dict→POCO mapping for '{typeMap.DestinationType.FullName}' requires a public " +
                "parameterless constructor. Constructor-injection support is planned for Task 6 of the " +
                "Atlas v2 Dynamic Mapping feature.");

        var srcParam = Expression.Parameter(typeMap.SourceType, "src");

        // Coerce src parameter to IDictionary<string, object> for uniform handling.
        var srcAsDict = typeMap.SourceType == _dictType
            ? (Expression)srcParam
            : Expression.Convert(srcParam, _dictType);

        var dst = Expression.Variable(typeMap.DestinationType, "dst");
        var body = new List<Expression> { Expression.Assign(dst, Expression.New(typeMap.DestinationType)) };

        var tryGetValue = _dictType.GetMethod(nameof(IDictionary<string, object>.TryGetValue))!;
        var registryConst = Expression.Constant(registry);
        var cmpConst = Expression.Constant(
            registry.ConventionOptions.CaseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase);

        foreach (var pm in typeMap.PropertyMaps)
        {
            if (pm.DynamicKey is null || pm.DestinationProperty is null) continue;

            var propExpr = EmitPropertyAssign(
                pm, srcAsDict, dst, registryConst, cmpConst, tryGetValue);
            if (propExpr is not null)
                body.Add(propExpr);
        }

        body.Add(dst);

        var block = Expression.Block(new[] { dst }, body);
        return Expression.Lambda(block, srcParam);
    }

    /// <summary>
    /// Emits the assignment expression for a single property.
    /// Returns null if the property should be skipped.
    /// </summary>
    private static Expression? EmitPropertyAssign(
        PropertyMap pm,
        Expression srcAsDict,
        Expression dst,
        Expression registryConst,
        Expression cmpConst,
        MethodInfo tryGetValue)
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
        var isPocoLike = collectionElementType is null && IsPocoLike(propType);

        if (isPocoLike)
        {
            // Nested POCO branch: top-level dict value or dot-notation prefix fallback
            return EmitNestedPocoAssign(
                keyStr, keyExpr, propType, valueVar, hasValue,
                dstPropExpr, srcAsDict, registryConst, cmpConst, tryGetValue);
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
    /// 1. Top-level key is a nested dict   → recursive Invoke
    /// 2. Top-level key is already the POCO type → direct assign (Assert.Same)
    /// 3. No top-level key, but dot-notation prefixed keys exist → ScanPrefix + recursive Invoke
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
        MethodInfo tryGetValue)
    {
        // Closed Invoke<IDictionary<string, object>, TProp>
        var closedInvoke = _invokeMethod.MakeGenericMethod(_dictType, propType);

        // Nested dict variable for the scan-prefix fallback
        var prefixStr = keyStr + ".";
        var prefixExpr = Expression.Constant(prefixStr, typeof(string));
        var nestedDictVar = Expression.Variable(_dictType, "nd_" + keyStr);

        // Branch A (inside TryGetValue hit):
        //   if (valueVar is IDictionary<string, object> nd) dst.Prop = Invoke<dict, TProp>(registry, nd)
        //   else if (valueVar is TProp typed)              dst.Prop = typed
        //   else if (valueVar is null)                     dst.Prop = null / default
        //   else throw AtlasMappingException
        var nestedDictCastVar = Expression.Variable(_dictType, "ndc_" + keyStr);
        var typedVar = Expression.Variable(propType, "tc_" + keyStr);

        // null-assign: dst.Prop = default(TProp)  (null for reference types)
        var nullAssign = Expression.Assign(dstPropExpr, Expression.Default(propType));

        // dict branch: dst.Prop = Invoke(registry, (IDictionary<string,object>)valueVar)
        var dictBranchAssign = Expression.Assign(dstPropExpr,
            Expression.Call(closedInvoke, registryConst, nestedDictCastVar));

        // typed branch: dst.Prop = (TProp)valueVar
        var typedBranchAssign = Expression.Assign(dstPropExpr, typedVar);

        // throw branch
        var throwExpr = Expression.Throw(
            Expression.New(
                typeof(AtlasMappingException).GetConstructor(new[] { typeof(string) })!,
                Expression.Call(
                    typeof(string).GetMethod(nameof(string.Concat), new[] { typeof(string), typeof(string), typeof(string), typeof(string) })!,
                    Expression.Constant($"Cannot convert value at key '"),
                    keyExpr,
                    Expression.Constant("' to '"),
                    Expression.Constant(propType.Name + "'."))));

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
                dictBranchAssign),
            ifTypedBranch);

        // Else branch (TryGetValue missed): ScanPrefix dot-notation fallback
        //   var nestedDict = ScanPrefix(src, "Prop.", cmp);
        //   if (nestedDict != null) dst.Prop = Invoke(registry, nestedDict);
        var scanCall = Expression.Call(_scanPrefix, srcAsDict, prefixExpr, cmpConst);
        var assignScanned = Expression.Assign(dstPropExpr,
            Expression.Call(closedInvoke, registryConst, nestedDictVar));
        var elseBranch = Expression.Block(
            new[] { nestedDictVar },
            Expression.Assign(nestedDictVar, scanCall),
            Expression.IfThen(
                Expression.NotEqual(nestedDictVar, Expression.Constant(null, _dictType)),
                assignScanned));

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
        => throw new NotImplementedException(
            "POCO→Dict codegen lands in Task 7; this branch is intentionally unreachable for Tasks 4–6.");

    /// <summary>
    /// Returns the element type if <paramref name="t"/> is List&lt;T&gt;, T[], or IEnumerable&lt;T&gt;;
    /// otherwise null. Sets <paramref name="isArray"/> to true for array types.
    /// </summary>
    private static Type? GetCollectionElementType(Type t, out bool isArray)
    {
        // Array
        if (t.IsArray)
        {
            isArray = true;
            return t.GetElementType();
        }

        isArray = false;

        // List<T>
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
            return t.GetGenericArguments()[0];

        // IEnumerable<T> (but not string, which is IEnumerable<char>)
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            return t.GetGenericArguments()[0];

        return null;
    }

    /// <summary>
    /// Returns true for POCO-like types (non-primitive, non-scalar, non-collection, non-dynamic).
    /// Must stay in sync with <see cref="MappingInvoker.IsPocoLike"/> (private there; duplicated here
    /// for codegen classification without changing visibility).
    /// </summary>
    private static bool IsPocoLike(Type t)
        => !t.IsPrimitive
        && t != typeof(string)
        && t != typeof(object)
        && t != typeof(Guid)
        && t != typeof(DateTime)
        && t != typeof(DateTimeOffset)
        && t != typeof(TimeSpan)
        && t != typeof(decimal)
        && !t.IsEnum
        && !t.IsArray
        && !DynamicShape.IsDynamicShape(t)
        && !(t.IsGenericType && (
               t.GetGenericTypeDefinition() == typeof(List<>)
            || t.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            || t.GetGenericTypeDefinition() == typeof(ICollection<>)
            || t.GetGenericTypeDefinition() == typeof(IList<>)
            || t.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)
            || t.GetGenericTypeDefinition() == typeof(IReadOnlyCollection<>)));
}
