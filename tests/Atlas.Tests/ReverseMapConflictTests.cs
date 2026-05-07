namespace Atlas.Tests;

public class ReverseMapConflictTests
{
    public sealed class S { public string? Foo { get; set; } }
    public sealed class D { public string? Foo { get; set; } }

    private sealed class ProfileA : MapperProfile
    {
        public ProfileA() { CreateMap<D, S>(); }
    }

    private sealed class ProfileB : MapperProfile
    {
        public ProfileB() { CreateMap<S, D>().ReverseMap(); }
    }

    private sealed class ProfileSingleConflict : MapperProfile
    {
        public ProfileSingleConflict()
        {
            CreateMap<S, D>().ReverseMap();
            CreateMap<D, S>();    // conflict — registered twice in this profile
        }
    }

    [Fact]
    public void CreateDestSrc_ThenReverseMapOnSrcDest_Throws_NamingBothSites()
    {
        var ex = Assert.Throws<AtlasConfigurationException>(() => new MapperConfiguration(c =>
        {
            c.AddProfile(new ProfileA());     // (D, S) registered, ReverseMapPair = null
            c.AddProfile(new ProfileB());     // (S, D) registered, then (D, S).ReverseMapPair = (S, D)
        }));

        Assert.Contains("CreateMap<D, S>()", ex.Message);
        Assert.Contains("CreateMap<S, D>().ReverseMap()", ex.Message);
    }

    [Fact]
    public void ReverseMapOnSrcDest_ThenCreateDestSrc_Throws_NamingBothSites()
    {
        var ex = Assert.Throws<AtlasConfigurationException>(() => new MapperConfiguration(c =>
        {
            c.AddProfile(new ProfileB());     // (S, D) registered, then (D, S) reverse
            c.AddProfile(new ProfileA());     // (D, S) registered — collides with the reverse
        }));

        Assert.Contains("CreateMap<D, S>()", ex.Message);
        Assert.Contains("CreateMap<S, D>().ReverseMap()", ex.Message);
    }

    [Fact]
    public void ReverseMapTwiceOnSameMap_DoesNotThrow()
    {
        // Idempotency check via the public surface — calling ReverseMap twice with the same MemberList
        // returns the same expression and does NOT register twice (so no conflict).
        var cfg = new MapperConfiguration(c =>
        {
            var fwd = c.CreateMap<S, D>();
            fwd.ReverseMap();
            fwd.ReverseMap();   // returns the same instance; no second register
        });

        // Sanity: there are exactly two TypeMaps registered (forward + one reverse).
        Assert.Equal(2, cfg.Internal_Registry.AllTypeMaps.Count);
    }

    [Fact]
    public void TwoProfilesEachReversingTheSamePair_Throws()
    {
        var ex = Assert.Throws<AtlasConfigurationException>(() => new MapperConfiguration(c =>
        {
            c.AddProfile(new ProfileB());     // (S, D) + (D, S) reverse
            c.AddProfile(new ProfileB());     // again — second (S, D) forward collides under universal rule
        }));

        // Under the universal duplicate-pair rule the collision fires on the forward (S, D) pair
        // when the second ProfileB tries to register CreateMap<S, D> again, before the reverse
        // step runs. Verify the throw names the type pair.
        Assert.Contains("S", ex.Message);
        Assert.Contains("D", ex.Message);
    }

    [Fact]
    public void SingleProfile_DuplicatePair_DetectedAtHarvest()
    {
        var ex = Assert.Throws<AtlasConfigurationException>(() => new MapperConfiguration(c =>
            c.AddProfile(new ProfileSingleConflict())));

        Assert.Contains("CreateMap<D, S>()", ex.Message);
        Assert.Contains("CreateMap<S, D>().ReverseMap()", ex.Message);
    }
}
