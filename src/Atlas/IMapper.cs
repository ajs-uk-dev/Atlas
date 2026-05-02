namespace Atlas;

/// <summary>
/// The runtime mapping facade. Stateless and thread-safe; one instance per process is sufficient.
/// </summary>
public interface IMapper
{
    /// <summary>Map a source instance to a new destination of <typeparamref name="TDestination"/>.</summary>
    TDestination Map<TDestination>(object source);

    /// <summary>Map a source of <typeparamref name="TSource"/> to a new destination of <typeparamref name="TDestination"/>.</summary>
    TDestination Map<TSource, TDestination>(TSource source);

    /// <summary>Map a source onto an existing destination instance (update-in-place).</summary>
    void Map<TSource, TDestination>(TSource source, TDestination destination);

    /// <summary>The configuration this mapper was built from.</summary>
    MapperConfiguration ConfigurationProvider { get; }
}
