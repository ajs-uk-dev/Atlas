using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Dynamic;
using Atlas.Internal;

namespace Atlas.Tests;

public class DynamicShapeTests
{
    [Fact]
    public void IsDynamicShape_ReturnsTrue_ForIDictionaryStringObject()
    {
        Assert.True(DynamicShape.IsDynamicShape(typeof(IDictionary<string, object>)));
    }

    [Fact]
    public void IsDynamicShape_ReturnsTrue_ForExpandoObject()
    {
        Assert.True(DynamicShape.IsDynamicShape(typeof(ExpandoObject)));
    }

    [Fact]
    public void IsDynamicShape_ReturnsTrue_ForDictionaryStringObject()
    {
        Assert.True(DynamicShape.IsDynamicShape(typeof(Dictionary<string, object>)));
    }

    [Theory]
    [InlineData(typeof(Dictionary<string, int>))]
    [InlineData(typeof(IDictionary<int, object>))]
    [InlineData(typeof(ConcurrentDictionary<string, object>))]
    [InlineData(typeof(List<KeyValuePair<string, object>>))]
    [InlineData(typeof(string))]
    [InlineData(typeof(object))]
    public void IsDynamicShape_ReturnsFalse_ForNonRecognizedTypes(Type t)
    {
        Assert.False(DynamicShape.IsDynamicShape(t));
    }

    [Fact]
    public void IsDynamicPair_TrueWhenSourceDynamicDestinationPoco()
    {
        var pair = new TypePair(typeof(IDictionary<string, object>), typeof(SamplePoco));
        Assert.True(DynamicShape.IsDynamicPair(pair));
    }

    [Fact]
    public void IsDynamicPair_TrueWhenSourcePocoDestinationDynamic()
    {
        var pair = new TypePair(typeof(SamplePoco), typeof(ExpandoObject));
        Assert.True(DynamicShape.IsDynamicPair(pair));
    }

    [Fact]
    public void IsDynamicPair_FalseWhenBothDynamic()
    {
        var pair = new TypePair(typeof(ExpandoObject), typeof(Dictionary<string, object>));
        Assert.False(DynamicShape.IsDynamicPair(pair));
    }

    [Fact]
    public void IsDynamicPair_FalseWhenNeitherDynamic()
    {
        var pair = new TypePair(typeof(SamplePoco), typeof(SampleOtherPoco));
        Assert.False(DynamicShape.IsDynamicPair(pair));
    }

    private sealed class SamplePoco { public int Id { get; set; } }
    private sealed class SampleOtherPoco { public int Id { get; set; } }
}
