using System.Linq.Expressions;
using Atlas.Internal;

namespace Atlas.Projections.Internal;

/// <summary>
/// Walks a destination-typed lambda and produces a source-typed lambda by substituting
/// destination-member accesses with the source expressions Atlas's typemaps record
/// (<see cref="PropertyMap.SourcePath"/>, <see cref="PropertyMap.CustomExpression"/>).
/// See <c>docs/Atlas-Design-ExpressionTranslation.md</c> §4.1 / §5.
/// </summary>
internal static class ExpressionTranslator
{
    /// <summary>
    /// Top-level entry point. Validates pair registration (Phase 1) and projection
    /// compatibility (Phase 2), then descends via the visitor (Phase 3).
    /// </summary>
    public static LambdaExpression Translate(
        MapperRegistry registry,
        TypePair root,
        LambdaExpression destinationLambda)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(destinationLambda);

        // Phase 1: pair registration check.
        var rootTm = registry.GetTypeMap(root);
        if (rootTm is null)
            throw Reject(root.Source, root.Destination, "(translate)",
                $"no map registered for {root.Source.Name} → {root.Destination.Name}. " +
                "UseAsDataSource requires a registered map for the (source, destination) pair.");

        // Phase 2: projection compatibility dual-gate (existing).
        if (!ProjectionCompatibility.IsTypeMapProjectable(rootTm, out var reason))
            throw Reject(root.Source, root.Destination, "(translate)", reason!);

        // Phase 3: visitor descent.
        var destParam = destinationLambda.Parameters[0];
        var srcParam = Expression.Parameter(root.Source, "src");
        var visitor = new MemberAccessRewriter(registry, destParam, srcParam, root);

        var rewrittenBody = visitor.Visit(destinationLambda.Body);

        var funcType = typeof(Func<,>).MakeGenericType(root.Source, destinationLambda.ReturnType);
        return Expression.Lambda(funcType, rewrittenBody, srcParam);
    }

    /// <summary>
    /// Constructs a single-diagnostic <see cref="AtlasProjectionException"/> with the
    /// "UseAsDataSource translation: " prefix per design §7.
    /// </summary>
    private static AtlasProjectionException Reject(
        Type srcType, Type dstType, string member, string reason) =>
        new AtlasProjectionException(new[]
        {
            new ProjectionDiagnostic(srcType, dstType, member,
                "UseAsDataSource translation: " + reason)
        });

    private sealed class MemberAccessRewriter : ExpressionVisitor
    {
        private readonly MapperRegistry _registry;
        private readonly ParameterExpression _destParam;
        private readonly ParameterExpression _srcParam;
        private readonly TypePair _rootPair;

        public MemberAccessRewriter(
            MapperRegistry registry,
            ParameterExpression destParam,
            ParameterExpression srcParam,
            TypePair rootPair)
        {
            _registry = registry;
            _destParam = destParam;
            _srcParam = srcParam;
            _rootPair = rootPair;
        }

        protected override Expression VisitParameter(ParameterExpression node) =>
            node == _destParam ? _srcParam : base.VisitParameter(node);

        protected override Expression VisitMember(MemberExpression node)
        {
            // Walk the spine: chain of MemberExpressions rooted at a single Expression.
            // For Task 2 we handle the flat single-member case (length-1 spine rooted at _destParam).
            if (node.Expression is ParameterExpression p && p == _destParam)
            {
                // Single-member spine: d.X
                var pm = LookupPropertyMap(_rootPair, node.Member.Name);
                return BuildSourceExpression(pm, _srcParam);
            }

            // Spine root is not _destParam (closure access, etc.) — pass through.
            return base.VisitMember(node);
        }

        private PropertyMap LookupPropertyMap(TypePair pair, string memberName)
        {
            var tm = _registry.GetTypeMap(pair)
                ?? throw Reject(pair.Source, pair.Destination, memberName,
                    $"destination chain references nested map ({pair.Source.Name} → {pair.Destination.Name}) which is not registered.");

            var pm = tm.PropertyMaps.FirstOrDefault(p =>
                string.Equals(p.Name, memberName, StringComparison.Ordinal));

            if (pm is null)
                throw Reject(pair.Source, pair.Destination, memberName,
                    $"destination member '{pair.Destination.Name}.{memberName}' has no PropertyMap. " +
                    "Use UseAsDataSource only with members that have a configured source.");

            return pm;
        }

        private Expression BuildSourceExpression(PropertyMap pm, Expression currentSrcExpr)
        {
            var srcType = currentSrcExpr.Type;
            var dstType = _rootPair.Destination;

            // Phase 3 rejection: Ignored member.
            if (pm.Ignored)
                throw Reject(srcType, dstType, pm.Name,
                    $"destination member '{dstType.Name}.{pm.Name}' is configured with Ignore() and " +
                    "cannot be referenced in a UseAsDataSource expression.");

            // Phase 3 rejection: constant-mapped member.
            if (pm.HasConstant)
                throw Reject(srcType, dstType, pm.Name,
                    $"destination member '{dstType.Name}.{pm.Name}' is a constant ({pm.ConstantValue}); " +
                    "predicates against it are trivially true/false. Compare against the constant directly instead.");

            // SourcePath case: walk the path, chaining MemberAccess.
            if (pm.SourcePath is not null)
            {
                Expression result = currentSrcExpr;
                foreach (var member in pm.SourcePath.Members)
                {
                    result = Expression.MakeMemberAccess(result, member);
                }
                return result;
            }

            // CustomExpression case: inline the body, substituting the lambda's parameter
            // with currentSrcExpr. Same code path as ProjectionPlanBuilder.BuildBinding
            // (src/Atlas.Projections/Internal/ProjectionPlanBuilder.cs lines 105-110).
            if (pm.CustomExpression is not null)
            {
                return ParameterReplacer.Replace(
                    pm.CustomExpression.Body,
                    pm.CustomExpression.Parameters[0],
                    currentSrcExpr);
            }

            // Phase 3 rejection: unmapped (no SourcePath, no CustomExpression, not Ignored, not HasConstant).
            // KEEP THIS WORDING VERBATIM — the existing MemberNotFound test asserts on "no PropertyMap".
            throw Reject(srcType, dstType, pm.Name,
                $"destination member '{dstType.Name}.{pm.Name}' has no PropertyMap. " +
                "Use UseAsDataSource only with members that have a configured source.");
        }
    }
}
