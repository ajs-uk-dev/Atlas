namespace Atlas;

/// <summary>
/// One configuration-validation finding. Carries enough context for a developer to fix it
/// from the message alone.
/// </summary>
public sealed record ConfigurationError(
    Type SourceType,
    Type DestinationType,
    string DestinationMemberName,
    string Reason);

/// <summary>
/// Thrown when a mapper configuration fails validation or a profile cannot be constructed.
/// Aggregates every error so the developer sees them all in one run.
/// </summary>
public sealed class AtlasConfigurationException : Exception
{
    public IReadOnlyList<ConfigurationError> Errors { get; }

    public AtlasConfigurationException(IReadOnlyList<ConfigurationError> errors)
        : base(BuildMessage(errors))
    {
        Errors = errors;
    }

    private static string BuildMessage(IReadOnlyList<ConfigurationError> errors)
    {
        if (errors.Count == 0) return "Atlas configuration is invalid.";
        var lines = errors.Select(e =>
            $"{e.SourceType.Name} -> {e.DestinationType.Name}.{e.DestinationMemberName}: {e.Reason}");
        return "Atlas configuration is invalid:" + Environment.NewLine + string.Join(Environment.NewLine, lines);
    }
}
