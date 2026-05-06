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
        if (tm.IsDynamic)
        {
            reason = $"map is a dynamic-shape mapping ({tm.SourceType} → {tm.DestinationType}); LINQ providers cannot translate runtime dictionary key lookups against arbitrary keys. Use mapper.Map<>() instead.";
            return false;
        }

        if (tm.CustomConverter is not null)
        {
            reason = "ConvertUsing(...) — delegate-form converter is in-memory only.";
            return false;
        }

        if (tm.BeforeHooks.Count > 0 || tm.AfterHooks.Count > 0)
        {
            reason = $"map has {tm.BeforeHooks.Count} BeforeMap and {tm.AfterHooks.Count} AfterMap hook(s) — hooks are not translatable to IQueryable. Use mapper.Map<>() instead, or remove the hooks.";
            return false;
        }

        reason = null;
        return true;
    }

    public static bool IsBindingProjectable(PropertyMap pm, out string? reason)
    {
        if (pm.DestinationPath is { Count: > 1 })
        {
            reason = "DestinationPath bindings (ForPath) cannot be projected — IQueryable providers don't support member-init writes into nested chains. Use forward-direction Map() instead.";
            return false;
        }

        // v1 has no per-property delegate construct; if any are added, gate them here.
        reason = null;
        return true;
    }
}
