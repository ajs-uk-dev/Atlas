using System.Reflection;

namespace Atlas.Internal;

/// <summary>
/// Discovers <see cref="MapperProfile"/> subclasses across one or more assemblies and
/// instantiates them. Top-level public types only — nested types are intentionally skipped.
/// </summary>
internal static class ProfileScanner
{
    public static IEnumerable<MapperProfile> Discover(IEnumerable<Assembly> assemblies)
    {
        foreach (var asm in assemblies.Distinct())
        {
            foreach (var type in DiscoverProfileTypes(asm))
            {
                yield return CreateInstance(type);
            }
        }
    }

    public static IEnumerable<Type> DiscoverProfileTypes(Assembly asm) =>
        asm.GetTypes().Where(IsProfileCandidate);

    public static MapperProfile CreateInstance(Type type)
    {
        var ctor = type.GetConstructor(Type.EmptyTypes);
        if (ctor is null || !ctor.IsPublic)
        {
            throw new AtlasConfigurationException([
                new ConfigurationError(
                    type, type, "(profile)",
                    $"Profile {type.FullName} requires a public parameterless constructor.")
            ]);
        }
        return (MapperProfile)Activator.CreateInstance(type)!;
    }

    private static bool IsProfileCandidate(Type t) =>
        t.IsClass
        && !t.IsAbstract
        && !t.IsNested
        && typeof(MapperProfile).IsAssignableFrom(t);
}
