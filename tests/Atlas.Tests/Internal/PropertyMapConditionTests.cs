using System.Linq.Expressions;
using Atlas.Internal;

namespace Atlas.Tests.Internal;

public class PropertyMapConditionTests
{
    private sealed class S { public int V { get; set; } }
    private sealed class D { public int V { get; set; } }

    [Fact]
    public void NewPropertyMap_PreCondition_DefaultsToNull()
    {
        var prop = typeof(D).GetProperty(nameof(D.V))!;
        var pm = PropertyMap.ForProperty(prop);

        Assert.Null(pm.PreCondition);
    }

    [Fact]
    public void NewPropertyMap_Condition_DefaultsToNull()
    {
        var prop = typeof(D).GetProperty(nameof(D.V))!;
        var pm = PropertyMap.ForProperty(prop);

        Assert.Null(pm.Condition);
    }

    [Fact]
    public void PropertyMap_AcceptsBothPredicates()
    {
        var prop = typeof(D).GetProperty(nameof(D.V))!;
        var pm = PropertyMap.ForProperty(prop);

        Expression<Func<S, bool>> pre = s => s.V > 0;
        Expression<Func<S, int, bool>> cond = (s, v) => v < 100;

        pm.PreCondition = pre;
        pm.Condition = cond;

        Assert.Same(pre, pm.PreCondition);
        Assert.Same(cond, pm.Condition);
    }
}
