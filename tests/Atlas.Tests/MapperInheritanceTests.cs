using Atlas;

namespace Atlas.Tests;

public class MapperInheritanceTests
{
    private static IMapper BuildMapper(Action<MapperConfigurationExpression> configure)
    {
        var config = new MapperConfiguration(configure);
        return config.CreateMapper();
    }

    [Fact]
    public void Map_TypedOverload_BaseDeclared_RuntimeIsDerived_DispatchesToDerivedMap()
    {
        var mapper = BuildMapper(c =>
        {
            c.CreateMap<MiAnimal, MiAnimalDto>()
                .ForMember(d => d.DisplayName, o => o.MapFrom(s => s.Name))
                .Include<MiDog, MiDogDto>();
            c.CreateMap<MiDog, MiDogDto>();
        });
        MiAnimal a = new MiDog { Name = "rex", Breed = "Beagle" };
        var dto = mapper.Map<MiAnimal, MiAnimalDto>(a);
        Assert.IsType<MiDogDto>(dto);
        var dogDto = (MiDogDto)dto;
        Assert.Equal("rex", dogDto.DisplayName);
        Assert.Equal("Beagle", dogDto.Breed);
    }

    [Fact]
    public void Map_TypedOverload_BaseDeclared_RuntimeIsBase_UsesBaseMap()
    {
        var mapper = BuildMapper(c =>
        {
            c.CreateMap<MiAnimal, MiAnimalDto>()
                .ForMember(d => d.DisplayName, o => o.MapFrom(s => s.Name))
                .Include<MiDog, MiDogDto>();
            c.CreateMap<MiDog, MiDogDto>();
        });
        var dto = mapper.Map<MiAnimal, MiAnimalDto>(new MiAnimal { Name = "rex" });
        Assert.IsType<MiAnimalDto>(dto);
        Assert.Equal("rex", dto.DisplayName);
    }

    [Fact]
    public void Map_NestedDerivedInBaseCollection_DispatchesElementByElement()
    {
        var mapper = BuildMapper(c =>
        {
            c.CreateMap<MiAnimal, MiAnimalDto>()
                .ForMember(d => d.DisplayName, o => o.MapFrom(s => s.Name))
                .Include<MiDog, MiDogDto>()
                .Include<MiCat, MiCatDto>();
            c.CreateMap<MiDog, MiDogDto>();
            c.CreateMap<MiCat, MiCatDto>();
            c.CreateMap<List<MiAnimal>, List<MiAnimalDto>>(MemberList.None);
        });
        var animals = new List<MiAnimal>
        {
            new MiDog { Name = "rex", Breed = "Beagle" },
            new MiCat { Name = "whiskers", IsIndoor = true },
        };
        var dtos = mapper.Map<List<MiAnimal>, List<MiAnimalDto>>(animals);
        Assert.IsType<MiDogDto>(dtos[0]);
        Assert.IsType<MiCatDto>(dtos[1]);
        Assert.Equal("Beagle", ((MiDogDto)dtos[0]).Breed);
        Assert.True(((MiCatDto)dtos[1]).IsIndoor);
    }

    [Fact]
    public void Map_TwoLevelInheritance_BeagleViaAnimal_UsesBeagleMap()
    {
        var mapper = BuildMapper(c =>
        {
            c.CreateMap<MiAnimal, MiAnimalDto>()
                .ForMember(d => d.DisplayName, o => o.MapFrom(s => s.Name))
                .Include<MiDog, MiDogDto>()
                .Include<MiBeagle, MiBeagleDto>();
            c.CreateMap<MiDog, MiDogDto>().Include<MiBeagle, MiBeagleDto>();
            c.CreateMap<MiBeagle, MiBeagleDto>();
        });
        MiAnimal a = new MiBeagle { Name = "rex", Breed = "Beagle", ShortLegs = true };
        var dto = mapper.Map<MiAnimal, MiAnimalDto>(a);
        Assert.IsType<MiBeagleDto>(dto);
        Assert.True(((MiBeagleDto)dto).ShortLegs);
    }

    [Fact]
    public void Map_BaseWithIgnore_DerivedDoesNotPopulate()
    {
        // Load-bearing precedence test: base Ignore beats derived convention.
        // MiAnimal.Name normally maps to MiAnimalDto.Name by convention. Base Ignore should kill it.
        var mapper = BuildMapper(c =>
        {
            c.CreateMap<MiAnimal, MiAnimalDtoNamed>()
                .ForMember(d => d.Name, o => o.Ignore())
                .Include<MiDog, MiDogDtoNamed>();
            c.CreateMap<MiDog, MiDogDtoNamed>();
        });
        MiAnimal a = new MiDog { Name = "rex" };
        var dto = mapper.Map<MiAnimal, MiAnimalDtoNamed>(a);
        Assert.Equal("", dto.Name); // default — Ignore inherited, convention overridden
    }

    [Fact]
    public void Map_DerivedOverridesBaseMapFrom_DerivedValueAppears()
    {
        var mapper = BuildMapper(c =>
        {
            c.CreateMap<MiAnimal, MiAnimalDto>()
                .ForMember(d => d.DisplayName, o => o.MapFrom(s => s.Name))
                .Include<MiDog, MiDogDto>();
            c.CreateMap<MiDog, MiDogDto>()
                .ForMember(d => d.DisplayName, o => o.MapFrom(s => "DOG-" + s.Name));
        });
        var dto = mapper.Map<MiAnimal, MiAnimalDto>(new MiDog { Name = "rex" });
        Assert.Equal("DOG-rex", dto.DisplayName); // derived wins
    }

    [Fact]
    public void Map_DerivedInheritsBaseMapFrom_BaseValueAppears()
    {
        var mapper = BuildMapper(c =>
        {
            c.CreateMap<MiAnimal, MiAnimalDto>()
                .ForMember(d => d.DisplayName, o => o.MapFrom(s => "BASE-" + s.Name))
                .Include<MiDog, MiDogDto>();
            c.CreateMap<MiDog, MiDogDto>(); // no override
        });
        var dto = mapper.Map<MiAnimal, MiAnimalDto>(new MiDog { Name = "rex" });
        Assert.Equal("BASE-rex", dto.DisplayName); // inherited
    }

    [Fact]
    public void Map_AbstractBase_RuntimeDerivedDispatched_Succeeds()
    {
        var mapper = BuildMapper(c =>
        {
            c.CreateMap<MiAbstractAnimal, MiAnimalDto>()
                .ForMember(d => d.DisplayName, o => o.MapFrom(s => s.Name))
                .Include<MiAbstractDog, MiDogDto>();
            c.CreateMap<MiAbstractDog, MiDogDto>();
        });
        MiAbstractAnimal a = new MiAbstractDog { Name = "rex", Breed = "Beagle" };
        var dto = mapper.Map<MiAbstractAnimal, MiAnimalDto>(a);
        Assert.IsType<MiDogDto>(dto);
    }

    [Fact]
    public void Map_RuntimeTypeNotIncluded_FallsThroughToBase()
    {
        var mapper = BuildMapper(c =>
        {
            c.CreateMap<MiAnimal, MiAnimalDto>()
                .ForMember(d => d.DisplayName, o => o.MapFrom(s => s.Name))
                .Include<MiDog, MiDogDto>();
            c.CreateMap<MiDog, MiDogDto>();
            // MiCat NOT included
        });
        MiAnimal a = new MiCat { Name = "whiskers" };
        var dto = mapper.Map<MiAnimal, MiAnimalDto>(a);
        // Falls through to base Animal -> AnimalDto map.
        Assert.IsType<MiAnimalDto>(dto);
        Assert.Equal("whiskers", dto.DisplayName);
    }

    [Fact]
    public void Map_SelfMapWithIncludes_DispatchChainExecutes_NotUnsafeAsShortCircuit()
    {
        // CRITICAL: MiAnimal -> MiAnimal (same type both sides) with Include<MiDog, MiDog>().
        // v1's MappingInvoker.Invoke short-circuits on typeof(TSource) == typeof(TDestination)
        // via Unsafe.As, returning the source unchanged. With Includes, that short-circuit
        // would skip the dispatch chain. The fix (Step 4) guards the short-circuit.
        var mapper = BuildMapper(c =>
        {
            c.CreateMap<MiAnimal, MiAnimal>(MemberList.None)
                .Include<MiDog, MiDog>();
            c.CreateMap<MiDog, MiDog>(MemberList.None)
                .ForMember(d => d.Name, o => o.MapFrom(s => "CLONED-" + s.Name));
        });
        MiAnimal a = new MiDog { Name = "rex" };
        var result = mapper.Map<MiAnimal, MiAnimal>(a);
        Assert.NotSame(a, result); // dispatch chain ran, NOT identity short-circuit
        Assert.Equal("CLONED-rex", result.Name);
    }
}

// ---- Test fixtures ----
public class MiAnimal { public string Name { get; set; } = ""; }
public class MiDog : MiAnimal { public string Breed { get; set; } = ""; }
public class MiBeagle : MiDog { public bool ShortLegs { get; set; } }
public class MiCat : MiAnimal { public bool IsIndoor { get; set; } }

public class MiAnimalDto { public string DisplayName { get; set; } = ""; }
public class MiDogDto : MiAnimalDto { public string Breed { get; set; } = ""; }
public class MiBeagleDto : MiDogDto { public bool ShortLegs { get; set; } }
public class MiCatDto : MiAnimalDto { public bool IsIndoor { get; set; } }

// For the Ignore test, separate DTO with Name (matching source) instead of DisplayName.
public class MiAnimalDtoNamed { public string Name { get; set; } = ""; }
public class MiDogDtoNamed : MiAnimalDtoNamed { public string Breed { get; set; } = ""; }

public abstract class MiAbstractAnimal { public string Name { get; set; } = ""; }
public class MiAbstractDog : MiAbstractAnimal { public string Breed { get; set; } = ""; }
