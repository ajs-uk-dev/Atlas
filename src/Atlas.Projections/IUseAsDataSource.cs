namespace Atlas.Projections;

/// <summary>
/// Intermediate handle returned by
/// <see cref="UseAsDataSourceExtensions.UseAsDataSource{TSource}"/>. Single method
/// <see cref="For{TDestination}"/> binds the destination type and returns a wrapper
/// presenting destination-typed LINQ operators.
/// </summary>
public interface IUseAsDataSource<TSource>
{
    /// <summary>
    /// Binds the destination type and returns the destination-typed wrapper. The
    /// (TSource, TDestination) pair must be registered with the
    /// <see cref="MapperConfiguration"/> passed to <c>UseAsDataSource</c>; otherwise this
    /// throws <see cref="AtlasProjectionException"/> at the FIRST translated operator call
    /// (Task 9 / Task 10 wraps each translation in a Phase 1 check).
    /// </summary>
    IUseAsDataSourceQueryable<TSource, TDestination> For<TDestination>();
}
