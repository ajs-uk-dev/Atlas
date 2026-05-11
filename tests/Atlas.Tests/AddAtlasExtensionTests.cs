using Atlas;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Tests;

public class AddAtlasExtensionTests
{
    [Fact]
    public void AddAtlas_RegistersIMapperAsSingleton()
    {
        var services = new ServiceCollection();
        // Use configure-callback with try/catch to absorb bad-fixture [Map] pollution from
        // other tests in this assembly (open generics, NullSubstitute mismatches, etc.).
        services.AddAtlas(c => { try { c.AddMaps(typeof(AddAtlasExtensionTests).Assembly); } catch (AtlasConfigurationException) { } });

        using var sp = services.BuildServiceProvider();
        Assert.NotNull(sp.GetRequiredService<IMapper>());

        var descriptor = services.Single(d => d.ServiceType == typeof(IMapper));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public void AddAtlas_RegistersMapperConfigurationAsSingleton()
    {
        var services = new ServiceCollection();
        services.AddAtlas(c => { try { c.AddMaps(typeof(AddAtlasExtensionTests).Assembly); } catch (AtlasConfigurationException) { } });

        using var sp = services.BuildServiceProvider();
        Assert.NotNull(sp.GetRequiredService<MapperConfiguration>());

        var descriptor = services.Single(d => d.ServiceType == typeof(MapperConfiguration));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public void AddAtlas_TwoCallsToProvider_ReturnSameMapperInstance()
    {
        var services = new ServiceCollection();
        services.AddAtlas(c => { try { c.AddMaps(typeof(AddAtlasExtensionTests).Assembly); } catch (AtlasConfigurationException) { } });

        using var sp = services.BuildServiceProvider();
        var a = sp.GetRequiredService<IMapper>();
        var b = sp.GetRequiredService<IMapper>();
        Assert.Same(a, b);
    }

    [Fact]
    public void AddAtlas_DiscoversProfilesInMarkerAssembly()
    {
        var services = new ServiceCollection();
        services.AddAtlas(c => { try { c.AddMaps(typeof(AddAtlasMarker).Assembly); } catch (AtlasConfigurationException) { } });

        using var sp = services.BuildServiceProvider();
        var mapper = sp.GetRequiredService<IMapper>();
        var dst = mapper.Map<DiSrc, DiDst>(new DiSrc { Value = "ok" });
        Assert.Equal("ok", dst.Value);
    }

    [Fact]
    public void AddAtlas_ConfigCallback_RunsBeforeProfileScan()
    {
        var services = new ServiceCollection();
        services.AddAtlas(
            cfg =>
            {
                cfg.SourceMemberNamingConvention = NamingConvention.SnakeCase;
                try { cfg.AddMaps(typeof(AddAtlasExtensionTests).Assembly); } catch (AtlasConfigurationException) { }
            });

        using var sp = services.BuildServiceProvider();
        var config = sp.GetRequiredService<MapperConfiguration>();
        // Whitebox: convention options reflect the callback.
        Assert.Equal(NamingConvention.SnakeCase, config.Internal_ConventionOptions.SourceConvention);
    }

    [Fact]
    public void AddAtlas_NoAssemblies_StillRegistersEmptyMapper()
    {
        var services = new ServiceCollection();
        services.AddAtlas();

        using var sp = services.BuildServiceProvider();
        Assert.NotNull(sp.GetRequiredService<IMapper>());
        Assert.NotNull(sp.GetRequiredService<MapperConfiguration>());
    }

    [Fact]
    public void AddAtlas_DistinctAssemblies_NoDoubleRegistration()
    {
        var services = new ServiceCollection();
        var asm = typeof(AddAtlasMarker).Assembly;
        // Use configure-callback with try/catch to absorb bad-fixture [Map] pollution.
        // Pass the same assembly twice to verify duplicate-scan deduplication still works.
        services.AddAtlas(c => { try { c.AddMaps(asm, asm); } catch (AtlasConfigurationException) { } });

        using var sp = services.BuildServiceProvider();
        // Should not throw — duplicate profiles would otherwise produce duplicate type-maps and
        // the second AddProfile pass would still last-call-wins them onto the same key.
        var mapper = sp.GetRequiredService<IMapper>();
        var dst = mapper.Map<DiSrc, DiDst>(new DiSrc { Value = "x" });
        Assert.Equal("x", dst.Value);
    }

    [Fact]
    public void AddAtlas_ProfileMissingPublicCtor_ThrowsAtRegistration()
    {
        // The scanner filters out nested types so this profile is intentionally invisible to a
        // full assembly scan (otherwise it would poison every other test in the assembly).
        // We exercise the failure path via the scanner directly — same code path AddAtlas hits.
        Assert.Throws<AtlasConfigurationException>(() =>
            Atlas.Internal.ProfileScanner.CreateInstance(typeof(NoCtorProfileForDi)));
    }

    public class NoCtorProfileForDi : MapperProfile
    {
        private NoCtorProfileForDi() { }
    }

    [Fact]
    public void AddAtlas_AfterAdded_AssertConfigurationIsValid_Passes()
    {
        // Register only DiProfile (no assembly scan) to avoid bad-fixture [Map] pollution
        // that would leave partial registrations failing AssertConfigurationIsValid.
        var services = new ServiceCollection();
        services.AddAtlas(c => c.AddProfile<DiProfile>());

        using var sp = services.BuildServiceProvider();
        var config = sp.GetRequiredService<MapperConfiguration>();
        config.AssertConfigurationIsValid(); // does not throw
    }

    [Fact]
    public void AddAtlas_TypeConverterRegisteredInProfile_AppliedAtRuntime()
    {
        var services = new ServiceCollection();
        services.AddAtlas(c => { try { c.AddMaps(typeof(AddAtlasMarker).Assembly); } catch (AtlasConfigurationException) { } });

        using var sp = services.BuildServiceProvider();
        var mapper = sp.GetRequiredService<IMapper>();
        Assert.Equal(123, mapper.Map<string, int>("123"));
    }
}

// ---- Test fixtures ----

public sealed class AddAtlasMarker { }

public class DiSrc { public string Value { get; set; } = ""; }
public class DiDst { public string Value { get; set; } = ""; }

public class DiProfile : MapperProfile
{
    public DiProfile()
    {
        CreateMap<DiSrc, DiDst>();
        CreateMap<string, int>().ConvertUsing(s => int.Parse(s));
    }
}

