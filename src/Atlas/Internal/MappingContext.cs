using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Atlas.Internal;

/// <summary>
/// Per-call instance cache for cycle-safe mapping (Atlas v2 #11 — see PreserveReferences).
/// Allocated by IMapper.Map at the public-API boundary when typeMap.PreserveReferences is true;
/// threaded through every nested map call as a MappingContext? parameter on compiled lambdas.
/// One MappingContext instance lives for the duration of one top-level Map call; abandoned afterward.
/// Not thread-safe — each call gets its own instance.
/// See docs/Atlas-Design-ReferenceHandling.md §4.1.
/// </summary>
internal sealed class MappingContext
{
    private readonly Dictionary<CacheKey, object> _cache = new(CacheKey.Comparer);

    /// <summary>
    /// Look up the destination instance previously registered for (<paramref name="source"/>,
    /// <paramref name="destinationType"/>). Returns true on hit; the caller skips body execution
    /// and returns the cached destination.
    /// </summary>
    internal bool TryGet(object source, Type destinationType, out object? destination)
    {
        if (_cache.TryGetValue(new CacheKey(source, destinationType), out var found))
        {
            destination = found;
            return true;
        }
        destination = null;
        return false;
    }

    /// <summary>
    /// Register a freshly-allocated (or update-in-place existing) destination BEFORE its members
    /// are populated. Pre-population registration is what breaks cycles: any nested map call that
    /// resolves back to <paramref name="source"/> finds <paramref name="destination"/> in the
    /// cache and returns it (partially-populated at that moment, fully-populated by the time
    /// control returns to the user).
    /// </summary>
    internal void Register(object source, Type destinationType, object destination)
    {
        _cache[new CacheKey(source, destinationType)] = destination;
    }

    /// <summary>
    /// Cache key: source instance (by reference) + destination type. Two calls with the same
    /// source and different destination types get separate slots.
    /// </summary>
    private readonly record struct CacheKey(object Source, Type DestinationType)
    {
        internal static readonly IEqualityComparer<CacheKey> Comparer = new RefEqComparer();

        private sealed class RefEqComparer : IEqualityComparer<CacheKey>
        {
            public bool Equals(CacheKey x, CacheKey y) =>
                ReferenceEquals(x.Source, y.Source) && x.DestinationType == y.DestinationType;

            public int GetHashCode(CacheKey obj) =>
                HashCode.Combine(
                    RuntimeHelpers.GetHashCode(obj.Source),
                    obj.DestinationType);
        }
    }
}
