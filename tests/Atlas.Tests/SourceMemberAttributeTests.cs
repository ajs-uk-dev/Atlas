using System.Reflection;

namespace Atlas.Tests;

public class SourceMemberAttributeTests
{
    [Fact]
    public void Ctor_NullName_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SourceMemberAttribute(null!));
    }

    [Fact]
    public void Ctor_NameAssigned()
    {
        var attr = new SourceMemberAttribute("Customer.Name");
        Assert.Equal("Customer.Name", attr.MemberName);
    }

    [Fact]
    public void AttributeUsage_TargetsPropertyOnly_NotInheritedNotMultiple_Sealed()
    {
        var usage = typeof(SourceMemberAttribute).GetCustomAttribute<AttributeUsageAttribute>();
        Assert.NotNull(usage);
        Assert.Equal(AttributeTargets.Property, usage!.ValidOn);
        Assert.False(usage.Inherited);
        Assert.False(usage.AllowMultiple);
        Assert.True(typeof(SourceMemberAttribute).IsSealed);
    }
}

public class SourceMemberAttributeBehaviorTests
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
    public void FlatRedirect_RedirectsToOtherSourceMember()
    {
        var mapper = BuildMapper(typeof(SourceMemberFlatDto));
        var dto = mapper.Map<SourceMemberFlatDto>(new SourceMemberFlatSource { OriginalName = "Alice" });
        Assert.Equal("Alice", dto.RedirectedName);
    }

    [Fact]
    public void DottedPath_FlattensFromNestedSource()
    {
        var mapper = BuildMapper(typeof(SourceMemberDottedDto));
        var src = new SourceMemberDottedSource { Customer = new SourceMemberCustomer { Name = "Bob" } };
        var dto = mapper.Map<SourceMemberDottedDto>(src);
        Assert.Equal("Bob", dto.CustomerName);
    }

    [Fact]
    public void MultiLevelDottedPath_FlattensFromDeepSource()
    {
        var mapper = BuildMapper(typeof(SourceMemberDeepDto));
        var src = new SourceMemberDeepSource { Customer = new SourceMemberDeepCustomer { Address = new SourceMemberDeepAddress { City = "London" } } };
        var dto = mapper.Map<SourceMemberDeepDto>(src);
        Assert.Equal("London", dto.City);
    }

    [Fact]
    public void BadPath_FailsAtConfigBuildWithStructuredError()
    {
        // The bad-path fixture is in the test assembly, so even with the catch-all wrapper
        // the AtlasConfigurationException's Errors list will contain the BadPath error.
        AtlasConfigurationException? caught = null;
        try
        {
            Atlas.Internal.AttributeScanner.Discover(typeof(SourceMemberBadPathDto).Assembly, new MapperConfigurationExpression());
        }
        catch (AtlasConfigurationException ex)
        {
            caught = ex;
        }
        Assert.NotNull(caught);
        Assert.Contains(caught!.Errors, e => e.Reason.Contains("Customer.Missing"));
    }

    [Fact]
    public void IgnoreShortCircuitsSourceMember_OnSameProperty()
    {
        var mapper = BuildMapper(typeof(SourceMemberIgnoreShortCircuitDto));
        var src = new SourceMemberIgnoreShortCircuitSource { OriginalName = "Eve" };
        var dto = mapper.Map<SourceMemberIgnoreShortCircuitDto>(src);
        Assert.Null(dto.RedirectedName);   // Ignored — SourceMember is unreachable
    }
}

public class SourceMemberFlatSource { public string OriginalName { get; set; } = ""; }
[AutoMap(typeof(SourceMemberFlatSource))]
public class SourceMemberFlatDto
{
    [SourceMember(nameof(SourceMemberFlatSource.OriginalName))]
    public string RedirectedName { get; set; } = "";
}

public class SourceMemberCustomer { public string Name { get; set; } = ""; }
public class SourceMemberDottedSource { public SourceMemberCustomer Customer { get; set; } = new(); }
[AutoMap(typeof(SourceMemberDottedSource))]
public class SourceMemberDottedDto
{
    [SourceMember("Customer.Name")]
    public string CustomerName { get; set; } = "";
}

public class SourceMemberDeepAddress { public string City { get; set; } = ""; }
public class SourceMemberDeepCustomer { public SourceMemberDeepAddress Address { get; set; } = new(); }
public class SourceMemberDeepSource { public SourceMemberDeepCustomer Customer { get; set; } = new(); }
[AutoMap(typeof(SourceMemberDeepSource))]
public class SourceMemberDeepDto
{
    [SourceMember("Customer.Address.City")]
    public string City { get; set; } = "";
}

public class SourceMemberBadPathSource { public SourceMemberCustomer Customer { get; set; } = new(); }
[AutoMap(typeof(SourceMemberBadPathSource))]
public class SourceMemberBadPathDto
{
    [SourceMember("Customer.Missing")]
    public string Bad { get; set; } = "";
}

public class SourceMemberIgnoreShortCircuitSource { public string OriginalName { get; set; } = ""; }
[AutoMap(typeof(SourceMemberIgnoreShortCircuitSource))]
public class SourceMemberIgnoreShortCircuitDto
{
    [Ignore]
    [SourceMember(nameof(SourceMemberIgnoreShortCircuitSource.OriginalName))]
    public string? RedirectedName { get; set; }
}
