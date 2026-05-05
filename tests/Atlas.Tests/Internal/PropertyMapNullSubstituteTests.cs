using System.Linq.Expressions;
using Atlas.Internal;

namespace Atlas.Tests.Internal;

public class PropertyMapNullSubstituteTests
{
    private sealed class S { public string? V { get; set; } }
    private sealed class D { public string V { get; set; } = ""; }

    [Fact]
    public void NewPropertyMap_NullSubstitute_DefaultsToNull()
    {
        var prop = typeof(D).GetProperty(nameof(D.V))!;
        var pm = PropertyMap.ForProperty(prop);

        Assert.Null(pm.NullSubstitute);
    }

    [Fact]
    public void PropertyMap_AcceptsNullSubstituteLambda()
    {
        var prop = typeof(D).GetProperty(nameof(D.V))!;
        var pm = PropertyMap.ForProperty(prop);

        Expression<Func<string>> sub = () => "Unknown";
        pm.NullSubstitute = sub;

        Assert.Same(sub, pm.NullSubstitute);
        Assert.Equal(typeof(string), pm.NullSubstitute!.Body.Type);
    }
}
