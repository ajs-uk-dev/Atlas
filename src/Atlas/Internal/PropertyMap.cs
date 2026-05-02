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
}
