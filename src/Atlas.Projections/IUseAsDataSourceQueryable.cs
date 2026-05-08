using System.Linq.Expressions;

namespace Atlas.Projections;

/// <summary>
/// Destination-typed LINQ-operator surface for UseAsDataSource. Each operator accepts
/// a <c>Func&lt;TDestination, ...&gt;</c>-shaped lambda; the wrapper translates the
/// destination-typed expression to a source-typed expression and applies the underlying
/// LINQ operator to the wrapped <see cref="IQueryable{TSource}"/>.
/// </summary>
/// <remarks>
/// Operator scope per design §1 / §2 (v1):
/// <list type="bullet">
///   <item>Filtering: <c>Where</c></item>
///   <item>Ordering: <c>OrderBy</c>, <c>OrderByDescending</c>, <c>ThenBy</c>, <c>ThenByDescending</c></item>
///   <item>Paging: <c>Skip</c>, <c>Take</c></item>
///   <item>Terminal predicate: <c>Any</c>, <c>All</c>, <c>Count(predicate)</c>,
///         <c>First[OrDefault](predicate)</c>, <c>Single[OrDefault](predicate)</c>,
///         <c>Last[OrDefault](predicate)</c></item>
/// </list>
/// </remarks>
public interface IUseAsDataSourceQueryable<TSource, TDestination> : IEnumerable<TDestination>
{
    // Filtering
    IUseAsDataSourceQueryable<TSource, TDestination> Where(
        Expression<Func<TDestination, bool>> predicate);

    // Ordering
    IUseAsDataSourceOrdered<TSource, TDestination> OrderBy<TKey>(
        Expression<Func<TDestination, TKey>> keySelector);
    IUseAsDataSourceOrdered<TSource, TDestination> OrderByDescending<TKey>(
        Expression<Func<TDestination, TKey>> keySelector);

    // Paging
    IUseAsDataSourceQueryable<TSource, TDestination> Skip(int count);
    IUseAsDataSourceQueryable<TSource, TDestination> Take(int count);

    // Terminal predicate
    bool Any();
    bool Any(Expression<Func<TDestination, bool>> predicate);
    bool All(Expression<Func<TDestination, bool>> predicate);
    int Count();
    int Count(Expression<Func<TDestination, bool>> predicate);
    long LongCount();
    long LongCount(Expression<Func<TDestination, bool>> predicate);
    TDestination First();
    TDestination First(Expression<Func<TDestination, bool>> predicate);
    TDestination? FirstOrDefault();
    TDestination? FirstOrDefault(Expression<Func<TDestination, bool>> predicate);
    TDestination Single();
    TDestination Single(Expression<Func<TDestination, bool>> predicate);
    TDestination? SingleOrDefault();
    TDestination? SingleOrDefault(Expression<Func<TDestination, bool>> predicate);
    TDestination Last();
    TDestination Last(Expression<Func<TDestination, bool>> predicate);
    TDestination? LastOrDefault();
    TDestination? LastOrDefault(Expression<Func<TDestination, bool>> predicate);

    // Escape hatch
    IQueryable<TDestination> AsQueryable();
}
