using System.Reflection;

namespace Atlas.Tests;

public class AutoMapAttributeTests
{
    [Fact]
    public void Ctor_NullSourceType_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new AutoMapAttribute(null!));
    }

    [Fact]
    public void Ctor_SourceTypeAssigned()
    {
        var attr = new AutoMapAttribute(typeof(string));
        Assert.Equal(typeof(string), attr.SourceType);
    }

    [Fact]
    public void Defaults_MemberListIsDestination_FlagsAreFalse()
    {
        var attr = new AutoMapAttribute(typeof(string));
        Assert.Equal(MemberList.Destination, attr.MemberList);
        Assert.False(attr.ReverseMap);
        Assert.False(attr.PreserveReferences);
    }

    [Fact]
    public void Properties_AreSettable()
    {
        var attr = new AutoMapAttribute(typeof(string))
        {
            MemberList = MemberList.Source,
            ReverseMap = true,
            PreserveReferences = true,
        };
        Assert.Equal(MemberList.Source, attr.MemberList);
        Assert.True(attr.ReverseMap);
        Assert.True(attr.PreserveReferences);
    }

    [Fact]
    public void AttributeUsage_TargetsClassOnly_NotInheritedNotMultiple()
    {
        var usage = typeof(AutoMapAttribute).GetCustomAttribute<AttributeUsageAttribute>();
        Assert.NotNull(usage);
        Assert.Equal(AttributeTargets.Class, usage!.ValidOn);
        Assert.False(usage.Inherited);
        Assert.False(usage.AllowMultiple);
    }

    [Fact]
    public void Sealed()
    {
        Assert.True(typeof(AutoMapAttribute).IsSealed);
    }
}
