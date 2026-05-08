using System.Linq.Expressions;
using Atlas;
using Atlas.Internal;
using Atlas.Projections.Internal;

namespace Atlas.Projections.Tests;

public class ExpressionTranslatorRejectionTests
{
    [Fact]
    public void PairNotRegistered_ThrowsAtlasProjectionException_AtTranslate()
    {
        var cfg = new MapperConfiguration(_ => { /* no maps */ });
        Expression<Func<UEDS_RejectDtoA, bool>> predicate = d => d.Id == 1;

        var ex = Assert.Throws<AtlasProjectionException>(() =>
            ExpressionTranslator.Translate(
                cfg.Internal_Registry,
                new TypePair(typeof(UEDS_RejectSrcA), typeof(UEDS_RejectDtoA)),
                predicate));

        Assert.Contains(ex.Diagnostics, d => d.Reason.Contains("UseAsDataSource translation:")
                                          && d.Reason.Contains("no map registered"));
    }

    [Fact]
    public void TypeMapWithHooks_ThrowsAtlasProjectionException_AtTranslate()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap<UEDS_RejectSrcHooks, UEDS_RejectDtoHooks>()
                .BeforeMap((s, d) => { });
        });
        Expression<Func<UEDS_RejectDtoHooks, bool>> predicate = d => d.Id == 1;

        var ex = Assert.Throws<AtlasProjectionException>(() =>
            ExpressionTranslator.Translate(
                cfg.Internal_Registry,
                new TypePair(typeof(UEDS_RejectSrcHooks), typeof(UEDS_RejectDtoHooks)),
                predicate));

        Assert.Contains(ex.Diagnostics, d => d.Reason.Contains("hook"));
    }

    [Fact]
    public void TypeMapWithPreserveReferences_ThrowsAtlasProjectionException_AtTranslate()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap<UEDS_RejectSrcPR, UEDS_RejectDtoPR>().PreserveReferences();
        });
        Expression<Func<UEDS_RejectDtoPR, bool>> predicate = d => d.Id == 1;

        var ex = Assert.Throws<AtlasProjectionException>(() =>
            ExpressionTranslator.Translate(
                cfg.Internal_Registry,
                new TypePair(typeof(UEDS_RejectSrcPR), typeof(UEDS_RejectDtoPR)),
                predicate));

        Assert.Contains(ex.Diagnostics, d => d.Reason.Contains("PreserveReferences"));
    }
}

public class UEDS_RejectSrcA { public int Id { get; set; } }
public class UEDS_RejectDtoA { public int Id { get; set; } }

public class UEDS_RejectSrcHooks { public int Id { get; set; } }
public class UEDS_RejectDtoHooks { public int Id { get; set; } }

public class UEDS_RejectSrcPR { public int Id { get; set; } public UEDS_RejectSrcPR? Self { get; set; } }
public class UEDS_RejectDtoPR { public int Id { get; set; } public UEDS_RejectDtoPR? Self { get; set; } }

public class ExpressionTranslatorMemberRejectionTests
{
    [Fact]
    public void MemberNotFound_ThrowsAtlasProjectionException()
    {
        // Configure a map but reference a destination member that doesn't have a PropertyMap.
        // The simplest case: a destination DTO whose property Atlas's convention engine
        // can't resolve to the source — declare an extra DTO property no source has.
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<UEDS_MissingMemberSrc, UEDS_MissingMemberDto>(MemberList.None));
        Expression<Func<UEDS_MissingMemberDto, bool>> predicate = d => d.PhantomMember == "x";

        var ex = Assert.Throws<AtlasProjectionException>(() =>
            ExpressionTranslator.Translate(
                cfg.Internal_Registry,
                new TypePair(typeof(UEDS_MissingMemberSrc), typeof(UEDS_MissingMemberDto)),
                predicate));

        Assert.Contains(ex.Diagnostics, d => d.Reason.Contains("PhantomMember")
                                          && d.Reason.Contains("no PropertyMap"));
    }

    [Fact]
    public void IgnoredMember_ThrowsAtlasProjectionException()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<UEDS_IgnoredSrc, UEDS_IgnoredDto>()
                .ForMember(d => d.Computed, opt => opt.Ignore()));
        Expression<Func<UEDS_IgnoredDto, bool>> predicate = d => d.Computed > 100;

        var ex = Assert.Throws<AtlasProjectionException>(() =>
            ExpressionTranslator.Translate(
                cfg.Internal_Registry,
                new TypePair(typeof(UEDS_IgnoredSrc), typeof(UEDS_IgnoredDto)),
                predicate));

        Assert.Contains(ex.Diagnostics, d => d.Reason.Contains("Computed")
                                          && d.Reason.Contains("Ignore"));
    }

    [Fact]
    public void ConstantMember_ThrowsAtlasProjectionException()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<UEDS_ConstantSrc, UEDS_ConstantDto>()
                .ForMember(d => d.Status, opt => opt.MapFrom("active")));
        Expression<Func<UEDS_ConstantDto, bool>> predicate = d => d.Status == "active";

        var ex = Assert.Throws<AtlasProjectionException>(() =>
            ExpressionTranslator.Translate(
                cfg.Internal_Registry,
                new TypePair(typeof(UEDS_ConstantSrc), typeof(UEDS_ConstantDto)),
                predicate));

        Assert.Contains(ex.Diagnostics, d => d.Reason.Contains("Status")
                                          && d.Reason.Contains("constant"));
    }

    [Fact]
    public void MidChainPairNotRegistered_ThrowsAtlasProjectionException()
    {
        // Outer (Order, OrderDto) is registered; inner (Customer, CustomerDto) is NOT.
        var cfg = new MapperConfiguration(c => c.CreateMap<UEDS_MidChainSrc, UEDS_MidChainDto>());
        Expression<Func<UEDS_MidChainDto, bool>> predicate = d => d.Customer.Name == "Alice";

        var ex = Assert.Throws<AtlasProjectionException>(() =>
            ExpressionTranslator.Translate(
                cfg.Internal_Registry,
                new TypePair(typeof(UEDS_MidChainSrc), typeof(UEDS_MidChainDto)),
                predicate));

        Assert.Contains(ex.Diagnostics, d => d.Reason.Contains("not registered"));
    }
}

public class UEDS_MissingMemberSrc { public int Id { get; set; } }
public class UEDS_MissingMemberDto { public int Id { get; set; } public string PhantomMember { get; set; } = ""; }

public class UEDS_IgnoredSrc { public int Id { get; set; } }
public class UEDS_IgnoredDto { public int Id { get; set; } public decimal Computed { get; set; } }

public class UEDS_ConstantSrc { public int Id { get; set; } }
public class UEDS_ConstantDto { public int Id { get; set; } public string Status { get; set; } = ""; }

public class UEDS_MidChainCustomer { public string Name { get; set; } = ""; }
public class UEDS_MidChainCustomerDto { public string Name { get; set; } = ""; }
public class UEDS_MidChainSrc { public UEDS_MidChainCustomer Customer { get; set; } = new(); }
public class UEDS_MidChainDto { public UEDS_MidChainCustomerDto Customer { get; set; } = new(); }
