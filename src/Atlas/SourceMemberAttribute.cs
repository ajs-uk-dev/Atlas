namespace Atlas;

/// <summary>
/// Member-level attribute redirecting a destination property to a different source-side
/// member by name. Equivalent to fluent
/// <c>ForMember(d =&gt; d.X, opt =&gt; opt.MapFrom(s =&gt; s.OtherName))</c>, except that
/// the right-hand side is a name (or dotted path), not a lambda.
/// </summary>
/// <remarks>
/// Resolved at config-build time. The path uses dotted segments for source-side flattening
/// (e.g., <c>"Customer.Address.City"</c>); each segment must resolve to a public readable
/// property or field on the source-side type at that depth. If resolution fails, the scanner
/// accumulates a <see cref="ConfigurationError"/> and the eventual
/// <see cref="AtlasConfigurationException"/> names the offending segment and the type it
/// was looked up on.
/// </remarks>
[AttributeUsage(AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
public sealed class SourceMemberAttribute : Attribute
{
    public SourceMemberAttribute(string memberName)
    {
        ArgumentNullException.ThrowIfNull(memberName);
        MemberName = memberName;
    }

    public string MemberName { get; }
}
