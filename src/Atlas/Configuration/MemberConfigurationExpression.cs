using System.Linq.Expressions;
using Atlas.Internal;

namespace Atlas.Configuration;

/// <summary>
/// Captures the per-member configuration declared inside a single <c>ForMember</c> /
/// <c>ForCtorParam</c> options callback, then applies it to a <see cref="PropertyMap"/>.
/// Last-call-wins for repeated calls inside the same callback.
/// </summary>
internal sealed class MemberConfigurationExpression<TSource, TDestination, TMember>
    : IMemberConfigurationExpression<TSource, TDestination, TMember>
{
    private LambdaExpression? _customExpression;
    private object? _constantValue;
    private bool _hasConstant;
    private bool _ignored;

    public void MapFrom<TSourceMember>(Expression<Func<TSource, TSourceMember>> sourceMember)
    {
        _customExpression = sourceMember;
        _constantValue = null;
        _hasConstant = false;
        _ignored = false;
    }

    public void MapFrom(TMember constantValue)
    {
        _constantValue = constantValue;
        _hasConstant = true;
        _customExpression = null;
        _ignored = false;
    }

    public void Ignore()
    {
        _ignored = true;
        _customExpression = null;
        _constantValue = null;
        _hasConstant = false;
    }

    public void ApplyTo(PropertyMap propertyMap)
    {
        propertyMap.SourcePath = null;
        propertyMap.CustomExpression = _customExpression;
        propertyMap.ConstantValue = _constantValue;
        propertyMap.HasConstant = _hasConstant;
        propertyMap.Ignored = _ignored;
    }
}
