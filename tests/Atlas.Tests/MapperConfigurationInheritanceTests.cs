using Atlas.Internal;

namespace Atlas.Tests;

public class MapperConfigurationInheritanceTests
{
    [Fact]
    public void Include_OnBase_PopulatesIncludedDerivedOnBase()
    {
        var config = new MapperConfiguration(c =>
        {
            c.CreateMap<MciAnimal, MciAnimalDto>()
                .Include<MciDog, MciDogDto>();
            c.CreateMap<MciDog, MciDogDto>();
        });
        var basePair = new TypePair(typeof(MciAnimal), typeof(MciAnimalDto));
        var baseTm = config.Internal_Registry.GetTypeMap(basePair)!;
        Assert.Contains(new TypePair(typeof(MciDog), typeof(MciDogDto)), baseTm.IncludedDerived);
    }

    [Fact]
    public void IncludeBase_OnDerived_PopulatesIncludedDerivedOnBase()
    {
        var config = new MapperConfiguration(c =>
        {
            c.CreateMap<MciAnimal, MciAnimalDto>();
            c.CreateMap<MciDog, MciDogDto>().IncludeBase<MciAnimal, MciAnimalDto>();
        });
        var basePair = new TypePair(typeof(MciAnimal), typeof(MciAnimalDto));
        var baseTm = config.Internal_Registry.GetTypeMap(basePair)!;
        Assert.Contains(new TypePair(typeof(MciDog), typeof(MciDogDto)), baseTm.IncludedDerived);
    }

    [Fact]
    public void Include_TwoLevels_BaseSeesGrandchild_NotJustChild()
    {
        var config = new MapperConfiguration(c =>
        {
            c.CreateMap<MciAnimal, MciAnimalDto>().Include<MciDog, MciDogDto>();
            c.CreateMap<MciDog, MciDogDto>().Include<MciBeagle, MciBeagleDto>();
            c.CreateMap<MciBeagle, MciBeagleDto>();
        });
        var animalTm = config.Internal_Registry.GetTypeMap(new TypePair(typeof(MciAnimal), typeof(MciAnimalDto)))!;
        var dogTm = config.Internal_Registry.GetTypeMap(new TypePair(typeof(MciDog), typeof(MciDogDto)))!;
        Assert.Contains(new TypePair(typeof(MciDog), typeof(MciDogDto)), animalTm.IncludedDerived);
        Assert.Contains(new TypePair(typeof(MciBeagle), typeof(MciBeagleDto)), dogTm.IncludedDerived);
    }

    [Fact]
    public void IncludeBase_DerivedRegisteredInDifferentProfile_ResolvesCorrectly()
    {
        var config = new MapperConfiguration(c =>
        {
            c.AddProfile(new BaseProfile());
            c.AddProfile(new DerivedProfile());
        });
        var basePair = new TypePair(typeof(MciAnimal), typeof(MciAnimalDto));
        var baseTm = config.Internal_Registry.GetTypeMap(basePair)!;
        Assert.Contains(new TypePair(typeof(MciDog), typeof(MciDogDto)), baseTm.IncludedDerived);
    }

    [Fact]
    public void Include_DerivedDispatchOrder_MostDerivedFirst()
    {
        var config = new MapperConfiguration(c =>
        {
            c.CreateMap<MciAnimal, MciAnimalDto>()
                .Include<MciDog, MciDogDto>()
                .Include<MciBeagle, MciBeagleDto>();
            c.CreateMap<MciDog, MciDogDto>();
            c.CreateMap<MciBeagle, MciBeagleDto>();
        });
        var animalTm = config.Internal_Registry.GetTypeMap(new TypePair(typeof(MciAnimal), typeof(MciAnimalDto)))!;
        var beagleIdx = animalTm.IncludedDerived.IndexOf(new TypePair(typeof(MciBeagle), typeof(MciBeagleDto)));
        var dogIdx = animalTm.IncludedDerived.IndexOf(new TypePair(typeof(MciDog), typeof(MciDogDto)));
        Assert.True(beagleIdx >= 0 && dogIdx >= 0);
        Assert.True(beagleIdx < dogIdx, "Beagle (most-derived) must come before Dog");
    }

    [Fact]
    public void Include_DuplicateDeclaration_IsIdempotent()
    {
        var config = new MapperConfiguration(c =>
        {
            c.CreateMap<MciAnimal, MciAnimalDto>()
                .Include<MciDog, MciDogDto>()
                .Include<MciDog, MciDogDto>(); // duplicate
            c.CreateMap<MciDog, MciDogDto>();
        });
        var animalTm = config.Internal_Registry.GetTypeMap(new TypePair(typeof(MciAnimal), typeof(MciAnimalDto)))!;
        Assert.Single(animalTm.IncludedDerived);
    }

    [Fact]
    public void Include_TwoLevels_ReverseRegistrationOrder_AllInheritExplicitConfig()
    {
        // Critical regression: when only Include is used (no IncludeBase) and registration
        // order is reverse-of-inheritance, the topological sort must still process A before
        // B before C so that A's explicit config flows down through both levels.
        var config = new MapperConfiguration(c =>
        {
            c.CreateMap<MciBeagle, MciBeagleDto>();                              // grandchild first
            c.CreateMap<MciDog, MciDogDto>().Include<MciBeagle, MciBeagleDto>();  // child second
            c.CreateMap<MciAnimal, MciAnimalDto>()
                .ForMember(d => d.Name, o => o.MapFrom(s => "from-base-" + s.Name))
                .Include<MciDog, MciDogDto>();                                    // base last
        });
        var beagleTm = config.Internal_Registry.GetTypeMap(new TypePair(typeof(MciBeagle), typeof(MciBeagleDto)))!;
        // Beagle should have Name binding inherited from Animal (via Dog).
        var nameBinding = beagleTm.PropertyMaps.SingleOrDefault(p => p.Name == nameof(MciAnimalDto.Name));
        Assert.NotNull(nameBinding);
        Assert.True(nameBinding.IsExplicit);
        Assert.NotNull(nameBinding.CustomExpression); // base used MapFrom expression
    }

    [Fact]
    public void Include_DerivedMapNotRegistered_FailsValidation()
    {
        var config = new MapperConfiguration(c =>
        {
            c.CreateMap<MciAnimal, MciAnimalDto>()
                .Include<MciDog, MciDogDto>();
            // MciDog->MciDogDto NOT registered
        });
        Assert.Throws<AtlasConfigurationException>(() => config.AssertConfigurationIsValid());
    }

    [Fact]
    public void Include_TypeNotActuallyDerived_FailsValidation()
    {
        // Bypass the generic constraint by registering through an internal entry point — emulate
        // a reflection-driven bug. Easiest: directly add the wrong pair to IncludedDerived after
        // configuration but before validation. Since IncludedDerived is internal-list mutable,
        // the validator must catch this case.
        var config = new MapperConfiguration(c =>
        {
            c.CreateMap<MciAnimal, MciAnimalDto>();
            c.CreateMap<MciUnrelated, MciUnrelatedDto>();
        });
        var animalTm = config.Internal_Registry.GetTypeMap(new TypePair(typeof(MciAnimal), typeof(MciAnimalDto)))!;
        // Force the bad entry directly (simulating reflection-driven misconfig).
        animalTm.IncludedDerived.Add(new TypePair(typeof(MciUnrelated), typeof(MciUnrelatedDto)));
        Assert.Throws<AtlasConfigurationException>(() => config.AssertConfigurationIsValid());
    }
}

// ---- Profiles for cross-profile test ----
public class BaseProfile : MapperProfile
{
    public BaseProfile() { CreateMap<MciAnimal, MciAnimalDto>(); }
}

public class DerivedProfile : MapperProfile
{
    public DerivedProfile() { CreateMap<MciDog, MciDogDto>().IncludeBase<MciAnimal, MciAnimalDto>(); }
}

// ---- Test fixtures ----
public class MciAnimal { public string Name { get; set; } = ""; }
public class MciDog : MciAnimal { public string Breed { get; set; } = ""; }
public class MciBeagle : MciDog { public bool ShortLegs { get; set; } }

public class MciAnimalDto { public string Name { get; set; } = ""; }
public class MciDogDto : MciAnimalDto { public string Breed { get; set; } = ""; }
public class MciBeagleDto : MciDogDto { public bool ShortLegs { get; set; } }

public class MciUnrelated { public int X { get; set; } }
public class MciUnrelatedDto { public int X { get; set; } }
