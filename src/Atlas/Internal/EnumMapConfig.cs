namespace Atlas.Internal;

internal enum EnumMappingStrategy { ByValue, ByName }

internal sealed class EnumMapConfig
{
    public EnumMappingStrategy Strategy { get; private set; } = EnumMappingStrategy.ByValue;
    public bool IgnoreCase { get; private set; }
    public bool StrategyExplicitlySet { get; private set; }

    public Dictionary<object, object> PerValueOverrides { get; } = new();
    public HashSet<object> IgnoredSourceValues { get; } = new();

    public bool HasFallback { get; private set; }
    public object? FallbackValue { get; private set; }

    public void SetStrategy(EnumMappingStrategy strategy, bool ignoreCase)
    {
        if (StrategyExplicitlySet)
            ThrowConfig("Enum strategy already set; only one of MapByValue() / MapByName() allowed per map.");
        Strategy = strategy;
        IgnoreCase = ignoreCase;
        StrategyExplicitlySet = true;
    }

    public void AddOverride(object src, object dst)
    {
        if (IgnoredSourceValues.Contains(src))
            ThrowConfig($"Source value '{src}' is already marked Ignore(); cannot also MapValue().");
        if (!PerValueOverrides.TryAdd(src, dst))
            ThrowConfig($"MapValue for source value '{src}' is already configured.");
    }

    public void AddIgnore(object src)
    {
        if (PerValueOverrides.ContainsKey(src))
            ThrowConfig($"Source value '{src}' already has MapValue(); cannot also Ignore().");
        IgnoredSourceValues.Add(src);
    }

    public void SetFallback(object dst)
    {
        if (HasFallback)
            ThrowConfig("WithFallback() already set; only one fallback allowed per map.");
        HasFallback = true;
        FallbackValue = dst;
    }

    private static void ThrowConfig(string message) =>
        throw new AtlasConfigurationException(
            new[] { new ConfigurationError(typeof(void), typeof(void), "(enum-config)", message) });
}
