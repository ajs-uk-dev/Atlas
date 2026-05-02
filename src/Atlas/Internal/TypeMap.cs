namespace Atlas.Internal;

/// <summary>
/// Configuration for a single (source, destination) type pair. Mutable during configuration build,
/// frozen by <see cref="Seal"/> when the configuration is constructed.
/// </summary>
internal sealed class TypeMap
{
    public Type SourceType { get; }
    public Type DestinationType { get; }
    public MemberList MemberList { get; set; }
    public List<PropertyMap> PropertyMaps { get; } = new();
    public Delegate? CustomConverter { get; set; }
    public bool IsSealed { get; private set; }

    public TypePair Pair => new(SourceType, DestinationType);

    public TypeMap(Type sourceType, Type destinationType, MemberList memberList)
    {
        SourceType = sourceType;
        DestinationType = destinationType;
        MemberList = memberList;
    }

    public void EnsureMutable()
    {
        if (IsSealed)
            throw new InvalidOperationException(
                $"TypeMap {SourceType.Name} -> {DestinationType.Name} is sealed and cannot be modified.");
    }

    public void Seal() => IsSealed = true;
}
