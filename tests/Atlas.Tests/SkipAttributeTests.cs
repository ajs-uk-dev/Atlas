using System.Reflection;

namespace Atlas.Tests;

public class SkipAttributeTests
{
    [Fact]
    public void AttributeUsage_TargetsPropertyOnly_NotInheritedNotMultiple_Sealed()
    {
        var usage = typeof(SkipAttribute).GetCustomAttribute<AttributeUsageAttribute>();
        Assert.NotNull(usage);
        Assert.Equal(AttributeTargets.Property, usage!.ValidOn);
        Assert.False(usage.Inherited);
        Assert.False(usage.AllowMultiple);
        Assert.True(typeof(SkipAttribute).IsSealed);
    }

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
    public void Skipped_PropertyExcludedFromMapping()
    {
        var mapper = BuildMapper(typeof(SkipFixtureDto));
        var src = new SkipFixtureSource { Id = 1, Skipped = "should be skipped" };
        var dto = mapper.Map<SkipFixtureDto>(src);
        Assert.Equal(1, dto.Id);
        Assert.Null(dto.Skipped);
    }

    [Fact]
    public void Skipped_PropertyExcludedFromValidation()
    {
        var cfg = new MapperConfigurationExpression();
        try { Atlas.Internal.AttributeScanner.Discover(typeof(SkipFixtureValidationDto).Assembly, cfg); }
        catch (AtlasConfigurationException) { /* expected */ }

        var tm = cfg.GetTypeMaps().First(t => t.DestinationType == typeof(SkipFixtureValidationDto));
        var skippedPm = tm.PropertyMaps.FirstOrDefault(pm => pm.Name == "Skipped");
        Assert.NotNull(skippedPm);
        Assert.True(skippedPm!.Ignored, "Skipped property should be marked Ignored after [Skip] attribute applied.");
    }

    [Fact]
    public void Skip_OnPropertyWithoutMap_SilentlyNoOp()
    {
        // Class is NOT decorated with [Map]; its [Skip] property is silently ignored.
        var expr = new MapperConfigurationExpression();
        try { Atlas.Internal.AttributeScanner.Discover(typeof(SkipOrphanFixture).Assembly, expr); }
        catch (AtlasConfigurationException) { /* expected — bad fixtures from Task 3 */ }
        Assert.DoesNotContain(expr.GetTypeMaps(), t => t.DestinationType == typeof(SkipOrphanFixture));
    }

    [Fact]
    public void Skipped_UpdateInPlace_PreservesExistingValue()
    {
        var mapper = BuildMapper(typeof(SkipFixtureDto));
        var existing = new SkipFixtureDto { Id = 0, Skipped = "do not touch" };
        var src = new SkipFixtureSource { Id = 99, Skipped = "ignored" };
        mapper.Map(src, existing);
        Assert.Equal(99, existing.Id);
        Assert.Equal("do not touch", existing.Skipped);
    }
}

public class SkipFixtureSource
{
    public int Id { get; set; }
    public string? Skipped { get; set; }
}

[Map(typeof(SkipFixtureSource))]
public class SkipFixtureDto
{
    public int Id { get; set; }
    [Skip]
    public string? Skipped { get; set; }
}

[Map(typeof(SkipFixtureSource), MemberList = MemberList.Destination)]
public class SkipFixtureValidationDto
{
    public int Id { get; set; }
    [Skip]
    public string? Skipped { get; set; }
}

public class SkipOrphanFixture
{
    public int Id { get; set; }
    [Skip]
    public string Skipped { get; set; } = "";
}
