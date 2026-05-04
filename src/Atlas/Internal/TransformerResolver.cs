using System.Linq.Expressions;

namespace Atlas.Internal;

/// <summary>
/// Composes global + profile + type-map value transformers into each TypeMap's
/// <see cref="TypeMap.EffectiveTransformers"/> dictionary. Runs at config-build time, after
/// <c>ReverseMapMirror.Mirror</c>, before <c>tm.Seal()</c>.
/// </summary>
internal static class TransformerResolver
{
    public static void Resolve(
        IEnumerable<TypeMap> typeMaps,
        ValueTransformerCollection globalTransformers)
    {
        var globalLookup = globalTransformers.AllTransformers;

        foreach (var tm in typeMaps)
        {
            // Collect every destination type referenced by ANY scope for this TypeMap.
            var allTypes = new HashSet<Type>();
            foreach (var t in globalLookup.Keys) allTypes.Add(t);
            if (tm.OriginatingProfile is { } profile)
                foreach (var t in profile.ValueTransformers.AllTransformers.Keys) allTypes.Add(t);
            foreach (var t in tm.TypeMapTransformers.Keys) allTypes.Add(t);

            foreach (var destType in allTypes)
            {
                var composed = new List<LambdaExpression>();

                // Broad-first compose: global → profile → type-map. FIFO within each scope.
                if (globalLookup.TryGetValue(destType, out var g))
                    composed.AddRange(g);

                if (tm.OriginatingProfile is { } prof
                    && prof.ValueTransformers.AllTransformers.TryGetValue(destType, out var p))
                    composed.AddRange(p);

                if (tm.TypeMapTransformers.TryGetValue(destType, out var t))
                    composed.AddRange(t);

                if (composed.Count > 0)
                    tm.EffectiveTransformers[destType] = composed;
            }
        }
    }
}
