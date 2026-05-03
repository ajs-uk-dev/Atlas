namespace Atlas.Internal;

/// <summary>
/// Per-value resolution for enum→enum mapping (§6.1). Pure: takes a source enum value plus
/// the typemap's <see cref="EnumMapConfig"/> and returns a (Hit, dstValue) or (Throw, reason).
/// Single source of truth — used by both <c>ExecutionPlanBuilder.BuildEnumLambda</c> and
/// <c>ConfigurationValidator</c>.
/// </summary>
internal static class EnumResolver
{
    public enum ActionKind { Hit, Throw }

    public readonly record struct ResolveAction(ActionKind Kind, object? DestValue, string? Reason);

    public static ResolveAction Resolve(object src, EnumMapConfig cfg, Type srcType, Type dstType)
    {
        // 1. Explicit override
        if (cfg.PerValueOverrides.TryGetValue(src, out var overridden))
            return new ResolveAction(ActionKind.Hit, overridden, null);

        // 2. Explicit ignore → default(dstType)
        if (cfg.IgnoredSourceValues.Contains(src))
            return new ResolveAction(ActionKind.Hit, GetDefault(dstType), null);

        // 3. Strategy
        var strategyHit = cfg.Strategy switch
        {
            EnumMappingStrategy.ByValue => ResolveByValue(src, srcType, dstType),
            EnumMappingStrategy.ByName  => ResolveByName(src, srcType, dstType, cfg.IgnoreCase),
            _ => null
        };
        if (strategyHit is not null)
            return new ResolveAction(ActionKind.Hit, strategyHit, null);

        // 4. Fallback
        if (cfg.HasFallback)
            return new ResolveAction(ActionKind.Hit, cfg.FallbackValue, null);

        // 5. No match
        return new ResolveAction(
            ActionKind.Throw,
            null,
            $"No mapping defined for {srcType.Name}.{src} -> {dstType.Name}.");
    }

    private static object? ResolveByValue(object src, Type srcType, Type dstType)
    {
        var srcUnderlying = Convert.ChangeType(src, Enum.GetUnderlyingType(srcType));
        foreach (var dstVal in Enum.GetValues(dstType))
        {
            var dstUnderlying = Convert.ChangeType(dstVal, Enum.GetUnderlyingType(dstType));
            // Compare as long for portability across underlying types (with a ulong edge case
            // we accept for v1 — see spec R2). Conversion through long handles byte/short/int.
            if (Convert.ToInt64(srcUnderlying) == Convert.ToInt64(dstUnderlying))
                return dstVal;
        }
        return null;
    }

    private static object? ResolveByName(object src, Type srcType, Type dstType, bool ignoreCase)
    {
        var srcName = Enum.GetName(srcType, src);
        if (srcName is null) return null;
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var dstVal in Enum.GetValues(dstType))
        {
            var dstName = Enum.GetName(dstType, dstVal);
            if (dstName is not null && string.Equals(srcName, dstName, comparison))
                return dstVal;
        }
        return null;
    }

    private static object GetDefault(Type t) => Activator.CreateInstance(t)!;
}
