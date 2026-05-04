using Atlas.Configuration;

namespace Atlas.Tests;

public class ExecutionPlanBuilderHookTests
{
    public class S { public int Value { get; set; } }
    public class D { public int Value { get; set; } public List<string> Trace { get; } = new(); }

    [Fact]
    public void BuildPocoLambda_EmitsBeforeHooksAtTop()
    {
        var trace = new List<string>();
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .BeforeMap((s, d) => trace.Add("before")));
        var mapper = cfg.CreateMapper();

        mapper.Map<D>(new S());

        Assert.Single(trace);
        Assert.Equal("before", trace[0]);
    }

    [Fact]
    public void BuildPocoLambda_EmitsAfterHooksAtBottom()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .AfterMap((s, d) => d.Trace.Add($"after value={d.Value}")));
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<D>(new S { Value = 7 });

        Assert.Single(dst.Trace);
        Assert.Equal("after value=7", dst.Trace[0]);
    }

    [Fact]
    public void BuildUpdate_EmitsHooksToo()
    {
        var trace = new List<string>();
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .BeforeMap((s, d) => trace.Add($"before existing-value={d.Value}"))
                .AfterMap((s, d) => trace.Add($"after value={d.Value}")));
        var mapper = cfg.CreateMapper();

        var existing = new D { Value = 99 };
        mapper.Map<S, D>(new S { Value = 7 }, existing);

        Assert.Equal(2, trace.Count);
        Assert.Equal("before existing-value=99", trace[0]);
        Assert.Equal("after value=7", trace[1]);
    }

    [Fact]
    public void NoHooks_NoExtraStatementsEmitted()
    {
        // Sanity: no-hook config behaves identically to pre-feature.
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None));
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<D>(new S { Value = 42 });

        Assert.Equal(42, dst.Value);
        Assert.Empty(dst.Trace);
    }
}
