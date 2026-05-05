using Atlas.Configuration;

namespace Atlas.Tests;

public class ExecutionPlanBuilderCtorParamConditionTests
{
    public class S { public int V { get; set; } }

    // Destination with a ctor param that has a declared default.
    public class DWithDefault
    {
        public int V { get; }
        public DWithDefault(int v = 42) { V = v; }
    }

    // Destination with a ctor param that has no declared default.
    public class DNoDefault
    {
        public int V { get; }
        public DNoDefault(int v) { V = v; }
    }

    [Fact]
    public void CtorParam_PreConditionFalse_UsesDeclaredDefault()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, DWithDefault>(MemberList.None)
                .ForCtorParam("v", opt =>
                {
                    opt.PreCondition(s => s.V > 0);
                    opt.MapFrom(s => s.V);
                }));
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<DWithDefault>(new S { V = -5 });

        Assert.Equal(42, dst.V);   // ctor's declared default wins over default(int)
    }

    [Fact]
    public void CtorParam_NoDeclaredDefault_PreConditionFalse_UsesDefaultT()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, DNoDefault>(MemberList.None)
                .ForCtorParam("v", opt =>
                {
                    opt.PreCondition(s => s.V > 0);
                    opt.MapFrom(s => s.V);
                }));
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<DNoDefault>(new S { V = -5 });

        Assert.Equal(0, dst.V);   // default(int) — no declared default to fall back to
    }
}
