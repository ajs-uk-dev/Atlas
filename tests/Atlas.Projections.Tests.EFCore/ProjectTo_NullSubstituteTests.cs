using Atlas;
using Atlas.Configuration;
using Atlas.Projections;
using Atlas.Projections.Tests.EFCore.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Projections.Tests.EFCore;

public class ProjectTo_NullSubstituteTests
{
    [Fact]
    public void ProjectTo_NullSubstitute_GeneratesCoalesceSql()
    {
        // Map Post.WordCount (int?) → PostDto.WordCount (long?), substituting -1 for null.
        var config = new MapperConfiguration(c =>
            c.CreateMap<Post, PostDto>(MemberList.None)
                .ForMember(d => d.WordCount, opt =>
                {
                    opt.MapFrom(s => s.WordCount);
                    opt.NullSubstitute(-1);
                })
                .ForMember(d => d.Body, opt => opt.MapFrom(s => s.Body))
                .ForMember(d => d.Id, opt => opt.MapFrom(s => s.Id)));
        using var ctx = BlogContext.CreateInMemory();
        ctx.Seed();

        var sql = ctx.Posts.ProjectTo<PostDto>(config).ToQueryString();

        Assert.Contains("COALESCE", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProjectTo_NullSubstitute_RowReturnsSubstitutedValue()
    {
        // Post 1 has WordCount = 100 → projected as 100.
        // Post 2 has WordCount = null → projected as -1 (substitute).
        var config = new MapperConfiguration(c =>
            c.CreateMap<Post, PostDto>(MemberList.None)
                .ForMember(d => d.WordCount, opt =>
                {
                    opt.MapFrom(s => s.WordCount);
                    opt.NullSubstitute(-1);
                })
                .ForMember(d => d.Body, opt => opt.MapFrom(s => s.Body))
                .ForMember(d => d.Id, opt => opt.MapFrom(s => s.Id)));
        using var ctx = BlogContext.CreateInMemory();
        ctx.Seed();

        var posts = ctx.Posts.OrderBy(p => p.Id).ProjectTo<PostDto>(config).ToList();

        Assert.Equal(2, posts.Count);
        Assert.Equal(100L, posts[0].WordCount);    // real value passes through
        Assert.Equal(-1L, posts[1].WordCount);     // null source → substitute -1 (widened to long)
    }
}
