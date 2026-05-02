using Atlas;
using Atlas.Projections;

namespace Atlas.Projections.Tests;

public class ProjectionExtensionsTests
{
    private static MapperConfiguration BuildConfig(Action<MapperConfigurationExpression> configure) =>
        new MapperConfiguration(configure);

    [Fact]
    public void ProjectTo_FlatPair_ReturnsMappedItems()
    {
        var config = BuildConfig(c => c.CreateMap<EFlatSrc, EFlatDst>());
        var src = new[]
        {
            new EFlatSrc { Id = 1, Name = "a" },
            new EFlatSrc { Id = 2, Name = "b" },
        };
        var result = src.AsQueryable().ProjectTo<EFlatDst>(config).ToList();
        Assert.Equal(2, result.Count);
        Assert.Equal("a", result[0].Name);
    }

    [Fact]
    public void ProjectTo_NestedObject_PopulatesNestedMembersCorrectly()
    {
        var config = BuildConfig(c =>
        {
            c.CreateMap<EInnerSrc, EInnerDst>();
            c.CreateMap<EOuterSrc, EOuterDst>();
        });
        var src = new[] { new EOuterSrc { Inner = new EInnerSrc { Name = "n" } } };
        var result = src.AsQueryable().ProjectTo<EOuterDst>(config).ToList();
        Assert.Equal("n", result[0].Inner.Name);
    }

    [Fact]
    public void ProjectTo_NullNestedSource_ReturnsDefaultDestination_NoNRE()
    {
        var config = BuildConfig(c =>
        {
            c.CreateMap<EInnerSrc, EInnerDst>();
            c.CreateMap<EOuterSrc, EOuterDst>();
        });
        var src = new[] { new EOuterSrc { Inner = null! } };
        var result = src.AsQueryable().ProjectTo<EOuterDst>(config).ToList();
        Assert.Null(result[0].Inner);
    }

    [Fact]
    public void ProjectTo_Collection_MappedItemsInOrder()
    {
        var config = BuildConfig(c =>
        {
            c.CreateMap<EInnerSrc, EInnerDst>();
            c.CreateMap<EParentSrc, EParentDst>();
        });
        var src = new[] { new EParentSrc { Items = { new() { Name = "a" }, new() { Name = "b" } } } };
        var result = src.AsQueryable().ProjectTo<EParentDst>(config).ToList();
        Assert.Equal(["a", "b"], result[0].Items.Select(i => i.Name));
    }

    [Fact]
    public void ProjectTo_FilteredQueryThenProjectTo_ReturnsFilteredResults()
    {
        var config = BuildConfig(c => c.CreateMap<EFlatSrc, EFlatDst>());
        var src = new[]
        {
            new EFlatSrc { Id = 1, Name = "a" },
            new EFlatSrc { Id = 2, Name = "b" },
        };
        var result = src.AsQueryable().Where(s => s.Id == 2).ProjectTo<EFlatDst>(config).ToList();
        Assert.Single(result);
        Assert.Equal("b", result[0].Name);
    }

    [Fact]
    public void ProjectTo_TypeConverterPair_Throws_WithDiagnostic()
    {
        var config = BuildConfig(c =>
            c.CreateMap<EFlatSrc, EFlatDst>().ConvertUsing(s => new EFlatDst { Id = s.Id, Name = s.Name }));
        var src = new[] { new EFlatSrc() };
        var ex = Assert.Throws<AtlasProjectionException>(() => src.AsQueryable().ProjectTo<EFlatDst>(config));
        Assert.Contains(ex.Diagnostics, d => d.Member == "(whole map)");
    }

    [Fact]
    public void ProjectTo_MissingMap_Throws_WithDiagnosticListing()
    {
        var config = BuildConfig(c => { });
        var src = new[] { new EFlatSrc() };
        var ex = Assert.Throws<AtlasProjectionException>(() => src.AsQueryable().ProjectTo<EFlatDst>(config));
        Assert.Single(ex.Diagnostics);
    }

    [Fact]
    public void ProjectTo_DepthLimit_TruncatesRecursiveMember_AtMaxDepth()
    {
        var config = BuildConfig(c => c.CreateMap<ENode, ENode>(MemberList.None));
        var src = new[] { new ENode { Next = new ENode { Next = new ENode() } } };
        var result = src.AsQueryable().ProjectTo<ENode>(config, maxDepth: 1).ToList();
        Assert.NotNull(result[0]);
        Assert.Null(result[0].Next);
    }

    [Fact]
    public void ProjectTo_DefaultMaxDepth_IsThree()
    {
        // A 4-deep chain projected with default depth 3: the 4th level becomes default(null).
        var config = BuildConfig(c => c.CreateMap<ENode, ENode>(MemberList.None));
        var deep = new ENode { Next = new ENode { Next = new ENode { Next = new ENode() } } };
        var result = new[] { deep }.AsQueryable().ProjectTo<ENode>(config).ToList();
        Assert.NotNull(result[0].Next);
        Assert.NotNull(result[0].Next!.Next);
        Assert.Null(result[0].Next!.Next!.Next);
    }

    [Fact]
    public void ProjectTo_MaxDepthZero_ThrowsArgumentOutOfRange()
    {
        var config = BuildConfig(c => c.CreateMap<EFlatSrc, EFlatDst>());
        var src = new[] { new EFlatSrc() };
        Assert.Throws<ArgumentOutOfRangeException>(() => src.AsQueryable().ProjectTo<EFlatDst>(config, maxDepth: 0));
    }

    [Fact]
    public void ProjectTo_NumericConversion_IntToLong_ProjectsSuccessfully()
    {
        var config = BuildConfig(c => c.CreateMap<ENumericSrc, ENumericDstLong>());
        var src = new[] { new ENumericSrc { Value = 42 } };
        var result = src.AsQueryable().ProjectTo<ENumericDstLong>(config).ToList();
        Assert.Single(result);
        Assert.Equal(42L, result[0].Value);
    }

    [Fact]
    public void ProjectTo_NumericConversion_ByteToInt_ProjectsSuccessfully()
    {
        var config = BuildConfig(c => c.CreateMap<ENumericByteSrc, ENumericIntDst>());
        var src = new[] { new ENumericByteSrc { Value = 255 } };
        var result = src.AsQueryable().ProjectTo<ENumericIntDst>(config).ToList();
        Assert.Single(result);
        Assert.Equal(255, result[0].Value);
    }

    [Fact]
    public void ProjectTo_NumericConversion_ShortToFloat_ProjectsSuccessfully()
    {
        var config = BuildConfig(c => c.CreateMap<ENumericShortSrc, ENumericFloatDst>());
        var src = new[] { new ENumericShortSrc { Value = 100 } };
        var result = src.AsQueryable().ProjectTo<ENumericFloatDst>(config).ToList();
        Assert.Single(result);
        Assert.Equal(100f, result[0].Value);
    }

    [Fact]
    public void ProjectTo_NumericConversion_NullableIntToNullableLong_ProjectsSuccessfully()
    {
        var config = BuildConfig(c => c.CreateMap<ENumericNullableSrc, ENumericNullableDst>());
        var src = new[] { new ENumericNullableSrc { Value = 123 } };
        var result = src.AsQueryable().ProjectTo<ENumericNullableDst>(config).ToList();
        Assert.Single(result);
        Assert.Equal(123L, result[0].Value);
    }

    [Fact]
    public void ProjectTo_NumericConversion_CharToInt_ProjectsSuccessfully()
    {
        var config = BuildConfig(c => c.CreateMap<ENumericCharSrc, ENumericIntDst2>());
        var src = new[] { new ENumericCharSrc { Value = 'A' } };
        var result = src.AsQueryable().ProjectTo<ENumericIntDst2>(config).ToList();
        Assert.Single(result);
        Assert.Equal((int)'A', result[0].Value);
    }

    [Fact]
    public void ProjectTo_CollectionToArray_ProjectsItemsToArray()
    {
        var config = BuildConfig(c =>
        {
            c.CreateMap<EInnerSrc, EInnerDst>();
            c.CreateMap<EParentSrcArray, EParentDstArray>();
        });
        var src = new[] { new EParentSrcArray { Items = new List<EInnerSrc> { new() { Name = "a" }, new() { Name = "b" } } } };
        var result = src.AsQueryable().ProjectTo<EParentDstArray>(config).ToList();
        Assert.Single(result);
        Assert.Equal(2, result[0].Items.Length);
        Assert.Equal("a", result[0].Items[0].Name);
    }

    [Fact]
    public void ProjectTo_IdentityCollection_ProjectsWithoutElementMap()
    {
        var config = BuildConfig(c => c.CreateMap<EStringCollectionSrc, EStringCollectionDst>());
        var src = new[] { new EStringCollectionSrc { Items = new List<string> { "x", "y" } } };
        var result = src.AsQueryable().ProjectTo<EStringCollectionDst>(config).ToList();
        Assert.Single(result);
        Assert.Equal(["x", "y"], result[0].Items);
    }

    [Fact]
    public void ProjectTo_CollectionToIEnumerable_ProjectsItems()
    {
        var config = BuildConfig(c =>
        {
            c.CreateMap<EInnerSrc, EInnerDst>();
            c.CreateMap<EParentSrcIEnumerable, EParentDstIEnumerable>();
        });
        var src = new[] { new EParentSrcIEnumerable { Items = new List<EInnerSrc> { new() { Name = "test" } } } };
        var result = src.AsQueryable().ProjectTo<EParentDstIEnumerable>(config).ToList();
        Assert.Single(result);
        Assert.Single(result[0].Items);
    }

    [Fact]
    public void ProjectTo_ParameterlessConstructor_ProjectsSuccessfully()
    {
        var config = BuildConfig(c => c.CreateMap<ENoCtorParamSrc, ENoCtorParamDst>());
        var src = new[] { new ENoCtorParamSrc { Name = "test" } };
        var result = src.AsQueryable().ProjectTo<ENoCtorParamDst>(config).ToList();
        Assert.Single(result);
        Assert.Equal("test", result[0].Name);
    }

    [Fact]
    public void ProjectTo_StringIsNotTreatedAsCollection_ProjectsAsProperty()
    {
        var config = BuildConfig(c => c.CreateMap<EStringPropSrc, EStringPropDst>());
        var src = new[] { new EStringPropSrc { Value = "hello" } };
        var result = src.AsQueryable().ProjectTo<EStringPropDst>(config).ToList();
        Assert.Single(result);
        Assert.Equal("hello", result[0].Value);
    }
}

// ---- Test fixtures ----
public class EFlatSrc { public int Id { get; set; } public string Name { get; set; } = ""; }
public class EFlatDst { public int Id { get; set; } public string Name { get; set; } = ""; }
public class EInnerSrc { public string Name { get; set; } = ""; }
public class EInnerDst { public string Name { get; set; } = ""; }
public class EOuterSrc { public EInnerSrc Inner { get; set; } = new(); }
public class EOuterDst { public EInnerDst Inner { get; set; } = new(); }
public class EParentSrc { public List<EInnerSrc> Items { get; set; } = new(); }
public class EParentDst { public List<EInnerDst> Items { get; set; } = new(); }
public class ENode { public ENode? Next { get; set; } }
public class ENumericSrc { public int Value { get; set; } }
public class ENumericDstLong { public long Value { get; set; } }
public class ENumericByteSrc { public byte Value { get; set; } }
public class ENumericIntDst { public int Value { get; set; } }
public class ENumericShortSrc { public short Value { get; set; } }
public class ENumericFloatDst { public float Value { get; set; } }
public class ENumericNullableSrc { public int? Value { get; set; } }
public class ENumericNullableDst { public long? Value { get; set; } }
public class ENumericCharSrc { public char Value { get; set; } }
public class ENumericIntDst2 { public int Value { get; set; } }
public class EParentSrcArray { public List<EInnerSrc> Items { get; set; } = new(); }
public class EParentDstArray { public EInnerDst[] Items { get; set; } = Array.Empty<EInnerDst>(); }
public class EStringCollectionSrc { public List<string> Items { get; set; } = new(); }
public class EStringCollectionDst { public List<string> Items { get; set; } = new(); }
public class EParentSrcIEnumerable { public List<EInnerSrc> Items { get; set; } = new(); }
public class EParentDstIEnumerable { public IEnumerable<EInnerDst> Items { get; set; } = Array.Empty<EInnerDst>(); }
public class ENoCtorParamDst { public string Name { get; set; } = ""; }
public class ENoCtorParamSrc { public string Name { get; set; } = ""; }
public class EStringPropSrc { public string Value { get; set; } = ""; }
public class EStringPropDst { public string Value { get; set; } = ""; }
