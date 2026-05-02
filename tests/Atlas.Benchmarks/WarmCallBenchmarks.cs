using Atlas;
using BenchmarkDotNet.Attributes;

namespace Atlas.Benchmarks;

/// <summary>
/// Per-call cost after compilation. The "Allocated" column is the load-bearing metric — it must
/// match the §2.4 budget: one delegate-invoke + the destination's own allocations + (for collections)
/// one collection allocation.
/// </summary>
[MemoryDiagnoser]
public class WarmCallBenchmarks
{
    private IMapper _mapper = default!;
    private FlatSrc _flatSrc = default!;
    private NestedSrc _nestedSrc = default!;
    private List<FlatSrc> _list = default!;

    [GlobalSetup]
    public void Setup()
    {
        var config = new MapperConfiguration(c =>
        {
            c.CreateMap<FlatSrc, FlatDst>();
            c.CreateMap<InnerSrc, InnerDst>();
            c.CreateMap<MidSrc, MidDst>();
            c.CreateMap<NestedSrc, NestedDst>();
            c.CreateMap<List<FlatSrc>, List<FlatDst>>(MemberList.None);
        });
        config.CompileMappings();
        _mapper = config.CreateMapper();

        _flatSrc = new FlatSrc { A = "a", B = "b", C = "c", D = "d", E = "e" };
        _nestedSrc = new NestedSrc { Mid = new MidSrc { Inner = new InnerSrc { Value = "z" } } };

        _list = new List<FlatSrc>(100);
        for (var i = 0; i < 100; i++)
            _list.Add(new FlatSrc { A = "a" + i, B = "b", C = "c", D = "d", E = "e" });
    }

    [Benchmark]
    public FlatDst WarmCall_FlatPoco_5Strings() => _mapper.Map<FlatSrc, FlatDst>(_flatSrc);

    [Benchmark]
    public NestedDst WarmCall_Nested_3Levels() => _mapper.Map<NestedSrc, NestedDst>(_nestedSrc);

    [Benchmark]
    public List<FlatDst> WarmCall_List_100Items() => _mapper.Map<List<FlatSrc>, List<FlatDst>>(_list);

    public class FlatSrc { public string A { get; set; } = ""; public string B { get; set; } = ""; public string C { get; set; } = ""; public string D { get; set; } = ""; public string E { get; set; } = ""; }
    public class FlatDst { public string A { get; set; } = ""; public string B { get; set; } = ""; public string C { get; set; } = ""; public string D { get; set; } = ""; public string E { get; set; } = ""; }

    public class InnerSrc { public string Value { get; set; } = ""; }
    public class InnerDst { public string Value { get; set; } = ""; }
    public class MidSrc { public InnerSrc Inner { get; set; } = new(); }
    public class MidDst { public InnerDst Inner { get; set; } = new(); }
    public class NestedSrc { public MidSrc Mid { get; set; } = new(); }
    public class NestedDst { public MidDst Mid { get; set; } = new(); }
}
