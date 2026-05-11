using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Tests;

public class AttributeFluentInteractionTests
{
    [Fact]
    public void AddMaps_DiscoversAttributeDecoratedType()
    {
        // Use a configure callback that ONLY registers what we need, avoiding the
        // bad-fixture pollution from a full-assembly scan.
        var cfg = new MapperConfiguration(c =>
        {
            try { c.AddMaps(typeof(InteractionDtoA).Assembly); }
            catch (AtlasConfigurationException) { /* swallow assembly-wide errors from sibling fixtures */ }
        });
        var mapper = cfg.CreateMapper();
        var dto = mapper.Map<InteractionDtoA>(new InteractionSrcA { Id = 5 });
        Assert.Equal(5, dto.Id);
    }

    [Fact]
    public void AddAtlas_ConfigureCallback_AddMapsInside_DiscoversAttribute()
    {
        // Uses the configure-callback overload with an inner AddMaps call. The callback
        // tolerates bad-fixture pollution from sibling fixtures via try/catch.
        var services = new ServiceCollection();
        services.AddAtlas(c =>
        {
            try { c.AddMaps(typeof(InteractionDtoA).Assembly); }
            catch (AtlasConfigurationException) { }
        });
        using var sp = services.BuildServiceProvider();
        var mapper = sp.GetRequiredService<IMapper>();
        var dto = mapper.Map<InteractionDtoA>(new InteractionSrcA { Id = 5 });
        Assert.Equal(5, dto.Id);
    }

    [Fact]
    public void AddAtlas_NoConfigure_RoutesAttributeDiscoveryThroughAddMaps()
    {
        // Exercises the no-configure overload services.AddAtlas(asm). Before the C1 fix this
        // overload silently ignored [Map] types because it bypassed AddMaps. The fix routes
        // the scan through expression.AddMaps(assemblies) which invokes AttributeScanner.Discover.
        //
        // The test assembly contains Task 3 bad fixtures, so attribute discovery throws
        // AtlasConfigurationException. We use this throw as the SIGNAL that the wiring is now
        // active — before C1 fix, this throw would not have occurred (the bad fixtures would
        // have been silently ignored).
        //
        // MapperConfiguration is constructed lazily inside the DI singleton factory, so the
        // throw surfaces at GetRequiredService<IMapper>() time, not at AddAtlas() time.
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        using var sp = services.AddAtlas(typeof(InteractionDtoA).Assembly).BuildServiceProvider();
        Assert.Throws<AtlasConfigurationException>(() => sp.GetRequiredService<IMapper>());
    }

    [Fact]
    public void AttributeAndFluent_SamePair_ThrowsWithBothOrigins()
    {
        var ex = Assert.Throws<AtlasConfigurationException>(() =>
        {
            new MapperConfiguration(c =>
            {
                // Profile registers (InteractionSrcB, InteractionDtoB) via fluent CreateMap.
                // Nested profile — ProfileScanner skips it, so AddMaps below won't re-discover it.
                c.AddProfile(new AttributeFluentInteractionFixtures.InteractionConflictProfile());
                // AddMaps invokes the scanner which tries to register the same pair via [Map].
                c.AddMaps(typeof(InteractionDtoB).Assembly);
            });
        });
        Assert.Contains(ex.Errors, e =>
            e.Reason.Contains("registered twice")
            && e.Reason.Contains(nameof(InteractionSrcB))
            && e.Reason.Contains(nameof(InteractionDtoB)));
    }

    [Fact]
    public void AttributeMap_GlobalTransformer_Fires()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.ValueTransformers.Add<string>(s => s + "!");
            try { c.AddMaps(typeof(InteractionGlobalTransformerDto).Assembly); }
            catch (AtlasConfigurationException) { }
        });
        var mapper = cfg.CreateMapper();
        var dto = mapper.Map<InteractionGlobalTransformerDto>(
            new InteractionGlobalTransformerSource { Name = "Hello" });
        Assert.Equal("Hello!", dto.Name);
    }

    [Fact]
    public void AttributeMap_ProfileScopeTransformer_DoesNotFire()
    {
        // OriginatingProfile is null on attribute-declared TypeMaps (matches DynamicMapping #10).
        var cfg = new MapperConfiguration(c =>
        {
            c.AddProfile(new AttributeFluentInteractionFixtures.InteractionTransformerProfile());
            try { c.AddMaps(typeof(InteractionGlobalTransformerDto).Assembly); }
            catch (AtlasConfigurationException) { }
        });
        var mapper = cfg.CreateMapper();
        var dto = mapper.Map<InteractionGlobalTransformerDto>(
            new InteractionGlobalTransformerSource { Name = "Hello" });
        Assert.Equal("Hello", dto.Name);   // profile transformer did NOT fire
    }

    [Fact]
    public void AttributeAddMaps_ProfileFirst_AttributeSecond_OrderingMatchesIntent()
    {
        var ex = Assert.Throws<AtlasConfigurationException>(() =>
        {
            new MapperConfiguration(c =>
            {
                // Nested profile — ProfileScanner skips nested types, so AddMaps below
                // won't re-discover it. The conflict surfaces at the AttributeScanner level.
                c.AddProfile(new AttributeFluentInteractionFixtures.InteractionConflictProfile());
                c.AddMaps(typeof(InteractionDtoB).Assembly);
            });
        });
        var error = ex.Errors.First(e => e.Reason.Contains("registered twice"));
        // Profile's CreateMap fluent registered first; attribute's [Map] registered second.
        // Verify the existing-origin (profile fluent) precedes the new-origin (attribute) in the message.
        var fluentIdx = error.Reason.IndexOf("CreateMap<");
        var attributeIdx = error.Reason.IndexOf("[Map");
        Assert.True(fluentIdx >= 0, $"Profile origin should appear in error: {error.Reason}");
        Assert.True(attributeIdx >= 0, $"Attribute origin should appear in error: {error.Reason}");
        Assert.True(fluentIdx < attributeIdx,
            $"Profile origin should precede attribute origin. Got: {error.Reason}");
    }

    [Fact]
    public void AttributeReverseMap_CollidesWithExplicitReversePair_ThrowsAtlasConfigException()
    {
        // Profile declares the reverse pair (D_Holistic, S_Holistic) explicitly via fluent.
        // Attribute on D_Holistic declares [Map(typeof(S_Holistic), ReverseMap = true)],
        // which forces the scanner to emit .ReverseMap() that creates (D_Holistic, S_Holistic)
        // — collides with the explicit profile registration.
        var ex = Assert.Throws<AtlasConfigurationException>(() =>
        {
            new MapperConfiguration(c =>
            {
                c.AddProfile(new AttributeFluentInteractionFixtures.HolisticReverseConflictProfile());
                c.AddMaps(typeof(D_Holistic).Assembly);
            });
        });
        // CRITICAL: verify the user sees AtlasConfigurationException, NOT TargetInvocationException.
        // (Before the fix, the reflection invoke would leak a TIE wrapping the AtlasConfigurationException.)
        Assert.IsType<AtlasConfigurationException>(ex);
        Assert.Contains(ex.Errors, e => e.Reason.Contains("registered twice"));
    }
}

public class InteractionSrcA { public int Id { get; set; } }
[Map(typeof(InteractionSrcA))]
public class InteractionDtoA { public int Id { get; set; } }

public class InteractionSrcB { public int Id { get; set; } }
[Map(typeof(InteractionSrcB))]
public class InteractionDtoB { public int Id { get; set; } }

// Nested so ProfileScanner (which skips nested types) does NOT discover it during AddMaps,
// allowing the conflict to manifest at the AttributeScanner level.
public class AttributeFluentInteractionFixtures
{
    public class InteractionConflictProfile : MapperProfile
    {
        public InteractionConflictProfile()
        {
            CreateMap<InteractionSrcB, InteractionDtoB>();
        }
    }

    public class InteractionTransformerProfile : MapperProfile
    {
        public InteractionTransformerProfile()
        {
            ValueTransformers.Add<string>(s => s + "?");
        }
    }

    // Nested so ProfileScanner skips it — only loaded explicitly via AddProfile in the test.
    public class HolisticReverseConflictProfile : MapperProfile
    {
        public HolisticReverseConflictProfile()
        {
            // Explicit reverse pair (D_Holistic, S_Holistic) — collides with what [Map]+ReverseMap produces.
            CreateMap<D_Holistic, S_Holistic>();
        }
    }
}

public class InteractionGlobalTransformerSource { public string Name { get; set; } = ""; }
[Map(typeof(InteractionGlobalTransformerSource))]
public class InteractionGlobalTransformerDto { public string Name { get; set; } = ""; }

public class S_Holistic { public int X { get; set; } }
[Map(typeof(S_Holistic), ReverseMap = true)]
public class D_Holistic { public int X { get; set; } }
