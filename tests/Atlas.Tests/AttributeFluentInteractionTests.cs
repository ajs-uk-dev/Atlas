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
    public void AddAtlas_DI_DiscoversAttributeDecoratedType()
    {
        // services.AddAtlas internally calls cfg.AddMaps. The build-time exception will be
        // swallowed by the configure callback's try/catch... actually services.AddAtlas does
        // NOT swallow — it calls AddMaps directly. To test DI discovery we need a way to
        // tolerate the bad-fixture exception. Workaround: use a SEPARATE configure callback
        // shape that runs AddMaps inside a try/catch.
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
    public void AttributeAndFluent_SamePair_ThrowsWithBothOrigins()
    {
        var ex = Assert.Throws<AtlasConfigurationException>(() =>
        {
            new MapperConfiguration(c =>
            {
                // Profile registers (InteractionSrcB, InteractionDtoB) via fluent CreateMap.
                // Nested profile — ProfileScanner skips it, so AddMaps below won't re-discover it.
                c.AddProfile(new AttributeFluentInteractionFixtures.InteractionConflictProfile());
                // AddMaps invokes the scanner which tries to register the same pair via [AutoMap].
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
        // Profile's CreateMap fluent registered first; attribute's [AutoMap] registered second.
        // Verify the existing-origin (profile fluent) precedes the new-origin (attribute) in the message.
        var fluentIdx = error.Reason.IndexOf("CreateMap<");
        var attributeIdx = error.Reason.IndexOf("[AutoMap");
        Assert.True(fluentIdx >= 0, $"Profile origin should appear in error: {error.Reason}");
        Assert.True(attributeIdx >= 0, $"Attribute origin should appear in error: {error.Reason}");
        Assert.True(fluentIdx < attributeIdx,
            $"Profile origin should precede attribute origin. Got: {error.Reason}");
    }
}

public class InteractionSrcA { public int Id { get; set; } }
[AutoMap(typeof(InteractionSrcA))]
public class InteractionDtoA { public int Id { get; set; } }

public class InteractionSrcB { public int Id { get; set; } }
[AutoMap(typeof(InteractionSrcB))]
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
}

public class InteractionGlobalTransformerSource { public string Name { get; set; } = ""; }
[AutoMap(typeof(InteractionGlobalTransformerSource))]
public class InteractionGlobalTransformerDto { public string Name { get; set; } = ""; }
