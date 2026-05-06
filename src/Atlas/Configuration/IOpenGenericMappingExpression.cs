namespace Atlas.Configuration;

/// <summary>
/// Fluent surface returned by <see cref="MapperConfigurationExpression.CreateMap(Type, Type, MemberList)"/>
/// (and the matching <see cref="MapperProfile"/> overload). Provides a minimal opt-in surface for
/// open-generic registrations. v1 exposes only PreserveReferences; future versions may add per-template
/// configuration (e.g. ConvertUsing for open generics — currently a v3 follow-up).
/// </summary>
public interface IOpenGenericMappingExpression
{
    /// <summary>
    /// Marks the open-generic template as cycle-safe. Every closed-pair materialization derived
    /// from this template will inherit <see cref="Internal.TypeMap.PreserveReferences"/> = true.
    /// See docs/Atlas-Design-ReferenceHandling.md §7.7.
    /// </summary>
    /// <returns>This expression, for fluent chaining.</returns>
    IOpenGenericMappingExpression PreserveReferences();
}
