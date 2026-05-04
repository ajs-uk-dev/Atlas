namespace Atlas.Tests;

public class ConfigurationValidatorPathTests
{
    public sealed class Src { public string? Value { get; set; } }
    public sealed class GoodInner { public string? Name { get; set; } }
    public sealed class GoodOuter { public GoodInner? Child { get; set; } public string? Other { get; set; } }
    public sealed class CtorlessInner { public string? Name { get; set; } public CtorlessInner(string n) { Name = n; } }   // no parameterless ctor
    public sealed class CtorlessOuter { public CtorlessInner? Child { get; set; } }
    public sealed class GetterOnlyOuter { public GoodInner? Child { get; } = new(); }   // intermediate has no setter
    public sealed class LeafReadOnlyInner { public string? Name { get; } }
    public sealed class LeafReadOnlyOuter { public LeafReadOnlyInner? Child { get; set; } = new(); }

    [Fact]
    public void Validate_IntermediateMissingParameterlessCtor_Throws_NamingPath()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<Src, CtorlessOuter>(MemberList.None)
                .ForPath(d => d.Child!.Name, opt => opt.MapFrom(s => s.Value)));

        var ex = Assert.Throws<AtlasConfigurationException>(() => cfg.AssertConfigurationIsValid());
        Assert.Contains("CtorlessInner", ex.Message);
        Assert.Contains("parameterless constructor", ex.Message);
    }

    [Fact]
    public void Validate_IntermediateMissingSetter_Throws_NamingProperty()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<Src, GetterOnlyOuter>(MemberList.None)
                .ForPath(d => d.Child!.Name, opt => opt.MapFrom(s => s.Value)));

        var ex = Assert.Throws<AtlasConfigurationException>(() => cfg.AssertConfigurationIsValid());
        Assert.Contains("Child", ex.Message);
        Assert.Contains("setter", ex.Message);
    }

    [Fact]
    public void Validate_LeafMissingSetter_Throws()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<Src, LeafReadOnlyOuter>(MemberList.None)
                .ForPath(d => d.Child!.Name, opt => opt.MapFrom(s => s.Value)));

        var ex = Assert.Throws<AtlasConfigurationException>(() => cfg.AssertConfigurationIsValid());
        Assert.Contains("Name", ex.Message);
        Assert.Contains("setter", ex.Message);
    }

    [Fact]
    public void Validate_AllValid_ReturnsCleanly()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<Src, GoodOuter>(MemberList.None)
                .ForPath(d => d.Child!.Name, opt => opt.MapFrom(s => s.Value)));

        cfg.AssertConfigurationIsValid();   // does not throw
    }

    [Fact]
    public void Validate_DestinationPathCountsAsCoveringTopIntermediate_ForMemberListDestination()
    {
        // Reverse-style scenario without using ReverseMap (which lands in Task 6).
        // GoodOuter has { Child, Other }. We map Child.Name (covers Child) and leave Other unmapped.
        // With MemberList.Destination, ONLY Other should be reported as unmapped — Child is "covered"
        // because path[0] == Child.
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<Src, GoodOuter>(MemberList.Destination)
                .ForPath(d => d.Child!.Name, opt => opt.MapFrom(s => s.Value)));

        var ex = Assert.Throws<AtlasConfigurationException>(() => cfg.AssertConfigurationIsValid());
        Assert.Contains("Other", ex.Message);
        Assert.DoesNotContain("Child", ex.Message);   // path[0] coverage suppresses the "Child unmapped" complaint
    }
}
