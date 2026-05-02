using Atlas.Internal;

namespace Atlas.Tests;

public class ValidationInheritanceTests
{
    [Fact]
    public void AssertConfigurationIsValid_AbstractSourceWithNoInclude_Throws()
    {
        var config = new MapperConfiguration(c => c.CreateMap<ViAbstractAnimal, ViAnimalDto>());
        var ex = Assert.Throws<AtlasConfigurationException>(() => config.AssertConfigurationIsValid());
        Assert.Contains(ex.Errors, e => e.Reason.Contains("Abstract type used without any Include", StringComparison.Ordinal));
    }

    [Fact]
    public void AssertConfigurationIsValid_AbstractDestinationWithNoInclude_Throws()
    {
        var config = new MapperConfiguration(c => c.CreateMap<ViAnimal, ViAbstractAnimalDto>());
        var ex = Assert.Throws<AtlasConfigurationException>(() => config.AssertConfigurationIsValid());
        Assert.Contains(ex.Errors, e => e.Reason.Contains("Abstract type used without any Include", StringComparison.Ordinal));
    }

    [Fact]
    public void AssertConfigurationIsValid_AbstractWithInclude_Passes()
    {
        var config = new MapperConfiguration(c =>
        {
            c.CreateMap<ViAbstractAnimal, ViAnimalDto>().Include<ViAbstractDog, ViDogDto>();
            c.CreateMap<ViAbstractDog, ViDogDto>();
        });
        config.AssertConfigurationIsValid(); // does not throw
    }

    [Fact]
    public void AssertConfigurationIsValid_IncludePointsAtUnregisteredMap_Throws()
    {
        var config = new MapperConfiguration(c =>
        {
            c.CreateMap<ViAnimal, ViAnimalDto>().Include<ViDog, ViDogDto>();
            // ViDog -> ViDogDto NOT registered
        });
        var ex = Assert.Throws<AtlasConfigurationException>(() => config.AssertConfigurationIsValid());
        Assert.Contains(ex.Errors, e => e.Reason.Contains("Include declares", StringComparison.Ordinal));
    }

    [Fact]
    public void AssertConfigurationIsValid_IncludeWithNonDerivedTypes_Throws()
    {
        // Force the bad entry directly via the internal list (emulating reflection-driven misconfig).
        var config = new MapperConfiguration(c =>
        {
            c.CreateMap<ViAnimal, ViAnimalDto>();
            c.CreateMap<ViUnrelated, ViUnrelatedDto>();
        });
        var animalTm = config.Internal_Registry.GetTypeMap(new TypePair(typeof(ViAnimal), typeof(ViAnimalDto)))!;
        animalTm.IncludedDerived.Add(new TypePair(typeof(ViUnrelated), typeof(ViUnrelatedDto)));

        var ex = Assert.Throws<AtlasConfigurationException>(() => config.AssertConfigurationIsValid());
        Assert.Contains(ex.Errors, e => e.Reason.Contains("does not derive from the base map's", StringComparison.Ordinal));
    }

    [Fact]
    public void AssertConfigurationIsValid_CustomConverterWithInclude_StillValidatesInheritance()
    {
        // Even with ConvertUsing on the base, an Include pointing at an unregistered map
        // should be reported by the validator.
        var config = new MapperConfiguration(c =>
        {
            var mapping = c.CreateMap<ViAnimal, ViAnimalDto>();
            mapping.ConvertUsing(s => new ViAnimalDto { Name = s.Name });
            mapping.Include<ViDog, ViDogDto>();
            // ViDog -> ViDogDto NOT registered.
        });
        var ex = Assert.Throws<AtlasConfigurationException>(() => config.AssertConfigurationIsValid());
        Assert.Contains(ex.Errors, e => e.Reason.Contains("Include declares", StringComparison.Ordinal));
    }

    [Fact]
    public void AssertConfigurationIsValid_MemberListNoneWithBadInclude_StillValidatesInheritance()
    {
        // Even MemberList.None maps must have their Include declarations validated.
        var config = new MapperConfiguration(c =>
        {
            c.CreateMap<ViAnimal, ViAnimalDto>(MemberList.None)
                .Include<ViDog, ViDogDto>();
            // ViDog -> ViDogDto NOT registered.
        });
        var ex = Assert.Throws<AtlasConfigurationException>(() => config.AssertConfigurationIsValid());
        Assert.Contains(ex.Errors, e => e.Reason.Contains("Include declares", StringComparison.Ordinal));
    }

    [Fact]
    public void AssertConfigurationIsValid_AggregatesAllInheritanceErrors_NotJustFirst()
    {
        var config = new MapperConfiguration(c =>
        {
            c.CreateMap<ViAnimal, ViAnimalDto>()
                .Include<ViDog, ViDogDto>()
                .Include<ViCat, ViCatDto>();
            // Neither derived map is registered.
        });
        var ex = Assert.Throws<AtlasConfigurationException>(() => config.AssertConfigurationIsValid());
        Assert.Equal(2, ex.Errors.Count(e => e.Reason.Contains("Include declares", StringComparison.Ordinal)));
    }
}

// ---- Test fixtures ----
public class ViAnimal { public string Name { get; set; } = ""; }
public class ViDog : ViAnimal { }
public class ViCat : ViAnimal { }
public abstract class ViAbstractAnimal { public string Name { get; set; } = ""; }
public class ViAbstractDog : ViAbstractAnimal { }

public class ViAnimalDto { public string Name { get; set; } = ""; }
public class ViDogDto : ViAnimalDto { }
public class ViCatDto : ViAnimalDto { }
public abstract class ViAbstractAnimalDto { public string Name { get; set; } = ""; }

public class ViUnrelated { public int X { get; set; } }
public class ViUnrelatedDto { public int X { get; set; } }
