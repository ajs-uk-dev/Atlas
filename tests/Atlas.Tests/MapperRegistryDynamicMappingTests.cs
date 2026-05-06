using System.Collections.Generic;
using System.Dynamic;
using Atlas;
using Atlas.Internal;

namespace Atlas.Tests;

public class MapperRegistryDynamicMappingTests
{
    [Fact]
    public void GetTypeMap_MaterializesDictToPoco_OnFirstCall()
    {
        var cfg = new MapperConfiguration(_ => { });
        var registry = cfg.Internal_Registry;

        var pair = new TypePair(typeof(IDictionary<string, object>), typeof(SamplePoco));
        var tm = registry.GetTypeMap(pair);

        Assert.NotNull(tm);
        Assert.True(tm!.IsDynamic);
        Assert.Equal(typeof(IDictionary<string, object>), tm.SourceType);
        Assert.Equal(typeof(SamplePoco), tm.DestinationType);
        Assert.True(tm.IsSealed);
    }

    [Fact]
    public void GetTypeMap_MaterializesPocoToDict_OnFirstCall()
    {
        var cfg = new MapperConfiguration(_ => { });
        var registry = cfg.Internal_Registry;

        var pair = new TypePair(typeof(SamplePoco), typeof(ExpandoObject));
        var tm = registry.GetTypeMap(pair);

        Assert.NotNull(tm);
        Assert.True(tm!.IsDynamic);
    }

    [Fact]
    public void GetTypeMap_ReturnsCachedInstance_OnSecondCall()
    {
        var cfg = new MapperConfiguration(_ => { });
        var registry = cfg.Internal_Registry;

        var pair = new TypePair(typeof(IDictionary<string, object>), typeof(SamplePoco));
        var first = registry.GetTypeMap(pair);
        var second = registry.GetTypeMap(pair);

        Assert.Same(first, second);
    }

    [Fact]
    public void GetTypeMap_DoesNotFire_WhenBothSidesDynamic()
    {
        var cfg = new MapperConfiguration(_ => { });
        var registry = cfg.Internal_Registry;

        var pair = new TypePair(typeof(ExpandoObject), typeof(Dictionary<string, object>));
        var tm = registry.GetTypeMap(pair);

        // Detector requires XOR — both-dynamic falls through; no map registered → null.
        Assert.Null(tm);
    }

    [Fact]
    public void GetTypeMap_OriginatingProfileIsNull_ForDynamicMaps()
    {
        var cfg = new MapperConfiguration(_ => { });
        var registry = cfg.Internal_Registry;

        var pair = new TypePair(typeof(IDictionary<string, object>), typeof(SamplePoco));
        var tm = registry.GetTypeMap(pair);

        Assert.Null(tm!.OriginatingProfile);
    }

    [Fact]
    public void GetTypeMap_SynthesizesOnePropertyMapPerWritablePocoMember_ForDictToPoco()
    {
        var cfg = new MapperConfiguration(_ => { });
        var registry = cfg.Internal_Registry;

        var pair = new TypePair(typeof(IDictionary<string, object>), typeof(SamplePoco));
        var tm = registry.GetTypeMap(pair);

        Assert.Equal(2, tm!.PropertyMaps.Count);  // SamplePoco has Id and Name (both writable)
        Assert.Contains(tm.PropertyMaps, pm => pm.DynamicKey == "Id");
        Assert.Contains(tm.PropertyMaps, pm => pm.DynamicKey == "Name");
    }

    [Fact]
    public void GetTypeMap_ExplicitClosedRegistration_TakesPrecedenceOverDetector()
    {
        // Per design §7.1: explicit registration wins; detector never fires for this pair.
        // (V1 limitation: the explicit registration produces broken codegen, but the
        //  detector-skip behaviour is the property under test here, not the codegen.)
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<SamplePoco, IDictionary<string, object>>());
        var registry = cfg.Internal_Registry;

        var pair = new TypePair(typeof(SamplePoco), typeof(IDictionary<string, object>));
        var tm = registry.GetTypeMap(pair);

        Assert.NotNull(tm);
        Assert.False(tm!.IsDynamic);   // explicit registration is NOT marked dynamic
    }

    private sealed class SamplePoco
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }
}
