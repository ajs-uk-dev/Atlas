using System.Reflection;

namespace Atlas.Internal;

/// <summary>
/// Implements the <c>AssertConfigurationIsValid</c> algorithm from design §8. Pure: walks the
/// already-built <see cref="MapperRegistry"/> and reports every problem in one pass so the
/// developer sees them all at once.
/// </summary>
internal static class ConfigurationValidator
{
    public static void Validate(MapperRegistry registry, bool enumValidationEnabled = false)
    {
        var errors = new List<ConfigurationError>();
        foreach (var tm in registry.AllTypeMaps)
        {
            // Enum rules (always-on; covers per-value overrides, fallback, foot-gun guard).
            ValidateEnum(tm, errors);

            // Strict-mode enum source-side coverage (Task 9).
            if (enumValidationEnabled)
                ValidateEnumStrict(tm, errors);

            // Inheritance rules (design §7.3).
            ValidateInheritance(tm, registry, errors);

            if (tm.MemberList == MemberList.None) continue;

            if (tm.CustomConverter is not null) continue;

            if (tm.MemberList == MemberList.Destination)
                ValidateDestination(tm, registry, errors);
            else
                ValidateSource(tm, registry, errors);
        }

        if (errors.Count > 0)
            throw new AtlasConfigurationException(errors);
    }

    private static void ValidateDestination(TypeMap tm, MapperRegistry registry, List<ConfigurationError> errors)
    {
        foreach (var prop in GetWritableProperties(tm.DestinationType))
        {
            var pm = tm.PropertyMaps.FirstOrDefault(p =>
                string.Equals(p.Name, prop.Name, StringComparison.Ordinal));

            if (pm is null)
            {
                errors.Add(new ConfigurationError(
                    tm.SourceType, tm.DestinationType, prop.Name,
                    "No mapping configured for destination member."));
                continue;
            }

            if (!pm.IsResolved)
            {
                errors.Add(new ConfigurationError(
                    tm.SourceType, tm.DestinationType, prop.Name,
                    "Destination member is unmapped (no source path, constant, or Ignore)."));
                continue;
            }

            if (pm.SourcePath is not null)
            {
                var srcType = pm.SourcePath.Members[^1].PropertyType;
                if (!IsAssignmentLegal(srcType, prop.PropertyType, registry))
                {
                    errors.Add(new ConfigurationError(
                        tm.SourceType, tm.DestinationType, prop.Name,
                        $"No registered map or implicit conversion from {srcType.Name} to {prop.PropertyType.Name}."));
                }
            }
        }
    }

    private static void ValidateSource(TypeMap tm, MapperRegistry registry, List<ConfigurationError> errors)
    {
        var consumed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pm in tm.PropertyMaps)
        {
            if (pm.SourcePath is { Members.Count: > 0 } sp)
                consumed.Add(sp.Members[0].Name);
        }

        foreach (var prop in GetReadableProperties(tm.SourceType))
        {
            if (consumed.Contains(prop.Name)) continue;
            errors.Add(new ConfigurationError(
                tm.SourceType, tm.DestinationType, prop.Name,
                "Source member is not consumed by any destination binding."));
        }
    }

    private static bool IsAssignmentLegal(Type src, Type dst, MapperRegistry registry)
    {
        if (dst.IsAssignableFrom(src)) return true;
        if (NumericConversions.HasImplicitConversion(src, dst)) return true;
        if (EnumConversions.HasImplicitConversion(src, dst)) return true;
        if (registry.GetTypeMap(new TypePair(src, dst)) is not null) return true;
        if (IsEnumerable(src) && IsEnumerable(dst)) return true;
        return false;
    }

    private static IEnumerable<PropertyInfo> GetWritableProperties(Type t) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0);

    private static IEnumerable<PropertyInfo> GetReadableProperties(Type t) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0);

    private static bool IsEnumerable(Type t) =>
        t != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(t);

    private static void ValidateEnum(TypeMap tm, List<ConfigurationError> errors)
    {
        if (tm.EnumConfig is null) return;
        var cfg = tm.EnumConfig;
        var srcType = tm.SourceType;
        var dstType = tm.DestinationType;

        // Rule: PerValueOverrides keys must be defined on srcType / dstType.
        foreach (var (src, dst) in cfg.PerValueOverrides)
        {
            if (srcType.IsEnum && !Enum.IsDefined(srcType, src))
                errors.Add(new ConfigurationError(
                    srcType, dstType, "(MapValue)",
                    $"MapValue source value '{src}' is not defined on {srcType.Name}."));
            if (dstType.IsEnum && !Enum.IsDefined(dstType, dst))
                errors.Add(new ConfigurationError(
                    srcType, dstType, "(MapValue)",
                    $"MapValue destination value '{dst}' is not defined on {dstType.Name}."));
        }

        // Rule: IgnoredSourceValues entries must be defined on srcType.
        foreach (var src in cfg.IgnoredSourceValues)
        {
            if (srcType.IsEnum && !Enum.IsDefined(srcType, src))
                errors.Add(new ConfigurationError(
                    srcType, dstType, "(Ignore)",
                    $"Ignore source value '{src}' is not defined on {srcType.Name}."));
        }

        // Rule: Fallback must be defined on dstType.
        if (cfg.HasFallback && dstType.IsEnum)
        {
            if (!Enum.IsDefined(dstType, cfg.FallbackValue!))
                errors.Add(new ConfigurationError(
                    srcType, dstType, "(WithFallback)",
                    $"WithFallback value '{cfg.FallbackValue}' is not defined on {dstType.Name}."));
        }

        // Rule: foot-gun guard — Ignore + undefined default(dstType).
        if (cfg.IgnoredSourceValues.Count > 0 && dstType.IsEnum)
        {
            var defaultDst = Activator.CreateInstance(dstType)!;
            if (!Enum.IsDefined(dstType, defaultDst))
                errors.Add(new ConfigurationError(
                    srcType, dstType, "(Ignore)",
                    $"Ignore() would produce default({dstType.Name}) which is not a defined enum value (zero value undefined). Use MapValue with an explicit destination instead."));
        }
    }

    private static void ValidateEnumStrict(TypeMap tm, List<ConfigurationError> errors)
    {
        // Strict mode applies only to typemaps where BOTH sides are enum types.
        // (Auto-conversions at the property level — enum→string, string→enum, etc. — are not
        // validated here; they're not registered typemaps.)
        if (!tm.SourceType.IsEnum || !tm.DestinationType.IsEnum) return;

        var cfg = tm.EnumConfig ?? new EnumMapConfig();   // null → use defaults (ByValue)
        var uncovered = new List<object>();

        foreach (var definedSrc in Enum.GetValues(tm.SourceType))
        {
            var action = EnumResolver.Resolve(definedSrc, cfg, tm.SourceType, tm.DestinationType);
            if (action.Kind == EnumResolver.ActionKind.Throw)
                uncovered.Add(definedSrc);
        }

        if (uncovered.Count > 0)
        {
            var list = string.Join(", ", uncovered);
            errors.Add(new ConfigurationError(
                tm.SourceType, tm.DestinationType, "(strict)",
                $"Strict enum validation: source values [{list}] have no mapping. Declare MapValue / Ignore for each, or WithFallback for a catch-all."));
        }
    }

    private static void ValidateInheritance(TypeMap tm, MapperRegistry registry, List<ConfigurationError> errors)
    {
        // Rule 1: abstract type without any Include is unreachable.
        if ((tm.SourceType.IsAbstract || tm.DestinationType.IsAbstract) && tm.IncludedDerived.Count == 0)
        {
            errors.Add(new ConfigurationError(
                tm.SourceType, tm.DestinationType, "(map)",
                "Abstract type used without any Include — map is unreachable."));
        }

        // Rule 2: each Include must point at a registered map.
        foreach (var derivedPair in tm.IncludedDerived)
        {
            if (registry.GetTypeMap(derivedPair) is null)
            {
                errors.Add(new ConfigurationError(
                    tm.SourceType, tm.DestinationType, "(include)",
                    $"Include declares {derivedPair.Source.Name} -> {derivedPair.Destination.Name} but no such map is registered."));
            }
        }

        // Rule 3: each Include's types must derive from the base map's types.
        foreach (var derivedPair in tm.IncludedDerived)
        {
            if (!tm.SourceType.IsAssignableFrom(derivedPair.Source) ||
                !tm.DestinationType.IsAssignableFrom(derivedPair.Destination))
            {
                errors.Add(new ConfigurationError(
                    tm.SourceType, tm.DestinationType, "(include)",
                    $"Include's source/destination type ({derivedPair.Source.Name} -> {derivedPair.Destination.Name}) does not derive from the base map's source/destination type."));
            }
        }

        // Rule 4: each IncludeBase must point at a registered map.
        foreach (var basePair in tm.IncludedBases)
        {
            if (registry.GetTypeMap(basePair) is null)
            {
                errors.Add(new ConfigurationError(
                    tm.SourceType, tm.DestinationType, "(include-base)",
                    $"IncludeBase references {basePair.Source.Name} -> {basePair.Destination.Name} but no such map is registered."));
            }
        }
    }
}
