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
}
