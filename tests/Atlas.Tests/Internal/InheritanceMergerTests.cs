using System.Linq.Expressions;
using System.Reflection;
using Atlas.Internal;

namespace Atlas.Tests.Internal;

public class InheritanceMergerTests
{
    private static TypeMap MapFor(Type src, Type dst, MemberList memberList = MemberList.None) =>
        new(src, dst, memberList);

    private static PropertyInfo Prop<T>(string name) => typeof(T).GetProperty(name)!;

    [Fact]
    public void Merge_BaseHasExplicitMapFrom_DerivedInheritsIt()
    {
        var baseTm = MapFor(typeof(BaseSrc), typeof(BaseDst));
        var basePm = PropertyMap.ForProperty(Prop<BaseDst>(nameof(BaseDst.Name)));
        basePm.SourcePath = new SourceMemberPath([Prop<BaseSrc>(nameof(BaseSrc.Title))]);
        basePm.IsExplicit = true;
        baseTm.PropertyMaps.Add(basePm);

        var derivedTm = MapFor(typeof(DerivedSrc), typeof(DerivedDst));

        InheritanceMerger.MergeBaseConfig(baseTm, derivedTm);

        var inherited = derivedTm.PropertyMaps.Single(p => p.Name == nameof(BaseDst.Name));
        Assert.NotNull(inherited.SourcePath);
        Assert.Equal(nameof(BaseSrc.Title), inherited.SourcePath!.Members[0].Name);
        Assert.True(inherited.IsExplicit);
    }

    [Fact]
    public void Merge_DerivedHasExplicitMapFrom_BaseDoesNotOverride()
    {
        var baseTm = MapFor(typeof(BaseSrc), typeof(BaseDst));
        var basePm = PropertyMap.ForProperty(Prop<BaseDst>(nameof(BaseDst.Name)));
        basePm.SourcePath = new SourceMemberPath([Prop<BaseSrc>(nameof(BaseSrc.Title))]);
        basePm.IsExplicit = true;
        baseTm.PropertyMaps.Add(basePm);

        var derivedTm = MapFor(typeof(DerivedSrc), typeof(DerivedDst));
        var derivedPm = PropertyMap.ForProperty(Prop<DerivedDst>(nameof(DerivedDst.Name)));
        derivedPm.SourcePath = new SourceMemberPath([Prop<DerivedSrc>(nameof(DerivedSrc.OtherField))]);
        derivedPm.IsExplicit = true;
        derivedTm.PropertyMaps.Add(derivedPm);

        InheritanceMerger.MergeBaseConfig(baseTm, derivedTm);

        var kept = derivedTm.PropertyMaps.Single(p => p.Name == nameof(BaseDst.Name));
        Assert.Equal(nameof(DerivedSrc.OtherField), kept.SourcePath!.Members[0].Name);
    }

    [Fact]
    public void Merge_BaseHasIgnore_DerivedConventionPathIsOverridden()
    {
        // Load-bearing precedence test: base Ignore beats derived convention.
        var baseTm = MapFor(typeof(BaseSrc), typeof(BaseDst));
        var basePm = PropertyMap.ForProperty(Prop<BaseDst>(nameof(BaseDst.Name)));
        basePm.Ignored = true;
        basePm.IsExplicit = true;
        baseTm.PropertyMaps.Add(basePm);

        var derivedTm = MapFor(typeof(DerivedSrc), typeof(DerivedDst));
        var derivedConvPm = PropertyMap.ForProperty(Prop<DerivedDst>(nameof(DerivedDst.Name)));
        derivedConvPm.SourcePath = new SourceMemberPath([Prop<DerivedSrc>(nameof(DerivedSrc.Name))]);
        derivedConvPm.IsExplicit = false; // convention-resolved
        derivedTm.PropertyMaps.Add(derivedConvPm);

        InheritanceMerger.MergeBaseConfig(baseTm, derivedTm);

        var merged = derivedTm.PropertyMaps.Single(p => p.Name == nameof(BaseDst.Name));
        Assert.True(merged.Ignored);
        Assert.True(merged.IsExplicit);
    }

    [Fact]
    public void Merge_DerivedExplicitlyIgnores_BaseMapFromIsIgnored()
    {
        var baseTm = MapFor(typeof(BaseSrc), typeof(BaseDst));
        var basePm = PropertyMap.ForProperty(Prop<BaseDst>(nameof(BaseDst.Name)));
        basePm.SourcePath = new SourceMemberPath([Prop<BaseSrc>(nameof(BaseSrc.Title))]);
        basePm.IsExplicit = true;
        baseTm.PropertyMaps.Add(basePm);

        var derivedTm = MapFor(typeof(DerivedSrc), typeof(DerivedDst));
        var derivedPm = PropertyMap.ForProperty(Prop<DerivedDst>(nameof(DerivedDst.Name)));
        derivedPm.Ignored = true;
        derivedPm.IsExplicit = true;
        derivedTm.PropertyMaps.Add(derivedPm);

        InheritanceMerger.MergeBaseConfig(baseTm, derivedTm);

        var kept = derivedTm.PropertyMaps.Single(p => p.Name == nameof(BaseDst.Name));
        Assert.True(kept.Ignored);
        Assert.Null(kept.SourcePath);
    }

    [Fact]
    public void Merge_BaseMemberAbsentFromDerivedDestination_NotCopied()
    {
        // BaseDst has Name; DerivedOnlyDst doesn't. Don't copy a binding for a property that
        // doesn't exist on the derived destination.
        var baseTm = MapFor(typeof(BaseSrc), typeof(BaseDst));
        var basePm = PropertyMap.ForProperty(Prop<BaseDst>(nameof(BaseDst.Name)));
        basePm.SourcePath = new SourceMemberPath([Prop<BaseSrc>(nameof(BaseSrc.Title))]);
        basePm.IsExplicit = true;
        baseTm.PropertyMaps.Add(basePm);

        var derivedTm = MapFor(typeof(DerivedSrc), typeof(DerivedOnlyDst));

        InheritanceMerger.MergeBaseConfig(baseTm, derivedTm);

        Assert.Empty(derivedTm.PropertyMaps);
    }

    [Fact]
    public void Merge_DerivedHasOnlyConvention_BaseMapFromOverwrites()
    {
        // Derived has a convention-resolved binding (IsExplicit=false). Base's explicit
        // MapFrom wins — overwrite in place.
        var baseTm = MapFor(typeof(BaseSrc), typeof(BaseDst));
        var basePm = PropertyMap.ForProperty(Prop<BaseDst>(nameof(BaseDst.Name)));
        basePm.SourcePath = new SourceMemberPath([Prop<BaseSrc>(nameof(BaseSrc.Title))]);
        basePm.IsExplicit = true;
        baseTm.PropertyMaps.Add(basePm);

        var derivedTm = MapFor(typeof(DerivedSrc), typeof(DerivedDst));
        var derivedConvPm = PropertyMap.ForProperty(Prop<DerivedDst>(nameof(DerivedDst.Name)));
        derivedConvPm.SourcePath = new SourceMemberPath([Prop<DerivedSrc>(nameof(DerivedSrc.Name))]);
        derivedConvPm.IsExplicit = false;
        derivedTm.PropertyMaps.Add(derivedConvPm);

        InheritanceMerger.MergeBaseConfig(baseTm, derivedTm);

        var merged = derivedTm.PropertyMaps.Single(p => p.Name == nameof(BaseDst.Name));
        // Base path won.
        Assert.Equal(nameof(BaseSrc.Title), merged.SourcePath!.Members[0].Name);
        Assert.True(merged.IsExplicit);
    }

    [Fact]
    public void Merge_BaseAndDerivedBothExplicit_DerivedWins()
    {
        var baseTm = MapFor(typeof(BaseSrc), typeof(BaseDst));
        var basePm = PropertyMap.ForProperty(Prop<BaseDst>(nameof(BaseDst.Name)));
        basePm.SourcePath = new SourceMemberPath([Prop<BaseSrc>(nameof(BaseSrc.Title))]);
        basePm.IsExplicit = true;
        baseTm.PropertyMaps.Add(basePm);

        var derivedTm = MapFor(typeof(DerivedSrc), typeof(DerivedDst));
        var derivedPm = PropertyMap.ForProperty(Prop<DerivedDst>(nameof(DerivedDst.Name)));
        derivedPm.SourcePath = new SourceMemberPath([Prop<DerivedSrc>(nameof(DerivedSrc.OtherField))]);
        derivedPm.IsExplicit = true;
        derivedTm.PropertyMaps.Add(derivedPm);

        InheritanceMerger.MergeBaseConfig(baseTm, derivedTm);

        var merged = derivedTm.PropertyMaps.Single(p => p.Name == nameof(BaseDst.Name));
        Assert.Equal(nameof(DerivedSrc.OtherField), merged.SourcePath!.Members[0].Name);
    }

    [Fact]
    public void Merge_NoBaseConfig_DerivedConventionPreserved()
    {
        // Base has only convention-resolved (IsExplicit=false) bindings. Don't propagate.
        var baseTm = MapFor(typeof(BaseSrc), typeof(BaseDst));
        var baseConvPm = PropertyMap.ForProperty(Prop<BaseDst>(nameof(BaseDst.Name)));
        baseConvPm.SourcePath = new SourceMemberPath([Prop<BaseSrc>(nameof(BaseSrc.Title))]);
        baseConvPm.IsExplicit = false;
        baseTm.PropertyMaps.Add(baseConvPm);

        var derivedTm = MapFor(typeof(DerivedSrc), typeof(DerivedDst));
        var derivedConvPm = PropertyMap.ForProperty(Prop<DerivedDst>(nameof(DerivedDst.Name)));
        derivedConvPm.SourcePath = new SourceMemberPath([Prop<DerivedSrc>(nameof(DerivedSrc.Name))]);
        derivedConvPm.IsExplicit = false;
        derivedTm.PropertyMaps.Add(derivedConvPm);

        InheritanceMerger.MergeBaseConfig(baseTm, derivedTm);

        var kept = derivedTm.PropertyMaps.Single(p => p.Name == nameof(BaseDst.Name));
        Assert.Equal(nameof(DerivedSrc.Name), kept.SourcePath!.Members[0].Name);
        Assert.False(kept.IsExplicit);
    }
}

// ---- Test fixtures ----
public class BaseSrc { public string Title { get; set; } = ""; }
public class DerivedSrc : BaseSrc { public string Name { get; set; } = ""; public string OtherField { get; set; } = ""; }

public class BaseDst { public string Name { get; set; } = ""; }
public class DerivedDst : BaseDst { }

public class DerivedOnlyDst { public string OtherProperty { get; set; } = ""; }
