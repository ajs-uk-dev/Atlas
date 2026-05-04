using Atlas;
using Atlas.Internal;
using Atlas.Projections;
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

    [Fact]
    public void IsBindingProjectable_DestinationPathBinding_ReturnsFalseWithReason()
    {
        // ForPath(d => d.Inner.Value) produces a PM with DestinationPath.Count > 1.
        // Projection cannot emit member-init writes into nested chains — must be rejected.
        var path = new System.Reflection.PropertyInfo[]
        {
            typeof(ProjNestedDst).GetProperty(nameof(ProjNestedDst.Inner))!,
            typeof(InnerHolder).GetProperty(nameof(InnerHolder.Value))!,
        };
        var pm = PropertyMap.ForPath(path);
        Assert.False(ProjectionCompatibility.IsBindingProjectable(pm, out var reason));
        Assert.NotNull(reason);
        Assert.Contains("DestinationPath", reason);
        Assert.Contains("ForPath", reason);
    }

    [Fact]
    public void ProjectTo_ForwardMapWithForPath_IsRejectedWithDiagnostic()
    {
        // A forward map that uses ForPath(d => d.Inner.Value, ...) should be rejected by ProjectTo.
        var config = new MapperConfiguration(c =>
            c.CreateMap<ProjFlatSrc, ProjNestedDst>()
                .ForPath(d => d.Inner!.Value, opt => opt.MapFrom(s => s.Name)));

        var src = new[] { new ProjFlatSrc { Name = "test" } };
        var ex = Assert.Throws<AtlasProjectionException>(
            () => src.AsQueryable().ProjectTo<ProjNestedDst>(config));

        Assert.Contains(ex.Diagnostics, d =>
            d.Member == "Inner.Value" &&
            d.Reason != null &&
            d.Reason.Contains("DestinationPath"));
    }

    [Fact]
    public void ProjectTo_ReverseMapWithMirroredUnflattenPaths_IsRejectedWithDiagnostic()
    {
        // Forward: ProjNestedSrc.Inner.Value -> ProjFlatDst.InnerValue (convention flattening,
        // multi-level SourcePath). ReverseMap() mirrors this: the reverse PM for InnerValue
        // becomes DestinationPath=[Inner, Value] on the reverse map ProjFlatDst -> ProjNestedSrc.
        // ProjectTo on that reverse direction must reject the mirrored DestinationPath binding.
        var config = new MapperConfiguration(c =>
            c.CreateMap<ProjNestedSrc, ProjFlatDst>().ReverseMap());

        var reverseSrc = new[] { new ProjFlatDst { InnerValue = "test" } };
        var ex = Assert.Throws<AtlasProjectionException>(
            () => reverseSrc.AsQueryable().ProjectTo<ProjNestedSrc>(config));

        Assert.Contains(ex.Diagnostics, d =>
            d.Reason != null &&
            d.Reason.Contains("DestinationPath"));
    }

    private class Holder { public string Name { get; set; } = ""; }
    private class InnerHolder { public string Value { get; set; } = ""; }
    private class ProjFlatSrc { public string Name { get; set; } = ""; }
    private class ProjNestedDst { public InnerHolder? Inner { get; set; } }
    private class ProjInnerSrc { public string Value { get; set; } = ""; }
    private class ProjNestedSrc { public ProjInnerSrc? Inner { get; set; } }
    private class ProjFlatDst { public string InnerValue { get; set; } = ""; }
}
