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

    /// <summary>
    /// Global value transformers — post-processing functions applied to every value of a
    /// given destination type, regardless of which map produces it. Composed broad-first
    /// (global → profile → type-map) with finer-scope transformers running after broader
    /// ones. Within this scope, transformers run in registration order (FIFO).
    /// </summary>
    /// <remarks>
    /// Transformers are stored as <c>Expression&lt;Func&lt;T, T&gt;&gt;</c> so the same
    /// declaration works for both in-memory <see cref="IMapper.Map{TDestination}"/> (compiled
    /// to a delegate) and <c>query.ProjectTo&lt;T&gt;()</c> (inlined into the projection
    /// lambda for SQL translation by the underlying provider).
    /// </remarks>
    public ValueTransformerCollection ValueTransformers { get; } = new();

    public IMappingExpression<TSource, TDestination> CreateMap<TSource, TDestination>(
        MemberList memberList = MemberList.Destination)
    {
        EnsureMutable();
        var map = new TypeMap(typeof(TSource), typeof(TDestination), memberList)
        {
            RegistrationOrigin = $"CreateMap<{typeof(TSource).Name}, {typeof(TDestination).Name}>()"
        };
        RegisterTypeMap(map);
        return new MappingExpression<TSource, TDestination>(map, RegisterTypeMap);
    }

    private void RegisterTypeMap(TypeMap newTm)
    {
        if (_typeMaps.TryGetValue(newTm.Pair, out var existing))
        {
            var existingIsReverse = existing.ReverseMapPair is not null;
            var newIsReverse = newTm.ReverseMapPair is not null;
            if (existingIsReverse || newIsReverse)
            {
                throw new AtlasConfigurationException(new List<ConfigurationError>
                {
                    new(newTm.SourceType, newTm.DestinationType, "(register)",
                        $"Type pair ({newTm.SourceType.Name}, {newTm.DestinationType.Name}) is registered twice: " +
                        $"{existing.RegistrationOrigin} and {newTm.RegistrationOrigin}. " +
                        $"Pick one — either remove the duplicate, or rely solely on .ReverseMap() to produce the inverse.")
                });
            }
            // Otherwise: preserve v1 last-write-wins behavior (silent overwrite).
        }
        _typeMaps[newTm.Pair] = newTm;
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
            RegisterTypeMap(map);
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
                RegisterTypeMap(map);
            }
        }
    }

    /// <summary>Read-only snapshot of registered type-maps. Used by tests and by MapperConfiguration.</summary>
    internal IReadOnlyList<TypeMap> GetTypeMaps() => _typeMaps.Values.ToList();
}
