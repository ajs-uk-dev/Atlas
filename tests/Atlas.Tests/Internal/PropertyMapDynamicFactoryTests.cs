using Atlas.Internal;

namespace Atlas.Tests.Internal;

public class PropertyMapDynamicFactoryTests
{
    [Fact]
    public void ForDictKey_PopulatesDynamicKeyAndDestinationProperty()
    {
        var member = typeof(SamplePoco).GetProperty(nameof(SamplePoco.Name))!;
        var pm = PropertyMap.ForDictKey(member, "Name");
        Assert.NotNull(pm.DestinationProperty);
        Assert.Equal(nameof(SamplePoco.Name), pm.DestinationProperty!.Name);
        Assert.Equal("Name", pm.DynamicKey);
        Assert.Null(pm.SourcePath);
    }

    [Fact]
    public void ForPocoSource_PopulatesDynamicKeyAndSourcePath()
    {
        var member = typeof(SamplePoco).GetProperty(nameof(SamplePoco.Name))!;
        var pm = PropertyMap.ForPocoSource(member, "Name");
        Assert.Null(pm.DestinationProperty);
        Assert.Equal("Name", pm.DynamicKey);
        Assert.NotNull(pm.SourcePath);
        Assert.Single(pm.SourcePath!.Members);
        Assert.Same(member, pm.SourcePath!.Members[0]);
    }

    [Fact]
    public void DynamicKey_DefaultsToNull_ForRegularFactories()
    {
        var member = typeof(SamplePoco).GetProperty(nameof(SamplePoco.Name))!;
        var pm = PropertyMap.ForProperty(member);
        Assert.Null(pm.DynamicKey);
    }

    private sealed class SamplePoco { public string Name { get; set; } = ""; }
}
