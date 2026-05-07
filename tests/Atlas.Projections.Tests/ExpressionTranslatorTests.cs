using System.Linq.Expressions;
using Atlas;
using Atlas.Internal;
using Atlas.Projections.Internal;

namespace Atlas.Projections.Tests;

public class ExpressionTranslatorTests
{
    [Fact]
    public void FlatPropertyTranslation_RewritesParameter()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<UEDS_FlatSrc, UEDS_FlatDto>());
        Expression<Func<UEDS_FlatDto, bool>> predicate = d => d.Total > 100m;

        var translated = (Expression<Func<UEDS_FlatSrc, bool>>)ExpressionTranslator.Translate(
            cfg.Internal_Registry,
            new TypePair(typeof(UEDS_FlatSrc), typeof(UEDS_FlatDto)),
            predicate);

        // Compile + run against an in-memory instance to verify behavior.
        var compiled = translated.Compile();
        Assert.True(compiled(new UEDS_FlatSrc { Total = 150m }));
        Assert.False(compiled(new UEDS_FlatSrc { Total = 50m }));
    }

    [Fact]
    public void FlatPropertyTranslation_ProducesCorrectExpressionShape()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<UEDS_FlatSrc, UEDS_FlatDto>());
        Expression<Func<UEDS_FlatDto, bool>> predicate = d => d.Total > 100m;

        var translated = ExpressionTranslator.Translate(
            cfg.Internal_Registry,
            new TypePair(typeof(UEDS_FlatSrc), typeof(UEDS_FlatDto)),
            predicate);

        // Source-typed lambda
        Assert.Equal(typeof(UEDS_FlatSrc), translated.Parameters[0].Type);
        Assert.Equal(typeof(bool), translated.ReturnType);
    }
}

public class UEDS_FlatSrc
{
    public int Id { get; set; }
    public decimal Total { get; set; }
}

public class UEDS_FlatDto
{
    public int Id { get; set; }
    public decimal Total { get; set; }
}
