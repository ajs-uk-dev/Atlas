namespace Atlas.Tests;

public class EnumExplicitMapTypeGuardTests
{
    public enum MyEnum { A, B }
    public class NotAnEnum { public int Value { get; set; } }

    [Fact]
    public void MapByValue_OnNonEnumSource_Throws_InvalidOperationException()
    {
        var cfg = new MapperConfigurationExpression();
        var map = cfg.CreateMap<NotAnEnum, MyEnum>();
        var ex = Assert.Throws<InvalidOperationException>(() => map.MapByValue());
        Assert.Contains("enum", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MapByName_OnNonEnumDest_Throws_InvalidOperationException()
    {
        var cfg = new MapperConfigurationExpression();
        var map = cfg.CreateMap<MyEnum, NotAnEnum>();
        var ex = Assert.Throws<InvalidOperationException>(() => map.MapByName());
        Assert.Contains("enum", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MapValue_OnNonEnumSource_Throws_InvalidOperationException()
    {
        var cfg = new MapperConfigurationExpression();
        var map = cfg.CreateMap<NotAnEnum, MyEnum>();
        var ex = Assert.Throws<InvalidOperationException>(() => map.MapValue(new NotAnEnum(), MyEnum.A));
        Assert.Contains("enum", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ignore_TSourceOverload_OnNonEnumSource_Throws_InvalidOperationException()
    {
        var cfg = new MapperConfigurationExpression();
        var map = cfg.CreateMap<NotAnEnum, MyEnum>();
        var ex = Assert.Throws<InvalidOperationException>(() => map.Ignore(new NotAnEnum()));
        Assert.Contains("enum", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WithFallback_OnNonEnumDest_Throws_InvalidOperationException()
    {
        var cfg = new MapperConfigurationExpression();
        var map = cfg.CreateMap<MyEnum, NotAnEnum>();
        var ex = Assert.Throws<InvalidOperationException>(() => map.WithFallback(new NotAnEnum()));
        Assert.Contains("enum", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
