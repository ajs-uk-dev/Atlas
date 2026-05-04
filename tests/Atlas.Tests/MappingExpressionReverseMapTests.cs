using Atlas.Configuration;
using Atlas.Internal;

namespace Atlas.Tests;

public class MappingExpressionReverseMapTests
{
    public sealed class A { public string? Foo { get; set; } }
    public sealed class B { public string? Foo { get; set; } }

    [Fact]
    public void ReverseMap_ReturnsExpression_OfReverseGenericArgs()
    {
        var cfg = new MapperConfigurationExpression();
        var fwd = cfg.CreateMap<A, B>();
        var rev = fwd.ReverseMap();

        Assert.IsAssignableFrom<IMappingExpression<B, A>>(rev);
    }

    [Fact]
    public void ReverseMap_DefaultMemberListIsNone()
    {
        var cfg = new MapperConfigurationExpression();
        cfg.CreateMap<A, B>().ReverseMap();

        var revTm = cfg.GetTypeMaps().Single(t => t.SourceType == typeof(B));
        Assert.Equal(MemberList.None, revTm.MemberList);
    }

    [Fact]
    public void ReverseMap_ExplicitMemberListHonoured()
    {
        var cfg = new MapperConfigurationExpression();
        cfg.CreateMap<A, B>().ReverseMap(MemberList.Destination);

        var revTm = cfg.GetTypeMaps().Single(t => t.SourceType == typeof(B));
        Assert.Equal(MemberList.Destination, revTm.MemberList);
    }

    [Fact]
    public void ReverseMap_CalledTwice_ReturnsSameInstance()
    {
        var cfg = new MapperConfigurationExpression();
        var fwd = cfg.CreateMap<A, B>();

        var rev1 = fwd.ReverseMap();
        var rev2 = fwd.ReverseMap();

        Assert.Same(rev1, rev2);
    }

    [Fact]
    public void ReverseMap_TwoCallsWithDifferentMemberList_Throws()
    {
        var cfg = new MapperConfigurationExpression();
        var fwd = cfg.CreateMap<A, B>();
        fwd.ReverseMap();   // default None

        var ex = Assert.Throws<AtlasConfigurationException>(() => fwd.ReverseMap(MemberList.Destination));
        Assert.Contains("None", ex.Message);
        Assert.Contains("Destination", ex.Message);
    }

    [Fact]
    public void ReverseMap_RegistersTypeMap_AndChainsForMember()
    {
        var cfg = new MapperConfigurationExpression();
        cfg.CreateMap<A, B>()
           .ReverseMap()
           .ForMember(d => d.Foo, opt => opt.Ignore());

        var revTm = cfg.GetTypeMaps().Single(t => t.SourceType == typeof(B));
        var pm = revTm.PropertyMaps.Single(p => p.Name == "Foo");
        Assert.True(pm.Ignored);
        Assert.True(pm.IsExplicit);
    }
}
