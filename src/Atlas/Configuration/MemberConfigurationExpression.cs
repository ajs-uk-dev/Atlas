using System.Linq.Expressions;
using System.Reflection;
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
    private SourceMemberPath? _sourcePath;
    private object? _constantValue;
    private bool _hasConstant;
    private bool _ignored;

    public void MapFrom<TSourceMember>(Expression<Func<TSource, TSourceMember>> sourceMember)
    {
        _constantValue = null;
        _hasConstant = false;
        _ignored = false;

        // If the expression is a pure member-access chain (e.g., s => s.A.B.C),
        // store it as a SourceMemberPath so projection engines can inspect it.
        // Otherwise fall back to CustomExpression for arbitrary lambdas.
        var path = TryExtractMemberPath(sourceMember);
        if (path is not null)
        {
            _sourcePath = path;
            _customExpression = null;
        }
        else
        {
            _customExpression = sourceMember;
            _sourcePath = null;
        }
    }

    public void MapFrom(TMember constantValue)
    {
        _constantValue = constantValue;
        _hasConstant = true;
        _customExpression = null;
        _sourcePath = null;
        _ignored = false;
    }

    public void Ignore()
    {
        _ignored = true;
        _customExpression = null;
        _sourcePath = null;
        _constantValue = null;
        _hasConstant = false;
    }

    public void ApplyTo(PropertyMap propertyMap)
    {
        propertyMap.SourcePath = _sourcePath;
        propertyMap.CustomExpression = _customExpression;
        propertyMap.ConstantValue = _constantValue;
        propertyMap.HasConstant = _hasConstant;
        propertyMap.Ignored = _ignored;
    }

    private static SourceMemberPath? TryExtractMemberPath<TSourceMember>(Expression<Func<TSource, TSourceMember>> selector)
    {
        var body = selector.Body;
        if (body is UnaryExpression { NodeType: ExpressionType.Convert, Operand: var operand })
            body = operand;

        var stack = new Stack<PropertyInfo>();
        var current = body;
        while (current is MemberExpression me)
        {
            if (me.Member is not PropertyInfo prop)
                return null;
            stack.Push(prop);
            current = me.Expression!;
        }

        if (current is not ParameterExpression || stack.Count == 0)
            return null;

        return new SourceMemberPath(stack.ToArray());
    }
}
