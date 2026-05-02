using System.Reflection;

namespace Atlas.Internal;

/// <summary>
/// Implements the <c>AssertConfigurationIsValid</c> algorithm from design §8. Pure: walks the
/// already-built <see cref="MapperRegistry"/> and reports every problem in one pass so the
/// developer sees them all at once.
/// </summary>
internal static class ConfigurationValidator
{
    public static void Validate(MapperRegistry registry)
    {
        var errors = new List<ConfigurationError>();
        foreach (var tm in registry.AllTypeMaps)
        {
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
        if (HasImplicitNumericConversion(src, dst)) return true;
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

    private static bool HasImplicitNumericConversion(Type src, Type dst) =>
        (src, dst) switch
        {
            _ when src == typeof(sbyte) => dst == typeof(short) || dst == typeof(int) || dst == typeof(long) || dst == typeof(float) || dst == typeof(double) || dst == typeof(decimal),
            _ when src == typeof(byte) => dst == typeof(short) || dst == typeof(ushort) || dst == typeof(int) || dst == typeof(uint) || dst == typeof(long) || dst == typeof(ulong) || dst == typeof(float) || dst == typeof(double) || dst == typeof(decimal),
            _ when src == typeof(short) => dst == typeof(int) || dst == typeof(long) || dst == typeof(float) || dst == typeof(double) || dst == typeof(decimal),
            _ when src == typeof(ushort) => dst == typeof(int) || dst == typeof(uint) || dst == typeof(long) || dst == typeof(ulong) || dst == typeof(float) || dst == typeof(double) || dst == typeof(decimal),
            _ when src == typeof(int) => dst == typeof(long) || dst == typeof(float) || dst == typeof(double) || dst == typeof(decimal),
            _ when src == typeof(uint) => dst == typeof(long) || dst == typeof(ulong) || dst == typeof(float) || dst == typeof(double) || dst == typeof(decimal),
            _ when src == typeof(long) => dst == typeof(float) || dst == typeof(double) || dst == typeof(decimal),
            _ when src == typeof(ulong) => dst == typeof(float) || dst == typeof(double) || dst == typeof(decimal),
            _ when src == typeof(float) => dst == typeof(double),
            _ when src == typeof(char) => dst == typeof(ushort) || dst == typeof(int) || dst == typeof(uint) || dst == typeof(long) || dst == typeof(ulong) || dst == typeof(float) || dst == typeof(double) || dst == typeof(decimal),
            _ => false,
        };
}
