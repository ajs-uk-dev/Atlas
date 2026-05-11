namespace Atlas;

/// <summary>
/// Class-level attribute declaring that the decorated class is the destination type
/// of a mapping from <see cref="SourceType"/>. Equivalent to a fluent
/// <c>cfg.CreateMap&lt;TSource, TDestination&gt;(MemberList)</c> registration.
/// </summary>
/// <remarks>
/// Discovered by <see cref="MapperConfigurationExpression.AddMaps(System.Reflection.Assembly[])"/>
/// during the same scan that finds <see cref="MapperProfile"/> subclasses. Member-level
/// customization comes from <see cref="IgnoreAttribute"/>, <see cref="SourceMemberAttribute"/>,
/// and <see cref="NullSubstituteAttribute"/> on the decorated class's properties.
/// Configuring the same (TSource, TDestination) pair both via attributes AND via fluent
/// <c>CreateMap</c> throws <see cref="AtlasConfigurationException"/> at registration time.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class MapAttribute : Attribute
{
    public MapAttribute(Type sourceType)
    {
        ArgumentNullException.ThrowIfNull(sourceType);
        SourceType = sourceType;
    }

    /// <summary>The source type for this mapping (positional argument).</summary>
    public Type SourceType { get; }

    /// <summary>
    /// Validation policy for this mapping. Defaults to <see cref="MemberList.Destination"/> —
    /// the same default fluent <c>CreateMap</c> uses.
    /// </summary>
    public MemberList MemberList { get; set; } = MemberList.Destination;

    /// <summary>
    /// If <c>true</c>, the scanner additionally calls <c>.ReverseMap()</c> on the translated
    /// registration. Member-level attribute config (Ignore, SourceMember, NullSubstitute)
    /// describes the FORWARD direction only and does not auto-flip.
    /// </summary>
    public bool ReverseMap { get; set; }

    /// <summary>
    /// If <c>true</c>, the scanner calls <c>.PreserveReferences()</c> on the translated
    /// registration. When <see cref="ReverseMap"/> is also <c>true</c>, the flag propagates
    /// to the reverse pair via the bidirectional propagation machinery shipped in PR #11.
    /// </summary>
    public bool PreserveReferences { get; set; }
}
