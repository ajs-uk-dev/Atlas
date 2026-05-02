using Atlas;

namespace Atlas.Tests;

public class ValidationTests
{
    [Fact]
    public void AssertConfigurationIsValid_AllMembersMapped_ReturnsSilently()
    {
        var config = new MapperConfiguration(c => c.CreateMap<VtFlatSrc, VtFlatDst>());

        config.AssertConfigurationIsValid();
    }

    [Fact]
    public void AssertConfigurationIsValid_UnmappedDestinationMember_Throws()
    {
        var config = new MapperConfiguration(c => c.CreateMap<VtFlatSrc, VtExtraDst>());

        var ex = Assert.Throws<AtlasConfigurationException>(() => config.AssertConfigurationIsValid());
        Assert.Contains(ex.Errors, e => e.DestinationMemberName == nameof(VtExtraDst.Extra));
    }

    [Fact]
    public void AssertConfigurationIsValid_ExceptionListsAllErrors_NotJustFirst()
    {
        var config = new MapperConfiguration(c => c.CreateMap<VtFlatSrc, VtTwoMissingDst>());

        var ex = Assert.Throws<AtlasConfigurationException>(() => config.AssertConfigurationIsValid());
        Assert.Equal(2, ex.Errors.Count);
        Assert.Contains(ex.Errors, e => e.DestinationMemberName == nameof(VtTwoMissingDst.Missing1));
        Assert.Contains(ex.Errors, e => e.DestinationMemberName == nameof(VtTwoMissingDst.Missing2));
    }

    [Fact]
    public void AssertConfigurationIsValid_ExplicitIgnore_PassesValidation()
    {
        var config = new MapperConfiguration(c =>
            c.CreateMap<VtFlatSrc, VtExtraDst>()
                .ForMember(d => d.Extra, o => o.Ignore()));

        config.AssertConfigurationIsValid();
    }

    [Fact]
    public void AssertConfigurationIsValid_MemberListNone_SkipsCheck()
    {
        var config = new MapperConfiguration(c => c.CreateMap<VtFlatSrc, VtExtraDst>(MemberList.None));

        config.AssertConfigurationIsValid();
    }

    [Fact]
    public void AssertConfigurationIsValid_MemberListSource_UnconsumedSourceMember_Throws()
    {
        var config = new MapperConfiguration(c => c.CreateMap<VtTwoSrc, VtOneDst>(MemberList.Source));

        var ex = Assert.Throws<AtlasConfigurationException>(() => config.AssertConfigurationIsValid());
        Assert.Contains(ex.Errors, e => e.DestinationMemberName == nameof(VtTwoSrc.Extra));
    }

    [Fact]
    public void AssertConfigurationIsValid_NestedMapMissing_SurfacedAsError()
    {
        var config = new MapperConfiguration(c => c.CreateMap<VtOuterSrc, VtOuterDst>());

        var ex = Assert.Throws<AtlasConfigurationException>(() => config.AssertConfigurationIsValid());
        Assert.Contains(ex.Errors, e => e.DestinationMemberName == nameof(VtOuterDst.Inner));
    }

    [Fact]
    public void AssertConfigurationIsValid_TypeConverterPresent_ValidationPasses()
    {
        var config = new MapperConfiguration(c =>
        {
            c.CreateMap<string, int>().ConvertUsing(s => int.Parse(s));
            c.CreateMap<VtStringSrc, VtIntDst>();
        });

        config.AssertConfigurationIsValid();
    }

    [Fact]
    public void AssertConfigurationIsValid_ImplicitNumericConversion_ValidationPasses()
    {
        var config = new MapperConfiguration(c => c.CreateMap<VtIntSrc, VtLongDst>());

        config.AssertConfigurationIsValid();
    }

    [Fact]
    public void AssertConfigurationIsValid_ExceptionMessageFormat_HasOneLinePerError()
    {
        var config = new MapperConfiguration(c => c.CreateMap<VtFlatSrc, VtTwoMissingDst>());

        var ex = Assert.Throws<AtlasConfigurationException>(() => config.AssertConfigurationIsValid());
        var lines = ex.Message.Split(Environment.NewLine);
        // header line + one line per error
        Assert.Equal(1 + ex.Errors.Count, lines.Length);
        Assert.Contains(nameof(VtTwoMissingDst.Missing1), ex.Message);
        Assert.Contains(nameof(VtTwoMissingDst.Missing2), ex.Message);
    }

    [Fact]
    public void AssertConfigurationIsValid_NullableNumericWidening_PassesValidation()
    {
        var config = new MapperConfiguration(c => c.CreateMap<VtNullableIntSrc, VtNullableLongDst>());
        config.AssertConfigurationIsValid();
    }
}

// ---- Test fixtures ----

public class VtFlatSrc { public int Id { get; set; } public string Name { get; set; } = ""; }
public class VtFlatDst { public int Id { get; set; } public string Name { get; set; } = ""; }
public class VtExtraDst { public int Id { get; set; } public string Name { get; set; } = ""; public string Extra { get; set; } = ""; }
public class VtTwoMissingDst
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Missing1 { get; set; } = "";
    public string Missing2 { get; set; } = "";
}

public class VtTwoSrc { public int Id { get; set; } public string Extra { get; set; } = ""; }
public class VtOneDst { public int Id { get; set; } }

public class VtInnerSrc { public string Name { get; set; } = ""; }
public class VtInnerDst { public string Name { get; set; } = ""; }
public class VtOuterSrc { public VtInnerSrc Inner { get; set; } = new(); }
public class VtOuterDst { public VtInnerDst Inner { get; set; } = new(); }

public class VtStringSrc { public string Value { get; set; } = ""; }
public class VtIntDst { public int Value { get; set; } }

public class VtIntSrc { public int Count { get; set; } }
public class VtLongDst { public long Count { get; set; } }

public class VtNullableIntSrc { public int? Count { get; set; } }
public class VtNullableLongDst { public long? Count { get; set; } }
