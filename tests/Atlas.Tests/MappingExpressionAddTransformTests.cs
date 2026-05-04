using System.Linq.Expressions;
using Atlas.Configuration;
using Atlas.Internal;

namespace Atlas.Tests;

public class MappingExpressionAddTransformTests
{
    public sealed class S { public string? V { get; set; } }
    public sealed class D { public string? V { get; set; } }

    private static MappingExpression<S, D> NewExpr() =>
        new(new TypeMap(typeof(S), typeof(D), MemberList.None));

    [Fact]
    public void AddTransform_AppendsToTypeMapTransformers()
    {
        var expr = NewExpr();
        Expression<Func<string, string>> transformer = s => s.Trim();

        expr.AddTransform(transformer);

        Assert.Single(expr.TypeMap.TypeMapTransformers);
        Assert.Single(expr.TypeMap.TypeMapTransformers[typeof(string)]);
        Assert.Same(transformer, expr.TypeMap.TypeMapTransformers[typeof(string)][0]);
    }

    [Fact]
    public void AddTransform_MultipleSameType_PreservesFifoOrder()
    {
        var expr = NewExpr();
        Expression<Func<string, string>> first = s => s + "1";
        Expression<Func<string, string>> second = s => s + "2";
        Expression<Func<string, string>> third = s => s + "3";

        expr.AddTransform(first);
        expr.AddTransform(second);
        expr.AddTransform(third);

        var entries = expr.TypeMap.TypeMapTransformers[typeof(string)];
        Assert.Equal(3, entries.Count);
        Assert.Same(first, entries[0]);
        Assert.Same(second, entries[1]);
        Assert.Same(third, entries[2]);
    }

    [Fact]
    public void AddTransform_NullTransformer_Throws()
    {
        var expr = NewExpr();
        Assert.Throws<ArgumentNullException>(() =>
            expr.AddTransform<string>(null!));
    }

    [Fact]
    public void AddTransform_ReturnsExpression_ForChaining()
    {
        var expr = NewExpr();
        var returned = expr.AddTransform<string>(s => s);

        Assert.Same(expr, returned);
    }
}
