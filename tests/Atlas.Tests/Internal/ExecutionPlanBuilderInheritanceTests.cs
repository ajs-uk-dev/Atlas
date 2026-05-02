using System.Linq.Expressions;
using Atlas.Internal;

namespace Atlas.Tests.Internal;

public class ExecutionPlanBuilderInheritanceTests
{
    private static (MapperRegistry registry, LambdaExpression lambda) Build<TSource, TDestination>(
        Action<MapperConfigurationExpression> configure)
    {
        var config = new MapperConfiguration(configure);
        var registry = config.Internal_Registry;
        var tm = registry.GetTypeMap(new TypePair(typeof(TSource), typeof(TDestination)))!;
        return (registry, ExecutionPlanBuilder.Build(tm, registry));
    }

    [Fact]
    public void Build_BaseWithSingleInclude_LambdaContainsTypeIs()
    {
        var (_, lambda) = Build<EpAnimal, EpAnimalDto>(c =>
        {
            c.CreateMap<EpAnimal, EpAnimalDto>().Include<EpDog, EpDogDto>();
            c.CreateMap<EpDog, EpDogDto>();
        });
        Assert.True(AssertExpression.Contains<TypeBinaryExpression>(lambda.Body));
    }

    [Fact]
    public void Build_BaseWithThreeIncludes_LambdaHasThreeChainedConditionals()
    {
        var (_, lambda) = Build<EpAnimal, EpAnimalDto>(c =>
        {
            c.CreateMap<EpAnimal, EpAnimalDto>()
                .Include<EpDog, EpDogDto>()
                .Include<EpCat, EpCatDto>()
                .Include<EpBird, EpBirdDto>();
            c.CreateMap<EpDog, EpDogDto>();
            c.CreateMap<EpCat, EpCatDto>();
            c.CreateMap<EpBird, EpBirdDto>();
        });
        // Three TypeBinaryExpressions (one per Include) plus possibly one ReferenceEqual for null guard.
        Assert.Equal(3, AssertExpression.CountNodes<TypeBinaryExpression>(lambda.Body));
    }

    [Fact]
    public void Build_BaseWithIncludes_FallsThroughToOriginalBaseBody()
    {
        // Test that compiling and invoking with a non-derived base instance still maps via base.
        var (_, lambda) = Build<EpAnimal, EpAnimalDto>(c =>
        {
            c.CreateMap<EpAnimal, EpAnimalDto>().Include<EpDog, EpDogDto>();
            c.CreateMap<EpDog, EpDogDto>();
        });
        var fn = (Func<EpAnimal, EpAnimalDto>)lambda.Compile();
        var dst = fn(new EpAnimal { Name = "x" });
        Assert.NotNull(dst);
        Assert.Equal("x", dst.Name);
        Assert.IsType<EpAnimalDto>(dst);
        Assert.IsNotType<EpDogDto>(dst);
    }

    [Fact]
    public void Build_DispatchOrder_MostDerivedFirst()
    {
        // Compile + invoke with a Beagle should hit the Beagle branch (returning EpBeagleDto),
        // not the Dog branch (returning EpDogDto).
        var (_, lambda) = Build<EpAnimal, EpAnimalDto>(c =>
        {
            c.CreateMap<EpAnimal, EpAnimalDto>()
                .Include<EpDog, EpDogDto>()
                .Include<EpBeagle, EpBeagleDto>();
            c.CreateMap<EpDog, EpDogDto>();
            c.CreateMap<EpBeagle, EpBeagleDto>();
        });
        var fn = (Func<EpAnimal, EpAnimalDto>)lambda.Compile();
        var dst = fn(new EpBeagle { Name = "rex" });
        Assert.IsType<EpBeagleDto>(dst);
    }

    [Fact]
    public void Build_NullSource_StillReturnsDefault()
    {
        var (_, lambda) = Build<EpAnimal, EpAnimalDto>(c =>
        {
            c.CreateMap<EpAnimal, EpAnimalDto>().Include<EpDog, EpDogDto>();
            c.CreateMap<EpDog, EpDogDto>();
        });
        var fn = (Func<EpAnimal, EpAnimalDto>)lambda.Compile();
        Assert.Null(fn(null!));
    }

    [Fact]
    public void Build_NoIncludes_NoTypeIsConditionalsEmitted()
    {
        // Zero-overhead invariant: a TypeMap with no IncludedDerived must have the same shape
        // as v1's compiled lambda (no TypeBinaryExpression nodes from inheritance prologue).
        var (_, lambda) = Build<EpFlatSrc, EpFlatDst>(c => c.CreateMap<EpFlatSrc, EpFlatDst>());
        Assert.False(AssertExpression.Contains<TypeBinaryExpression>(lambda.Body));
    }

    [Fact]
    public void Build_DerivedDispatchCallsMappingInvoker()
    {
        // Each derived branch should call MappingInvoker.Invoke<TDerived, TDerivedDst>.
        var (_, lambda) = Build<EpAnimal, EpAnimalDto>(c =>
        {
            c.CreateMap<EpAnimal, EpAnimalDto>().Include<EpDog, EpDogDto>();
            c.CreateMap<EpDog, EpDogDto>();
        });
        Assert.True(AssertExpression.ContainsCallTo(lambda.Body, "MappingInvoker", "Invoke"));
    }

    [Fact]
    public void Build_DerivedDestinationCastToBase_EmitsConvert()
    {
        // The Map<Dog, DogDto>(d) result must be cast to AnimalDto for the base lambda's return type.
        var (_, lambda) = Build<EpAnimal, EpAnimalDto>(c =>
        {
            c.CreateMap<EpAnimal, EpAnimalDto>().Include<EpDog, EpDogDto>();
            c.CreateMap<EpDog, EpDogDto>();
        });
        Assert.True(AssertExpression.Contains<UnaryExpression>(lambda.Body));
    }
}

// ---- Test fixtures ----
public class EpAnimal { public string Name { get; set; } = ""; }
public class EpDog : EpAnimal { public string Breed { get; set; } = ""; }
public class EpBeagle : EpDog { public bool ShortLegs { get; set; } }
public class EpCat : EpAnimal { }
public class EpBird : EpAnimal { }

public class EpAnimalDto { public string Name { get; set; } = ""; }
public class EpDogDto : EpAnimalDto { public string Breed { get; set; } = ""; }
public class EpBeagleDto : EpDogDto { public bool ShortLegs { get; set; } }
public class EpCatDto : EpAnimalDto { }
public class EpBirdDto : EpAnimalDto { }

public class EpFlatSrc { public int Id { get; set; } public string Name { get; set; } = ""; }
public class EpFlatDst { public int Id { get; set; } public string Name { get; set; } = ""; }
