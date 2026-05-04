namespace Atlas.Internal;

/// <summary>
/// Mirrors forward TypeMap bindings into reverse TypeMaps. Runs after
/// <c>InheritanceMerger.Resolve</c> and <c>ConventionEngine.ResolveMissingMembers</c>
/// have populated forward maps, before any sealing. See spec §6.
/// </summary>
internal static class ReverseMapMirror
{
    /// <summary>
    /// For every TypeMap with a non-null <see cref="TypeMap.ReverseMapPair"/>: look up the
    /// forward TypeMap, then for each forward PropertyMap that is eligible for mirroring
    /// (per §6.2) AND not already covered on the reverse map (per skip rules in §6.1),
    /// create a reverse PropertyMap with directions flipped.
    /// </summary>
    public static void Mirror(IEnumerable<TypeMap> typeMaps)
    {
        var byPair = typeMaps.ToDictionary(t => t.Pair);

        foreach (var tm in byPair.Values)
        {
            if (tm.ReverseMapPair is not { } forwardPair) continue;
            if (!byPair.TryGetValue(forwardPair, out var forward)) continue;

            foreach (var fwdPm in forward.PropertyMaps)
            {
                if (!IsMirrorEligible(fwdPm)) continue;
                var mirrored = FlipBinding(fwdPm);
                if (mirrored is null) continue;

                // Skip-rule-1: exact-name binding already exists on the reverse.
                if (tm.PropertyMaps.Any(p => p.Name == mirrored.Name)) continue;

                // Skip-rule-2: user explicitly mapped the TOP intermediate as a whole.
                if (mirrored.DestinationPath is { Count: > 1 } path)
                {
                    var topName = path[0].Name;
                    if (tm.PropertyMaps.Any(p => p.Name == topName && p.IsExplicit))
                        continue;
                }

                tm.PropertyMaps.Add(mirrored);
            }
        }
    }

    private static bool IsMirrorEligible(PropertyMap fwdPm)
    {
        if (fwdPm.SourcePath is null) return false;
        if (fwdPm.Ignored) return false;
        if (fwdPm.HasConstant) return false;
        if (fwdPm.CustomExpression is not null) return false;
        if (fwdPm.DestinationProperty is null) return false;            // skip ctor-param bindings
        if (fwdPm.DestinationProperty.GetMethod is not { IsPublic: true }) return false;   // can't read on reverse
        return true;
    }

    private static PropertyMap? FlipBinding(PropertyMap fwdPm)
    {
        var fwdSourceChain = fwdPm.SourcePath!.Members;
        var fwdDestProp = fwdPm.DestinationProperty!;

        if (fwdSourceChain.Count == 1)
        {
            var revDestProp = fwdSourceChain[0];
            if (revDestProp.SetMethod is not { IsPublic: true }) return null;
            var revPm = PropertyMap.ForProperty(revDestProp);
            revPm.SourcePath = new SourceMemberPath(new[] { fwdDestProp });
            return revPm;
        }

        // Multi-level: reverse becomes an unflatten path.
        var revPath = fwdSourceChain;
        if (revPath[^1].SetMethod is not { IsPublic: true }) return null;
        var revPmMulti = PropertyMap.ForPath(revPath);
        revPmMulti.SourcePath = new SourceMemberPath(new[] { fwdDestProp });
        return revPmMulti;
    }
}
