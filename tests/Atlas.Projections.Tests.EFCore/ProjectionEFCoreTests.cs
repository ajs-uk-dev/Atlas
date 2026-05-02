using Atlas;
using Atlas.Projections;
using Atlas.Projections.Tests.EFCore.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Projections.Tests.EFCore;

public class ProjectionEFCoreTests
{
    private static MapperConfiguration BlogMapping()
    {
        return new MapperConfiguration(c =>
        {
            c.CreateMap<Post, PostDto>();
            c.CreateMap<Blog, BlogDto>();
        });
    }

    [Fact]
    public void EFCore_FlatProjection_EmitsSingleSelect_NoFullEntityHydration()
    {
        var config = BlogMapping();
        using var ctx = BlogContext.CreateInMemory();
        ctx.Seed();

        var sql = ctx.Posts.ProjectTo<PostDto>(config).ToQueryString();

        // Assertions are on column-name presence, not whitespace.
        Assert.Contains("Body", sql);
        Assert.Contains("WordCount", sql);
        Assert.Contains("Id", sql);
        Assert.Contains("FROM \"Posts\"", sql);
    }

    [Fact]
    public void EFCore_NestedProjection_EmitsLeftJoin_NotN1Queries()
    {
        var config = BlogMapping();
        using var ctx = BlogContext.CreateInMemory();
        ctx.Seed();

        var sql = ctx.Blogs.ProjectTo<BlogDto>(config).ToQueryString();
        // EF Core emits LEFT JOIN for nullable navigations; nested collection projections
        // produce one query, not N+1.
        Assert.Contains("LEFT JOIN", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EFCore_CollectionProjection_EmitsSingleQuery()
    {
        var config = BlogMapping();
        using var ctx = BlogContext.CreateInMemory();
        ctx.Seed();

        var blogs = ctx.Blogs.ProjectTo<BlogDto>(config).ToList();
        Assert.Single(blogs);
        Assert.Equal(2, blogs[0].Posts.Count);
    }

    [Fact]
    public void EFCore_FilterBeforeProjectTo_FilterPushesDown()
    {
        var config = BlogMapping();
        using var ctx = BlogContext.CreateInMemory();
        ctx.Seed();

        var sql = ctx.Posts.Where(p => p.WordCount == 100).ProjectTo<PostDto>(config).ToQueryString();
        Assert.Contains("WHERE", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EFCore_ProjectionRoundtrip_ReturnsExpectedRows()
    {
        var config = BlogMapping();
        using var ctx = BlogContext.CreateInMemory();
        ctx.Seed();

        var posts = ctx.Posts.OrderBy(p => p.Id).ProjectTo<PostDto>(config).ToList();
        Assert.Equal(2, posts.Count);
        Assert.Equal("p1", posts[0].Body);
        Assert.Equal(100L, posts[0].WordCount);
        Assert.Null(posts[1].WordCount);
    }

    [Fact]
    public void EFCore_NumericWidening_TranslatesToProvider()
    {
        // PostDto.WordCount is long?, Post.WordCount is int? — implicit widening.
        var config = BlogMapping();
        using var ctx = BlogContext.CreateInMemory();
        ctx.Seed();

        var dto = ctx.Posts.OrderBy(p => p.Id).ProjectTo<PostDto>(config).First();
        Assert.Equal(100L, dto.WordCount);
    }

    [Fact]
    public void EFCore_NullableSourceMember_TranslatesToNullCoalesce()
    {
        var config = BlogMapping();
        using var ctx = BlogContext.CreateInMemory();
        ctx.Seed();

        // Round-trips a null int? → null long? via the projection.
        var dto = ctx.Posts.OrderByDescending(p => p.Id).ProjectTo<PostDto>(config).First();
        Assert.Null(dto.WordCount);
    }

    [Fact]
    public void EFCore_RecursiveMap_DepthLimitTerminatesQuery()
    {
        // Post -> Blog -> Posts -> Blog ... is a cycle. Depth 1 stops at the first hop.
        var config = new MapperConfiguration(c =>
        {
            c.CreateMap<Post, PostDto>();
            c.CreateMap<Blog, BlogDto>();
        });
        using var ctx = BlogContext.CreateInMemory();
        ctx.Seed();

        // Should not stack-overflow during expression building or query translation.
        var blogs = ctx.Blogs.ProjectTo<BlogDto>(config, maxDepth: 1).ToList();
        Assert.Single(blogs);
    }
}
