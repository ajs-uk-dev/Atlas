using System.Reflection;

namespace Atlas.Tests;

public class NullSubstituteAttributeTests
{
    [Fact]
    public void Ctor_NullValue_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new NullSubstituteAttribute(null!));
    }

    [Fact]
    public void Ctor_ConstantValueAssigned()
    {
        var attr = new NullSubstituteAttribute("(none)");
        Assert.Equal("(none)", attr.ConstantValue);
    }

    [Fact]
    public void AttributeUsage_TargetsPropertyOnly_NotInheritedNotMultiple_Sealed()
    {
        var usage = typeof(NullSubstituteAttribute).GetCustomAttribute<AttributeUsageAttribute>();
        Assert.NotNull(usage);
        Assert.Equal(AttributeTargets.Property, usage!.ValidOn);
        Assert.False(usage.Inherited);
        Assert.False(usage.AllowMultiple);
        Assert.True(typeof(NullSubstituteAttribute).IsSealed);
    }
}

public class NullSubstituteAttributeBehaviorTests
{
    private static IMapper BuildMapper(Type fixtureType)
    {
        var cfg = new MapperConfiguration(c =>
        {
            try { Atlas.Internal.AttributeScanner.Discover(fixtureType.Assembly, c); }
            catch (AtlasConfigurationException) { /* expected — caused by other tests' bad fixtures */ }
        });
        return cfg.CreateMapper();
    }

    [Fact]
    public void NullSubstitute_String_ReplacesNullWithConstant()
    {
        var mapper = BuildMapper(typeof(NullSubstituteStringDto));
        var dto = mapper.Map<NullSubstituteStringDto>(new NullSubstituteStringSource { Email = null });
        Assert.Equal("(no email)", dto.Email);
    }

    [Fact]
    public void NullSubstitute_NullableInt_ReplacesNullWithConstant()
    {
        var mapper = BuildMapper(typeof(NullSubstituteIntDto));
        var dto = mapper.Map<NullSubstituteIntDto>(new NullSubstituteIntSource { Count = null });
        Assert.Equal(0, dto.Count);
    }

    [Fact]
    public void NullSubstitute_NonNullableSource_RejectedWithUnreachableMessage()
    {
        // The unreachable-substitute fixture is in the test assembly. Discover throws an
        // aggregated AtlasConfigurationException; the BadFixture's error is somewhere inside.
        AtlasConfigurationException? caught = null;
        try
        {
            Atlas.Internal.AttributeScanner.Discover(typeof(NullSubstituteUnreachableDto).Assembly, new MapperConfigurationExpression());
        }
        catch (AtlasConfigurationException ex)
        {
            caught = ex;
        }
        Assert.NotNull(caught);
        Assert.Contains(caught!.Errors, e =>
            e.DestinationType == typeof(NullSubstituteUnreachableDto)
            && e.Reason.Contains("non-nullable")
            && e.Reason.Contains("unreachable"));
    }

    [Fact]
    public void NullSubstitute_TypeMismatch_RejectedWithStructuredMessage()
    {
        AtlasConfigurationException? caught = null;
        try
        {
            Atlas.Internal.AttributeScanner.Discover(typeof(NullSubstituteTypeMismatchDto).Assembly, new MapperConfigurationExpression());
        }
        catch (AtlasConfigurationException ex)
        {
            caught = ex;
        }
        Assert.NotNull(caught);
        Assert.Contains(caught!.Errors, e =>
            e.DestinationType == typeof(NullSubstituteTypeMismatchDto)
            && e.Reason.Contains("not assignable to source-member type"));
    }

    [Fact]
    public void NullSubstitute_CombinedWithSourceMember_BothApply()
    {
        var mapper = BuildMapper(typeof(NullSubstituteWithSourceMemberDto));
        var src = new NullSubstituteWithSourceMemberSource
        {
            Customer = new NullSubstituteWithSourceMemberCustomer { Email = null }
        };
        var dto = mapper.Map<NullSubstituteWithSourceMemberDto>(src);
        Assert.Equal("(no email)", dto.CustomerEmail);
    }

    [Fact]
    public void NullSubstitute_IgnoreShortCircuits()
    {
        var mapper = BuildMapper(typeof(NullSubstituteIgnoreShortCircuitDto));
        var src = new NullSubstituteIgnoreShortCircuitSource { Email = "alice@example.com" };
        var dto = mapper.Map<NullSubstituteIgnoreShortCircuitDto>(src);
        Assert.Null(dto.Email);   // Ignored — NullSubstitute unreachable
    }
}

public class NullSubstituteStringSource { public string? Email { get; set; } }
[Map(typeof(NullSubstituteStringSource))]
public class NullSubstituteStringDto
{
    [NullSubstitute("(no email)")]
    public string Email { get; set; } = "";
}

public class NullSubstituteIntSource { public int? Count { get; set; } }
[Map(typeof(NullSubstituteIntSource))]
public class NullSubstituteIntDto
{
    [NullSubstitute(0)]
    public int Count { get; set; }
}

public class NullSubstituteUnreachableSource { public int Count { get; set; } }   // non-nullable!
[Map(typeof(NullSubstituteUnreachableSource))]
public class NullSubstituteUnreachableDto
{
    [NullSubstitute(0)]
    public int Count { get; set; }
}

public class NullSubstituteTypeMismatchSource { public int? Count { get; set; } }
[Map(typeof(NullSubstituteTypeMismatchSource))]
public class NullSubstituteTypeMismatchDto
{
    [NullSubstitute("not-an-int")]   // string is not assignable to int?
    public int Count { get; set; }
}

public class NullSubstituteWithSourceMemberCustomer { public string? Email { get; set; } }
public class NullSubstituteWithSourceMemberSource { public NullSubstituteWithSourceMemberCustomer Customer { get; set; } = new(); }
[Map(typeof(NullSubstituteWithSourceMemberSource))]
public class NullSubstituteWithSourceMemberDto
{
    [SourceMember("Customer.Email")]
    [NullSubstitute("(no email)")]
    public string CustomerEmail { get; set; } = "";
}

public class NullSubstituteIgnoreShortCircuitSource { public string? Email { get; set; } }
[Map(typeof(NullSubstituteIgnoreShortCircuitSource))]
public class NullSubstituteIgnoreShortCircuitDto
{
    [Ignore]
    [NullSubstitute("(unreachable)")]
    public string? Email { get; set; }
}
