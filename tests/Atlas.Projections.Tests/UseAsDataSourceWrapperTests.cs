using System.Linq.Expressions;
using Atlas;

namespace Atlas.Projections.Tests;

public class UseAsDataSourceWrapperTests
{
    private static MapperConfiguration BuildCfg() =>
        new MapperConfiguration(c => c.CreateMap<UEDS_WrapperSrc, UEDS_WrapperDto>());

    private static IQueryable<UEDS_WrapperSrc> BuildSource() => new[]
    {
        new UEDS_WrapperSrc { Id = 1, Name = "Alice", Total = 50m },
        new UEDS_WrapperSrc { Id = 2, Name = "Bob",   Total = 150m },
        new UEDS_WrapperSrc { Id = 3, Name = "Carol", Total = 250m },
    }.AsQueryable();

    [Fact]
    public void Where_TranslatesPredicateAndAppliesToUnderlying()
    {
        var cfg = BuildCfg();
        var wrapper = BuildSource().UseAsDataSource(cfg).For<UEDS_WrapperDto>();

        var filtered = wrapper.Where(d => d.Total > 100m);

        Assert.NotNull(filtered);
        Assert.NotSame(wrapper, filtered);
    }

    [Fact]
    public void Where_ChainedTwice_BothApply()
    {
        var cfg = BuildCfg();
        var wrapper = BuildSource().UseAsDataSource(cfg).For<UEDS_WrapperDto>();

        var filtered = wrapper
            .Where(d => d.Total > 100m)
            .Where(d => d.Name.StartsWith("B"));

        Assert.NotNull(filtered);
    }

    [Fact]
    public void OrderBy_ReturnsOrderedWrapper()
    {
        var cfg = BuildCfg();
        var wrapper = BuildSource().UseAsDataSource(cfg).For<UEDS_WrapperDto>();

        IUseAsDataSourceOrdered<UEDS_WrapperSrc, UEDS_WrapperDto> ordered =
            wrapper.OrderBy(d => d.Total);

        Assert.NotNull(ordered);
    }

    [Fact]
    public void ThenBy_ChainsAfterOrderBy()
    {
        var cfg = BuildCfg();
        var wrapper = BuildSource().UseAsDataSource(cfg).For<UEDS_WrapperDto>();

        var ordered = wrapper
            .OrderBy(d => d.Total)
            .ThenBy(d => d.Name);

        Assert.NotNull(ordered);
    }

    [Fact]
    public void OrderByDescending_ReturnsOrderedWrapper()
    {
        var cfg = BuildCfg();
        var wrapper = BuildSource().UseAsDataSource(cfg).For<UEDS_WrapperDto>();

        var ordered = wrapper.OrderByDescending(d => d.Total);

        Assert.NotNull(ordered);
    }

    [Fact]
    public void Skip_PassesThroughWithoutTranslation()
    {
        var cfg = BuildCfg();
        var wrapper = BuildSource().UseAsDataSource(cfg).For<UEDS_WrapperDto>();

        var skipped = wrapper.Skip(1);
        Assert.NotNull(skipped);
        Assert.NotSame(wrapper, skipped);
    }

    [Fact]
    public void Take_PassesThroughWithoutTranslation()
    {
        var cfg = BuildCfg();
        var wrapper = BuildSource().UseAsDataSource(cfg).For<UEDS_WrapperDto>();

        var taken = wrapper.Take(2);
        Assert.NotNull(taken);
        Assert.NotSame(wrapper, taken);
    }
}

public class UEDS_WrapperSrc
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Total { get; set; }
}

public class UEDS_WrapperDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Total { get; set; }
}

public class UseAsDataSourceTerminalOperatorTests
{
    private static MapperConfiguration BuildCfg() =>
        new MapperConfiguration(c => c.CreateMap<UEDS_WrapperSrc, UEDS_WrapperDto>());

    private static IQueryable<UEDS_WrapperSrc> BuildSource() => new[]
    {
        new UEDS_WrapperSrc { Id = 1, Name = "Alice", Total = 50m },
        new UEDS_WrapperSrc { Id = 2, Name = "Bob",   Total = 150m },
        new UEDS_WrapperSrc { Id = 3, Name = "Carol", Total = 250m },
    }.AsQueryable();

    [Fact]
    public void Any_NoPredicate_DelegatesToUnderlying()
    {
        var cfg = BuildCfg();
        var wrapper = BuildSource().UseAsDataSource(cfg).For<UEDS_WrapperDto>();

        Assert.True(wrapper.Any());
    }

    [Fact]
    public void Any_WithPredicate_TranslatesAndDelegates()
    {
        var cfg = BuildCfg();
        var wrapper = BuildSource().UseAsDataSource(cfg).For<UEDS_WrapperDto>();

        Assert.True(wrapper.Any(d => d.Total > 100m));
        Assert.False(wrapper.Any(d => d.Total > 1000m));
    }

    [Fact]
    public void All_TranslatesAndDelegates()
    {
        var cfg = BuildCfg();
        var wrapper = BuildSource().UseAsDataSource(cfg).For<UEDS_WrapperDto>();

        Assert.True(wrapper.All(d => d.Total > 0m));
        Assert.False(wrapper.All(d => d.Total > 100m));
    }

    [Fact]
    public void Count_NoPredicate_DelegatesToUnderlying()
    {
        var cfg = BuildCfg();
        var wrapper = BuildSource().UseAsDataSource(cfg).For<UEDS_WrapperDto>();

        Assert.Equal(3, wrapper.Count());
    }

    [Fact]
    public void Count_WithPredicate_TranslatesAndDelegates()
    {
        var cfg = BuildCfg();
        var wrapper = BuildSource().UseAsDataSource(cfg).For<UEDS_WrapperDto>();

        Assert.Equal(2, wrapper.Count(d => d.Total > 100m));
    }

    [Fact]
    public void First_WithPredicate_MaterializesViaProjectTo()
    {
        var cfg = BuildCfg();
        var wrapper = BuildSource().UseAsDataSource(cfg).For<UEDS_WrapperDto>();

        var dto = wrapper.First(d => d.Total > 100m);
        Assert.True(dto.Total > 100m);
    }

    [Fact]
    public void FirstOrDefault_WithPredicate_NoMatch_ReturnsNull()
    {
        var cfg = BuildCfg();
        var wrapper = BuildSource().UseAsDataSource(cfg).For<UEDS_WrapperDto>();

        var dto = wrapper.FirstOrDefault(d => d.Total > 1000m);
        Assert.Null(dto);
    }

    [Fact]
    public void AsQueryable_ReturnsTranslatedDestinationQuery()
    {
        var cfg = BuildCfg();
        var wrapper = BuildSource().UseAsDataSource(cfg).For<UEDS_WrapperDto>();

        var q = wrapper.AsQueryable();
        var list = q.ToList();
        Assert.Equal(3, list.Count);
        Assert.Contains(list, d => d.Name == "Alice");
    }

    [Fact]
    public void Enumeration_TriggersProjectTo()
    {
        var cfg = BuildCfg();
        var wrapper = BuildSource().UseAsDataSource(cfg).For<UEDS_WrapperDto>();

        var list = wrapper.ToList();
        Assert.Equal(3, list.Count);
    }

    [Fact]
    public void WhereThenEnumerate_TranslatesAndProjects()
    {
        var cfg = BuildCfg();
        var wrapper = BuildSource().UseAsDataSource(cfg).For<UEDS_WrapperDto>();

        var filtered = wrapper.Where(d => d.Total > 100m).ToList();
        Assert.Equal(2, filtered.Count);
        Assert.All(filtered, d => Assert.True(d.Total > 100m));
    }

    [Fact]
    public void OrderByThenEnumerate_TranslatesAndProjectsOrdered()
    {
        var cfg = BuildCfg();
        var wrapper = BuildSource().UseAsDataSource(cfg).For<UEDS_WrapperDto>();

        var sorted = wrapper.OrderByDescending(d => d.Total).ToList();
        Assert.Equal(3, sorted.Count);
        Assert.Equal(250m, sorted[0].Total);
        Assert.Equal(150m, sorted[1].Total);
        Assert.Equal(50m,  sorted[2].Total);
    }
}
