using System.Linq.Expressions;
using Atlas.Configuration;
using Atlas.Internal;

namespace Atlas.Tests;

public class MappingExpressionNullSubstituteTests
{
    public sealed class S { public string? Name { get; set; } public int? Score { get; set; } }
    public sealed class D { public string Name { get; set; } = ""; public int Score { get; set; } }

    private static MappingExpression<S, D> NewExpr() =>
        new(new TypeMap(typeof(S), typeof(D), MemberList.None));

    [Fact]
    public void NullSubstitute_ConstantOverload_StoredAsParameterlessLambda()
    {
        var expr = NewExpr();

        expr.ForMember(d => d.Name, opt => opt.NullSubstitute("Unknown"));

        var pm = expr.TypeMap.PropertyMaps.Single(p => p.Name == nameof(D.Name));
        Assert.NotNull(pm.NullSubstitute);
        Assert.Empty(pm.NullSubstitute!.Parameters);
        Assert.Equal(typeof(string), pm.NullSubstitute!.Body.Type);
    }

    [Fact]
    public void NullSubstitute_ExpressionOverload_StoredAsIs()
    {
        var expr = NewExpr();
        Expression<Func<string>> factory = () => "Computed";

        expr.ForMember(d => d.Name, opt => opt.NullSubstitute(factory));

        var pm = expr.TypeMap.PropertyMaps.Single(p => p.Name == nameof(D.Name));
        Assert.Same(factory, pm.NullSubstitute);
    }

    [Fact]
    public void NullSubstitute_ExpressionOverload_NullArg_Throws()
    {
        var expr = NewExpr();
        Assert.Throws<ArgumentNullException>(() =>
            expr.ForMember(d => d.Name, opt =>
                opt.NullSubstitute<string>((Expression<Func<string>>)null!)));
    }

    [Fact]
    public void NullSubstitute_LastCallWins_TwoConstants()
    {
        var expr = NewExpr();

        expr.ForMember(d => d.Name, opt =>
        {
            opt.NullSubstitute("First");
            opt.NullSubstitute("Second");
        });

        var pm = expr.TypeMap.PropertyMaps.Single(p => p.Name == nameof(D.Name));
        Assert.NotNull(pm.NullSubstitute);
        // The lambda body for "Second" must be the surviving substitute.
        var compiled = ((Expression<Func<string>>)pm.NullSubstitute!).Compile();
        Assert.Equal("Second", compiled());
    }

    [Fact]
    public void NullSubstitute_LastCallWins_ConstantThenExpression()
    {
        var expr = NewExpr();
        Expression<Func<string>> factory = () => "FromExpr";

        expr.ForMember(d => d.Name, opt =>
        {
            opt.NullSubstitute("FromConstant");
            opt.NullSubstitute(factory);
        });

        var pm = expr.TypeMap.PropertyMaps.Single(p => p.Name == nameof(D.Name));
        Assert.Same(factory, pm.NullSubstitute);
    }

    [Fact]
    public void NullSubstitute_BodyTypeMatchesGenericArg_NullableValueType()
    {
        var expr = NewExpr();

        expr.ForMember(d => d.Score, opt => opt.NullSubstitute(42));

        var pm = expr.TypeMap.PropertyMaps.Single(p => p.Name == nameof(D.Score));
        Assert.NotNull(pm.NullSubstitute);
        Assert.Equal(typeof(int), pm.NullSubstitute!.Body.Type);
    }
}
