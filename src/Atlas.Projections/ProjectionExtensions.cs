using System.Linq.Expressions;
using Atlas.Internal;
using Atlas.Projections.Internal;

namespace Atlas.Projections;

/// <summary>
/// Translates a configured Atlas map into a LINQ expression and applies it as a Select.
/// Designed to be the last operator in an IQueryable chain — apply Where/OrderBy first.
/// </summary>
public static class ProjectionExtensions
{
    /// <summary>
    /// Translates the configured map for <c>(source.ElementType, TDestination)</c> and applies
    /// it via <c>Queryable.Select</c>. Throws <see cref="AtlasProjectionException"/> if any
    /// reachable binding within <paramref name="maxDepth"/> is non-projectable.
    /// </summary>
    public static IQueryable<TDestination> ProjectTo<TDestination>(
        this IQueryable source,
        MapperConfiguration configuration,
        int maxDepth = 3)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(configuration);
        if (maxDepth <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxDepth), maxDepth, "maxDepth must be > 0.");

        var srcType = source.ElementType;
        var pair = new TypePair(srcType, typeof(TDestination));
        var registry = configuration.Internal_Registry;
        var cache = ProjectionPlanCacheRegistry.For(configuration);

        var lambda = cache.GetOrBuild(pair, maxDepth, () =>
        {
            ProjectionValidator.Validate(registry, pair, maxDepth);
            return ProjectionPlanBuilder.Build(registry, pair, maxDepth);
        });

        var selectCall = Expression.Call(
            typeof(Queryable),
            nameof(Queryable.Select),
            new[] { srcType, typeof(TDestination) },
            source.Expression,
            Expression.Quote(lambda));

        return source.Provider.CreateQuery<TDestination>(selectCall);
    }
}
