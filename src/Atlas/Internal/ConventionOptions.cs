namespace Atlas.Internal;

/// <summary>
/// Inputs to the convention engine for resolving destination members against source members.
/// </summary>
internal readonly record struct ConventionOptions(
    NamingConvention SourceConvention,
    NamingConvention DestinationConvention,
    bool CaseSensitive);
