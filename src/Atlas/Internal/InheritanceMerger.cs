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
}
