using Atlas;
using BenchmarkDotNet.Attributes;

namespace Atlas.Benchmarks;

/// <summary>
/// Configuration-build + first-call latency. Establishes the floor for one-shot startup work.
/// </summary>
[MemoryDiagnoser]
public class ColdCallBenchmarks
{
    [Benchmark]
    public ColdDst Cold_BuildAndFirstCall_FlatPoco()
    {
        var config = new MapperConfiguration(c => c.CreateMap<ColdSrc, ColdDst>());
        config.CompileMappings();
        var mapper = config.CreateMapper();
        return mapper.Map<ColdSrc, ColdDst>(new ColdSrc { Id = 7, Name = "x" });
    }

    public class ColdSrc { public int Id { get; set; } public string Name { get; set; } = ""; }
    public class ColdDst { public int Id { get; set; } public string Name { get; set; } = ""; }
}
