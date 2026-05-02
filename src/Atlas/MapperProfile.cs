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
        var map = new TypeMap(typeof(TSource), typeof(TDestination), memberList);
        _typeMaps.Add(map);
        return new MappingExpression<TSource, TDestination>(map);
    }

    /// <summary>Used by <see cref="MapperConfigurationExpression"/> to harvest the registered maps.</summary>
    internal IReadOnlyList<TypeMap> GetTypeMaps() => _typeMaps;

    public NamingConvention? SourceMemberNamingConvention { get; protected set; }
    public NamingConvention? DestinationMemberNamingConvention { get; protected set; }
    public bool? CaseSensitive { get; protected set; }
}
