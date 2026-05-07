using System.Reflection;

namespace Atlas.Tests;

public class SourceMemberAttributeTests
{
    [Fact]
    public void Ctor_NullName_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SourceMemberAttribute(null!));
    }

    [Fact]
    public void Ctor_NameAssigned()
    {
        var attr = new SourceMemberAttribute("Customer.Name");
        Assert.Equal("Customer.Name", attr.MemberName);
    }

    [Fact]
    public void AttributeUsage_TargetsPropertyOnly_NotInheritedNotMultiple_Sealed()
    {
        var usage = typeof(SourceMemberAttribute).GetCustomAttribute<AttributeUsageAttribute>();
        Assert.NotNull(usage);
        Assert.Equal(AttributeTargets.Property, usage!.ValidOn);
        Assert.False(usage.Inherited);
        Assert.False(usage.AllowMultiple);
        Assert.True(typeof(SourceMemberAttribute).IsSealed);
    }
}
