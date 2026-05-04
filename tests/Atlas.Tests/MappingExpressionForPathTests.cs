using Atlas.Configuration;
using Atlas.Internal;

namespace Atlas.Tests;

public class MappingExpressionForPathTests
{
    public sealed class Src { public string? Value { get; set; } }
    public sealed class Inner { public string? Name { get; set; } public int Count { get; set; } }
    public sealed class Outer { public Inner? Child { get; set; } public Mid? Middle { get; set; } public string? Top { get; set; } }
    public sealed class Mid { public Inner? Deep { get; set; } }

    private static MappingExpression<Src, Outer> NewExpr() =>
        new(new TypeMap(typeof(Src), typeof(Outer), MemberList.None));

    [Fact]
    public void ForPath_SingleLevel_EquivalentToForMember()
    {
        var expr = NewExpr();
        expr.ForPath(d => d.Top, opt => opt.MapFrom(s => s.Value));

        var pm = expr.TypeMap.PropertyMaps.Single(p => p.Name == "Top");
        Assert.NotNull(pm.SourcePath);
        Assert.Equal("Value", pm.SourcePath!.Members.Single().Name);
        Assert.Null(pm.DestinationPath);   // single-level uses ForProperty path, not ForPath path
        Assert.True(pm.IsExplicit);
    }

    [Fact]
    public void ForPath_TwoLevelChain_StoresFullPath()
    {
        var expr = NewExpr();
        expr.ForPath(d => d.Child!.Name, opt => opt.MapFrom(s => s.Value));

        var pm = expr.TypeMap.PropertyMaps.Single(p => p.Name == "Child.Name");
        Assert.NotNull(pm.DestinationPath);
        Assert.Collection(pm.DestinationPath!,
            p => Assert.Equal("Child", p.Name),
            p => Assert.Equal("Name", p.Name));
        Assert.True(pm.IsExplicit);
    }

    [Fact]
    public void ForPath_ThreeLevelChain_StoresFullPath()
    {
        var expr = NewExpr();
        expr.ForPath(d => d.Middle!.Deep!.Count, opt => opt.MapFrom(s => 5));

        var pm = expr.TypeMap.PropertyMaps.Single(p => p.Name == "Middle.Deep.Count");
        Assert.NotNull(pm.DestinationPath);
        Assert.Collection(pm.DestinationPath!,
            p => Assert.Equal("Middle", p.Name),
            p => Assert.Equal("Deep", p.Name),
            p => Assert.Equal("Count", p.Name));
    }

    [Fact]
    public void ForPath_MethodCallInChain_Throws()
    {
        var expr = NewExpr();
        var ex = Assert.Throws<ArgumentException>(() =>
            expr.ForPath(d => d.Top!.ToUpper(), opt => opt.MapFrom(s => s.Value)));
        Assert.Contains("chain of property accesses", ex.Message);
    }

    [Fact]
    public void ForPath_ArithmeticInChain_Throws()
    {
        var expr = NewExpr();
        var ex = Assert.Throws<ArgumentException>(() =>
            expr.ForPath(d => d.Child!.Count + 1, opt => opt.MapFrom(s => 7)));
        Assert.Contains("chain of property accesses", ex.Message);
    }

    [Fact]
    public void ForPath_LastCallWins_OnSamePath()
    {
        var expr = NewExpr();
        expr.ForPath(d => d.Child!.Name, opt => opt.MapFrom(s => "first"));
        expr.ForPath(d => d.Child!.Name, opt => opt.MapFrom(s => "second"));

        var matches = expr.TypeMap.PropertyMaps.Where(p => p.Name == "Child.Name").ToList();
        Assert.Single(matches);
        Assert.NotNull(matches[0].CustomExpression);
    }
}
