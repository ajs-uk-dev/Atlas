using System.Linq.Expressions;
using Atlas.Internal;

namespace Atlas.Projections.Internal;

/// <summary>
/// Internal wrapper around an <see cref="IQueryable{TSource}"/> that exposes the
/// destination-typed LINQ-operator surface. Each operator translates the destination-
/// typed lambda via <see cref="ExpressionTranslator"/> + <see cref="TranslationPlanCache"/>
/// and applies the source-typed result to the underlying query.
///
/// Task 8: skeleton with stub operator implementations. Tasks 9-10 fill in the bodies.
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

    // Filtering
    public IUseAsDataSourceQueryable<TSource, TDestination> Where(
        Expression<Func<TDestination, bool>> predicate) => throw new NotImplementedException("Task 9");

    // Ordering
    public IUseAsDataSourceOrdered<TSource, TDestination> OrderBy<TKey>(
        Expression<Func<TDestination, TKey>> keySelector) => throw new NotImplementedException("Task 9");

    public IUseAsDataSourceOrdered<TSource, TDestination> OrderByDescending<TKey>(
        Expression<Func<TDestination, TKey>> keySelector) => throw new NotImplementedException("Task 9");

    public IUseAsDataSourceOrdered<TSource, TDestination> ThenBy<TKey>(
        Expression<Func<TDestination, TKey>> keySelector) => throw new NotImplementedException("Task 9");

    public IUseAsDataSourceOrdered<TSource, TDestination> ThenByDescending<TKey>(
        Expression<Func<TDestination, TKey>> keySelector) => throw new NotImplementedException("Task 9");

    // Paging
    public IUseAsDataSourceQueryable<TSource, TDestination> Skip(int count) => throw new NotImplementedException("Task 9");
    public IUseAsDataSourceQueryable<TSource, TDestination> Take(int count) => throw new NotImplementedException("Task 9");

    // Terminal predicate
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

    // Escape hatch
    public IQueryable<TDestination> AsQueryable() => throw new NotImplementedException("Task 10");

    // IEnumerable
    public IEnumerator<TDestination> GetEnumerator() => throw new NotImplementedException("Task 10");
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
