namespace Atlas;

/// <summary>
/// Member-level attribute marking a destination property as ignored (excluded from mapping
/// AND from validation). Equivalent to fluent
/// <c>ForMember(d =&gt; d.X, opt =&gt; opt.Ignore())</c>.
/// </summary>
/// <remarks>
/// Has effect only when applied to a property of a class decorated with
/// <see cref="AutoMapAttribute"/>. Silently no-op otherwise (no error). Combined with
/// <see cref="SourceMemberAttribute"/> or <see cref="NullSubstituteAttribute"/> on the
/// same property, <see cref="IgnoreAttribute"/> short-circuits — the property is never
/// assigned, so the other attributes' configuration is unreachable.
/// </remarks>
[AttributeUsage(AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
public sealed class IgnoreAttribute : Attribute { }
