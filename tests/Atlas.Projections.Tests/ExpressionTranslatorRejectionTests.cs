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
}

public class UEDS_MissingMemberSrc { public int Id { get; set; } }
public class UEDS_MissingMemberDto { public int Id { get; set; } public string PhantomMember { get; set; } = ""; }
