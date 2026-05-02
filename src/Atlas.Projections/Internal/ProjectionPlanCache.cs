using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Atlas.Internal;

namespace Atlas.Projections.Internal;

/// <summary>
/// Per-<see cref="MapperConfiguration"/> cache of built projection lambdas. Keyed by
/// <c>(TypePair, maxDepth)</c> — different depths produce different lambdas.
/// </summary>
internal sealed class ProjectionPlanCache
{
    private readonly Dictionary<(TypePair pair, int maxDepth), LambdaExpression> _cache = new();
    private readonly Lock _lock = new();

    public LambdaExpression GetOrBuild(TypePair pair, int maxDepth, Func<LambdaExpression> build)
    {
        lock (_lock)
        {
            var key = (pair, maxDepth);
            if (_cache.TryGetValue(key, out var existing)) return existing;
            var fresh = build();
            _cache[key] = fresh;
            return fresh;
        }
    }
}

/// <summary>
/// Binds one <see cref="ProjectionPlanCache"/> instance per <see cref="MapperConfiguration"/>
/// without contaminating the v1 core type. Bound via <see cref="ConditionalWeakTable{TKey,TValue}"/>
/// so cache lifetime tracks the configuration's lifetime.
/// </summary>
internal static class ProjectionPlanCacheRegistry
{
    private static readonly ConditionalWeakTable<MapperConfiguration, ProjectionPlanCache> _table = new();

    public static ProjectionPlanCache For(MapperConfiguration config) =>
        _table.GetValue(config, _ => new ProjectionPlanCache());
}
