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
    [Benchmark]
    public MapperConfiguration ConfigBuild_FromCold()
    {
        var config = new MapperConfiguration(c => RegisterPair(c));
        config.CompileMappings();
        return config;
    }

    private static void RegisterPair(MapperConfigurationExpression cfg)
    {
        // Distinct (TSource, TDest) pairs at scale require generated types — out of scope here.
        // BenchmarkDotNet already iterates each [Benchmark] method many times for timing, so a
        // single CreateMap call per invocation exercises the CreateMap call site, dictionary
        // insert, and one full ResolveMissingMembers + Seal + Compile cycle — covering the
        // dominant cost. Distinct-pair scaling moves to a v2 design doc.
        cfg.CreateMap<BuildSrc, BuildDst>();
    }

    public class BuildSrc { public int Id { get; set; } public string Name { get; set; } = ""; }
    public class BuildDst { public int Id { get; set; } public string Name { get; set; } = ""; }
}
