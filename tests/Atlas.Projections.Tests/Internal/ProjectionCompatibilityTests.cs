using Atlas.Internal;
using Atlas.Projections.Internal;

namespace Atlas.Projections.Tests.Internal;

public class ProjectionCompatibilityTests
{
    [Fact]
    public void IsTypeMapProjectable_NoCustomConverter_ReturnsTrue()
    {
        var tm = new TypeMap(typeof(string), typeof(string), MemberList.Destination);
        Assert.True(ProjectionCompatibility.IsTypeMapProjectable(tm, out var reason));
        Assert.Null(reason);
    }

    [Fact]
    public void IsTypeMapProjectable_CustomConverter_ReturnsFalseWithReason()
    {
        var tm = new TypeMap(typeof(string), typeof(int), MemberList.None)
        {
            CustomConverter = (Func<string, int>)(s => int.Parse(s)),
        };
        Assert.False(ProjectionCompatibility.IsTypeMapProjectable(tm, out var reason));
        Assert.NotNull(reason);
        Assert.Contains("ConvertUsing", reason);
    }

    [Fact]
    public void IsBindingProjectable_Constant_ReturnsTrue()
    {
        var pm = PropertyMap.ForProperty(typeof(Holder).GetProperty(nameof(Holder.Name))!);
        pm.HasConstant = true;
        pm.ConstantValue = "x";
        Assert.True(ProjectionCompatibility.IsBindingProjectable(pm, out var reason));
        Assert.Null(reason);
    }

    [Fact]
    public void IsBindingProjectable_CustomExpression_ReturnsTrue()
    {
        var pm = PropertyMap.ForProperty(typeof(Holder).GetProperty(nameof(Holder.Name))!);
        pm.CustomExpression = (System.Linq.Expressions.Expression<Func<Holder, string>>)(h => h.Name);
        Assert.True(ProjectionCompatibility.IsBindingProjectable(pm, out _));
    }

    [Fact]
    public void IsBindingProjectable_SourcePath_ReturnsTrue()
    {
        var pm = PropertyMap.ForProperty(typeof(Holder).GetProperty(nameof(Holder.Name))!);
        pm.SourcePath = new SourceMemberPath([typeof(Holder).GetProperty(nameof(Holder.Name))!]);
        Assert.True(ProjectionCompatibility.IsBindingProjectable(pm, out _));
    }

    [Fact]
    public void IsBindingProjectable_Ignored_ReturnsTrue()
    {
        // Ignore is fine — the validator skips ignored bindings entirely.
        var pm = PropertyMap.ForProperty(typeof(Holder).GetProperty(nameof(Holder.Name))!);
        pm.Ignored = true;
        Assert.True(ProjectionCompatibility.IsBindingProjectable(pm, out _));
    }

    private class Holder { public string Name { get; set; } = ""; }
}
