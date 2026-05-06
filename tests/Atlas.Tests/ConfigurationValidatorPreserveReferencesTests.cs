using Atlas;

namespace Atlas.Tests;

public class ConfigurationValidatorPreserveReferencesTests
{
    [Fact]
    public void AssertConfigurationIsValid_PreserveReferencesOnly_Passes()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>().PreserveReferences());

        cfg.AssertConfigurationIsValid();   // no exception
    }

    [Fact]
    public void AssertConfigurationIsValid_PreserveReferencesPlusConvertUsing_ThrowsAtlasConfigurationException()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>()
                .PreserveReferences()
                .ConvertUsing(src => new PersonDto { Name = src.Name }));

        var ex = Assert.Throws<AtlasConfigurationException>(() => cfg.AssertConfigurationIsValid());
        Assert.Contains("PreserveReferences", ex.Message);
        Assert.Contains("ConvertUsing", ex.Message);
    }

    [Fact]
    public void AssertConfigurationIsValid_PreserveReferencesPlusOtherFeatures_Passes()
    {
        // Verifies PR + Hooks + AddTransform is allowed.
        // Only ConvertUsing is the conflict.
        var cfg = new MapperConfiguration(c =>
        {
            c.ValueTransformers.Add<string>(s => s == null ? null! : s.Trim());
            c.CreateMap<Person, PersonDto>()
                .PreserveReferences()
                .BeforeMap((s, d) => { })
                .AfterMap((s, d) => { });
        });

        cfg.AssertConfigurationIsValid();   // no exception
    }

    private sealed class Person
    {
        public string? Name { get; set; }
    }

    private sealed class PersonDto
    {
        public string? Name { get; set; }
    }
}
