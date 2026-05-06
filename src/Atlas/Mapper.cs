using System.Reflection;
using Atlas.Internal;

namespace Atlas;

/// <summary>
/// Default <see cref="IMapper"/> implementation. Thin facade over <see cref="MapperRegistry"/>.
/// </summary>
internal sealed class Mapper : IMapper
{
    private readonly MapperRegistry _registry;

    public Mapper(MapperConfiguration configurationProvider, MapperRegistry registry)
    {
        ConfigurationProvider = configurationProvider;
        _registry = registry;
    }

    public MapperConfiguration ConfigurationProvider { get; }

    public TDestination Map<TSource, TDestination>(TSource source) =>
        MappingInvoker.Invoke<TSource, TDestination>(_registry, source);

    public TDestination Map<TDestination>(object source)
    {
        if (source is null) return default!;
        var srcType = source.GetType();
        // Reflection dispatch — typed overload is the fast path; this one boxes the source.
        var method = typeof(MappingInvoker)
            .GetMethod(nameof(MappingInvoker.Invoke), BindingFlags.Public | BindingFlags.Static)!
            .MakeGenericMethod(srcType, typeof(TDestination));
        try
        {
            return (TDestination)method.Invoke(null, [_registry, source])!;
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw; // unreachable — satisfies compiler
        }
    }

    public void Map<TSource, TDestination>(TSource source, TDestination destination) =>
        MappingInvoker.InvokeUpdate(_registry, source, destination);
}
