using System.Linq.Expressions;

namespace Atlas.Projections.Internal;

/// <summary>
/// Swaps a <see cref="ParameterExpression"/> for an arbitrary replacement expression while
/// visiting an expression tree. Used by <c>ProjectionPlanBuilder</c> when inlining a
/// user-provided custom expression or a nested map's body into the parent projection.
/// </summary>
internal sealed class ParameterReplacer : ExpressionVisitor
{
    private readonly ParameterExpression _target;
    private readonly Expression _replacement;

    public ParameterReplacer(ParameterExpression target, Expression replacement)
    {
        _target = target;
        _replacement = replacement;
    }

    public static Expression Replace(Expression body, ParameterExpression target, Expression replacement) =>
        new ParameterReplacer(target, replacement).Visit(body)!;

    protected override Expression VisitParameter(ParameterExpression node) =>
        node == _target ? _replacement : base.VisitParameter(node);
}
