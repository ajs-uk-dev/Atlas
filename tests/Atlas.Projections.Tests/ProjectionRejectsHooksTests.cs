namespace Atlas.Projections.Tests;

public class ProjectionRejectsHooksTests
{
    public class S { public int X { get; set; } }
    public class D { public int X { get; set; } }

    [Fact]
    public void ProjectTo_ForwardMapWithBeforeMap_ThrowsNamingHookCount()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .BeforeMap((s, d) => { }));
        var srcs = new List<S> { new() { X = 1 } }.AsQueryable();

        var ex = Assert.Throws<AtlasProjectionException>(() => srcs.ProjectTo<D>(cfg).ToList());
        Assert.Contains("BeforeMap", ex.Message);
        Assert.Contains("1", ex.Message);   // hook count
    }

    [Fact]
    public void ProjectTo_ForwardMapWithAfterMap_ThrowsNamingHookCount()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .AfterMap((s, d) => { })
                .AfterMap((s, d) => { }));
        var srcs = new List<S> { new() { X = 1 } }.AsQueryable();

        var ex = Assert.Throws<AtlasProjectionException>(() => srcs.ProjectTo<D>(cfg).ToList());
        Assert.Contains("AfterMap", ex.Message);
        Assert.Contains("2", ex.Message);   // hook count
    }
}
