using System.Linq.Expressions;
using Atlas.Internal;

namespace Atlas.Tests.Internal;

public class TransformerResolverTests
{
    public class TestProfile : MapperProfile { }

    private static TypeMap NewTypeMap(MapperProfile? profile = null) =>
        new(typeof(string), typeof(string), MemberList.None) { OriginatingProfile = profile };

    [Fact]
    public void Resolve_GlobalOnly_EffectiveContainsGlobal()
    {
        var global = new ValueTransformerCollection();
        Expression<Func<string, string>> g = s => s + "g";
        global.Add(g);
        var tm = NewTypeMap();

        TransformerResolver.Resolve(new[] { tm }, global);

        var effective = tm.EffectiveTransformers[typeof(string)];
        Assert.Single(effective);
        Assert.Same(g, effective[0]);
    }

    [Fact]
    public void Resolve_ProfileOnly_EffectiveContainsProfile()
    {
        var global = new ValueTransformerCollection();
        var profile = new TestProfile();
        Expression<Func<string, string>> p = s => s + "p";
        profile.ValueTransformers.Add(p);
        var tm = NewTypeMap(profile);

        TransformerResolver.Resolve(new[] { tm }, global);

        var effective = tm.EffectiveTransformers[typeof(string)];
        Assert.Single(effective);
        Assert.Same(p, effective[0]);
    }

    [Fact]
    public void Resolve_TypeMapOnly_EffectiveContainsTypeMap()
    {
        var global = new ValueTransformerCollection();
        var tm = NewTypeMap();
        Expression<Func<string, string>> t = s => s + "t";
        tm.TypeMapTransformers[typeof(string)] = new List<LambdaExpression> { t };

        TransformerResolver.Resolve(new[] { tm }, global);

        var effective = tm.EffectiveTransformers[typeof(string)];
        Assert.Single(effective);
        Assert.Same(t, effective[0]);
    }

    [Fact]
    public void Resolve_AllThreeScopes_ComposedBroadFirst()
    {
        var global = new ValueTransformerCollection();
        Expression<Func<string, string>> g = s => s + "g";
        global.Add(g);

        var profile = new TestProfile();
        Expression<Func<string, string>> p = s => s + "p";
        profile.ValueTransformers.Add(p);

        var tm = NewTypeMap(profile);
        Expression<Func<string, string>> t = s => s + "t";
        tm.TypeMapTransformers[typeof(string)] = new List<LambdaExpression> { t };

        TransformerResolver.Resolve(new[] { tm }, global);

        var effective = tm.EffectiveTransformers[typeof(string)];
        Assert.Equal(3, effective.Count);
        Assert.Same(g, effective[0]);   // global first (broadest)
        Assert.Same(p, effective[1]);
        Assert.Same(t, effective[2]);   // type-map last (narrowest)
    }

    [Fact]
    public void Resolve_NoTransformers_EffectiveEmpty()
    {
        var global = new ValueTransformerCollection();
        var tm = NewTypeMap();

        TransformerResolver.Resolve(new[] { tm }, global);

        Assert.Empty(tm.EffectiveTransformers);
    }

    [Fact]
    public void Resolve_OriginatingProfileNull_OnlyGlobalAndTypeMap()
    {
        var global = new ValueTransformerCollection();
        Expression<Func<string, string>> g = s => s + "g";
        global.Add(g);

        var tm = NewTypeMap(profile: null);   // No profile.
        Expression<Func<string, string>> t = s => s + "t";
        tm.TypeMapTransformers[typeof(string)] = new List<LambdaExpression> { t };

        TransformerResolver.Resolve(new[] { tm }, global);

        var effective = tm.EffectiveTransformers[typeof(string)];
        Assert.Equal(2, effective.Count);
        Assert.Same(g, effective[0]);
        Assert.Same(t, effective[1]);
    }
}
