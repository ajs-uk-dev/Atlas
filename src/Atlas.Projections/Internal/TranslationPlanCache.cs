using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Atlas.Internal;

namespace Atlas.Projections.Internal;

/// <summary>
/// Per-<see cref="MapperConfiguration"/> cache of translated lambdas. Keyed on
/// <c>(TypePair, lambda-reference-identity)</c> using <see cref="RuntimeHelpers.GetHashCode"/>
/// — catches the realistic hot path of <c>static readonly Expression&lt;&gt;</c> lambdas
/// reused across call sites; freshly-constructed lambdas miss the cache (and translate once
/// each). See design §4.3 / §6.4.
/// </summary>
internal sealed class TranslationPlanCache
{
    private readonly ConcurrentDictionary<CacheKey, LambdaExpression> _cache = new();

    public LambdaExpression GetOrTranslate(
        TypePair pair,
        LambdaExpression destLambda,
        Func<LambdaExpression> factory)
    {
        ArgumentNullException.ThrowIfNull(destLambda);
        ArgumentNullException.ThrowIfNull(factory);

        var key = new CacheKey(pair, destLambda);
        return _cache.GetOrAdd(key, _ => factory());
    }

    private readonly record struct CacheKey(TypePair Pair, LambdaExpression Lambda)
    {
        public bool Equals(CacheKey other) =>
            Pair.Equals(other.Pair) && ReferenceEquals(Lambda, other.Lambda);

        public override int GetHashCode() =>
            HashCode.Combine(Pair, RuntimeHelpers.GetHashCode(Lambda));
    }
}

/// <summary>
/// Binds one <see cref="TranslationPlanCache"/> instance per <see cref="MapperConfiguration"/>
/// without contaminating the v1 core type. Bound via <see cref="ConditionalWeakTable{TKey,TValue}"/>
/// so cache lifetime tracks the configuration's lifetime.
/// </summary>
internal static class TranslationPlanCacheRegistry
{
    private static readonly ConditionalWeakTable<MapperConfiguration, TranslationPlanCache> _table = new();

    public static TranslationPlanCache For(MapperConfiguration config) =>
        _table.GetValue(config, _ => new TranslationPlanCache());
}
