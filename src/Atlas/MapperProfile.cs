using Atlas.Configuration;
using Atlas.Internal;

namespace Atlas;

/// <summary>
/// Base class for grouping related mappings. Subclass and call <see cref="CreateMap{TSource, TDestination}"/>
/// from the constructor. Profiles are discovered by assembly scanning and must have a public parameterless ctor.
/// </summary>
public abstract class MapperProfile
{
    private readonly List<TypeMap> _typeMaps = new();
    private readonly List<OpenGenericTypeMap> _openGenericMaps = new();

    protected IMappingExpression<TSource, TDestination> CreateMap<TSource, TDestination>(
        MemberList memberList = MemberList.Destination)
    {
        var map = new TypeMap(typeof(TSource), typeof(TDestination), memberList)
        {
            RegistrationOrigin = $"CreateMap<{typeof(TSource).Name}, {typeof(TDestination).Name}>()",
            OriginatingProfile = this,
        };
        _typeMaps.Add(map);
        return new MappingExpression<TSource, TDestination>(map, _typeMaps.Add);
    }

    /// <summary>
    /// Registers an open-generic class map scoped to this profile. See
    /// <see cref="MapperConfigurationExpression.CreateMap(Type, Type, MemberList)"/> for
    /// full semantics. Profile-level value transformers apply to materialized closed pairs.
    /// </summary>
    /// <exception cref="AtlasConfigurationException">
    /// Thrown if either type is not an open generic type definition, or if the source and
    /// destination have different generic arities.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// Thrown if <paramref name="sourceType"/> or <paramref name="destinationType"/> is null.
    /// </exception>
    protected IOpenGenericMappingExpression CreateMap(Type sourceType, Type destinationType,
                             MemberList memberList = MemberList.None)
    {
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
            $"CreateMap(typeof({sourceType.Name}), typeof({destinationType.Name}))",
            originatingProfile: this);

        _openGenericMaps.Add(openMap);
        return new OpenGenericMappingExpression(openMap);
    }

    /// <summary>Used by <see cref="MapperConfigurationExpression"/> to harvest the registered maps.</summary>
    internal IReadOnlyList<TypeMap> GetTypeMaps() => _typeMaps;

    /// <summary>Used by <see cref="MapperConfigurationExpression"/> to harvest the registered open-generic templates.</summary>
    internal IReadOnlyList<OpenGenericTypeMap> GetOpenGenericMaps() => _openGenericMaps;

    public NamingConvention? SourceMemberNamingConvention { get; protected set; }
    public NamingConvention? DestinationMemberNamingConvention { get; protected set; }
    public bool? CaseSensitive { get; protected set; }

    /// <summary>
    /// Profile-scoped value transformers — apply only to TypeMaps registered in this profile.
    /// See <see cref="MapperConfigurationExpression.ValueTransformers"/> for global scope and
    /// <c>IMappingExpression.AddTransform</c> for type-map scope.
    /// </summary>
    public ValueTransformerCollection ValueTransformers { get; } = new();
}
