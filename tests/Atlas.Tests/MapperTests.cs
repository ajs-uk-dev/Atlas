using Atlas;

namespace Atlas.Tests;

public class MapperTests
{
    private static IMapper BuildMapper(Action<MapperConfigurationExpression> configure)
    {
        var config = new MapperConfiguration(configure);
        config.CompileMappings();
        return config.CreateMapper();
    }

    [Fact]
    public void Map_FlatPocoToFlatPoco_AllPropertiesMapped()
    {
        var mapper = BuildMapper(c => c.CreateMap<MtFlatSrc, MtFlatDst>());

        var dst = mapper.Map<MtFlatSrc, MtFlatDst>(new MtFlatSrc { Id = 7, Name = "Ada" });

        Assert.Equal(7, dst.Id);
        Assert.Equal("Ada", dst.Name);
    }

    [Fact]
    public void Map_NullSource_ReturnsDefault()
    {
        var mapper = BuildMapper(c => c.CreateMap<MtFlatSrc, MtFlatDst>());

        var dst = mapper.Map<MtFlatSrc, MtFlatDst>(null!);

        Assert.Null(dst);
    }

    [Fact]
    public void Map_NestedObject_NestedMapApplied()
    {
        var mapper = BuildMapper(c =>
        {
            c.CreateMap<MtCustomerSrc, MtCustomerDst>();
            c.CreateMap<MtOrderSrc, MtOrderDst>();
        });

        var src = new MtOrderSrc { Customer = new MtCustomerSrc { Name = "Bob" } };
        var dst = mapper.Map<MtOrderSrc, MtOrderDst>(src);

        Assert.NotNull(dst.Customer);
        Assert.Equal("Bob", dst.Customer.Name);
    }

    [Fact]
    public void Map_TwoLevelFlattening_DestinationIsCorrect()
    {
        var mapper = BuildMapper(c => c.CreateMap<MtFlatteningSrc, MtFlatteningDst>());

        var dst = mapper.Map<MtFlatteningSrc, MtFlatteningDst>(
            new MtFlatteningSrc { Customer = new MtCustomer { Name = "Cleo" } });

        Assert.Equal("Cleo", dst.CustomerName);
    }

    [Fact]
    public void Map_NamingConventionTranslation_DestinationIsCorrect()
    {
        var mapper = BuildMapper(c =>
        {
            c.SourceMemberNamingConvention = NamingConvention.SnakeCase;
            c.DestinationMemberNamingConvention = NamingConvention.PascalCase;
            c.CreateMap<MtSnakeSrc, MtPascalDst>();
        });

        var dst = mapper.Map<MtSnakeSrc, MtPascalDst>(new MtSnakeSrc { customer_name = "Dee" });

        Assert.Equal("Dee", dst.CustomerName);
    }

    [Fact]
    public void Map_CustomTypeConverter_AppliedGlobally()
    {
        var mapper = BuildMapper(c => c.CreateMap<string, int>().ConvertUsing(s => int.Parse(s)));

        Assert.Equal(42, mapper.Map<string, int>("42"));
    }

    [Fact]
    public void Map_ConstantMapFrom_DestinationHasConstantValue()
    {
        var mapper = BuildMapper(c =>
            c.CreateMap<MtFlatSrc, MtFlatDst>()
                .ForMember(d => d.Name, o => o.MapFrom("constant")));

        var dst = mapper.Map<MtFlatSrc, MtFlatDst>(new MtFlatSrc { Id = 1, Name = "ignored" });

        Assert.Equal("constant", dst.Name);
    }

    [Fact]
    public void Map_IgnoredMember_KeepsDestinationDefault()
    {
        var mapper = BuildMapper(c =>
            c.CreateMap<MtFlatSrc, MtFlatDst>()
                .ForMember(d => d.Name, o => o.Ignore()));

        var dst = mapper.Map<MtFlatSrc, MtFlatDst>(new MtFlatSrc { Id = 1, Name = "input" });

        Assert.Equal(1, dst.Id);
        Assert.Equal("", dst.Name); // string default for "" -initialized property; Ignore leaves it untouched
    }

    [Fact]
    public void Map_ListSourceToListDestination_ItemsMappedRecursively()
    {
        var mapper = BuildMapper(c =>
        {
            c.CreateMap<MtCustomerSrc, MtCustomerDst>();
            c.CreateMap<List<MtCustomerSrc>, List<MtCustomerDst>>(MemberList.None);
        });

        var src = new List<MtCustomerSrc>
        {
            new() { Name = "A" }, new() { Name = "B" }, new() { Name = "C" }
        };
        var dst = mapper.Map<List<MtCustomerSrc>, List<MtCustomerDst>>(src);

        Assert.Equal(3, dst.Count);
        Assert.Equal("A", dst[0].Name);
        Assert.Equal("C", dst[2].Name);
    }

    [Fact]
    public void Map_ListSourceToArrayDestination_LengthMatches()
    {
        var mapper = BuildMapper(c =>
        {
            c.CreateMap<MtCustomerSrc, MtCustomerDst>();
            c.CreateMap<List<MtCustomerSrc>, MtCustomerDst[]>(MemberList.None);
        });

        var src = new List<MtCustomerSrc> { new() { Name = "x" }, new() { Name = "y" } };
        var dst = mapper.Map<List<MtCustomerSrc>, MtCustomerDst[]>(src);

        Assert.Equal(2, dst.Length);
        Assert.Equal("x", dst[0].Name);
    }

    [Fact]
    public void Map_DictionarySourceToDestination_KeysAndValuesMapped()
    {
        var mapper = BuildMapper(c => c.CreateMap<Dictionary<string, int>, Dictionary<string, int>>(MemberList.None));

        var src = new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 };
        var dst = mapper.Map<Dictionary<string, int>, Dictionary<string, int>>(src);

        Assert.Equal(2, dst.Count);
        Assert.Equal(1, dst["a"]);
        Assert.Equal(2, dst["b"]);
        Assert.NotSame(src, dst); // mapped, not aliased
    }

    [Fact]
    public void Map_RecordDestination_ConstructorBindingByName()
    {
        var mapper = BuildMapper(c => c.CreateMap<MtRecordSrc, MtRecordDst>(MemberList.None));

        var dst = mapper.Map<MtRecordSrc, MtRecordDst>(new MtRecordSrc { DisplayName = "rec" });

        Assert.Equal("rec", dst.DisplayName);
    }

    [Fact]
    public void Map_UpdateInPlace_NullSource_IsNoOp()
    {
        var mapper = BuildMapper(c => c.CreateMap<MtFlatSrc, MtFlatDst>());
        var existing = new MtFlatDst { Id = 99, Name = "kept" };

        mapper.Map<MtFlatSrc, MtFlatDst>(null!, existing);

        Assert.Equal(99, existing.Id);
        Assert.Equal("kept", existing.Name);
    }

    [Fact]
    public void Map_UpdateInPlace_OverwritesScalarsKeepsUntouchedReferences()
    {
        var mapper = BuildMapper(c =>
            c.CreateMap<MtUpdSrc, MtUpdDst>()
                .ForMember(d => d.Untouched, o => o.Ignore()));

        var existing = new MtUpdDst { Id = 1, Name = "old", Untouched = "stay" };
        mapper.Map(new MtUpdSrc { Id = 7, Name = "new" }, existing);

        Assert.Equal(7, existing.Id);
        Assert.Equal("new", existing.Name);
        Assert.Equal("stay", existing.Untouched);
    }

    [Fact]
    public void Map_NullableIntToNullableLong_Widens()
    {
        var mapper = BuildMapper(c => c.CreateMap<MtNullableSrc, MtNullableDst>());
        var dst = mapper.Map<MtNullableSrc, MtNullableDst>(new MtNullableSrc { Value = 42 });
        Assert.Equal(42L, dst.Value);
    }

    [Fact]
    public void Map_NullableIntToNullableLong_NullPreserved()
    {
        var mapper = BuildMapper(c => c.CreateMap<MtNullableSrc, MtNullableDst>());
        var dst = mapper.Map<MtNullableSrc, MtNullableDst>(new MtNullableSrc { Value = null });
        Assert.Null(dst.Value);
    }
}

// ---- Test fixtures ----

public class MtFlatSrc { public int Id { get; set; } public string Name { get; set; } = ""; }
public class MtFlatDst { public int Id { get; set; } public string Name { get; set; } = ""; }

public class MtCustomerSrc { public string Name { get; set; } = ""; }
public class MtCustomerDst { public string Name { get; set; } = ""; }

public class MtOrderSrc { public MtCustomerSrc Customer { get; set; } = new(); }
public class MtOrderDst { public MtCustomerDst Customer { get; set; } = new(); }

public class MtCustomer { public string Name { get; set; } = ""; }
public class MtFlatteningSrc { public MtCustomer Customer { get; set; } = new(); }
public class MtFlatteningDst { public string CustomerName { get; set; } = ""; }

public class MtSnakeSrc { public string customer_name { get; set; } = ""; }
public class MtPascalDst { public string CustomerName { get; set; } = ""; }

public class MtRecordSrc { public string DisplayName { get; set; } = ""; }
public class MtRecordDst { public MtRecordDst(string displayName) { DisplayName = displayName; } public string DisplayName { get; } }

public class MtUpdSrc { public int Id { get; set; } public string Name { get; set; } = ""; }
public class MtUpdDst { public int Id { get; set; } public string Name { get; set; } = ""; public string Untouched { get; set; } = ""; }

public class MtNullableSrc { public int? Value { get; set; } }
public class MtNullableDst { public long? Value { get; set; } }
