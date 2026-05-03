namespace Atlas.Tests;

public class MapperEnumTests
{
    public enum Status { Pending, Active, Cancelled }
    public enum StatusV2 { Pending, Active, Cancelled }
    public enum LegacyStatus { Pending, Active, Internal }

    public class Src { public Status Status { get; set; } public string Name { get; set; } = ""; }
    public class Dst { public Status Status { get; set; } public string Name { get; set; } = ""; }

    public class SrcWithNullable { public Status? Status { get; set; } }
    public class DstWithNullable { public Status? Status { get; set; } }

    public class SrcWithStringStatus { public string Status { get; set; } = ""; }
    public class DstWithEnumStatus { public Status Status { get; set; } }

    public class SrcWithLegacy { public LegacyStatus Status { get; set; } }
    public class DstWithStatusV2 { public StatusV2 Status { get; set; } }

    public class Outer { public Inner? Inner { get; set; } }
    public class Inner { public Status Status { get; set; } }
    public class OuterDst { public InnerDst? Inner { get; set; } }
    public class InnerDst { public Status Status { get; set; } }

    [Fact]
    public void Map_ObjectWithEnumProperty_AutoConverts_SameUnderlyingType()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<Src, Dst>());
        var mapper = cfg.CreateMapper();
        var dst = mapper.Map<Src, Dst>(new Src { Status = Status.Active, Name = "hello" });
        Assert.Equal(Status.Active, dst.Status);
        Assert.Equal("hello", dst.Name);
    }

    [Fact]
    public void Map_ObjectWithNullableEnumProperty_NullSource_PreservesNull()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<SrcWithNullable, DstWithNullable>());
        var mapper = cfg.CreateMapper();
        var dst = mapper.Map<SrcWithNullable, DstWithNullable>(new SrcWithNullable { Status = null });
        Assert.Null(dst.Status);
    }

    [Fact]
    public void Map_ObjectWithStringPropertyToEnumProperty_AutoConvertsViaName()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<SrcWithStringStatus, DstWithEnumStatus>());
        var mapper = cfg.CreateMapper();
        var dst = mapper.Map<SrcWithStringStatus, DstWithEnumStatus>(new SrcWithStringStatus { Status = "Active" });
        Assert.Equal(Status.Active, dst.Status);
    }

    [Fact]
    public void Map_ObjectWithEnumProperty_RegisteredMapWithMapByName_UsesNameStrategy()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap<LegacyStatus, StatusV2>().MapByName();
            c.CreateMap<SrcWithLegacy, DstWithStatusV2>();
        });
        var mapper = cfg.CreateMapper();
        var dst = mapper.Map<SrcWithLegacy, DstWithStatusV2>(new SrcWithLegacy { Status = LegacyStatus.Active });
        Assert.Equal(StatusV2.Active, dst.Status);
    }

    [Fact]
    public void Map_ObjectWithEnumProperty_RegisteredMapWithFallback_UnmatchedUsesFallback()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap<LegacyStatus, StatusV2>().WithFallback(StatusV2.Cancelled);
            c.CreateMap<SrcWithLegacy, DstWithStatusV2>();
        });
        var mapper = cfg.CreateMapper();
        // LegacyStatus.Internal has no matching value in StatusV2 by ByValue → fallback Cancelled.
        var dst = mapper.Map<SrcWithLegacy, DstWithStatusV2>(new SrcWithLegacy { Status = LegacyStatus.Internal });
        Assert.Equal(StatusV2.Cancelled, dst.Status);
    }

    [Fact]
    public void Map_NestedDtoWithEnumProperty_RoutesThroughRegisteredEnumMap()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap<Inner, InnerDst>();
            c.CreateMap<Outer, OuterDst>();
        });
        var mapper = cfg.CreateMapper();
        var dst = mapper.Map<Outer, OuterDst>(new Outer { Inner = new Inner { Status = Status.Cancelled } });
        Assert.NotNull(dst.Inner);
        Assert.Equal(Status.Cancelled, dst.Inner!.Status);
    }
}
