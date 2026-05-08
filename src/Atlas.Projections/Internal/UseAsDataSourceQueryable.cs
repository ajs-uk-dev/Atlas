using System.Linq.Expressions;
using Atlas.Internal;

namespace Atlas.Projections.Internal;

/// <summary>
/// Internal wrapper around an <see cref="IQueryable{TSource}"/> that exposes the
/// destination-typed LINQ-operator surface. Each operator translates the destination-
/// typed lambda via <see cref="ExpressionTranslator"/> + <see cref="TranslationPlanCache"/>
/// and applies the source-typed result to the underlying query.
/// </summary>
internal sealed class UseAsDataSourceQueryable<TSource, TDestination>
    : IUseAsDataSourceOrdered<TSource, TDestination>
{
    private readonly IQueryable<TSource> _underlying;
    private readonly MapperConfiguration _configuration;
    private readonly TypePair _pair;

    internal UseAsDataSourceQueryable(IQueryable<TSource> underlying, MapperConfiguration configuration)
    {
        _underlying = underlying;
        _configuration = configuration;
        _pair = new TypePair(typeof(TSource), typeof(TDestination));
    }

    private Expression<Func<TSource, TResult>> Translate<TResult>(
        Expression<Func<TDestination, TResult>> destLambda)
    {
        var cached = TranslationPlanCacheRegistry.For(_configuration).GetOrTranslate(
            _pair, destLambda,
            () => ExpressionTranslator.Translate(_configuration.Internal_Registry, _pair, destLambda));
        return (Expression<Func<TSource, TResult>>)cached;
    }

    private static IOrderedQueryable<TSource> AsOrderedQueryable(IQueryable<TSource> q) =>
        q as IOrderedQueryable<TSource>
        ?? throw new InvalidOperationException(
            "ThenBy/ThenByDescending called on a non-ordered query. " +
            "Call OrderBy or OrderByDescending first.");

    // ---- Filtering ----
    public IUseAsDataSourceQueryable<TSource, TDestination> Where(
        Expression<Func<TDestination, bool>> predicate) =>
        new UseAsDataSourceQueryable<TSource, TDestination>(
            _underlying.Where(Translate(predicate)),
            _configuration);

    // ---- Ordering ----
    public IUseAsDataSourceOrdered<TSource, TDestination> OrderBy<TKey>(
        Expression<Func<TDestination, TKey>> keySelector) =>
        new UseAsDataSourceQueryable<TSource, TDestination>(
            _underlying.OrderBy(Translate(keySelector)),
            _configuration);

    public IUseAsDataSourceOrdered<TSource, TDestination> OrderByDescending<TKey>(
        Expression<Func<TDestination, TKey>> keySelector) =>
        new UseAsDataSourceQueryable<TSource, TDestination>(
            _underlying.OrderByDescending(Translate(keySelector)),
            _configuration);

    public IUseAsDataSourceOrdered<TSource, TDestination> ThenBy<TKey>(
        Expression<Func<TDestination, TKey>> keySelector) =>
        new UseAsDataSourceQueryable<TSource, TDestination>(
            AsOrderedQueryable(_underlying).ThenBy(Translate(keySelector)),
            _configuration);

    public IUseAsDataSourceOrdered<TSource, TDestination> ThenByDescending<TKey>(
        Expression<Func<TDestination, TKey>> keySelector) =>
        new UseAsDataSourceQueryable<TSource, TDestination>(
            AsOrderedQueryable(_underlying).ThenByDescending(Translate(keySelector)),
            _configuration);

    // ---- Paging ----
    public IUseAsDataSourceQueryable<TSource, TDestination> Skip(int count) =>
        new UseAsDataSourceQueryable<TSource, TDestination>(_underlying.Skip(count), _configuration);

    public IUseAsDataSourceQueryable<TSource, TDestination> Take(int count) =>
        new UseAsDataSourceQueryable<TSource, TDestination>(_underlying.Take(count), _configuration);

    // ---- Terminal predicate (Task 10 fills in) ----
    public bool Any() => throw new NotImplementedException("Task 10");
    public bool Any(Expression<Func<TDestination, bool>> predicate) => throw new NotImplementedException("Task 10");
    public bool All(Expression<Func<TDestination, bool>> predicate) => throw new NotImplementedException("Task 10");
    public int Count() => throw new NotImplementedException("Task 10");
    public int Count(Expression<Func<TDestination, bool>> predicate) => throw new NotImplementedException("Task 10");
    public long LongCount() => throw new NotImplementedException("Task 10");
    public long LongCount(Expression<Func<TDestination, bool>> predicate) => throw new NotImplementedException("Task 10");
    public TDestination First() => throw new NotImplementedException("Task 10");
    public TDestination First(Expression<Func<TDestination, bool>> predicate) => throw new NotImplementedException("Task 10");
    public TDestination? FirstOrDefault() => throw new NotImplementedException("Task 10");
    public TDestination? FirstOrDefault(Expression<Func<TDestination, bool>> predicate) => throw new NotImplementedException("Task 10");
    public TDestination Single() => throw new NotImplementedException("Task 10");
    public TDestination Single(Expression<Func<TDestination, bool>> predicate) => throw new NotImplementedException("Task 10");
    public TDestination? SingleOrDefault() => throw new NotImplementedException("Task 10");
    public TDestination? SingleOrDefault(Expression<Func<TDestination, bool>> predicate) => throw new NotImplementedException("Task 10");
    public TDestination Last() => throw new NotImplementedException("Task 10");
    public TDestination Last(Expression<Func<TDestination, bool>> predicate) => throw new NotImplementedException("Task 10");
    public TDestination? LastOrDefault() => throw new NotImplementedException("Task 10");
    public TDestination? LastOrDefault(Expression<Func<TDestination, bool>> predicate) => throw new NotImplementedException("Task 10");

    // ---- Escape hatch (Task 10 fills in) ----
    public IQueryable<TDestination> AsQueryable() => throw new NotImplementedException("Task 10");

    // ---- IEnumerable (Task 10 fills in) ----
    public IEnumerator<TDestination> GetEnumerator() => throw new NotImplementedException("Task 10");
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
