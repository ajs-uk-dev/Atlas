# Atlas v2 — Open Generics

> **Status:** Design approved 2026-05-05. Implementation plan: `Atlas-Plan-OpenGenerics.md` (to be written next).
> **Spec inputs:** `Object-Mapping-Functional-Reference.md` §12 (Open Generics), `AutoMapper-Analysis.md` §10.2 (`CreateMap(typeof(Source<>), typeof(Destination<>))`).
> **Position in v2 roadmap:** Feature #9 of 13 deferred groups. Builds on v1 + ProjectTo (#1) + Inheritance (#2) + Enum (#3) + Reverse Mapping (#4) + Hooks (#5) + Value Transformers (#6) + Conditional Mapping (#7) + Null Substitution (#8).

---

## 1. Goals & Non-Goals

### 1.1 Goal

Add open-generic class maps to Atlas. A single registration like `cfg.CreateMap(typeof(Source<>), typeof(Destination<>))` applies to every closed instantiation at runtime — `mapper.Map<Destination<int>>(srcInt)`, `mapper.Map<Destination<Customer>>(srcCustomer)`, and `query.ProjectTo<Destination<Order>>(...)` all resolve through one declaration via lazy materialization. The user writes one declaration; Atlas materializes a closed `TypeMap` per closed pair on first use, then caches it.

The headline example from `Object-Mapping-Functional-Reference.md` §12 — *"A single map definition can apply to every closed generic instantiation at runtime"* — works under this design.

### 1.2 In scope (v2 MVP)

1. New non-generic overloads:
   - `void MapperConfigurationExpression.CreateMap(Type sourceType, Type destinationType, MemberList memberList = MemberList.None)` — root config.
   - `void MapperProfile.CreateMap(Type sourceType, Type destinationType, MemberList memberList = MemberList.None)` — profile-scoped (mirror).
2. New internal type `OpenGenericTypeMap` — a registration template. Different shape from `TypeMap` (has no `PropertyMap`s; those are derived per closed pair via the convention engine at materialization time).
3. New private list `_openGenericMaps` on both `MapperConfigurationExpression` and `MapperProfile`. `AddProfile` propagates open-generic registrations from profile into root.
4. `MapperConfiguration` constructor collects open-generic registrations alongside closed `TypeMap`s and passes both into `MapperRegistry`.
5. `MapperRegistry` changes:
   - `_typeMaps` field type changes from `Dictionary<TypePair, TypeMap>` to `ConcurrentDictionary<TypePair, TypeMap>` for thread-safe lazy mutation.
   - New fields: `_openGenericMaps`, `_globalTransformers`, `_conventionOptions` (the latter two needed by lazy materialization to replay the same convention + transformer pipeline that closed registrations get at config time).
   - `GetTypeMap(TypePair)` extension: on closed-pair miss, scan `_openGenericMaps` for an arity-matching template whose generic-type-definitions match; on hit, materialize a closed `TypeMap` via `GetOrAdd` and return.
   - New private `MaterializeClosed(template, closedPair)`: constructs `TypeMap`, runs `ConventionEngine.ResolveMissingMembers`, runs `TransformerResolver.Resolve`, calls `Seal`.
   - New private `FindMatchingOpenGenericTemplate(closedPair)`: linear scan over `_openGenericMaps` returning the first match.
6. Two registration-time validation rules (in both `CreateMap(Type, Type)` overloads):
   - **Not-a-generic-type-definition**: throw `AtlasConfigurationException` if either argument fails `IsGenericTypeDefinition`.
   - **Arity mismatch**: throw if source and destination have different generic arities (e.g., `Source<>` vs `Destination<,>`).
7. Open-generic registrations are excluded from `AssertConfigurationIsValid()` per the reference doc — they can't be validated until closed.

### 1.3 Out of scope (deferred to a future v3 design doc)

- **Open-generic `ConvertUsing(typeof(Converter<>))`** — converter type closes alongside source/dest. Doable but requires reflection-based factory plumbing for closing the converter type and instantiating it per closed pair. Defer until user demand surfaces.
- **Per-member overrides on open generics** — C# can't write `Expression<Func<Source<>, ...>>` for unbound generics. AutoMapper's string-based `ForMember("PropertyName", ...)` workaround is awkward and rarely used. Users who need overrides on a specific closed pair register that closed pair separately (the closed-pair-takes-precedence rule applies).
- **`Include` / `IncludeBase` between open generics** — open-generic inheritance dispatch is complex (requires materialization to know the closed inheritance chain). Users who need polymorphic dispatch register specific closed pairs.
- **`.ReverseMap()` on open generics** — could work conceptually but adds another lazy-materialization path. Defer.
- **`BeforeMap` / `AfterMap` / `AddTransform` / `NullSubstitute` / `Condition` / `PreCondition` on open generics** — same expression-tree-on-unbound-generic problem. Per-member features apply only to manually-registered closed pairs.
- **Validation of open-generic templates beyond arity matching** — per the reference doc, "not every closed combination is valid". User must register-and-validate per closed pair if they care.
- **Pre-materialization at config-build time** — the closed-pair space is unbounded. The user implicitly chooses which closed pairs exist by which `Map<>()` / `ProjectTo<>()` calls they make.

### 1.4 Non-goals (out of scope permanently for this feature)

- Discovering open-generic registrations by attribute or convention without an explicit `CreateMap(Type, Type)` call.
- Generic constraints on the type parameters (e.g., "this open generic only matches closed pairs where T : IComparable"). Closed pairs that fail to materialize fail at runtime via the existing `default(TMember)` path or via `Expression.Bind` errors during codegen — Atlas doesn't enforce constraints at the open-generic level.
- Variance handling beyond what `Type.GetGenericTypeDefinition` naturally provides.

---

## 2. Architecture Overview

### 2.1 What changes

- **`MapperConfigurationExpression`** gains a `CreateMap(Type, Type, MemberList)` overload + `_openGenericMaps` field + `GetOpenGenericMaps()` accessor. `AddProfile` propagates profile open-generic registrations.
- **`MapperProfile`** gains a `CreateMap(Type, Type, MemberList)` overload + `_openGenericMaps` field + `GetOpenGenericMaps()` accessor.
- **`OpenGenericTypeMap`** — new internal record with `SourceTypeDefinition`, `DestinationTypeDefinition`, `MemberList`, `RegistrationOrigin`, `OriginatingProfile`, and `Matches(TypePair)` method.
- **`MapperRegistry`** — `_typeMaps` field type changes to `ConcurrentDictionary`; constructor gains 3 new optional parameters; `GetTypeMap` gains lookup-and-materialize fallback; two new private methods (`FindMatchingOpenGenericTemplate`, `MaterializeClosed`).
- **`MapperConfiguration`** — both constructors pass the new parameters to `MapperRegistry`.

### 2.2 What does NOT change

- **`TypeMap`** — unchanged. Materialized closed pairs are normal `TypeMap`s.
- **`PropertyMap`** — unchanged.
- **`ConventionEngine`** — unchanged. Invoked by materialization with the same parameters as config-time.
- **`TransformerResolver`** — unchanged. Invoked by materialization with the same parameters.
- **`InheritanceMerger`** — unchanged. Not invoked during materialization (open generics don't support `Include`).
- **`ReverseMapMirror`** — unchanged. Not invoked during materialization (open generics don't support `.ReverseMap()`).
- **`ExecutionPlanBuilder`** — unchanged. Compiles materialized closed maps the same as any closed map.
- **`ProjectionPlanBuilder`** — unchanged. Projects materialized closed maps the same as any closed map.
- **`ProjectionCompatibility`** — unchanged. Materialized closed maps are projectable by default.
- **`ConfigurationValidator`** — unchanged at the iteration layer; open-generic templates are simply not in `_typeMaps`.
- **Build-time pipeline order** — unchanged. Open-generic registrations are stored alongside but not processed during config-build.

### 2.3 Architecture: three components

**1. Registration.** `CreateMap(Type, Type)` validates arguments (`IsGenericTypeDefinition`, matching arity) and stores an `OpenGenericTypeMap` on `MapperConfigurationExpression` (or on `MapperProfile`, harvested into the configuration via `AddProfile`).

**2. Lookup.** `MapperRegistry.GetTypeMap(TypePair)` gains a fallback path. Closed-pair miss → linear scan over open registrations for an arity-matching template whose `GetGenericTypeDefinition()`s match → materialize on hit.

**3. Materialization.** Build a closed `TypeMap` for the requested pair, run `ConventionEngine.ResolveMissingMembers`, run `TransformerResolver.Resolve` for just this single map, seal, cache via `ConcurrentDictionary.GetOrAdd`.

The single point of insertion in `MapperRegistry.GetTypeMap` means **both `Atlas` core and `Atlas.Projections` get open-generic support automatically** — `ProjectionPlanBuilder.Build` calls `registry.GetTypeMap` directly.

### 2.4 Closed-pair-takes-precedence rule

When a user has registered both an open-generic `(Source<>, Destination<>)` and a specific closed pair `(Source<int>, Destination<int>)`, the closed-pair registration sits in `_typeMaps` from config-build time. Closed-pair lookup in `GetTypeMap` hits it BEFORE `_openGenericMaps` is ever scanned. The open-generic registration silently doesn't apply for `(Source<int>, Destination<int>)` — the user's explicit closed-pair config wins.

This is the documented escape hatch for users who need per-member overrides on a specific closed pair while keeping the open-generic template for everything else.

### 2.5 Why lazy materialization

Three reasons this is the only sensible choice:

1. **The closed-pair space is unbounded.** Open generics by definition don't enumerate possible type-arg combinations. Eager materialization would require either materialization-on-every-`Type` (impossible) or some other heuristic (always wrong).
2. **Pay for what you use.** Most apps that register open generics use only a handful of closed pairs in practice. Lazy materialization means zero cost for closed pairs that are never requested.
3. **Matches AutoMapper's contract.** Reference docs and AutoMapper both describe lazy materialization. Matching the established mental model reduces user surprise.

The cost of lazy materialization is a small first-call latency for each new closed pair (convention engine + transformer resolution + seal). Subsequent calls hit the lock-free `ConcurrentDictionary` read path.

### 2.6 Why convention-only materialization

Open-generic registrations carry no `PropertyMap`s — they're templates. At materialization time, the convention engine populates the closed `TypeMap`'s `PropertyMap`s based on the closed source/destination types' actual public properties. This is identical to what would happen if the user manually registered the closed pair via `CreateMap<TSrc, TDst>(MemberList.None)` and never customized it.

The deliberate exclusion of per-member overrides at the open-generic level keeps the API narrow and forces a clear pattern: "use open generics for the convention case; explicit closed-pair registration for the customization case".

### 2.7 Thread safety

The hot path (closed-pair lookup) stays lock-free via `ConcurrentDictionary.TryGetValue`. The materialization path uses `ConcurrentDictionary.GetOrAdd`, which may invoke the materialization factory more than once under contention but only stores one returned value. Materialization is deterministic for a given `(template, closedPair)` — concurrent duplicate work is wasted CPU but never produces inconsistent results.

For the rare case where the wasted CPU matters, a future optimization could use `Lazy<TypeMap>` factories — explicitly out of scope for v1.

---

## 3. Public API Surface

### 3.1 `MapperConfigurationExpression.CreateMap(Type, Type)` overload

```csharp
namespace Atlas;

public sealed class MapperConfigurationExpression
{
    // ... existing CreateMap<TSource, TDestination> generic overload unchanged ...

    /// <summary>
    /// Registers an open-generic class map. A single registration applies to every closed
    /// instantiation at runtime — <c>mapper.Map&lt;Destination&lt;int&gt;&gt;(srcInt)</c>,
    /// <c>mapper.Map&lt;Destination&lt;Customer&gt;&gt;(srcCustomer)</c>, and
    /// <c>query.ProjectTo&lt;Destination&lt;Order&gt;&gt;(cfg)</c> all resolve through one
    /// declaration via lazy materialization.
    /// </summary>
    /// <param name="sourceType">
    /// An open generic type definition (e.g., <c>typeof(Source&lt;&gt;)</c>). Must satisfy
    /// <see cref="Type.IsGenericTypeDefinition"/>.
    /// </param>
    /// <param name="destinationType">
    /// An open generic type definition with the same generic arity as
    /// <paramref name="sourceType"/>.
    /// </param>
    /// <param name="memberList">
    /// Validation policy. Default <see cref="MemberList.None"/> — open-generic templates are
    /// excluded from <see cref="MapperConfiguration.AssertConfigurationIsValid"/> per the
    /// "not every closed combination is valid" rule. The materialized closed
    /// <see cref="System.Type"/> inherits this policy; pass
    /// <see cref="MemberList.Destination"/> if you want every materialized closed pair
    /// validated lazily on first use.
    /// </param>
    /// <remarks>
    /// On lookup of a closed pair, Atlas first checks the closed-pair registry; on miss,
    /// scans open-generic registrations for an arity-matching template whose
    /// <see cref="Type.GetGenericTypeDefinition"/> matches both source and destination
    /// types. On match, a closed <c>TypeMap</c> is materialized via the convention engine +
    /// transformer resolver and cached under the closed pair.
    /// <para>
    /// <b>Closed pairs registered separately take precedence over open-generic matches.</b>
    /// To customize a specific closed pair (per-member <c>ForMember</c>, <c>Include</c>,
    /// <c>BeforeMap</c>, <c>AddTransform</c>, <c>NullSubstitute</c>, etc.), register the
    /// closed pair via the generic <c>CreateMap&lt;TSrc, TDst&gt;()</c> overload — the
    /// closed-pair registration short-circuits the open-generic lookup.
    /// </para>
    /// <para>
    /// <b>Open-generic registrations are convention-only.</b> No fluent surface is exposed
    /// — no per-member overrides, no <c>Include</c>, no <c>ConvertUsing</c>,
    /// no <c>BeforeMap</c>/<c>AfterMap</c>, no <c>AddTransform</c>, no <c>NullSubstitute</c>,
    /// no <c>.ReverseMap()</c>. Users needing any of these register the specific closed pair.
    /// </para>
    /// <para>
    /// Global and profile-level value transformers
    /// (<see cref="ValueTransformers"/> and <see cref="MapperProfile.ValueTransformers"/>)
    /// DO apply to materialized closed pairs — the materialization runs
    /// <c>TransformerResolver</c> exactly as closed registrations do at config-build time.
    /// </para>
    /// <para>
    /// Open-generic templates are NOT validated by
    /// <see cref="MapperConfiguration.AssertConfigurationIsValid"/>. Materialized closed
    /// pairs that exist by validation time will be validated as a side effect of being in
    /// the closed-pair registry, but users should not depend on this — register the closed
    /// pair explicitly if validation is required.
    /// </para>
    /// </remarks>
    /// <exception cref="AtlasConfigurationException">
    /// Thrown at registration time if either type is not an open generic type definition,
    /// or if the source and destination have different generic arities.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// Thrown if <paramref name="sourceType"/> or <paramref name="destinationType"/> is null.
    /// </exception>
    public void CreateMap(Type sourceType, Type destinationType,
                          MemberList memberList = MemberList.None);
}
```

### 3.2 `MapperProfile.CreateMap(Type, Type)` overload

Mirror of the root-config method, scoped to the profile. Profile-level transformers apply to materialized closed pairs that were registered via this profile.

```csharp
public abstract class MapperProfile
{
    // ... existing CreateMap<TSource, TDestination> generic overload unchanged ...

    /// <summary>
    /// Registers an open-generic class map scoped to this profile. See
    /// <see cref="MapperConfigurationExpression.CreateMap(Type, Type, MemberList)"/> for
    /// full semantics. Profile-level value transformers apply to materialized closed pairs
    /// registered via this profile.
    /// </summary>
    /// <exception cref="AtlasConfigurationException">
    /// Thrown at registration time if either type is not an open generic type definition,
    /// or if the source and destination have different generic arities.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// Thrown if <paramref name="sourceType"/> or <paramref name="destinationType"/> is null.
    /// </exception>
    protected void CreateMap(Type sourceType, Type destinationType,
                             MemberList memberList = MemberList.None);
}
```

### 3.3 Usage examples

**Simple open generic:**

```csharp
public class Wrapper<T>
{
    public T Value { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<T> History { get; set; } = new();
}

public class WrapperDto<T>
{
    public T Value { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<T> History { get; set; } = new();
}

var cfg = new MapperConfiguration(c =>
{
    c.CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>));
});
var mapper = cfg.CreateMapper();

var intDto = mapper.Map<WrapperDto<int>>(new Wrapper<int> { Value = 42 });
var stringDto = mapper.Map<WrapperDto<string>>(new Wrapper<string> { Value = "hi" });
```

**Closed-pair override coexists with open-generic template:**

```csharp
var cfg = new MapperConfiguration(c =>
{
    c.CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>));   // open template
    c.CreateMap<Wrapper<Customer>, WrapperDto<Customer>>()  // closed override
        .ForMember(d => d.Value, opt => opt.MapFrom(s => s.Value with { IsActive = true }));
});
// Wrapper<int>, Wrapper<string>, etc. → use the open template.
// Wrapper<Customer> → uses the closed override.
```

**Profile-scoped open generic:**

```csharp
public class WrapperProfile : MapperProfile
{
    public WrapperProfile()
    {
        CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>));
        ValueTransformers.Add<string>(s => s.Trim());   // applies to materialized Wrapper<string>
    }
}

var cfg = new MapperConfiguration(c => c.AddProfile<WrapperProfile>());
```

**Higher arity (e.g., `Tuple<,>`):**

```csharp
c.CreateMap(typeof(Tuple<,>), typeof(KeyValuePair<,>));
// Now mapper.Map<KeyValuePair<int, string>>(Tuple<int, string>) materializes via this template.
```

**With `MemberList.Destination` for lazy validation:**

```csharp
c.CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>), MemberList.Destination);
// Each materialized closed pair will, when AssertConfigurationIsValid is called AFTER first use,
// validate that every destination member is mapped.
```

### 3.4 Registration-time errors

| Error | Example | Behavior |
|---|---|---|
| Not a generic type definition | `CreateMap(typeof(Wrapper<int>), typeof(WrapperDto<>))` | `AtlasConfigurationException` at registration ("Source must be an open generic type definition; got `Wrapper<Int32>`. Use CreateMap<TSource, TDestination>() for closed types."). |
| Mismatched arity | `CreateMap(typeof(Source<>), typeof(Destination<,>))` | `AtlasConfigurationException` at registration ("Generic arity mismatch: source has 1 type parameter, destination has 2."). |
| Null argument | `CreateMap(null!, typeof(Destination<>))` | `ArgumentNullException`. |
| Duplicate open-generic registration | `CreateMap(typeof(Source<>), typeof(Destination<>))` twice | Silent — both entries are added to `_openGenericMaps`. The first match in `FindMatchingOpenGenericTemplate`'s linear scan wins (registration-order). Practically rare; documented as "don't do this". |

### 3.5 Interactions documented in the API XML

| Other feature | Interaction |
|---|---|
| Closed-pair `CreateMap<TSrc, TDst>` for the same closed instantiation | Closed-pair registration takes precedence; open-generic lookup short-circuits. |
| `MapperProfile` | Both root and profile registrations work. Materialized closed pairs inherit `OriginatingProfile` from the registering profile. |
| Global value transformers | Apply to materialized closed pairs via the same `TransformerResolver` invocation. |
| Profile value transformers | Apply when the open-generic was registered on a profile (template carries `OriginatingProfile`; materialization uses it). |
| Hooks (#5) / Conditional Mapping (#7) / Null Substitution (#8) | Per-member features — not configurable on open generics (no fluent surface). Apply only to manually-registered closed pairs. |
| `Include` / `IncludeBase` | Not supported on open generics. Manually register the closed pair if needed. |
| `.ReverseMap()` | Not supported on open generics. Manually register the reverse closed pair. |
| `ProjectTo<TDest>(query)` | Works automatically — `ProjectionPlanBuilder` uses `MapperRegistry.GetTypeMap` which now does open-generic lookup-and-materialize. |
| `AssertConfigurationIsValid()` | Open-generic templates are excluded. Materialized closed pairs that exist by validation time are validated as a side effect of being in the closed-pair registry. |
| Identity mapping (e.g., `WrapperDto<T>.History: List<T>` ← `Wrapper<T>.History: List<T>`) | Existing Atlas behavior: same-typed properties pass through by reference (no element-wise copy). For deep-copying, register the element type's map (e.g., `CreateMap<Customer, CustomerDto>()`) and the convention engine will use it. |

---

## 4. Internal Data Shape

### 4.1 `OpenGenericTypeMap` — new internal record

```csharp
// src/Atlas/Internal/OpenGenericTypeMap.cs (new file)
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
```

### 4.2 `MapperConfigurationExpression` field + registration

```csharp
public sealed class MapperConfigurationExpression
{
    private readonly Dictionary<TypePair, TypeMap> _typeMaps = new();
    private readonly List<OpenGenericTypeMap> _openGenericMaps = new();   // NEW

    // ... existing fields and CreateMap<TS, TD> unchanged ...

    public void CreateMap(Type sourceType, Type destinationType,
                          MemberList memberList = MemberList.None)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(sourceType);
        ArgumentNullException.ThrowIfNull(destinationType);

        if (!sourceType.IsGenericTypeDefinition)
            throw new AtlasConfigurationException(new List<ConfigurationError>
            {
                new(sourceType, destinationType, "(register)",
                    $"Source must be an open generic type definition; got '{sourceType.Name}'. " +
                    "Use CreateMap<TSource, TDestination>() for closed types.")
            });

        if (!destinationType.IsGenericTypeDefinition)
            throw new AtlasConfigurationException(new List<ConfigurationError>
            {
                new(sourceType, destinationType, "(register)",
                    $"Destination must be an open generic type definition; got '{destinationType.Name}'. " +
                    "Use CreateMap<TSource, TDestination>() for closed types.")
            });

        var sourceArity = sourceType.GetGenericArguments().Length;
        var destArity = destinationType.GetGenericArguments().Length;
        if (sourceArity != destArity)
            throw new AtlasConfigurationException(new List<ConfigurationError>
            {
                new(sourceType, destinationType, "(register)",
                    $"Generic arity mismatch: source has {sourceArity} type parameter(s), destination has {destArity}.")
            });

        var openMap = new OpenGenericTypeMap(
            sourceType,
            destinationType,
            memberList,
            $"CreateMap(typeof({sourceType.Name}), typeof({destinationType.Name}))");

        _openGenericMaps.Add(openMap);
    }

    internal IReadOnlyList<OpenGenericTypeMap> GetOpenGenericMaps() => _openGenericMaps;
}
```

### 4.3 `MapperProfile` mirror

```csharp
public abstract class MapperProfile
{
    private readonly List<TypeMap> _typeMaps = new();
    private readonly List<OpenGenericTypeMap> _openGenericMaps = new();   // NEW

    // ... existing CreateMap<TS, TD> unchanged ...

    protected void CreateMap(Type sourceType, Type destinationType,
                             MemberList memberList = MemberList.None)
    {
        ArgumentNullException.ThrowIfNull(sourceType);
        ArgumentNullException.ThrowIfNull(destinationType);

        // Same IsGenericTypeDefinition + arity validation as MapperConfigurationExpression.

        if (!sourceType.IsGenericTypeDefinition)
            throw new AtlasConfigurationException(new List<ConfigurationError>
            {
                new(sourceType, destinationType, "(register)",
                    $"Source must be an open generic type definition; got '{sourceType.Name}'. " +
                    "Use CreateMap<TSource, TDestination>() for closed types.")
            });

        if (!destinationType.IsGenericTypeDefinition)
            throw new AtlasConfigurationException(new List<ConfigurationError>
            {
                new(sourceType, destinationType, "(register)",
                    $"Destination must be an open generic type definition; got '{destinationType.Name}'. " +
                    "Use CreateMap<TSource, TDestination>() for closed types.")
            });

        var sourceArity = sourceType.GetGenericArguments().Length;
        var destArity = destinationType.GetGenericArguments().Length;
        if (sourceArity != destArity)
            throw new AtlasConfigurationException(new List<ConfigurationError>
            {
                new(sourceType, destinationType, "(register)",
                    $"Generic arity mismatch: source has {sourceArity} type parameter(s), destination has {destArity}.")
            });

        var openMap = new OpenGenericTypeMap(
            sourceType,
            destinationType,
            memberList,
            $"CreateMap(typeof({sourceType.Name}), typeof({destinationType.Name}))",
            originatingProfile: this);

        _openGenericMaps.Add(openMap);
    }

    internal IReadOnlyList<OpenGenericTypeMap> GetOpenGenericMaps() => _openGenericMaps;
}
```

The validation logic is duplicated between the two registration sites. It's small enough to inline rather than extracting a helper. (One could factor it later if a third registration site appears.)

### 4.4 `MapperConfigurationExpression.AddProfile` extension

```csharp
public void AddProfile(MapperProfile profile)
{
    EnsureMutable();
    foreach (var map in profile.GetTypeMaps())
        RegisterTypeMap(map);
    foreach (var openMap in profile.GetOpenGenericMaps())   // NEW
        _openGenericMaps.Add(openMap);
}
```

`AddMaps(params Assembly[])` follows the same path through `ProfileScanner.Discover` — no separate change needed.

### 4.5 What does NOT change

- `TypeMap` — unchanged. Materialized closed pairs are normal `TypeMap`s.
- `PropertyMap` — unchanged.
- `MapperConfigurationExpression.CreateMap<TSource, TDestination>` (generic overload) — unchanged.
- `MapperProfile.CreateMap<TSource, TDestination>` (generic overload) — unchanged.
- `RegisterTypeMap` (the closed-pair conflict guard) — unchanged.
- `MarkBuilt` / `EnsureMutable` — unchanged.

---

## 5. Lookup + Materialization in `MapperRegistry`

The single point of change for runtime behavior. `GetTypeMap` gets a lookup-and-materialize fallback; everything downstream consumes the result transparently.

### 5.1 Field type change: `Dictionary` → `ConcurrentDictionary`

```csharp
internal sealed class MapperRegistry
{
    // CHANGED from Dictionary<TypePair, TypeMap>: ConcurrentDictionary supports lock-free
    // reads on the hot path AND atomic GetOrAdd for lazy materialization.
    private readonly ConcurrentDictionary<TypePair, TypeMap> _typeMaps;

    // NEW fields:
    private readonly IReadOnlyList<OpenGenericTypeMap> _openGenericMaps;
    private readonly ValueTransformerCollection _globalTransformers;
    private readonly ConventionOptions _conventionOptions;

    // ... existing _delegates, _updateDelegates, _compileCounts, _lock,
    //     StringToEnumCache, ServiceProvider, ActionInstances ...
}
```

`ConcurrentDictionary` exposes the same surface that the rest of `MapperRegistry` already uses: `TryGetValue`, `ContainsKey`, indexer, and `Values` enumeration. All existing reads continue to behave identically. The only new write path is `GetOrAdd` in the lookup-and-materialize fallback.

### 5.2 Constructor signature

```csharp
public MapperRegistry(
    IEnumerable<TypeMap> typeMaps,
    StringToEnumCache? stringToEnumCache = null,
    IServiceProvider? serviceProvider = null,
    IReadOnlyList<OpenGenericTypeMap>? openGenericMaps = null,
    ValueTransformerCollection? globalTransformers = null,
    ConventionOptions? conventionOptions = null)
{
    _typeMaps = new ConcurrentDictionary<TypePair, TypeMap>(
        typeMaps.ToDictionary(t => t.Pair));
    StringToEnumCache = stringToEnumCache ?? new StringToEnumCache();
    ServiceProvider = serviceProvider;
    _openGenericMaps = openGenericMaps ?? Array.Empty<OpenGenericTypeMap>();
    _globalTransformers = globalTransformers ?? new ValueTransformerCollection();
    _conventionOptions = conventionOptions ?? new ConventionOptions(
        NamingConvention.PascalCase, NamingConvention.PascalCase, CaseSensitive: true);
}
```

The new parameters are nullable with sensible defaults so existing test helpers that construct `MapperRegistry` directly keep compiling. The `_conventionOptions` fallback uses the same defaults that `MapperConfigurationExpression` uses (PascalCase ↔ PascalCase, case-sensitive). `MapperConfiguration` always supplies all three parameters in practice.

### 5.3 `MapperConfiguration` constructor — pass-through

Both constructor paths must pass the new fields. The current DI-aware constructor replaces the registry post-`base()`-call to inject the `IServiceProvider`; this replacement also needs the new parameters.

**New fields on `MapperConfiguration`** so both constructors can access the registration data:
```csharp
public sealed class MapperConfiguration
{
    private readonly MapperRegistry _registry;
    private readonly ConventionOptions _conventionOptions;
    // ... existing fields ...
    private readonly IReadOnlyList<OpenGenericTypeMap> _openGenericMaps;     // NEW
    private readonly ValueTransformerCollection _globalTransformers;          // NEW
}
```

**Primary constructor** sets them:
```csharp
public MapperConfiguration(MapperConfigurationExpression expression)
{
    ArgumentNullException.ThrowIfNull(expression);

    _conventionOptions = new ConventionOptions(
        expression.SourceMemberNamingConvention,
        expression.DestinationMemberNamingConvention,
        expression.CaseSensitive);
    _enumValidationEnabled = expression.EnumValidationEnabled;

    var typeMaps = expression.GetTypeMaps().ToList();
    _openGenericMaps = expression.GetOpenGenericMaps().ToList();   // NEW (stored on field)
    _globalTransformers = expression.ValueTransformers;             // NEW (stored on field)
    var pairIndex = typeMaps.ToDictionary(t => t.Pair);
    bool HasRegisteredMap(Type s, Type d) => pairIndex.ContainsKey(new TypePair(s, d));

    InheritanceMerger.Resolve(typeMaps, pairIndex);
    foreach (var tm in typeMaps)
        ConventionEngine.ResolveMissingMembers(tm, _conventionOptions, HasRegisteredMap);
    ReverseMapMirror.Mirror(typeMaps);
    TransformerResolver.Resolve(typeMaps, expression.ValueTransformers);
    foreach (var tm in typeMaps)
        tm.Seal();

    expression.MarkBuilt();
    _registry = new MapperRegistry(
        typeMaps,
        _stringToEnumCache,
        openGenericMaps: _openGenericMaps,        // NEW
        globalTransformers: _globalTransformers,   // NEW
        conventionOptions: _conventionOptions);    // NEW
}
```

**DI-aware constructor** also passes them when replacing the registry:
```csharp
public MapperConfiguration(MapperConfigurationExpression expression, IServiceProvider serviceProvider)
    : this(expression)
{
    ArgumentNullException.ThrowIfNull(serviceProvider);
    _serviceProvider = serviceProvider;
    _registry = new MapperRegistry(
        _registry.AllTypeMaps.ToList(),
        _stringToEnumCache,
        serviceProvider,
        openGenericMaps: _openGenericMaps,         // NEW (from field)
        globalTransformers: _globalTransformers,   // NEW (from field)
        conventionOptions: _conventionOptions);    // NEW (from field)
}
```

This preserves the existing "replace the registry to inject IServiceProvider" pattern while ensuring open-generic state survives the replacement.

### 5.4 `GetTypeMap` lookup-and-materialize

```csharp
public TypeMap? GetTypeMap(TypePair pair)
{
    // Hot path: exact closed-pair match. ConcurrentDictionary read is lock-free.
    if (_typeMaps.TryGetValue(pair, out var m)) return m;

    // Fast bail when no open-generic registrations exist — hot-path zero-cost
    // for users who don't use the feature.
    if (_openGenericMaps.Count == 0) return null;

    // Closed-pair miss. Search open-generic registrations.
    var template = FindMatchingOpenGenericTemplate(pair);
    if (template is null) return null;

    // Materialize the closed pair via GetOrAdd. Under contention, the factory may
    // run more than once but only one TypeMap is stored — materialization is
    // idempotent (deterministic given the same pair + template + convention options).
    return _typeMaps.GetOrAdd(pair, p => MaterializeClosed(template, p));
}

private OpenGenericTypeMap? FindMatchingOpenGenericTemplate(TypePair pair)
{
    // Linear scan — open-generic registrations are typically a handful per app,
    // not enough to warrant a hashed lookup.
    foreach (var template in _openGenericMaps)
    {
        if (template.Matches(pair)) return template;
    }
    return null;
}
```

### 5.5 `MaterializeClosed` algorithm

```csharp
private TypeMap MaterializeClosed(OpenGenericTypeMap template, TypePair closedPair)
{
    // Step 1: construct the closed TypeMap with origin metadata.
    var tm = new TypeMap(closedPair.Source, closedPair.Destination, template.MemberList)
    {
        OriginatingProfile = template.OriginatingProfile,
        RegistrationOrigin = $"{template.RegistrationOrigin} " +
                             $"(closed at runtime as ({closedPair.Source.Name}, {closedPair.Destination.Name}))"
    };

    // Step 2: convention engine populates PropertyMaps for the closed types.
    // HasRegisteredMap probe is the same one used at config-build time, so nested
    // map resolution works (a closed map's nested types may reference other closed
    // maps that were materialized earlier or registered explicitly).
    bool HasRegisteredMap(Type s, Type d) => _typeMaps.ContainsKey(new TypePair(s, d));
    ConventionEngine.ResolveMissingMembers(tm, _conventionOptions, HasRegisteredMap);

    // Step 3: profile/global value transformers via the existing resolver.
    // Pass a singleton list so TransformerResolver.Resolve operates on just this map.
    TransformerResolver.Resolve(new[] { tm }, _globalTransformers);

    // Step 4: seal so the TypeMap is immutable from this point on.
    tm.Seal();

    return tm;
}
```

**What does NOT run in materialization:**
- `InheritanceMerger.Resolve` — open generics don't support `Include` (deferred to v3).
- `ReverseMapMirror.Mirror` — open generics don't support `.ReverseMap()` (deferred).
- `ConfigurationValidator` — open-generic templates are excluded from validation per the reference doc. Materialized closed pairs that exist by validation time WILL be validated as a side effect of being in `_typeMaps`, but that's a documented side-effect rather than a designed feature.

### 5.6 Concrete trace — primitive closed pair

User registers:
```csharp
c.CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>));
```

`MapperConfigurationExpression._openGenericMaps` now contains one `OpenGenericTypeMap` with `SourceTypeDefinition = typeof(Wrapper<>)`, `DestinationTypeDefinition = typeof(WrapperDto<>)`. `MapperConfiguration` constructor passes it to `MapperRegistry`.

User calls:
```csharp
mapper.Map<WrapperDto<int>>(new Wrapper<int> { Value = 42 });
```

1. `Mapper.Map<WrapperDto<int>>` → `_registry.GetTypeMap(new TypePair(typeof(Wrapper<int>), typeof(WrapperDto<int>)))`.
2. `_typeMaps.TryGetValue(pair, out _)` → miss (only the open-generic registration exists).
3. `_openGenericMaps.Count == 1` → not bailing.
4. `FindMatchingOpenGenericTemplate(pair)`:
   - Template's `SourceTypeDefinition == typeof(Wrapper<>)` matches `pair.Source.GetGenericTypeDefinition() == typeof(Wrapper<>)`.
   - Same for destination.
   - Returns the template.
5. `_typeMaps.GetOrAdd(pair, p => MaterializeClosed(template, p))`:
   - Construct `TypeMap(typeof(Wrapper<int>), typeof(WrapperDto<int>), MemberList.None)`.
   - `ConventionEngine.ResolveMissingMembers` walks `WrapperDto<int>`'s public writable properties (`Value: int`, `CreatedAt: DateTime`, `History: List<int>`); finds matching source paths on `Wrapper<int>`; populates `PropertyMaps`.
   - `TransformerResolver.Resolve([tm], _globalTransformers)` populates `EffectiveTransformers` (empty if no transformers registered).
   - `tm.Seal()`.
6. Returned `TypeMap` is identical in structure to one a user would have written via `c.CreateMap<Wrapper<int>, WrapperDto<int>>(MemberList.None)`.
7. Subsequent `mapper.Map<WrapperDto<int>>(...)` calls hit `_typeMaps.TryGetValue` directly (lock-free hot path).

### 5.7 Concrete trace — second closed pair (independent materialization)

```csharp
mapper.Map<WrapperDto<string>>(new Wrapper<string> { Value = "hi" });
```

Same flow as §5.6 with `(Wrapper<string>, WrapperDto<string>)`. Independent `TypeMap` materialized and cached separately.

### 5.8 Concrete trace — nested-closed-pair already registered

User registers:
```csharp
c.CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>));
c.CreateMap<Customer, CustomerDto>();
```

Now calls `mapper.Map<WrapperDto<Customer>>(new Wrapper<Customer> { Value = new Customer { Name = "Alice" } })`:

1. Lookup for `(Wrapper<Customer>, WrapperDto<Customer>)` → miss → materialize.
2. Convention engine for `WrapperDto<Customer>.Value: Customer`:
   - Source path: `Wrapper<Customer>.Value: Customer` (direct).
   - `ConvertOrMap(srcExpr [Customer type], typeof(Customer), registry)`: not direct same-type because... actually `Customer == Customer`, so `if (source.Type == targetType) return source;` returns the source directly.
   - Wait — both are `Customer`. So `WrapperDto<Customer>.Value` reference-copies from `Wrapper<Customer>.Value`. The `(Customer, CustomerDto)` registration doesn't apply because the types are `Customer → Customer`, not `Customer → CustomerDto`.
   - To get `CustomerDto`-typed `Value` in the destination, the user would need `WrapperDto2<T>` with `Value: T` where they map `Wrapper<Customer> → WrapperDto2<CustomerDto>`. That's a different scenario (heterogeneous T-positions), out of scope for this MVP.
3. Result: `WrapperDto<Customer>` with `Value` reference-copied (same closed type on both sides).

For the heterogeneous case to work, the user would close the map manually:
```csharp
c.CreateMap<Wrapper<Customer>, WrapperDto<CustomerDto>>();
```

The open-generic registration doesn't apply (source type is `Wrapper<>` but destination type is `WrapperDto<>` — both at arity 1 but the closing types are `Customer` vs `CustomerDto` independently substituted). Actually wait — let me re-examine.

For `(Wrapper<Customer>, WrapperDto<CustomerDto>)`:
- Source GTD: `Wrapper<>`. Template source: `Wrapper<>`. Match.
- Destination GTD: `WrapperDto<>`. Template destination: `WrapperDto<>`. Match.
- The `Matches` check passes (only checks `GetGenericTypeDefinition()`s). The closing TYPES don't have to match — they substitute INDEPENDENTLY into source and destination positions.

So the open template DOES match `(Wrapper<Customer>, WrapperDto<CustomerDto>)`. Materialization runs. Convention engine on `WrapperDto<CustomerDto>` properties:
- `Value: CustomerDto` ← `Wrapper<Customer>.Value: Customer`. ConvertOrMap detects different types, looks up `(Customer, CustomerDto)` map, finds it, emits a nested invoke. Works.

So heterogeneous T-positions DO work with the open-generic template, as long as the user-facing call site uses compatible closing types.

### 5.9 `Atlas.Projections` interaction (Bug-4 audit)

`ProjectionPlanBuilder.Build(registry, root, maxDepth)` calls `registry.GetTypeMap(root)` directly. With the lookup-and-materialize change in `GetTypeMap`, `query.ProjectTo<WrapperDto<int>>(cfg)` automatically triggers materialization on first call, then reuses the cached closed `TypeMap` for subsequent projections.

`ProjectionCompatibility.IsTypeMapProjectable(tm, out _)` runs against the materialized closed `TypeMap`, which is structurally identical to a manually-registered one — no Hooks (open generics don't support `BeforeMap`/`AfterMap`), no `ConvertUsing`, so the projection compatibility check passes naturally.

The only way a materialized closed pair becomes non-projectable is if the user registered the SAME closed pair MANUALLY with `BeforeMap`/`AfterMap`/`ConvertUsing`/`ForPath` — but in that case, the closed-pair-takes-precedence rule routes the lookup there, not to the materialized map.

---

## 6. Validation, Edge Cases, and Worked Example

### 6.1 Validation policy

**Open-generic templates (`OpenGenericTypeMap`):** excluded entirely from `AssertConfigurationIsValid()`. Per the reference doc, "open generic maps are typically excluded from configuration validation, since not every closed combination will be valid." The validator iterates `_typeMaps.Values` (closed pairs only) and never reaches `_openGenericMaps`.

**Materialized closed pairs:** because materialization adds the closed `TypeMap` to `_typeMaps` (via `GetOrAdd`), any materialized pair that exists by the time `AssertConfigurationIsValid()` is called WILL be validated. This is a side effect of the architecture — not a documented feature. The user should not rely on this for validation coverage; the recommended pattern is to register specific closed pairs explicitly when validation guarantees are needed.

**Registration-time errors** (per §3.4) are the only validation that runs at config time:
- Both arguments must be `IsGenericTypeDefinition`.
- Generic arity must match.

### 6.2 Edge cases

| Scenario | Behavior |
|---|---|
| Open-generic match exists but the closed source-property type doesn't match the closed destination-property type | Convention engine produces an unresolved `PropertyMap`; if `MemberList.Destination`, validator (if called after first use) reports the unmapped member; if `MemberList.None` (default), the property silently uses `default(TMember)` at runtime (existing Atlas convention behavior). |
| Open-generic registration matches but the closed pair has nested types that aren't registered | Convention engine's `HasRegisteredMap` probe returns false; the convention treats the property as unresolved. User must register the nested closed pair separately, or register an open-generic for it. |
| Same closed pair requested concurrently by N threads | `ConcurrentDictionary.GetOrAdd` may invoke the materialization factory more than once under contention, but only one `TypeMap` is stored. Wasted CPU only — never inconsistent results. |
| User registers `CreateMap(typeof(Source<>), typeof(Destination<>))` AND `CreateMap<Source<int>, Destination<int>>()` | Closed-pair registration sits in `_typeMaps` from config-build time; closed-pair lookup hits it before `_openGenericMaps` is scanned. Open-generic doesn't apply for that closed pair. |
| Multiple open-generic registrations match | First match in `FindMatchingOpenGenericTemplate`'s linear scan wins; ordering reflects registration order. Practically rare; documented. |
| Closed pair has different type-args on source vs. destination (e.g., `Source<int>` to `Destination<long>`) | `Matches` checks BOTH `GetGenericTypeDefinition()`s independently. Open template DOES match. Convention engine then resolves member types based on actual closed types — works if the actual closed property types are compatible (or if a `(closedSrcMember, closedDstMember)` map is registered). |
| Generic type definition mismatch (source is `Source<>` but destination is `Other<>`) | `Matches` requires the registered template to be `(Source<>, Other<>)`. If no such registration exists, lookup returns null. |
| `IList<>`, `IEnumerable<>`, etc. as source type | Open-generic interface types work. `CreateMap(typeof(IEnumerable<>), typeof(List<>))` matches when source is `IEnumerable<int>` and destination is `List<int>`. Existing collection logic handles it. |
| User calls `mapper.Map<DestinationClosed>(srcClosed)` where source is null | Existing Atlas null-source handling applies — returns `default(DestinationClosed)`. Materialization happens regardless (the lookup is type-driven, not value-driven). |
| `CompileMappings()` called at startup with open generics registered | Eagerly compiles all closed pairs in `_typeMaps`. Open-generic templates aren't enumerated (the closed-pair space is unbounded). Materialized closed pairs compile lazily on first `Map<>()` call (existing Atlas behavior for any TypeMap not yet in `_delegates`). |

### 6.3 Worked end-to-end example

**Setup:**
```csharp
public class Wrapper<T>
{
    public T Value { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<T> History { get; set; } = new();
}

public class WrapperDto<T>
{
    public T Value { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<T> History { get; set; } = new();
}

public class Customer
{
    public string Name { get; set; } = "";
    public int Score { get; set; }
}

public class CustomerDto
{
    public string Name { get; set; } = "";
    public int Score { get; set; }
}

var cfg = new MapperConfiguration(c =>
{
    // Open generic: handles every Wrapper<T> → WrapperDto<T> closure.
    c.CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>));

    // Closed pair for Customer-related nested mapping.
    c.CreateMap<Customer, CustomerDto>();

    // Global value transformer applies to every materialized closed pair.
    c.ValueTransformers.Add<string>(s => s.Trim());
});
var mapper = cfg.CreateMapper();
```

**First call — primitive type:**
```csharp
var intDto = mapper.Map<WrapperDto<int>>(new Wrapper<int>
{
    Value = 42,
    CreatedAt = new DateTime(2024, 1, 1),
    History = new List<int> { 1, 2, 3 }
});
```

Trace:
1. `_registry.GetTypeMap(new TypePair(typeof(Wrapper<int>), typeof(WrapperDto<int>)))`.
2. `_typeMaps.TryGetValue` → miss.
3. `_openGenericMaps` has 1 entry. `FindMatchingOpenGenericTemplate` iterates; the registered `(typeof(Wrapper<>), typeof(WrapperDto<>))` matches.
4. `MaterializeClosed`:
   - New `TypeMap(Wrapper<int>, WrapperDto<int>, MemberList.None)`.
   - `ConventionEngine.ResolveMissingMembers` walks `WrapperDto<int>` properties: `Value: int` → `Wrapper<int>.Value: int`, `CreatedAt: DateTime` → direct, `History: List<int>` → direct.
   - `TransformerResolver.Resolve([tm], globalTransformers)`: `EffectiveTransformers[typeof(string)] = [globalTrim]` (no `string` properties on this map; transformer registered but won't fire).
   - `tm.Seal()`.
5. `ExecutionPlanBuilder.Build(tm, registry)` compiles a delegate. `Map<>` invokes it.
6. Result: `WrapperDto<int> { Value = 42, CreatedAt = ..., History = [1,2,3] }`.

**Second call — heterogeneous closed pair triggers `(Customer, CustomerDto)` nested map:**
```csharp
var hetDto = mapper.Map<WrapperDto<CustomerDto>>(new Wrapper<Customer>
{
    Value = new Customer { Name = "  Alice  ", Score = 100 },
    CreatedAt = new DateTime(2024, 6, 1),
    History = new List<Customer> { new() { Name = "  Bob  ", Score = 50 } }
});
```

Trace:
1. Lookup `(Wrapper<Customer>, WrapperDto<CustomerDto>)` → miss → materialize.
2. Convention engine for `WrapperDto<CustomerDto>` properties:
   - `Value: CustomerDto` ← `Wrapper<Customer>.Value: Customer`. `ConvertOrMap` detects type mismatch (`Customer ≠ CustomerDto`), looks up `(Customer, CustomerDto)` in registry — **registered manually**, found. Emits a nested invoke. Within that nested map, the global string transformer applies to `Name`.
   - `History: List<CustomerDto>` ← `Wrapper<Customer>.History: List<Customer>`. `ConvertOrMap` detects collection types differ in element type, recurses into element mapping `Customer → CustomerDto`. Same nested map.
3. Result: `WrapperDto<CustomerDto>` with `Value` deep-copied to `CustomerDto { Name = "Alice", Score = 100 }` (string trimmed by transformer) and `History` element-wise-mapped to `[CustomerDto { Name = "Bob", Score = 50 }]`.

**Third call — ProjectTo:**
```csharp
var dtos = dbContext.Wrappers
    .Where(w => w.CreatedAt > DateTime.UtcNow.AddYears(-1))
    .ProjectTo<WrapperDto<int>>(cfg)
    .ToList();
```

Trace:
1. `ProjectionPlanBuilder.Build(registry, new TypePair(typeof(Wrapper<int>), typeof(WrapperDto<int>)), maxDepth)` → calls `registry.GetTypeMap(...)`.
2. The same closed pair was materialized in the first example. Hot-path hit. Returns the cached `TypeMap`.
3. Projection codegen builds `Expression.MemberInit` from the closed `TypeMap`'s `PropertyMaps` — same algorithm as any manually-registered closed pair. EF Core executes against the database.

Result: open-generic registration works transparently across both `Map<>()` and `ProjectTo<>()`.

---

## 7. Build-Time Pipeline

**Unchanged.** No new step is needed.

Current order (post-#8) inside `MapperConfiguration` constructor:
```
1. Profile.Configure() — TypeMaps + open-generic templates registered.
2. ConfigExpression conflict-guard (#4).
3. AddProfile harvest (#4) + open-generic propagation (this feature).
4. InheritanceMerger.Resolve(typeMaps) — closed maps only; open templates skipped.
5. ConventionEngine.ResolveMissingMembers(tm) — closed maps only; open templates skipped.
6. ReverseMapMirror.Mirror(typeMaps) — closed maps only.
7. TransformerResolver.Resolve(typeMaps, expression.ValueTransformers) — closed maps only.
8. tm.Seal() for each closed TypeMap.
9. (On AssertConfigurationIsValid) ConfigurationValidator.Validate — closed maps only.
10. CompileMappings — eagerly compiles closed maps; materialized maps compile lazily on first use.
```

Open-generic registrations are stored alongside closed `TypeMap`s but processed nowhere during config-build. Their first relevant moment is at runtime, when `MapperRegistry.GetTypeMap` falls through to the lookup-and-materialize path.

---

## 8. Test Plan

Total: **~25 new tests**. Test baseline goes from **465 → ~490** after this feature.

### 8.1 `OpenGenericTypeMapTests` (Atlas.Tests/Internal)

Add 3 tests:
1. `Matches_ArityMatchingClosedPair_ReturnsTrue` — closed pair whose generic-type-definitions match the template returns true.
2. `Matches_DifferentGenericTypeDefinitions_ReturnsFalse` — closed pair whose source GTD doesn't match returns false; same for destination.
3. `Matches_NonConstructedGenericType_ReturnsFalse` — non-generic type pair returns false.

### 8.2 `MapperConfigurationExpressionOpenGenericTests` (Atlas.Tests)

Add 5 tests:
1. `CreateMap_StoresOpenGenericRegistration` — successful registration appears in `GetOpenGenericMaps()`.
2. `CreateMap_NotAGenericTypeDefinition_ThrowsAtlasConfigurationException` — passing `typeof(Wrapper<int>)` as source.
3. `CreateMap_ArityMismatch_ThrowsAtlasConfigurationException` — passing `typeof(Source<>)` and `typeof(Destination<,>)`.
4. `CreateMap_NullArgs_ThrowsArgumentNullException` — null source or destination.
5. `AddProfile_PropagatesOpenGenericRegistrations` — open-generic registration on a profile lands in the root config's `_openGenericMaps`.

### 8.3 `MapperProfileOpenGenericTests` (Atlas.Tests)

Add 2 tests:
1. `CreateMap_OnProfile_StoresWithOriginatingProfile` — registration stored with `OriginatingProfile = profile`.
2. `ProfileValueTransformer_AppliesToMaterializedClosedPair_FromThisProfile` — profile-level transformer fires on a closed pair materialized from this profile's open template.

### 8.4 `MapperRegistryOpenGenericTests` (Atlas.Tests/Internal)

Add 5 tests:
1. `GetTypeMap_PrimitiveTypeArg_MaterializesAndCaches` — first call materializes, second call hits cache.
2. `GetTypeMap_ReferenceTypeArg_MaterializesAndCaches` — same with reference-type arg.
3. `GetTypeMap_NestedClosedPairAlreadyRegistered_UsesExistingMap` — convention engine's HasRegisteredMap probe finds the nested registration.
4. `GetTypeMap_ClosedPairTakesPrecedenceOverOpenGeneric` — manually-registered closed pair wins over open-generic match.
5. `GetTypeMap_NoMatchingOpenGeneric_ReturnsNull` — closed pair with no matching template returns null.

### 8.5 `MapperOpenGenericTests` (Atlas.Tests)

Add 4 end-to-end tests via real `IMapper`:
1. `Map_PrimitiveTypeArg_HeadlineExample` — `mapper.Map<WrapperDto<int>>` with primitive type arg.
2. `Map_ReferenceTypeArg_NestedMapResolved` — `mapper.Map<WrapperDto<CustomerDto>>` with `(Customer, CustomerDto)` registered for nested mapping.
3. `Map_HigherArity_TupleStyle` — open generic with arity 2, e.g., `CreateMap(typeof(Source<,>), typeof(Destination<,>))`.
4. `Update_OpenGeneric_SubstituteAppliesUniformly` — update-in-place via `Map<TS, TD>(src, existing)` works for materialized closed pairs.

### 8.6 `ProjectionPlanBuilderOpenGenericTests` (Atlas.Projections.Tests/Internal)

Add 2 tests:
1. `Projection_OpenGenericTemplate_ProducesCorrectMemberInit` — `ProjectionPlanBuilder.Build` for an open-generic-only-registered closed pair returns a valid lambda.
2. `Projection_ClosedPairTakesPrecedence` — manually-registered closed pair with `ForMember` is used by projection, not the open-generic template.

### 8.7 `ProjectTo_OpenGenericTests` (Atlas.Projections.Tests.EFCore)

Add 2 EF Core SQLite E2E tests:
1. `ProjectTo_OpenGeneric_GeneratesValidSql` — `query.ProjectTo<WrapperDto<int>>(cfg)` generates a SELECT statement against the in-memory database.
2. `ProjectTo_OpenGeneric_RowsRoundtrip` — seeded rows materialize correctly via the open-generic-materialized projection.

### 8.8 `MapperConfigurationOpenGenericValidationTests` (Atlas.Tests)

Add 2 tests:
1. `AssertConfigurationIsValid_OpenGenericOnly_DoesNotThrow` — only open-generic templates registered; validator runs successfully.
2. `AssertConfigurationIsValid_OpenGenericPlusClosedPairs_ValidatesClosedPairsOnly` — open templates excluded from validation; closed registrations validated normally.

### 8.9 What we do NOT add tests for

- **Performance benchmarks** for materialization latency. Tied to Atlas's existing benchmark harness — out of scope for this design (could be a follow-up).
- **Open-generic interactions with Hooks/Conditions/NullSubstitute** — by design, these don't apply to open generics (no fluent surface). No interaction to test.
- **Open-generic with `ConvertUsing`** — explicitly out of scope.

### 8.10 Coverage targets

Same as prior features: line ≥ 90%, branch ≥ 80% on the changed assemblies. The Atlas core change-set is moderate (one new file + helper methods + concurrent dictionary refactor + 2 fluent overloads + validation rules). Coverage should land comfortably in the high 90s on Atlas core.

---

## 9. README Updates

Three changes to `README.md`:

1. **New "Open generics" section** between "Null substitution" and "What's in v1":

   ```markdown
   ## Open generics

   A single `CreateMap(typeof(Source<>), typeof(Destination<>))` registration applies to
   every closed instantiation at runtime. Atlas materializes a closed `TypeMap` per closed
   pair on first use via `MapperRegistry.GetTypeMap`'s lazy fallback, then caches it.

   ```csharp
   public class Wrapper<T> { public T Value { get; set; } }
   public class WrapperDto<T> { public T Value { get; set; } }

   var cfg = new MapperConfiguration(c =>
   {
       c.CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>));
   });
   var mapper = cfg.CreateMapper();

   var intDto = mapper.Map<WrapperDto<int>>(new Wrapper<int> { Value = 42 });
   var stringDto = mapper.Map<WrapperDto<string>>(new Wrapper<string> { Value = "hi" });
   ```

   **Closed-pair-takes-precedence rule:** when a user has registered both the open template
   AND a specific closed pair, the closed pair wins. This is the documented escape hatch for
   per-member overrides on a specific instantiation.

   **Convention-only:** open-generic registrations carry no fluent surface — no `ForMember`,
   no `Include`, no `BeforeMap`, no `NullSubstitute`, no `ReverseMap`. Users who need any of
   these register the specific closed pair via the generic `CreateMap<TSrc, TDst>()` overload.

   **Validation:** open-generic templates are excluded from `AssertConfigurationIsValid()`
   per the "not every closed combination is valid" rule. Materialized closed pairs that exist
   by validation time will be validated as a side effect of being in the closed-pair registry.

   **Translates to ProjectTo:** `query.ProjectTo<WrapperDto<int>>(cfg)` triggers
   materialization on first call and reuses the cached closed `TypeMap` for subsequent
   projections.
   ```

2. **Coverage / test-count refresh** — bump 465 to 490 if README quotes a number.

3. **Remove "Open generics" from the "Deferred to v2" list.**

---

## 10. Risks & Implementer Notes

### 10.1 Cross-package consumer audit (Bug-4 lesson applied)

The new lookup-and-materialize logic lives in `MapperRegistry.GetTypeMap`, which is consumed by both `Atlas` core (via `Mapper.Map<>`) and `Atlas.Projections` (via `ProjectionPlanBuilder.Build`). Because the change is at a single point of insertion, both packages get open-generic support automatically. **No separate wire-in for `Atlas.Projections` is needed** — the spec reviewer should verify this and not flag the absence of projection-side production-code changes as missing. The 2-test projection coverage in §8.6 + 2-test EF Core coverage in §8.7 confirms the cross-package behavior.

### 10.2 NOT scope-identifying TypeMap metadata (Bug-5 lesson applied)

Open-generic registrations are stored in a SEPARATE `_openGenericMaps` list, not on `TypeMap`. The materialized closed `TypeMap`s carry `OriginatingProfile` (inherited from the template) and `RegistrationOrigin` (annotated to indicate runtime materialization). No new `TypeMap` field added; no need to audit `ReverseMapMirror` or other TypeMap-creator sites.

### 10.3 Convention-only at materialization is a deliberate constraint

The materialization pipeline runs `ConventionEngine.ResolveMissingMembers` and `TransformerResolver.Resolve` but NOT `InheritanceMerger.Resolve` or `ReverseMapMirror.Mirror`. This is correct because open generics don't support `Include`/`IncludeBase`/`.ReverseMap()` (deferred to v3). Per-task review should confirm these are NOT called during `MaterializeClosed`.

### 10.4 Bug-6 lesson — `ConvertOrMap` already handles asymmetric Nullable<T>

Materialized closed pairs use the same `ConvertOrMap` / `ConvertOrInline` codegen as manually-registered ones, so any `Nullable<T>` source + non-nullable destination (or vice-versa) goes through the asymmetric-nullable widening branches added in #8. No new test scenarios needed beyond the §8 plan.

### 10.5 Thread-safety via `ConcurrentDictionary` change

The `_typeMaps` field changes from `Dictionary` to `ConcurrentDictionary`. Per-task review should verify:
- All existing reads still compile and behave identically (`ConcurrentDictionary` exposes `TryGetValue`, `ContainsKey`, indexer, enumeration — all the same surface).
- No write paths added beyond `GetOrAdd` in the new lookup-and-materialize.
- `AllTypeMaps` (used by `MapperConfiguration.CompileMappings`, `ConfigurationValidator.Validate`) still returns a snapshot — `ConcurrentDictionary.Values` is a snapshot enumerator, semantically equivalent.

### 10.6 Validator non-coverage of open-generic templates is documented behavior

The validator iterates `_typeMaps.Values`. Open-generic templates aren't in there. Materialized closed pairs that exist by validation time WILL be validated, but this is a side effect, not a designed feature. If the user wants to validate a specific closed pair, they should register it explicitly.

### 10.7 `MapperRegistry` constructor signature change — backward compatibility for tests

The new `openGenericMaps`, `globalTransformers`, `conventionOptions` parameters are nullable with defaults. Existing test helpers that construct `MapperRegistry` directly keep working without changes. New helpers added for open-generic tests pass non-null values.

### 10.8 Holistic review is non-negotiable

NullSubstitution (#8) was the empirical proof that even features with low per-task issue counts can have cross-task bugs (the `Coalesce(Nullable<T>, T)` lifted-nullable bug surfaced via test-deviation scrutiny). Open generics introduces a runtime mutation pattern (lazy materialization) that didn't exist before — high-value target for cross-task review. Don't skip the holistic review.

### 10.9 Test-deviation scrutiny

Per the lesson from #8: when an implementer subagent makes a test change, even if disclosed as a "small" deviation, the spec reviewer should TRACE THROUGH why the deviation was needed — not just accept "it worked". A passing test on a different code path may hide a bug on the intended path. Particularly relevant for this feature because materialization touches central shared infrastructure (`MapperRegistry`).

---

## 11. Final Feature Summary

**One sentence:** A single `cfg.CreateMap(typeof(Source<>), typeof(Destination<>))` registration applies to every closed instantiation at runtime via lazy materialization through `MapperRegistry.GetTypeMap`'s open-generic fallback.

**API surface added:**
- `void MapperConfigurationExpression.CreateMap(Type, Type, MemberList = MemberList.None)`.
- `void MapperProfile.CreateMap(Type, Type, MemberList = MemberList.None)` (mirror).

**Internal additions:**
- New record `OpenGenericTypeMap` — registration template (no `PropertyMap`s).
- New field `_openGenericMaps` on `MapperConfigurationExpression` and `MapperProfile`.
- `MapperRegistry`: `_typeMaps` changes from `Dictionary` to `ConcurrentDictionary`; new constructor parameters (`openGenericMaps`, `globalTransformers`, `conventionOptions`); new `GetTypeMap` lookup-and-materialize fallback; new private `MaterializeClosed` and `FindMatchingOpenGenericTemplate` methods.
- `MapperConfiguration` constructor passes the new fields to `MapperRegistry`.

**Things explicitly NOT in this feature:**
- Per-member overrides on open generics (no fluent surface).
- `Include` / `IncludeBase` / `.ReverseMap()` on open generics.
- `BeforeMap` / `AfterMap` / `AddTransform` / `NullSubstitute` / `Condition` / `PreCondition` on open generics.
- `ConvertUsing(typeof(Converter<>))` open-generic converter type.
- Open-generic resolvers / value converters.
- Validation of open-generic templates beyond arity / type-definition checks at registration.

**Test count:** 25 new tests (baseline 465 → 490).

**Plan task count estimate:** 8 tasks (Branch setup → OpenGenericTypeMap + tests → MapperConfigurationExpression registration + tests → MapperProfile registration + tests → MapperRegistry constructor refactor → MapperRegistry lookup-and-materialize + tests → E2E Mapper integration → README + final coverage). Smaller than #8 (9 tasks) because there's only one cross-package consumer (`Atlas.Projections` gets it for free via the single `GetTypeMap` insertion point).

---

*End of design.*
