using System.Reflection;

namespace Atlas.Tests;

public class DefaultWhenNullAttributeTests
{
    [Fact]
    public void Ctor_NullValue_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DefaultWhenNullAttribute(null!));
    }

    [Fact]
    public void Ctor_ConstantValueAssigned()
    {
        var attr = new DefaultWhenNullAttribute("(none)");
        Assert.Equal("(none)", attr.ConstantValue);
    }

    [Fact]
    public void AttributeUsage_TargetsPropertyOnly_NotInheritedNotMultiple_Sealed()
    {
        var usage = typeof(DefaultWhenNullAttribute).GetCustomAttribute<AttributeUsageAttribute>();
        Assert.NotNull(usage);
        Assert.Equal(AttributeTargets.Property, usage!.ValidOn);
        Assert.False(usage.Inherited);
        Assert.False(usage.AllowMultiple);
        Assert.True(typeof(DefaultWhenNullAttribute).IsSealed);
    }
}

public class DefaultWhenNullAttributeBehaviorTests
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
    public void DefaultWhenNull_String_ReplacesNullWithConstant()
    {
        var mapper = BuildMapper(typeof(DefaultWhenNullStringDto));
        var dto = mapper.Map<DefaultWhenNullStringDto>(new DefaultWhenNullStringSource { Email = null });
        Assert.Equal("(no email)", dto.Email);
    }

    [Fact]
    public void DefaultWhenNull_NullableInt_ReplacesNullWithConstant()
    {
        var mapper = BuildMapper(typeof(DefaultWhenNullIntDto));
        var dto = mapper.Map<DefaultWhenNullIntDto>(new DefaultWhenNullIntSource { Count = null });
        Assert.Equal(0, dto.Count);
    }

    [Fact]
    public void DefaultWhenNull_NonNullableSource_RejectedWithUnreachableMessage()
    {
        // The unreachable-substitute fixture is in the test assembly. Discover throws an
        // aggregated AtlasConfigurationException; the BadFixture's error is somewhere inside.
        AtlasConfigurationException? caught = null;
        try
        {
            Atlas.Internal.AttributeScanner.Discover(typeof(DefaultWhenNullUnreachableDto).Assembly, new MapperConfigurationExpression());
        }
        catch (AtlasConfigurationException ex)
        {
            caught = ex;
        }
        Assert.NotNull(caught);
        Assert.Contains(caught!.Errors, e =>
            e.DestinationType == typeof(DefaultWhenNullUnreachableDto)
            && e.Reason.Contains("non-nullable")
            && e.Reason.Contains("unreachable"));
    }

    [Fact]
    public void DefaultWhenNull_TypeMismatch_RejectedWithStructuredMessage()
    {
        AtlasConfigurationException? caught = null;
        try
        {
            Atlas.Internal.AttributeScanner.Discover(typeof(DefaultWhenNullTypeMismatchDto).Assembly, new MapperConfigurationExpression());
        }
        catch (AtlasConfigurationException ex)
        {
            caught = ex;
        }
        Assert.NotNull(caught);
        Assert.Contains(caught!.Errors, e =>
            e.DestinationType == typeof(DefaultWhenNullTypeMismatchDto)
            && e.Reason.Contains("not assignable to source-member type"));
    }

    [Fact]
    public void DefaultWhenNull_CombinedWithSourceMember_BothApply()
    {
        var mapper = BuildMapper(typeof(DefaultWhenNullWithFromDto));
        var src = new DefaultWhenNullWithFromSource
        {
            Customer = new DefaultWhenNullWithFromCustomer { Email = null }
        };
        var dto = mapper.Map<DefaultWhenNullWithFromDto>(src);
        Assert.Equal("(no email)", dto.CustomerEmail);
    }

    [Fact]
    public void DefaultWhenNull_IgnoreShortCircuits()
    {
        var mapper = BuildMapper(typeof(DefaultWhenNullSkipShortCircuitDto));
        var src = new DefaultWhenNullSkipShortCircuitSource { Email = "alice@example.com" };
        var dto = mapper.Map<DefaultWhenNullSkipShortCircuitDto>(src);
        Assert.Null(dto.Email);   // Ignored — NullSubstitute unreachable
    }
}

public class DefaultWhenNullStringSource { public string? Email { get; set; } }
[Map(typeof(DefaultWhenNullStringSource))]
public class DefaultWhenNullStringDto
{
    [DefaultWhenNull("(no email)")]
    public string Email { get; set; } = "";
}

public class DefaultWhenNullIntSource { public int? Count { get; set; } }
[Map(typeof(DefaultWhenNullIntSource))]
public class DefaultWhenNullIntDto
{
    [DefaultWhenNull(0)]
    public int Count { get; set; }
}

public class DefaultWhenNullUnreachableSource { public int Count { get; set; } }   // non-nullable!
[Map(typeof(DefaultWhenNullUnreachableSource))]
public class DefaultWhenNullUnreachableDto
{
    [DefaultWhenNull(0)]
    public int Count { get; set; }
}

public class DefaultWhenNullTypeMismatchSource { public int? Count { get; set; } }
[Map(typeof(DefaultWhenNullTypeMismatchSource))]
public class DefaultWhenNullTypeMismatchDto
{
    [DefaultWhenNull("not-an-int")]   // string is not assignable to int?
    public int Count { get; set; }
}

public class DefaultWhenNullWithFromCustomer { public string? Email { get; set; } }
public class DefaultWhenNullWithFromSource { public DefaultWhenNullWithFromCustomer Customer { get; set; } = new(); }
[Map(typeof(DefaultWhenNullWithFromSource))]
public class DefaultWhenNullWithFromDto
{
    [From("Customer.Email")]
    [DefaultWhenNull("(no email)")]
    public string CustomerEmail { get; set; } = "";
}

public class DefaultWhenNullSkipShortCircuitSource { public string? Email { get; set; } }
[Map(typeof(DefaultWhenNullSkipShortCircuitSource))]
public class DefaultWhenNullSkipShortCircuitDto
{
    [Skip]
    [DefaultWhenNull("(unreachable)")]
    public string? Email { get; set; }
}
