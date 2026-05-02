namespace Atlas.Internal;

/// <summary>
/// Resolves inheritance relationships between TypeMaps at config-build time. See
/// design §6 (algorithm) and §7 (codegen interaction).
/// </summary>
internal static class InheritanceMerger
{
    /// <summary>
    /// Copies explicit base config (ForMember / ForCtorParam / Ignore) onto the derived TypeMap
    /// per AutoMapper §6.3 precedence: derived explicit beats base explicit beats derived
    /// convention. Convention-resolved base bindings (IsExplicit=false) do NOT propagate —
    /// the derived map re-resolves its own conventions.
    /// </summary>
    public static void MergeBaseConfig(TypeMap baseTm, TypeMap derivedTm)
    {
        foreach (var basePm in baseTm.PropertyMaps)
        {
            if (!basePm.IsExplicit) continue;

            var derivedPm = derivedTm.PropertyMaps.FirstOrDefault(p => p.Name == basePm.Name);

            if (derivedPm is null)
            {
                // Base member not yet on derived. Copy if the derived destination has the property.
                var derivedProp = derivedTm.DestinationType.GetProperty(basePm.Name);
                if (derivedProp is null) continue;

                var clone = PropertyMap.ForProperty(derivedProp);
                CopyConfig(basePm, clone);
                clone.IsExplicit = true;
                derivedTm.PropertyMaps.Add(clone);
            }
            else if (!derivedPm.IsExplicit)
            {
                // Derived has a convention-resolved binding. Base's explicit choice wins.
                CopyConfig(basePm, derivedPm);
                derivedPm.IsExplicit = true;
            }
            // else: derived is explicit — keep it as-is.
        }
    }

    private static void CopyConfig(PropertyMap source, PropertyMap target)
    {
        target.SourcePath = source.SourcePath;
        target.HasConstant = source.HasConstant;
        target.ConstantValue = source.ConstantValue;
        target.CustomExpression = source.CustomExpression;
        target.Ignored = source.Ignored;
        // Note: do NOT copy DestinationProperty / DestinationCtorParameter — those are
        // already correctly bound to the target's PropertyMap.
        // For Ignore-only bindings: source.SourcePath is null, which is fine — target gets null too.
    }

    /// <summary>
    /// Three-phase pass over the registered TypeMaps:
    /// 1. Propagate <see cref="TypeMap.IncludedBases"/> entries onto the corresponding base
    ///    TypeMap's <see cref="TypeMap.IncludedDerived"/>.
    /// 2. In topological order (base-before-derived), merge each base's explicit config into
    ///    each derived TypeMap.
    /// 3. Sort each <see cref="TypeMap.IncludedDerived"/> list most-derived-first for runtime
    ///    dispatch ordering.
    /// </summary>
    public static void Resolve(IReadOnlyList<TypeMap> typeMaps, IReadOnlyDictionary<TypePair, TypeMap> pairIndex)
    {
        // Phase 1: propagate IncludeBase declarations.
        foreach (var tm in typeMaps)
        {
            foreach (var basePair in tm.IncludedBases)
            {
                if (!pairIndex.TryGetValue(basePair, out var baseTm)) continue; // validator reports
                if (baseTm.IncludedDerived.Contains(tm.Pair)) continue;          // idempotent
                baseTm.IncludedDerived.Add(tm.Pair);
            }
        }

        // Phase 2: merge in topological order. Cycles impossible by C# type system.
        var sorted = TopologicalSort(typeMaps, pairIndex);
        foreach (var tm in sorted)
        {
            foreach (var derivedPair in tm.IncludedDerived)
            {
                if (!pairIndex.TryGetValue(derivedPair, out var derivedTm)) continue;
                MergeBaseConfig(tm, derivedTm);
            }
        }

        // Phase 3: sort each IncludedDerived list most-derived-first.
        foreach (var tm in typeMaps)
        {
            tm.IncludedDerived.Sort(MostDerivedFirstComparer);
        }
    }

    /// <summary>
    /// Returns typeMaps in an order where every base TypeMap appears before its derived TypeMaps.
    /// Edges are built from <see cref="TypeMap.IncludedDerived"/> (populated during Phase 1 for
    /// both Include and IncludeBase declarations). Standard Kahn's algorithm; cycles are impossible
    /// because IncludedDerived edges follow C# inheritance.
    /// </summary>
    private static List<TypeMap> TopologicalSort(
        IReadOnlyList<TypeMap> typeMaps,
        IReadOnlyDictionary<TypePair, TypeMap> pairIndex)
    {
        // Build adjacency using IncludedDerived as the edge source.
        // After Phase 1, IncludedDerived contains every base->derived relationship the user
        // declared (via Include or IncludeBase). Topo order then merges base before derived.
        var children = new Dictionary<TypeMap, List<TypeMap>>();
        var inDegree = new Dictionary<TypeMap, int>();
        foreach (var tm in typeMaps) { children[tm] = new(); inDegree[tm] = 0; }

        foreach (var tm in typeMaps)
        {
            foreach (var derivedPair in tm.IncludedDerived)
            {
                if (!pairIndex.TryGetValue(derivedPair, out var derivedTm)) continue;
                children[tm].Add(derivedTm);
                inDegree[derivedTm]++;
            }
        }

        var queue = new Queue<TypeMap>(typeMaps.Where(tm => inDegree[tm] == 0));
        var result = new List<TypeMap>(typeMaps.Count);
        var seen = new HashSet<TypeMap>();
        while (queue.Count > 0)
        {
            var tm = queue.Dequeue();
            if (!seen.Add(tm)) continue;
            result.Add(tm);
            foreach (var child in children[tm])
            {
                inDegree[child]--;
                if (inDegree[child] == 0) queue.Enqueue(child);
            }
        }

        // Cycles are impossible by C# inheritance — but if anything got missed (e.g. dangling
        // IncludeBase referencing unregistered map), append to keep total count.
        foreach (var tm in typeMaps)
            if (!seen.Contains(tm)) result.Add(tm);
        return result;
    }

    private static int MostDerivedFirstComparer(TypePair a, TypePair b)
    {
        if (a.Source == b.Source) return 0;
        // a is more derived if b's source is assignable from a's source.
        if (b.Source.IsAssignableFrom(a.Source)) return -1;
        if (a.Source.IsAssignableFrom(b.Source)) return 1;
        // Unrelated siblings — stable order by full name.
        var srcCmp = string.CompareOrdinal(a.Source.FullName, b.Source.FullName);
        if (srcCmp != 0) return srcCmp;
        return string.CompareOrdinal(a.Destination.FullName, b.Destination.FullName);
    }
}
