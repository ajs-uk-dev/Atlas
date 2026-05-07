using System.Reflection;

namespace Atlas.Tests;

public class NullSubstituteAttributeTests
{
    [Fact]
    public void Ctor_NullValue_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new NullSubstituteAttribute(null!));
    }

    [Fact]
    public void Ctor_ConstantValueAssigned()
    {
        var attr = new NullSubstituteAttribute("(none)");
        Assert.Equal("(none)", attr.ConstantValue);
    }

    [Fact]
    public void AttributeUsage_TargetsPropertyOnly_NotInheritedNotMultiple_Sealed()
    {
        var usage = typeof(NullSubstituteAttribute).GetCustomAttribute<AttributeUsageAttribute>();
        Assert.NotNull(usage);
        Assert.Equal(AttributeTargets.Property, usage!.ValidOn);
        Assert.False(usage.Inherited);
        Assert.False(usage.AllowMultiple);
        Assert.True(typeof(NullSubstituteAttribute).IsSealed);
    }
}
