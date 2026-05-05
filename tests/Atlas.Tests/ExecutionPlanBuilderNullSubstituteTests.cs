using Atlas.Configuration;

namespace Atlas.Tests;

public class ExecutionPlanBuilderNullSubstituteTests
{
    public class S
    {
        public string? Name { get; set; }
        public int? Score { get; set; }
        public Customer? Customer { get; set; }
    }
    public class Customer { public string? Nick { get; set; } }
    public class D
    {
        public string Name { get; set; } = "";
        public long Score { get; set; }
        public string Nick { get; set; } = "";
    }

    [Fact]
    public void ReferenceTypeSourceNull_UsesSubstitute()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .ForMember(d => d.Name, opt =>
                {
                    opt.MapFrom(s => s.Name);
                    opt.NullSubstitute("Anonymous");
                }));
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<D>(new S { Name = null });

        Assert.Equal("Anonymous", dst.Name);
    }

    [Fact]
    public void ReferenceTypeSourceNonNull_BypassesSubstitute()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .ForMember(d => d.Name, opt =>
                {
                    opt.MapFrom(s => s.Name);
                    opt.NullSubstitute("Anonymous");
                }));
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<D>(new S { Name = "Alice" });

        Assert.Equal("Alice", dst.Name);
    }

    [Fact]
    public void NullableValueTypeSourceNull_UsesSubstitute()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .ForMember(d => d.Score, opt =>
                {
                    opt.MapFrom(s => s.Score);
                    opt.NullSubstitute(0);
                }));
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<D>(new S { Score = null });

        Assert.Equal(0L, dst.Score);
    }

    [Fact]
    public void NullableValueTypeSourceNonNull_UsesValue()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .ForMember(d => d.Score, opt =>
                {
                    opt.MapFrom(s => s.Score);
                    opt.NullSubstitute(0);
                }));
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<D>(new S { Score = 42 });

        Assert.Equal(42L, dst.Score);
    }

    [Fact]
    public void SubstituteParticipatesInNumericConversion()
    {
        // Substitute is int (0); destination is long. ApplyNullSubstitute runs BEFORE
        // ConvertOrMap so the int → long widening is applied to the coalesced value.
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .ForMember(d => d.Score, opt =>
                {
                    opt.MapFrom(s => s.Score);
                    opt.NullSubstitute(7);
                }));
        var mapper = cfg.CreateMapper();

        var nullScore = mapper.Map<D>(new S { Score = null });
        var realScore = mapper.Map<D>(new S { Score = 99 });

        Assert.Equal(7L, nullScore.Score);
        Assert.Equal(99L, realScore.Score);
    }

    public class CtorD { public string Name { get; } public CtorD(string name) { Name = name; } }

    [Fact]
    public void CtorParam_WithNullSubstitute_Works()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, CtorD>(MemberList.None)
                .ForCtorParam("name", opt =>
                {
                    opt.MapFrom(s => s.Name);
                    opt.NullSubstitute("FromCtor");
                }));
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<CtorD>(new S { Name = null });

        Assert.Equal("FromCtor", dst.Name);
    }

    [Fact]
    public void Substitute_AppliesWhenSourcePathIsNullable()
    {
        // Customer is present but Nick is null → substitute fires on the null Nick value.
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .ForMember(d => d.Name, opt => opt.Ignore())
                .ForMember(d => d.Score, opt => opt.Ignore())
                .ForMember(d => d.Nick, opt =>
                {
                    opt.MapFrom(s => s.Customer!.Nick);
                    opt.NullSubstitute("NoCustomer");
                }));
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<D>(new S { Customer = new Customer { Nick = null } });

        Assert.Equal("NoCustomer", dst.Nick);
    }

    [Fact]
    public void Substitute_Combined_With_TransformerAndCondition()
    {
        // Pipeline order: substitute → transform → condition.
        // Substitute fires first (null source → "(none)"), transformer trims, condition gates.
        var cfg = new MapperConfiguration(c =>
        {
            c.ValueTransformers.Add<string>(s => s.Trim());
            c.CreateMap<S, D>(MemberList.None)
                .ForMember(d => d.Name, opt =>
                {
                    opt.MapFrom(s => s.Name);
                    opt.NullSubstitute("(none)");
                    opt.Condition((s, name) => name.Length > 0);
                });
        });
        var mapper = cfg.CreateMapper();

        var nullSrc = mapper.Map<D>(new S { Name = null });
        var spaces = mapper.Map<D>(new S { Name = "   " });
        var real = mapper.Map<D>(new S { Name = "  Alice  " });

        // Null source → substitute "(none)" → trim → length 6 → assigns "(none)".
        Assert.Equal("(none)", nullSrc.Name);
        // Spaces source → substitute bypassed (non-null) → trim → "" → length 0 → Condition fails → default(string) = null.
        Assert.Null(spaces.Name);
        // Real source → substitute bypassed → trim → "Alice" → length > 0 → assigns "Alice".
        Assert.Equal("Alice", real.Name);
    }
}
