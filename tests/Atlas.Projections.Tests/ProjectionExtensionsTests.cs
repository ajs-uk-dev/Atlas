using Atlas;
using Atlas.Projections;

namespace Atlas.Projections.Tests;

public class ProjectionExtensionsTests
{
    private static MapperConfiguration BuildConfig(Action<MapperConfigurationExpression> configure) =>
        new MapperConfiguration(configure);

    [Fact]
    public void ProjectTo_FlatPair_ReturnsMappedItems()
    {
        var config = BuildConfig(c => c.CreateMap<EFlatSrc, EFlatDst>());
        var src = new[]
        {
            new EFlatSrc { Id = 1, Name = "a" },
            new EFlatSrc { Id = 2, Name = "b" },
        };
        var result = src.AsQueryable().ProjectTo<EFlatDst>(config).ToList();
        Assert.Equal(2, result.Count);
        Assert.Equal("a", result[0].Name);
    }

    [Fact]
    public void ProjectTo_NestedObject_PopulatesNestedMembersCorrectly()
    {
        var config = BuildConfig(c =>
        {
            c.CreateMap<EInnerSrc, EInnerDst>();
            c.CreateMap<EOuterSrc, EOuterDst>();
        });
        var src = new[] { new EOuterSrc { Inner = new EInnerSrc { Name = "n" } } };
        var result = src.AsQueryable().ProjectTo<EOuterDst>(config).ToList();
        Assert.Equal("n", result[0].Inner.Name);
    }

    [Fact]
    public void ProjectTo_NullNestedSource_ReturnsDefaultDestination_NoNRE()
    {
        var config = BuildConfig(c =>
        {
            c.CreateMap<EInnerSrc, EInnerDst>();
            c.CreateMap<EOuterSrc, EOuterDst>();
        });
        var src = new[] { new EOuterSrc { Inner = null! } };
        var result = src.AsQueryable().ProjectTo<EOuterDst>(config).ToList();
        Assert.Null(result[0].Inner);
    }

    [Fact]
    public void ProjectTo_Collection_MappedItemsInOrder()
    {
        var config = BuildConfig(c =>
        {
            c.CreateMap<EInnerSrc, EInnerDst>();
            c.CreateMap<EParentSrc, EParentDst>();
        });
        var src = new[] { new EParentSrc { Items = { new() { Name = "a" }, new() { Name = "b" } } } };
        var result = src.AsQueryable().ProjectTo<EParentDst>(config).ToList();
        Assert.Equal(["a", "b"], result[0].Items.Select(i => i.Name));
    }

    [Fact]
    public void ProjectTo_FilteredQueryThenProjectTo_ReturnsFilteredResults()
    {
        var config = BuildConfig(c => c.CreateMap<EFlatSrc, EFlatDst>());
        var src = new[]
        {
            new EFlatSrc { Id = 1, Name = "a" },
            new EFlatSrc { Id = 2, Name = "b" },
        };
        var result = src.AsQueryable().Where(s => s.Id == 2).ProjectTo<EFlatDst>(config).ToList();
        Assert.Single(result);
        Assert.Equal("b", result[0].Name);
    }

    [Fact]
    public void ProjectTo_TypeConverterPair_Throws_WithDiagnostic()
    {
        var config = BuildConfig(c =>
            c.CreateMap<EFlatSrc, EFlatDst>().ConvertUsing(s => new EFlatDst { Id = s.Id, Name = s.Name }));
        var src = new[] { new EFlatSrc() };
        var ex = Assert.Throws<AtlasProjectionException>(() => src.AsQueryable().ProjectTo<EFlatDst>(config));
        Assert.Contains(ex.Diagnostics, d => d.Member == "(whole map)");
    }

    [Fact]
    public void ProjectTo_MissingMap_Throws_WithDiagnosticListing()
    {
        var config = BuildConfig(c => { });
        var src = new[] { new EFlatSrc() };
        var ex = Assert.Throws<AtlasProjectionException>(() => src.AsQueryable().ProjectTo<EFlatDst>(config));
        Assert.Single(ex.Diagnostics);
    }

    [Fact]
    public void ProjectTo_DepthLimit_TruncatesRecursiveMember_AtMaxDepth()
    {
        var config = BuildConfig(c => c.CreateMap<ENode, ENode>(MemberList.None));
        var src = new[] { new ENode { Next = new ENode { Next = new ENode() } } };
        var result = src.AsQueryable().ProjectTo<ENode>(config, maxDepth: 1).ToList();
        Assert.NotNull(result[0]);
        Assert.Null(result[0].Next);
    }

    [Fact]
    public void ProjectTo_DefaultMaxDepth_IsThree()
    {
        // A 4-deep chain projected with default depth 3: the 4th level becomes default(null).
        var config = BuildConfig(c => c.CreateMap<ENode, ENode>(MemberList.None));
        var deep = new ENode { Next = new ENode { Next = new ENode { Next = new ENode() } } };
        var result = new[] { deep }.AsQueryable().ProjectTo<ENode>(config).ToList();
        Assert.NotNull(result[0].Next);
        Assert.NotNull(result[0].Next!.Next);
        Assert.Null(result[0].Next!.Next!.Next);
    }

    [Fact]
    public void ProjectTo_MaxDepthZero_ThrowsArgumentOutOfRange()
    {
        var config = BuildConfig(c => c.CreateMap<EFlatSrc, EFlatDst>());
        var src = new[] { new EFlatSrc() };
        Assert.Throws<ArgumentOutOfRangeException>(() => src.AsQueryable().ProjectTo<EFlatDst>(config, maxDepth: 0));
    }
}

// ---- Test fixtures ----
public class EFlatSrc { public int Id { get; set; } public string Name { get; set; } = ""; }
public class EFlatDst { public int Id { get; set; } public string Name { get; set; } = ""; }
public class EInnerSrc { public string Name { get; set; } = ""; }
public class EInnerDst { public string Name { get; set; } = ""; }
public class EOuterSrc { public EInnerSrc Inner { get; set; } = new(); }
public class EOuterDst { public EInnerDst Inner { get; set; } = new(); }
public class EParentSrc { public List<EInnerSrc> Items { get; set; } = new(); }
public class EParentDst { public List<EInnerDst> Items { get; set; } = new(); }
public class ENode { public ENode? Next { get; set; } }
