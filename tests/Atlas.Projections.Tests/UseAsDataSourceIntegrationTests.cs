using System.Linq.Expressions;
using Atlas;

namespace Atlas.Projections.Tests;

public class UseAsDataSourceIntegrationTests
{
    private static (IQueryable<UEDS_IntSrc> source, MapperConfiguration cfg) Setup()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<UEDS_IntSrc, UEDS_IntDto>());
        var source = new[]
        {
            new UEDS_IntSrc { Id = 1, Name = "Alice", Total = 50m },
            new UEDS_IntSrc { Id = 2, Name = "Bob", Total = 150m },
            new UEDS_IntSrc { Id = 3, Name = "Carol", Total = 250m },
        }.AsQueryable();
        return (source, cfg);
    }

    [Fact]
    public void DI_ResolvedMapperConfiguration_WorksThroughWrapper()
    {
        // Simulates a MapperConfiguration resolved from a DI container: eagerly compiled,
        // then used through the wrapper exactly as a singleton resolved at runtime would be.
        var cfg = new MapperConfiguration(c => c.CreateMap<UEDS_IntSrc, UEDS_IntDto>());
        cfg.CompileMappings();

        var (source, _) = Setup();
        var list = source.UseAsDataSource(cfg).For<UEDS_IntDto>()
            .Where(d => d.Total > 100m)
            .OrderBy(d => d.Total)
            .ToList();

        Assert.Equal(2, list.Count);
        Assert.Equal("Bob", list[0].Name);
        Assert.Equal("Carol", list[1].Name);
    }

    [Fact]
    public void MixedSourceAndDestinationOps_ApplyInOrder()
    {
        var (source, cfg) = Setup();

        // Pre-filter on source side (Total > 0), then DTO-typed ops.
        var list = source
            .Where(s => s.Total > 0m)
            .UseAsDataSource(cfg).For<UEDS_IntDto>()
            .Where(d => d.Total < 200m)
            .OrderBy(d => d.Id)
            .ToList();

        Assert.Equal(2, list.Count);
        Assert.Equal(1, list[0].Id);
        Assert.Equal(2, list[1].Id);
    }

    [Fact]
    public void MultiOpChain_AllFourCategories_ProducesCorrectResult()
    {
        var (source, cfg) = Setup();

        // Filter + order + paging + terminal-predicate.
        var any = source.UseAsDataSource(cfg).For<UEDS_IntDto>()
            .Where(d => d.Total > 0m)
            .OrderBy(d => d.Id)
            .Skip(1)
            .Take(1)
            .Any(d => d.Name == "Bob");

        Assert.True(any);
    }

    [Fact]
    public void DirectUseHelper_ProducesEquivalentResult()
    {
        var (source, cfg) = Setup();

        Expression<Func<UEDS_IntDto, bool>> destPredicate = d => d.Total > 100m;
        var srcPredicate = cfg.Translate<UEDS_IntSrc, UEDS_IntDto, bool>(destPredicate);

        var directList = source.Where(srcPredicate).ProjectTo<UEDS_IntDto>(cfg).ToList();
        var wrapperList = source.UseAsDataSource(cfg).For<UEDS_IntDto>().Where(destPredicate).ToList();

        Assert.Equal(directList.Count, wrapperList.Count);
        Assert.Equal(directList.Select(d => d.Id), wrapperList.Select(d => d.Id));
    }

    [Fact]
    public void MultipleUseAsDataSourceCalls_OnSameSource_ComposeIndependently()
    {
        var (source, cfg) = Setup();

        var list1 = source.UseAsDataSource(cfg).For<UEDS_IntDto>().Where(d => d.Total > 100m).ToList();
        var list2 = source.UseAsDataSource(cfg).For<UEDS_IntDto>().Where(d => d.Total < 100m).ToList();

        Assert.Equal(2, list1.Count);
        Assert.Single(list2);
        Assert.Equal(1, list2[0].Id);
    }

    [Fact]
    public void AsQueryable_EscapeHatch_WorksWithStandardLinq()
    {
        var (source, cfg) = Setup();

        // Drop down to IQueryable<TDest>, use Select (not on the wrapper surface).
        var totals = source.UseAsDataSource(cfg).For<UEDS_IntDto>()
            .Where(d => d.Total > 0m)
            .AsQueryable()
            .Select(d => d.Total)
            .ToList();

        Assert.Equal(3, totals.Count);
    }
}

public class UEDS_IntSrc
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Total { get; set; }
}

public class UEDS_IntDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Total { get; set; }
}
