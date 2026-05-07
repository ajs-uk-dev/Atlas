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

public class ExpressionTranslatorFlatteningTests
{
    [Fact]
    public void FlattenedMember_ResolvesViaMultiSegmentSourcePath()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<UEDS_FlattenSrc, UEDS_FlattenDto>());
        Expression<Func<UEDS_FlattenDto, bool>> predicate = d => d.CustomerName == "Alice";

        var translated = (Expression<Func<UEDS_FlattenSrc, bool>>)ExpressionTranslator.Translate(
            cfg.Internal_Registry,
            new TypePair(typeof(UEDS_FlattenSrc), typeof(UEDS_FlattenDto)),
            predicate);

        var compiled = translated.Compile();
        Assert.True(compiled(new UEDS_FlattenSrc { Customer = new UEDS_FlattenCustomer { Name = "Alice" } }));
        Assert.False(compiled(new UEDS_FlattenSrc { Customer = new UEDS_FlattenCustomer { Name = "Bob" } }));
    }

    [Fact]
    public void DeepFlattenedMember_ResolvesViaMultiSegmentSourcePath()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<UEDS_DeepFlattenSrc, UEDS_DeepFlattenDto>());
        Expression<Func<UEDS_DeepFlattenDto, bool>> predicate = d => d.CustomerAddressCity == "London";

        var translated = (Expression<Func<UEDS_DeepFlattenSrc, bool>>)ExpressionTranslator.Translate(
            cfg.Internal_Registry,
            new TypePair(typeof(UEDS_DeepFlattenSrc), typeof(UEDS_DeepFlattenDto)),
            predicate);

        var compiled = translated.Compile();
        Assert.True(compiled(new UEDS_DeepFlattenSrc
        {
            Customer = new UEDS_DeepFlattenCustomer { Address = new UEDS_DeepFlattenAddress { City = "London" } }
        }));
        Assert.False(compiled(new UEDS_DeepFlattenSrc
        {
            Customer = new UEDS_DeepFlattenCustomer { Address = new UEDS_DeepFlattenAddress { City = "Paris" } }
        }));
    }

    [Fact]
    public void MethodCallOnTranslatedMember_PassesThrough()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<UEDS_FlattenSrc, UEDS_FlattenDto>());
        Expression<Func<UEDS_FlattenDto, bool>> predicate = d => d.CustomerName.StartsWith("A");

        var translated = (Expression<Func<UEDS_FlattenSrc, bool>>)ExpressionTranslator.Translate(
            cfg.Internal_Registry,
            new TypePair(typeof(UEDS_FlattenSrc), typeof(UEDS_FlattenDto)),
            predicate);

        var compiled = translated.Compile();
        Assert.True(compiled(new UEDS_FlattenSrc { Customer = new UEDS_FlattenCustomer { Name = "Alice" } }));
        Assert.False(compiled(new UEDS_FlattenSrc { Customer = new UEDS_FlattenCustomer { Name = "Bob" } }));
    }
}

public class UEDS_FlattenCustomer { public string Name { get; set; } = ""; }
public class UEDS_FlattenSrc { public UEDS_FlattenCustomer Customer { get; set; } = new(); }
public class UEDS_FlattenDto { public string CustomerName { get; set; } = ""; }

public class UEDS_DeepFlattenAddress { public string City { get; set; } = ""; }
public class UEDS_DeepFlattenCustomer { public UEDS_DeepFlattenAddress Address { get; set; } = new(); }
public class UEDS_DeepFlattenSrc { public UEDS_DeepFlattenCustomer Customer { get; set; } = new(); }
public class UEDS_DeepFlattenDto { public string CustomerAddressCity { get; set; } = ""; }

public class ExpressionTranslatorCustomExpressionTests
{
    [Fact]
    public void CustomExpression_InlinesBodyViaParameterReplacer()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<UEDS_CustomSrc, UEDS_CustomDto>()
                .ForMember(d => d.DisplayName,
                           opt => opt.MapFrom(s => s.FirstName + " " + s.LastName)));
        Expression<Func<UEDS_CustomDto, bool>> predicate = d => d.DisplayName.Contains("Alice");

        var translated = (Expression<Func<UEDS_CustomSrc, bool>>)ExpressionTranslator.Translate(
            cfg.Internal_Registry,
            new TypePair(typeof(UEDS_CustomSrc), typeof(UEDS_CustomDto)),
            predicate);

        var compiled = translated.Compile();
        Assert.True(compiled(new UEDS_CustomSrc { FirstName = "Alice", LastName = "Smith" }));
        Assert.False(compiled(new UEDS_CustomSrc { FirstName = "Bob", LastName = "Smith" }));
    }
}

public class UEDS_CustomSrc
{
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
}
public class UEDS_CustomDto
{
    public string DisplayName { get; set; } = "";
}
