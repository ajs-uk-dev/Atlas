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
    private readonly List<OpenGenericTypeMap> _openGenericMaps = new();
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

    /// <summary>
    /// Registers an open-generic class map. A single registration applies to every closed
    /// instantiation at runtime via lazy materialization.
    /// </summary>
    /// <param name="sourceType">An open generic type definition (e.g., <c>typeof(Source&lt;&gt;)</c>).</param>
    /// <param name="destinationType">An open generic type definition with the same arity.</param>
    /// <param name="memberList">Validation policy for materialized closed pairs. Default <see cref="MemberList.None"/>.</param>
    /// <exception cref="AtlasConfigurationException">
    /// Thrown if either type is not an open generic type definition, or if the source and
    /// destination have different generic arities.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// Thrown if <paramref name="sourceType"/> or <paramref name="destinationType"/> is null.
    /// </exception>
    public IOpenGenericMappingExpression CreateMap(Type sourceType, Type destinationType,
                          MemberList memberList = MemberList.None)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(sourceType);
        ArgumentNullException.ThrowIfNull(destinationType);

        if (!sourceType.IsGenericTypeDefinition)
            throw new AtlasConfigurationException(new List<ConfigurationError>
            {
                new(sourceType, destinationType, "(register)",
                    $"Source must be an open generic type definition; got '{sourceType.Name}'. " +
                    "Use CreateMap<TSource, TDestination>() for closed types.")
            });

        if (!destinationType.IsGenericTypeDefinition)
            throw new AtlasConfigurationException(new List<ConfigurationError>
            {
                new(sourceType, destinationType, "(register)",
                    $"Destination must be an open generic type definition; got '{destinationType.Name}'. " +
                    "Use CreateMap<TSource, TDestination>() for closed types.")
            });

        var sourceArity = sourceType.GetGenericArguments().Length;
        var destArity = destinationType.GetGenericArguments().Length;
        if (sourceArity != destArity)
            throw new AtlasConfigurationException(new List<ConfigurationError>
            {
                new(sourceType, destinationType, "(register)",
                    $"Generic arity mismatch: source has {sourceArity} type parameter(s), destination has {destArity}.")
            });

        var openMap = new OpenGenericTypeMap(
            sourceType,
            destinationType,
            memberList,
            $"CreateMap(typeof({sourceType.Name}), typeof({destinationType.Name}))");

        _openGenericMaps.Add(openMap);
        return new OpenGenericMappingExpression(openMap);
    }

    /// <summary>Read-only snapshot of registered open-generic templates. Used by MapperConfiguration.</summary>
    internal IReadOnlyList<OpenGenericTypeMap> GetOpenGenericMaps() => _openGenericMaps;

    private void RegisterTypeMap(TypeMap newTm)
    {
        if (_typeMaps.TryGetValue(newTm.Pair, out var existing))
        {
            throw new AtlasConfigurationException(new List<ConfigurationError>
            {
                new(newTm.SourceType, newTm.DestinationType, "(register)",
                    $"Type pair ({newTm.SourceType.Name}, {newTm.DestinationType.Name}) is registered twice: " +
                    $"{existing.RegistrationOrigin} and {newTm.RegistrationOrigin}. " +
                    $"Pick one — every (TSource, TDestination) pair must have a single registration.")
            });
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
        foreach (var openMap in profile.GetOpenGenericMaps())
        {
            _openGenericMaps.Add(openMap);
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
            foreach (var openMap in profile.GetOpenGenericMaps())
            {
                _openGenericMaps.Add(openMap);
            }
        }
    }

    /// <summary>Read-only snapshot of registered type-maps. Used by tests and by MapperConfiguration.</summary>
    internal IReadOnlyList<TypeMap> GetTypeMaps() => _typeMaps.Values.ToList();
}
