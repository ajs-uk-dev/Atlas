using Atlas;
using Atlas.Internal;

namespace Atlas.Tests;

public class MapperConfigurationExpressionTests
{
    [Fact]
    public void CreateMap_SimpleType_RegistersTypeMap()
    {
        var expr = new MapperConfigurationExpression();
        expr.CreateMap<SrcA, DstA>();

        var maps = expr.GetTypeMaps();
        Assert.Single(maps);
        Assert.Equal(typeof(SrcA), maps[0].SourceType);
        Assert.Equal(typeof(DstA), maps[0].DestinationType);
    }

    [Fact]
    public void CreateMap_Twice_ReplacesPreviousMap()
    {
        var expr = new MapperConfigurationExpression();
        expr.CreateMap<SrcA, DstA>();
        expr.CreateMap<SrcA, DstA>(MemberList.None);

        var maps = expr.GetTypeMaps();
        Assert.Single(maps);
        Assert.Equal(MemberList.None, maps[0].MemberList);
    }

    [Fact]
    public void AddProfile_Generic_InvokesProfileConstructor()
    {
        RecordingProfile.ResetForTest();
        var expr = new MapperConfigurationExpression();
        expr.AddProfile<RecordingProfile>();

        Assert.True(RecordingProfile.WasConstructed);
        Assert.Single(expr.GetTypeMaps());
    }

    [Fact]
    public void AddProfile_Instance_RegistersProfileMaps()
    {
        var expr = new MapperConfigurationExpression();
        var profile = new RecordingProfile();
        expr.AddProfile(profile);

        Assert.Single(expr.GetTypeMaps());
    }

    [Fact]
    public void AddMaps_AssemblyMarker_DiscoversProfiles()
    {
        var expr = new MapperConfigurationExpression();
        expr.AddMaps<MapperConfigurationExpressionTests>();

        // RecordingProfile is a top-level profile in this assembly; nested fixtures (e.g. NoCtorProfile)
        // are deliberately filtered out by the scanner.
        Assert.NotEmpty(expr.GetTypeMaps());
        Assert.Contains(expr.GetTypeMaps(), m =>
            m.SourceType == typeof(SrcA) && m.DestinationType == typeof(DstA));
    }

    [Fact]
    public void AddMaps_AssemblyArray_DistinctAssemblies()
    {
        var expr = new MapperConfigurationExpression();
        var asm = typeof(MapperConfigurationExpressionTests).Assembly;
        expr.AddMaps(asm, asm); // intentional duplicate

        var maps = expr.GetTypeMaps();
        var grouped = maps.GroupBy(m => (m.SourceType, m.DestinationType));
        Assert.All(grouped, g => Assert.Single(g));
    }

    [Fact]
    public void AddMaps_ProfileWithoutPublicCtor_Throws()
    {
        // The scanner's profile-instance creation step is the load-bearing check.
        // A nested fixture exercises it without polluting other tests' assembly scans.
        var ex = Assert.Throws<AtlasConfigurationException>(() =>
            ProfileScanner.CreateInstance(typeof(NoCtorProfile)));

        Assert.Contains("public parameterless constructor", ex.Message);
    }

    [Fact]
    public void Defaults_CaseSensitiveTrue_PascalCaseBothSides()
    {
        var expr = new MapperConfigurationExpression();

        Assert.True(expr.CaseSensitive);
        Assert.Equal(NamingConvention.PascalCase, expr.SourceMemberNamingConvention);
        Assert.Equal(NamingConvention.PascalCase, expr.DestinationMemberNamingConvention);
    }

    // ---- Nested fixture: skipped by scanner due to IsNested filter ----
    public class NoCtorProfile : MapperProfile
    {
        private NoCtorProfile() { }
    }
}

// ---- Top-level test fixtures (these ARE scanned) ----

public class SrcA { public int Id { get; set; } }
public class DstA { public int Id { get; set; } }

public class RecordingProfile : MapperProfile
{
    public static bool WasConstructed { get; private set; }
    public static void ResetForTest() => WasConstructed = false;

    public RecordingProfile()
    {
        WasConstructed = true;
        CreateMap<SrcA, DstA>();
    }
}
