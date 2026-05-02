namespace Atlas.Projections;

/// <summary>
/// One entry in a projection-incompatibility report. <see cref="Member"/> is the destination
/// member name, or "(whole map)" when the entire pair is non-projectable, or
/// "(no map registered)" when the pair has no registered mapping at all.
/// </summary>
public sealed record ProjectionDiagnostic(
    Type SourceType,
    Type DestinationType,
    string Member,
    string Reason);

/// <summary>
/// Thrown when ProjectTo is asked to translate a configuration that contains constructs the
/// LINQ provider cannot handle. Aggregates every incompatibility for the requested
/// (TSource, TDestination) pair, including reachable nested pairs within maxDepth.
/// </summary>
public sealed class AtlasProjectionException : Exception
{
    public IReadOnlyList<ProjectionDiagnostic> Diagnostics { get; }

    public AtlasProjectionException(IReadOnlyList<ProjectionDiagnostic> diagnostics)
        : base(BuildMessage(diagnostics))
    {
        Diagnostics = diagnostics;
    }

    private static string BuildMessage(IReadOnlyList<ProjectionDiagnostic> diagnostics)
    {
        if (diagnostics.Count == 0) return "Atlas projection is invalid.";
        var lines = diagnostics.Select(d =>
            $"{d.SourceType.Name} -> {d.DestinationType.Name}.{d.Member}: {d.Reason}");
        return "Atlas projection is invalid:" + Environment.NewLine + string.Join(Environment.NewLine, lines);
    }
}
