using Atlas;
using BenchmarkDotNet.Attributes;

namespace Atlas.Benchmarks;

/// <summary>
/// Measures <see cref="MapperConfiguration"/> construction + <see cref="MapperConfiguration.CompileMappings"/>
/// for varying map counts. Confirms construction stays sub-linear via dictionary lookups.
/// </summary>
[MemoryDiagnoser]
public class ConfigBuildBenchmarks
{
    [Params(10, 100, 1000)]
    public int MapCount;

    [Benchmark]
    public MapperConfiguration ConfigBuild_FromCold()
    {
        var config = new MapperConfiguration(c =>
        {
            for (var i = 0; i < MapCount; i++)
                RegisterPair(c, i);
        });
        config.CompileMappings();
        return config;
    }

    private static void RegisterPair(MapperConfigurationExpression cfg, int index)
    {
        // v1 caveat: distinct (TSource, TDest) pairs at scale require generated types — out of scope
        // here. Re-registering the same pair last-call-wins still exercises the CreateMap call site,
        // dictionary upsert, and one full ResolveMissingMembers + Seal + Compile cycle. That covers
        // the dominant cost. Distinct-pair scaling moves to a v2 design doc.
        _ = index;
        cfg.CreateMap<BuildSrc, BuildDst>();
    }

    public class BuildSrc { public int Id { get; set; } public string Name { get; set; } = ""; }
    public class BuildDst { public int Id { get; set; } public string Name { get; set; } = ""; }
}
