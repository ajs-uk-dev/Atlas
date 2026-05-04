using System.Reflection;
using Atlas.Configuration;
using Atlas.Internal;

namespace Atlas;

/// <summary>
/// Root of the fluent configuration surface. Collects type-maps and global settings, then
/// hands the result to the mapper configuration for compilation.
/// </summary>
public sealed class MapperConfigurationExpression
{
    private readonly Dictionary<TypePair, TypeMap> _typeMaps = new();
    private bool _built;

    public NamingConvention SourceMemberNamingConvention { get; set; } = NamingConvention.PascalCase;
    public NamingConvention DestinationMemberNamingConvention { get; set; } = NamingConvention.PascalCase;
    public bool CaseSensitive { get; set; } = true;
    internal bool EnumValidationEnabled { get; private set; }

    /// <summary>
    /// Enables strict source-side enum mapping validation. When enabled,
    /// <see cref="MapperConfiguration.AssertConfigurationIsValid"/> asserts that every defined
    /// source enum value in every registered enum→enum map is covered by MapValue, Ignore,
    /// the strategy, or WithFallback. Disabled by default.
    /// </summary>
    public void EnableEnumMappingValidation() => EnumValidationEnabled = true;

    public IMappingExpression<TSource, TDestination> CreateMap<TSource, TDestination>(
        MemberList memberList = MemberList.Destination)
    {
        EnsureMutable();
        var map = new TypeMap(typeof(TSource), typeof(TDestination), memberList)
        {
            RegistrationOrigin = $"CreateMap<{typeof(TSource).Name}, {typeof(TDestination).Name}>()"
        };
        _typeMaps[map.Pair] = map; // last call wins (Task 7 will route through RegisterTypeMap with the conflict guard)
        return new MappingExpression<TSource, TDestination>(map, tm => _typeMaps[tm.Pair] = tm);
    }

    private void EnsureMutable()
    {
        if (_built)
            throw new InvalidOperationException(
                "MapperConfigurationExpression has already been consumed by a MapperConfiguration; cannot add more maps.");
    }

    internal void MarkBuilt() => _built = true;

    public void AddProfile<TProfile>() where TProfile : MapperProfile, new()
    {
        EnsureMutable();
        AddProfile(new TProfile());
    }

    public void AddProfile(MapperProfile profile)
    {
        EnsureMutable();
        foreach (var map in profile.GetTypeMaps())
        {
            _typeMaps[map.Pair] = map;
        }
    }

    public void AddMaps<TMarker>() => AddMaps(typeof(TMarker).Assembly);

    public void AddMaps(params Assembly[] assemblies)
    {
        EnsureMutable();
        foreach (var profile in ProfileScanner.Discover(assemblies))
        {
            foreach (var map in profile.GetTypeMaps())
            {
                _typeMaps[map.Pair] = map;
            }
        }
    }

    /// <summary>Read-only snapshot of registered type-maps. Used by tests and by MapperConfiguration.</summary>
    internal IReadOnlyList<TypeMap> GetTypeMaps() => _typeMaps.Values.ToList();
}
