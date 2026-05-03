using System.Reflection;

namespace Atlas.Internal;

/// <summary>
/// Resolves destination members to source-side member paths using naming conventions and
/// optional flattening. Pure: no DI, no state. Algorithm per design §6.
/// </summary>
internal static class ConventionEngine
{
    public static SourceMemberPath? TryResolve(
        Type sourceType,
        PropertyInfo destinationMember,
        ConventionOptions options,
        Func<Type, Type, bool>? hasRegisteredMap = null) =>
        TryResolveByName(sourceType, destinationMember.Name, destinationMember.PropertyType, options, hasRegisteredMap);

    public static SourceMemberPath? TryResolveByName(
        Type sourceType,
        string destinationName,
        Type destinationType,
        ConventionOptions options,
        Func<Type, Type, bool>? hasRegisteredMap = null)
    {
        var destLogical = NamingConventionTranslator.ToCanonical(destinationName, options.DestinationConvention);
        var comparer = options.CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

        var direct = MatchDirect(sourceType, destLogical, options.SourceConvention, comparer);
        if (direct is not null && IsCompatible(direct.PropertyType, destinationType, hasRegisteredMap))
            return new SourceMemberPath([direct]);

        var segments = NamingConventionTranslator.SplitPascal(destLogical);
        if (segments.Count > 1)
        {
            var path = TryWalkPath(sourceType, segments, 0, options.SourceConvention, comparer);
            if (path is not null && IsCompatible(path[^1].PropertyType, destinationType, hasRegisteredMap))
                return new SourceMemberPath(path);
        }

        return null;
    }

    /// <summary>
    /// Populates <paramref name="typeMap"/>'s <see cref="TypeMap.PropertyMaps"/> with one entry per
    /// destination binding (constructor parameter or writable property) that isn't already configured.
    /// Convention-resolved source paths are attached when found; unmatched bindings remain unresolved
    /// so validation can report them.
    /// </summary>
    public static void ResolveMissingMembers(
        TypeMap typeMap,
        ConventionOptions options,
        Func<Type, Type, bool>? hasRegisteredMap = null)
    {
        var existingNames = new HashSet<string>(
            typeMap.PropertyMaps.Select(p => p.Name),
            StringComparer.OrdinalIgnoreCase);

        var parameterless = typeMap.DestinationType.GetConstructor(Type.EmptyTypes);
        var hasParameterless = parameterless is { IsPublic: true };

        if (!hasParameterless)
        {
            var ctor = typeMap.DestinationType
                .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .OrderByDescending(c => c.GetParameters().Length)
                .FirstOrDefault();
            if (ctor is not null)
            {
                // Per design §7.3: constructor parameter names match case-insensitively, regardless
                // of the configured CaseSensitive flag (parameter names are conventionally camelCase
                // while source members are typically PascalCase).
                var ctorOptions = options with { CaseSensitive = false };
                foreach (var param in ctor.GetParameters())
                {
                    if (param.Name is null) continue;
                    if (existingNames.Contains(param.Name)) continue;
                    var pm = PropertyMap.ForCtorParam(param);
                    pm.SourcePath = TryResolveByName(typeMap.SourceType, param.Name, param.ParameterType, ctorOptions, hasRegisteredMap);
                    typeMap.PropertyMaps.Add(pm);
                    existingNames.Add(param.Name);
                }
            }
        }

        foreach (var prop in GetWritableProperties(typeMap.DestinationType))
        {
            if (existingNames.Contains(prop.Name)) continue;
            var pm = PropertyMap.ForProperty(prop);
            pm.SourcePath = TryResolve(typeMap.SourceType, prop, options, hasRegisteredMap);
            typeMap.PropertyMaps.Add(pm);
        }
    }

    private static IEnumerable<PropertyInfo> GetWritableProperties(Type t) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0);

    private static PropertyInfo? MatchDirect(
        Type sourceType,
        string destLogical,
        NamingConvention srcConvention,
        StringComparer comparer)
    {
        foreach (var p in GetReadableProperties(sourceType))
        {
            var srcLogical = NamingConventionTranslator.ToCanonical(p.Name, srcConvention);
            if (comparer.Equals(srcLogical, destLogical))
                return p;
        }
        return null;
    }

    private static List<PropertyInfo>? TryWalkPath(
        Type currentType,
        IReadOnlyList<string> segments,
        int startIndex,
        NamingConvention srcConvention,
        StringComparer comparer)
    {
        if (startIndex >= segments.Count) return [];
        var head = segments[startIndex];
        foreach (var p in GetReadableProperties(currentType))
        {
            var srcLogical = NamingConventionTranslator.ToCanonical(p.Name, srcConvention);
            if (!comparer.Equals(srcLogical, head)) continue;

            if (startIndex == segments.Count - 1)
                return [p];

            var rest = TryWalkPath(p.PropertyType, segments, startIndex + 1, srcConvention, comparer);
            if (rest is not null)
            {
                rest.Insert(0, p);
                return rest;
            }
        }
        return null;
    }

    private static IEnumerable<PropertyInfo> GetReadableProperties(Type t) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0);

    /// <summary>
    /// Loose compatibility check: does it make sense to attempt a mapping from src to dst type?
    /// Validation (§8) runs the strict version against the registry; this only filters obvious nonsense.
    /// </summary>
    private static bool IsCompatible(Type src, Type dst, Func<Type, Type, bool>? hasRegisteredMap)
    {
        if (dst.IsAssignableFrom(src)) return true;
        if (NumericConversions.HasImplicitConversion(src, dst)) return true;
        if (EnumConversions.HasImplicitConversion(src, dst)) return true;   // NEW
        if (IsEnumerable(src) && IsEnumerable(dst)) return true;
        if (IsComplex(src) && IsComplex(dst)) return true;
        if (hasRegisteredMap is not null && hasRegisteredMap(src, dst)) return true;
        return false;
    }

    private static bool IsEnumerable(Type t) =>
        t != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(t);

    private static bool IsComplex(Type t)
    {
        if (t.IsPrimitive) return false;
        if (t == typeof(string)) return false;
        if (t == typeof(decimal)) return false;
        if (t == typeof(DateTime) || t == typeof(DateTimeOffset)) return false;
        if (t == typeof(Guid)) return false;
        if (t.IsEnum) return false;
        if (Nullable.GetUnderlyingType(t) is not null) return false;
        return t.IsClass || (t.IsValueType && !t.IsPrimitive);
    }
}
