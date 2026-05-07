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

        // Phase 3: visitor descent. Filled in across Tasks 2-6.
        // For Task 1, return the input unchanged so the rejection-only tests pass.
        // Tasks 2-6 replace this with the visitor invocation.
        return destinationLambda;
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
}
