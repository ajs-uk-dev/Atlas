using Atlas.Configuration;
using Atlas.Internal;

namespace Atlas.Tests;

public class MappingExpressionBeforeAfterMapTests
{
    public sealed class S { public string? V { get; set; } }
    public sealed class D { public string? V { get; set; } }

    public sealed class TestAction : IMappingAction<S, D>
    {
        public void Process(S source, D destination) { }
    }

    private static MappingExpression<S, D> NewExpr() =>
        new(new TypeMap(typeof(S), typeof(D), MemberList.None));

    [Fact]
    public void BeforeMap_Lambda_AppendsToBeforeHooks()
    {
        var expr = NewExpr();
        Action<S, D> hook = (s, d) => { };

        expr.BeforeMap(hook);

        Assert.Single(expr.TypeMap.BeforeHooks);
        Assert.Same(hook, expr.TypeMap.BeforeHooks[0].Lambda);
        Assert.Null(expr.TypeMap.BeforeHooks[0].ActionType);
    }

    [Fact]
    public void BeforeMap_ActionType_AppendsToBeforeHooks()
    {
        var expr = NewExpr();

        expr.BeforeMap<TestAction>();

        Assert.Single(expr.TypeMap.BeforeHooks);
        Assert.Equal(typeof(TestAction), expr.TypeMap.BeforeHooks[0].ActionType);
        Assert.Null(expr.TypeMap.BeforeHooks[0].Lambda);
    }

    [Fact]
    public void AfterMap_Lambda_AppendsToAfterHooks()
    {
        var expr = NewExpr();
        Action<S, D> hook = (s, d) => { };

        expr.AfterMap(hook);

        Assert.Single(expr.TypeMap.AfterHooks);
        Assert.Same(hook, expr.TypeMap.AfterHooks[0].Lambda);
    }

    [Fact]
    public void AfterMap_ActionType_AppendsToAfterHooks()
    {
        var expr = NewExpr();

        expr.AfterMap<TestAction>();

        Assert.Single(expr.TypeMap.AfterHooks);
        Assert.Equal(typeof(TestAction), expr.TypeMap.AfterHooks[0].ActionType);
    }

    [Fact]
    public void MultipleBeforeMap_PreservesFifoOrder()
    {
        var expr = NewExpr();
        Action<S, D> first = (s, d) => { };
        Action<S, D> second = (s, d) => { };
        Action<S, D> third = (s, d) => { };

        expr.BeforeMap(first);
        expr.BeforeMap(second);
        expr.BeforeMap(third);

        Assert.Equal(3, expr.TypeMap.BeforeHooks.Count);
        Assert.Same(first, expr.TypeMap.BeforeHooks[0].Lambda);
        Assert.Same(second, expr.TypeMap.BeforeHooks[1].Lambda);
        Assert.Same(third, expr.TypeMap.BeforeHooks[2].Lambda);
    }

    [Fact]
    public void MultipleAfterMap_PreservesFifoOrder()
    {
        var expr = NewExpr();
        Action<S, D> first = (s, d) => { };
        Action<S, D> second = (s, d) => { };

        expr.AfterMap(first);
        expr.AfterMap(second);

        Assert.Equal(2, expr.TypeMap.AfterHooks.Count);
        Assert.Same(first, expr.TypeMap.AfterHooks[0].Lambda);
        Assert.Same(second, expr.TypeMap.AfterHooks[1].Lambda);
    }

    [Fact]
    public void BeforeMap_NullLambda_Throws()
    {
        var expr = NewExpr();
        Assert.Throws<ArgumentNullException>(() => expr.BeforeMap((Action<S, D>)null!));
    }

    [Fact]
    public void BeforeMap_ReturnsExpression_ForChaining()
    {
        var expr = NewExpr();
        var returned = expr.BeforeMap((s, d) => { });

        Assert.Same(expr, returned);
    }
}
