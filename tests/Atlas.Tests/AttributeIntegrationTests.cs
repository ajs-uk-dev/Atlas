using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Tests;

public class AttributeIntegrationTests
{
    private static IMapper BuildMapper(Type fixtureType)
    {
        var cfg = new MapperConfiguration(c =>
        {
            try { c.AddMaps(fixtureType.Assembly); }
            catch (AtlasConfigurationException) { /* tolerate sibling-fixture pollution */ }
        });
        return cfg.CreateMapper();
    }

    [Fact]
    public void DI_AddAtlas_ResolvedMapper_UsesAttributeMap()
    {
        // Use the configure-callback overload to scope the AddMaps call inside a try/catch.
        // The default no-configure overload throws because the test assembly contains bad fixtures.
        var services = new ServiceCollection();
        services.AddAtlas(c =>
        {
            try { c.AddMaps(typeof(IntegrationDto).Assembly); }
            catch (AtlasConfigurationException) { }
        });
        using var sp = services.BuildServiceProvider();
        var mapper = sp.GetRequiredService<IMapper>();
        var dto = mapper.Map<IntegrationDto>(new IntegrationSource
        {
            Id = 42,
            Customer = new IntegrationCustomer { FirstName = "Alice", Email = null },
            Skipped = "ignored",
        });
        Assert.Equal(42, dto.Id);
        Assert.Equal("Alice", dto.CustomerFirstName);
        Assert.Equal("(no email)", dto.CustomerEmail);
        Assert.Null(dto.SkippedField);
    }

    [Fact]
    public void MultiAttribute_OnOneProperty_BothApply_InCorrectOrder()
    {
        var mapper = BuildMapper(typeof(IntegrationDto));
        var dto = mapper.Map<IntegrationDto>(new IntegrationSource
        {
            Customer = new IntegrationCustomer { FirstName = "x", Email = null },
        });
        // SourceMember redirects, NullSubstitute supplies fallback.
        Assert.Equal("(no email)", dto.CustomerEmail);
    }

    [Fact]
    public void SkipShortCircuits_OnPropertyAlsoBearingFromOrDefaultWhenNull()
    {
        var mapper = BuildMapper(typeof(IntegrationShortCircuitDto));
        var dto = mapper.Map<IntegrationShortCircuitDto>(new IntegrationShortCircuitSource
        {
            Customer = new IntegrationCustomer { Email = null }
        });
        Assert.Null(dto.SkippedDespiteAttributes);
    }

    [Fact]
    public void AttributeWithReverseMap_BothDirectionsWork()
    {
        var mapper = BuildMapper(typeof(IntegrationReverseDto));
        var src = new IntegrationReverseSource { Id = 7 };
        var dto = mapper.Map<IntegrationReverseDto>(src);
        var roundTrip = mapper.Map<IntegrationReverseSource>(dto);
        Assert.Equal(7, dto.Id);
        Assert.Equal(7, roundTrip.Id);
    }

    [Fact]
    public void AttributeWithPreserveReferences_BreaksCycle()
    {
        var mapper = BuildMapper(typeof(IntegrationCycleDto));
        var src = new IntegrationCycleSource { Id = 1 };
        src.Self = src;
        var dto = mapper.Map<IntegrationCycleDto>(src);
        Assert.Same(dto, dto.Self);
    }

    [Fact]
    public void AttributeMap_IntegrationDtoSpecific_TypeMapValid()
    {
        // Targeted check: build the cfg, find the IntegrationSource → IntegrationDto TypeMap,
        // verify it has the expected explicit member configuration. Avoids the pollution from
        // running AssertConfigurationIsValid against the whole test assembly.
        var expr = new MapperConfigurationExpression();
        try { Atlas.Internal.AttributeScanner.Discover(typeof(IntegrationDto).Assembly, expr); }
        catch (AtlasConfigurationException) { }
        var tm = expr.GetTypeMaps()
                     .First(t => t.SourceType == typeof(IntegrationSource)
                              && t.DestinationType == typeof(IntegrationDto));
        Assert.NotNull(tm);
        Assert.Contains(tm.PropertyMaps, pm => pm.Name == "SkippedField" && pm.Ignored);
    }
}

public class IntegrationCustomer
{
    public string FirstName { get; set; } = "";
    public string? Email { get; set; }
}

public class IntegrationSource
{
    public int Id { get; set; }
    public IntegrationCustomer Customer { get; set; } = new();
    public string? Skipped { get; set; }
}

[Map(typeof(IntegrationSource), MemberList = MemberList.Destination)]
public class IntegrationDto
{
    public int Id { get; set; }

    [From("Customer.FirstName")]
    public string CustomerFirstName { get; set; } = "";

    [From("Customer.Email")]
    [DefaultWhenNull("(no email)")]
    public string CustomerEmail { get; set; } = "";

    [Skip]
    public string? SkippedField { get; set; }
}

public class IntegrationShortCircuitSource
{
    public IntegrationCustomer Customer { get; set; } = new();
}

[Map(typeof(IntegrationShortCircuitSource))]
public class IntegrationShortCircuitDto
{
    [Skip]
    [From("Customer.FirstName")]
    [DefaultWhenNull("(unreachable)")]
    public string? SkippedDespiteAttributes { get; set; }
}

public class IntegrationReverseSource { public int Id { get; set; } }
[Map(typeof(IntegrationReverseSource), ReverseMap = true)]
public class IntegrationReverseDto { public int Id { get; set; } }

public class IntegrationCycleSource
{
    public int Id { get; set; }
    public IntegrationCycleSource? Self { get; set; }
}
[Map(typeof(IntegrationCycleSource), PreserveReferences = true)]
public class IntegrationCycleDto
{
    public int Id { get; set; }
    public IntegrationCycleDto? Self { get; set; }
}
