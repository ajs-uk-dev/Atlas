using System.Linq.Expressions;
using Atlas.Internal;
using Atlas.Projections.Internal;

namespace Atlas.Projections;

/// <summary>
/// Direct-use translation helper on <see cref="MapperConfiguration"/>. Used by power
/// users who want a translated lambda as a value (e.g., for unit tests or composing
/// with custom LINQ providers); also used internally by
/// <see cref="UseAsDataSourceExtensions"/>'s wrapper operators.
/// </summary>
public static class MapperConfigurationExpressionTranslationExtensions
{
    /// <summary>
    /// Translates a destination-typed expression into a source-typed expression by
    /// substituting destination-member accesses with the source expressions Atlas's
    /// typemaps record (<c>PropertyMap.SourcePath</c> or <c>PropertyMap.CustomExpression</c>).
    /// </summary>
    /// <exception cref="AtlasProjectionException">
    /// Thrown when the lambda references an unmapped, ignored, or constant-mapped
    /// destination member, OR when the (TSource, TDestination) pair is not registered,
    /// OR when the typemap has hooks/PreserveReferences/dynamic-shape attributes that
    /// reject projection.
    /// </exception>
    public static Expression<Func<TSource, TResult>> Translate<TSource, TDestination, TResult>(
        this MapperConfiguration configuration,
        Expression<Func<TDestination, TResult>> destinationExpression)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(destinationExpression);

        var pair = new TypePair(typeof(TSource), typeof(TDestination));
        var translated = TranslationPlanCacheRegistry.For(configuration).GetOrTranslate(
            pair, destinationExpression,
            () => ExpressionTranslator.Translate(configuration.Internal_Registry, pair, destinationExpression));

        return (Expression<Func<TSource, TResult>>)translated;
    }
}
