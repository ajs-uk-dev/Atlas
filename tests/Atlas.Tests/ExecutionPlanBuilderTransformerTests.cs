using System.Linq.Expressions;

namespace Atlas.Tests;

public class ExecutionPlanBuilderTransformerTests
{
    public class S { public string? Name { get; set; } public int Count { get; set; } }
    public class D { public string? Name { get; set; } public int Count { get; set; } }

    public class CtorD
    {
        public string Name { get; }
        public CtorD(string name) { Name = name; }
    }

    public sealed class Inner { public string? Name { get; set; } }
    public sealed class Outer { public Inner? Child { get; set; } }

    [Fact]
    public void NoTransformer_PropertyAssign_SourceUntouched()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None));
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<D>(new S { Name = "  hello  " });

        Assert.Equal("  hello  ", dst.Name);   // untouched
    }

    [Fact]
    public void SingleTransformer_WrapsSource()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .AddTransform<string>(s => s == null ? null! : s.Trim()));
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<D>(new S { Name = "  hello  " });

        Assert.Equal("hello", dst.Name);
    }

    [Fact]
    public void TwoTransformers_ComposeLeftToRight()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .AddTransform<string>(s => s == null ? null! : s.Trim())
                .AddTransform<string>(s => s == null ? null! : s.ToUpperInvariant()));
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<D>(new S { Name = "  hello  " });

        // Step 1: trim → "hello". Step 2: upper → "HELLO".
        Assert.Equal("HELLO", dst.Name);
    }

    [Fact]
    public void CtorArg_AlsoTransformed()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, CtorD>(MemberList.None)
                .AddTransform<string>(s => s == null ? null! : s.Trim()));
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<CtorD>(new S { Name = "  hello  " });

        Assert.Equal("hello", dst.Name);
    }

    [Fact]
    public void NestedPath_AlsoTransformed()
    {
        // ForPath into a nested destination — transformer must wrap before the nested-assign emit.
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, Outer>(MemberList.None)
                .ForPath(d => d.Child!.Name, opt => opt.MapFrom(s => s.Name))
                .AddTransform<string>(s => s == null ? null! : s.Trim()));
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<Outer>(new S { Name = "  hello  " });

        Assert.NotNull(dst.Child);
        Assert.Equal("hello", dst.Child!.Name);
    }
}
