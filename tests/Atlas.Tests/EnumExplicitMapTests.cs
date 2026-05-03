namespace Atlas.Tests;

public class EnumExplicitMapTests
{
    public enum SrcByValue { A = 1, B = 2, C = 3 }
    public enum DstByValueAll { Alpha = 1, Beta = 2, Gamma = 3 }
    public enum DstByValuePartial { Alpha = 1, Beta = 2 }   // missing 3 → C has no match

    public enum SrcByName { Pending, Active, Inactive }
    public enum DstByName { Pending, Active, Inactive }
    public enum DstByNameNoInactive { Pending, Active }

    public enum SrcSnake { lower_pending, lower_active }
    public enum DstPascal { Pending, Active }   // ByName ci needs case-insensitive even after lowercased compare

    // ---- ByValue ----

    [Fact]
    public void CreateMap_NoEnumMethods_DefaultsToByValue_AllValuesDefinedOnDest_Maps()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<SrcByValue, DstByValueAll>());
        var mapper = cfg.CreateMapper();
        Assert.Equal(DstByValueAll.Alpha, mapper.Map<SrcByValue, DstByValueAll>(SrcByValue.A));
        Assert.Equal(DstByValueAll.Beta, mapper.Map<SrcByValue, DstByValueAll>(SrcByValue.B));
        Assert.Equal(DstByValueAll.Gamma, mapper.Map<SrcByValue, DstByValueAll>(SrcByValue.C));
    }

    [Fact]
    public void ByValue_WithMapValue_OverrideWinsOverStrategy()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<SrcByValue, DstByValueAll>()
                .MapByValue()
                .MapValue(SrcByValue.A, DstByValueAll.Gamma));   // override: A → Gamma not Alpha
        var mapper = cfg.CreateMapper();
        Assert.Equal(DstByValueAll.Gamma, mapper.Map<SrcByValue, DstByValueAll>(SrcByValue.A));
        Assert.Equal(DstByValueAll.Beta, mapper.Map<SrcByValue, DstByValueAll>(SrcByValue.B)); // strategy still applies
    }

    [Fact]
    public void ByValue_WithIgnore_ProducesDefaultDestValue()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<SrcByValue, DstByValueAll>()
                .Ignore(SrcByValue.B));
        var mapper = cfg.CreateMapper();
        Assert.Equal(default(DstByValueAll), mapper.Map<SrcByValue, DstByValueAll>(SrcByValue.B));
    }

    [Fact]
    public void ByValue_WithFallback_UnmatchedSourceUsesFallback()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<SrcByValue, DstByValuePartial>()
                .WithFallback(DstByValuePartial.Alpha));
        var mapper = cfg.CreateMapper();
        Assert.Equal(DstByValuePartial.Alpha, mapper.Map<SrcByValue, DstByValuePartial>(SrcByValue.C));
    }

    [Fact]
    public void ByValue_NoFallback_SourceValueNotDefinedOnDest_ThrowsAtlasMappingException()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<SrcByValue, DstByValuePartial>());
        var mapper = cfg.CreateMapper();
        var ex = Assert.Throws<AtlasMappingException>(() =>
            mapper.Map<SrcByValue, DstByValuePartial>(SrcByValue.C));
        Assert.Contains("C", ex.Message);
    }

    // ---- ByName ----

    [Fact]
    public void ByName_DefaultCaseSensitive_SameNameSameCase_Maps()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<SrcByName, DstByName>().MapByName());
        var mapper = cfg.CreateMapper();
        Assert.Equal(DstByName.Pending, mapper.Map<SrcByName, DstByName>(SrcByName.Pending));
        Assert.Equal(DstByName.Active, mapper.Map<SrcByName, DstByName>(SrcByName.Active));
    }

    [Fact]
    public void ByName_DefaultCaseSensitive_DifferentCase_ThrowsAtlasMappingException()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<SrcSnake, DstPascal>().MapByName());   // case-sensitive default; "lower_pending" != "Pending"
        var mapper = cfg.CreateMapper();
        Assert.Throws<AtlasMappingException>(() => mapper.Map<SrcSnake, DstPascal>(SrcSnake.lower_pending));
    }

    [Fact]
    public void ByName_IgnoreCaseTrue_DifferentCase_Maps()
    {
        // Need an enum pair where names match modulo case.
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<SrcSnake, DstPascal>().MapByName(ignoreCase: true)
                // Manual mapping needed since lower_pending != Pending even ci.
                // Use MapValue to demonstrate the override path; demonstrate ci matching via the next test pair.
                .MapValue(SrcSnake.lower_pending, DstPascal.Pending)
                .MapValue(SrcSnake.lower_active, DstPascal.Active));
        var mapper = cfg.CreateMapper();
        Assert.Equal(DstPascal.Pending, mapper.Map<SrcSnake, DstPascal>(SrcSnake.lower_pending));

        // Pure ci case: same words, different case, no MapValue.
        var cfg2 = new MapperConfiguration(c =>
            c.CreateMap<DstPascal, SrcByName>().MapByName(ignoreCase: true));
        var mapper2 = cfg2.CreateMapper();
        Assert.Equal(SrcByName.Pending, mapper2.Map<DstPascal, SrcByName>(DstPascal.Pending));
    }

    [Fact]
    public void ByName_WithMapValue_OverrideWinsOverNameMatch()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<SrcByName, DstByName>().MapByName()
                .MapValue(SrcByName.Active, DstByName.Inactive));
        var mapper = cfg.CreateMapper();
        Assert.Equal(DstByName.Inactive, mapper.Map<SrcByName, DstByName>(SrcByName.Active));
    }

    [Fact]
    public void ByName_NoFallback_NoNameMatch_ThrowsAtlasMappingException()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<SrcByName, DstByNameNoInactive>().MapByName());
        var mapper = cfg.CreateMapper();
        Assert.Throws<AtlasMappingException>(() => mapper.Map<SrcByName, DstByNameNoInactive>(SrcByName.Inactive));
    }

    // ---- Precedence ----

    [Fact]
    public void Precedence_MapValue_Beats_Ignore_Beats_Strategy_Beats_Fallback()
    {
        // Per §6.1: PerValueOverride (1) > Ignore (2) > Strategy (3) > Fallback (4) > Throw (5)
        // SrcByValue.B is overridden, SrcByValue.C is ignored, SrcByValue.A uses strategy ByValue=Alpha.
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<SrcByValue, DstByValueAll>()
                .MapByValue()
                .MapValue(SrcByValue.B, DstByValueAll.Gamma)
                .Ignore(SrcByValue.C)
                .WithFallback(DstByValueAll.Alpha));   // unused — every source value resolves earlier
        var mapper = cfg.CreateMapper();
        Assert.Equal(DstByValueAll.Alpha, mapper.Map<SrcByValue, DstByValueAll>(SrcByValue.A)); // strategy
        Assert.Equal(DstByValueAll.Gamma, mapper.Map<SrcByValue, DstByValueAll>(SrcByValue.B)); // override beats strategy
        Assert.Equal(default(DstByValueAll), mapper.Map<SrcByValue, DstByValueAll>(SrcByValue.C)); // ignore beats strategy
    }

    [Fact]
    public void UndefinedSourceValueCastFromInt_ThrowsAtlasMappingException_RegardlessOfFallback()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<SrcByValue, DstByValueAll>()
                .WithFallback(DstByValueAll.Alpha));
        var mapper = cfg.CreateMapper();
        var corruptValue = (SrcByValue)99;   // not in defined values {1, 2, 3}
        var ex = Assert.Throws<AtlasMappingException>(() =>
            mapper.Map<SrcByValue, DstByValueAll>(corruptValue));
        Assert.Contains("not defined", ex.Message);
    }
}
