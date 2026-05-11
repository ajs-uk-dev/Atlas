namespace Atlas;

/// <summary>
/// Member-level attribute supplying a constant fallback value used when the resolved source
/// member is <c>null</c>. Equivalent to fluent
/// <c>ForMember(d =&gt; d.X, opt =&gt; opt.NullSubstitute(constant))</c>.
/// </summary>
/// <remarks>
/// Has effect only when applied to a property of a class decorated with
/// <see cref="MapAttribute"/>. The validator rejects substitutes whose source-member
/// type is non-nullable (the substitute would be unreachable) or whose substitute type is
/// not assignable to the source-member type. The constructor itself rejects literal
/// <c>null</c> as the substitute value.
/// </remarks>
[AttributeUsage(AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
public sealed class NullSubstituteAttribute : Attribute
{
    public NullSubstituteAttribute(object constantValue)
    {
        ArgumentNullException.ThrowIfNull(constantValue);
        ConstantValue = constantValue;
    }

    public object ConstantValue { get; }
}
