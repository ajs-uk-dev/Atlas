using Atlas;
using Atlas.Internal;

namespace Atlas.Tests.Internal;

public class TypeMapPreserveReferencesFieldTests
{
    [Fact]
    public void DefaultTypeMap_HasPreserveReferencesFalse()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<SamplePoco, SamplePoco>());
        var tm = GetTypeMap(cfg, typeof(SamplePoco), typeof(SamplePoco));
        Assert.False(tm.PreserveReferences);
    }

    [Fact]
    public void CreateMapWithPreserveReferences_SetsFlagOnTypeMap()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<SamplePoco, SamplePoco>().PreserveReferences());
        var tm = GetTypeMap(cfg, typeof(SamplePoco), typeof(SamplePoco));
        Assert.True(tm.PreserveReferences);
    }

    [Fact]
    public void PreserveReferences_ReturnsExpressionForFluentChaining()
    {
        var cfg = new MapperConfiguration(c =>
        {
            // Verify that PreserveReferences() returns IMappingExpression so chains work.
            c.CreateMap<SamplePoco, SamplePoco>()
                .PreserveReferences()
                .ForMember(d => d.Name, opt => opt.MapFrom(s => s.Name));
        });
        var tm = GetTypeMap(cfg, typeof(SamplePoco), typeof(SamplePoco));
        Assert.True(tm.PreserveReferences);
    }

    private static TypeMap GetTypeMap(MapperConfiguration cfg, Type src, Type dst)
        => cfg.Internal_Registry.GetTypeMap(new TypePair(src, dst))
           ?? throw new InvalidOperationException($"No typemap for ({src}, {dst})");

    private sealed class SamplePoco
    {
        public string? Name { get; set; }
    }
}
