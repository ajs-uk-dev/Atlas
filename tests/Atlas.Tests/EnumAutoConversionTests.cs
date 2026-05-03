namespace Atlas.Tests;

public class EnumAutoConversionTests
{
    public enum E1 { A = 1, B = 2, C = 3 }
    public enum E2 { A = 1, B = 2 }   // missing C
    public enum EByte : byte { A = 1, B = 2 }
    public enum EInt : int { A = 1, B = 2 }

    public class SrcWithE1 { public E1 Value { get; set; } }
    public class DstWithE2 { public E2 Value { get; set; } }
    public class DstWithString { public string? Value { get; set; } }
    public class SrcWithString { public string? Value { get; set; } }
    public class DstWithE1 { public E1 Value { get; set; } }
    public class DstWithInt { public int Value { get; set; } }
    public class SrcWithEByte { public EByte Value { get; set; } }
    public class DstWithEInt { public EInt Value { get; set; } }
    public class SrcWithNullableE1 { public E1? Value { get; set; } }
    public class DstWithNullableE1 { public E1? Value { get; set; } }

    [Fact]
    public void EnumToEnum_SameUnderlyingType_AllValuesDefinedOnDest_Maps()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<SrcWithE1, DstWithE2>());
        var mapper = cfg.CreateMapper();
        var dst = mapper.Map<SrcWithE1, DstWithE2>(new SrcWithE1 { Value = E1.A });
        Assert.Equal(E2.A, dst.Value);
    }

    [Fact]
    public void EnumToEnum_SourceValueNotDefinedOnDest_ThrowsAtlasMappingException_AtRuntime()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<SrcWithE1, DstWithE2>());
        var mapper = cfg.CreateMapper();
        Assert.Throws<AtlasMappingException>(() =>
            mapper.Map<SrcWithE1, DstWithE2>(new SrcWithE1 { Value = E1.C }));
    }

    [Fact]
    public void EnumToEnum_DifferentUnderlyingTypes_ByteToInt_Maps()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<SrcWithEByte, DstWithEInt>());
        var mapper = cfg.CreateMapper();
        var dst = mapper.Map<SrcWithEByte, DstWithEInt>(new SrcWithEByte { Value = EByte.A });
        Assert.Equal(EInt.A, dst.Value);
    }

    [Fact]
    public void EnumToString_DefinedValue_ReturnsVerbatimMemberName()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<SrcWithE1, DstWithString>());
        var mapper = cfg.CreateMapper();
        var dst = mapper.Map<SrcWithE1, DstWithString>(new SrcWithE1 { Value = E1.A });
        Assert.Equal("A", dst.Value);
    }

    [Fact]
    public void EnumToString_UndefinedValueCastFromInt_ReturnsNull()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<SrcWithE1, DstWithString>());
        var mapper = cfg.CreateMapper();
        var dst = mapper.Map<SrcWithE1, DstWithString>(new SrcWithE1 { Value = (E1)99 });
        Assert.Null(dst.Value);
    }

    [Fact]
    public void StringToEnum_ExactCaseMatch_Maps()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<SrcWithString, DstWithE1>());
        var mapper = cfg.CreateMapper();
        var dst = mapper.Map<SrcWithString, DstWithE1>(new SrcWithString { Value = "A" });
        Assert.Equal(E1.A, dst.Value);
    }

    [Fact]
    public void StringToEnum_CaseMismatch_ThrowsAtlasMappingException()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<SrcWithString, DstWithE1>());
        var mapper = cfg.CreateMapper();
        Assert.Throws<AtlasMappingException>(() =>
            mapper.Map<SrcWithString, DstWithE1>(new SrcWithString { Value = "a" }));
    }

    [Fact]
    public void StringToEnum_UnrecognizedString_ThrowsAtlasMappingException()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<SrcWithString, DstWithE1>());
        var mapper = cfg.CreateMapper();
        Assert.Throws<AtlasMappingException>(() =>
            mapper.Map<SrcWithString, DstWithE1>(new SrcWithString { Value = "Z" }));
    }

    [Fact]
    public void EnumToUnderlyingNumeric_ReturnsUnderlyingInt()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<SrcWithE1, DstWithInt>());
        var mapper = cfg.CreateMapper();
        var dst = mapper.Map<SrcWithE1, DstWithInt>(new SrcWithE1 { Value = E1.B });
        Assert.Equal(2, dst.Value);
    }

    [Fact]
    public void NullableEnum_NullSource_NullableDest_PreservesNull()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<SrcWithNullableE1, DstWithNullableE1>());
        var mapper = cfg.CreateMapper();
        var dst = mapper.Map<SrcWithNullableE1, DstWithNullableE1>(new SrcWithNullableE1 { Value = null });
        Assert.Null(dst.Value);
    }
}
