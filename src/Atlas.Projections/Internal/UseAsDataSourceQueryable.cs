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

    // ---- Terminal predicate ----
    public bool Any() => _underlying.Any();
    public bool Any(Expression<Func<TDestination, bool>> predicate) => _underlying.Any(Translate(predicate));
    public bool All(Expression<Func<TDestination, bool>> predicate) => _underlying.All(Translate(predicate));

    public int Count() => _underlying.Count();
    public int Count(Expression<Func<TDestination, bool>> predicate) => _underlying.Count(Translate(predicate));
    public long LongCount() => _underlying.LongCount();
    public long LongCount(Expression<Func<TDestination, bool>> predicate) => _underlying.LongCount(Translate(predicate));

    public TDestination First() => AsQueryable().First();
    public TDestination First(Expression<Func<TDestination, bool>> predicate) =>
        new UseAsDataSourceQueryable<TSource, TDestination>(
            _underlying.Where(Translate(predicate)), _configuration).AsQueryable().First();
    public TDestination? FirstOrDefault() => AsQueryable().FirstOrDefault();
    public TDestination? FirstOrDefault(Expression<Func<TDestination, bool>> predicate) =>
        new UseAsDataSourceQueryable<TSource, TDestination>(
            _underlying.Where(Translate(predicate)), _configuration).AsQueryable().FirstOrDefault();

    public TDestination Single() => AsQueryable().Single();
    public TDestination Single(Expression<Func<TDestination, bool>> predicate) =>
        new UseAsDataSourceQueryable<TSource, TDestination>(
            _underlying.Where(Translate(predicate)), _configuration).AsQueryable().Single();
    public TDestination? SingleOrDefault() => AsQueryable().SingleOrDefault();
    public TDestination? SingleOrDefault(Expression<Func<TDestination, bool>> predicate) =>
        new UseAsDataSourceQueryable<TSource, TDestination>(
            _underlying.Where(Translate(predicate)), _configuration).AsQueryable().SingleOrDefault();

    public TDestination Last() => AsQueryable().Last();
    public TDestination Last(Expression<Func<TDestination, bool>> predicate) =>
        new UseAsDataSourceQueryable<TSource, TDestination>(
            _underlying.Where(Translate(predicate)), _configuration).AsQueryable().Last();
    public TDestination? LastOrDefault() => AsQueryable().LastOrDefault();
    public TDestination? LastOrDefault(Expression<Func<TDestination, bool>> predicate) =>
        new UseAsDataSourceQueryable<TSource, TDestination>(
            _underlying.Where(Translate(predicate)), _configuration).AsQueryable().LastOrDefault();

    // ---- Escape hatch ----
    public IQueryable<TDestination> AsQueryable() => _underlying.ProjectTo<TDestination>(_configuration);

    // ---- IEnumerable ----
    public IEnumerator<TDestination> GetEnumerator() => AsQueryable().GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
