using Atlas.Configuration;

namespace Atlas.Tests;

public class ExecutionPlanBuilderUpdateConditionTests
{
    public class S
    {
        public int V { get; set; }
        public string? Email { get; set; }
    }

    public class D
    {
        public int V { get; set; }
        public string Email { get; set; } = "";
    }

    [Fact]
    public void Update_PreConditionFalse_PreservesExistingDestValue()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .ForMember(d => d.V, opt =>
                {
                    opt.PreCondition(s => s.V > 0);
                    opt.MapFrom(s => s.V);
                }));
        var mapper = cfg.CreateMapper();

        var existing = new D { V = 99 };
        mapper.Map(new S { V = -5 }, existing);

        Assert.Equal(99, existing.V);   // preserved, NOT zeroed
    }

    [Fact]
    public void Update_ConditionFalse_PreservesExistingDestValue()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .ForMember(d => d.V, opt =>
                {
                    opt.MapFrom(s => s.V);
                    opt.Condition((s, v) => v > 100);   // 5 fails
                }));
        var mapper = cfg.CreateMapper();

        var existing = new D { V = 99 };
        mapper.Map(new S { V = 5 }, existing);

        Assert.Equal(99, existing.V);
    }

    [Fact]
    public void Update_BothPredicatesPass_OverwritesValue()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .ForMember(d => d.V, opt =>
                {
                    opt.PreCondition(s => s.V > 0);
                    opt.MapFrom(s => s.V);
                    opt.Condition((s, v) => v < 100);
                }));
        var mapper = cfg.CreateMapper();

        var existing = new D { V = 99 };
        mapper.Map(new S { V = 7 }, existing);

        Assert.Equal(7, existing.V);
    }

    [Fact]
    public void Update_PreConditionFalse_DoesNotInvokeMapFromExpression()
    {
        var counter = new SourceCounter();
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<SourceCounter, CounterDto>(MemberList.None)
                .ForMember(d => d.Probed, opt =>
                {
                    opt.PreCondition(s => false);
                    opt.MapFrom(s => s.IncrementAndReturn);
                }));
        var mapper = cfg.CreateMapper();

        var existing = new CounterDto { Probed = 99 };
        mapper.Map(counter, existing);

        Assert.Equal(0, counter.Probes);   // resolution skipped
        Assert.Equal(99, existing.Probed);  // value preserved
    }

    public sealed class SourceCounter
    {
        public int Probes;
        public int IncrementAndReturn { get { Probes++; return 1; } }
    }
    public sealed class CounterDto { public int Probed { get; set; } }
}
