using System.Linq.Expressions;
using System.Reflection;

namespace Atlas.Projections.Tests.Internal;

/// <summary>
/// Small whitebox assertions over an expression tree. Used by the builder tests to verify
/// the SHAPE of the emitted lambda, not just its execution result.
/// </summary>
internal static class AssertExpression
{
    public static bool Contains<TNode>(Expression expression) where TNode : Expression
    {
        var found = false;
        var visitor = new PredicateVisitor(node => { if (node is TNode) { found = true; return true; } return false; });
        visitor.Visit(expression);
        return found;
    }

    public static bool ContainsCallTo(Expression expression, string declaringTypeName, string methodName)
    {
        var found = false;
        var visitor = new PredicateVisitor(node =>
        {
            if (node is MethodCallExpression mc &&
                mc.Method.DeclaringType?.Name == declaringTypeName &&
                mc.Method.Name == methodName)
            {
                found = true;
                return true;
            }
            return false;
        });
        visitor.Visit(expression);
        return found;
    }

    public static int CountNodes<TNode>(Expression expression) where TNode : Expression
    {
        var count = 0;
        var visitor = new PredicateVisitor(node => { if (node is TNode) count++; return false; });
        visitor.Visit(expression);
        return count;
    }

    private sealed class PredicateVisitor : ExpressionVisitor
    {
        private readonly Func<Expression, bool> _stopWhen;
        public PredicateVisitor(Func<Expression, bool> stopWhen) { _stopWhen = stopWhen; }
        public override Expression? Visit(Expression? node)
        {
            if (node is null) return null;
            if (_stopWhen(node)) return node;
            return base.Visit(node);
        }
    }
}
