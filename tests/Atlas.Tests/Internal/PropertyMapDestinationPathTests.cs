using System.Reflection;
using Atlas.Internal;

namespace Atlas.Tests.Internal;

public class PropertyMapDestinationPathTests
{
    private sealed class Outer { public Inner? Child { get; set; } }
    private sealed class Inner { public string? Name { get; set; } }

    [Fact]
    public void ForPath_StoresPathAndLeafInDestinationProperty()
    {
        var childProp = typeof(Outer).GetProperty(nameof(Outer.Child))!;
        var nameProp = typeof(Inner).GetProperty(nameof(Inner.Name))!;
        var path = new[] { childProp, nameProp };

        var pm = PropertyMap.ForPath(path);

        Assert.Equal(path, pm.DestinationPath);
        Assert.Same(nameProp, pm.DestinationProperty);
        Assert.Equal(typeof(string), pm.DestinationType);
    }

    [Fact]
    public void ForPath_NameIsDottedJoin()
    {
        var childProp = typeof(Outer).GetProperty(nameof(Outer.Child))!;
        var nameProp = typeof(Inner).GetProperty(nameof(Inner.Name))!;

        var pm = PropertyMap.ForPath(new[] { childProp, nameProp });

        Assert.Equal("Child.Name", pm.Name);
    }

    [Fact]
    public void ForPath_EmptyPath_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => PropertyMap.ForPath(Array.Empty<PropertyInfo>()));
        Assert.Contains("at least one property", ex.Message);
    }
}
