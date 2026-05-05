using System.Linq.Expressions;
using Atlas.Configuration;
using Atlas.Internal;

namespace Atlas.Tests;

public class MappingExpressionConditionTests
{
    public sealed class S { public int V { get; set; } }
    public sealed class D { public int V { get; set; } }

    private static MappingExpression<S, D> NewExpr() =>
        new(new TypeMap(typeof(S), typeof(D), MemberList.None));

    [Fact]
    public void PreCondition_StoredOnPropertyMap()
    {
        var expr = NewExpr();
        Expression<Func<S, bool>> pre = s => s.V > 0;

        expr.ForMember(d => d.V, opt => opt.PreCondition(pre));

        var pm = expr.TypeMap.PropertyMaps.Single(p => p.Name == nameof(D.V));
        Assert.Same(pre, pm.PreCondition);
        Assert.Equal(typeof(bool), pm.PreCondition!.ReturnType);
        Assert.Single(pm.PreCondition!.Parameters);
        Assert.Equal(typeof(S), pm.PreCondition!.Parameters[0].Type);
    }

    [Fact]
    public void Condition_StoredOnPropertyMap()
    {
        var expr = NewExpr();
        Expression<Func<S, int, bool>> cond = (s, v) => v < 100;

        expr.ForMember(d => d.V, opt => opt.Condition(cond));

        var pm = expr.TypeMap.PropertyMaps.Single(p => p.Name == nameof(D.V));
        Assert.Same(cond, pm.Condition);
        Assert.Equal(typeof(bool), pm.Condition!.ReturnType);
        Assert.Equal(2, pm.Condition!.Parameters.Count);
        Assert.Equal(typeof(S), pm.Condition!.Parameters[0].Type);
        Assert.Equal(typeof(int), pm.Condition!.Parameters[1].Type);
    }

    [Fact]
    public void BothPredicates_BothStored()
    {
        var expr = NewExpr();
        Expression<Func<S, bool>> pre = s => s.V > 0;
        Expression<Func<S, int, bool>> cond = (s, v) => v < 100;

        expr.ForMember(d => d.V, opt =>
        {
            opt.PreCondition(pre);
            opt.Condition(cond);
        });

        var pm = expr.TypeMap.PropertyMaps.Single(p => p.Name == nameof(D.V));
        Assert.Same(pre, pm.PreCondition);
        Assert.Same(cond, pm.Condition);
    }

    [Fact]
    public void PreCondition_LastCallWins()
    {
        var expr = NewExpr();
        Expression<Func<S, bool>> first = s => s.V > 0;
        Expression<Func<S, bool>> second = s => s.V > 10;

        expr.ForMember(d => d.V, opt =>
        {
            opt.PreCondition(first);
            opt.PreCondition(second);
        });

        var pm = expr.TypeMap.PropertyMaps.Single(p => p.Name == nameof(D.V));
        Assert.Same(second, pm.PreCondition);
    }

    [Fact]
    public void Condition_LastCallWins()
    {
        var expr = NewExpr();
        Expression<Func<S, int, bool>> first = (s, v) => v < 100;
        Expression<Func<S, int, bool>> second = (s, v) => v < 50;

        expr.ForMember(d => d.V, opt =>
        {
            opt.Condition(first);
            opt.Condition(second);
        });

        var pm = expr.TypeMap.PropertyMaps.Single(p => p.Name == nameof(D.V));
        Assert.Same(second, pm.Condition);
    }

    [Fact]
    public void PreCondition_NullPredicate_Throws()
    {
        var expr = NewExpr();
        Assert.Throws<ArgumentNullException>(() =>
            expr.ForMember(d => d.V, opt => opt.PreCondition(null!)));
    }

    [Fact]
    public void Condition_NullPredicate_Throws()
    {
        var expr = NewExpr();
        Assert.Throws<ArgumentNullException>(() =>
            expr.ForMember(d => d.V, opt => opt.Condition(null!)));
    }
}
