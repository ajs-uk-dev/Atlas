using System.Linq.Expressions;

namespace Atlas.Configuration;

/// <summary>
/// Fluent surface for configuring a single (TSource, TDestination) mapping.
/// </summary>
public interface IMappingExpression<TSource, TDestination>
{
    /// <summary>Override the mapping for one destination property.</summary>
    IMappingExpression<TSource, TDestination> ForMember<TMember>(
        Expression<Func<TDestination, TMember>> destinationMember,
        Action<IMemberConfigurationExpression<TSource, TDestination, TMember>> memberOptions);

    /// <summary>Override the mapping for a destination constructor parameter (case-insensitive name match).</summary>
    IMappingExpression<TSource, TDestination> ForCtorParam(
        string ctorParamName,
        Action<IMemberConfigurationExpression<TSource, TDestination, object?>> paramOptions);

    /// <summary>Replace the entire mapping with a globally-registered converter.</summary>
    void ConvertUsing<TConverter>() where TConverter : ITypeConverter<TSource, TDestination>, new();

    /// <summary>Replace the entire mapping with an inline conversion delegate.</summary>
    void ConvertUsing(Func<TSource, TDestination> converter);
}
