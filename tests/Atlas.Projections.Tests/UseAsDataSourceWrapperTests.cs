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
