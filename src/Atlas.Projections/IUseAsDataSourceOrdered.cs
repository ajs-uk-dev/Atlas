using System.Linq.Expressions;

namespace Atlas.Projections;

/// <summary>
/// Ordered wrapper produced by <c>OrderBy</c>/<c>OrderByDescending</c>; adds <c>ThenBy</c>
/// chaining. Inherits the full destination-typed surface so non-ordered operators continue
/// to work after an ordering is applied.
/// </summary>
public interface IUseAsDataSourceOrdered<TSource, TDestination>
    : IUseAsDataSourceQueryable<TSource, TDestination>
{
    IUseAsDataSourceOrdered<TSource, TDestination> ThenBy<TKey>(
        Expression<Func<TDestination, TKey>> keySelector);
    IUseAsDataSourceOrdered<TSource, TDestination> ThenByDescending<TKey>(
        Expression<Func<TDestination, TKey>> keySelector);
}
