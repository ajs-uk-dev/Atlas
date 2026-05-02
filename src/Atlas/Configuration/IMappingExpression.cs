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

    /// <summary>
    /// Declares that <typeparamref name="TDerivedSource"/> (which must derive from
    /// <typeparamref name="TSource"/>) should map to <typeparamref name="TDerivedDestination"/>
    /// (which must derive from <typeparamref name="TDestination"/>) when the runtime source
    /// is the derived type. The derived map must be registered separately via its own
    /// <c>CreateMap&lt;TDerivedSource, TDerivedDestination&gt;()</c> call.
    /// </summary>
    /// <remarks>
    /// At runtime, the compiled lambda for the base map starts with an inline type-test
    /// chain: any registered derived dispatch is checked before the base body runs.
    /// Most-derived-first ordering is computed at config-build time.
    /// </remarks>
    IMappingExpression<TSource, TDestination> Include<TDerivedSource, TDerivedDestination>()
        where TDerivedSource : TSource
        where TDerivedDestination : TDestination;

    /// <summary>
    /// Declares that this map participates in the runtime dispatch of a base map and inherits
    /// member configuration from it. Equivalent to declaring
    /// <c>.Include&lt;TSource, TDestination&gt;()</c> on the base map — useful when the base
    /// map lives in a different profile.
    /// </summary>
    IMappingExpression<TSource, TDestination> IncludeBase<TBaseSource, TBaseDestination>()
        where TBaseSource : TSource
        where TBaseDestination : TDestination;
}
