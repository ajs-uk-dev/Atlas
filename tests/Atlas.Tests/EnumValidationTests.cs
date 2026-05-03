namespace Atlas.Tests;

public class EnumValidationTests
{
    public enum Src { A = 1, B = 2 }
    public enum Dst { X = 1, Y = 2 }
    public enum DstNoZero { X = 1, Y = 2 }   // no defined value for 0
    public enum DstWithZero { X = 0, Y = 1 }

    [Fact]
    public void MapValue_SourceValueNotDefinedOnSourceEnum_AssertConfig_Throws()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<Src, Dst>().MapValue((Src)99, Dst.X));
        var ex = Assert.Throws<AtlasConfigurationException>(() => cfg.AssertConfigurationIsValid());
        Assert.Contains("99", ex.Message);
    }

    [Fact]
    public void MapValue_DestValueNotDefinedOnDestEnum_AssertConfig_Throws()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<Src, Dst>().MapValue(Src.A, (Dst)99));
        Assert.Throws<AtlasConfigurationException>(() => cfg.AssertConfigurationIsValid());
    }

    [Fact]
    public void Ignore_SourceValueNotDefinedOnSourceEnum_AssertConfig_Throws()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<Src, Dst>().Ignore((Src)99));
        Assert.Throws<AtlasConfigurationException>(() => cfg.AssertConfigurationIsValid());
    }

    [Fact]
    public void WithFallback_DestValueNotDefinedOnDestEnum_AssertConfig_Throws()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<Src, Dst>().WithFallback((Dst)99));
        Assert.Throws<AtlasConfigurationException>(() => cfg.AssertConfigurationIsValid());
    }

    [Fact]
    public void Ignore_WhenDefaultDstIsNotDefined_AssertConfig_Throws_TheFootGunGuard()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<Src, DstNoZero>().Ignore(Src.A));
        var ex = Assert.Throws<AtlasConfigurationException>(() => cfg.AssertConfigurationIsValid());
        Assert.Contains("default", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
