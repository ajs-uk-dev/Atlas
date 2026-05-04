namespace Atlas.Tests;

public class MapperProfileValueTransformersTests
{
    private sealed class EmptyProfile : MapperProfile { }

    private sealed class TrimAndLowerProfile : MapperProfile
    {
        public TrimAndLowerProfile()
        {
            ValueTransformers.Add<string>(s => s.Trim());
            ValueTransformers.Add<string>(s => s.ToLowerInvariant());
        }
    }

    [Fact]
    public void ValueTransformers_PropertyExposedAndAccessible()
    {
        var profile = new EmptyProfile();

        Assert.NotNull(profile.ValueTransformers);
    }

    [Fact]
    public void ValueTransformers_RegisteredInConstructor_Persists()
    {
        var profile = new TrimAndLowerProfile();

        var stringEntries = profile.ValueTransformers.AllTransformers[typeof(string)];
        Assert.Equal(2, stringEntries.Count);
    }
}
