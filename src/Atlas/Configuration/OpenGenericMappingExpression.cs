using Atlas.Internal;

namespace Atlas.Configuration;

/// <summary>
/// Concrete fluent expression wrapping an <see cref="OpenGenericTypeMap"/>. Implements
/// <see cref="IOpenGenericMappingExpression"/>. Returned by
/// <see cref="MapperConfigurationExpression.CreateMap(Type, Type, MemberList)"/> and the matching
/// <see cref="MapperProfile"/> overload.
/// </summary>
internal sealed class OpenGenericMappingExpression : IOpenGenericMappingExpression
{
    private readonly OpenGenericTypeMap _template;

    internal OpenGenericMappingExpression(OpenGenericTypeMap template)
    {
        _template = template;
    }

    public IOpenGenericMappingExpression PreserveReferences()
    {
        _template.PreserveReferences = true;
        return this;
    }
}
