using System.Linq.Expressions;
using System.Reflection;

namespace Atlas.Internal;

/// <summary>
/// One destination-binding mapping rule. Three mutually-exclusive resolution states:
/// source-resolved (carries a member path or expression), constant (carries a value), or ignored.
/// Bindings target either a property (<see cref="DestinationProperty"/> set) or a constructor
/// parameter (<see cref="DestinationCtorParameter"/> set).
/// </summary>
internal sealed class PropertyMap
{
    public string Name { get; }
    public Type DestinationType { get; }
    public PropertyInfo? DestinationProperty { get; }
    public ParameterInfo? DestinationCtorParameter { get; }

    public SourceMemberPath? SourcePath { get; set; }
    public LambdaExpression? CustomExpression { get; set; }
    public object? ConstantValue { get; set; }
    public bool HasConstant { get; set; }
    public bool Ignored { get; set; }
    /// <summary>
    /// True when this binding was configured via <c>ForMember</c> / <c>ForCtorParam</c> /
    /// <c>ForPath</c> (an explicit user choice). False when populated by <c>ConventionEngine</c>
    /// or ReverseMapMirror. Used by <c>InheritanceMerger</c> as the precedence
    /// discriminator: derived explicit beats base explicit beats derived convention.
    /// Also used by ReverseMapMirror skip-rule-2 (the user-explicit top-level guard).
    /// </summary>
    public bool IsExplicit { get; set; }

    /// <summary>
    /// Non-null when this binding writes into a nested destination chain (e.g.,
    /// Customer.Name) rather than a single property. The leaf is the writable target;
    /// intermediates are auto-instantiated at runtime via parameterless constructor.
    /// When null, <see cref="DestinationProperty"/> is used (single-level write — current
    /// behavior).
    /// </summary>
    public IReadOnlyList<PropertyInfo>? DestinationPath { get; set; }

    public bool IsResolved => Ignored || HasConstant || CustomExpression is not null || SourcePath is not null;

    private PropertyMap(string name, Type destinationType, PropertyInfo? prop, ParameterInfo? ctorParam)
    {
        Name = name;
        DestinationType = destinationType;
        DestinationProperty = prop;
        DestinationCtorParameter = ctorParam;
    }

    public static PropertyMap ForProperty(PropertyInfo property) =>
        new(property.Name, property.PropertyType, property, null);

    public static PropertyMap ForCtorParam(ParameterInfo parameter) =>
        new(parameter.Name ?? throw new ArgumentException("Constructor parameter must have a name.", nameof(parameter)),
            parameter.ParameterType, null, parameter);

    /// <summary>
    /// Factory for nested-path bindings. Produces a PropertyMap whose <see cref="Name"/>
    /// is the dotted path ("Customer.Name") for diagnostics, whose
    /// <see cref="DestinationProperty"/> is the leaf (so existing consumers like
    /// <c>ConventionEngine</c> and <c>ConfigurationValidator</c> see a stable
    /// "single property" view), and whose <see cref="DestinationPath"/> carries the full chain.
    /// </summary>
    public static PropertyMap ForPath(IReadOnlyList<PropertyInfo> path)
    {
        if (path is null || path.Count == 0)
            throw new ArgumentException("Path must contain at least one property.", nameof(path));
        var leaf = path[^1];
        var pm = new PropertyMap(string.Join('.', path.Select(p => p.Name)),
                                 leaf.PropertyType, leaf, null);
        pm.DestinationPath = path;
        return pm;
    }
}
