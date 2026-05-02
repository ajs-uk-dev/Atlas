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
                var pm = ctorParamMaps.FirstOrDefault(m =>
                    string.Equals(m.Name, p.Name, StringComparison.OrdinalIgnoreCase));
                if (pm is null)
                {
                    return p.HasDefaultValue
                        ? (Expression)Expression.Constant(p.DefaultValue, p.ParameterType)
                        : Expression.Default(p.ParameterType);
                }
                return BuildBinding(srcExpr, pm, depth, p.ParameterType, registry, maxDepth)
                    ?? Expression.Default(p.ParameterType);
            }).ToArray();
            newExpr = Expression.New(ctor, args);
        }

        var bindings = new List<MemberBinding>();
        foreach (var pm in propertyMaps)
        {
            if (pm.Ignored) continue;
            if (pm.DestinationProperty is null) continue;
            var binding = BuildBinding(srcExpr, pm, depth, pm.DestinationProperty.PropertyType, registry, maxDepth);
            if (binding is null) continue;
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

        if (pm.CustomExpression is not null)
        {
            var rebound = ParameterReplacer.Replace(
                pm.CustomExpression.Body,
                pm.CustomExpression.Parameters[0],
                srcExpr);
            return ConvertOrInline(rebound, targetType, depth, registry, maxDepth);
        }

        if (pm.SourcePath is null) return null;

        var pathExpr = BuildNullSafePath(srcExpr, pm.SourcePath.Members);
        return ConvertOrInline(pathExpr, targetType, depth, registry, maxDepth);
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
        if (HasImplicitNumericConversion(source.Type, targetType))
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

    private static bool HasImplicitNumericConversion(Type src, Type dst)
    {
        // Unwrap Nullable<T> on both sides: int? -> long? is valid if int -> long is valid.
        var srcUnderlying = Nullable.GetUnderlyingType(src);
        var dstUnderlying = Nullable.GetUnderlyingType(dst);
        if (srcUnderlying is not null || dstUnderlying is not null)
        {
            if (srcUnderlying is null || dstUnderlying is null) return false;
            return HasImplicitNumericConversion(srcUnderlying, dstUnderlying);
        }

        return (src, dst) switch
        {
            _ when src == typeof(sbyte) => dst == typeof(short) || dst == typeof(int) || dst == typeof(long) || dst == typeof(float) || dst == typeof(double) || dst == typeof(decimal),
            _ when src == typeof(byte) => dst == typeof(short) || dst == typeof(ushort) || dst == typeof(int) || dst == typeof(uint) || dst == typeof(long) || dst == typeof(ulong) || dst == typeof(float) || dst == typeof(double) || dst == typeof(decimal),
            _ when src == typeof(short) => dst == typeof(int) || dst == typeof(long) || dst == typeof(float) || dst == typeof(double) || dst == typeof(decimal),
            _ when src == typeof(ushort) => dst == typeof(int) || dst == typeof(uint) || dst == typeof(long) || dst == typeof(ulong) || dst == typeof(float) || dst == typeof(double) || dst == typeof(decimal),
            _ when src == typeof(int) => dst == typeof(long) || dst == typeof(float) || dst == typeof(double) || dst == typeof(decimal),
            _ when src == typeof(uint) => dst == typeof(long) || dst == typeof(ulong) || dst == typeof(float) || dst == typeof(double) || dst == typeof(decimal),
            _ when src == typeof(long) => dst == typeof(float) || dst == typeof(double) || dst == typeof(decimal),
            _ when src == typeof(ulong) => dst == typeof(float) || dst == typeof(double) || dst == typeof(decimal),
            _ when src == typeof(float) => dst == typeof(double),
            _ when src == typeof(char) => dst == typeof(ushort) || dst == typeof(int) || dst == typeof(uint) || dst == typeof(long) || dst == typeof(ulong) || dst == typeof(float) || dst == typeof(double) || dst == typeof(decimal),
            _ => false,
        };
    }
}
