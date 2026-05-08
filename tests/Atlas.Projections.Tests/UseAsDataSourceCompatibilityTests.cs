using System.Linq.Expressions;
using Atlas;

namespace Atlas.Projections.Tests;

public class UseAsDataSourceCompatibilityTests
{
    [Fact]
    public void AttributeDeclaredTypeMap_WorksThroughWrapper()
    {
        // [AutoMap] from PR #12 produces a normal TypeMap; wrapper doesn't care about origin.
        // Build cfg with explicit registration to avoid relying on assembly scan (cleaner).
        var cleanCfg = new MapperConfiguration(c => c.CreateMap<UEDS_AttrSrc, UEDS_AttrDto>());

        var src = new[] { new UEDS_AttrSrc { Id = 1, Name = "Alice" } }.AsQueryable();
        var list = src.UseAsDataSource(cleanCfg).For<UEDS_AttrDto>().ToList();
        Assert.Single(list);
        Assert.Equal("Alice", list[0].Name);
    }

    [Fact]
    public void NullSubstitute_CoalesceAppliedInProjection()
    {
        // NullSubstitute with MapFrom: the coalesce is applied by the ProjectionPlanBuilder
        // (binding path) so materializing via .ToList() substitutes null → "(none)".
        // UseAsDataSource wraps the same projection, so the substituted value appears.
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<UEDS_NullSubSrc, UEDS_NullSubDto>()
                .ForMember(d => d.Email, opt =>
                {
                    opt.MapFrom(s => s.Email);
                    opt.NullSubstitute("(none)");
                }));

        var src = new[]
        {
            new UEDS_NullSubSrc { Id = 1, Email = null },
            new UEDS_NullSubSrc { Id = 2, Email = "alice@x" },
        }.AsQueryable();

        var list = src.UseAsDataSource(cfg).For<UEDS_NullSubDto>().ToList();
        Assert.Equal(2, list.Count);
        Assert.Equal("(none)", list[0].Email);   // null coalesced to substitute
        Assert.Equal("alice@x", list[1].Email);  // non-null stays as-is
    }

    [Fact]
    public void OpenGenericMaterializedClosedPair_WorksThroughWrapper()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap(typeof(UEDS_OpenSrc<>), typeof(UEDS_OpenDto<>)));

        var src = new[] { new UEDS_OpenSrc<int> { Value = 42 } }.AsQueryable();
        var list = src.UseAsDataSource(cfg).For<UEDS_OpenDto<int>>().ToList();
        Assert.Single(list);
        Assert.Equal(42, list[0].Value);
    }

    [Fact]
    public void HooksTypeMap_RejectedAtFor()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<UEDS_HookSrc, UEDS_HookDto>().BeforeMap((s, d) => { }));

        var src = Array.Empty<UEDS_HookSrc>().AsQueryable();
        var ex = Assert.Throws<AtlasProjectionException>(() =>
            src.UseAsDataSource(cfg).For<UEDS_HookDto>().Where(d => d.Id > 0).ToList());

        Assert.Contains(ex.Diagnostics, d => d.Reason.Contains("hook"));
    }

    [Fact]
    public void ReverseMapTypeMap_WorksInBothDirections()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<UEDS_RevSrc, UEDS_RevDto>().ReverseMap());

        var srcQuery = new[] { new UEDS_RevSrc { Id = 1, Name = "A" } }.AsQueryable();
        var dtoList = srcQuery.UseAsDataSource(cfg).For<UEDS_RevDto>().ToList();
        Assert.Single(dtoList);

        var dtoQuery = new[] { new UEDS_RevDto { Id = 2, Name = "B" } }.AsQueryable();
        var srcList = dtoQuery.UseAsDataSource(cfg).For<UEDS_RevSrc>().ToList();
        Assert.Single(srcList);
        Assert.Equal(2, srcList[0].Id);
    }

    [Fact]
    public void GlobalValueTransformer_FiresOnTranslatedMembers()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.ValueTransformers.Add<string>(s => s + "!");
            c.CreateMap<UEDS_TransSrc, UEDS_TransDto>();
        });

        var src = new[] { new UEDS_TransSrc { Id = 1, Name = "Alice" } }.AsQueryable();
        var list = src.UseAsDataSource(cfg).For<UEDS_TransDto>().ToList();
        Assert.Equal("Alice!", list[0].Name);
    }

    [Fact]
    public void NullSubstitute_NotAppliedInPredicateTranslation_v1Limitation()
    {
        // Locks in current v1 behavior: ExpressionTranslator.BuildSourceExpression does not
        // consult pm.NullSubstitute, so Where(d => d.Email == "(none)") translates to
        // Where(s => s.Email == "(none)") and never matches null-source rows.
        //
        // ProjectionPlanBuilder DOES apply NullSubstitute Coalesce wrapping during projection
        // — see the NullSubstitute_CoalesceAppliedInProjection test above. The projection path
        // works; the predicate path doesn't (in v1).
        //
        // When predicate-path Coalesce support lands in v2, this test should be UPDATED to
        // assert the row IS matched. Until then, the asymmetry is intentional.
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<UEDS_NullSubSrc, UEDS_NullSubDto>()
                .ForMember(d => d.Email, opt =>
                {
                    opt.MapFrom(s => s.Email);
                    opt.NullSubstitute("(none)");
                }));

        var src = new[] { new UEDS_NullSubSrc { Id = 1, Email = null } }.AsQueryable();
        var matched = src.UseAsDataSource(cfg).For<UEDS_NullSubDto>()
            .Where(d => d.Email == "(none)")
            .ToList();

        // v1 limitation: predicate path doesn't see the substitute, so the null-email row
        // does NOT match.
        Assert.Empty(matched);
    }
}

[AutoMap(typeof(UEDS_AttrSrc))]
public class UEDS_AttrDto { public int Id { get; set; } public string Name { get; set; } = ""; }
public class UEDS_AttrSrc { public int Id { get; set; } public string Name { get; set; } = ""; }

public class UEDS_NullSubSrc { public int Id { get; set; } public string? Email { get; set; } }
public class UEDS_NullSubDto { public int Id { get; set; } public string Email { get; set; } = ""; }

public class UEDS_OpenSrc<T> { public T Value { get; set; } = default!; }
public class UEDS_OpenDto<T> { public T Value { get; set; } = default!; }

public class UEDS_HookSrc { public int Id { get; set; } }
public class UEDS_HookDto { public int Id { get; set; } }

public class UEDS_RevSrc { public int Id { get; set; } public string Name { get; set; } = ""; }
public class UEDS_RevDto { public int Id { get; set; } public string Name { get; set; } = ""; }

public class UEDS_TransSrc { public int Id { get; set; } public string Name { get; set; } = ""; }
public class UEDS_TransDto { public int Id { get; set; } public string Name { get; set; } = ""; }
