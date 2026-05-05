using System.Linq.Expressions;

namespace Atlas.Configuration;

/// <summary>
/// Per-member fluent surface inside a <c>ForMember</c> or <c>ForCtorParam</c> options callback.
/// </summary>
public interface IMemberConfigurationExpression<TSource, TDestination, TMember>
{
    /// <summary>Map this destination member from an arbitrary expression on the source.</summary>
    void MapFrom<TSourceMember>(Expression<Func<TSource, TSourceMember>> sourceMember);

    /// <summary>Map this destination member from a constant value.</summary>
    void MapFrom(TMember constantValue);

    /// <summary>Skip this destination member entirely (also removes it from validation).</summary>
    void Ignore();

    /// <summary>
    /// Predicate evaluated BEFORE source-side resolution. If the predicate returns false,
    /// the destination member is not mapped — for fresh <c>Map&lt;TDest&gt;(src)</c> the
    /// property remains at its default value; for update-in-place
    /// <c>Map&lt;TS,TD&gt;(src, existingDest)</c> the existing destination value is preserved.
    /// Use when source-side resolution is expensive and would be wasted work if the predicate
    /// fails.
    /// </summary>
    /// <remarks>
    /// Stored as <see cref="Expression{TDelegate}"/> so the predicate participates in both
    /// in-memory mapping and IQueryable projection. In <c>ProjectTo&lt;&gt;()</c>, the predicate
    /// becomes part of a LINQ <see cref="System.Linq.Expressions.ConditionalExpression"/> that the
    /// underlying provider translates to SQL (typically <c>CASE WHEN</c>). Untranslatable
    /// predicates fail at query-execution time with the provider's standard error — Atlas does
    /// not pre-inspect lambdas for translatability.
    /// <para>
    /// Multiple <c>PreCondition</c> calls on the same member: last-call-wins (matches
    /// <c>MapFrom</c>). Repeating clears the prior predicate.
    /// </para>
    /// <para>
    /// On a map configured with <see cref="IMappingExpression{TSource, TDestination}.ConvertUsing(Func{TSource, TDestination})"/>,
    /// per-member predicates are silently inactive (the converter replaces all per-member assigns).
    /// On a constructor-parameter binding (<c>ForCtorParam</c>), predicate-fail produces the
    /// parameter's declared default value (<c>p.HasDefaultValue ? p.DefaultValue : default(T)</c>)
    /// rather than skipping the assignment, because a constructor argument cannot be omitted.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="predicate"/> is null.</exception>
    void PreCondition(Expression<Func<TSource, bool>> predicate);

    /// <summary>
    /// Predicate evaluated AFTER source-side resolution but BEFORE assignment. The second
    /// argument is the resolved value (the result of <c>MapFrom</c> / source path / value
    /// transformers). If the predicate returns false, the destination member is not assigned
    /// — same skip semantics as <see cref="PreCondition"/>. Use when the predicate depends
    /// on the resolved value (e.g., "only assign if the resolved value is non-empty").
    /// </summary>
    /// <remarks>
    /// See <see cref="PreCondition"/> for storage, projection, multi-call, ConvertUsing,
    /// and ForCtorParam semantics — they apply identically.
    /// <para>
    /// The resolved sub-expression is hoisted into a local variable in the in-memory codegen
    /// so it is evaluated only once per call, regardless of how many times the predicate
    /// references the resolved value. In projection codegen the resolved expression is
    /// inlined twice (once for the predicate test, once for the assigned value); LINQ
    /// providers handle this fine.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="predicate"/> is null.</exception>
    void Condition(Expression<Func<TSource, TMember, bool>> predicate);

    /// <summary>
    /// Supplies a fallback value used when the resolved source value is <c>null</c>.
    /// The substitute is typed as the source member and runs through the same conversion
    /// pipeline as a real source value would (numeric / enum auto-conversion, registered
    /// TypeMaps).
    /// </summary>
    /// <typeparam name="TSourceMember">
    /// The source-member type. Compiler-inferred from the literal in the constant overload
    /// or the lambda body in the Expression overload.
    /// </typeparam>
    /// <param name="constant">The fallback value used when the resolved source is null.</param>
    /// <remarks>
    /// Only meaningful when the resolved source-member type is a reference type or
    /// <see cref="Nullable{T}"/>. <see cref="MapperConfiguration.AssertConfigurationIsValid"/>
    /// reports an error if <c>NullSubstitute</c> is configured on a non-nullable
    /// value-typed source member (the substitute would be unreachable). It also reports
    /// an error if the substitute's type is not assignable to the resolved source-member type.
    /// <para>
    /// Pipeline placement: <b>PreCondition → resolve → null-substitute → convert → transform →
    /// Condition → assign</b>. Value transformers and <c>Condition</c> see the substituted
    /// (non-null) value, never the original null.
    /// </para>
    /// <para>
    /// Multiple <c>NullSubstitute</c> calls on the same member: last-call-wins (matches
    /// <c>MapFrom</c>). Repeating clears the prior substitute.
    /// </para>
    /// <para>
    /// On a map configured with <see cref="IMappingExpression{TSource, TDestination}.ConvertUsing(Func{TSource, TDestination})"/>,
    /// per-member substitutes are silently inactive (the converter replaces all per-member assigns).
    /// Substitutes flow base→derived through inheritance via the existing explicit-config
    /// precedence rule. Substitutes do NOT auto-flip across <c>.ReverseMap()</c> —
    /// reconfigure on the reverse expression.
    /// </para>
    /// <para>
    /// Translates to SQL <c>COALESCE</c> in <c>ProjectTo&lt;&gt;()</c>.
    /// </para>
    /// </remarks>
    void NullSubstitute<TSourceMember>(TSourceMember constant);

    /// <summary>
    /// Expression form of <see cref="NullSubstitute{TSourceMember}(TSourceMember)"/>.
    /// Use for computed defaults (e.g., <c>() =&gt; DateTime.UtcNow</c>) that cannot be
    /// expressed as a literal constant.
    /// </summary>
    /// <typeparam name="TSourceMember">
    /// The source-member type. Compiler-inferred from the lambda body's return type.
    /// </typeparam>
    /// <param name="factory">A no-arg lambda that produces the fallback value.</param>
    /// <remarks>
    /// See <see cref="NullSubstitute{TSourceMember}(TSourceMember)"/> for storage,
    /// projection, multi-call, ConvertUsing, and inheritance semantics — they apply identically.
    /// <para>
    /// The factory is stored as <see cref="Expression{TDelegate}"/>. For projection
    /// translation, the body must be translatable by the underlying LINQ provider —
    /// untranslatable factories fail at query-execution time with the provider's standard
    /// error. Atlas does not pre-inspect lambdas for translatability.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="factory"/> is null.</exception>
    void NullSubstitute<TSourceMember>(Expression<Func<TSourceMember>> factory);
}
