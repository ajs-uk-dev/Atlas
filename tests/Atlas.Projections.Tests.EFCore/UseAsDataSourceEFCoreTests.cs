using Atlas;
using Atlas.Projections;
using Atlas.Projections.Tests.EFCore.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Projections.Tests.EFCore;

/// <summary>
/// Verifies that UseAsDataSource produces correct results when the underlying
/// IQueryable is an EF Core query backed by SQLite :memory:.
/// </summary>
public class UseAsDataSourceEFCoreTests
{
    // Mapping: Post -> PostDto (flat, numeric widening int? -> long?).
    private static MapperConfiguration FlatCfg() =>
        new MapperConfiguration(c => c.CreateMap<Post, PostDto>());

    // Mapping: Blog -> BlogDto (with navigation Posts).
    private static MapperConfiguration BlogCfg() =>
        new MapperConfiguration(c =>
        {
            c.CreateMap<Post, PostDto>();
            c.CreateMap<Blog, BlogDto>();
        });

    // BlogContext seeded state:
    //   Blog  Id=1, Title="T1"
    //   Post  Id=1, Body="p1", WordCount=100,  BlogId=1
    //   Post  Id=2, Body="p2", WordCount=null, BlogId=1

    [Fact]
    public void Where_FlatPredicate_FiltersCorrectly()
    {
        // Id > 1 matches only Post 2 (Body="p2"). Uses Id (int, not widened) to avoid
        // the int?/long? type mismatch that would occur with WordCount predicates.
        var cfg = FlatCfg();
        using var ctx = BlogContext.CreateInMemory();
        ctx.Seed();

        var list = ctx.Posts
            .UseAsDataSource(cfg)
            .For<PostDto>()
            .Where(d => d.Id > 1)
            .ToList();

        Assert.Single(list);
        Assert.Equal("p2", list[0].Body);
    }

    [Fact]
    public void Where_ExactStringMatch_FiltersCorrectly()
    {
        // Body == "p2" matches only Post 2.
        var cfg = FlatCfg();
        using var ctx = BlogContext.CreateInMemory();
        ctx.Seed();

        var list = ctx.Posts
            .UseAsDataSource(cfg)
            .For<PostDto>()
            .Where(d => d.Body == "p2")
            .ToList();

        Assert.Single(list);
        Assert.Null(list[0].WordCount);
    }

    [Fact]
    public void OrderBy_ProducesOrderedResults()
    {
        // OrderBy Body descending: p2 before p1.
        var cfg = FlatCfg();
        using var ctx = BlogContext.CreateInMemory();
        ctx.Seed();

        var list = ctx.Posts
            .UseAsDataSource(cfg)
            .For<PostDto>()
            .OrderByDescending(d => d.Body)
            .ToList();

        Assert.Equal(2, list.Count);
        Assert.Equal("p2", list[0].Body);
        Assert.Equal("p1", list[1].Body);
    }

    [Fact]
    public void OrderBy_Ascending_ProducesOrderedResults()
    {
        // OrderBy Id ascending: Post 1 then Post 2.
        var cfg = FlatCfg();
        using var ctx = BlogContext.CreateInMemory();
        ctx.Seed();

        var list = ctx.Posts
            .UseAsDataSource(cfg)
            .For<PostDto>()
            .OrderBy(d => d.Id)
            .ToList();

        Assert.Equal(2, list.Count);
        Assert.Equal(1, list[0].Id);
        Assert.Equal(2, list[1].Id);
    }

    [Fact]
    public void SkipTake_PagesResults()
    {
        // With 2 posts ordered by Id, Skip(1).Take(1) returns only Post 2.
        var cfg = FlatCfg();
        using var ctx = BlogContext.CreateInMemory();
        ctx.Seed();

        var page = ctx.Posts
            .UseAsDataSource(cfg)
            .For<PostDto>()
            .OrderBy(d => d.Id)
            .Skip(1)
            .Take(1)
            .ToList();

        Assert.Single(page);
        Assert.Equal("p2", page[0].Body);
    }

    [Fact]
    public void Any_WithPredicate_DelegatesCorrectly()
    {
        // Id > 1 matches Post 2; Id > 1000 matches nothing.
        // Use Id (int, not widened) to avoid int?/long? binary-operator mismatch.
        var cfg = FlatCfg();
        using var ctx = BlogContext.CreateInMemory();
        ctx.Seed();

        var hasPost = ctx.Posts
            .UseAsDataSource(cfg)
            .For<PostDto>()
            .Any(d => d.Id > 1);

        var hasNoPost = ctx.Posts
            .UseAsDataSource(cfg)
            .For<PostDto>()
            .Any(d => d.Id > 1000);

        Assert.True(hasPost);
        Assert.False(hasNoPost);
    }

    [Fact]
    public void Count_WithPredicate_DelegatesCorrectly()
    {
        // Id > 1 matches only Post 2 (one row). Use Id (int, not widened).
        var cfg = FlatCfg();
        using var ctx = BlogContext.CreateInMemory();
        ctx.Seed();

        var count = ctx.Posts
            .UseAsDataSource(cfg)
            .For<PostDto>()
            .Count(d => d.Id > 1);

        Assert.Equal(1, count);
    }

    [Fact]
    public void Combined_WhereOrderBySkipTake_ProducesExpectedResults()
    {
        // Both posts qualify the predicate (Body != null/empty string).
        // OrderBy Id → [p1, p2]. Skip(0).Take(1) → [p1].
        var cfg = FlatCfg();
        using var ctx = BlogContext.CreateInMemory();
        ctx.Seed();

        var result = ctx.Posts
            .UseAsDataSource(cfg)
            .For<PostDto>()
            .Where(d => d.Id > 0)
            .OrderBy(d => d.Id)
            .Skip(0)
            .Take(1)
            .ToList();

        Assert.Single(result);
        Assert.Equal("p1", result[0].Body);
    }

    [Fact]
    public void First_WithPredicate_MaterializesViaProjectTo()
    {
        // First post with Body == "p1"; verify DTO is populated.
        // Uses string equality to avoid int?/long? widening mismatch.
        var cfg = FlatCfg();
        using var ctx = BlogContext.CreateInMemory();
        ctx.Seed();

        var dto = ctx.Posts
            .UseAsDataSource(cfg)
            .For<PostDto>()
            .First(d => d.Body == "p1");

        Assert.Equal("p1", dto.Body);
        Assert.Equal(100L, dto.WordCount);
    }

    [Fact]
    public void Count_NoPredicate_ReturnsAllRows()
    {
        var cfg = FlatCfg();
        using var ctx = BlogContext.CreateInMemory();
        ctx.Seed();

        var count = ctx.Posts
            .UseAsDataSource(cfg)
            .For<PostDto>()
            .Count();

        Assert.Equal(2, count);
    }

    [Fact]
    public void Enumeration_WithBlogMapping_ReturnsPopulatedDtos()
    {
        // Verify basic Blog -> BlogDto round-trip through wrapper.
        var cfg = BlogCfg();
        using var ctx = BlogContext.CreateInMemory();
        ctx.Seed();

        var list = ctx.Blogs
            .UseAsDataSource(cfg)
            .For<BlogDto>()
            .ToList();

        Assert.Single(list);
        Assert.Equal("T1", list[0].Title);
    }
}
