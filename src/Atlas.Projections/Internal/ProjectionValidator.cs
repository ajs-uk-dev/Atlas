using Atlas.Internal;

namespace Atlas.Projections.Internal;

/// <summary>
/// Walks a <see cref="MapperRegistry"/> from a root <see cref="TypePair"/> and reports every
/// reachable binding that the projection builder will not be able to translate. Algorithm per
/// design §5.2.
/// </summary>
internal static class ProjectionValidator
{
    public static void Validate(MapperRegistry registry, TypePair root, int maxDepth)
    {
        var diagnostics = new List<ProjectionDiagnostic>();
        var visited = new HashSet<TypePair>();
        Walk(root, depth: 0, registry, visited, diagnostics, maxDepth);
        if (diagnostics.Count > 0)
            throw new AtlasProjectionException(diagnostics);
    }

    private static void Walk(
        TypePair pair,
        int depth,
        MapperRegistry registry,
        HashSet<TypePair> visited,
        List<ProjectionDiagnostic> diagnostics,
        int maxDepth)
    {
        if (depth >= maxDepth) return;
        if (!visited.Add(pair)) return;

        var tm = registry.GetTypeMap(pair);
        if (tm is null)
        {
            diagnostics.Add(new ProjectionDiagnostic(
                pair.Source, pair.Destination, "(no map registered)",
                $"No map registered for {pair.Source.Name} -> {pair.Destination.Name}."));
            return;
        }

        if (!ProjectionCompatibility.IsTypeMapProjectable(tm, out var typeMapReason))
        {
            diagnostics.Add(new ProjectionDiagnostic(
                pair.Source, pair.Destination, "(whole map)", typeMapReason!));
            return;
        }

        foreach (var pm in tm.PropertyMaps)
        {
            if (pm.Ignored) continue;
            if (!ProjectionCompatibility.IsBindingProjectable(pm, out var bindingReason))
            {
                diagnostics.Add(new ProjectionDiagnostic(
                    pair.Source, pair.Destination, pm.Name, bindingReason!));
                continue;
            }
            if (pm.HasConstant) continue;
            if (pm.CustomExpression is not null) continue;
            if (pm.SourcePath is null)
            {
                diagnostics.Add(new ProjectionDiagnostic(
                    pair.Source, pair.Destination, pm.Name,
                    "Unmapped — projection requires every destination binding resolved."));
                continue;
            }

            var leaf = pm.SourcePath.Members[^1].PropertyType;
            var target = pm.DestinationType;
            if (leaf == target || target.IsAssignableFrom(leaf)) continue;
            if (NumericConversions.HasImplicitConversion(leaf, target)) continue;

            // Dictionary<,> destinations are out-of-scope for v1; the validator does not special-case them.
            if (IsCollection(leaf) && IsCollection(target))
            {
                Walk(new TypePair(GetEnumerableElementType(leaf)!, GetEnumerableElementType(target)!),
                    depth + 1, registry, visited, diagnostics, maxDepth);
                continue;
            }

            Walk(new TypePair(leaf, target), depth + 1, registry, visited, diagnostics, maxDepth);
        }
    }

    private static bool IsCollection(Type t) =>
        t != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(t);

    private static Type? GetEnumerableElementType(Type t)
    {
        if (t.IsArray) return t.GetElementType();
        foreach (var i in new[] { t }.Concat(t.GetInterfaces()))
            if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return i.GetGenericArguments()[0];
        return null;
    }
}
