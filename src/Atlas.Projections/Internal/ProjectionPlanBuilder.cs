using System.Linq.Expressions;
using System.Reflection;
using Atlas.Internal;

namespace Atlas.Projections.Internal;

/// <summary>
/// Emits a fully-inlined <see cref="LambdaExpression"/> for a (TSource, TDestination) pair.
/// No call to <c>MappingInvoker</c> appears in the output — nested maps are inlined recursively
/// up to <c>maxDepth</c>. Algorithm per design §5.3.
/// </summary>
internal static class ProjectionPlanBuilder
{
    public static LambdaExpression Build(MapperRegistry registry, TypePair root, int maxDepth)
    {
        var tm = registry.GetTypeMap(root)
            ?? throw new InvalidOperationException(
                $"No map registered for {root.Source.Name} -> {root.Destination.Name}.");
        var srcParam = Expression.Parameter(tm.SourceType, "src");
        var body = BuildBody(tm, srcParam, depth: 0, registry, maxDepth);
        var funcType = typeof(Func<,>).MakeGenericType(tm.SourceType, tm.DestinationType);
        return Expression.Lambda(funcType, body, srcParam);
    }

    private static Expression BuildBody(TypeMap tm, Expression srcExpr, int depth, MapperRegistry registry, int maxDepth)
    {
        RejectHooksOrThrow(tm);
        RejectDynamicOrThrow(tm);
        RejectPreserveReferencesOrThrow(tm);

        var (ctor, ctorParamMaps, propertyMaps) = ClassifyBindings(tm);

        Expression newExpr;
        if (ctor.GetParameters().Length == 0)
        {
            newExpr = Expression.New(ctor);
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
                    sourceExpr = BuildBinding(srcExpr, pm, depth, p.ParameterType, registry, maxDepth)
                        ?? Expression.Default(p.ParameterType);
                }
                var transformed = WrapProjectionWithTransformers(sourceExpr, p.ParameterType, tm);

                if (pm is not null)
                {
                    var fallback = p.HasDefaultValue
                        ? (Expression)Expression.Constant(p.DefaultValue, p.ParameterType)
                        : Expression.Default(p.ParameterType);
                    transformed = WrapProjectionWithConditions(
                        transformed, pm, srcExpr, p.ParameterType, fallback);
                }
                return transformed;
            }).ToArray();
            newExpr = Expression.New(ctor, args);
        }

        var bindings = new List<MemberBinding>();
        foreach (var pm in propertyMaps)
        {
            if (pm.Ignored) continue;
            if (pm.DestinationProperty is null) continue;
            if (!ProjectionCompatibility.IsBindingProjectable(pm, out _)) continue;
            var binding = BuildBinding(srcExpr, pm, depth, pm.DestinationProperty.PropertyType, registry, maxDepth);
            if (binding is null) continue;

            binding = WrapProjectionWithTransformers(binding, pm.DestinationProperty.PropertyType, tm);
            binding = WrapProjectionWithConditions(
                binding, pm, srcExpr, pm.DestinationProperty.PropertyType);

            bindings.Add(Expression.Bind(pm.DestinationProperty, binding));
        }

        return bindings.Count > 0
            ? Expression.MemberInit((NewExpression)newExpr, bindings)
            : newExpr;
    }

    private static Expression? BuildBinding(
        Expression srcExpr,
        PropertyMap pm,
        int depth,
        Type targetType,
        MapperRegistry registry,
        int maxDepth)
    {
        if (pm.HasConstant)
            return Expression.Constant(pm.ConstantValue, targetType);

        Expression resolved;
        if (pm.CustomExpression is not null)
        {
            resolved = ParameterReplacer.Replace(
                pm.CustomExpression.Body,
                pm.CustomExpression.Parameters[0],
                srcExpr);
        }
        else if (pm.SourcePath is not null)
        {
            resolved = BuildNullSafePath(srcExpr, pm.SourcePath.Members);
        }
        else
        {
            return null;
        }

        // NEW: apply NullSubstitute BEFORE ConvertOrInline.
        resolved = ApplyProjectionNullSubstitute(resolved, pm);

        return ConvertOrInline(resolved, targetType, depth, registry, maxDepth);
    }

    private static Expression ConvertOrInline(
        Expression source,
        Type targetType,
        int depth,
        MapperRegistry registry,
        int maxDepth)
    {
        if (source.Type == targetType)
        {
            // For reference types with a registered map, inline the projection (handles recursive/self maps).
            if (source.Type.IsClass && source.Type != typeof(string))
            {
                var selfTm = registry.GetTypeMap(new TypePair(source.Type, targetType));
                if (selfTm is not null)
                    return BuildNestedProjection(source, selfTm, depth + 1, registry, maxDepth);
            }
            return source;
        }
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

        if (IsCollection(source.Type) && IsCollection(targetType))
            return BuildCollectionProjection(source, targetType, depth, registry, maxDepth);

        var nestedTm = registry.GetTypeMap(new TypePair(source.Type, targetType));
        if (nestedTm is null) return source; // validator should have caught this

        return BuildNestedProjection(source, nestedTm, depth + 1, registry, maxDepth);
    }

    private static Expression BuildNestedProjection(
        Expression pathExpr,
        TypeMap nestedTm,
        int depth,
        MapperRegistry registry,
        int maxDepth)
    {
        if (depth >= maxDepth)
            return Expression.Default(nestedTm.DestinationType);

        var nestedParam = Expression.Parameter(nestedTm.SourceType, "n");
        var nestedBody = BuildBody(nestedTm, nestedParam, depth, registry, maxDepth);
        var inlined = ParameterReplacer.Replace(nestedBody, nestedParam, pathExpr);

        if (pathExpr.Type.IsClass)
        {
            return Expression.Condition(
                Expression.ReferenceEqual(pathExpr, Expression.Constant(null, pathExpr.Type)),
                Expression.Default(nestedTm.DestinationType),
                inlined);
        }
        return inlined;
    }

    private static Expression BuildCollectionProjection(
        Expression sourceExpr,
        Type targetType,
        int depth,
        MapperRegistry registry,
        int maxDepth)
    {
        var srcElem = GetEnumerableElementType(sourceExpr.Type)!;
        var dstElem = GetEnumerableElementType(targetType)!;

        var elementMap = registry.GetTypeMap(new TypePair(srcElem, dstElem));
        Expression selector;
        if (elementMap is not null)
        {
            var itemParam = Expression.Parameter(srcElem, "i");
            var itemBody = BuildBody(elementMap, itemParam, depth + 1, registry, maxDepth);
            selector = Expression.Lambda(itemBody, itemParam);
        }
        else
        {
            // No element map — identity (covers e.g. List<string> -> List<string>).
            var itemParam = Expression.Parameter(srcElem, "i");
            selector = Expression.Lambda(itemParam, itemParam);
        }

        var selectCall = Expression.Call(
            typeof(Enumerable),
            nameof(Enumerable.Select),
            new[] { srcElem, dstElem },
            sourceExpr,
            selector);

        if (targetType.IsArray)
            return Expression.Call(typeof(Enumerable), nameof(Enumerable.ToArray), new[] { dstElem }, selectCall);
        if (IsListLike(targetType))
            return Expression.Call(typeof(Enumerable), nameof(Enumerable.ToList), new[] { dstElem }, selectCall);
        return selectCall; // IEnumerable<T> destination
    }

    private static Expression BuildNullSafePath(Expression source, IReadOnlyList<PropertyInfo> path)
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

    private static Expression WrapProjectionWithConditions(
        Expression resolvedExpr,
        PropertyMap pm,
        Expression srcExpr,
        Type valueType,
        Expression? fallbackExpr = null)
    {
        if (pm.PreCondition is null && pm.Condition is null)
            return resolvedExpr;

        var fallback = fallbackExpr ?? Expression.Default(valueType);
        Expression? testExpr = null;

        if (pm.PreCondition is not null)
        {
            var preBody = ParameterReplacer.Replace(
                pm.PreCondition.Body, pm.PreCondition.Parameters[0], srcExpr);
            testExpr = preBody;
        }

        if (pm.Condition is not null)
        {
            // Substitute BOTH parameters: param 0 = srcExpr, param 1 = resolvedExpr (inlined twice).
            var condBody = ParameterReplacer.Replace(
                pm.Condition.Body, pm.Condition.Parameters[0], srcExpr);
            condBody = ParameterReplacer.Replace(
                condBody, pm.Condition.Parameters[1], resolvedExpr);
            testExpr = testExpr is null ? condBody : Expression.AndAlso(testExpr, condBody);
        }

        return Expression.Condition(testExpr!, resolvedExpr, fallback);
    }

    private static Expression ApplyProjectionNullSubstitute(Expression resolvedExpr, PropertyMap pm)
    {
        if (pm.NullSubstitute is null) return resolvedExpr;

        var substituteBody = pm.NullSubstitute.Body;

        if (substituteBody.Type != resolvedExpr.Type)
        {
            // Common case: resolvedExpr is Nullable<T>, substituteBody is T.
            // Expression.Coalesce(Nullable<T>, T) returns T (non-nullable). Re-wrap the result
            // back to Nullable<T> so downstream ConvertOrInline sees a symmetric widening case
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

    private static Expression WrapProjectionWithTransformers(
        Expression sourceExpr,
        Type destType,
        TypeMap typeMap)
    {
        if (!typeMap.EffectiveTransformers.TryGetValue(destType, out var transformers))
            return sourceExpr;

        Expression current = sourceExpr;
        foreach (var transformer in transformers)
        {
            // CRITICAL: inline via parameter substitution (NOT Expression.Invoke).
            // The existing ParameterReplacer.Replace helper does this already.
            current = ParameterReplacer.Replace(transformer.Body, transformer.Parameters[0], current);
        }
        return current;
    }

    private static void RejectHooksOrThrow(TypeMap tm)
    {
        if (tm.BeforeHooks.Count == 0 && tm.AfterHooks.Count == 0) return;
        throw new AtlasProjectionException(new List<ProjectionDiagnostic>
        {
            new(tm.SourceType, tm.DestinationType, "(BeforeMap/AfterMap)",
                $"map has {tm.BeforeHooks.Count} BeforeMap and {tm.AfterHooks.Count} AfterMap hook(s) — hooks are not translatable to IQueryable. Use mapper.Map<>() instead, or remove the hooks.")
        });
    }

    private static void RejectDynamicOrThrow(TypeMap tm)
    {
        if (!tm.IsDynamic) return;
        throw new AtlasProjectionException(new List<ProjectionDiagnostic>
        {
            new(tm.SourceType, tm.DestinationType, "(Dynamic mapping)",
                $"map is a dynamic-shape mapping ({tm.SourceType} → {tm.DestinationType}); " +
                $"LINQ providers cannot translate runtime dictionary key lookups against arbitrary keys. " +
                $"Use mapper.Map<>() instead.")
        });
    }

    private static void RejectPreserveReferencesOrThrow(TypeMap tm)
    {
        if (!tm.PreserveReferences) return;
        throw new AtlasProjectionException(new List<ProjectionDiagnostic>
        {
            new(tm.SourceType, tm.DestinationType, "(PreserveReferences)",
                $"map has PreserveReferences set; LINQ providers cannot model identity tracking. " +
                $"Use mapper.Map<>() instead, or remove PreserveReferences for this typemap.")
        });
    }

    private static (ConstructorInfo ctor,
                    IReadOnlyList<PropertyMap> ctorParamMaps,
                    IReadOnlyList<PropertyMap> propertyMaps)
        ClassifyBindings(TypeMap tm)
    {
        var dstType = tm.DestinationType;
        var parameterless = dstType.GetConstructor(Type.EmptyTypes);
        ConstructorInfo ctor = parameterless is { IsPublic: true }
            ? parameterless
            : dstType.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .OrderByDescending(c => c.GetParameters().Length)
                .FirstOrDefault()
                ?? throw new InvalidOperationException($"Type {dstType.Name} has no public constructor.");

        var ctorParamNames = new HashSet<string>(
            ctor.GetParameters().Select(p => p.Name ?? ""), StringComparer.OrdinalIgnoreCase);

        var ctorParamMaps = tm.PropertyMaps
            .Where(p => p.DestinationCtorParameter is not null && ctorParamNames.Contains(p.Name))
            .ToList();
        var propertyMaps = tm.PropertyMaps
            .Where(p => p.DestinationProperty is not null)
            .ToList();
        return (ctor, ctorParamMaps, propertyMaps);
    }

    private static bool IsCollection(Type t) =>
        t != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(t);

    private static bool IsListLike(Type t)
    {
        if (!t.IsGenericType) return false;
        var def = t.GetGenericTypeDefinition();
        return def == typeof(List<>) || def == typeof(IList<>) ||
               def == typeof(ICollection<>) || def == typeof(IReadOnlyList<>) ||
               def == typeof(IReadOnlyCollection<>);
    }

    private static Type? GetEnumerableElementType(Type t)
    {
        if (t.IsArray) return t.GetElementType();
        foreach (var i in new[] { t }.Concat(t.GetInterfaces()))
            if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return i.GetGenericArguments()[0];
        return null;
    }

}
