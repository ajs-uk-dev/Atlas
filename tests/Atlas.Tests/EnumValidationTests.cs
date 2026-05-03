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

    public enum SrcGap { A = 1, B = 2, C = 3 }
    public enum DstGap { A = 1, B = 2 }   // missing 3 → SrcGap.C uncovered

    [Fact]
    public void EnableEnumMappingValidation_NotCalled_GapsInCoverage_AssertConfig_Passes()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<SrcGap, DstGap>());
        // No EnableEnumMappingValidation, so strict validation is OFF.
        cfg.AssertConfigurationIsValid();   // no throw — gap is allowed without strict mode
    }

    [Fact]
    public void EnableEnumMappingValidation_GapInCoverage_AssertConfig_ThrowsListsAllUncoveredValues()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.EnableEnumMappingValidation();
            c.CreateMap<SrcGap, DstGap>();
        });
        var ex = Assert.Throws<AtlasConfigurationException>(() => cfg.AssertConfigurationIsValid());
        Assert.Contains("C", ex.Message);
    }

    [Fact]
    public void EnableEnumMappingValidation_WithFallback_AllValuesCovered_Passes()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.EnableEnumMappingValidation();
            c.CreateMap<SrcGap, DstGap>().WithFallback(DstGap.A);
        });
        cfg.AssertConfigurationIsValid();   // no throw — fallback covers C
    }

    [Fact]
    public void EnableEnumMappingValidation_RegisteredMapWithNoEnumMethods_DefaultByValueAppliesToValidation()
    {
        // CreateMap with no enum methods → EnumConfig is null → strict validation uses default ByValue.
        // SrcGap.C has no underlying value match in DstGap → uncovered.
        var cfg = new MapperConfiguration(c =>
        {
            c.EnableEnumMappingValidation();
            c.CreateMap<SrcGap, DstGap>();   // no enum methods; null EnumConfig
        });
        var ex = Assert.Throws<AtlasConfigurationException>(() => cfg.AssertConfigurationIsValid());
        Assert.Contains("C", ex.Message);
    }

    [Fact]
    public void EnableEnumMappingValidation_DoesNotValidate_StringToEnumOrEnumToString_AutoConversions()
    {
        // Property-level auto-conversions don't go through registered typemaps → strict validation
        // doesn't apply to them. A DTO with enum→string property mapping shouldn't be validated.
        var cfg = new MapperConfiguration(c =>
        {
            c.EnableEnumMappingValidation();
            c.CreateMap<SrcWithEnumProp, DstWithStringProp>();   // not enum→enum at the typemap level
        });
        cfg.AssertConfigurationIsValid();   // no throw
    }

    public class SrcWithEnumProp { public SrcGap Value { get; set; } }
    public class DstWithStringProp { public string? Value { get; set; } }
}
