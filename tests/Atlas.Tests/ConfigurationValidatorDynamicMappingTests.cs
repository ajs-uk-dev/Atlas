using System.Collections.Generic;
using Atlas;

namespace Atlas.Tests;

public class ConfigurationValidatorDynamicMappingTests
{
    [Fact]
    public void AssertConfigurationIsValid_PassesWhenNoDynamicTypeMapsMaterialized()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<RegularA, RegularB>());
        cfg.AssertConfigurationIsValid();  // no exception
    }

    [Fact]
    public void AssertConfigurationIsValid_PassesAfterDynamicTypeMapMaterialized()
    {
        var cfg = new MapperConfiguration(_ => { });
        var mapper = cfg.CreateMapper();
        // Materialize a dynamic TypeMap by performing a map call:
        _ = mapper.Map<RegularA>(new Dictionary<string, object> { ["Id"] = 1 });

        // Validator should skip the dynamic TypeMap, no exception.
        cfg.AssertConfigurationIsValid();
    }

    [Fact]
    public void AssertConfigurationIsValid_DoesNotComplain_AboutUnmappedDestinationMembers_ForDynamicTypeMaps()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<RegularA, RegularB>(MemberList.Destination));
        var mapper = cfg.CreateMapper();
        // Materialize a dynamic TypeMap with destination members the validator would normally consider unmapped:
        _ = mapper.Map<RegularC>(new Dictionary<string, object> { });

        cfg.AssertConfigurationIsValid();  // no exception
    }

    private sealed class RegularA { public int Id { get; set; } }
    private sealed class RegularB { public int Id { get; set; } }
    private sealed class RegularC
    {
        public int X { get; set; }
        public string? Y { get; set; }
    }
}
