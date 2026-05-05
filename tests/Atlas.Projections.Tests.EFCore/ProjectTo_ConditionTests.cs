using Atlas;
using Atlas.Configuration;
using Atlas.Projections;
using Atlas.Projections.Tests.EFCore.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Projections.Tests.EFCore;

public class ProjectTo_ConditionTests
{
    [Fact]
    public void ProjectTo_PredicateGeneratesCaseWhen()
    {
        var config = new MapperConfiguration(c =>
            c.CreateMap<Post, PostDto>(MemberList.None)
                .ForMember(d => d.Body, opt =>
                {
                    opt.PreCondition(s => s.WordCount > 0);
                    opt.MapFrom(s => s.Body);
                }));
        using var ctx = BlogContext.CreateInMemory();
        ctx.Seed();

        var sql = ctx.Posts.ProjectTo<PostDto>(config).ToQueryString();

        Assert.Contains("CASE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WHEN", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProjectTo_PredicateFalse_RowReturnsDefaultForGatedColumn()
    {
        // Use s.Id == 2 as the cleanest predicate separator: Post 1 fails, Post 2 passes.
        // (Seeded posts: Post 1 Id=1 WordCount=100 Body="p1"; Post 2 Id=2 WordCount=null Body="p2".)
        var config = new MapperConfiguration(c =>
            c.CreateMap<Post, PostDto>(MemberList.None)
                .ForMember(d => d.Body, opt =>
                {
                    opt.PreCondition(s => s.Id == 2);
                    opt.MapFrom(s => s.Body);
                })
                .ForMember(d => d.WordCount, opt => opt.MapFrom(s => s.WordCount))
                .ForMember(d => d.Id, opt => opt.MapFrom(s => s.Id)));
        using var ctx = BlogContext.CreateInMemory();
        ctx.Seed();

        var posts = ctx.Posts.OrderBy(p => p.Id).ProjectTo<PostDto>(config).ToList();

        Assert.Equal(2, posts.Count);
        // Post 1: Id=1, predicate Id==2 false → Body comes back as null/default(string).
        // For PostDto.Body which has a "" default value, projection materialization
        // assigns default(string) (= null) when the predicate is false because the
        // projection construction uses MemberInit, which calls the parameterless ctor
        // and then sets each binding — the binding's else-branch is Default(string)=null.
        Assert.Null(posts[0].Body);
        // Post 2: Id=2, predicate Id==2 true → Body is "p2".
        Assert.Equal("p2", posts[1].Body);
    }
}
