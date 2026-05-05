using Atlas.Internal;

namespace Atlas.Tests.Internal;

public class OpenGenericTypeMapTests
{
    public class Wrapper<T> { public T Value { get; set; } = default!; }
    public class WrapperDto<T> { public T Value { get; set; } = default!; }
    public class Other<T> { public T Value { get; set; } = default!; }

    [Fact]
    public void Matches_ArityMatchingClosedPair_ReturnsTrue()
    {
        var template = new OpenGenericTypeMap(
            typeof(Wrapper<>), typeof(WrapperDto<>),
            MemberList.None,
            "CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>))");

        var closedPair = new TypePair(typeof(Wrapper<int>), typeof(WrapperDto<int>));

        Assert.True(template.Matches(closedPair));
    }

    [Fact]
    public void Matches_DifferentGenericTypeDefinitions_ReturnsFalse()
    {
        var template = new OpenGenericTypeMap(
            typeof(Wrapper<>), typeof(WrapperDto<>),
            MemberList.None,
            "CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>))");

        // Source GTD differs.
        var pair1 = new TypePair(typeof(Other<int>), typeof(WrapperDto<int>));
        Assert.False(template.Matches(pair1));

        // Destination GTD differs.
        var pair2 = new TypePair(typeof(Wrapper<int>), typeof(Other<int>));
        Assert.False(template.Matches(pair2));
    }

    [Fact]
    public void Matches_NonConstructedGenericType_ReturnsFalse()
    {
        var template = new OpenGenericTypeMap(
            typeof(Wrapper<>), typeof(WrapperDto<>),
            MemberList.None,
            "CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>))");

        // Source is non-generic.
        var pair1 = new TypePair(typeof(string), typeof(WrapperDto<int>));
        Assert.False(template.Matches(pair1));

        // Destination is non-generic.
        var pair2 = new TypePair(typeof(Wrapper<int>), typeof(string));
        Assert.False(template.Matches(pair2));
    }
}
