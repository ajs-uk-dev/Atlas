using Atlas;
using Atlas.Configuration;
using Atlas.Internal;

namespace Atlas.Tests;

public class MappingExpressionTests
{
    private static MappingExpression<TSource, TDestination> CreateExpr<TSource, TDestination>()
    {
        var expr = new MapperConfigurationExpression();
        var mapping = expr.CreateMap<TSource, TDestination>();
        return (MappingExpression<TSource, TDestination>)mapping;
    }

    private static PropertyMap PropMap<TDest>(TypeMap typeMap, string memberName) =>
        typeMap.PropertyMaps.Single(p => p.Name == memberName);

    [Fact]
    public void ForMember_MapFromExpression_PropertyMapHasExpression()
    {
        var mapping = CreateExpr<MeSrc, MeDst>();

        mapping.ForMember(d => d.FullName, opt => opt.MapFrom(s => s.First + " " + s.Last));

        var pm = PropMap<MeDst>(mapping.TypeMap, nameof(MeDst.FullName));
        Assert.NotNull(pm.CustomExpression);
        Assert.False(pm.HasConstant);
        Assert.False(pm.Ignored);
    }

    [Fact]
    public void ForMember_MapFromConstant_PropertyMapHasConstant()
    {
        var mapping = CreateExpr<MeSrc, MeDst>();

        mapping.ForMember(d => d.FullName, opt => opt.MapFrom("constant"));

        var pm = PropMap<MeDst>(mapping.TypeMap, nameof(MeDst.FullName));
        Assert.True(pm.HasConstant);
        Assert.Equal("constant", pm.ConstantValue);
        Assert.Null(pm.CustomExpression);
    }

    [Fact]
    public void ForMember_Ignore_PropertyMapIsIgnored()
    {
        var mapping = CreateExpr<MeSrc, MeDst>();

        mapping.ForMember(d => d.FullName, opt => opt.Ignore());

        var pm = PropMap<MeDst>(mapping.TypeMap, nameof(MeDst.FullName));
        Assert.True(pm.Ignored);
        Assert.False(pm.HasConstant);
        Assert.Null(pm.CustomExpression);
    }

    [Fact]
    public void ForMember_OnUnknownDestinationMember_Throws()
    {
        var mapping = CreateExpr<MeSrc, MeDst>();

        // A method-call expression instead of a member access is not a valid destination selector.
        Assert.Throws<ArgumentException>(() =>
            mapping.ForMember(d => d.FullName.Substring(0), opt => opt.MapFrom("x")));
    }

    [Fact]
    public void ForCtorParam_NamedParam_RegistersCtorMap()
    {
        var mapping = CreateExpr<MeSrc, RecordDst>();

        mapping.ForCtorParam("displayName", opt => opt.MapFrom(s => s.First));

        Assert.Single(mapping.TypeMap.PropertyMaps);
        var pm = mapping.TypeMap.PropertyMaps[0];
        Assert.Equal("displayName", pm.Name);
        // MapFrom(s => s.First) is a pure member access — stored as SourcePath, not CustomExpression.
        Assert.NotNull(pm.SourcePath);
    }

    [Fact]
    public void ConvertUsing_Generic_RegistersConverter()
    {
        var mapping = CreateExpr<string, int>();

        mapping.ConvertUsing<StringToIntConverter>();

        Assert.NotNull(mapping.TypeMap.CustomConverter);
    }

    [Fact]
    public void ConvertUsing_Lambda_RegistersConverter()
    {
        var mapping = CreateExpr<string, int>();

        mapping.ConvertUsing(s => int.Parse(s));

        Assert.NotNull(mapping.TypeMap.CustomConverter);
    }

    [Fact]
    public void ConvertUsing_AndForMember_OnSameMap_LastWins()
    {
        // Documented v1 behavior: a later ConvertUsing replaces any property-level config
        // (since the converter takes over the entire mapping for the type pair).
        var mapping = CreateExpr<MeSrc, MeDst>();

        mapping.ForMember(d => d.FullName, opt => opt.MapFrom("first"));
        mapping.ConvertUsing(s => new MeDst { FullName = "from-converter" });

        Assert.NotNull(mapping.TypeMap.CustomConverter);
    }

    [Fact]
    public void ForMember_MultipleCallsForSameMember_ReplacesPrevious()
    {
        var mapping = CreateExpr<MeSrc, MeDst>();

        mapping.ForMember(d => d.FullName, opt => opt.MapFrom("first"));
        mapping.ForMember(d => d.FullName, opt => opt.MapFrom("second"));

        var pm = PropMap<MeDst>(mapping.TypeMap, nameof(MeDst.FullName));
        Assert.Equal("second", pm.ConstantValue);
        Assert.Equal(1, mapping.TypeMap.PropertyMaps.Count(p => p.Name == nameof(MeDst.FullName)));
    }

    [Fact]
    public void Builder_AfterMapSealed_Throws()
    {
        var mapping = CreateExpr<MeSrc, MeDst>();
        mapping.TypeMap.Seal();

        Assert.Throws<InvalidOperationException>(() =>
            mapping.ForMember(d => d.FullName, opt => opt.MapFrom("x")));
    }
}

// ---- Test fixtures ----

public class MeSrc
{
    public string First { get; set; } = "";
    public string Last { get; set; } = "";
}

public class MeDst
{
    public string FullName { get; set; } = "";
}

public class RecordDst
{
    public RecordDst(string displayName) { DisplayName = displayName; }
    public string DisplayName { get; }
}

public class StringToIntConverter : ITypeConverter<string, int>
{
    public int Convert(string source, int destination) => int.Parse(source);
}
