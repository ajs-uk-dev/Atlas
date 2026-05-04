using System.Linq.Expressions;

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

    /// <summary>
    /// When this map was created via <c>.ReverseMap()</c> on another map, points back to
    /// that forward pair. Used by ReverseMapMirror to know which forward to
    /// read from, AND by the conflict guard in <c>MapperConfigurationExpression</c> to
    /// detect duplicate registrations. Null for maps registered directly via
    /// <see cref="MapperProfile.CreateMap{TSource,TDestination}"/>.
    /// </summary>
    public TypePair? ReverseMapPair { get; set; }

    /// <summary>
    /// Cached reverse <c>MappingExpression</c> instance for idempotent <c>.ReverseMap()</c>
    /// calls. Boxed as <c>object?</c> because the generic args differ from the forward map's.
    /// Set by the first <c>.ReverseMap()</c> call on the corresponding forward
    /// <c>MappingExpression</c>; null on the reverse TypeMap and on TypeMaps that were
    /// never reversed.
    /// </summary>
    public object? CachedReverseExpression { get; set; }

    /// <summary>
    /// Human-readable origin string for diagnostic messages
    /// (<c>"CreateMap&lt;Order, OrderDto&gt;()"</c> or
    /// <c>"CreateMap&lt;Order, OrderDto&gt;().ReverseMap()"</c>). Set at construction in
    /// <see cref="MapperProfile.CreateMap"/>, <c>MapperConfigurationExpression.CreateMap</c>,
    /// and <c>MappingExpression.ReverseMap</c>. Empty string for TypeMaps constructed in
    /// tests that don't care about the origin.
    /// </summary>
    public string RegistrationOrigin { get; set; } = string.Empty;

    /// <summary>
    /// Hooks that run BEFORE any destination member is mapped, in FIFO order (registration
    /// order at the user's call site). After <c>InheritanceMerger</c> runs, this list also
    /// contains base TypeMaps' BeforeHooks prepended at the front (base-first order).
    /// </summary>
    public List<HookEntry> BeforeHooks { get; } = new();

    /// <summary>
    /// Hooks that run AFTER all destination members are mapped, in FIFO order. After
    /// <c>InheritanceMerger</c> runs, this list also contains base TypeMaps' AfterHooks
    /// appended at the end (so unwind goes derived-first then base-last, pairing with
    /// <see cref="BeforeHooks"/>'s base-first order).
    /// </summary>
    public List<HookEntry> AfterHooks { get; } = new();

    /// <summary>
    /// Type-map-scoped value transformers (declared via <c>IMappingExpression.AddTransform</c>),
    /// keyed by destination property type. Multiple transformers for the same type are stored
    /// in registration (FIFO) order. Populated at fluent-call time; consumed by
    /// <c>TransformerResolver</c> at config-build time.
    /// </summary>
    public Dictionary<Type, List<LambdaExpression>> TypeMapTransformers { get; } = new();

    /// <summary>
    /// Resolved (effective) transformers per destination type, computed by
    /// <c>TransformerResolver</c> by composing global → profile → type-map. Within each scope,
    /// FIFO order. The list is the application order: <c>Effective[T][0]</c> runs first on
    /// the raw source value; <c>Effective[T][^1]</c> runs last and produces the final value
    /// assigned to the destination property. Empty (no entry) means no transformers apply
    /// for that type.
    /// </summary>
    public Dictionary<Type, IReadOnlyList<LambdaExpression>> EffectiveTransformers { get; } = new();

    /// <summary>
    /// Backref to the <see cref="MapperProfile"/> that registered this map (when registered
    /// via <c>AddProfile</c>). Null for maps registered directly via
    /// <see cref="MapperConfigurationExpression.CreateMap"/>. Used by
    /// <c>TransformerResolver</c> to identify which profile-level transformers apply.
    /// </summary>
    public MapperProfile? OriginatingProfile { get; set; }

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
