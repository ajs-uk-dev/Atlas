using System.Linq.Expressions;
using System.Reflection;

namespace Atlas.Internal;

/// <summary>
/// Builds the <see cref="LambdaExpression"/> that materializes a <see cref="TypeMap"/> at runtime.
/// Algorithm per design §7.
/// </summary>
internal static class ExecutionPlanBuilder
{
    public static LambdaExpression Build(TypeMap typeMap, MapperRegistry registry)
    {
        // Dynamic dispatch (Atlas v2 #10) — must be first so dict↔POCO pairs are never
        // intercepted by the enum or POCO branches below.
        if (typeMap.IsDynamic)
            return DynamicPlanBuilder.Build(typeMap, registry);

        // Enum dispatch — both source AND destination must be enums (sealed value types,
        // so this branch is mutually exclusive with inheritance dispatch below).
        if (typeMap.SourceType.IsEnum && typeMap.DestinationType.IsEnum)
            return BuildEnumLambda(typeMap);

        // Per-base body (existing v1 codegen).
        var baseLambda = BuildBaseBody(typeMap, registry);

        if (typeMap.IncludedDerived.Count == 0)
            return baseLambda;

        return BuildWithInheritanceDispatch(baseLambda, typeMap, registry);
    }

    private static readonly EnumMapConfig DefaultEnumConfig = new();

    private static readonly ConstructorInfo AtlasMappingExceptionCtor =
        typeof(AtlasMappingException).GetConstructor(new[] { typeof(string) })!;

    private static LambdaExpression BuildEnumLambda(TypeMap typeMap)
    {
        var cfg = typeMap.EnumConfig ?? DefaultEnumConfig;
        var srcType = typeMap.SourceType;
        var dstType = typeMap.DestinationType;
        var srcParam = Expression.Parameter(srcType, "src");

        var cases = new List<SwitchCase>();
        foreach (var definedSrc in Enum.GetValues(srcType))
        {
            var action = EnumResolver.Resolve(definedSrc, cfg, srcType, dstType);
            Expression caseBody = action.Kind switch
            {
                EnumResolver.ActionKind.Hit =>
                    Expression.Constant(action.DestValue, dstType),
                EnumResolver.ActionKind.Throw =>
                    Expression.Throw(
                        Expression.New(AtlasMappingExceptionCtor, Expression.Constant(action.Reason)),
                        dstType),
                _ => throw new InvalidOperationException("Unreachable"),
            };
            cases.Add(Expression.SwitchCase(caseBody, Expression.Constant(definedSrc, srcType)));
        }

        // Default case: source value not in defined values of srcType (e.g., (SrcEnum)99).
        var defaultBody = Expression.Throw(
            Expression.New(
                AtlasMappingExceptionCtor,
                Expression.Constant($"Source value is not defined on {srcType.Name}.")),
            dstType);

        var switchExpr = Expression.Switch(srcParam, defaultBody, cases.ToArray());
        var ctxParam = Expression.Parameter(typeof(MappingContext), "ctx");
        var funcType = typeof(Func<,,>).MakeGenericType(srcType, typeof(MappingContext), dstType);
        return Expression.Lambda(funcType, switchExpr, srcParam, ctxParam);
    }

    private static LambdaExpression BuildBaseBody(TypeMap typeMap, MapperRegistry registry)
    {
        if (typeMap.CustomConverter is not null)
            return BuildConverterLambda(typeMap);

        if (IsCollection(typeMap.SourceType) && IsCollection(typeMap.DestinationType))
            return BuildCollectionLambda(typeMap, registry);

        if (IsDictionary(typeMap.SourceType) && IsDictionary(typeMap.DestinationType))
            return BuildDictionaryLambda(typeMap, registry);

        return BuildPocoLambda(typeMap, registry);
    }

    private static LambdaExpression BuildWithInheritanceDispatch(
        LambdaExpression baseLambda,
        TypeMap typeMap,
        MapperRegistry registry)
    {
        var srcParam = baseLambda.Parameters[0];
        var ctxParam = baseLambda.Parameters[1];

        // Inline the base body (substitute baseLambda's parameter for our srcParam).
        // baseLambda.Parameters[0] IS srcParam after our wrap, so the body already references it.
        // No replacement needed — we use baseLambda.Body directly as the fall-through.
        Expression body = baseLambda.Body;

        // IncludedDerived is sorted most-derived-first by InheritanceMerger. We iterate in
        // reverse (least-derived-first) so that each new wrap becomes the outer Condition,
        // putting the most-derived type check outermost (evaluated first at runtime).
        foreach (var derivedPair in ((IEnumerable<TypePair>)typeMap.IncludedDerived).Reverse())
        {
            var derivedSrc = derivedPair.Source;
            var derivedDst = derivedPair.Destination;

            // src is TDerivedSrc
            var typeIsExpr = Expression.TypeIs(srcParam, derivedSrc);

            // MappingInvoker.Invoke<TDerivedSrc, TDerivedDst>(registry, (TDerivedSrc)src, ctx)
            var method = typeof(MappingInvoker)
                .GetMethod(nameof(MappingInvoker.Invoke), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!
                .MakeGenericMethod(derivedSrc, derivedDst);
            var invoke = Expression.Call(
                method,
                Expression.Constant(registry),
                Expression.Convert(srcParam, derivedSrc),
                ctxParam);

            // Cast to base destination.
            var upcast = Expression.Convert(invoke, typeMap.DestinationType);

            // Conditional: src is TDerivedSrc ? upcast : body
            body = Expression.Condition(typeIsExpr, upcast, body);
        }

        // Null guard outside the dispatch chain (matches v1 idiom).
        if (typeMap.SourceType.IsClass)
        {
            body = Expression.Condition(
                Expression.ReferenceEqual(srcParam, Expression.Constant(null, typeMap.SourceType)),
                Expression.Default(typeMap.DestinationType),
                body);
        }

        var funcType = typeof(Func<,,>).MakeGenericType(typeMap.SourceType, typeof(MappingContext), typeMap.DestinationType);
        return Expression.Lambda(funcType, body, srcParam, ctxParam);
    }

    /// <summary>Build the update-in-place lambda for the given type map.</summary>
    public static LambdaExpression BuildUpdate(TypeMap typeMap, MapperRegistry registry)
    {
        // Dynamic dispatch (Atlas v2 #10) — mirrors the Build() IsDynamic branch for update direction.
        if (typeMap.IsDynamic)
            return DynamicPlanBuilder.BuildUpdate(typeMap, registry);

        var srcType = typeMap.SourceType;
        var dstType = typeMap.DestinationType;
        var srcParam = Expression.Parameter(srcType, "src");
        var ctxParam = Expression.Parameter(typeof(MappingContext), "ctx");
        var destParam = Expression.Parameter(dstType, "dest");

        // Return label for early-exit (void lambda uses a void-typed label).
        var returnLabel = Expression.Label("return");

        var statements = new List<Expression>();
        var locals = new List<ParameterExpression>();

        // ── Cache preamble: only for reference-type sources ──────────────────────
        if (srcType.IsClass)
        {
            // Cache check: if (ctx != null && ctx.TryGet((object)src, typeof(TDst), out cached))
            //                 return;   // already being updated — skip to break cycles
            var cachedVar = Expression.Variable(typeof(object), "cached");
            locals.Add(cachedVar);

            var ctxNotNull = Expression.NotEqual(
                ctxParam, Expression.Constant(null, typeof(MappingContext)));
            var tryGetCall = Expression.Call(
                ctxParam,
                MappingContextTryGetMethod,
                Expression.Convert(srcParam, typeof(object)),
                Expression.Constant(dstType, typeof(Type)),
                cachedVar);
            statements.Add(Expression.IfThen(
                Expression.AndAlso(ctxNotNull, tryGetCall),
                Expression.Return(returnLabel)));

            // Cache register: ctx.Register immediately before member emit.
            // if (ctx != null) ctx.Register((object)src, typeof(TDst), (object)dest);
            statements.Add(Expression.IfThen(
                Expression.NotEqual(ctxParam, Expression.Constant(null, typeof(MappingContext))),
                Expression.Call(
                    ctxParam,
                    MappingContextRegisterMethod,
                    Expression.Convert(srcParam, typeof(object)),
                    Expression.Constant(dstType, typeof(Type)),
                    Expression.Convert(destParam, typeof(object)))));
        }

        // NEW: emit BeforeHooks.
        foreach (var hookEntry in typeMap.BeforeHooks)
            statements.Add(BuildHookCall(hookEntry, srcParam, destParam, registry));

        foreach (var pm in typeMap.PropertyMaps)
        {
            if (pm.Ignored) continue;
            if (pm.DestinationProperty is null) continue;     // ctor params skipped on update

            var sourceExpr = BuildSourceExpression(pm, srcParam, ctxParam, registry, pm.DestinationProperty.PropertyType);
            if (sourceExpr is null) continue;

            var transformed = WrapWithTransformers(sourceExpr, pm.DestinationProperty.PropertyType, typeMap);

            Expression dstAccess;
            Expression? intermediates = null;
            if (pm.DestinationPath is { } path && path.Count > 1)
            {
                (intermediates, dstAccess) = BuildNestedPathAccess(destParam, path);
            }
            else
            {
                dstAccess = Expression.Property(destParam, pm.DestinationProperty);
            }

            var gatedAssign = BuildUpdateAssignWithConditions(
                transformed, pm, srcParam, dstAccess, pm.DestinationProperty.PropertyType);

            statements.Add(intermediates is null
                ? gatedAssign
                : Expression.Block(intermediates, gatedAssign));
        }

        // NEW: emit AfterHooks.
        foreach (var hookEntry in typeMap.AfterHooks)
            statements.Add(BuildHookCall(hookEntry, srcParam, destParam, registry));

        // Return label at end (default fall-through for void lambdas).
        statements.Add(Expression.Label(returnLabel));

        Expression body = locals.Count > 0
            ? Expression.Block(locals, statements)
            : (statements.Count > 0
                ? Expression.Block(statements)
                : Expression.Empty());

        if (srcType.IsClass)
        {
            body = Expression.IfThen(
                Expression.Not(Expression.ReferenceEqual(srcParam, Expression.Constant(null, srcType))),
                body);
        }

        return Expression.Lambda(body, srcParam, ctxParam, destParam);
    }

    // ---- POCO ----

    // Cached MethodInfo for MappingContext.TryGet and MappingContext.Register,
    // resolved once to avoid repeated GetMethod lookups per compile.
    private static readonly MethodInfo MappingContextTryGetMethod =
        typeof(MappingContext).GetMethod(
            nameof(MappingContext.TryGet),
            BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo MappingContextRegisterMethod =
        typeof(MappingContext).GetMethod(
            nameof(MappingContext.Register),
            BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static LambdaExpression BuildPocoLambda(TypeMap typeMap, MapperRegistry registry)
    {
        var srcType = typeMap.SourceType;
        var dstType = typeMap.DestinationType;
        var srcParam = Expression.Parameter(srcType, "src");
        var ctxParam = Expression.Parameter(typeof(MappingContext), "ctx");
        var destVar = Expression.Variable(dstType, "dest");

        var (ctor, ctorParamMaps, propertyMaps) = ClassifyBindings(typeMap);

        Expression newDest;
        if (ctor.GetParameters().Length == 0)
        {
            newDest = Expression.New(ctor);
        }
        else
        {
            var args = ctor.GetParameters().Select(p =>
            {
                Expression sourceExpr;
                var pm = ctorParamMaps.FirstOrDefault(m =>
                    string.Equals(m.Name, p.Name, StringComparison.OrdinalIgnoreCase));
                if (pm is null)
                {
                    sourceExpr = p.HasDefaultValue
                        ? Expression.Constant(p.DefaultValue, p.ParameterType)
                        : Expression.Default(p.ParameterType);
                }
                else
                {
                    sourceExpr = BuildSourceExpression(pm, srcParam, ctxParam, registry, p.ParameterType)
                        ?? Expression.Default(p.ParameterType);
                }
                var transformed = WrapWithTransformers(sourceExpr, p.ParameterType, typeMap);

                // Gate ctor-arg with predicates. Skip semantics for ctor args:
                // p.DefaultValue if the param has one, else default(T) — a ctor argument
                // cannot be omitted.
                if (pm is not null)
                {
                    var fallback = p.HasDefaultValue
                        ? (Expression)Expression.Constant(p.DefaultValue, p.ParameterType)
                        : Expression.Default(p.ParameterType);
                    transformed = WrapWithConditions(
                        transformed, pm, srcParam, p.ParameterType, fallback);
                }
                return transformed;
            }).ToArray();
            newDest = Expression.New(ctor, args);
        }

        // Return label used by the cache-hit early-exit path.
        var returnLabel = Expression.Label(dstType, "return");

        var statements = new List<Expression>();
        var locals = new List<ParameterExpression> { destVar };

        // ── Cache preamble: only for reference-type sources ──────────────────────
        if (srcType.IsClass)
        {
            // Declare a local to receive the out parameter from TryGet.
            var cachedVar = Expression.Variable(typeof(object), "cached");
            locals.Add(cachedVar);

            // if (ctx != null && ctx.TryGet((object)src, typeof(TDst), out cached))
            //     return (TDst)cached;
            var ctxNotNull = Expression.NotEqual(
                ctxParam, Expression.Constant(null, typeof(MappingContext)));
            var tryGetCall = Expression.Call(
                ctxParam,
                MappingContextTryGetMethod,
                Expression.Convert(srcParam, typeof(object)),
                Expression.Constant(dstType, typeof(Type)),
                cachedVar);
            statements.Add(Expression.IfThen(
                Expression.AndAlso(ctxNotNull, tryGetCall),
                Expression.Return(returnLabel, Expression.Convert(cachedVar, dstType))));
        }

        // ── Allocate destination ─────────────────────────────────────────────────
        statements.Add(Expression.Assign(destVar, newDest));

        // ── Cache register: immediately after allocation, before member emit ─────
        if (srcType.IsClass)
        {
            // if (ctx != null) ctx.Register((object)src, typeof(TDst), (object)dest);
            statements.Add(Expression.IfThen(
                Expression.NotEqual(ctxParam, Expression.Constant(null, typeof(MappingContext))),
                Expression.Call(
                    ctxParam,
                    MappingContextRegisterMethod,
                    Expression.Convert(srcParam, typeof(object)),
                    Expression.Constant(dstType, typeof(Type)),
                    Expression.Convert(destVar, typeof(object)))));
        }

        // NEW: emit BeforeHooks (FIFO order).
        foreach (var hookEntry in typeMap.BeforeHooks)
            statements.Add(BuildHookCall(hookEntry, srcParam, destVar, registry));

        foreach (var pm in propertyMaps)
        {
            if (pm.Ignored) continue;
            if (pm.DestinationProperty is null) continue;

            var sourceExpr = BuildSourceExpression(pm, srcParam, ctxParam, registry, pm.DestinationProperty.PropertyType);
            if (sourceExpr is null) continue;

            var transformed = WrapWithTransformers(sourceExpr, pm.DestinationProperty.PropertyType, typeMap);
            var assignValue = WrapWithConditions(
                transformed, pm, srcParam, pm.DestinationProperty.PropertyType);

            if (pm.DestinationPath is { } path && path.Count > 1)
            {
                statements.Add(BuildNestedAssign(destVar, path, assignValue));
            }
            else
            {
                statements.Add(Expression.Assign(
                    Expression.Property(destVar, pm.DestinationProperty),
                    assignValue));
            }
        }

        // NEW: emit AfterHooks (FIFO order).
        foreach (var hookEntry in typeMap.AfterHooks)
            statements.Add(BuildHookCall(hookEntry, srcParam, destVar, registry));

        // Return label at end: default value is destVar (normal non-cached path).
        statements.Add(Expression.Label(returnLabel, destVar));

        Expression body = Expression.Block(locals, statements);

        if (srcType.IsClass)
        {
            body = Expression.Condition(
                Expression.ReferenceEqual(srcParam, Expression.Constant(null, srcType)),
                Expression.Default(dstType),
                body);
        }

        return Expression.Lambda(body, srcParam, ctxParam);
    }

    private static (ConstructorInfo ctor,
                    IReadOnlyList<PropertyMap> ctorParamMaps,
                    IReadOnlyList<PropertyMap> propertyMaps)
        ClassifyBindings(TypeMap typeMap)
    {
        var dstType = typeMap.DestinationType;
        var parameterless = dstType.GetConstructor(Type.EmptyTypes);
        ConstructorInfo ctor;
        if (parameterless is { IsPublic: true })
        {
            ctor = parameterless;
        }
        else
        {
            ctor = dstType.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .OrderByDescending(c => c.GetParameters().Length)
                .FirstOrDefault()
                ?? throw new InvalidOperationException(
                    $"Type {dstType.Name} has no public constructor.");
        }

        var ctorParamNames = new HashSet<string>(
            ctor.GetParameters().Select(p => p.Name ?? ""),
            StringComparer.OrdinalIgnoreCase);

        var ctorParamMaps = typeMap.PropertyMaps
            .Where(p => p.DestinationCtorParameter is not null && ctorParamNames.Contains(p.Name))
            .ToList();
        var propertyMaps = typeMap.PropertyMaps
            .Where(p => p.DestinationProperty is not null)
            .ToList();

        return (ctor, ctorParamMaps, propertyMaps);
    }

    // ---- Source expression generation ----

    private static Expression? BuildSourceExpression(
        PropertyMap pm,
        ParameterExpression srcParam,
        ParameterExpression ctxParam,
        MapperRegistry registry,
        Type targetType)
    {
        if (pm.HasConstant)
            return Expression.Constant(pm.ConstantValue, targetType);

        Expression? resolved;
        if (pm.CustomExpression is not null)
        {
            var rebound = new ParameterReplacer(pm.CustomExpression.Parameters[0], srcParam)
                .Visit(pm.CustomExpression.Body);
            resolved = rebound;
        }
        else if (pm.SourcePath is not null)
        {
            resolved = BuildPathAccess(srcParam, pm.SourcePath.Members);
        }
        else
        {
            return null;
        }

        // NEW: apply NullSubstitute BEFORE ConvertOrMap so the substitute participates
        // in the conversion pipeline exactly like a real value (numeric / enum auto-conversion,
        // registered TypeMaps).
        resolved = ApplyNullSubstitute(resolved!, pm);

        return ConvertOrMap(resolved, targetType, ctxParam, registry);
    }

    private static Expression BuildPathAccess(Expression source, IReadOnlyList<PropertyInfo> path)
    {
        Expression current = source;
        foreach (var step in path)
        {
            var stepProp = Expression.Property(current, step);
            if (current.Type.IsClass)
            {
                current = Expression.Condition(
                    Expression.ReferenceEqual(current, Expression.Constant(null, current.Type)),
                    Expression.Default(stepProp.Type),
                    stepProp);
            }
            else
            {
                current = stepProp;
            }
        }
        return current;
    }

    private static Expression ConvertOrMap(Expression source, Type targetType, ParameterExpression ctxParam, MapperRegistry registry)
    {
        if (source.Type == targetType) return source;
        if (targetType.IsAssignableFrom(source.Type)) return Expression.Convert(source, targetType);

        if (NumericConversions.HasImplicitConversion(source.Type, targetType))
            return Expression.Convert(source, targetType);

        // Handle asymmetric nullable cases that NumericConversions rejects at line 21
        // (it requires BOTH sides to be nullable for the symmetric unwrap).
        // Case A: Nullable<T> → U  — unwrap (and optionally widen) (e.g. int? → int, int? → long).
        // Case B: T → Nullable<U>  — widen then wrap   (e.g. int → long?).
        var srcUnderlying = Nullable.GetUnderlyingType(source.Type);
        var dstUnderlying = Nullable.GetUnderlyingType(targetType);
        if (srcUnderlying is not null && dstUnderlying is null
            && (srcUnderlying == targetType || NumericConversions.HasImplicitConversion(srcUnderlying, targetType)))
            return Expression.Convert(source, targetType);
        if (srcUnderlying is null && dstUnderlying is not null
            && (source.Type == dstUnderlying || NumericConversions.HasImplicitConversion(source.Type, dstUnderlying)))
            return Expression.Convert(source, targetType);

        // Enum auto-conversion (NEW): only if no registered typemap covers the pair.
        if (EnumConversions.HasImplicitConversion(source.Type, targetType)
            && registry.GetTypeMap(new TypePair(source.Type, targetType)) is null)
        {
            return EnumConversions.BuildConversion(source, targetType, registry.StringToEnumCache);
        }

        if (IsCollection(source.Type) && IsCollection(targetType))
            return BuildCollectionInvoke(source, targetType, ctxParam, registry);

        // Fallback: nested map invocation
        return BuildNestedInvoke(source, targetType, ctxParam, registry);
    }

    private static Expression BuildNestedInvoke(Expression source, Type destType, ParameterExpression ctxParam, MapperRegistry registry)
    {
        var method = typeof(MappingInvoker)
            .GetMethod(nameof(MappingInvoker.Invoke), BindingFlags.Public | BindingFlags.Static)!
            .MakeGenericMethod(source.Type, destType);
        return Expression.Call(method, Expression.Constant(registry), source, ctxParam);
    }

    private static Expression BuildCollectionInvoke(Expression source, Type destType, ParameterExpression ctxParam, MapperRegistry registry)
    {
        var srcElement = GetEnumerableElementType(source.Type)!;
        var dstElement = GetEnumerableElementType(destType)!;

        var helperName = destType.IsArray
            ? nameof(MappingInvoker.InvokeToArray)
            : nameof(MappingInvoker.InvokeToList);

        var method = typeof(MappingInvoker)
            .GetMethod(helperName, BindingFlags.Public | BindingFlags.Static)!
            .MakeGenericMethod(srcElement, dstElement);

        var sourceAsEnumerable = source.Type.IsAssignableTo(typeof(IEnumerable<>).MakeGenericType(srcElement))
            ? source
            : Expression.Convert(source, typeof(IEnumerable<>).MakeGenericType(srcElement));

        return Expression.Call(method, Expression.Constant(registry), sourceAsEnumerable, ctxParam);
    }

    // ---- Custom converter ----

    private static LambdaExpression BuildConverterLambda(TypeMap typeMap)
    {
        var srcParam = Expression.Parameter(typeMap.SourceType, "src");
        var ctxParam = Expression.Parameter(typeof(MappingContext), "ctx");
        var converter = typeMap.CustomConverter!;
        // Wrap the converter delegate in a Func<TSource, MappingContext?, TDestination>.
        var invoke = Expression.Invoke(Expression.Constant(converter), srcParam);
        return Expression.Lambda(invoke, srcParam, ctxParam);
    }

    // ---- Collections ----

    private static LambdaExpression BuildCollectionLambda(TypeMap typeMap, MapperRegistry registry)
    {
        var srcParam = Expression.Parameter(typeMap.SourceType, "src");
        var ctxParam = Expression.Parameter(typeof(MappingContext), "ctx");
        var srcElement = GetEnumerableElementType(typeMap.SourceType)!;
        var dstElement = GetEnumerableElementType(typeMap.DestinationType)!;

        var helperName = typeMap.DestinationType.IsArray
            ? nameof(MappingInvoker.InvokeToArray)
            : nameof(MappingInvoker.InvokeToList);

        var method = typeof(MappingInvoker)
            .GetMethod(helperName, BindingFlags.Public | BindingFlags.Static)!
            .MakeGenericMethod(srcElement, dstElement);

        var enumerableType = typeof(IEnumerable<>).MakeGenericType(srcElement);
        var sourceAsEnumerable = typeMap.SourceType.IsAssignableTo(enumerableType)
            ? (Expression)srcParam
            : Expression.Convert(srcParam, enumerableType);

        var call = Expression.Call(method, Expression.Constant(registry), sourceAsEnumerable, ctxParam);
        return Expression.Lambda(call, srcParam, ctxParam);
    }

    // ---- Dictionaries ----

    private static LambdaExpression BuildDictionaryLambda(TypeMap typeMap, MapperRegistry registry)
    {
        var srcParam = Expression.Parameter(typeMap.SourceType, "src");
        var ctxParam = Expression.Parameter(typeof(MappingContext), "ctx");
        var srcArgs = typeMap.SourceType.GetGenericArguments();
        var dstArgs = typeMap.DestinationType.GetGenericArguments();

        var method = typeof(MappingInvoker)
            .GetMethod(nameof(MappingInvoker.InvokeToDictionary), BindingFlags.Public | BindingFlags.Static)!
            .MakeGenericMethod(srcArgs[0], srcArgs[1], dstArgs[0], dstArgs[1]);

        var call = Expression.Call(method, Expression.Constant(registry), srcParam, ctxParam);
        return Expression.Lambda(call, srcParam, ctxParam);
    }

    // ---- Type helpers ----

    internal static bool IsCollection(Type t)
    {
        if (t == typeof(string)) return false;
        if (t.IsArray) return true;
        if (!typeof(System.Collections.IEnumerable).IsAssignableFrom(t)) return false;
        if (IsDictionary(t)) return false;
        return true;
    }

    private static bool IsDictionary(Type t) =>
        t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>);

    internal static Type? GetEnumerableElementType(Type t)
    {
        if (t.IsArray) return t.GetElementType();
        if (t.IsGenericType)
        {
            var def = t.GetGenericTypeDefinition();
            if (def == typeof(IEnumerable<>) || def == typeof(ICollection<>) || def == typeof(IList<>)
                || def == typeof(List<>) || def == typeof(IReadOnlyCollection<>) || def == typeof(IReadOnlyList<>))
                return t.GetGenericArguments()[0];
        }
        var iface = t.GetInterfaces().FirstOrDefault(i =>
            i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return iface?.GetGenericArguments()[0];
    }

    /// <summary>
    /// Splits a multi-level destination path into (a) the block of intermediate-coalesce
    /// statements and (b) the leaf-access MemberExpression. Used by the gated update-path
    /// so the predicate wraps just the leaf-assign, while intermediates are still
    /// auto-instantiated regardless of the predicate's value.
    /// </summary>
    private static (Expression Intermediates, Expression LeafAccess) BuildNestedPathAccess(
        Expression destRoot,
        IReadOnlyList<PropertyInfo> destPath)
    {
        var statements = new List<Expression>();
        Expression accessSoFar = destRoot;

        for (int i = 0; i < destPath.Count - 1; i++)
        {
            var intermediateProp = destPath[i];
            accessSoFar = Expression.Property(accessSoFar, intermediateProp);
            var ctor = intermediateProp.PropertyType.GetConstructor(Type.EmptyTypes)
                ?? throw new InvalidOperationException(
                    $"Cannot unflatten path through {intermediateProp.DeclaringType?.Name}.{intermediateProp.Name}: " +
                    $"intermediate type {intermediateProp.PropertyType.FullName} has no public parameterless constructor. " +
                    "Call AssertConfigurationIsValid() at startup to catch this at config time.");
            var coalesce = Expression.Coalesce(accessSoFar, Expression.New(ctor));
            statements.Add(Expression.Assign(accessSoFar, coalesce));
        }

        var leafAccess = Expression.Property(accessSoFar, destPath[^1]);
        return (Expression.Block(statements), leafAccess);
    }

    private static Expression BuildNestedAssign(
        Expression destRoot,                       // dst (parameter or local var)
        IReadOnlyList<PropertyInfo> destPath,      // [Customer, Address, City]
        Expression valueExpr)                      // src.CustomerCity (already built)
    {
        var statements = new List<Expression>();
        Expression accessSoFar = destRoot;

        // Walk all intermediates (destPath[0..^2]) emitting a coalesce-and-assign per step.
        for (int i = 0; i < destPath.Count - 1; i++)
        {
            var intermediateProp = destPath[i];
            accessSoFar = Expression.Property(accessSoFar, intermediateProp);

            // emit: accessSoFar = accessSoFar ?? new IntermediateType();
            // Validator should have verified parameterless ctor exists; emit a clear runtime
            // error here as a safety net for users who skip AssertConfigurationIsValid.
            var ctor = intermediateProp.PropertyType.GetConstructor(Type.EmptyTypes)
                ?? throw new InvalidOperationException(
                    $"Cannot unflatten path through {intermediateProp.DeclaringType?.Name}.{intermediateProp.Name}: " +
                    $"intermediate type {intermediateProp.PropertyType.FullName} has no public parameterless constructor. " +
                    "Call AssertConfigurationIsValid() at startup to catch this at config time.");
            var coalesce = Expression.Coalesce(accessSoFar, Expression.New(ctor));
            statements.Add(Expression.Assign(accessSoFar, coalesce));
        }

        // Final step: leaf assign.
        var leafAccess = Expression.Property(accessSoFar, destPath[^1]);
        statements.Add(Expression.Assign(leafAccess, valueExpr));

        return Expression.Block(statements);
    }

    private static Expression BuildHookCall(
        HookEntry entry,
        Expression srcExpr,
        Expression destExpr,
        MapperRegistry registry)
    {
        var srcType = srcExpr.Type;
        var dstType = destExpr.Type;
        var resolveMethod = typeof(HookResolver)
            .GetMethod(nameof(HookResolver.Resolve), BindingFlags.Public | BindingFlags.Static)!
            .MakeGenericMethod(srcType, dstType);
        var typedDelegate = (Delegate)resolveMethod.Invoke(null, new object?[] { entry, registry })!;

        // Emit: typedDelegate.Invoke(src, dest)
        return Expression.Invoke(Expression.Constant(typedDelegate), srcExpr, destExpr);
    }

    private static Expression BuildUpdateAssignWithConditions(
        Expression resolvedExpr,
        PropertyMap pm,
        ParameterExpression srcParam,
        Expression dstAccess,
        Type valueType)
    {
        // Inner: assign (gated by Condition if present).
        Expression assign;
        if (pm.Condition is not null)
        {
            var resolvedVar = Expression.Variable(valueType, "r");
            var condBody = SubstituteTwoParams(pm.Condition, srcParam, resolvedVar);
            assign = Expression.Block(
                variables: new[] { resolvedVar },
                Expression.Assign(resolvedVar, resolvedExpr),
                Expression.IfThen(condBody, Expression.Assign(dstAccess, resolvedVar)));
        }
        else
        {
            assign = Expression.Assign(dstAccess, resolvedExpr);
        }

        // Outer: PreCondition gate.
        if (pm.PreCondition is not null)
        {
            var preBody = SubstituteOneParam(pm.PreCondition, srcParam);
            assign = Expression.IfThen(preBody, assign);
        }

        return assign;
    }

    private static Expression WrapWithConditions(
        Expression resolvedExpr,
        PropertyMap pm,
        ParameterExpression srcParam,
        Type valueType,
        Expression? fallbackExpr = null)
    {
        if (pm.PreCondition is null && pm.Condition is null)
            return resolvedExpr;

        var fallback = fallbackExpr ?? Expression.Default(valueType);

        // Inner: Condition gate (post-resolution).
        Expression inner = resolvedExpr;
        if (pm.Condition is not null)
        {
            // Hoist resolvedExpr into a local so it is evaluated once even if the
            // condition body references it multiple times.
            var resolvedVar = Expression.Variable(valueType, "r");
            var condBody = SubstituteTwoParams(pm.Condition, srcParam, resolvedVar);
            inner = Expression.Block(
                variables: new[] { resolvedVar },
                Expression.Assign(resolvedVar, resolvedExpr),
                Expression.Condition(condBody, resolvedVar, fallback));
        }

        // Outer: PreCondition gate (pre-resolution). Wraps the entire Condition block,
        // so resolvedExpr is not evaluated when PreCondition fails.
        if (pm.PreCondition is not null)
        {
            var preBody = SubstituteOneParam(pm.PreCondition, srcParam);
            inner = Expression.Condition(preBody, inner, fallback);
        }

        return inner;
    }

    private static Expression ApplyNullSubstitute(Expression resolvedExpr, PropertyMap pm)
    {
        if (pm.NullSubstitute is null) return resolvedExpr;

        // The substitute is a parameterless lambda; inline its body directly.
        var substituteBody = pm.NullSubstitute.Body;

        // The substitute body's type is TSourceMember per the public API. resolvedExpr.Type
        // may be either the same TSourceMember or a wrapped form (e.g., Nullable<int>).
        // Coalesce handles Nullable<T> natively (returns the unwrapped T).
        if (substituteBody.Type != resolvedExpr.Type)
        {
            // Common case: resolvedExpr is Nullable<T>, substituteBody is T.
            // Expression.Coalesce(Nullable<T>, T) returns T (non-nullable). Re-wrap the result
            // back to Nullable<T> so downstream ConvertOrMap sees a symmetric widening case
            // (e.g., int? → long?) rather than asymmetric (int → long?), which
            // NumericConversions rejects when one side is nullable and the other isn't.
            if (Nullable.GetUnderlyingType(resolvedExpr.Type) == substituteBody.Type)
            {
                return Expression.Convert(
                    Expression.Coalesce(resolvedExpr, substituteBody),
                    resolvedExpr.Type);
            }
            substituteBody = Expression.Convert(substituteBody, resolvedExpr.Type);
        }

        return Expression.Coalesce(resolvedExpr, substituteBody);
    }

    private static Expression WrapWithTransformers(
        Expression sourceExpr,
        Type destType,
        TypeMap typeMap)
    {
        if (!typeMap.EffectiveTransformers.TryGetValue(destType, out var transformers))
            return sourceExpr;

        Expression current = sourceExpr;
        foreach (var transformer in transformers)
        {
            // CRITICAL: inline the transformer's body via parameter substitution.
            // Do NOT use Expression.Invoke(transformer, current) — EF Core (and most LINQ
            // providers) cannot translate Invoke nodes to SQL; they CAN translate the inlined
            // body. The same pattern is used today by ProjectionPlanBuilder for MapFrom lambdas.
            var paramSubst = new ParameterReplacer(transformer.Parameters[0], current);
            current = paramSubst.Visit(transformer.Body)!;
        }
        return current;
    }

    private static Expression SubstituteOneParam(LambdaExpression lambda, Expression param0Replacement)
        => new ParameterReplacer(lambda.Parameters[0], param0Replacement).Visit(lambda.Body)!;

    private static Expression SubstituteTwoParams(LambdaExpression lambda,
        Expression param0Replacement, Expression param1Replacement)
    {
        var afterFirst = new ParameterReplacer(lambda.Parameters[0], param0Replacement).Visit(lambda.Body)!;
        return new ParameterReplacer(lambda.Parameters[1], param1Replacement).Visit(afterFirst)!;
    }

    private sealed class ParameterReplacer(ParameterExpression from, Expression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) =>
            node == from ? to : base.VisitParameter(node);
    }
}
