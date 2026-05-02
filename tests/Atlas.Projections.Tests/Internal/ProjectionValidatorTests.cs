using Atlas;
using Atlas.Internal;
using Atlas.Projections.Internal;

namespace Atlas.Projections.Tests.Internal;

public class ProjectionValidatorTests
{
    private static MapperRegistry RegistryFor(Action<MapperConfigurationExpression> configure)
    {
        var config = new MapperConfiguration(configure);
        return config.Internal_Registry;
    }

    [Fact]
    public void Validate_FullyMappedSimplePair_ReturnsSilently()
    {
        var registry = RegistryFor(c => c.CreateMap<VFlatSrc, VFlatDst>());
        ProjectionValidator.Validate(registry, new TypePair(typeof(VFlatSrc), typeof(VFlatDst)), maxDepth: 3);
    }

    [Fact]
    public void Validate_NoMapRegistered_ReportsRootMissing()
    {
        var registry = RegistryFor(c => { });
        var ex = Assert.Throws<AtlasProjectionException>(() =>
            ProjectionValidator.Validate(registry, new TypePair(typeof(VFlatSrc), typeof(VFlatDst)), maxDepth: 3));
        Assert.Single(ex.Diagnostics);
        Assert.Equal("(no map registered)", ex.Diagnostics[0].Member);
    }

    [Fact]
    public void Validate_CustomConverter_ReportsWholeMapIncompatible()
    {
        var registry = RegistryFor(c =>
            c.CreateMap<VFlatSrc, VFlatDst>().ConvertUsing(s => new VFlatDst { Id = s.Id, Name = s.Name }));
        var ex = Assert.Throws<AtlasProjectionException>(() =>
            ProjectionValidator.Validate(registry, new TypePair(typeof(VFlatSrc), typeof(VFlatDst)), maxDepth: 3));
        Assert.Equal("(whole map)", ex.Diagnostics[0].Member);
    }

    [Fact]
    public void Validate_NestedCustomConverter_ReportsNestedMap_NotRoot()
    {
        var registry = RegistryFor(c =>
        {
            c.CreateMap<VOuterSrc, VOuterDst>();
            c.CreateMap<VInnerSrc, VInnerDst>().ConvertUsing(s => new VInnerDst { Name = s.Name });
        });
        var ex = Assert.Throws<AtlasProjectionException>(() =>
            ProjectionValidator.Validate(registry, new TypePair(typeof(VOuterSrc), typeof(VOuterDst)), maxDepth: 3));
        // The diagnostic should be on the nested pair, not on VOuterSrc -> VOuterDst.
        Assert.Equal(typeof(VInnerSrc), ex.Diagnostics[0].SourceType);
        Assert.Equal(typeof(VInnerDst), ex.Diagnostics[0].DestinationType);
    }

    [Fact]
    public void Validate_UnresolvedDestinationMember_ReportsMember()
    {
        // VExtraDst has a member with no source counterpart and no Ignore.
        var registry = RegistryFor(c => c.CreateMap<VFlatSrc, VExtraDst>());
        var ex = Assert.Throws<AtlasProjectionException>(() =>
            ProjectionValidator.Validate(registry, new TypePair(typeof(VFlatSrc), typeof(VExtraDst)), maxDepth: 3));
        Assert.Contains(ex.Diagnostics, d => d.Member == nameof(VExtraDst.Extra));
    }

    [Fact]
    public void Validate_IgnoredMember_DoesNotReport()
    {
        var registry = RegistryFor(c =>
            c.CreateMap<VFlatSrc, VExtraDst>().ForMember(d => d.Extra, o => o.Ignore()));
        ProjectionValidator.Validate(registry, new TypePair(typeof(VFlatSrc), typeof(VExtraDst)), maxDepth: 3);
    }

    [Fact]
    public void Validate_NumericWidening_PassesValidation()
    {
        var registry = RegistryFor(c => c.CreateMap<VIntSrc, VLongDst>());
        ProjectionValidator.Validate(registry, new TypePair(typeof(VIntSrc), typeof(VLongDst)), maxDepth: 3);
    }

    [Fact]
    public void Validate_NestedObjectWithMissingMap_ReportsNestedTypePair()
    {
        var registry = RegistryFor(c => c.CreateMap<VOuterSrc, VOuterDst>());
        var ex = Assert.Throws<AtlasProjectionException>(() =>
            ProjectionValidator.Validate(registry, new TypePair(typeof(VOuterSrc), typeof(VOuterDst)), maxDepth: 3));
        Assert.Contains(ex.Diagnostics, d => d.SourceType == typeof(VInnerSrc) && d.DestinationType == typeof(VInnerDst));
    }

    [Fact]
    public void Validate_RecursiveCycle_StopsAtMaxDepth_DoesNotInfiniteLoop()
    {
        // VNode -> VNode is a self-cycle.
        var registry = RegistryFor(c => c.CreateMap<VNode, VNode>(MemberList.None));
        ProjectionValidator.Validate(registry, new TypePair(typeof(VNode), typeof(VNode)), maxDepth: 3);
    }

    [Fact]
    public void Validate_AggregatesAllErrors_NotJustFirst()
    {
        var registry = RegistryFor(c => c.CreateMap<VFlatSrc, VTwoMissingDst>());
        var ex = Assert.Throws<AtlasProjectionException>(() =>
            ProjectionValidator.Validate(registry, new TypePair(typeof(VFlatSrc), typeof(VTwoMissingDst)), maxDepth: 3));
        Assert.Equal(2, ex.Diagnostics.Count);
    }
}

// ---- Test fixtures ----
public class VFlatSrc { public int Id { get; set; } public string Name { get; set; } = ""; }
public class VFlatDst { public int Id { get; set; } public string Name { get; set; } = ""; }
public class VExtraDst { public int Id { get; set; } public string Name { get; set; } = ""; public string Extra { get; set; } = ""; }
public class VTwoMissingDst { public int Id { get; set; } public string Missing1 { get; set; } = ""; public string Missing2 { get; set; } = ""; }
public class VInnerSrc { public string Name { get; set; } = ""; }
public class VInnerDst { public string Name { get; set; } = ""; }
public class VOuterSrc { public VInnerSrc Inner { get; set; } = new(); }
public class VOuterDst { public VInnerDst Inner { get; set; } = new(); }
public class VIntSrc { public int Count { get; set; } }
public class VLongDst { public long Count { get; set; } }
public class VNode { public VNode? Next { get; set; } }
