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

    /// <summary>Used by <see cref="MapperConfigurationExpression"/> to harvest the registered maps.</summary>
    internal IReadOnlyList<TypeMap> GetTypeMaps() => _typeMaps;

    /// <summary>TEMPORARY STUB — replaced by full implementation in Task 3.</summary>
    internal IReadOnlyList<OpenGenericTypeMap> GetOpenGenericMaps() => Array.Empty<OpenGenericTypeMap>();

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
