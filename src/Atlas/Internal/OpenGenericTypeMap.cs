namespace Atlas.Internal;

/// <summary>
/// Registration template for an open-generic class map. Different shape from
/// <see cref="TypeMap"/> — has no <see cref="TypeMap.PropertyMaps"/>; those are derived
/// per closed pair via the convention engine at materialization time.
/// </summary>
internal sealed class OpenGenericTypeMap
{
    public Type SourceTypeDefinition { get; }
    public Type DestinationTypeDefinition { get; }
    public MemberList MemberList { get; }
    public string RegistrationOrigin { get; }
    public MapperProfile? OriginatingProfile { get; }
    public bool PreserveReferences { get; set; }

    public OpenGenericTypeMap(
        Type sourceTypeDefinition,
        Type destinationTypeDefinition,
        MemberList memberList,
        string registrationOrigin,
        MapperProfile? originatingProfile = null)
    {
        SourceTypeDefinition = sourceTypeDefinition;
        DestinationTypeDefinition = destinationTypeDefinition;
        MemberList = memberList;
        RegistrationOrigin = registrationOrigin;
        OriginatingProfile = originatingProfile;
    }

    /// <summary>
    /// True when this template can materialize a <see cref="TypeMap"/> for the given
    /// closed pair — i.e., both source and destination are constructed-generic types
    /// whose generic-type-definitions match the registered template.
    /// </summary>
    public bool Matches(TypePair closedPair)
    {
        if (!closedPair.Source.IsConstructedGenericType) return false;
        if (!closedPair.Destination.IsConstructedGenericType) return false;
        return closedPair.Source.GetGenericTypeDefinition() == SourceTypeDefinition
            && closedPair.Destination.GetGenericTypeDefinition() == DestinationTypeDefinition;
    }
}
