using System.Reflection;

namespace Atlas.Tests;

public class IgnoreAttributeTests
{
    [Fact]
    public void AttributeUsage_TargetsPropertyOnly_NotInheritedNotMultiple_Sealed()
    {
        var usage = typeof(IgnoreAttribute).GetCustomAttribute<AttributeUsageAttribute>();
        Assert.NotNull(usage);
        Assert.Equal(AttributeTargets.Property, usage!.ValidOn);
        Assert.False(usage.Inherited);
        Assert.False(usage.AllowMultiple);
        Assert.True(typeof(IgnoreAttribute).IsSealed);
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
    public void Ignored_PropertyExcludedFromMapping()
    {
        var mapper = BuildMapper(typeof(IgnoreFixtureDto));
        var src = new IgnoreFixtureSource { Id = 1, Skipped = "should be skipped" };
        var dto = mapper.Map<IgnoreFixtureDto>(src);
        Assert.Equal(1, dto.Id);
        Assert.Null(dto.Skipped);
    }

    [Fact]
    public void Ignored_PropertyExcludedFromValidation()
    {
        var cfg = new MapperConfigurationExpression();
        try { Atlas.Internal.AttributeScanner.Discover(typeof(IgnoreFixtureValidationDto).Assembly, cfg); }
        catch (AtlasConfigurationException) { /* expected */ }

        var tm = cfg.GetTypeMaps().First(t => t.DestinationType == typeof(IgnoreFixtureValidationDto));
        var skippedPm = tm.PropertyMaps.FirstOrDefault(pm => pm.Name == "Skipped");
        Assert.NotNull(skippedPm);
        Assert.True(skippedPm!.Ignored, "Skipped property should be marked Ignored after [Ignore] attribute applied.");
    }

    [Fact]
    public void Ignore_OnPropertyWithoutMap_SilentlyNoOp()
    {
        // Class is NOT decorated with [Map]; its [Ignore] property is silently ignored.
        var expr = new MapperConfigurationExpression();
        try { Atlas.Internal.AttributeScanner.Discover(typeof(IgnoreOrphanFixture).Assembly, expr); }
        catch (AtlasConfigurationException) { /* expected — bad fixtures from Task 3 */ }
        Assert.DoesNotContain(expr.GetTypeMaps(), t => t.DestinationType == typeof(IgnoreOrphanFixture));
    }

    [Fact]
    public void Ignored_UpdateInPlace_PreservesExistingValue()
    {
        var mapper = BuildMapper(typeof(IgnoreFixtureDto));
        var existing = new IgnoreFixtureDto { Id = 0, Skipped = "do not touch" };
        var src = new IgnoreFixtureSource { Id = 99, Skipped = "ignored" };
        mapper.Map(src, existing);
        Assert.Equal(99, existing.Id);
        Assert.Equal("do not touch", existing.Skipped);
    }
}

public class IgnoreFixtureSource
{
    public int Id { get; set; }
    public string? Skipped { get; set; }
}

[Map(typeof(IgnoreFixtureSource))]
public class IgnoreFixtureDto
{
    public int Id { get; set; }
    [Ignore]
    public string? Skipped { get; set; }
}

[Map(typeof(IgnoreFixtureSource), MemberList = MemberList.Destination)]
public class IgnoreFixtureValidationDto
{
    public int Id { get; set; }
    [Ignore]
    public string? Skipped { get; set; }
}

public class IgnoreOrphanFixture
{
    public int Id { get; set; }
    [Ignore]
    public string Skipped { get; set; } = "";
}
