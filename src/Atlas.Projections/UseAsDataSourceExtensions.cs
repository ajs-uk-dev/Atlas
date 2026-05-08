using Atlas.Projections.Internal;

namespace Atlas.Projections;

/// <summary>
/// Entry point for destination-typed-lambda LINQ operators against a source-typed
/// <see cref="IQueryable{TSource}"/>. Translates each operator's destination-typed
/// expression back to source-typed via the configured Atlas typemaps, then applies
/// the underlying LINQ operator to the source query.
/// </summary>
public static class UseAsDataSourceExtensions
{
    public static IUseAsDataSource<TSource> UseAsDataSource<TSource>(
        this IQueryable<TSource> source,
        MapperConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(configuration);
        return new Intermediate<TSource>(source, configuration);
    }

    private sealed class Intermediate<TSource> : IUseAsDataSource<TSource>
    {
        private readonly IQueryable<TSource> _source;
        private readonly MapperConfiguration _configuration;

        public Intermediate(IQueryable<TSource> source, MapperConfiguration configuration)
        {
            _source = source;
            _configuration = configuration;
        }

        public IUseAsDataSourceQueryable<TSource, TDestination> For<TDestination>() =>
            new UseAsDataSourceQueryable<TSource, TDestination>(_source, _configuration);
    }
}
