using System.Reflection;
using System.Runtime.ExceptionServices;

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
    /// Translates one [AutoMap]-decorated type into fluent calls. Stub in Task 2 — fully
    /// implemented across Tasks 3 (validation), 4 (path resolution), 5 (CreateMap +
    /// class-level flags), 6/7/8 (member attributes).
    /// </summary>
    private static void ProcessAutoMapType(Type decoratedType, MapperConfigurationExpression cfg, List<ConfigurationError> errors)
    {
        // Task 2 stub. Real logic lands in Tasks 3-8.
    }
}
