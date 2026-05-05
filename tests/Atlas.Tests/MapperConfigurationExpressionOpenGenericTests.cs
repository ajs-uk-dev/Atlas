using Atlas.Internal;

namespace Atlas.Tests;

public class MapperConfigurationExpressionOpenGenericTests
{
    public class Wrapper<T> { public T Value { get; set; } = default!; }
    public class WrapperDto<T> { public T Value { get; set; } = default!; }
    public class Pair<T1, T2> { }

    [Fact]
    public void CreateMap_StoresOpenGenericRegistration()
    {
        var expr = new MapperConfigurationExpression();

        expr.CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>));

        var registrations = expr.GetOpenGenericMaps();
        Assert.Single(registrations);
        Assert.Equal(typeof(Wrapper<>), registrations[0].SourceTypeDefinition);
        Assert.Equal(typeof(WrapperDto<>), registrations[0].DestinationTypeDefinition);
        Assert.Equal(MemberList.None, registrations[0].MemberList);
    }

    [Fact]
    public void CreateMap_NotAGenericTypeDefinition_ThrowsAtlasConfigurationException()
    {
        var expr = new MapperConfigurationExpression();

        var ex = Assert.Throws<AtlasConfigurationException>(() =>
            expr.CreateMap(typeof(Wrapper<int>), typeof(WrapperDto<>)));

        Assert.Contains("Source", ex.Message);
        Assert.Contains("open generic type definition", ex.Message);
    }

    [Fact]
    public void CreateMap_ArityMismatch_ThrowsAtlasConfigurationException()
    {
        var expr = new MapperConfigurationExpression();

        var ex = Assert.Throws<AtlasConfigurationException>(() =>
            expr.CreateMap(typeof(Wrapper<>), typeof(Pair<,>)));

        Assert.Contains("arity mismatch", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateMap_NullArgs_ThrowsArgumentNullException()
    {
        var expr = new MapperConfigurationExpression();

        Assert.Throws<ArgumentNullException>(() => expr.CreateMap(null!, typeof(WrapperDto<>)));
        Assert.Throws<ArgumentNullException>(() => expr.CreateMap(typeof(Wrapper<>), null!));
    }
}
