using System.Reflection;

namespace Atlas.Tests;

public class FromAttributeTests
{
    [Fact]
    public void Ctor_NullName_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new FromAttribute(null!));
    }

    [Fact]
    public void Ctor_NameAssigned()
    {
        var attr = new FromAttribute("Customer.Name");
        Assert.Equal("Customer.Name", attr.MemberName);
    }

    [Fact]
    public void AttributeUsage_TargetsPropertyOnly_NotInheritedNotMultiple_Sealed()
    {
        var usage = typeof(FromAttribute).GetCustomAttribute<AttributeUsageAttribute>();
        Assert.NotNull(usage);
        Assert.Equal(AttributeTargets.Property, usage!.ValidOn);
        Assert.False(usage.Inherited);
        Assert.False(usage.AllowMultiple);
        Assert.True(typeof(FromAttribute).IsSealed);
    }
}

public class FromAttributeBehaviorTests
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
    public void FlatRedirect_RedirectsToOtherFrom()
    {
        var mapper = BuildMapper(typeof(FromFlatDto));
        var dto = mapper.Map<FromFlatDto>(new FromFlatSource { OriginalName = "Alice" });
        Assert.Equal("Alice", dto.RedirectedName);
    }

    [Fact]
    public void DottedPath_FlattensFromNestedSource()
    {
        var mapper = BuildMapper(typeof(FromDottedDto));
        var src = new FromDottedSource { Customer = new FromCustomer { Name = "Bob" } };
        var dto = mapper.Map<FromDottedDto>(src);
        Assert.Equal("Bob", dto.CustomerName);
    }

    [Fact]
    public void MultiLevelDottedPath_FlattensFromDeepSource()
    {
        var mapper = BuildMapper(typeof(FromDeepDto));
        var src = new FromDeepSource { Customer = new FromDeepCustomer { Address = new FromDeepAddress { City = "London" } } };
        var dto = mapper.Map<FromDeepDto>(src);
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
            Atlas.Internal.AttributeScanner.Discover(typeof(FromBadPathDto).Assembly, new MapperConfigurationExpression());
        }
        catch (AtlasConfigurationException ex)
        {
            caught = ex;
        }
        Assert.NotNull(caught);
        Assert.Contains(caught!.Errors, e => e.Reason.Contains("Customer.Missing"));
    }

    [Fact]
    public void SkipShortCircuitsFrom_OnSameProperty()
    {
        var mapper = BuildMapper(typeof(FromIgnoreShortCircuitDto));
        var src = new FromIgnoreShortCircuitSource { OriginalName = "Eve" };
        var dto = mapper.Map<FromIgnoreShortCircuitDto>(src);
        Assert.Null(dto.RedirectedName);   // Ignored — From is unreachable
    }
}

public class FromFlatSource { public string OriginalName { get; set; } = ""; }
[Map(typeof(FromFlatSource))]
public class FromFlatDto
{
    [From(nameof(FromFlatSource.OriginalName))]
    public string RedirectedName { get; set; } = "";
}

public class FromCustomer { public string Name { get; set; } = ""; }
public class FromDottedSource { public FromCustomer Customer { get; set; } = new(); }
[Map(typeof(FromDottedSource))]
public class FromDottedDto
{
    [From("Customer.Name")]
    public string CustomerName { get; set; } = "";
}

public class FromDeepAddress { public string City { get; set; } = ""; }
public class FromDeepCustomer { public FromDeepAddress Address { get; set; } = new(); }
public class FromDeepSource { public FromDeepCustomer Customer { get; set; } = new(); }
[Map(typeof(FromDeepSource))]
public class FromDeepDto
{
    [From("Customer.Address.City")]
    public string City { get; set; } = "";
}

public class FromBadPathSource { public FromCustomer Customer { get; set; } = new(); }
[Map(typeof(FromBadPathSource))]
public class FromBadPathDto
{
    [From("Customer.Missing")]
    public string Bad { get; set; } = "";
}

public class FromIgnoreShortCircuitSource { public string OriginalName { get; set; } = ""; }
[Map(typeof(FromIgnoreShortCircuitSource))]
public class FromIgnoreShortCircuitDto
{
    [Skip]
    [From(nameof(FromIgnoreShortCircuitSource.OriginalName))]
    public string? RedirectedName { get; set; }
}
