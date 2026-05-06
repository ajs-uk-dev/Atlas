# Atlas v2 — Dynamic / `ExpandoObject` / `Dictionary<string, object>` Mapping

**Status:** Approved design (2026-05-06).
**Implementation target:** v2 feature group #10 (post-MVP, post-OpenGenerics).
**Predecessor designs:** `docs/Atlas-Design-OpenGenerics.md` (lazy-materialization architecture, single-insertion-point pattern, closed-pair-takes-precedence rule), `docs/Atlas-Design.md` (v1 baseline — `MapperConfiguration`, `MapperRegistry`, `ExecutionPlanBuilder`, `ConvertOrMap` pipeline).

This document specifies Atlas's tenth post-MVP feature: **convention-only mapping between strongly-typed POCOs and the three recognized dynamic shapes** — `IDictionary<string, object>`, `ExpandoObject`, and `Dictionary<string, object>`. The feature requires **zero new fluent surface**; lazy materialization fires inside `MapperRegistry.GetTypeMap` whenever a closed-pair lookup misses and one side of the pair is a recognized dynamic shape.

---

## 1. Goals & Non-Goals

### 1.1 Goals

1. **Convention-only dict ↔ POCO mapping.** Users call `mapper.Map<MyPoco>(someDict)` or `mapper.Map<ExpandoObject>(somePoco)` with **no `CreateMap` registration**. The dynamic-shape detector materializes a `TypeMap` on demand inside `MapperRegistry.GetTypeMap`. Mirrors Open Generics' lazy-materialization architecture (#9): single insertion point, `ConcurrentDictionary.GetOrAdd`-cached, thread-safe by construction.

2. **Three recognized dynamic shapes (both directions).** `IDictionary<string, object>`, `ExpandoObject`, `Dictionary<string, object>`. Detection is **exact runtime-type match** against this allowlist — no assignability fallback, no subtype recognition.

3. **Bidirectional, asymmetric codegen.** Dict→POCO enumerates destination POCO members and looks up dict values via `TryGetValue`-keyed reads, flowing each result through Atlas's existing `ConvertOrMap` pipeline. POCO→dict enumerates source POCO public readable properties, emits nested `ExpandoObject` for nested POCO members, `List<object?>` for collection members, recurses element-wise for typed-POCO `Dictionary<string, T>` source properties.

4. **Dot-notation read-side fallback.** When the destination is a nested POCO and the dict has no top-level matching key, the codegen scans for `"Prefix."`-keyed siblings and synthesizes a nested `IDictionary<string, object>` from them. Top-level keys always take precedence over dot-notation siblings (mixed dicts ignore the siblings).

5. **Atlas.Projections rejects dynamic TypeMaps.** LINQ providers cannot translate `dict.TryGetValue` against arbitrary keys — projection-build-time gate at the top of `ProjectionPlanBuilder.Build` (or sibling `ProjectionCompatibility.IsTranslatable`) raises `AtlasProjectionException` with the dynamic-shape pair in the message. Same single-insertion-point pattern Hooks (#5) and ConditionalMapping (#7)'s rejection paths use.

6. **Closed-pair-takes-precedence.** If the user explicitly registers `cfg.CreateMap<MyPoco, IDictionary<string, object>>()` (with whatever per-member configuration), the registered `TypeMap` wins because the cache lookup hits BEFORE the detector. Same precedence rule OpenGenerics' "closed pair beats open template" ships.

### 1.2 Non-Goals (deferred to v3)

- **`IDynamicMetaObjectProvider` / DLR beyond `ExpandoObject`.** Custom dynamic objects (anything overriding `GetMetaObject` to provide member dispatch) are out of scope. `ExpandoObject` is supported because it implements `IDictionary<string, object>` directly.
- **`Dictionary<string, T>` for `T != object`.** `Dictionary<string, string>`, `Dictionary<string, JsonElement>`, etc., are NOT recognized as dynamic shapes. Users wanting `Dictionary<string, string>` as input/output type either (a) register an explicit `CreateMap<,>` and treat as element collection, or (b) wait for v3.
- **Per-member fluent customization for dynamic TypeMaps.** No `Ignore`, `MapFrom`, `Condition`, `PreCondition`, `NullSubstitute`, `BeforeMap`/`AfterMap`, `AddTransform`, etc. — there's no fluent surface for the materialized TypeMap. Users who need customization fall back to writing an explicit `CreateMap<MyPoco, IDictionary<string, object>>` and configuring normally — though see §7.5 for v1 limitations of the explicit path.
- **Custom dictionary types beyond the three named shapes.** `ImmutableDictionary<string, object>`, `ConcurrentDictionary<string, object>`, `OrderedDictionary`, etc., are NOT recognized.
- **Naming-policy-aware key translation on emit.** Property name `CustomerName` always emits as dict key `"CustomerName"` verbatim; never `"customer_name"` or `"customerName"` regardless of `MapperConfigurationExpression.NamingConvention`. v3 may add a `WithDynamicKeyConvention(...)` toggle.
- **Per-call parameter dictionaries** (the functional reference's "context bag" pattern from §6.5/§16.7). Out of scope for #10 — its own future v3 feature.
- **Reverse-map machinery (`.ReverseMap()`).** Dynamic TypeMaps are never registered via `CreateMap`, so `.ReverseMap()` is unreachable. The reverse direction is just a separately-materialized dynamic TypeMap (cache miss on the reverse pair → detector fires again).
- **`Map<object>(poco)` returning `ExpandoObject` automatically.** v1 leaves `Map<object>` semantics unchanged (returns whatever an explicitly-registered `(MyPoco, object)` map would, or throws). Users wanting `dynamic`-friendly output write `Map<ExpandoObject>` explicitly. Documented in §3.4.
- **Profile-scoped value transformer composition for dynamic TypeMaps.** Dynamic TypeMaps are global-scope only — profile-scoped transformers do NOT fire on their properties. Global-scope transformers DO fire (composed via the existing `TransformerResolver` pipeline at materialization-seal time). See §7.4.

---

## 2. Architecture Overview

### 2.1 Lazy materialization, single insertion point

```
mapper.Map<TDest>(src)  /  Map<TSrc, TDest>(src)  /  Map<TSrc, TDest>(src, dest)
   │
   ▼
MapperRegistry.GetTypeMap(typePair)
   │
   ├── ConcurrentDictionary lookup (closed-pair cache)         ◄─ HIT: return
   │     └── populated by explicit CreateMap registrations and
   │         already-materialized open-generic / dynamic pairs
   │
   ├── Open-generic template scan + materialize                ◄─ HIT: return
   │     └── existing logic from #9 (FindMatchingOpenGenericTemplate)
   │
   ├── DYNAMIC-SHAPE DETECTOR (NEW, this feature)              ◄─ HIT: materialize and return
   │     │
   │     ├── DynamicShape.IsDynamicPair(typePair)
   │     │     └── exactly one side is a recognized dynamic shape XOR the other side
   │     │
   │     ├── Branch on direction:
   │     │     ├── Source is dynamic    →  Build dict-to-POCO TypeMap
   │     │     └── Destination is dynamic →  Build POCO-to-dict TypeMap
   │     │
   │     └── _typeMaps.GetOrAdd(closedPair, materializedTypeMap)
   │
   └── MISS → caller throws AtlasMappingException("no map registered")
```

The detector is the **third stage** in `GetTypeMap`'s lookup pipeline, after the closed-pair cache and after the open-generic template scan. The order is deliberate:

- Closed-pair first: explicit registrations always win (`CreateMap<MyPoco, IDictionary<string, object>>()` precedence).
- Open generics second: a `(typeof(Source<>), typeof(Destination<>))` template can match a closed pair where neither side is a recognized dynamic shape. The dynamic detector and the open-generic detector are mutually exclusive for any given pair (a closed pair can match at most one — the open-generic template requires both sides to be constructed-generic; the dynamic detector requires exactly one side to be a recognized exact-type-match dynamic shape). Order matters defensively: if a future feature relaxes the open-generic constraints, "open-generic first" would still be preferred since open-generic registrations are explicit and the dynamic detector is implicit.
- Dynamic detector third: the pure-convention fallback.

### 2.2 New components

| Component | Type | Lives in | Responsibility |
|---|---|---|---|
| `DynamicShape` | `internal static class` | `src/Atlas/Internal/DynamicShape.cs` | Predicates `IsDynamicShape(Type)` and `IsDynamicPair(TypePair)`; static factory `MaterializeTypeMap(...)` |
| `DynamicPlanBuilder` | `internal static class` | `src/Atlas/Internal/DynamicPlanBuilder.cs` | Two methods: `BuildDictToPocoLambda(TypeMap, MapperRegistry)` and `BuildPocoToDictLambda(TypeMap, MapperRegistry)`. Called by `ExecutionPlanBuilder` when `TypeMap.IsDynamic` is set. |
| `MappingInvoker.ConvertObjectTo<T>` | `internal static T` (existing class, new method) | `src/Atlas/Internal/MappingInvoker.cs` | Per-key value coercion helper for the dict→POCO codegen. Delegates to `NumericConversions`, `Convert.ChangeType`, registered TypeMap lookup, and `ConvertibleHelpers` for Guid/DateTime parsing. |
| `MappingInvoker.SerializeValue` | `internal static object?` (existing class, new method) | `src/Atlas/Internal/MappingInvoker.cs` | POCO→dict per-property emit helper. Boxes primitives, recurses through `mapper.Map<TDecl, ExpandoObject>` for nested POCOs, emits `List<object?>` for collections. |
| `MappingInvoker.ScanPrefix` | `internal static IDictionary<string, object>?` | `src/Atlas/Internal/MappingInvoker.cs` | Dot-notation fallback scan helper for the dict→POCO codegen. |
| `MappingInvoker.SerializeCollection<T>` | `internal static List<object?>?` | `src/Atlas/Internal/MappingInvoker.cs` | POCO→dict collection-emit helper. |
| `MappingInvoker.SerializeDictionary<TKey, TValue>` | `internal static IDictionary<string, object>?` | `src/Atlas/Internal/MappingInvoker.cs` | POCO→dict typed-POCO-dictionary-emit helper. |

### 2.3 Modified components

| Component | Lives in | Change |
|---|---|---|
| `TypeMap` | `src/Atlas/Internal/TypeMap.cs` | New `bool IsDynamic { get; init; }` field (defaults to `false`; set `true` only by `DynamicShape.MaterializeTypeMap`). |
| `PropertyMap` | `src/Atlas/Internal/PropertyMap.cs` | New `string? DynamicKey { get; init; }` field (defaults to `null`; non-null only on dynamic TypeMaps' synthesized PropertyMaps). |
| `MapperRegistry.GetTypeMap` | `src/Atlas/Internal/MapperRegistry.cs` | Adds the dynamic-shape detection branch as the third lookup stage (after closed-pair cache, after open-generic template scan). |
| `ExecutionPlanBuilder.Build` | `src/Atlas/Internal/ExecutionPlanBuilder.cs` | Adds `if (typeMap.IsDynamic) return DynamicPlanBuilder.Build(typeMap, registry);` as the first branch. |
| `ExecutionPlanBuilder.IsDictionary` (or sibling routing) | `src/Atlas/Internal/ExecutionPlanBuilder.cs` | Extended to recognize `(ExpandoObject, ExpandoObject)`, `(IDictionary<string, object>, IDictionary<string, object>)`, and mixed-shape dynamic round-trips as element-mapping-eligible verbatim copies. See §7.2. |
| `ProjectionPlanBuilder.Build` | `src/Atlas.Projections/Internal/ProjectionPlanBuilder.cs` | Adds projection-build-time rejection for `typeMap.IsDynamic == true`. |
| `ConfigurationValidator.Walk` | `src/Atlas/Internal/ConfigurationValidator.cs` | Skips dynamic TypeMaps via the same `IsDynamic` short-circuit OpenGenerics established for templates. |
| `TransformerResolver.Resolve` | `src/Atlas/Internal/TransformerResolver.cs` | Guards on null `pm.DestinationProperty` (POCO→dict synthesizes PMs with null destination property — see §4.3). Verified at implementation; if guard already exists, no change. |

### 2.4 Why a synthesized `PropertyMap[]` rather than a fully-custom codegen path

Three reasons:

1. **Aligns with the existing `TypeMap` data shape** — no fork in the type-system. `TypeMap.PropertyMaps` continues to be an `IReadOnlyList<PropertyMap>`; the consumers that walk it can be updated incrementally.
2. **`Atlas.Projections` already inspects `TypeMap.IsDynamic` and rejects** — it never sees the synthesized `PropertyMap`s. No projection-side migration needed.
3. **Future per-member overrides drop in cleanly.** If v3 adds `CreateMap<MyPoco, IDictionary<string, object>>().ForKey("name", ...)`, the explicit registration populates the same `PropertyMap[]` (with explicit `Ignore`/`MapFrom`/`DynamicKey`/etc.), and `DynamicPlanBuilder` consumes whatever `PropertyMap[]` it's given.

---

## 3. Public API Surface

### 3.1 No new fluent surface

The feature ships **zero new public methods/types/extension points** on `MapperConfigurationExpression`, `MapperProfile`, `IMappingExpression`, or `IMemberConfigurationExpression`. Everything is convention-only, materialized at first call. The `IMapper.Map<...>(...)` overloads users already use are sufficient.

### 3.2 Recognized call patterns

```csharp
// Dict → POCO
MyPoco p1 = mapper.Map<MyPoco>(someDictionary);                       // matches Map<TDest>(object)
MyPoco p2 = mapper.Map<IDictionary<string, object>, MyPoco>(d);       // matches Map<TSrc, TDest>(TSrc)
MyPoco p3 = mapper.Map<ExpandoObject, MyPoco>(d);
MyPoco p4 = mapper.Map<Dictionary<string, object>, MyPoco>(d);

// POCO → Dict (destination concrete type chosen explicitly)
ExpandoObject              e = mapper.Map<ExpandoObject>(somePoco);
IDictionary<string, object> i = mapper.Map<IDictionary<string, object>>(somePoco);
Dictionary<string, object>  d = mapper.Map<Dictionary<string, object>>(somePoco);

// Update-in-place
mapper.Map(someDict, existingPoco);   // missing keys preserve existing destination values
mapper.Map(somePoco, existingDict);   // POCO-side properties OVERWRITE matching dict keys; other keys preserved
```

### 3.3 Concrete-type contract for POCO→dict outputs

| Destination type argument | Materialized concrete type returned |
|---|---|
| `ExpandoObject` | `ExpandoObject` |
| `Dictionary<string, object>` | `Dictionary<string, object>` |
| `IDictionary<string, object>` | `ExpandoObject` (declared as the abstraction; runtime type is `ExpandoObject`) |

The codegen for the `IDictionary<string, object>` destination path emits `Expression.New(typeof(ExpandoObject))` and assigns into a local typed `IDictionary<string, object>`. Choosing `ExpandoObject` for the abstraction default is the most ergonomic: downstream code that does `dynamic d = result; var x = d.SomeKey;` works because `ExpandoObject` participates in dynamic dispatch via its `IDynamicMetaObjectProvider` implementation. `Dictionary<string, object>` returns do not participate in dynamic dispatch (calling `dict.SomeKey` on a `Dictionary<string, object>` will not work — only `dict["SomeKey"]` will).

### 3.4 The `dynamic` keyword

`mapper.Map<dynamic>(somePoco)` compiles to `Map<object>(somePoco)` at the IL level (the `dynamic` keyword is erased to `object` plus call-site sugar). v1 does **NOT** special-case `Map<object>` to mean "give me a dynamic shape." Users wanting dynamic-friendly output write `Map<ExpandoObject>` explicitly:

```csharp
// Wrong — returns whatever (MyPoco, object) registered map produces, or throws if none
dynamic d = mapper.Map<dynamic>(somePoco);

// Right — explicit ExpandoObject return, which downstream `dynamic` consumers can use:
dynamic d = mapper.Map<ExpandoObject>(somePoco);
var x = d.SomeKey;  // works
```

**Rationale.** Special-casing `Map<object>` would break any user with an explicitly-registered `(TSrc, object)` map (e.g., for boxing scenarios) — too aggressive. Documenting the explicit pattern is preferable.

### 3.5 No `MemberList.Source/Destination` validation knob

Dynamic TypeMaps are never validated by `AssertConfigurationIsValid()` — there's no registered pair to walk, the validator's rules don't apply (no member-resolution decisions to verify; no per-call data to examine). Same exclusion strategy OpenGenerics uses for templates (`IsDynamic == true` short-circuits the validator's per-TypeMap inspection).

### 3.6 Exception surface — additive only

| Exception | When | Direction |
|---|---|---|
| `AtlasMappingException` (existing) | dict→POCO conversion fails for a single key (e.g., dict has `"Age" = "abc"`, dest is `int`) — message names the key + source-runtime-type + dest-type | Dict → POCO (per-call, runtime) |
| `AtlasMappingException` (existing) | POCO→dict serialization fails (only path: a getter throws — the dict emit itself is total) | POCO → Dict (per-call, runtime) |
| `AtlasProjectionException` (existing) | `ProjectTo<TDest>()` is called against a dynamic TypeMap | Either direction (build-time) |
| `AtlasConfigurationException` (existing) | NOT raised for dynamic mapping in v1 — there's no config-time validation gate. Closed-pair-precedence collisions don't throw because the explicit registration simply wins. | n/a |

---

## 4. Internal Data Shape

### 4.1 `TypeMap.IsDynamic` field

```csharp
internal sealed class TypeMap
{
    // ... existing fields ...
    public bool IsDynamic { get; init; }   // NEW
}
```

Set `true` only by `DynamicShape.MaterializeTypeMap`. Read by:
- `ExecutionPlanBuilder.Build` — branches to `DynamicPlanBuilder.Build` first
- `ConfigurationValidator.Walk` — skips dynamic TypeMaps
- `ProjectionPlanBuilder.Build` — rejects dynamic TypeMaps with `AtlasProjectionException`

### 4.2 `PropertyMap.DynamicKey` field

```csharp
internal sealed class PropertyMap
{
    // ... existing fields ...
    public string? DynamicKey { get; init; }   // NEW
}
```

Non-null **iff** the containing `TypeMap` has `IsDynamic == true`. The value is the dictionary key under which to read (dict→POCO direction) or write (POCO→dict direction). Convention: `DynamicKey == pocoMember.Name` for the matching POCO side member.

For a regular `(MyPoco, MyOtherPoco)` `TypeMap`, `DynamicKey` is null on every `PropertyMap` and existing consumers (`ExecutionPlanBuilder.BuildPocoLambda`, `ProjectionPlanBuilder.BuildBody`, `ConfigurationValidator.Walk`, `InheritanceMerger.CopyConfig`, `TransformerResolver.Resolve`) read `SourceMember`/`DestinationProperty` exactly as before — no change.

### 4.3 `DynamicShape` — predicate + factory

```csharp
internal static class DynamicShape
{
    private static readonly Type[] _shapes =
    {
        typeof(IDictionary<string, object>),
        typeof(ExpandoObject),
        typeof(Dictionary<string, object>),
    };

    /// <summary>True if <paramref name="t"/> is one of the three recognized dynamic shapes.</summary>
    internal static bool IsDynamicShape(Type t) => Array.IndexOf(_shapes, t) >= 0;

    /// <summary>
    /// True iff exactly one side of the pair is a recognized dynamic shape (XOR).
    /// Self-pairs (both dynamic) and non-pairs (neither dynamic) return false.
    /// </summary>
    internal static bool IsDynamicPair(TypePair pair) =>
        IsDynamicShape(pair.Source) ^ IsDynamicShape(pair.Destination);

    /// <summary>
    /// Materializes a dynamic TypeMap on demand. Called by MapperRegistry.GetTypeMap when the
    /// closed-pair cache and open-generic template scan both miss and IsDynamicPair returns true.
    /// </summary>
    internal static TypeMap MaterializeTypeMap(
        TypePair pair,
        ValueTransformerCollection globalTransformers,
        ConventionOptions conventions)
    {
        if (IsDynamicShape(pair.Source))
            return BuildDictToPocoTypeMap(pair, globalTransformers, conventions);
        else
            return BuildPocoToDictTypeMap(pair, globalTransformers, conventions);
    }

    private static TypeMap BuildDictToPocoTypeMap(...)
    {
        var pocoType = pair.Destination;
        var pms = new List<PropertyMap>();

        foreach (var pocoMember in GetWritableMembers(pocoType))
        {
            pms.Add(new PropertyMap
            {
                DestinationProperty = pocoMember,
                SourceMember = null,
                DynamicKey = pocoMember.Name,
                // Other PM fields (NullSubstitute, Condition, etc.) all default
            });
        }

        var typeMap = new TypeMap
        {
            SourceType = pair.Source,
            DestinationType = pair.Destination,
            PropertyMaps = pms,
            IsDynamic = true,
            OriginatingProfile = null,                    // Dynamic TypeMaps are global-scope only — see §7.4
            RegistrationOrigin = "<dynamic>",
        };

        // TransformerResolver runs against globalTransformers (profile-scope skipped).
        // PMs with DynamicKey != null get the same resolved transformer chain as a regular member-keyed PM.
        TransformerResolver.Resolve(typeMap, globalTransformers, profileTransformers: null);

        typeMap.Seal();
        return typeMap;
    }

    private static TypeMap BuildPocoToDictTypeMap(...)
    {
        var pocoType = pair.Source;
        var pms = new List<PropertyMap>();

        foreach (var pocoMember in GetReadableMembers(pocoType))
        {
            pms.Add(new PropertyMap
            {
                SourceMember = pocoMember,
                DestinationProperty = null,
                DynamicKey = pocoMember.Name,
            });
        }

        var typeMap = new TypeMap
        {
            SourceType = pair.Source,
            DestinationType = pair.Destination,
            PropertyMaps = pms,
            IsDynamic = true,
            OriginatingProfile = null,
            RegistrationOrigin = "<dynamic>",
        };

        TransformerResolver.Resolve(typeMap, globalTransformers, profileTransformers: null);

        typeMap.Seal();
        return typeMap;
    }
}
```

### 4.4 `MapperRegistry.GetTypeMap` extension

```csharp
public TypeMap? GetTypeMap(TypePair pair)
{
    // Stage 1: closed-pair cache (existing)
    if (_typeMaps.TryGetValue(pair, out var existing)) return existing;

    // Stage 2: open-generic template scan + materialize (existing from #9)
    if (TryFindMatchingOpenGenericTemplate(pair, out var template))
        return _typeMaps.GetOrAdd(pair, _ => MaterializeFromOpenGeneric(pair, template));

    // Stage 3: dynamic-shape detection (NEW)
    if (DynamicShape.IsDynamicPair(pair))
        return _typeMaps.GetOrAdd(pair, _ =>
            DynamicShape.MaterializeTypeMap(pair, _globalTransformers, _conventionOptions));

    return null;  // caller throws AtlasMappingException("no map registered")
}
```

Stage 3 uses `_typeMaps.GetOrAdd` exactly as Stage 2 does — thread-safe, single-materialization-wins semantics from #9 carry over verbatim. The materialized dynamic TypeMap is cached under the closed pair, so subsequent calls hit Stage 1 directly.

### 4.5 Member discovery rules

For dict→POCO (`pocoType = pair.Destination`):
- **Public writable instance properties only.** Static, non-public, indexers, fields excluded.
- For **constructor-using** POCOs (no public parameterless ctor — records, primary ctors, types with `required` properties or only-init-parameter ctors), each ctor parameter contributes a PropertyMap whose `DestinationProperty` is `null` and `SourceMember` is `null` and `DynamicKey = paramName`. The codegen branches on `pocoType.HasPublicParameterlessConstructor()` to decide between member-init and ctor-init pipelines (existing v1 ctor-mapping helper extended for dynamic context).

For POCO→dict (`pocoType = pair.Source`):
- **Public readable instance properties only.** Read-only properties (no setter) ARE included on the emit side. Static, non-public, indexers, fields excluded.

These rules match Atlas's v1 convention engine exactly — the dynamic detector reuses the existing `MemberAccessor` / `ConventionEngine.GetMembers` helpers.

### 4.6 Bug-4 / Bug-5 / Bug-6 cross-package consumer audit

**Bug-4 (cross-package consumer not audited when adding a new field to a shared shape):** The new `PropertyMap.DynamicKey` field is consumed by `DynamicPlanBuilder` only. Audit:

| Consumer | Behavior |
|---|---|
| `ExecutionPlanBuilder.BuildPocoLambda` | Never sees dynamic TypeMaps (branched out at the top via `IsDynamic` check). ✅ |
| `ProjectionPlanBuilder.BuildBody` | Rejected at projection-build time — `typeMap.IsDynamic` short-circuits before walking PMs. ✅ |
| `InheritanceMerger.CopyConfig` | Dynamic TypeMaps have no `IncludedBases`/`IncludedDerived` (no fluent surface to declare them on). ✅ |
| `ConfigurationValidator.Walk` | Skipped via `if (typeMap.IsDynamic) continue;`. ✅ |
| `TransformerResolver.Resolve` | Reads `pm.DestinationProperty`. Dict→POCO direction sets `DestinationProperty` (the POCO member); POCO→dict sets it to `null`. **The resolver must guard on null.** Implementation task verifies and adds the guard if absent. ✅ (verified at implementation) |
| `ReverseMap` machinery | Dynamic TypeMaps don't go through `MappingExpression.ReverseMap` — only `CreateMap`-registered TypeMaps do. ✅ |

**Bug-5 (scope-identifying TypeMap metadata not propagated across related TypeMaps):** Dynamic TypeMaps don't have a sibling/derived/reverse pair to propagate `OriginatingProfile` to — there's only one materialized TypeMap per closed pair. Propagation is inert. `OriginatingProfile = null` is set explicitly in the materialization factory (documented as "global-scope only" — see §7.4).

**Bug-6 (`Coalesce` over `Nullable<T>` with widening destination):** The dict→POCO codegen extracts `dict[key]` as `object`, then unboxes/converts via the existing `ConvertOrMap` helper. There's no `Coalesce` step (dict values are `object` and never `Nullable<T>`-typed). The asymmetric-nullable-widening branches added in #8 (`Nullable<T> → U` and `T → Nullable<U>`) are still consulted via the existing pipeline if the dest property is `int?` and the dict value is a `long`, but the bug surface itself doesn't reappear here.

---

## 5. Codegen — Dict → POCO

### 5.1 Generated lambda shape

Worked example: `IDictionary<string, object>` → `OrderDto` where `OrderDto` has:
- `int OrderId`
- `string CustomerName`
- `Customer Customer` (nested POCO with `string Name`, `string Email`)
- `List<OrderLine> Lines`

```csharp
(IDictionary<string, object> src, MappingContext ctx) =>
{
    var dst = new OrderDto();

    // Per-property block emitted for each public writable property of OrderDto:

    // 1. int OrderId — primitive
    if (src.TryGetValue("OrderId", out var v_OrderId))
        dst.OrderId = MappingInvoker.ConvertObjectTo<int>(v_OrderId, ctx);
    // missing key → leave at default(int) = 0

    // 2. string? CustomerName — primitive (reference type)
    if (src.TryGetValue("CustomerName", out var v_CustomerName))
        dst.CustomerName = MappingInvoker.ConvertObjectTo<string?>(v_CustomerName, ctx);

    // 3. Customer Customer — nested POCO with prefix-fallback
    if (src.TryGetValue("Customer", out var v_Customer))
    {
        if (v_Customer is null)
            dst.Customer = null;
        else if (v_Customer is IDictionary<string, object> nested)
            dst.Customer = ctx.Mapper.Map<IDictionary<string, object>, Customer>(nested);
        else if (v_Customer is Customer typed)
            dst.Customer = typed;
        else
            throw new AtlasMappingException(
                $"Cannot convert {v_Customer.GetType()} to Customer at key 'Customer'");
    }
    else
    {
        // Dot-notation fallback: scan for "Customer." prefix and synthesize a nested dict
        var nested = MappingInvoker.ScanPrefix(src, "Customer.", ctx.NameComparison);
        if (nested is not null)
            dst.Customer = ctx.Mapper.Map<IDictionary<string, object>, Customer>(nested);
    }

    // 4. List<OrderLine> Lines — collection
    if (src.TryGetValue("Lines", out var v_Lines))
        dst.Lines = MappingInvoker.ConvertObjectToList<OrderLine>(v_Lines, ctx);

    return dst;
}
```

### 5.2 Update-in-place variant

`Map(src, existing)` wraps each per-property block in an `IfThen` (no else-branch) so missing-key paths leave existing destination values untouched:

```csharp
if (src.TryGetValue("OrderId", out var v))
    dst.OrderId = MappingInvoker.ConvertObjectTo<int>(v, ctx);
// Missing key → no else branch → dst.OrderId untouched
```

For nested POCO destinations under update-in-place, the nested-POCO assignment branch becomes `dst.Customer = mapper.Map(nested, dst.Customer ?? new Customer())` — recurse into the nested map preserving the existing nested instance. This requires the recursive `mapper.Map(srcDict, existing)` overload to exist (it already does in v1).

### 5.3 `ConvertObjectTo<T>` — runtime helper

```csharp
internal static class MappingInvoker
{
    public static T ConvertObjectTo<T>(object? value, MappingContext ctx)
    {
        if (value is null)
            return default!;                                  // null → default(T)

        if (value is T direct)
            return direct;                                    // identity / assignable

        var srcType = value.GetType();
        var dstType = typeof(T);

        // 1. Numeric widening / IConvertible primitives
        if (TryNumericOrConvertible(value, srcType, dstType, out var converted))
            return (T)converted!;

        // 2. Registered (srcRuntimeType, dstType) TypeMap — invokes mapper.Map for nested POCOs
        if (ctx.Registry.GetTypeMap(new TypePair(srcType, dstType)) is { } nested)
            return (T)ctx.Mapper.Map(value, srcType, dstType);

        // 3. String parsing: Guid, DateTime, TimeSpan, enum
        if (value is string s && TryParseString(s, dstType, out var parsed))
            return (T)parsed!;

        // 4. IDictionary<string, object> → POCO (recursive dynamic detection)
        if (value is IDictionary<string, object> sub && IsPocoLike(dstType))
            return (T)ctx.Mapper.Map(sub, typeof(IDictionary<string, object>), dstType);

        // 5. IEnumerable<object> → IEnumerable<TElement> (collection element-mapping)
        if (value is IEnumerable enumerable && TryGetCollectionElementType(dstType, out var elemType))
            return (T)ConvertToCollection(enumerable, elemType, dstType, ctx);

        throw new AtlasMappingException($"Cannot convert {srcType} value to {dstType}");
    }
}
```

The helper centralizes runtime-typed coercion. **No new conversion semantics** — it composes existing helpers (`NumericConversions`, `Convert.ChangeType`, registered-typemap lookup via `ctx.Mapper`/`ctx.Registry`).

### 5.4 `ScanPrefix` — dot-notation fallback

```csharp
internal static IDictionary<string, object>? ScanPrefix(
    IDictionary<string, object> src,
    string prefix,
    StringComparison cmp)
{
    Dictionary<string, object>? result = null;
    foreach (var kv in src)
    {
        if (kv.Key.StartsWith(prefix, cmp))
        {
            result ??= new Dictionary<string, object>();
            result[kv.Key.Substring(prefix.Length)] = kv.Value;
            // Nested dot-notation ("Customer.Address.City") flows through naturally —
            // the recursive Map call sees "Address.City" and applies the same prefix-scan.
        }
    }
    return result;
}
```

`cmp` is `StringComparison.Ordinal` (case-sensitive default) or `StringComparison.OrdinalIgnoreCase` (when `WithCaseInsensitiveMatching()` was called). Sourced from `MappingContext.NameComparison`.

### 5.5 `ConvertObjectToList<T>` — collection helper

```csharp
internal static List<T> ConvertObjectToList<T>(object? value, MappingContext ctx)
{
    if (value is null) return new List<T>();
    if (value is IEnumerable enumerable)
    {
        var list = new List<T>();
        foreach (var item in enumerable)
            list.Add(ConvertObjectTo<T>(item, ctx));      // element-wise recursion
        return list;
    }
    throw new AtlasMappingException($"Cannot convert {value.GetType()} to List<{typeof(T)}>");
}
```

Variants for `T[]`, `IEnumerable<T>`, `HashSet<T>`, etc., follow the same pattern. The codegen picks the right variant based on the destination property's declared type, reusing v1's collection-routing logic.

### 5.6 Constructor-using POCOs

For records, primary constructors, and types with `required` properties or only-init-parameter ctors, the codegen detects `pocoType.HasPublicParameterlessConstructor() == false` and switches to the existing v1 ctor-mapping pipeline:

```csharp
(IDictionary<string, object> src, MappingContext ctx) =>
{
    int p_OrderId = src.TryGetValue("OrderId", out var v0)
        ? MappingInvoker.ConvertObjectTo<int>(v0, ctx)
        : default;
    string p_CustomerName = src.TryGetValue("CustomerName", out var v1)
        ? MappingInvoker.ConvertObjectTo<string>(v1, ctx)
        : default!;

    var dst = new OrderDto(p_OrderId, p_CustomerName);

    // init-only / required properties beyond the ctor populated via per-property emit (same as §5.1)

    return dst;
}
```

Each ctor parameter's name is matched as a dict key. Init-only / `required` properties beyond the ctor are populated via the same per-property emit. For `required` properties whose key is missing AND no default value applies, the runtime throws `AtlasMappingException("'X' is required but missing from source dictionary")`.

### 5.7 Performance characteristics

A dict→POCO map without dot-notation pays:
- One `TryGetValue` per destination property (O(1) on `Dictionary<string, object>`)
- One destination object allocation
- One nested POCO allocation per nested member
- One `List<T>` allocation per collection-typed property

Same allocation budget as v1's POCO→POCO mapping, plus one dictionary lookup per property. Dot-notation incurs an O(N) prefix-scan once per missing-key nested POCO.

---

## 6. Codegen — POCO → Dict

### 6.1 Generated lambda shape

Worked example: `OrderDto` → `ExpandoObject` (same `OrderDto` from §5.1):

```csharp
(OrderDto src, MappingContext ctx) =>
{
    var dst = (IDictionary<string, object>)new ExpandoObject();    // for ExpandoObject dest

    // Per-property emitted for each public readable property of OrderDto:

    // 1. int OrderId — primitive
    dst["OrderId"] = (object)src.OrderId;

    // 2. string? CustomerName — primitive (reference type)
    dst["CustomerName"] = src.CustomerName;                        // null is a valid dict value

    // 3. Customer? Customer — nested POCO
    dst["Customer"] = src.Customer is null
        ? (object?)null
        : ctx.Mapper.Map<Customer, ExpandoObject>(src.Customer);   // always emits ExpandoObject for nested

    // 4. List<OrderLine> Lines — collection
    dst["Lines"] = MappingInvoker.SerializeCollection(src.Lines, ctx);

    return dst;
}
```

### 6.2 Concrete-type contract (recap from §3.3)

| Destination type argument | Codegen `Expression.New` |
|---|---|
| `ExpandoObject` | `new ExpandoObject()` (cast to `IDictionary<string, object>` for member-access) |
| `Dictionary<string, object>` | `new Dictionary<string, object>(initialCapacity: <propertyCount>)` |
| `IDictionary<string, object>` | `new ExpandoObject()` (declared as the abstraction) |

### 6.3 Nested POCO emission — always `ExpandoObject`

The recursive `ctx.Mapper.Map<Customer, ExpandoObject>(src.Customer)` call is a fresh `Map` invocation that hits `MapperRegistry.GetTypeMap` — which materializes a `(Customer, ExpandoObject)` dynamic TypeMap on demand if one doesn't exist. The recursion terminates naturally at primitives.

**Choosing `ExpandoObject` for nested values regardless of the outer destination's concrete type** gives a uniform JSON-clean shape: whether the user asks for `Dictionary<string, object>` or `ExpandoObject` at the top level, every nested level is `ExpandoObject`. Both serialize correctly via `System.Text.Json`/Newtonsoft.

### 6.4 `SerializeCollection<T>` and `SerializeValue` runtime helpers

```csharp
public static List<object?>? SerializeCollection<T>(IEnumerable<T>? src, MappingContext ctx)
{
    if (src is null) return null;
    var list = new List<object?>();
    foreach (var item in src)
        list.Add(SerializeValue(item, typeof(T), ctx));
    return list;
}

private static object? SerializeValue(object? value, Type declaredType, MappingContext ctx)
{
    if (value is null) return null;
    if (IsPrimitiveOrString(declaredType)) return value;             // boxed verbatim
    if (declaredType.IsEnum) return Convert.ChangeType(value, Enum.GetUnderlyingType(declaredType));
    if (IsTypedPocoDictionary(declaredType, out var keyType, out var valueType))
        return SerializeDictionaryReflective(value, keyType, valueType, ctx);
    if (IsCollection(declaredType, out var elemType))
        return SerializeCollectionReflective(value, elemType, ctx);
    // Nested POCO: recurse via mapper.Map<TDecl, ExpandoObject>
    return ctx.Mapper.Map(value, declaredType, typeof(ExpandoObject));
}
```

`IsPrimitiveOrString` covers `int`, `long`, `double`, `decimal`, `bool`, `string`, `Guid`, `DateTime`, `DateTimeOffset`, `TimeSpan`, `byte`, `short`, `char`, etc. — anything that round-trips through JSON without further structuring.

### 6.5 Typed-POCO-dictionary recursion

```csharp
public static IDictionary<string, object>? SerializeDictionary<TKey, TValue>(
    IDictionary<TKey, TValue>? src,
    MappingContext ctx) where TKey : notnull
{
    if (src is null) return null;
    var dst = new ExpandoObject() as IDictionary<string, object>;
    foreach (var kv in src)
        dst[kv.Key.ToString()!] = SerializeValue(kv.Value, typeof(TValue), ctx)!;
    return dst;
}
```

If `TKey` is not `string`, `kv.Key.ToString()` is used (`int`/`Guid`/etc. round-trip through their `ToString()` representation). The destination is always `IDictionary<string, object>` shape — the v1 `Dictionary<K,V>` element-mapping pipeline does NOT apply when the target is a dynamic shape.

### 6.6 Enum emission

Enums emit as the **underlying integer** value:

```csharp
dst["Status"] = (object)(int)src.Status;   // for Status : enum-of-int
```

Matches AutoMapper's default. The user's `EnumSurface` (#3) per-member overrides do NOT apply (no `CreateMap` registration to read them from). Documented in §1.2 non-goals.

### 6.7 Read-only properties / fields

- **Read-only properties** (`get` only): emitted on POCO→dict (the value is read; no setter needed). Skipped on dict→POCO (no setter to call).
- **Write-only properties** (set only, no getter): skipped on POCO→dict. Populated on dict→POCO via the setter.
- **Fields**: skipped on both directions (matches Atlas's v1 convention engine — fields are not member candidates).
- **Static**: skipped.
- **Indexers**: skipped.

### 6.8 Property-key emission verbatim

The dict key is the property's `Name` exactly. No naming-policy translation. Users wanting `snake_case` or `camelCase` keys post-process the dict themselves (or wait for v3's `WithDynamicKeyConvention(...)` toggle).

### 6.9 Update-in-place

`Map(src, existing)` where `existing` is an `ExpandoObject`/`Dictionary<string, object>`:

```csharp
// Generated: emit dst[key] = value for every source property.
// Existing keys NOT present on the source POCO are preserved (the codegen only writes to keys it knows about).
dst["OrderId"] = (object)src.OrderId;
dst["CustomerName"] = src.CustomerName;
dst["Customer"] = src.Customer is null ? null : ctx.Mapper.Map<Customer, ExpandoObject>(src.Customer);
dst["Lines"] = SerializeCollection(src.Lines, ctx);
return dst;
```

Existing dict keys NOT corresponding to a source POCO property are preserved. Existing nested `Customer` ExpandoObject is REPLACED (not deep-merged) — the recursive `Map<Customer, ExpandoObject>` call returns a fresh `ExpandoObject`. Documenting this as a known limitation; deep-merge for nested dynamic emit is deferred to v3 if a use case emerges.

### 6.10 Allocation budget

A POCO→dict map allocates:
- 1 destination dict (or `ExpandoObject` for the abstraction default)
- 1 nested `ExpandoObject` per nested POCO property
- 1 `List<object?>` per collection-typed property
- 1 nested `ExpandoObject` per element of typed-POCO-dictionary properties

Each allocation is "the destination's own data shape" — no internal context bags or per-call dictionaries. Same budget category as Atlas's v1 nested-POCO mapping.

---

## 7. Edge cases and contract details

### 7.1 Closed-pair-takes-precedence (§1.1 Goal 6 detail)

The detector runs AFTER the closed-pair cache lookup. So:

```csharp
cfg.CreateMap<MyPoco, IDictionary<string, object>>();   // explicit registration
```

…wins over the dynamic detector — the explicit TypeMap is hit by the cache lookup, the detector never fires for this pair.

**v1 limitation noted (§7.5):** The explicit registration in the example above produces a regular non-dynamic TypeMap that runs through `BuildPocoLambda` — which would generate broken codegen for an `IDictionary<string, object>` destination (it expects member-init/property-set calls against the destination's type). Practical guidance: **users should not register explicit `CreateMap` calls for dynamic-shape pairs in v1.** Deferred to v3 as "explicit per-member overrides on dynamic TypeMaps." A dedicated test in `DynamicMapping_Routing_Tests` annotates this v1 limitation and confirms the precedence rule mechanically (the detector does NOT fire when an explicit registration exists), independent of whether the registered codegen actually works.

### 7.2 Dynamic self-pair routing

`(Dictionary<string, object>, Dictionary<string, object>)`, `(ExpandoObject, ExpandoObject)`, `(IDictionary<string, object>, IDictionary<string, object>)`, and mixed-shape pairs like `(ExpandoObject, Dictionary<string, object>)` all have **both** sides as dynamic shapes. `IsDynamicPair` returns FALSE (XOR), so the detector does NOT fire. These pairs need their own routing.

Routing extension in `ExecutionPlanBuilder`:

```csharp
public LambdaExpression Build(TypeMap typeMap, MapperRegistry registry)
{
    if (typeMap.IsDynamic)
        return DynamicPlanBuilder.Build(typeMap, registry);

    if (typeMap.ConvertUsing is not null)
        return BuildConvertUsingLambda(typeMap, registry);

    if (IsDynamicSelfPair(typeMap.SourceType, typeMap.DestinationType))   // NEW — see below
        return BuildDynamicVerbatimCopyLambda(typeMap, registry);

    if (IsCollection(typeMap.SourceType) && IsCollection(typeMap.DestinationType))
        return BuildCollectionLambda(typeMap, registry);

    if (IsDictionary(typeMap.SourceType) && IsDictionary(typeMap.DestinationType))
        return BuildDictionaryLambda(typeMap, registry);

    return BuildPocoLambda(typeMap, registry);
}

private static bool IsDynamicSelfPair(Type src, Type dst) =>
    DynamicShape.IsDynamicShape(src) && DynamicShape.IsDynamicShape(dst);

private static LambdaExpression BuildDynamicVerbatimCopyLambda(TypeMap typeMap, MapperRegistry registry)
{
    // Emit: foreach (kv in src) dst[kv.Key] = kv.Value;
    // Destination concrete type per §3.3 contract.
}
```

Self-pairs need an explicit `CreateMap` registration to materialize a TypeMap (the dynamic detector doesn't fire for them). Most users will not register self-pair maps; if they do, the verbatim-copy lambda runs. **Practical impact:** in the common case (no explicit self-pair registration), `mapper.Map<ExpandoObject>(someExpando)` throws "no map registered" — which is fine, since the sensible use is `Map<MyPoco>(someExpando)` (dict→POCO direction).

To make self-pair round-trips work without explicit registration, the **detector could be extended** to also fire when both sides are dynamic and equal (or compatible) — but this overlaps the existing dictionary-element-mapping path for `(Dictionary<,>, Dictionary<,>)` and creates ambiguity for `(IDictionary, IDictionary)`. **v1 decision:** self-pair dynamic round-trips require an explicit `CreateMap` registration. Documented in `DynamicMapping_Routing_Tests`.

### 7.3 Collection-of-dynamic recursion

`List<IDictionary<string, object>>` → `List<MyPoco>`: the OUTER pair is `(List<IDictionary<string, object>>, List<MyPoco>)` — neither side is itself one of the three named dynamic shapes. **However**, the detector DOES need to fire on this outer pair so that `MapperRegistry.GetTypeMap` returns a non-null `TypeMap` (otherwise `MappingInvoker.Invoke` throws "no map registered" before any per-element recursion has a chance to engage).

The shipped implementation handles this in two stages:

1. **Outer-pair detection.** `DynamicShape.IsDynamicPair` recognizes `(IEnumerable<X>, IEnumerable<Y>)` pairs (and other collection shapes from `GetCollectionElementType`'s allowlist) where EITHER element type is a dynamic shape. `DynamicShape.MaterializeTypeMap` synthesizes a placeholder `TypeMap` with `IsDynamic = false`, `MemberList.None`, and no PropertyMaps — just enough metadata so `ExecutionPlanBuilder.BuildBaseBody` routes it through the existing v1 `BuildCollectionLambda` path. This is the `BuildCollectionDynamicTypeMap` factory.

2. **Per-element recursion.** `BuildCollectionLambda` emits `MappingInvoker.InvokeToList<TSrcEl, TDstEl>(registry, src)` (or array variant). Each element call re-enters `MappingInvoker.Invoke` for the per-element pair `(IDictionary<string, object>, MyPoco)` (or the reverse), which IS a regular XOR-dynamic pair — so stage 3 of `GetTypeMap` materializes a regular dict↔POCO dynamic TypeMap and the per-element codegen runs.

Same logic applies symmetrically to `List<MyPoco>` → `List<ExpandoObject>` (POCO→dict direction), `IEnumerable<...>` and `T[]` source/dest types, and arrays-of-dynamic.

> **Note:** The original §7.3 wording in this design's first revision claimed "the existing collection-element-mapping path runs … so this works naturally without special casing." That claim was wrong; without the outer-pair detection, the OUTER `GetTypeMap` lookup returns null and the call throws before any per-element work happens. The two-stage bridging above is what actually ships.

### 7.4 Profile-scoped value transformer composition (deferred to v3)

`MapperConfiguration` is sealed at startup. The dynamic TypeMap is materialized at runtime, after configuration is sealed. At materialization time the factory has access to:
- `_globalTransformers` (collection on `MapperConfiguration`)
- (Potentially) the active `MapperProfile` if any registered profile on the configuration is associated with the POCO side

**v1 decision:** dynamic TypeMaps are **global-scope only** — `OriginatingProfile = null`. Only `_globalTransformers` are composed into the dynamic TypeMap's PMs at seal time. Profile-scoped transformers (added via `MapperProfile.AddTransform<T>(...)`) DO NOT fire on dynamic TypeMaps' properties.

**Why not inherit profile scope from a "matching" profile registration?** Two reasons:
1. **Ambiguity.** A POCO may be registered in multiple profiles (`CreateMap<Customer, OrderDto>` in ProfileA, `CreateMap<Foo, Customer>` in ProfileB). Picking one profile's transformer scope is arbitrary.
2. **Convention discipline.** Dynamic TypeMaps are convention-only; profile-scoped configuration is explicit. Mixing the two surfaces hidden coupling.

`DynamicMapping_Integration_Tests` includes one test confirming `_globalTransformers` fire (e.g., a global `AddTransform<string>(s => s.Trim())` applied during dict→POCO conversion to a string property runs) and one test confirming a profile-scoped transformer does NOT fire on a dynamic TypeMap.

### 7.5 Explicit `CreateMap<MyPoco, IDictionary<string, object>>()` is a v1 limitation

As noted in §7.1: explicit registration produces a non-dynamic TypeMap, but the resulting codegen is broken because `BuildPocoLambda` expects member-init/property-set against the destination type. v3 follow-up (§1.2) lifts this: per-member overrides become first-class, and the materialized TypeMap is constructed differently (probably by the explicit-registration path setting `IsDynamic = true` and routing through `DynamicPlanBuilder` with user-provided `PropertyMap`s).

**v1 user guidance:** If you need per-member customization on dynamic mapping, write a transformer or hook on a POCO→POCO map and let the dynamic detector handle the dict↔POCO leg.

### 7.6 Misc edge cases

- **`null` source.** `mapper.Map<TDest>(null)` for any direction returns `default(TDest)` (existing v1 contract; no new behavior).
- **`null` value at a dict key (dict→POCO).** Reference type / `Nullable<T>` destination → assigned null. Non-nullable value type → `default(T)`.
- **Missing dict key (dict→POCO).** Fresh map → leave at `default(T)`. Update-in-place → preserved.
- **Excess dict keys (dict→POCO).** Silently ignored. No exception. Matches AutoMapper's behavior; matches user expectation for "JSON document has more fields than my DTO."
- **Type-mismatched dict value** (`dict["Age"] = "abc"`, dest `int`). `ConvertObjectTo<int>` → `Convert.ChangeType` → `FormatException` → wrapped in `AtlasMappingException` with key + source-runtime-type + dest-type info.
- **`null` POCO source (POCO→dict).** Returns `default(TDest)` (i.e., `null`). Existing v1 contract.
- **Threading.** `ConcurrentDictionary.GetOrAdd` semantics — multiple threads racing on the same closed pair → one materializes, others reuse. Compiled delegate is immutable post-`Seal()`. No new locking required.

---

## 8. Validation, Atlas.Projections, and Closed-Pair Precedence

### 8.1 Validator behavior

`AssertConfigurationIsValid()` walks `_typeMaps`. For each TypeMap, it checks:
- Unmapped destination members (per `MemberList.Source/Destination/None` setting)
- Type-assignment legality (via `IsAssignmentLegal`)
- Per-feature rules (NullSubstitute reachability, EnumSurface defined-check, etc.)

**For dynamic TypeMaps:** all checks are skipped via the same `IsDynamic` short-circuit OpenGenerics templates use:

```csharp
public void Walk()
{
    foreach (var typeMap in _registry.AllTypeMaps)
    {
        if (typeMap.IsOpenGenericTemplate) continue;     // existing #9
        if (typeMap.IsDynamic) continue;                 // NEW — this feature

        // ... existing per-TypeMap rule checks ...
    }
}
```

`AssertConfigurationIsValid()` with NO dynamic TypeMaps materialized passes (nothing to validate). After dynamic mappings have been performed (cache populated), the validator still passes — dynamic TypeMaps are skipped. `DynamicMapping_Validator_Tests` confirms both states.

### 8.2 Atlas.Projections rejection

`ProjectionPlanBuilder.Build` (or sibling `ProjectionCompatibility.IsTranslatable`) gets a single rejection check at the top:

```csharp
public LambdaExpression Build(TypeMap typeMap, IProjectionRegistry registry)
{
    if (typeMap.IsDynamic)
        throw new AtlasProjectionException(
            $"ProjectTo<{typeMap.DestinationType.Name}>() is not supported for dynamic-shape mappings " +
            $"({typeMap.SourceType} → {typeMap.DestinationType}). LINQ providers cannot translate " +
            $"runtime dictionary key lookups against arbitrary keys at the SQL level.");

    // ... existing projection-build logic ...
}
```

Same single-insertion-point pattern as Hooks (#5) and ConditionalMapping rejection. `Atlas.Projections` needs zero other production-code changes; one consumer test file (`DynamicMapping_Projection_Rejection_Tests`) confirms the rejection.

### 8.3 Closed-pair precedence (recap from §7.1)

The detector runs in stage 3 of `MapperRegistry.GetTypeMap`, AFTER the closed-pair cache (stage 1). Explicit registrations always win. The v1 limitation noted in §7.5 (explicit `CreateMap<MyPoco, IDictionary<string, object>>` produces broken codegen) is documented but not blocked at config time — the user will see a broken-codegen exception at first call.

---

## 9. Test Plan

**Test count target:** ~50–60 tests new (aligns with the +24-60 net per-feature baseline; brings the v2 total of 489 to ~540 after merge).

### 9.1 File layout

```
tests/Atlas.Tests/Mapping/
  ├── DynamicMapping_Routing_Tests.cs            (~8 tests)
  ├── DynamicMapping_DictToPoco_Tests.cs         (~22 tests)
  ├── DynamicMapping_PocoToDict_Tests.cs         (~16 tests)
  └── DynamicMapping_Integration_Tests.cs        (~10 tests)
tests/Atlas.Tests/Validation/
  └── DynamicMapping_Validator_Tests.cs          (~3 tests)
tests/Atlas.Projections.Tests/
  └── DynamicMapping_Projection_Rejection_Tests.cs (~2 tests)
```

### 9.2 `DynamicMapping_Routing_Tests` (~8)

Gating predicate + cache integration:

- `IsDynamicShape` returns `true` for each of `IDictionary<string, object>`, `ExpandoObject`, `Dictionary<string, object>`; returns `false` for `Dictionary<string, int>`, `IDictionary<int, object>`, `ConcurrentDictionary<string, object>`, `List<KeyValuePair<...>>`, plain `MyPoco`.
- `IsDynamicPair` true when XOR holds (one side dynamic); false when both dynamic; false when neither dynamic.
- `MapperRegistry.GetTypeMap` materializes a dynamic TypeMap on first call for `(Dict, Poco)`; returns the same instance on second call (cache hit).
- Same as above for `(Poco, Dict)` direction.
- Closed-pair-takes-precedence: explicit `CreateMap<MyPoco, IDictionary<string, object>>()` causes the dynamic detector to NOT fire (registered TypeMap returned). v1 limitation noted in test annotation.
- Dynamic self-pair `(Dictionary<string, object>, Dictionary<string, object>)` does NOT trigger the dynamic detector (XOR = false); falls through to v1 dict-element-mapping path.
- Dynamic self-pair `(ExpandoObject, ExpandoObject)` requires explicit `CreateMap` to materialize; detector does not fire.
- Mixed-shape `(ExpandoObject, Dictionary<string, object>)` requires explicit `CreateMap` to materialize; detector does not fire.

### 9.3 `DynamicMapping_DictToPoco_Tests` (~22)

- Primitive direct: `int`, `string`, `bool`, `Guid`, `DateTime`, `decimal`, `double`, `enum` — each from a dict with the matching object type.
- Numeric widening: `long → int`, `int → long`, `double → decimal`, `int → double`.
- Numeric narrowing: `long → int` where the long fits → success.
- Nullable: `Dictionary<string, object> { ["Age"] = null }` → `int? Age` = null.
- Nullable: same → `int Age` (non-nullable) = `0` (default fallback).
- String parsing: `"550e8400..." → Guid`, `"2026-05-06" → DateTime`, `"42" → int` (via `Convert.ChangeType`), `"3.14" → double`.
- Nested POCO via top-level nested-dict key.
- Nested POCO via top-level key holding a typed POCO instance directly (the `else if (v is Customer typed)` branch).
- Nested POCO via dot-notation prefix scan when top-level key absent.
- Mixed: top-level key present AND dot-notation siblings — top-level wins, siblings ignored.
- Deep dot-notation (`"Customer.Address.City"`) → nested map → nested map (recursion).
- Collection: `List<int>` from `IEnumerable<object>` source value.
- Collection: `List<MyPoco>` from `List<IDictionary<string, object>>` source value.
- Collection: `int[]` from `IEnumerable<long>` source value (element widening).
- Update-in-place preserves existing destination value when key missing.
- Update-in-place preserves nested existing object when nested key missing (`mapper.Map(dict, existingPoco)` with `dict["Customer"]` absent leaves `existingPoco.Customer` intact).
- Excess dict keys silently ignored (no exception; corresponding properties left at default for fresh map).
- Type-mismatched value throws `AtlasMappingException` with key + source-runtime-type + dest-type in message.
- Constructor-using POCO (record): all ctor params populated from dict keys.
- `required` property: populated from matching dict key.
- `required` property: throws `AtlasMappingException("X is required but missing")` when key absent.
- Read-only property (`get` only) skipped on dict→POCO (no setter).

### 9.4 `DynamicMapping_PocoToDict_Tests` (~16)

- Primitive emit: each primitive type → matching dict value (boxed).
- Each destination shape (`ExpandoObject`, `Dictionary<string, object>`, `IDictionary<string, object>`) returns the correct concrete type — `Assert.IsType<ExpandoObject>(...)` etc.
- `null` property → null dict value (key written with `null`, not absent).
- Nested POCO → nested `ExpandoObject` (`Assert.IsType<ExpandoObject>(dict["Customer"])`), regardless of outer destination shape.
- Nested POCO that is null → null dict value at key.
- Collection of primitives → `List<object?>` with boxed elements.
- Collection of POCOs → `List<object?>` where each element is an `ExpandoObject`.
- Typed-POCO dictionary (`Dictionary<string, OrderLine>`) → `IDictionary<string, object>` with each value an `ExpandoObject`.
- Typed-POCO dictionary with non-string key (`Dictionary<int, OrderLine>`) → keys converted via `ToString()`.
- Enum emits as underlying integer.
- Read-only property emitted (getter-only is fine on emit side).
- Write-only property NOT emitted (no getter to read).
- Update-in-place: existing dict has unrelated key → preserved; matching key → overwritten.
- Update-in-place: existing dict has nested ExpandoObject → REPLACED (not deep-merged).
- `null` POCO source → `default(TDest)` (matches v1 contract — null returned).
- Round-trip: `OrderDto → ExpandoObject → OrderDto` produces equivalent POCO (deep equality via property comparison).

### 9.5 `DynamicMapping_Integration_Tests` (~10)

- `NameComparison` case-sensitive (default): `dict["age"]` does NOT populate `dst.Age` (key miss; left at default).
- `WithCaseInsensitiveMatching()`: same dict population works.
- Dot-notation prefix scan respects `NameComparison` (case-sensitive vs insensitive).
- Global-scope `AddTransform<string>(s => s.Trim())` applied during dict→POCO conversion to a string-typed destination property RUNS.
- Profile-scope `AddTransform<string>(s => s.Trim())` does NOT fire on a dynamic TypeMap (per §7.4).
- Threading: 16 concurrent `Map<MyPoco>(dict)` calls produce 16 successful results AND only one materialization (assertable via `_typeMaps.Count` before/after).
- Reverse direction also works concurrently: `List<MyPoco>` → `List<ExpandoObject>`.
- Collection-of-dynamic recursion: `List<IDictionary<string, object>>` → `List<MyPoco>` works via outer collection element-map + recursive dynamic detector.
- Inheritance is inert: a `MapperProfile` with `Include<Base, Derived>` and a separate dynamic call against `Derived` doesn't blow up; dynamic detector materializes `Derived` independently.
- Non-public properties NOT emitted (private setter on POCO emit side; private getter on POCO read side).

### 9.6 `DynamicMapping_Validator_Tests` (~3)

- `AssertConfigurationIsValid()` with NO dynamic TypeMaps materialized passes (the detector is lazy; nothing to validate).
- `AssertConfigurationIsValid()` AFTER one dynamic mapping has been performed (cache populated) STILL passes — dynamic TypeMaps skipped via `IsDynamic` flag.
- Validator does NOT throw "unmapped destination member" for dynamic TypeMaps even when the POCO has destination members the validator would normally consider unmapped.

### 9.7 `DynamicMapping_Projection_Rejection_Tests` (~2)

- `ProjectTo<MyPoco>(queryable<IDictionary<string, object>>)` throws `AtlasProjectionException` with the dynamic-shape pair in the message.
- Non-dynamic projections continue to work (regression check that the projection-build-time gate is correctly placed).

### 9.8 TDD discipline

Each test file is written failing first, with a single source-code change per task to green it (per the established workflow's `+22 / +33 / +24 / +25-60` per-feature task granularity). Estimated 8–10 plan tasks; the writing-plans stage refines the exact decomposition.

### 9.9 Coverage targets

Line ≥ 90%, branch ≥ 80% on the changed files (`DynamicShape.cs`, `DynamicPlanBuilder.cs`, the `MapperRegistry.GetTypeMap` extension, the `TypeMap.IsDynamic` field plumbing, the `PropertyMap.DynamicKey` field plumbing, the `MappingInvoker` runtime helpers). Has held on every prior feature.

---

## 10. README Updates

A new section in the project README, under the "Features" list, between the OpenGenerics section (#9) and any future v2 entries:

```markdown
## Dynamic / dictionary mapping

Atlas maps between strongly-typed POCOs and three recognized dynamic shapes
without any registration:

- `IDictionary<string, object>`
- `ExpandoObject`
- `Dictionary<string, object>`

Use cases: JSON documents, MongoDB BSON, configuration-shaped inputs.

```csharp
// Reading: dict → POCO
var dict = new Dictionary<string, object>
{
    ["OrderId"] = 42L,                              // long → int via NumericConversions
    ["CustomerName"] = "Alice",
    ["Customer.Email"] = "alice@example.com",       // dot-notation populates nested
    ["Lines"] = new[] { new Dictionary<string, object> { ["Sku"] = "X" } }
};
var order = mapper.Map<OrderDto>(dict);             // no CreateMap needed

// Writing: POCO → dict (any of the three shapes)
ExpandoObject e = mapper.Map<ExpandoObject>(order);
Dictionary<string, object> d = mapper.Map<Dictionary<string, object>>(order);

// dynamic-friendly output
dynamic json = mapper.Map<ExpandoObject>(order);
var name = json.CustomerName;
```

Behavior summary:

- Convention-only — no `CreateMap` registration.
- Honors `WithCaseInsensitiveMatching()` for both top-level key match and dot-notation prefix scan.
- Missing keys leave the destination at `default(T)` for fresh `Map`; preserve existing for update-in-place `Map(src, existing)`.
- Excess dict keys silently ignored.
- Nested POCOs read from nested-dict values OR from dot-notation keys (`"Customer.Email"`); top-level wins.
- Nested POCOs emit as nested `ExpandoObject` regardless of outer destination shape.
- Enums emit as underlying integer; read via `Convert.ChangeType`.
- `Atlas.Projections` rejects dynamic-shape mappings (LINQ providers can't translate
  arbitrary key lookups).

Limitations (v1):

- Only the three named shapes are recognized. `Dictionary<string, string>`,
  `ConcurrentDictionary<string, object>`, `IDynamicMetaObjectProvider` subtypes
  beyond `ExpandoObject` are out of scope.
- No per-member fluent customization (`Ignore`/`MapFrom`/`Condition`/`NullSubstitute`)
  on dynamic mappings — register an explicit `CreateMap` for those scenarios
  (note: explicit registration of dynamic-shape pairs has its own v1 limitations).
- Dict keys are emitted verbatim from property names — no naming-policy translation.
- Profile-scoped value transformers do NOT fire on dynamic TypeMaps; only
  global-scope transformers compose.
- Self-pair round-trips (`ExpandoObject → ExpandoObject`, etc.) require an
  explicit `CreateMap` registration.
```

---

## 11. Risks & Implementer Notes

### 11.1 Risk: `Map<object>` ambiguity

Users writing `dynamic d = mapper.Map<dynamic>(somePoco)` will receive an `object` reference whose runtime type depends on what `MapperRegistry.GetTypeMap((MyPoco, object))` returns. v1 leaves `Map<object>` semantics unchanged. **Mitigation:** README example shows the explicit `Map<ExpandoObject>` pattern. Implementation-time XML doc-comment on `IMapper.Map<TDest>` mentions the recommendation. Not a bug — a discoverability issue.

### 11.2 Risk: Pre-existing `IsDictionary` predicate's narrow typedef-equality match

The current `ExecutionPlanBuilder.IsDictionary` returns true only for `Dictionary<,>` typedef-equality. Self-pair routing for `(ExpandoObject, ExpandoObject)` and `(IDictionary<string, object>, IDictionary<string, object>)` requires an explicit branch — see §7.2's `IsDynamicSelfPair` predicate. **Mitigation:** §7.2 documents the addition; test coverage in `DynamicMapping_Routing_Tests` exercises the routing.

### 11.3 Risk: Per-call closure allocation for `MappingContext` in nested recursion

Each nested POCO emit calls `ctx.Mapper.Map<Customer, ExpandoObject>(src.Customer)`. In a deep-nested object (e.g., 10-level config tree), this is 10 dictionary lookups against `_typeMaps`. The lookups are O(1) but the closure allocation per `Map` call exists. **Mitigation:** acceptable for v1 — same allocation budget Atlas's nested-POCO maps already pay. A future optimization could inline the recursion via an `Expression.Invoke` against the cached delegate; deferred.

### 11.4 Risk: `TransformerResolver` null-`DestinationProperty` guard

`TransformerResolver.Resolve` reads `pm.DestinationProperty` when matching transformers to PMs. POCO→dict synthesizes PMs with `DestinationProperty == null`. **Mitigation:** plan task explicitly verifies `TransformerResolver` handles null `DestinationProperty`. If a guard is missing, add one (one-line: `if (pm.DestinationProperty is null) continue;` in the resolver loop). One regression test in `DynamicMapping_Integration_Tests` covers the guard.

### 11.5 Risk: Bug-4 lesson application — auditing `PropertyMap` consumers

The new `DynamicKey` field is consumed only by `DynamicPlanBuilder`. All other consumers (`ExecutionPlanBuilder.BuildPocoLambda`, `ProjectionPlanBuilder.BuildBody`, `InheritanceMerger.CopyConfig`, `ConfigurationValidator.Walk`) are short-circuited via `typeMap.IsDynamic` BEFORE walking PMs. **Mitigation:** §4.6's audit table is the implementation-time checklist. One test per consumer confirms the short-circuit (covered indirectly by `DynamicMapping_Validator_Tests` and `DynamicMapping_Projection_Rejection_Tests`).

### 11.6 Risk: Bug-5 lesson application — `OriginatingProfile` propagation

Dynamic TypeMaps don't have a sibling/derived/reverse pair. `OriginatingProfile = null` is set explicitly. There is no propagation work to do. The "Dynamic TypeMaps are global-scope only" rule (§7.4) makes the propagation question vacuous.

### 11.7 Risk: Pseudocode-trace discipline (per the user's standing self-correction)

Concrete worked examples to walk through during plan signing-off:

1. **3-level nested dot-notation.** `dict = { "A.B.C": 42 }` → `Root` with `Root.A` (POCO) → `A.B` (POCO) → `int B.C`. Walk through the codegen: outer materialization scans for `"A."` prefix → synthesizes `{ "B.C": 42 }` → recursive map for type `A` → no `"B"` key → scan for `"B."` prefix → synthesizes `{ "C": 42 }` → recursive map for type `B` → key `"C"` found → set `B.C = 42`.
2. **Mixed top-level + dot-notation.** `dict = { "Customer": { "Name": "alice" }, "Customer.Email": "alice@x" }` → top-level wins; the dot-notation sibling is ignored. The recursive map for `Customer` only sees `{ "Name": "alice" }`. `dst.Customer.Email` left at default. Test in `DynamicMapping_DictToPoco_Tests`.
3. **Numeric widening with `Nullable<T>` destination.** `dict = { "Age": 42L }` → `int? Age`. `ConvertObjectTo<int?>(42L)` → `value is int? direct` is FALSE → `TryNumericOrConvertible(42L, typeof(long), typeof(int?))` → asymmetric-nullable widening branch (#8 follow-on) → `(int?)(int)42L = 42`. Bug-6 lesson applied: the asymmetric-nullable branches added in NullSubstitution's scope-expansion ARE consumed here.
4. **Enum source, integer destination.** `dict = { "Status": "Active" }` → `int Status`. `ConvertObjectTo<int>("Active")` → string parsing tries `Convert.ChangeType("Active", typeof(int))` → `FormatException`. The codegen does NOT auto-resolve "Active" to its enum-integer value — that would require knowing about a destination-side enum type, which is absent in this case (dest is `int`). User must either map to `Status status` (enum-typed dest, which `Convert.ChangeType` handles via `Enum.Parse`) or pass `dict = { "Status": 1 }`.
5. **Cross-task dependency.** Plan task adds `TypeMap.IsDynamic` field; subsequent task adds `MapperRegistry.GetTypeMap` extension that USES the field. Cross-task stub-and-replace pattern (per the OpenGenerics workflow learning): Task N adds `IsDynamic` field with a TEMPORARY stub if the field's writer doesn't exist yet; Task N+1 replaces the stub with the real materializer. Plan stage to confirm the dependency order.

### 11.8 Implementation-stage ordering hint

A reasonable task decomposition (subject to plan-stage refinement):

1. `DynamicShape.IsDynamicShape` and `IsDynamicPair` predicates + tests.
2. `TypeMap.IsDynamic` and `PropertyMap.DynamicKey` fields + plumbing through `Seal()`.
3. `MapperRegistry.GetTypeMap` stage-3 detection + `DynamicShape.MaterializeTypeMap` factory (initial: empty PMs, just to validate caching).
4. `DynamicPlanBuilder.BuildDictToPocoLambda` for primitives + `MappingInvoker.ConvertObjectTo<T>` runtime helper.
5. Dict→POCO nested POCO + dot-notation fallback + `MappingInvoker.ScanPrefix`.
6. Dict→POCO collections + `MappingInvoker.ConvertObjectToList<T>`.
7. Dict→POCO ctor-using POCOs (records, primary ctors, required props).
8. `DynamicPlanBuilder.BuildPocoToDictLambda` for primitives + `MappingInvoker.SerializeValue`.
9. POCO→dict nested POCO + collections + typed-POCO-dictionary recursion.
10. `ExecutionPlanBuilder.IsDynamicSelfPair` routing + `BuildDynamicVerbatimCopyLambda`.
11. `ConfigurationValidator` skip + `ProjectionPlanBuilder` rejection + `Atlas.Projections.Tests` integration test.
12. README updates + holistic review prep.

---

## 12. Final Feature Summary

Atlas v2 #10 — Dynamic / `ExpandoObject` / `Dictionary<string, object>` mapping:

- **Convention-only.** Zero new fluent surface. `mapper.Map<MyPoco>(dict)` and `mapper.Map<ExpandoObject>(poco)` work without `CreateMap` registration.
- **Three recognized dynamic shapes:** `IDictionary<string, object>`, `ExpandoObject`, `Dictionary<string, object>`. Exact runtime-type match.
- **Lazy materialization.** Single insertion point in `MapperRegistry.GetTypeMap` after closed-pair cache and open-generic template scan. Mirrors OpenGenerics' architecture.
- **Bidirectional, asymmetric codegen.** Dict→POCO uses Atlas's existing `ConvertOrMap` pipeline through a synthesized PropertyMap-keyed-by-dict-key shape. POCO→dict iterates source POCO public properties, emits nested `ExpandoObject` for nested POCOs, `List<object?>` for collections.
- **Dot-notation read-side fallback.** `dict["Customer.Email"]` populates `dst.Customer.Email` when the top-level `"Customer"` key is absent. Top-level wins when both present.
- **Closed-pair-takes-precedence.** Explicit `CreateMap` registration wins over the dynamic detector. (v1 limitation: explicit registration produces broken codegen for dynamic-shape pairs — documented as an interim constraint.)
- **`Atlas.Projections` rejects.** Single rejection check at projection-build time; LINQ providers can't translate runtime dictionary key lookups.
- **No validator surface.** Dynamic TypeMaps are skipped by `AssertConfigurationIsValid()` via `IsDynamic` short-circuit.
- **Global-scope value transformers fire.** Profile-scope transformers do NOT — dynamic TypeMaps are global-scope only. Per-member fluent options (Hooks, Conditional, NullSubstitute, etc.) are inert.
- **~50–60 tests new.** Across 6 test files. Coverage targets: line ≥ 90%, branch ≥ 80% on changed assemblies.
- **8–10 plan tasks.** Plan-stage refinement.

The next step is `superpowers:writing-plans` against this design to produce `docs/Atlas-Plan-DynamicMapping.md`. Both docs commit directly to `main` per the established v2 workflow rhythm; implementation goes through a `feat/dynamic-mapping` branch and a single PR.
