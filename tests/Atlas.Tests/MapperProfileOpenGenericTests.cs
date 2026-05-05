using Atlas.Internal;

namespace Atlas.Tests;

public class MapperProfileOpenGenericTests
{
    public class Wrapper<T> { public T Value { get; set; } = default!; }
    public class WrapperDto<T> { public T Value { get; set; } = default!; }

    public sealed class WrapperProfile : MapperProfile
    {
        public WrapperProfile()
        {
            CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>));
            ValueTransformers.Add<string>(s => s == null ? null! : s.Trim());
        }
    }

    [Fact]
    public void CreateMap_OnProfile_StoresWithOriginatingProfile()
    {
        var profile = new WrapperProfile();

        var registrations = profile.GetOpenGenericMaps();

        Assert.Single(registrations);
        Assert.Same(profile, registrations[0].OriginatingProfile);
        Assert.Equal(typeof(Wrapper<>), registrations[0].SourceTypeDefinition);
        Assert.Equal(typeof(WrapperDto<>), registrations[0].DestinationTypeDefinition);
    }

    [Fact]
    public void ProfileValueTransformer_AppliesToMaterializedClosedPair()
    {
        // End-to-end: register profile via AddProfile, materialize Wrapper<string> at runtime,
        // verify the profile-level Trim transformer fires.
        var cfg = new MapperConfiguration(c => c.AddProfile<WrapperProfile>());
        var mapper = cfg.CreateMapper();

        var dto = mapper.Map<WrapperDto<string>>(new Wrapper<string> { Value = "  hello  " });

        Assert.Equal("hello", dto.Value);
    }
}
