using Atlas.Configuration;
using Atlas.Internal;

namespace Atlas.Tests;

public class ExecutionPlanBuilderNestedAssignTests
{
    public sealed class Src { public string? Value { get; set; } public int Count { get; set; } }
    public sealed class Inner { public string? Name { get; set; } public int Tally { get; set; } }
    public sealed class Mid { public Inner? Deep { get; set; } }
    public sealed class Outer { public Inner? Child { get; set; } public Mid? Middle { get; set; } public string? Top { get; set; } }

    private static MapperConfiguration BuildConfig(Action<IMappingExpression<Src, Outer>> configure) =>
        new(cfg =>
        {
            var expr = cfg.CreateMap<Src, Outer>(MemberList.None);
            configure(expr);
        });

    [Fact]
    public void NestedAssign_SingleLevel_NoCoalesceEmitted()
    {
        // Single-level path uses ForProperty, no DestinationPath, no coalesce.
        var cfg = BuildConfig(expr => expr.ForPath(d => d.Top, opt => opt.MapFrom(s => s.Value)));
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<Outer>(new Src { Value = "hi" });

        Assert.Equal("hi", dst.Top);
        Assert.Null(dst.Child);
    }

    [Fact]
    public void NestedAssign_TwoLevel_EmitsCoalesceThenAssign()
    {
        var cfg = BuildConfig(expr => expr.ForPath(d => d.Child!.Name, opt => opt.MapFrom(s => s.Value)));
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<Outer>(new Src { Value = "alice" });

        Assert.NotNull(dst.Child);
        Assert.Equal("alice", dst.Child!.Name);
    }

    [Fact]
    public void NestedAssign_ThreeLevel_EmitsTwoCoalescesThenAssign()
    {
        var cfg = BuildConfig(expr => expr.ForPath(d => d.Middle!.Deep!.Tally, opt => opt.MapFrom(s => s.Count)));
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<Outer>(new Src { Count = 7 });

        Assert.NotNull(dst.Middle);
        Assert.NotNull(dst.Middle!.Deep);
        Assert.Equal(7, dst.Middle.Deep!.Tally);
    }

    [Fact]
    public void NestedAssign_TwoBindingsSharingPrefix_BothPopulate()
    {
        // Probes the design's "second `??=` is a no-op" claim — both Name and Tally end up set.
        var cfg = BuildConfig(expr =>
        {
            expr.ForPath(d => d.Child!.Name, opt => opt.MapFrom(s => s.Value));
            expr.ForPath(d => d.Child!.Tally, opt => opt.MapFrom(s => s.Count));
        });
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<Outer>(new Src { Value = "bob", Count = 42 });

        Assert.NotNull(dst.Child);
        Assert.Equal("bob", dst.Child!.Name);
        Assert.Equal(42, dst.Child.Tally);
    }
}
