using Atlas.Internal;

namespace Atlas.Tests.Internal;

public class EnumMapConfigTests
{
    public enum SrcEnum { A = 1, B = 2, C = 3 }
    public enum DstEnum { X = 10, Y = 20, Z = 30 }

    [Fact]
    public void SetStrategy_ByValue_FirstCall_Succeeds()
    {
        var cfg = new EnumMapConfig();
        cfg.SetStrategy(EnumMappingStrategy.ByValue, ignoreCase: false);
        Assert.Equal(EnumMappingStrategy.ByValue, cfg.Strategy);
        Assert.False(cfg.IgnoreCase);
        Assert.True(cfg.StrategyExplicitlySet);
    }

    [Fact]
    public void SetStrategy_ByName_AfterByValue_Throws_AtlasConfigurationException()
    {
        var cfg = new EnumMapConfig();
        cfg.SetStrategy(EnumMappingStrategy.ByValue, false);
        var ex = Assert.Throws<AtlasConfigurationException>(() =>
            cfg.SetStrategy(EnumMappingStrategy.ByName, true));
        Assert.Contains("already set", ex.Message);
    }

    [Fact]
    public void SetStrategy_ByValue_AfterByName_Throws()
    {
        var cfg = new EnumMapConfig();
        cfg.SetStrategy(EnumMappingStrategy.ByName, true);
        Assert.Throws<AtlasConfigurationException>(() =>
            cfg.SetStrategy(EnumMappingStrategy.ByValue, false));
    }

    [Fact]
    public void AddOverride_NewKey_Succeeds()
    {
        var cfg = new EnumMapConfig();
        cfg.AddOverride(SrcEnum.A, DstEnum.X);
        Assert.True(cfg.PerValueOverrides.ContainsKey(SrcEnum.A));
        Assert.Equal(DstEnum.X, cfg.PerValueOverrides[SrcEnum.A]);
    }

    [Fact]
    public void AddOverride_DuplicateKey_Throws()
    {
        var cfg = new EnumMapConfig();
        cfg.AddOverride(SrcEnum.A, DstEnum.X);
        var ex = Assert.Throws<AtlasConfigurationException>(() =>
            cfg.AddOverride(SrcEnum.A, DstEnum.Y));
        Assert.Contains("already configured", ex.Message);
    }

    [Fact]
    public void AddOverride_KeyAlreadyIgnored_Throws()
    {
        var cfg = new EnumMapConfig();
        cfg.AddIgnore(SrcEnum.B);
        var ex = Assert.Throws<AtlasConfigurationException>(() =>
            cfg.AddOverride(SrcEnum.B, DstEnum.X));
        Assert.Contains("Ignore", ex.Message);
    }

    [Fact]
    public void AddIgnore_NewValue_Succeeds()
    {
        var cfg = new EnumMapConfig();
        cfg.AddIgnore(SrcEnum.C);
        Assert.Contains((object)SrcEnum.C, cfg.IgnoredSourceValues);
    }

    [Fact]
    public void AddIgnore_ValueAlreadyOverridden_Throws()
    {
        var cfg = new EnumMapConfig();
        cfg.AddOverride(SrcEnum.A, DstEnum.X);
        var ex = Assert.Throws<AtlasConfigurationException>(() =>
            cfg.AddIgnore(SrcEnum.A));
        Assert.Contains("MapValue", ex.Message);
    }

    [Fact]
    public void SetFallback_FirstCall_Succeeds()
    {
        var cfg = new EnumMapConfig();
        cfg.SetFallback(DstEnum.Z);
        Assert.True(cfg.HasFallback);
        Assert.Equal(DstEnum.Z, cfg.FallbackValue);
    }

    [Fact]
    public void SetFallback_SecondCall_Throws()
    {
        var cfg = new EnumMapConfig();
        cfg.SetFallback(DstEnum.X);
        var ex = Assert.Throws<AtlasConfigurationException>(() =>
            cfg.SetFallback(DstEnum.Y));
        Assert.Contains("already set", ex.Message);
    }
}
