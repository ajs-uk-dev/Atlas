namespace Atlas;

/// <summary>
/// Thrown by compiled mappings at runtime when an input value has no mapping —
/// e.g., a source enum value that's not defined on the destination enum and no
/// fallback was configured. Distinct from <see cref="AtlasConfigurationException"/>,
/// which surfaces config-time errors.
/// </summary>
public sealed class AtlasMappingException : Exception
{
    public AtlasMappingException(string message) : base(message) { }
}
