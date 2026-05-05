using Atlas.Configuration;

namespace Atlas.Tests;

public class ExecutionPlanBuilderConditionTests
{
    public class S
    {
        public int V { get; set; }
        public int? Maybe { get; set; }
        public string? Text { get; set; }
    }

    public class D
    {
        public int V { get; set; }
        public int Maybe { get; set; }
        public string Text { get; set; } = "";
    }

    [Fact]
    public void PreConditionTrue_AssignsResolvedValue()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .ForMember(d => d.V, opt =>
                {
                    opt.PreCondition(s => s.V > 0);
                    opt.MapFrom(s => s.V);
                }));
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<D>(new S { V = 42 });

        Assert.Equal(42, dst.V);
    }

    [Fact]
    public void PreConditionFalse_FreshMap_PropertyIsDefault()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .ForMember(d => d.V, opt =>
                {
                    opt.PreCondition(s => s.V > 0);
                    opt.MapFrom(s => s.V);
                }));
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<D>(new S { V = -5 });

        Assert.Equal(0, dst.V);
    }

    [Fact]
    public void PreConditionFalse_DoesNotInvokeMapFromExpression()
    {
        // Use a real-valued source with a side-effect counter inside MapFrom.
        // Even though we can't put state into the Expression, we use the only path
        // that lets us observe it: a property whose getter increments a counter.
        var counter = new SourceCounter();
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<SourceCounter, CounterDto>(MemberList.None)
                .ForMember(d => d.Probed, opt =>
                {
                    opt.PreCondition(s => false);              // always skip resolution
                    opt.MapFrom(s => s.IncrementAndReturn);    // would increment if invoked
                }));
        var mapper = cfg.CreateMapper();

        mapper.Map<CounterDto>(counter);

        Assert.Equal(0, counter.Probes);
    }

    public sealed class SourceCounter
    {
        public int Probes;
        public int IncrementAndReturn { get { Probes++; return 1; } }
    }
    public sealed class CounterDto { public int Probed { get; set; } }

    [Fact]
    public void ConditionTrueOnResolvedValue_AssignsValue()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .ForMember(d => d.V, opt =>
                {
                    opt.MapFrom(s => s.V);
                    opt.Condition((s, v) => v > 10);
                }));
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<D>(new S { V = 42 });

        Assert.Equal(42, dst.V);
    }

    [Fact]
    public void ConditionFalseOnResolvedValue_FreshMap_PropertyIsDefault()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .ForMember(d => d.V, opt =>
                {
                    opt.MapFrom(s => s.V);
                    opt.Condition((s, v) => v > 100);
                }));
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<D>(new S { V = 5 });

        Assert.Equal(0, dst.V);
    }

    [Fact]
    public void BothPredicates_BothPass_AssignsValue()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .ForMember(d => d.V, opt =>
                {
                    opt.PreCondition(s => s.V > 0);
                    opt.MapFrom(s => s.V * 2);
                    opt.Condition((s, v) => v < 100);
                }));
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<D>(new S { V = 10 });

        Assert.Equal(20, dst.V);
    }
}
