namespace Atlas.Tests;

public class MapperValueTransformerTests
{
    public class S { public string? Name { get; set; } public decimal Total { get; set; } }
    public class D { public string? Name { get; set; } public decimal Total { get; set; } }

    public sealed class TrimProfile : MapperProfile
    {
        public TrimProfile()
        {
            ValueTransformers.Add<string>(s => s == null ? null! : s.Trim());
            CreateMap<S, D>(MemberList.None)
                .AddTransform<decimal>(d => Math.Round(d, 2));
        }
    }

    [Fact]
    public void Global_StringTrim_AppliesEverywhere()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.ValueTransformers.Add<string>(s => s == null ? null! : s.Trim());
            c.CreateMap<S, D>(MemberList.None);
        });
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<D>(new S { Name = "  hello  " });

        Assert.Equal("hello", dst.Name);
    }

    [Fact]
    public void Profile_StringLower_ComposesAfterGlobal()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.ValueTransformers.Add<string>(s => s == null ? null! : s.ToLowerInvariant());
            c.AddProfile<TrimProfile>();
        });
        var mapper = cfg.CreateMapper();

        // Global runs first (lower), then profile (trim). Final: trimmed lowercase.
        var dst = mapper.Map<D>(new S { Name = "  HELLO  ", Total = 100.456m });

        Assert.Equal("hello", dst.Name);
        Assert.Equal(100.46m, dst.Total);   // type-map round
    }

    [Fact]
    public void TypeMap_DecimalRound_AppliesOnlyToConfiguredMap()
    {
        // Two maps in the same config; only one has the decimal transformer.
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap<S, D>(MemberList.None)
                .AddTransform<decimal>(d => Math.Round(d, 2));
            c.CreateMap<D, S>(MemberList.None);   // No transformer.
        });
        var mapper = cfg.CreateMapper();

        var d = mapper.Map<D>(new S { Total = 1.234m });
        var s = mapper.Map<S>(new D { Total = 1.234m });

        Assert.Equal(1.23m, d.Total);     // S → D rounds
        Assert.Equal(1.234m, s.Total);    // D → S does not
    }

    [Fact]
    public void Collection_PerElementTransformerFires()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap<S, D>(MemberList.None)
                .AddTransform<string>(s => s == null ? null! : s.Trim());
            c.CreateMap<List<S>, List<D>>(MemberList.None);
        });
        var mapper = cfg.CreateMapper();

        var srcs = new List<S>
        {
            new() { Name = "  a  " },
            new() { Name = "  b  " },
            new() { Name = "  c  " },
        };
        var dsts = mapper.Map<List<S>, List<D>>(srcs);

        Assert.Equal(3, dsts.Count);
        Assert.Equal("a", dsts[0].Name);
        Assert.Equal("b", dsts[1].Name);
        Assert.Equal("c", dsts[2].Name);
    }

    [Fact]
    public void UpdateInPlace_TransformersApply()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap<S, D>(MemberList.None)
                .AddTransform<string>(s => s == null ? null! : s.Trim());
        });
        var mapper = cfg.CreateMapper();

        var existing = new D { Name = "old" };
        mapper.Map<S, D>(new S { Name = "  new  " }, existing);

        Assert.Equal("new", existing.Name);
    }

    public sealed class Outer { public Inner? Child { get; set; } public string? Top { get; set; } }
    public sealed class OuterDto { public InnerDto? Child { get; set; } public string? Top { get; set; } }
    public sealed class Inner { public string? Name { get; set; } }
    public sealed class InnerDto { public string? Name { get; set; } }

    [Fact]
    public void NestedMap_DestinationPropertyTransformed()
    {
        // The OUTER (Outer → OuterDto) map has a string transformer. It applies to outer's
        // own string properties (e.g., Top). The Child property is mapped via a nested
        // (Inner → InnerDto) map — that nested map does NOT inherit transformers, so Inner.Name
        // is NOT trimmed by the outer's transformer. (To trim Inner.Name, the user would need
        // a global/profile-scoped string transformer or a type-map transformer on Inner→InnerDto.)
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap<Outer, OuterDto>(MemberList.None)
                .AddTransform<string>(s => s == null ? null! : s.Trim());
            c.CreateMap<Inner, InnerDto>(MemberList.None);
        });
        var mapper = cfg.CreateMapper();

        var src = new Outer { Top = "  outer  ", Child = new Inner { Name = "  inner  " } };
        var dst = mapper.Map<OuterDto>(src);

        Assert.Equal("outer", dst.Top);                 // Outer's transformer trims Top
        Assert.NotNull(dst.Child);
        Assert.Equal("  inner  ", dst.Child!.Name);     // Inner→InnerDto map has no transformer
    }
}
