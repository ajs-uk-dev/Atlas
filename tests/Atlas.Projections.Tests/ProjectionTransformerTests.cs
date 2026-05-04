namespace Atlas.Projections.Tests;

public class ProjectionTransformerTests
{
    public class S { public string? Name { get; set; } public int Count { get; set; } }
    public class D { public string? Name { get; set; } public int Count { get; set; } }

    [Fact]
    public void ProjectTo_TranslatableTransformer_AppliesInProjection()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap<S, D>(MemberList.None)
                .AddTransform<string>(s => s == null ? null! : s.Trim());
        });
        var srcs = new List<S>
        {
            new() { Name = "  hello  ", Count = 1 },
            new() { Name = "  world  ", Count = 2 },
        }.AsQueryable();

        var dsts = srcs.ProjectTo<D>(cfg).ToList();

        Assert.Equal(2, dsts.Count);
        Assert.Equal("hello", dsts[0].Name);
        Assert.Equal("world", dsts[1].Name);
    }

    [Fact]
    public void ProjectTo_TwoComposedTransformers_BothInlined()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap<S, D>(MemberList.None)
                .AddTransform<string>(s => s == null ? null! : s.Trim())
                .AddTransform<string>(s => s == null ? null! : s.ToUpperInvariant());
        });
        var srcs = new List<S> { new() { Name = "  hello  " } }.AsQueryable();

        var dsts = srcs.ProjectTo<D>(cfg).ToList();

        Assert.Single(dsts);
        Assert.Equal("HELLO", dsts[0].Name);
    }

    [Fact]
    public void ProjectTo_GlobalTransformer_AppliesInProjection()
    {
        // Verify the entire scope chain (global) flows into projection bindings.
        var cfg = new MapperConfiguration(c =>
        {
            c.ValueTransformers.Add<string>(s => s == null ? null! : s.Trim());
            c.CreateMap<S, D>(MemberList.None);
        });
        var srcs = new List<S> { new() { Name = "  hello  " } }.AsQueryable();

        var dsts = srcs.ProjectTo<D>(cfg).ToList();

        Assert.Single(dsts);
        Assert.Equal("hello", dsts[0].Name);
    }
}
