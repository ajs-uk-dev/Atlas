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
    /// <remarks>
    /// The C# type system cannot express the constraint that TSource derives from
    /// TBaseSource (CS0699 — outer type parameters can't be constrained from method where
    /// clauses). The validator catches misuse at config-build time instead.
    /// </remarks>
    IMappingExpression<TSource, TDestination> IncludeBase<TBaseSource, TBaseDestination>();

    // ---- Enum surface (callable only when both TSource and TDestination are enums; otherwise throws at config time) ----

    /// <summary>
    /// Forces by-value matching for this enum→enum map (matches by underlying integer).
    /// Default if neither MapByValue nor MapByName is called.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown at configuration time if <typeparamref name="TSource"/> or <typeparamref name="TDestination"/> is not an enum.
    /// </exception>
    IMappingExpression<TSource, TDestination> MapByValue();

    /// <summary>
    /// Forces by-name matching for this enum→enum map (matches by member name).
    /// </summary>
    /// <param name="ignoreCase">If true, name matching is case-insensitive. Defaults to false.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown at configuration time if <typeparamref name="TSource"/> or <typeparamref name="TDestination"/> is not an enum.
    /// </exception>
    IMappingExpression<TSource, TDestination> MapByName(bool ignoreCase = false);

    /// <summary>
    /// Maps a specific source enum value to a specific destination enum value.
    /// Takes precedence over the strategy default.
    /// </summary>
    /// <exception cref="AtlasConfigurationException">
    /// Thrown if <paramref name="sourceValue"/> is already configured via MapValue or Ignore.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown at configuration time if <typeparamref name="TSource"/> or <typeparamref name="TDestination"/> is not an enum.
    /// </exception>
    IMappingExpression<TSource, TDestination> MapValue(TSource sourceValue, TDestination destinationValue);

    /// <summary>
    /// Marks a source enum value as ignored. Mapping that value at runtime produces
    /// <c>default(TDestination)</c> rather than searching the strategy or fallback.
    /// </summary>
    /// <remarks>
    /// If <c>default(TDestination)</c> is not a defined value of <typeparamref name="TDestination"/>,
    /// <see cref="MapperConfiguration.AssertConfigurationIsValid"/> throws — Ignore would otherwise silently
    /// produce an undefined enum value (a subtle foot-gun).
    /// In that case, use <see cref="MapValue"/> with an explicit destination instead.
    /// </remarks>
    /// <exception cref="AtlasConfigurationException">
    /// Thrown if <paramref name="sourceValue"/> is already configured via MapValue.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown at configuration time if <typeparamref name="TSource"/> is not an enum.
    /// </exception>
    IMappingExpression<TSource, TDestination> Ignore(TSource sourceValue);

    /// <summary>
    /// Sets a fallback destination value used when no explicit MapValue, Ignore, or strategy match applies.
    /// Without a fallback, unmatched values throw <see cref="AtlasMappingException"/> at runtime.
    /// </summary>
    /// <exception cref="AtlasConfigurationException">
    /// Thrown if WithFallback was already called on this map.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown at configuration time if <typeparamref name="TDestination"/> is not an enum.
    /// </exception>
    IMappingExpression<TSource, TDestination> WithFallback(TDestination fallbackValue);
}
