namespace Atlas;

/// <summary>
/// Selects which side of a type-pair mapping participates in configuration validation.
/// </summary>
public enum MemberList
{
    /// <summary>Validate that every destination member is mapped or explicitly ignored. (Default.)</summary>
    Destination,
    /// <summary>Validate that every source member is consumed.</summary>
    Source,
    /// <summary>Skip validation for this map.</summary>
    None,
}
