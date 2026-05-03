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

    /// <summary>
    /// (TDerivedSource, TDerivedDestination) pairs declared via <c>Include</c> on this map,
    /// or via <c>IncludeBase</c> on a derived map (resolved into this list at config-build
    /// time by <c>InheritanceMerger.Resolve</c>). Sorted most-derived-first after
    /// <see cref="Seal"/>. Empty when inheritance isn't used.
    /// </summary>
    public List<TypePair> IncludedDerived { get; } = new();

    /// <summary>
    /// (TBaseSource, TBaseDestination) pairs declared via <c>IncludeBase</c> on this map.
    /// Used at config-build time to propagate this pair into each base's
    /// <see cref="IncludedDerived"/>, and to merge base config into this map's
    /// <see cref="PropertyMaps"/>.
    /// </summary>
    public List<TypePair> IncludedBases { get; } = new();

    /// <summary>
    /// Per-typemap enum customization (strategy, per-value overrides, ignored source values,
    /// fallback). Null unless an enum-method has been called on the fluent surface; null also
    /// for non-enum typemaps. Compilation honours null as "use default ByValue strategy with
    /// no overrides" for typemaps where source/dest are both enums.
    /// </summary>
    public EnumMapConfig? EnumConfig { get; set; }

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
