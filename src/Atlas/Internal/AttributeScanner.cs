using System.Reflection;

namespace Atlas.Internal;

/// <summary>
/// Discovers <see cref="AutoMapAttribute"/>-decorated types in scanned assemblies and registers
/// each into the configuration via the same fluent calls a hand-written profile would make.
/// See <c>docs/Atlas-Design-AttributeConfig.md</c> §4.1 / §5.
/// </summary>
internal static class AttributeScanner
{
    /// <summary>
    /// Top-level entry point. Enumerates public top-level non-abstract decorated types and
    /// processes each. Errors are accumulated; a fatal duplicate-pair throws immediately.
    /// </summary>
    public static void Discover(Assembly assembly, MapperConfigurationExpression cfg)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(cfg);

        var errors = new List<ConfigurationError>();
        foreach (var type in assembly.GetTypes())
        {
            if (!IsAttributeMapCandidate(type))
                continue;

            ProcessAutoMapType(type, cfg, errors);
        }

        if (errors.Count > 0)
            throw new AtlasConfigurationException(errors);
    }

    /// <summary>
    /// True when <paramref name="t"/> is a top-level public non-abstract non-interface
    /// non-nested non-enum class decorated with <see cref="AutoMapAttribute"/>.
    /// Static classes (encoded as <c>IsAbstract &amp;&amp; IsSealed</c>) are excluded.
    /// </summary>
    public static bool IsAttributeMapCandidate(Type t)
    {
        return t.IsClass
            && t.IsPublic
            && !t.IsAbstract
            && !t.IsNested
            && t.GetCustomAttribute<AutoMapAttribute>(inherit: false) is not null;
    }

    /// <summary>
    /// Translates one [AutoMap]-decorated type into fluent calls. Validation (Task 3) is
    /// applied first; invalid registrations accumulate errors and return early.
    /// CreateMap + member attribute application + class-level flags land in Tasks 5/6/7/8.
    /// </summary>
    private static void ProcessAutoMapType(Type decoratedType, MapperConfigurationExpression cfg, List<ConfigurationError> errors)
    {
        var attr = decoratedType.GetCustomAttribute<AutoMapAttribute>(inherit: false)!;
        if (!ValidateAutoMapTarget(decoratedType, attr, errors))
            return;

        // CreateMap + member attribute application + class-level flags lands in Tasks 5/6/7/8.
    }

    /// <summary>
    /// Validates the [AutoMap] decorated type and its source type against §6 rules 1-3.
    /// Adds a <see cref="ConfigurationError"/> for each violation and returns false if any
    /// violation is found. Returns true when the pair is valid.
    /// </summary>
    private static bool ValidateAutoMapTarget(Type decoratedType, AutoMapAttribute attr, List<ConfigurationError> errors)
    {
        var srcType = attr.SourceType;
        var dstType = decoratedType;

        // Rule: dest is open-generic
        if (dstType.IsGenericTypeDefinition || dstType.ContainsGenericParameters)
        {
            errors.Add(new(srcType, dstType, "(register)",
                $"[AutoMap] applied to open-generic type '{FormatTypeName(dstType)}'. " +
                $"Use cfg.CreateMap(typeof(Source<>), typeof(Dest<>)) for open-generic registrations."));
            return false;
        }

        // Rule: dest is enum (defense in depth; AttributeUsage(Class) usually catches at compile)
        if (dstType.IsEnum)
        {
            errors.Add(new(srcType, dstType, "(register)",
                $"[AutoMap] applied to enum '{dstType.Name}'. Use cfg.CreateMap<TSrcEnum, {dstType.Name}>().MapByName() (or similar) for enum-to-enum mappings."));
            return false;
        }

        // Rule: dest is interface (filter usually catches; defense in depth)
        if (dstType.IsInterface)
        {
            errors.Add(new(srcType, dstType, "(register)",
                $"[AutoMap] applied to interface '{dstType.Name}'. Atlas cannot instantiate interfaces; use a concrete destination type."));
            return false;
        }

        // Rule: dest is static (encoded as IsAbstract && IsSealed in CLR)
        if (dstType is { IsAbstract: true, IsSealed: true })
        {
            errors.Add(new(srcType, dstType, "(register)",
                $"[AutoMap] applied to static type '{dstType.Name}'. Static types cannot be mapping destinations."));
            return false;
        }

        // Rule: dest is abstract (filter usually catches; defense in depth — IsAbstract without IsSealed)
        if (dstType.IsAbstract)
        {
            errors.Add(new(srcType, dstType, "(register)",
                $"[AutoMap] applied to abstract type '{dstType.Name}'. Atlas cannot instantiate abstract destinations."));
            return false;
        }

        // Rule: source is open-generic
        if (srcType.IsGenericTypeDefinition)
        {
            errors.Add(new(srcType, dstType, "(register)",
                $"[AutoMap] on '{dstType.Name}' specifies open-generic source type '{FormatTypeName(srcType)}'. " +
                $"Open generics use cfg.CreateMap(typeof(Source<>), typeof(Dest<>)) — " +
                $"attribute syntax is not supported for open generics."));
            return false;
        }

        // Rule: source is a recognized dynamic shape
        if (DynamicShape.IsDynamicShape(srcType))
        {
            errors.Add(new(srcType, dstType, "(register)",
                $"[AutoMap] on '{dstType.Name}' specifies a recognized dynamic shape ('{FormatTypeName(srcType)}'). " +
                $"Dynamic mapping is convention-only and requires no registration — remove the attribute and call mapper.Map<{dstType.Name}>(dictInstance) directly. " +
                $"To explicitly register a non-dynamic mapping for this pair, use cfg.CreateMap<{FormatTypeName(srcType)}, {dstType.Name}>() in a profile."));
            return false;
        }

        return true;
    }

    private static string FormatTypeName(Type t)
    {
        var keyword = CSharpKeywordFor(t);
        if (keyword is not null) return keyword;
        if (!t.IsGenericType) return t.Name;
        var name = t.Name;
        var tickIdx = name.IndexOf('`');
        if (tickIdx >= 0) name = name[..tickIdx];
        if (t.IsGenericTypeDefinition) return $"{name}<>";
        var args = string.Join(", ", t.GetGenericArguments().Select(FormatTypeName));
        return $"{name}<{args}>";
    }

    private static string? CSharpKeywordFor(Type t)
    {
        if (t == typeof(string)) return "string";
        if (t == typeof(object)) return "object";
        if (t == typeof(bool)) return "bool";
        if (t == typeof(byte)) return "byte";
        if (t == typeof(sbyte)) return "sbyte";
        if (t == typeof(short)) return "short";
        if (t == typeof(ushort)) return "ushort";
        if (t == typeof(int)) return "int";
        if (t == typeof(uint)) return "uint";
        if (t == typeof(long)) return "long";
        if (t == typeof(ulong)) return "ulong";
        if (t == typeof(float)) return "float";
        if (t == typeof(double)) return "double";
        if (t == typeof(decimal)) return "decimal";
        if (t == typeof(char)) return "char";
        return null;
    }
}
