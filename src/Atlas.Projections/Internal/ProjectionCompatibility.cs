using Atlas.Internal;

namespace Atlas.Projections.Internal;

/// <summary>
/// Decides whether a <see cref="TypeMap"/> or a single <see cref="PropertyMap"/> can be emitted
/// as a projectable expression. Used by both <c>ProjectionValidator</c> (to surface diagnostics
/// up-front) and <c>ProjectionPlanBuilder</c> (to skip non-projectable bindings) so the two never
/// disagree on what's projectable.
/// </summary>
internal static class ProjectionCompatibility
{
    public static bool IsTypeMapProjectable(TypeMap tm, out string? reason)
    {
        if (tm.CustomConverter is not null)
        {
            reason = "ConvertUsing(...) — delegate-form converter is in-memory only.";
            return false;
        }
        reason = null;
        return true;
    }

    public static bool IsBindingProjectable(PropertyMap pm, out string? reason)
    {
        // v1 has no per-property delegate construct; if any are added, gate them here.
        reason = null;
        return true;
    }
}
