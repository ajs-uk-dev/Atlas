# Atlas v2 — Attribute-Based Configuration

**Status:** Approved design (2026-05-07).
**Implementation target:** v2 feature group #12 (post-MVP, post-ReferenceHandling).
**Predecessor designs:** `docs/Atlas-Design-ReferenceHandling.md` (per-typemap fluent flag with bidirectional propagation; same `_linkedForwardTypeMap` machinery the attribute scanner inherits transparently), `docs/Atlas-Design-OpenGenerics.md` (single-insertion-point translation pattern; the attribute scanner mirrors this by translating attributes into existing fluent calls), `docs/Atlas-Design-NullSubstitution.md` (constant-form `NullSubstitute<T>(T constant)` that `[NullSubstitute(value)]` translates to), `docs/Atlas-Design.md` (v1 baseline — `MapperProfile`, `ProfileScanner`, `MapperConfigurationExpression.AddMaps`, `IMappingExpression`).

This document specifies Atlas's twelfth post-MVP feature: **attribute-based class declarations** as a parallel front-end to the fluent API. The attribute scanner discovers types decorated with `[AutoMap(typeof(SourceType))]` during the same assembly scan that finds `MapperProfile` subclasses, and translates the attributes into existing fluent calls (`cfg.CreateMap<S,D>()`, `.ForMember(...)`, `.ReverseMap()`, `.PreserveReferences()`). The entire downstream pipeline — validation, propagation, projection, codegen — is unchanged.

---

## 1. Goals & Non-Goals

### 1.1 Goals

1. **Declarative parity for the simple cases.** A user with a flat DTO that needs only `[Ignore]` + `[SourceMember]` + auto-reverse can declare the entire mapping in attributes without writing a `MapperProfile`. The attribute set covers the AutoMapper canonical `[AutoMap]` example unchanged.

2. **Coexistence with fluent profiles.** Attribute and fluent registrations work together in the same configuration; profiles for complex maps that need lambdas, attributes for simple ones. The two front-ends produce identical `TypeMap` instances downstream — every consumer (validator, projection, propagation, codegen) is oblivious to the registration origin.

3. **AutoMapper UX alignment.** Attribute names, semantics, discovery shape, and class-level placement match what AutoMapper users already know — `[AutoMap(typeof(Source))]` on the destination DTO; member-level `[Ignore]`, `[SourceMember]`, `[NullSubstitute]` on destination properties.

4. **No new pipeline complexity.** Attribute scanning is a thin translator; it does not introduce a parallel propagation, validation, or codegen path. Every flag (Ignore, NullSubstitute, ReverseMap, PreserveReferences) flows through the *same* `MappingExpression`/`PropertyMap` machinery the fluent API uses. Bug-5 lesson applied: no new TypeMap-allocating consumer to audit.

5. **Loud failure on misconfiguration.** Duplicate registration (fluent + attribute on the same pair, or two fluent calls for the same pair), unresolvable `[SourceMember]` paths, `[NullSubstitute]` type mismatches, and `[AutoMap]` source/destination mismatches all surface at config-build time with structured `AtlasConfigurationException` messages naming both registration origins where applicable.

6. **Discovery integrated into existing entry points.** No new public method on `MapperConfigurationExpression` or `AtlasServiceCollectionExtensions`. `cfg.AddMaps(asm)` and `services.AddAtlas(asm)` discover both `MapperProfile` subclasses AND `[AutoMap]`-decorated types in one scan. Profiles register first, attributes register second — natural conflict ordering.

7. **Bidirectional propagation inherited transparently.** `[AutoMap(ReverseMap = true, PreserveReferences = true)]` translates to `.PreserveReferences()` then `.ReverseMap()` on the fluent expression. The bidirectional propagation fix from PR #11 (`_linkedForwardTypeMap` back-pointer) handles either calling order; the attribute scanner emits a fixed order but the fluent layer is order-insensitive.

### 1.2 Non-Goals (deferred to v3)

- **Lambda-shaped attributes.** Attributes cannot accept `Expression<Func<,>>` arguments (CLR limitation). Anything currently shaped as a lambda in fluent (`MapFrom(expr)`, `Condition(predicate)`, `PreCondition(predicate)`, `AddTransform(expr)`, `BeforeMap(action)` lambda overload, `AfterMap(action)` lambda overload, `ConvertUsing(func)`, `NullSubstitute(factory)`) stays fluent-only.

- **Tier-3 / Tier-4 attributes.** No `[ValueConverter(typeof(...))]` (member-level), no class-level `[ConvertUsing(typeof(...))]`, no `[BeforeMap(typeof(...))]` / `[AfterMap(typeof(...))]` typed-action attributes. Deferred to a future doc; the existing fluent surface covers these via `cfg.CreateMap<S,D>().ConvertUsing<T>()` / `.BeforeMap<T>()`.

- **Source-side attributes.** No `[AutoMapTo(typeof(TDest))]` on source classes. Destination-only — domain types stay attribute-free, matching the AutoMapper canonical placement.

- **Attribute-driven additive merging.** `cfg.CreateMap<S,D>()` plus `[AutoMap(typeof(S))]` on `D` is an error, not a merge. Pick one source of truth per pair. Future v3 may relax with explicit precedence rules if user demand surfaces.

- **Open-generic attribute support.** `[AutoMap(typeof(Source<>))]` on `Dest<T>` rejected at config-build with a clear error. Open generics already have a fluent registration shape (`cfg.CreateMap(typeof(Source<>), typeof(Dest<>))`).

- **Attribute-driven enum value overrides.** No `[MapValue(SourceEnum.X, DestEnum.Y)]`, no `[MapByName]`, no `[Ignore(SourceEnum.X)]`. Enum surface stays fluent.

- **Assembly-level `[AutoMap]`.** No `[assembly: AutoMap(typeof(Order), typeof(OrderDto))]`. Class-level only.

- **Profile-scope value transformer composition for attribute-declared TypeMaps.** Same limitation as DynamicMapping (#10) and OpenGenerics (#9) for profile-scope transformers — attribute-declared TypeMaps have `OriginatingProfile = null` because they don't come from a profile, so profile-scope transformers don't fire on them. Documented in §6 and §11.

- **Inheritance via attributes.** No `[Include(typeof(DerivedSrc), typeof(DerivedDst))]` / `[IncludeBase(typeof(BaseSrc), typeof(BaseDst))]`. Both require compile-time generics. Mixed mode works — base attribute + fluent derived `IncludeBase` — but pure-attribute inheritance is deferred.

- **Member-level attribute on fields.** Attributes target properties only; field-typed destination members are not supported (matches v1 convention engine which scans properties only — see `feedback_pseudocode_concrete_trace.md` Bug-3 observation).

---

## 2. Architecture Overview

### 2.1 Translation layer; existing pipeline unchanged

```
cfg.AddMaps(asm) / services.AddAtlas(asm)
   │
   ▼
ProfileScanner.Discover(asm)         ── existing, unchanged
   ├── enumerate MapperProfile subclasses
   └── instantiate + invoke profile.Configure() → fluent CreateMap calls
   │
AttributeScanner.Discover(asm, cfg)   ── NEW
   ├── enumerate types with [AutoMap]
   └── for each decorated type T_dst:
        │
        ├── Validate: not enum, not abstract, not interface, not open generic, source not dynamic shape...
        │
        ├── cfg.CreateMap<T_src, T_dst>(memberList)              // via MakeGenericMethod
        │      ◄─── routes through existing fluent path
        │
        ├── for each property carrying [Ignore]/[SourceMember]/[NullSubstitute]:
        │      expr.ForMember<TMember>(d => d.X, opt => { ... })
        │             ◄─── routes through existing fluent path
        │
        ├── if (autoMap.PreserveReferences)
        │      expr.PreserveReferences()
        │
        └── if (autoMap.ReverseMap)
               expr.ReverseMap()                                 // bidirectional propagation handles either order
```

The scanner's only API surface against the rest of Atlas is: invoking `cfg.CreateMap<S,D>(MemberList)` and the four fluent methods on the returned `IMappingExpression<S,D>` — `ForMember<TMember>`, `PreserveReferences()`, `ReverseMap(MemberList)`, plus the per-member methods `Ignore()`, `MapFrom<T>(Expression<...>)`, `NullSubstitute<T>(T)` on the inner `IMemberConfigurationExpression`. Everything downstream (the registry, validator, projection compatibility, codegen) is reached via these fluent calls and treats the resulting TypeMap as identical to a fluent-declared one.

**Single insertion point:** the only modification to existing code is one line appended to `MapperConfigurationExpression.AddMaps(params Assembly[])`:

```csharp
foreach (var asm in assemblies)
{
    AttributeScanner.Discover(asm, this);
}
```

— inserted after the existing `ProfileScanner.Discover` loop. The DI extension package (`Atlas.Extensions.DependencyInjection`) requires zero changes because `services.AddAtlas` calls `cfg.AddMaps` internally.

### 2.2 The four attribute types as the entire public-surface delta

```
┌─ Class-level (one allowed; non-inherited) ──────────────────────┐
│ [AutoMap(typeof(SourceType))]                                   │
│   .MemberList         (default Destination)                     │
│   .ReverseMap         (default false)                           │
│   .PreserveReferences (default false)                           │
└─────────────────────────────────────────────────────────────────┘
           │
           ▼ (decorates destination class)
┌─ Member-level (each non-multiple) ──────────────────────────────┐
│ [Ignore]                       — exclude member from mapping    │
│ [SourceMember(name)]           — name-based redirect (dotted    │
│                                  paths supported)               │
│ [NullSubstitute(constant)]     — constant null fallback         │
└─────────────────────────────────────────────────────────────────┘
```

No new methods on `IMapper`, `IMappingExpression`, `IMemberConfigurationExpression`, `MapperProfile`, `MapperConfigurationExpression`, or `AtlasServiceCollectionExtensions`.

### 2.3 Discovery + registration ordering

The scanner runs *after* profile discovery. Concrete order inside `cfg.AddMaps(params Assembly[])`:

1. For each assembly: `ProfileScanner.Discover(asm)` enumerates `MapperProfile` subclasses, instantiates them, and invokes `profile.Configure()` which makes fluent `cfg.CreateMap<>()` calls.
2. For each assembly: `AttributeScanner.Discover(asm, this)` enumerates `[AutoMap]`-decorated types and routes each through `cfg.CreateMap<>()` via `MakeGenericMethod`.

Why profiles first: the natural collision case is "I declared this pair in a profile AND I forgot to remove the attribute." Profile-first ordering means the profile `CreateMap` lands first; the scanner's second `CreateMap` for the same pair triggers the duplicate-pair rule (§4) with the profile listed as `existing.RegistrationOrigin` and the attribute listed as `newTm.RegistrationOrigin` — matching the user's mental model of "the profile is the explicit declaration; the attribute is the surprise."

### 2.4 Translate-to-fluent: what's gained, what's paid

**Gained** — every existing v2 feature works on attribute-declared TypeMaps without modification:

| Feature | Mechanism |
|---|---|
| Inheritance merging (#2) | Attribute-declared base TypeMap is a normal `TypeMap`; `InheritanceMerger.MergeBaseConfig` finds it. |
| ReverseMap auto-inversion (#4) | `[AutoMap(ReverseMap = true)]` calls `.ReverseMap()` which routes through the existing flip logic. |
| Hooks (#5) | Mixed-mode only: attribute pair + profile-attached `BeforeMap<T>()` works after a redesign that resolves Q4. v1 limitation: hooks require a fluent `CreateMap`. |
| Value transformers (#6) | Global-scope works; profile-scope does not (attribute TypeMap has `OriginatingProfile = null`). |
| Conditional/null substitute (#7, #8) | Constant-form `[NullSubstitute(value)]` ✓; lambda forms fluent-only. |
| Open generics (#9) | Mutually exclusive — attributes rejected on open generics. |
| Dynamic mapping (#10) | Mutually exclusive — `[AutoMap]` rejected when source is a dynamic shape. |
| Reference handling (#11) | `[AutoMap(PreserveReferences = true)]` ✓; bidirectional propagation handled by existing `_linkedForwardTypeMap`. |
| Atlas.Projections (#1) | Attribute-declared TypeMap projects identically to fluent-declared. `[Ignore]` excluded; `[SourceMember]` redirects; `[NullSubstitute]` translates to SQL `COALESCE`. |

**Paid** — three observable changes vs pre-#12 behavior:

1. **Universal duplicate-pair rule (§4).** Two `CreateMap<S,D>()` calls for the same pair now throw at registration, regardless of origin. Previously, non-reverse duplicates silently last-write-wins.

2. **Attribute scan startup cost.** For each `[AutoMap]`-decorated type: ~1 `Type.GetCustomAttribute` call, ~2 `MakeGenericMethod` invocations, ~1 `Expression.Compile()` per member-attribute-bearing property (the `ForMember` callback). Negligible against existing v1 startup costs (the per-typemap `Expression.Compile` for the mapping body itself dominates).

3. **One nullable-check per nested call.** The cost from PR #11's `MappingContext?` parameter is unchanged; `[AutoMap(PreserveReferences = true)]` exercises the same fast/slow paths.

### 2.5 Why translate-to-fluent rather than direct TypeMap synthesis

The attribute scanner could allocate `TypeMap` and `PropertyMap` instances directly, stamping the attribute config onto them. It would avoid the reflection dance with `MakeGenericMethod` and `Expression.Compile` for the `ForMember` callback. **The design rejects this approach.** Reasoning, drawn from prior bugs:

- **Bug-5 lesson** (`feedback_pseudocode_concrete_trace.md`): when scope-identifying TypeMap metadata is added, every TypeMap-allocating consumer must be audited and patched. The attribute scanner as a TypeMap-allocator would be a fourth such site (alongside `MappingExpression`, `OpenGenericTypeMap.Materialize`, dynamic-shape inference). Routing through `cfg.CreateMap<>()` keeps the count at three — every consumer downstream of `CreateMap` continues to see one canonical TypeMap shape.
- **Bug-8 lesson** (PR #11 holistic): bidirectional propagation has to fire regardless of fluent-call ordering. Attribute scanner emits a fixed order (`PreserveReferences()` then `ReverseMap()`) but the existing bidirectional-propagation machinery handles either ordering — duplicating the propagation in the scanner risks divergence as future flags are added.
- **Single source of truth for validation.** Validator rules (e.g., `ValidatePreserveReferences` from #11) run after `RegisterTypeMap`. The fluent path already invokes them; bypassing the fluent path would skip them.
- **Reflection cost is a startup-only concern.** §11 discusses caching strategies if measured needs justify them; no evidence from #11's 634-test baseline suggests startup is a real bottleneck.

The architectural consequence: the implementer must NOT introduce a parallel TypeMap-construction path inside `AttributeScanner`. Every TypeMap mutation must go through `IMappingExpression<,>`.

---

## 3. Public API Surface

Five new public types in the `Atlas` namespace; no changes to existing public types.

```csharp
namespace Atlas;

/// <summary>
/// Class-level attribute declaring that the decorated class is the destination type
/// of a mapping from <see cref="SourceType"/>. Equivalent to a fluent
/// <c>cfg.CreateMap&lt;TSource, TDestination&gt;(MemberList)</c> registration.
/// </summary>
/// <remarks>
/// Discovered by <see cref="MapperConfigurationExpression.AddMaps(System.Reflection.Assembly[])"/>
/// and <see cref="Microsoft.Extensions.DependencyInjection.AtlasServiceCollectionExtensions"/>'s
/// <c>AddAtlas</c> overloads during the same scan that finds <see cref="MapperProfile"/>
/// subclasses. Member-level customization comes from <see cref="IgnoreAttribute"/>,
/// <see cref="SourceMemberAttribute"/>, and <see cref="NullSubstituteAttribute"/>
/// on the decorated class's properties.
///
/// Configuring the same (TSource, TDestination) pair both via attributes AND via fluent
/// <c>CreateMap</c> throws <see cref="AtlasConfigurationException"/> at registration time —
/// the duplicate-pair rule applies universally regardless of registration origin.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class AutoMapAttribute : Attribute
{
    public AutoMapAttribute(Type sourceType)
    {
        ArgumentNullException.ThrowIfNull(sourceType);
        SourceType = sourceType;
    }

    /// <summary>The source type for this mapping (positional argument).</summary>
    public Type SourceType { get; }

    /// <summary>
    /// Validation policy for this mapping. Defaults to <see cref="MemberList.Destination"/> —
    /// the same default fluent <c>CreateMap</c> uses.
    /// </summary>
    public MemberList MemberList { get; set; } = MemberList.Destination;

    /// <summary>
    /// If <c>true</c>, the scanner additionally calls <c>.ReverseMap()</c> on the
    /// translated registration, producing a (TDestination, TSource) typemap with the same
    /// auto-inverted conventions and source-side flattening as the fluent equivalent.
    /// Member-level attribute config (Ignore, SourceMember, NullSubstitute) describes the
    /// FORWARD direction only and does not auto-flip — see
    /// <see cref="Atlas.Configuration.IMappingExpression{TSource,TDestination}.ReverseMap(MemberList)"/>.
    /// </summary>
    public bool ReverseMap { get; set; }

    /// <summary>
    /// If <c>true</c>, the scanner calls <c>.PreserveReferences()</c> on the translated
    /// registration. When <see cref="ReverseMap"/> is also <c>true</c>, the flag propagates
    /// to the reverse pair via the bidirectional propagation machinery shipped in PR #11.
    /// </summary>
    public bool PreserveReferences { get; set; }
}

/// <summary>
/// Member-level attribute marking a destination property as ignored (excluded from mapping
/// AND from validation). Equivalent to fluent
/// <c>ForMember(d =&gt; d.X, opt =&gt; opt.Ignore())</c>.
/// </summary>
/// <remarks>
/// Has effect only when applied to a property of a class decorated with
/// <see cref="AutoMapAttribute"/>. Silently no-op otherwise (no error). Combined with
/// <see cref="SourceMemberAttribute"/> or <see cref="NullSubstituteAttribute"/> on the
/// same property, <see cref="IgnoreAttribute"/> short-circuits — the property is never
/// assigned, so the other attributes' configuration is unreachable.
/// </remarks>
[AttributeUsage(AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
public sealed class IgnoreAttribute : Attribute { }

/// <summary>
/// Member-level attribute redirecting a destination property to a different source-side
/// member by name. Equivalent to fluent
/// <c>ForMember(d =&gt; d.X, opt =&gt; opt.MapFrom(s =&gt; s.OtherName))</c>, except that
/// the right-hand side is a name (or dotted path), not a lambda.
/// </summary>
/// <remarks>
/// Resolved at config-build time. The path uses dotted segments for source-side flattening
/// (e.g., <c>"Customer.Address.City"</c>); each segment must resolve to a public readable
/// property or field on the source-side type at that depth. If resolution fails, the
/// scanner accumulates a <see cref="ConfigurationError"/> and the eventual
/// <see cref="AtlasConfigurationException"/> names the offending segment and the type it
/// was looked up on. Has effect only when applied to a property of a class decorated with
/// <see cref="AutoMapAttribute"/>.
/// </remarks>
[AttributeUsage(AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
public sealed class SourceMemberAttribute : Attribute
{
    public SourceMemberAttribute(string memberName)
    {
        ArgumentNullException.ThrowIfNull(memberName);
        MemberName = memberName;
    }

    public string MemberName { get; }
}

/// <summary>
/// Member-level attribute supplying a constant fallback value used when the resolved source
/// member is <c>null</c>. Equivalent to fluent
/// <c>ForMember(d =&gt; d.X, opt =&gt; opt.NullSubstitute(constant))</c>.
/// </summary>
/// <remarks>
/// Has effect only when applied to a property of a class decorated with
/// <see cref="AutoMapAttribute"/>. The validator rejects substitutes whose source-member
/// type is non-nullable (the substitute would be unreachable) or whose substitute type is
/// not assignable to the source-member type. The constructor itself rejects literal
/// <c>null</c> as the substitute value (meaningless: "null substitute = null").
///
/// C# attribute argument types are limited to primitive types, <see cref="string"/>,
/// <see cref="Type"/>, enum values, and 1-D arrays of those. The compiler rejects other
/// constant types at attribute-declaration time, so the runtime never sees an
/// out-of-band <c>NullSubstituteAttribute</c>.
/// </remarks>
[AttributeUsage(AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
public sealed class NullSubstituteAttribute : Attribute
{
    public NullSubstituteAttribute(object constantValue)
    {
        ArgumentNullException.ThrowIfNull(constantValue);
        ConstantValue = constantValue;
    }

    public object ConstantValue { get; }
}
```

**No changes to existing public types.** No new method on `MapperConfigurationExpression`. No new method on `IMapper`. No new method on `MapperProfile`. The four attribute types are the entire public surface delta. `ConfigurationError` and `AtlasConfigurationException` (already public) are reused.

---

## 4. Internal Architecture

One new internal type, two surgical additions to existing types.

### 4.1 New: `Atlas.Internal.AttributeScanner`

`internal static class AttributeScanner` with a single public method:

```csharp
internal static class AttributeScanner
{
    /// <summary>
    /// Scans <paramref name="assembly"/> for top-level public types decorated with
    /// <see cref="AutoMapAttribute"/> and registers each into <paramref name="cfg"/>
    /// via the same fluent calls a hand-written profile would make.
    /// </summary>
    public static void Discover(Assembly assembly, MapperConfigurationExpression cfg);
}
```

Internal helpers:
- `IsAttributeMapCandidate(Type)` — top-level, public, non-abstract, non-interface, non-nested, non-open-generic, non-enum, decorated with `[AutoMap]`. Mirrors `ProfileScanner.IsProfileCandidate`.
- `ValidateAutoMapTarget(Type decoratedType, AutoMapAttribute attr, List<ConfigurationError> errors)` — runs §6 rules 1, 2, plus enum/abstract/interface/static/dynamic-shape rejection. Returns `true` if the pair is registrable.
- `InvokeCreateMap(MapperConfigurationExpression cfg, Type src, Type dst, MemberList memberList)` — uses the cached `CreateMapOpenMethodInfo` to call the generic `CreateMap<S,D>(MemberList)` via reflection. Returns the produced `IMappingExpression<,>` typed as `object`.
- `ApplyMemberAttributes(object mappingExpression, Type src, Type dst, List<ConfigurationError> errors)` — enumerates `dst.GetProperties(BindingFlags.Public | BindingFlags.Instance)`, dispatches per attribute presence (§5).
- `BuildSourcePathExpression(Type srcType, string dottedPath, string destMemberName, List<ConfigurationError> errors, out Type? leafType)` — walks the dotted path; appends a structured error and returns `null` on resolution failure.
- `ApplyClassLevelFlags(object mappingExpression, AutoMapAttribute attr)` — invokes `.PreserveReferences()` then `.ReverseMap()` if the corresponding flags are set.

Static caches resolved at type-init:
- `CreateMapOpenMethodInfo` — the generic `MapperConfigurationExpression.CreateMap<TS,TD>(MemberList)` definition; closed per-pair via `MakeGenericMethod`.
- `IsAutoMapAttributeKind` — set-membership check by `Attribute.GetType()` to avoid repeated `GetCustomAttribute<T>` calls when iterating types.

Per-pair `MethodInfo` resolution (e.g., `ForMember<TMember>` on the closed `IMappingExpression<TS,TD>`) is performed once per `[AutoMap]` type, not per property. The closed-interface `MethodInfo`s are cached locally for the duration of one `Discover` call and discarded afterward.

### 4.2 Modification: `MapperConfigurationExpression.AddMaps(params Assembly[])`

Append one line after the existing profile-scan loop:

```csharp
public void AddMaps(params Assembly[] assemblies)
{
    EnsureMutable();
    foreach (var profile in ProfileScanner.Discover(assemblies))
    {
        foreach (var map in profile.GetTypeMaps())
            RegisterTypeMap(map);
        foreach (var openMap in profile.GetOpenGenericMaps())
            _openGenericMaps.Add(openMap);
    }
    foreach (var asm in assemblies)
    {
        AttributeScanner.Discover(asm, this);   // ◄── NEW
    }
}
```

The two-phase order (profiles first, attributes second) is intentional — see §2.3.

### 4.3 Modification: `MapperConfigurationExpression.RegisterTypeMap` — universal duplicate-pair rule

Existing v1 behavior:

```csharp
private void RegisterTypeMap(TypeMap newTm)
{
    if (_typeMaps.TryGetValue(newTm.Pair, out var existing))
    {
        var existingIsReverse = existing.ReverseMapPair is not null;
        var newIsReverse = newTm.ReverseMapPair is not null;
        if (existingIsReverse || newIsReverse)
        {
            throw new AtlasConfigurationException([...]);
        }
        // Otherwise: preserve v1 last-write-wins behavior (silent overwrite).
    }
    _typeMaps[newTm.Pair] = newTm;
}
```

New behavior (§4 rule, captured in §6 of this design):

```csharp
private void RegisterTypeMap(TypeMap newTm)
{
    if (_typeMaps.TryGetValue(newTm.Pair, out var existing))
    {
        throw new AtlasConfigurationException([
            new(newTm.SourceType, newTm.DestinationType, "(register)",
                $"Type pair ({newTm.SourceType.Name}, {newTm.DestinationType.Name}) is registered twice: " +
                $"{existing.RegistrationOrigin} and {newTm.RegistrationOrigin}. " +
                $"Pick one — every (TSource, TDestination) pair must have a single registration.")
        ]);
    }
    _typeMaps[newTm.Pair] = newTm;
}
```

`TypeMap.RegistrationOrigin` already exists and records the call site. The attribute scanner sets it to `"[AutoMap(typeof({src.Name}))] on {dst.Name}"` so error messages name the attribute, not a synthesized fluent call.

**Behavior change risk acknowledged.** The previous silent last-write-wins is now a loud error. See §11 (Risks) for the migration path and the verification that existing tests don't rely on the silent behavior.

### 4.4 No changes to other internal types

`ConfigurationValidator`, `InheritanceMerger`, `MapperRegistry`, `ExecutionPlanBuilder`, `MappingInvoker`, `MappingContext`, `OpenGenericTypeMap`, `ProjectionCompatibility`, `ProjectionPlanBuilder` — unchanged. Attribute-declared TypeMaps reach all of these via the same paths fluent-declared ones do.

---

## 5. Reflection Mechanics for the Translation Layer

The trickiest implementation detail. The fluent surface is generic (`ForMember<TMember>`, `IMemberConfigurationExpression<TSource, TDestination, TMember>`); the attribute scanner has only `Type` instances. This section pins exactly how the scanner builds and invokes each fluent call.

### 5.1 Cache the open `MethodInfo` references at type-init

```csharp
internal static class AttributeScanner
{
    private static readonly MethodInfo CreateMapOpenMethodInfo =
        typeof(MapperConfigurationExpression)
            .GetMethods()
            .Single(m => m.Name == nameof(MapperConfigurationExpression.CreateMap)
                      && m.IsGenericMethodDefinition
                      && m.GetParameters().Length == 1
                      && m.GetParameters()[0].ParameterType == typeof(MemberList));

    private const string ForMemberMethodName =
        nameof(IMappingExpression<object, object>.ForMember);
    private const string ReverseMapMethodName =
        nameof(IMappingExpression<object, object>.ReverseMap);
    private const string PreserveReferencesMethodName =
        nameof(IMappingExpression<object, object>.PreserveReferences);

    private const string IgnoreMethodName =
        nameof(IMemberConfigurationExpression<object, object, object>.Ignore);
    private const string MapFromMethodName =
        nameof(IMemberConfigurationExpression<object, object, object>.MapFrom);
    private const string NullSubstituteMethodName =
        nameof(IMemberConfigurationExpression<object, object, object>.NullSubstitute);
}
```

`MethodInfo`s on the closed generic interfaces (`IMappingExpression<TS,TD>`, `IMemberConfigurationExpression<TS,TD,TM>`) are resolved per-pair, not statically — they require `MakeGenericType` against user-supplied `Type`s.

### 5.2 Invoke `CreateMap<TSrc, TDst>` for each `[AutoMap]` type

```csharp
var srcType = autoMapAttr.SourceType;
var dstType = decoratedType;

var createMapClosed = CreateMapOpenMethodInfo.MakeGenericMethod(srcType, dstType);
var mappingExpression = createMapClosed.Invoke(cfg, [autoMapAttr.MemberList])!;
// mappingExpression is IMappingExpression<TSrc, TDst>, typed as object here.
```

If the universal duplicate-pair rule fires, `Invoke` re-throws an `AtlasConfigurationException` wrapped in `TargetInvocationException`. The scanner unwraps via `ExceptionDispatchInfo.Capture(ex.InnerException).Throw()` (matches the pattern from PR #10's `Mapper.Map<TDestination>(object)` reflection-dispatch overload).

### 5.3 Resolve closed-generic `MethodInfo`s for the per-pair fluent surface

```csharp
var imappingExprClosed = typeof(IMappingExpression<,>).MakeGenericType(srcType, dstType);

var forMemberOpen = imappingExprClosed.GetMethods()
    .Single(m => m.Name == ForMemberMethodName && m.IsGenericMethodDefinition);

var reverseMapMethod = imappingExprClosed.GetMethod(
    ReverseMapMethodName,
    [typeof(MemberList)])!;

var preserveReferencesMethod = imappingExprClosed.GetMethod(
    PreserveReferencesMethodName,
    Type.EmptyTypes)!;
```

These three are cached in a small local struct for the duration of one `[AutoMap]` type's processing (used by both Step 5.4 and Step 5.5).

### 5.4 `ForMember` invocation per attribute-decorated property

The fluent surface looks like:

```csharp
expr.ForMember<TMember>(
    Expression<Func<TDestination, TMember>> destinationMember,
    Action<IMemberConfigurationExpression<TSource, TDestination, TMember>> memberOptions);
```

Both arguments need to be constructed dynamically. For each destination property `prop` carrying one or more of `[Ignore]` / `[SourceMember]` / `[NullSubstitute]`:

**5.4a — Build the destination selector `Expression<Func<TDestination, TMember>>`:**

```csharp
var memberType = prop.PropertyType;                              // TMember
var dstParam = Expression.Parameter(dstType, "d");                // d
var memberAccess = Expression.Property(dstParam, prop);           // d.X
var funcType = typeof(Func<,>).MakeGenericType(dstType, memberType);
var selector = Expression.Lambda(funcType, memberAccess, dstParam);  // d => d.X
// selector is typed as Expression<Func<TDest, TMember>> via the funcType
```

**5.4b — Build the options callback `Action<IMemberConfigurationExpression<TSrc, TDst, TMember>>`:**

This is the only place where reflection composes multiple operations on the per-member fluent surface. The natural shape is `Expression.Lambda` with a `Block` body containing one method call per attribute. The per-member methods (`Ignore`, `MapFrom`, `NullSubstitute`) live on the *closed* `IMemberConfigurationExpression<TSrc, TDst, TMember>`, so their `MethodInfo`s must be resolved against that closed type:

```csharp
var imemberConfigClosed = typeof(IMemberConfigurationExpression<,,>)
    .MakeGenericType(srcType, dstType, memberType);
var optParam = Expression.Parameter(imemberConfigClosed, "opt");

var statements = new List<Expression>();

// 1. [Ignore] short-circuits: if present, only emit Ignore() and skip MapFrom/NullSubstitute.
if (prop.GetCustomAttribute<IgnoreAttribute>() is not null)
{
    var ignoreMethod = imemberConfigClosed.GetMethod(IgnoreMethodName, Type.EmptyTypes)!;
    statements.Add(Expression.Call(optParam, ignoreMethod));
}
else
{
    // 2. [SourceMember(path)] → opt.MapFrom<TSourceMember>(s => s.Path)
    Type? sourceMemberType = null;
    if (prop.GetCustomAttribute<SourceMemberAttribute>() is { } sourceMemberAttr)
    {
        var sourceLambda = BuildSourcePathExpression(srcType, sourceMemberAttr.MemberName,
                                                    prop.Name, errors, out sourceMemberType);
        if (sourceLambda is not null)
        {
            var mapFromOpen = imemberConfigClosed.GetMethods()
                .Single(m => m.Name == MapFromMethodName
                          && m.IsGenericMethodDefinition
                          && m.GetParameters()[0].ParameterType.IsGenericType
                          && m.GetParameters()[0].ParameterType.GetGenericTypeDefinition()
                              == typeof(Expression<>));
            var mapFromClosed = mapFromOpen.MakeGenericMethod(sourceMemberType!);
            statements.Add(Expression.Call(optParam, mapFromClosed,
                Expression.Constant(sourceLambda, sourceLambda.GetType())));
        }
    }

    // 3. [NullSubstitute(value)] → opt.NullSubstitute<TSourceMember>(value)
    if (prop.GetCustomAttribute<NullSubstituteAttribute>() is { } nullSubAttr)
    {
        // Resolve TSourceMember: SourceMember leaf if present, else convention-resolved on src.
        var resolvedSourceType = sourceMemberType ?? ResolveSourceMemberByConvention(srcType, prop, errors);
        if (resolvedSourceType is not null
            && ValidateNullSubstituteCompatibility(resolvedSourceType, nullSubAttr.ConstantValue,
                                                   prop.Name, errors))
        {
            var constantOverloadOpen = imemberConfigClosed.GetMethods()
                .Single(m => m.Name == NullSubstituteMethodName
                          && m.IsGenericMethodDefinition
                          && m.GetParameters().Length == 1
                          && !m.GetParameters()[0].ParameterType.IsGenericType);  // constant overload
            var constantOverloadClosed = constantOverloadOpen.MakeGenericMethod(resolvedSourceType);
            statements.Add(Expression.Call(optParam, constantOverloadClosed,
                Expression.Constant(nullSubAttr.ConstantValue, resolvedSourceType)));
        }
    }
}

if (statements.Count == 0) return;  // No attributes meaningful for this prop
var body = statements.Count == 1 ? statements[0] : Expression.Block(statements);
var actionType = typeof(Action<>).MakeGenericType(imemberConfigClosed);
var optionsCallback = Expression.Lambda(actionType, body, optParam).Compile();
```

**5.4c — Invoke `ForMember<TMember>(selector, optionsCallback)`:**

```csharp
var forMemberClosed = forMemberOpen.MakeGenericMethod(memberType);
forMemberClosed.Invoke(mappingExpression, [selector, optionsCallback]);
```

The compiled `optionsCallback` is invoked exactly once (inside `ForMember`) and the resulting `PropertyMap` records the attribute-derived configuration via the same `IsExplicit = true` paths the fluent API uses. The compiled delegate is GC-eligible after `ForMember` returns (no retention on the `TypeMap`).

### 5.5 `PreserveReferences` and `ReverseMap` invocation

```csharp
if (autoMapAttr.PreserveReferences)
    preserveReferencesMethod.Invoke(mappingExpression, null);

if (autoMapAttr.ReverseMap)
    reverseMapMethod.Invoke(mappingExpression, [MemberList.None]);
```

Order: `PreserveReferences` first, `ReverseMap` second. Either order works because PR #11's bidirectional propagation handles both, but the scanner emits this fixed order for determinism in test snapshots / debugging.

### 5.6 Path resolution for `[SourceMember]`

```csharp
private static LambdaExpression? BuildSourcePathExpression(
    Type srcType, string dottedPath, string destMemberName,
    List<ConfigurationError> errors, out Type? leafType)
{
    leafType = null;
    var segments = dottedPath.Split('.');
    var srcParam = Expression.Parameter(srcType, "s");
    Expression current = srcParam;
    Type currentType = srcType;

    for (int i = 0; i < segments.Length; i++)
    {
        var segment = segments[i];

        var prop = currentType.GetProperty(segment, BindingFlags.Public | BindingFlags.Instance);
        var field = prop is null
            ? currentType.GetField(segment, BindingFlags.Public | BindingFlags.Instance)
            : null;

        if (prop is null && field is null)
        {
            errors.Add(new(srcType, decoratedType, destMemberName,
                $"[SourceMember(\"{dottedPath}\")] on '{decoratedType.Name}.{destMemberName}' — " +
                $"segment '{segment}' not found on '{currentType.Name}'."));
            return null;
        }

        if (prop is { CanRead: false })
        {
            errors.Add(new(srcType, decoratedType, destMemberName,
                $"[SourceMember(\"{dottedPath}\")] on '{decoratedType.Name}.{destMemberName}' — " +
                $"segment '{segment}' on '{currentType.Name}' has no public getter."));
            return null;
        }

        if (prop is not null)
        {
            current = Expression.Property(current, prop);
            currentType = prop.PropertyType;
        }
        else
        {
            current = Expression.Field(current, field!);
            currentType = field!.FieldType;
        }
    }

    leafType = currentType;
    var funcType = typeof(Func<,>).MakeGenericType(srcType, leafType);
    return Expression.Lambda(funcType, current, srcParam);
}
```

Errors include the *full* path and the failing segment with the type it was looked up on — enough to debug without consulting the source type definition.

### 5.7 Error accumulation pattern

The scanner uses the same accumulate-then-throw pattern as `ConfigurationValidator`:

```csharp
public static void Discover(Assembly assembly, MapperConfigurationExpression cfg)
{
    var errors = new List<ConfigurationError>();
    foreach (var type in assembly.GetTypes().Where(IsAttributeMapCandidate))
    {
        try
        {
            ProcessAutoMapType(type, cfg, errors);
        }
        catch (TargetInvocationException tie) when (tie.InnerException is AtlasConfigurationException acex)
        {
            // Universal duplicate-pair rule fired during scanner-issued CreateMap.
            // Re-throw with the inner exception so the user sees the proper exception type.
            ExceptionDispatchInfo.Capture(acex).Throw();
        }
    }
    if (errors.Count > 0)
    {
        throw new AtlasConfigurationException(errors);
    }
}
```

Some failures are fatal-immediately (the duplicate-pair `AtlasConfigurationException` thrown from `RegisterTypeMap`); they propagate up directly. Others are accumulated (path resolution failures, type compatibility failures, attribute validation failures) — the user sees them all in one exception.

### 5.8 Why `Expression.Compile()` per `ForMember` callback

The reflection alternative (`forMemberClosed.Invoke(mappingExpression, [selector, callbackDelegate])` where `callbackDelegate` is built without compilation) doesn't work cleanly because the `Action<...>` parameter has to be a real delegate of the right closed-generic type at the boundary, and `Delegate.CreateDelegate` requires a `MethodInfo` for a method that doesn't exist (we want to invoke a sequence of method calls, not call a single method). The cleanest path is `Expression.Lambda(...).Compile()` once per attribute-decorated property — paid at startup, never on the hot path. For an assembly with 200 attribute-decorated DTOs, this is ~200 `Expression.Compile()` calls — measurable but smaller than the existing `MapperConfiguration.CompileMappings()` cost (which compiles the per-typemap mapping body lambdas, a much heavier expression tree).

If startup profiling shows the per-`ForMember` `Expression.Compile()` is significant, a future optimization can use a code-generated invoker pattern (`Action<IMemberConfigurationExpression<TS,TD,TM>>` synthesized via `MakeGenericType` on a generic helper class). Defer until measured.

### 5.9 Performance posture

- Reflection runs once per `[AutoMap]`-decorated type at config-build. Hot path is unchanged — runtime `Map<>()` calls invoke the same compiled lambdas the fluent path produces.
- `Expression.Compile()` is invoked once per `[AutoMap]`-decorated property bearing at least one member-level attribute. Identical in count and shape to the per-`ForMember` lambda compilation the fluent path already pays.
- `MakeGenericMethod` resolutions on closed-interface methods are local to one type-processing iteration and not cached globally. If benchmarks show this matters, lift to a `ConcurrentDictionary<TypePair, ClosedFluentMethods>`. Defer until measured.

---

## 6. Validation

The attribute path produces standard `TypeMap` / `PropertyMap` instances, so the existing `ConfigurationValidator` covers most validation for free — every rule that checks "is this destination member mapped?", "is the resolved source-member type compatible?", "is `PreserveReferences + ConvertUsing` rejected?" runs unchanged. The attribute scanner adds five attribute-specific rules surfaced via `AtlasConfigurationException` with the standard `ConfigurationError` shape.

### 6.1 Rule 1: `[AutoMap]` source type cannot be open generic

```
[AutoMap(typeof(Source<>))]
public class DestDto { ... }
```
Error: `"[AutoMap] on 'DestDto' specifies open-generic source type 'Source<>'. Open generics use cfg.CreateMap(typeof(Source<>), typeof(Dest<>)) — attribute syntax is not supported for open generics."`

Check: `attr.SourceType.IsGenericTypeDefinition`.

### 6.2 Rule 2: `[AutoMap]`-decorated class cannot be open generic, abstract, interface, static, or enum

```
[AutoMap(typeof(Source))]
public class DestDto<T> { ... }                  // open generic
public abstract class AbstractDto { ... }        // abstract
public interface IDto { ... }                    // interface
public static class StaticDto { ... }            // static (= abstract sealed)
public enum DestEnum { A, B, C }                 // enum
```

Errors:
- `"[AutoMap] applied to open-generic type 'DestDto<>'. Use cfg.CreateMap(typeof(Source<>), typeof(DestDto<>)) for open-generic registrations."`
- `"[AutoMap] applied to abstract type 'AbstractDto'. Atlas cannot instantiate abstract destinations."`
- `"[AutoMap] applied to interface 'IDto'. Atlas cannot instantiate interfaces; use a concrete destination type."`
- `"[AutoMap] applied to static type 'StaticDto'. Static types cannot be mapping destinations."`
- `"[AutoMap] applied to enum 'DestEnum'. Use cfg.CreateMap<TSrcEnum, DestEnum>().MapByName() (or similar) for enum-to-enum mappings."`

Checks (in order — first match short-circuits):
- `decoratedType.IsEnum`
- `decoratedType.IsInterface`
- `decoratedType.IsAbstract && decoratedType.IsSealed` → "static" (CLR encoding)
- `decoratedType.IsAbstract` → "abstract"
- `decoratedType.IsGenericTypeDefinition || decoratedType.ContainsGenericParameters`

### 6.3 Rule 3: `[AutoMap]` source type cannot be a recognized dynamic shape

```
[AutoMap(typeof(Dictionary<string, object>))]
public class OrderDto { ... }
```
Error: `"[AutoMap] on 'OrderDto' specifies a recognized dynamic shape ('Dictionary<string, object>'). Dynamic mapping is convention-only and requires no registration — remove the attribute and call mapper.Map<OrderDto>(dictInstance) directly. To explicitly register a non-dynamic mapping for this pair, use cfg.CreateMap<Dictionary<string, object>, OrderDto>() in a profile."`

Check: shared helper `DynamicShape.IsDynamicSourceShape(type)` from #10. The three recognized shapes are `IDictionary<string, object>`, `Dictionary<string, object>`, and `ExpandoObject`.

Why this is an error rather than a silent fallthrough: a user who applies `[AutoMap]` to a DTO with a dynamic source has explicit member-level intent (they're decorating properties with `[Ignore]`, etc.) — but Atlas's dynamic-shape inference would otherwise fire on first `Map<DestDto>(dictInstance)` call and synthesize a different TypeMap. The attribute would either be silently ignored (confusing) or override the dynamic inference (also confusing — the user probably doesn't realize they're disabling dynamic mapping). Reject loudly, document the workarounds.

### 6.4 Rule 4: `[SourceMember]` path resolves to an existing member chain

Path-walking algorithm specified in §5.6. Errors include:
- Segment-not-found: `"[SourceMember(\"Customer.Address.City\")] on 'OrderDto.X' — segment 'Address' not found on 'Customer'."`
- Non-readable leaf: `"[SourceMember(\"Customer.Name\")] on 'OrderDto.X' — segment 'Name' on 'Customer' has no public getter."`
- Non-public segment: same message as "segment not found" (private/internal members are not reflectively visible to `BindingFlags.Public`).

### 6.5 Rule 5: `[NullSubstitute]` constant compatible with source-member type

This is the same rule the fluent `NullSubstitute` path enforces (already in `ConfigurationValidator`). For attribute-time clarity, the scanner runs the check eagerly so the error message names the attribute, not the synthesized `ForMember` call:

```
src member type: int? (Nullable<int>)
[NullSubstitute("hello")]   ← string is not assignable to int?
```
Error: `"[NullSubstitute(\"hello\")] on 'DestDto.X' — substitute type 'String' is not assignable to source-member type 'Nullable<Int32>'."`

The fluent path's `ConfigurationValidator` rule runs as a backstop. Compatibility checks (in `ValidateNullSubstituteCompatibility`):
- Substitute type assignable to source-member type, OR
- Substitute is a numeric primitive and source is `Nullable<T>` of a wider numeric type (existing numeric-coercion rule), OR
- Substitute is an enum value and source is the same enum (or `Nullable<>` of it), OR
- Substitute is `string` and source is `string`.

### 6.6 Rule 6: `[NullSubstitute]` on non-nullable source member is unreachable

Identical to the fluent rule. Surfaces eagerly with attribute-named messages:

```
src member type: int (non-nullable value type)
[NullSubstitute(0)]   ← unreachable; int is never null
```
Error: `"[NullSubstitute(0)] on 'DestDto.X' — source-member type 'Int32' is non-nullable; the substitute is unreachable. Use a different default mechanism or remove the attribute."`

Enums (which are value types) trigger the same rule unless the source member is `Nullable<TEnum>`.

### 6.7 Universal duplicate-pair rule

`MapperConfigurationExpression.RegisterTypeMap` throws `AtlasConfigurationException` on any second registration for the same `(TSource, TDestination)` pair regardless of origin (profile fluent, scanner-translated attribute, repeated `cfg.CreateMap` on the configuration root, `.ReverseMap()`).

```
Type pair (Customer, CustomerSummaryDto) is registered twice:
  CreateMap<Customer, CustomerSummaryDto>() and [AutoMap(typeof(Customer))] on CustomerSummaryDto.
  Pick one — every (TSource, TDestination) pair must have a single registration.
```

The `RegistrationOrigin` field on `TypeMap` already records the call site for fluent registrations. The attribute scanner sets it to `"[AutoMap(typeof({src.Name}))] on {dst.Name}"`.

**Behavior change risk:** previous v1 behavior on duplicate non-reverse `CreateMap<S,D>()` was silent last-write-wins. Tightening to throw is technically a breaking change for users who unintentionally relied on the silent overwrite. Verified via grep against the existing 634-test baseline that no tests depend on the silent behavior — the change is localized to one method body. Documented in §11 (Risks) and the README delta.

### 6.8 Validation phase ordering

1. **Attribute-scan time** (during `AddMaps`/`AddAtlas`): rules 1, 2, 3, 4, 5, 6. Errors collected into a list; thrown as a single `AtlasConfigurationException` when the scan completes. The user sees all attribute mistakes in one exception rather than fixing them one-by-one.
2. **Registration time** (`RegisterTypeMap`): universal duplicate-pair rule (§6.7). Throws immediately on first duplicate with both origin sites named.
3. **`AssertConfigurationIsValid()` time** (existing): the full `ConfigurationValidator` runs over all registered TypeMaps, including attribute-derived ones. Catches anything missed at attribute-scan time (e.g., `MemberList.Source` violations the scanner doesn't model).

The three phases are intentionally separate: phase 1 catches attribute-only mistakes with attribute-named messages; phase 2 catches the common "configured the same pair twice" mistake immediately so the stack trace points at the offending second registration; phase 3 is the existing fail-late catch-all the user already calls in their unit tests.

---

## 7. Interaction with Existing v2 Features

The translate-to-fluent architecture means most existing v2 features inherit transparently. Per-feature behavior:

### 7.1 #2 Inheritance (`Include` / `IncludeBase`)

**Status: not directly expressible via attributes; mixed-mode supported.**

`Include<TDerivedSource, TDerivedDestination>()` and `IncludeBase<TBaseSource, TBaseDestination>()` require compile-time generics. Mixed mode: a base map declared via `[AutoMap]` and a derived map declared via fluent `cfg.CreateMap<DerivedSrc, DerivedDst>().IncludeBase<BaseSrc, BaseDst>()` works as in fluent — `InheritanceMerger.MergeBaseConfig` finds the attribute-derived base TypeMap (it's a regular `TypeMap`) and merges its config. Member-level attribute config flows base → derived through the existing `CopyConfig` precedence machinery.

A user wanting attribute-decorated inheritance has two options: (a) declare both maps fluently with explicit `Include`/`IncludeBase`; (b) decorate base and derived independently with `[AutoMap]` accepting that runtime polymorphism dispatch won't fire (each map is a separate registration with no parent/child relationship).

### 7.2 #3 Enum surface

**Status: rejected.** §6 rule 2 rejects `[AutoMap]` on enum types. Use `cfg.CreateMap<SrcEnum, DestEnum>().MapByName()` (or similar) for enum-to-enum mappings.

### 7.3 #4 Reverse mapping (`ReverseMap`)

**Status: covered by `[AutoMap(ReverseMap = true)]`.**

The scanner calls `.ReverseMap(MemberList.None)` after applying member attributes. Member-level attribute config describes the **forward direction only** and does not auto-flip — matches fluent semantics (`Ignore`, `MapFrom`, `NullSubstitute` don't auto-flip in fluent either). A user needing reverse-side overrides must use a fluent profile for that pair (mutually exclusive with `[AutoMap]` per Q4).

`[AutoMap(typeof(SrcType))]` on `DestType` PLUS `[AutoMap(typeof(DestType))]` on `SrcType` (both directions independently decorated): if either also sets `ReverseMap = true`, the duplicate-pair rule (§6.7) fires. Without `ReverseMap = true`, both registrations succeed as independent unlinked pairs (no `_linkedForwardTypeMap` connection — `PreserveReferences` on one does NOT propagate to the other).

`ForPath` (nested-destination chain bindings) has no non-lambda equivalent. Deferred.

### 7.4 #5 Before/after hooks

**Status: deferred to future doc.** Tier-4 attributes (`[BeforeMap(typeof(MyAction))]` / `[AfterMap(typeof(MyAction))]`) explicitly out of scope (Q2). Mixed mode works in principle (profile attaches hooks to attribute-declared TypeMap) but collides with the duplicate-pair rule — adding hooks to an attribute-decorated DTO requires removing `[AutoMap]` and re-creating fluently. v1 limitation; v2 attribute hooks resolve it.

### 7.5 #6 Value transformers

**Status: global scope works; profile scope does not.**

Global transformers (`cfg.ValueTransformers.Add<T>(...)`) compose against attribute-declared TypeMaps the same as fluent ones — `TransformerResolver` doesn't care how the TypeMap was registered.

**Profile-scope transformers do NOT fire on attribute-declared TypeMaps.** Attribute-declared TypeMaps have `OriginatingProfile = null`. Same limitation as DynamicMapping (#10) and OpenGenerics (#9). Documented in §11 (Risks); workaround is global-scope or a fluent re-declaration.

### 7.6 #7 Conditional mapping

**Status: fluent-only.** Both `Condition` and `PreCondition` take `Expression<Func<,>>`. No attribute equivalent. A user with an attribute-decorated DTO needing a per-member skip predicate must remove the attribute and use fluent.

### 7.7 #8 Null substitution

**Status: covered by `[NullSubstitute(constant)]`.**

Constant-form `NullSubstitute<TSourceMember>(TSourceMember constant)` maps directly. Factory-form `NullSubstitute<TSourceMember>(Expression<Func<TSourceMember>> factory)` requires an expression — fluent-only.

Validator rules (unreachable on non-nullable; type-mismatch substitute) run identically — see §6 rules 5 & 6.

### 7.8 #9 Open generics

**Status: rejected via §6 rules 1 & 2.** Use fluent `cfg.CreateMap(typeof(Source<>), typeof(Dest<>))`.

A *closed* generic instantiation as the source IS allowed: `[AutoMap(typeof(List<int>))]` on `MyDto` (not `MyDto<>`) works — the resulting pair `(List<int>, MyDto)` is a closed pair. Edge case unlikely in practice; documented for completeness.

### 7.9 #10 Dynamic mapping

**Status: rejected via §6 rule 3.** Use convention-only (no registration) for dynamic shapes.

### 7.10 #11 Reference handling (`PreserveReferences`)

**Status: covered by `[AutoMap(PreserveReferences = true)]`.**

Bidirectional propagation works because the scanner routes through `.PreserveReferences()` — the `_linkedForwardTypeMap` machinery from PR #11 fires whether the call comes from a profile or a scanner. Combined with `ReverseMap = true`, the flag propagates to the reverse pair correctly regardless of attribute property declaration order.

### 7.11 Atlas.Projections (the IQueryable side)

**Status: works transparently via translate-to-fluent.**

Attribute-declared TypeMaps participate in `ProjectTo<T>()` exactly like fluent ones. `ProjectionCompatibility.IsTypeMapProjectable` sees a normal `TypeMap`; it doesn't know or care that the TypeMap originated from an attribute. Existing exclusions (Hooks, PreserveReferences, Dynamic, ForPath) still fire on attribute-declared maps that exhibit those features.

`[Ignore]`, `[SourceMember]`, `[NullSubstitute]` translate to the same `PropertyMap` shapes the fluent path produces; `ProjectionPlanBuilder` consumes them identically. `[NullSubstitute]` translates to SQL `COALESCE` natively.

### 7.12 Summary table

| Feature | Attribute support | Fluent fallback path |
|---|---|---|
| `CreateMap` | `[AutoMap]` ✓ | — |
| `MemberList` | `[AutoMap(MemberList = ...)]` ✓ | — |
| `ReverseMap` | `[AutoMap(ReverseMap = true)]` ✓ | — |
| `PreserveReferences` | `[AutoMap(PreserveReferences = true)]` ✓ | — |
| `Ignore` (member) | `[Ignore]` ✓ | — |
| `SourceMember` redirect | `[SourceMember(name)]` ✓ (incl. dotted paths) | — |
| `NullSubstitute` constant | `[NullSubstitute(value)]` ✓ | — |
| `MapFrom(Expression)` | ✗ (lambda) | Use profile |
| `NullSubstitute(factory)` | ✗ (lambda) | Use profile |
| `Condition` / `PreCondition` | ✗ (lambda) | Use profile |
| `BeforeMap` / `AfterMap` | ✗ (deferred) | Use profile |
| `ConvertUsing` | ✗ (deferred) | Use profile |
| `AddTransform<T>` | ✗ (lambda) | Use profile |
| `Include` / `IncludeBase` | ✗ (compile-time generics) | Use profile |
| `ForCtorParam` | ✗ (lambda) | Use profile |
| `ForPath` | ✗ (lambda) | Use profile |
| Enum `MapValue` / `WithFallback` | ✗ (deferred) | Use profile |
| Open-generic registrations | ✗ (rejected) | `cfg.CreateMap(typeof(<>), typeof(<>))` |
| Dynamic-shape registrations | ✗ (rejected) | Convention-only — no registration needed |

---

## 8. DI Integration

`Atlas.Extensions.DependencyInjection`'s `AddAtlas` overloads route through `MapperConfigurationExpression.AddMaps(...)` — which §4 already extended to call `AttributeScanner.Discover(asm, this)`. **Attribute discovery works through DI with zero new code in the DI extension package.**

### 8.1 Existing DI entry points (unchanged)

```csharp
services.AddAtlas(params Assembly[] assemblies);
services.AddAtlas(Action<MapperConfigurationExpression> configure, params Assembly[] assemblies);
```

Both call `cfg.AddMaps(assemblies)` internally. After §4's surgical change, that single call discovers `MapperProfile` subclasses via `ProfileScanner.Discover` AND attribute-decorated types via `AttributeScanner.Discover` against the same assemblies. No public-API change in `AtlasServiceCollectionExtensions`.

### 8.2 Worked example

```csharp
// In a class library:
[AutoMap(typeof(Order), MemberList = MemberList.Source, PreserveReferences = true)]
public class OrderDto
{
    [SourceMember("Customer.Name")] public string CustomerName { get; init; } = "";
    [Ignore] public decimal Total { get; init; }
    [NullSubstitute("(no email)")] public string Email { get; init; } = "";
}

public class OrderProfile : MapperProfile
{
    public OrderProfile()
    {
        // A fluent profile in the same assembly for a different pair.
        CreateMap<Customer, CustomerSummaryDto>()
            .ForMember(d => d.DisplayName, o => o.MapFrom(s => $"{s.FirstName} {s.LastName}"));
    }
}

// Application startup:
services.AddAtlas(typeof(OrderDto).Assembly);
```

After `AddAtlas` returns:
- `OrderProfile` discovered, instantiated, `Configure()` invoked → fluent `CreateMap<Customer, CustomerSummaryDto>()` lands first.
- `OrderDto` discovered via `[AutoMap]` → `cfg.CreateMap<Order, OrderDto>(MemberList.Source)` → `.ForMember` per attribute property → `.PreserveReferences()`.
- `MapperConfiguration` built and registered as singleton; `IMapper` registered as transient.

### 8.3 Discovery scope

Identical to fluent profile scanning:
- Top-level public types only — nested and non-public types skipped.
- One pass per assembly (`Assembly.GetTypes()` enumerated once); profile candidates and attribute candidates filtered from the same enumeration.
- No transitive/referenced-assembly traversal.

Implementation note: `AttributeScanner.Discover` and `ProfileScanner.Discover` could share the `asm.GetTypes()` call to avoid double-enumeration. Defer the optimization until benchmarks justify — `Assembly.GetTypes()` is fast and runs once per assembly per `AddMaps` call.

### 8.4 Lifetimes (unchanged)

| Type | Lifetime |
|---|---|
| `MapperConfiguration` | Singleton |
| `IMapper` | Transient (cheap; wraps singleton config) |
| `MapperProfile` subclasses | Single instance per `MapperConfiguration` build |
| `[AutoMap]`-decorated DTO classes | Not instantiated during config-build — scanner reads `Type` and reflects on attributes only. Destination instances are created at runtime by the compiled lambda the same way fluent-declared destinations are. |
| `IMappingAction<,>` (via #5 hooks) | Singleton in DI; per-config-cache without DI — unchanged from #5 |

The attribute scanner does NOT instantiate decorated DTO classes during scan. Instantiation happens only at `Map<>()` time.

### 8.5 Attribute support for tier-3 / tier-4 DI integration (deferred)

When `[BeforeMap(typeof(MyAction))]` / `[AfterMap(typeof(MyAction))]` / `[ValueConverter(typeof(MyConverter))]` / `[ConvertUsing(typeof(MyConverter))]` land in a future doc, DI integration reuses the existing `IServiceProvider`-aware activation path that #5 (BeforeAfterHooks) shipped — no DI plumbing changes anticipated. Out of scope for v1.

---

## 9. Edge Cases and Corner Behaviors

### 9.1 Inheritance of `[AutoMap]` itself

`AutoMapAttribute` is declared with `Inherited = false`. A subclass `SubDto : ParentDto` where `ParentDto` is decorated with `[AutoMap(typeof(Source))]` does **not** automatically get its own attribute-derived TypeMap.

Reasoning:
1. `Inherited = true` would silently register one TypeMap per subclass with the same `(Source, *)` source type — surprising and almost always wrong (subclasses usually need different mappings).
2. Atlas inheritance (#2) is explicit via `Include` / `IncludeBase`; auto-inherited attribute behavior would contradict the explicit-inheritance mental model.
3. Mirrors AutoMapper's default for the same attribute.

A user wanting attribute-derived mappings on both parent and subclass decorates both classes explicitly.

### 9.2 Member-level attributes on a class without `[AutoMap]`

Silently ignored. The attribute scanner enumerates only `[AutoMap]`-decorated types; non-decorated classes never reach property-attribute dispatch.

This is intentional — a class might carry `[Ignore]` for an unrelated framework, and Atlas should not error on it. The cost: `[Ignore]` typos (forgot the class-level `[AutoMap]`) become silent — diagnosed only via downstream `AssertConfigurationIsValid()` (an "ignored" required member is now missing a source under `MemberList.Destination` validation).

Mitigation: README example shows `[AutoMap]` first; documented in §11 (Risks).

### 9.3 `[SourceMember]` resolving to a non-readable member

The path-walk validates `prop.CanRead` on each property segment (§5.6). Write-only properties and private/internal fields produce structured errors (§6 rule 4). `Expression.Property` / `Expression.Field` won't blow up at runtime because the validation precedes the expression construction.

### 9.4 `[SourceMember]` colliding with the convention

If destination property `OrderDto.CustomerName` would *also* convention-resolve to `src.Customer.Name` without the attribute, the attribute and the convention agree — both produce the same `MapFrom` expression. The attribute marks `IsExplicit = true` on the resulting `PropertyMap` either way.

If they *disagree*, the attribute wins (same precedence as fluent `MapFrom`). The `IsExplicit = true` flag suppresses convention.

### 9.5 `[SourceMember]` on a property that doesn't exist on `TDestination`

Impossible — the attribute is property-targeted, so the `MemberInfo` carrying it is always a real destination property. The "destination property doesn't exist" failure mode doesn't apply to attribute-driven config.

### 9.6 Multiple member-level attributes on one property

`[SourceMember("Customer.Name")] [NullSubstitute("(unknown)")] public string CustomerName { get; init; }`

Both apply: `MapFrom(s => s.Customer.Name)` followed by `NullSubstitute("(unknown)")`. Order inside the `ForMember` callback (§5 step 5.4b): `Ignore` (if present, exclusive) → `MapFrom` → `NullSubstitute`.

`[Ignore]` + any other attribute on the same property: `[Ignore]` short-circuits — the property is never assigned, so `MapFrom`/`NullSubstitute` would be no-ops. The scanner only emits the `Ignore()` call (§5 step 5.4b's `if/else`), so the unreachable attributes don't cost compilation. Documented in `IgnoreAttribute` xmldoc.

### 9.7 `[NullSubstitute]` constant of an attribute-illegal type

C# attribute argument types are limited (primitives, `string`, `Type`, enums, 1-D arrays). `[NullSubstitute(new Customer())]` doesn't compile. So the runtime never sees a `NullSubstituteAttribute` carrying a `Customer` instance. The scanner's compatibility check (§6 rule 5) only handles the legal subset.

### 9.8 `[AutoMap(typeof(SomeType))]` where `SomeType` equals the decorated class

Self-pair. `[AutoMap(typeof(OrderDto))]` on `OrderDto`. Routes through `cfg.CreateMap<OrderDto, OrderDto>()`. Already legal in fluent (cloning use case); attribute version has the same behavior. No special handling.

### 9.9 `[Ignore]` + `MemberList.Destination` validation

`[Ignore]` correctly removes the property from validation — same as fluent `Ignore()`. The destination-list validator skips ignored properties.

### 9.10 `[AutoMap]` on a class with no public parameterless constructor

Identical to fluent — Atlas constructor mapping selects the only public constructor and binds parameters by name from the source via convention. Attribute version doesn't change this. Tier-4 attributes (deferred) might add `[ConstructorMapping]` later; for v1, the existing convention applies.

### 9.11 `[AutoMap]` + `ReverseMap = true` where source is also `[AutoMap]`-decorated

```csharp
[AutoMap(typeof(B))] public class A { ... }
[AutoMap(typeof(A))] public class B { ... }
```

Two independent attribute registrations: `(B, A)` and `(A, B)`. Not linked by `_linkedForwardTypeMap` (set only when `.ReverseMap()` produces the reverse). `PreserveReferences` on one does NOT propagate to the other.

If the user wants linkage: apply `[AutoMap(typeof(B), ReverseMap = true)]` on `A` and remove the attribute on `B` — the scanner produces the linked pair via `.ReverseMap()`. If both `[AutoMap]` attributes remain AND `ReverseMap = true` is set on either, the duplicate-pair rule fires.

### 9.12 Empty `[AutoMap(typeof(Source))]` (no member attributes)

Equivalent to bare `cfg.CreateMap<Source, Dest>()`. Pure convention-only mapping. No `ForMember` calls; the scanner's per-property loop runs with zero matching attributes. Valid and useful — declares "this DTO is mapped from Source, use conventions for everything."

### 9.13 Threading

Attribute discovery runs single-threaded inside `cfg.AddMaps(...)`. `_typeMaps` is mutated only on the calling thread. No new threading concerns. Post-build, the resulting `MapperConfiguration` is immutable and used per existing thread-safety guarantees.

### 9.14 `[AutoMap]` on multiple classes in the same assembly: scan ordering

The scanner enumerates `assembly.GetTypes()` whose ordering is implementation-defined (typically declaration order, not guaranteed). If two attribute-decorated classes both target the same source-pair direction (`[AutoMap(typeof(Order))]` on both `OrderDtoA` and `OrderDtoB`), they produce two distinct registrations `(Order, OrderDtoA)` and `(Order, OrderDtoB)` — both valid, no conflict.

If two attribute-decorated classes both target the *same* `(src, dst)` pair (impossible via `[AutoMap]` alone since the attribute is on the destination, but possible via `[AutoMap(typeof(SrcA))]` on `Dst` and `[AutoMap(typeof(SrcB))]` on a SUBCLASS where `SrcA == SrcB` — extremely unlikely): scan order determines which one registers first, the second triggers the duplicate-pair rule.

### 9.15 Reflection-time exceptions during `MakeGenericMethod` / `Invoke`

§5 specifies that `ArgumentException` from `MakeGenericMethod` (e.g., invalid type argument) and `TargetInvocationException` wrapping `AtlasConfigurationException` from `Invoke` are caught at the appropriate layer:
- `ArgumentException` → converted to a `ConfigurationError` with the failing property name and the underlying message.
- `TargetInvocationException` wrapping `AtlasConfigurationException` (from the duplicate-pair rule) → unwrapped via `ExceptionDispatchInfo.Capture(...).Throw()`, matches PR #10's pattern.

Other unexpected exceptions propagate (programmer errors, not user errors).

---

## 10. Worked Examples End-to-End

### 10.1 Example A — minimum: pure-convention attribute declaration

```csharp
public class Order
{
    public int Id { get; set; }
    public string Reference { get; set; } = "";
    public decimal Total { get; set; }
}

[AutoMap(typeof(Order))]
public class OrderDto
{
    public int Id { get; set; }
    public string Reference { get; set; } = "";
    public decimal Total { get; set; }
}

// Startup:
var cfg = new MapperConfiguration(c => c.AddMaps(typeof(OrderDto).Assembly));
var mapper = cfg.CreateMapper();
```

Scanner translation (conceptual):

```csharp
// Inside MapperConfigurationExpression.AddMaps(asm):
//   ProfileScanner.Discover(asm) → finds nothing
//   AttributeScanner.Discover(asm, this):
//     for OrderDto:
//       cfg.CreateMap<Order, OrderDto>(MemberList.Destination)
//         // No member attributes; convention engine resolves Id/Reference/Total by name.
```

Runtime:

```csharp
var dto = mapper.Map<OrderDto>(new Order { Id = 1, Reference = "R-100", Total = 42.0m });
// dto.Id == 1, dto.Reference == "R-100", dto.Total == 42.0m
```

### 10.2 Example B — full feature surface in one DTO

```csharp
public class Customer
{
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string? Email { get; set; }
}

public class Order
{
    public int Id { get; set; }
    public Customer Customer { get; set; } = new();
    public Order? PreviousOrder { get; set; }
    public decimal RawTotal { get; set; }
    public string? Notes { get; set; }
}

[AutoMap(typeof(Order),
         MemberList = MemberList.Destination,
         ReverseMap = true,
         PreserveReferences = true)]
public class OrderDto
{
    public int Id { get; init; }

    [SourceMember("Customer.FirstName")]
    public string CustomerFirstName { get; init; } = "";

    [SourceMember("Customer.Email")]
    [NullSubstitute("(no email)")]
    public string CustomerEmail { get; init; } = "";

    [Ignore]
    public decimal Total { get; init; }

    public OrderDto? PreviousOrder { get; init; }

    [NullSubstitute("(none)")]
    public string Notes { get; init; } = "";
}

var cfg = new MapperConfiguration(c => c.AddMaps(typeof(OrderDto).Assembly));
var mapper = cfg.CreateMapper();
```

Scanner translation (conceptual):

```csharp
var expr = cfg.CreateMap<Order, OrderDto>(MemberList.Destination);

expr.ForMember(d => d.CustomerFirstName,
               opt => opt.MapFrom<string>(s => s.Customer.FirstName));

expr.ForMember(d => d.CustomerEmail,
               opt => {
                   opt.MapFrom<string?>(s => s.Customer.Email);
                   opt.NullSubstitute<string?>("(no email)");
               });

expr.ForMember(d => d.Total, opt => opt.Ignore());

// PreviousOrder: no member attributes; convention engine resolves
// Order.PreviousOrder → OrderDto.PreviousOrder via the same (Order, OrderDto)
// typemap recursively. PreserveReferences breaks the cycle.

expr.ForMember(d => d.Notes, opt => opt.NullSubstitute<string?>("(none)"));

expr.PreserveReferences();
expr.ReverseMap();   // member-level forward attributes do NOT auto-flip; reverse uses convention only
```

Runtime:

```csharp
var customer = new Customer { FirstName = "Alice", LastName = "Smith", Email = null };
var order = new Order { Id = 1, Customer = customer, RawTotal = 42.0m, Notes = null };
order.PreviousOrder = order;  // self-cycle

var dto = mapper.Map<OrderDto>(order);
// dto.Id == 1
// dto.CustomerFirstName == "Alice"
// dto.CustomerEmail == "(no email)"
// dto.Total == default(decimal)
// dto.PreviousOrder == dto                // PreserveReferences shared the same destination instance
// dto.Notes == "(none)"
```

### 10.3 Example C — mixed mode: attribute DTO + fluent profile + DI

```csharp
[AutoMap(typeof(Customer))]
public class CustomerSummaryDto
{
    [SourceMember("FirstName")] public string GivenName { get; init; } = "";
    [SourceMember("LastName")] public string FamilyName { get; init; } = "";
}

public class OrderProfile : MapperProfile
{
    public OrderProfile()
    {
        // Order → OrderDto needs a computed display name; can't be expressed via attributes.
        CreateMap<Order, OrderDto>()
            .ForMember(d => d.DisplayName,
                       opt => opt.MapFrom(s => $"#{s.Id} for {s.Customer.FirstName} {s.Customer.LastName}"));
    }
}

services.AddAtlas(typeof(OrderProfile).Assembly);
// Discovers OrderProfile (fluent) AND CustomerSummaryDto (attribute) in one scan.
// Both registrations live side-by-side in the same MapperConfiguration.

public class OrderController(IMapper mapper) : ControllerBase
{
    public IActionResult Get(int id) =>
        Ok(new {
            Order = mapper.Map<OrderDto>(GetOrder(id)),                    // fluent map
            Customer = mapper.Map<CustomerSummaryDto>(GetCustomer(id))     // attribute map
        });
}
```

### 10.4 Example D — duplicate-pair conflict (the user must NOT do this)

```csharp
[AutoMap(typeof(Customer))]
public class CustomerSummaryDto { ... }

public class CustomerProfile : MapperProfile
{
    public CustomerProfile()
    {
        CreateMap<Customer, CustomerSummaryDto>();   // ALSO declares the same pair
    }
}

services.AddAtlas(typeof(CustomerProfile).Assembly);
```

Throws at `services.AddAtlas`:

```
AtlasConfigurationException: Configuration errors detected:
  [Customer → CustomerSummaryDto] (register): Type pair (Customer, CustomerSummaryDto)
    is registered twice: CreateMap<Customer, CustomerSummaryDto>() and
    [AutoMap(typeof(Customer))] on CustomerSummaryDto. Pick one — every (TSource, TDestination)
    pair must have a single registration.
```

### 10.5 What the implementer takes away

1. The scanner is a translator; nothing downstream of `cfg.CreateMap(...)` knows attributes exist.
2. Member-level attribute combination on a single property (Example B's `CustomerEmail`) is the most complex codepath — the multi-statement `Block` body in the `ForMember` callback (§5 step 5.4b) is the single most error-prone spot.
3. `PreserveReferences` + `ReverseMap` order in Example B is fixed (PreserveReferences first), but bidirectional propagation handles either order — verify both via tests.
4. Conflict-policy error messages list both registration origins. Don't merge into a generic "duplicate registration" message — both origins are needed for the user to find the second site.

---

## 11. Risks & Open Questions

### 11.1 Known risks (with mitigations)

**R1 — Universal duplicate-pair rule is a behavior change.** Existing v1 behavior on duplicate non-reverse `CreateMap` calls is silent last-write-wins. §6.7 tightens this to throw. Risk: a user upgrading may have unintentionally relied on the silent overwrite.

**Mitigation:**
1. Plan-writing phase greps `tests/` and `src/` for any path that exercises silent-overwrite behavior — confirms zero hits before tightening.
2. Error message names BOTH registration origins so users find and delete the duplicate quickly.
3. Documented prominently in the README delta (§12) and in this design.
4. If the change proves too aggressive in the field, a follow-up doc defines `cfg.AllowMapOverrides = true` opt-in to restore silent-overwrite. Not in v1 — YAGNI.

**R2 — Profile-scope value transformers don't fire on attribute-declared TypeMaps.** Same as DynamicMapping (#10) and OpenGenerics (#9). `OriginatingProfile = null` on attribute-declared TypeMaps. A user registering a profile-scope transformer expecting it to fire on `[AutoMap]`-decorated DTOs will be surprised.

**Mitigation:**
1. Documented in §7.5, README delta, and `MapperProfile.ValueTransformers` xmldoc.
2. Workaround: register the transformer at global scope.
3. Future v3 work: extend `AttributeScanner` with `[AutoMap(Profile = typeof(MyProfile))]` to scope attribute-declared TypeMaps.

**R3 — Member-level attributes silently no-op on non-`[AutoMap]` classes.** §9.2. A user applying `[Ignore]` / `[SourceMember]` / `[NullSubstitute]` on a destination property but forgetting class-level `[AutoMap]` gets no error.

**Mitigation:**
1. `MemberList.Destination` validation (default) catches the consequence: properties the user thinks are ignored show up as unmapped or differently-mapped at `AssertConfigurationIsValid()`.
2. README example shows `[AutoMap]` first; ordering convention is "decorate class, then properties."
3. Defer "lint-style" warning ("class has member attributes but no `[AutoMap]`") to a future doc.

**R4 — Reflection-heavy startup cost for large attribute-decorated assemblies.** §5 resolves `MethodInfo`s once per pair. For 500 attribute-decorated DTOs: ~500 × `MakeGenericType`/`MakeGenericMethod` resolutions + ~500 `Expression.Compile` (one per member-attribute-bearing property). Measurable but smaller than the existing `MapperConfiguration.CompileMappings()` cost.

**Mitigation:**
1. Lift per-pair `MethodInfo` resolution to `ConcurrentDictionary<TypePair, ClosedFluentMethods>` if benchmarks justify.
2. Defer the optimization until a real-world startup profile shows it matters.

**R5 — Test fixture leakage.** Test-fixture types decorated with `[AutoMap]` for one test could leak across tests if the test assembly is re-scanned by other tests' `cfg.AddMaps(typeof(this).Assembly)`.

**Mitigation:**
1. Each test file scopes fixtures to a dedicated namespace (per §13).
2. Tests use `cfg.AddMaps(typeof(SpecificFixture).Assembly)` only when the assembly is known to contain only the expected fixtures.

**R6 — `MakeGenericMethod`/`MakeGenericType` runtime exceptions.** §5's reflection dance is generally safe (controlled inputs), but pointer / by-ref / open-generic property types could slip through.

**Mitigation:**
1. The earlier rejection rules (§6 rules 1, 2; §9 rejection of abstract/interface/static) cover common failure modes.
2. Wrap `MakeGenericMethod` + `Invoke` in `try/catch (ArgumentException ex)` converting to `ConfigurationError` with property name + attribute name + underlying message.

### 11.2 Open questions to flag for the implementer

**O1 — Should `[assembly: AutoMap(typeof(Order), typeof(OrderDto))]` be supported?** AutoMapper has it. **Decision: class-level only for v1.** Documented to revisit in a future doc if user demand surfaces. Implementer must NOT add assembly-level — explicitly out of scope.

**O2 — `[NullSubstitute]` constant value of `0` on `int?` source.** The constructor rejects literal `null` keyword, not zero-equivalents. `[NullSubstitute(0)]` is valid for `int?` — substitutes `0` when source is null. **Decision: correct as designed.**

**O3 — Namespace collision with AutoMapper.** Atlas's attribute names match AutoMapper's. A user with both `using AutoMapper;` and `using Atlas;` on the same file hits ambiguous-reference errors when both attribute types are in scope. **Decision: own the namespace, document the conflict in README.** Atlas's attributes live in `namespace Atlas;`; AutoMapper's live in `namespace AutoMapper;`. The conflict only fires with both `using`s + both attribute types in scope.

**O4 — Should the scanner respect `MapperProfile.AddMaps(asm)`?** `MapperProfile` doesn't expose `AddMaps`. **Decision: configuration-level only for v1.** Profile-level attribute scan adds no value because attribute-declared maps are always configuration-level (never profile-scoped).

### 11.3 Things explicitly NOT design questions for v1

- Implementer must NOT add tier-3 (`[ValueConverter]`) or tier-4 (`[ConvertUsing]`, `[BeforeMap]`, `[AfterMap]`) attributes "while they're in there." Q2 deferred them.
- Implementer must NOT make `[AutoMap]` `Inherited = true`. §9.1 explicitly chose `Inherited = false`.
- Implementer must NOT add `cfg.AddAttributeMaps(asm)` or `scanAttributes: bool`. Q1 chose integrated discovery.
- Implementer must NOT implement source-side `[AutoMapTo(typeof(Dest))]`. Q3 chose destination-only.
- Implementer must NOT change duplicate-pair detection back to silent overwrite. §6.7 chose universal throw.

---

## 12. README Delta and Migration Notes

### 12.1 New README section: "Attribute-based configuration"

Inserted between the "Configuration" overview and the "Reference handling for cycles" section. Approximate length: 70-90 lines. Contents:

- One-line intro: "Decorate destination classes with `[AutoMap(typeof(SourceType))]` to declare mappings without writing a profile. Attributes coexist with profiles; both are discovered by `cfg.AddMaps(asm)` and `services.AddAtlas(asm)`."
- Minimal example (Example A from §10).
- Full feature example (Example B from §10, condensed).
- What attributes can express (§7 summary table).
- What attributes can't express: "Use a fluent profile for `MapFrom(expr)`, `Condition`, `PreCondition`, `BeforeMap`/`AfterMap` lambdas, `ConvertUsing`, `AddTransform`, `Include`/`IncludeBase`, `ForCtorParam`, `ForPath`, and per-value enum overrides."
- Conflict rule: "A `(TSource, TDestination)` pair must be declared exactly once. Declaring the same pair via both an attribute and fluent `CreateMap` throws at config-build naming both registration sites. The same rule now applies to two fluent `CreateMap` calls for the same pair (behavior change in v2 — see Migration notes below)."
- Profile-scope transformer note: "Profile-scope value transformers do NOT fire on attribute-declared TypeMaps (they have no originating profile). Use global-scope transformers or fluent profile-declared maps."

### 12.2 New README section: "Migration notes"

Subsection appended to existing "Configuration":

- v1 → v2 with #12: duplicate `CreateMap` is now an error. Two paragraphs explaining the universal duplicate-pair rule, previous silent last-write-wins, and how to find offending duplicates via the structured exception's two-origin message.
- Suggested migration: "Run existing tests against the new version. If any throw `AtlasConfigurationException` mentioning duplicate registration, the test exposed a latent configuration bug — pick one of the two sites and remove the other."

### 12.3 Deferred-list update (post-merge memory)

`C:\Users\ajsde\.claude\projects\C--Repos-Atlas\memory\atlas_v2_design_docs_deferred.md`:

```
12. ~~Attribute-based configuration as an alternative to fluent.~~ — **shipped** (PR #12 merged at HEAD `<sha>` on <date>; see `docs/Atlas-Design-AttributeConfig.md`). [Full recap to be written post-merge per the established pattern.]
13. Expression translation (`UseAsDataSource` equivalent). ← **next up: #13**
```

### 12.4 MEMORY.md update post-merge

```
- [Atlas v2 deferred features](atlas_v2_design_docs_deferred.md) — 13 feature groups; #1-12 shipped (...AttributeConfig). 1 remains; item #13 (Expression translation) is next.
```

### 12.5 `feedback_atlas_v2_workflow.md` test baseline update

`Test baseline: 634 → ~690-700 after AttributeConfig.`

### 12.6 `feedback_pseudocode_concrete_trace.md` — no anticipated change

The architecture intentionally avoids the bug categories that have bitten before:
- **Bug 4 (cross-package consumer audit):** the design routes attribute → fluent calls so `Atlas.Projections` sees a normal TypeMap. No new consumer to audit.
- **Bug 5 (scope-identifying metadata propagation):** scanner doesn't synthesize new TypeMap fields; `OriginatingProfile = null` set at the only allocation site (the existing `CreateMap` path).
- **Bug 8 (bidirectional propagation when fluent calls reorderable):** `[AutoMap(ReverseMap = true, PreserveReferences = true)]` always emits `.PreserveReferences()` then `.ReverseMap()` in fixed order; bidirectional propagation from PR #11 handles both orderings safely.

If the holistic review surfaces a new bug category, append it post-merge per the established pattern.

### 12.7 Documentation file list (final inventory)

**New files (in PR #12):**
- `docs/Atlas-Design-AttributeConfig.md` — this design doc
- `docs/Atlas-Plan-AttributeConfig.md` — implementation plan (next phase)
- `src/Atlas/AutoMapAttribute.cs`
- `src/Atlas/IgnoreAttribute.cs`
- `src/Atlas/SourceMemberAttribute.cs`
- `src/Atlas/NullSubstituteAttribute.cs`
- `src/Atlas/Internal/AttributeScanner.cs`
- 7 test files in `tests/Atlas.Tests/`
- 1 test file in `tests/Atlas.Projections.Tests/`

**Modified files (in PR #12):**
- `src/Atlas/MapperConfigurationExpression.cs` — add `AttributeScanner.Discover(asm, this)` call inside `AddMaps`; tighten `RegisterTypeMap` duplicate detection to universal throw
- `README.md` — add "Attribute-based configuration" section + "Migration notes"

---

## 13. Testing Strategy

The test layout mirrors v2 feature precedent: one xUnit v3 test file per concern, all using `Assert.X()` only (no FluentAssertions, per `feedback_no_fluentassertions.md`).

### 13.1 Test files (count: 8 new files, ~55-65 net new tests)

**1. `tests/Atlas.Tests/AttributeScannerTests.cs`** — discovery and registration mechanics (~14 tests):
- Decorated class registers via `cfg.AddMaps(asm)`.
- Decorated class registers via `services.AddAtlas(asm)`.
- Multiple `[AutoMap]`-decorated classes in one assembly all register.
- Non-decorated classes don't register.
- Nested types skipped.
- Non-public types skipped.
- `[AutoMap]` on abstract / interface / static rejected.
- `[AutoMap]` on enum rejected.
- `[AutoMap]` on open-generic destination rejected.
- `[AutoMap]` with open-generic source rejected.
- `[AutoMap]` with dynamic-shape source rejected (`Dictionary<string, object>` / `IDictionary<string,object>` / `ExpandoObject`).
- Profile + attribute scan share assembly: profiles register first, attributes second.
- Empty `[AutoMap]` (no member attributes) works as pure-convention map.
- Self-pair `[AutoMap(typeof(SameType))]` on `SameType` works.

**2. `tests/Atlas.Tests/AutoMapAttributeTests.cs`** — class-level behaviors (~7 tests):
- `MemberList = Source` enforced by validator on attribute-derived map.
- `MemberList = Destination` enforced.
- `MemberList = None` skips validation.
- `ReverseMap = true` produces `(Dest, Src)` map with auto-inverted conventions.
- `ReverseMap = true` member-level forward attributes do NOT auto-flip.
- `PreserveReferences = true` on forward propagates to reverse via existing bidirectional propagation.
- `PreserveReferences = true` + `ReverseMap = true` round-trip preserves cycle.

**3. `tests/Atlas.Tests/IgnoreAttributeTests.cs`** — `[Ignore]` member behavior (~4 tests):
- `[Ignore]` excludes property from mapping.
- `[Ignore]` excludes property from `MemberList.Destination` validation.
- `[Ignore]` on a property without class-level `[AutoMap]` is silently no-op.
- `[Ignore]` on update-in-place preserves existing destination value (matches fluent).

**4. `tests/Atlas.Tests/SourceMemberAttributeTests.cs`** — `[SourceMember]` redirection (~8 tests):
- `[SourceMember("OtherName")]` redirects flat member.
- `[SourceMember("Customer.Name")]` resolves dotted source path.
- `[SourceMember("Customer.Address.City")]` resolves multi-level path.
- `[SourceMember("BadPath")]` produces structured error.
- `[SourceMember("X.Y.MissingSegment")]` names failing segment + type it was looked up on.
- `[SourceMember]` redirecting to write-only / private member produces structured error.
- `[SourceMember]` overrides convention (different name from convention's resolution).
- `[SourceMember]` + convention agreement: same effective resolution, attribute marks `IsExplicit`.

**5. `tests/Atlas.Tests/NullSubstituteAttributeTests.cs`** — `[NullSubstitute]` constant behavior (~6 tests):
- `[NullSubstitute("default")]` replaces null on `string` source member.
- `[NullSubstitute(0)]` replaces null on `int?` source member.
- `[NullSubstitute(SomeEnum.Default)]` replaces null on `SomeEnum?` source member.
- `[NullSubstitute(0)]` on non-nullable `int` source rejected (unreachable).
- `[NullSubstitute("hello")]` on `int?` source rejected (type mismatch).
- `[NullSubstitute(typeof(X))]` on Type-typed source member works (Type is a legal attribute arg).

**6. `tests/Atlas.Tests/AttributeFluentInteractionTests.cs`** — Q4 conflict policy + mixed mode (~6 tests):
- Same `(TSource, TDestination)` declared via attribute AND fluent → throws at config-build with both origins named.
- Universal duplicate-pair rule: two fluent `CreateMap` calls for same pair throws (behavior change).
- Universal duplicate-pair rule: profile + scanner attribute on same pair throws.
- Mixed-mode inheritance: attribute base + fluent derived `IncludeBase`; member attributes flow base→derived.
- Attribute pair + global value transformer for matching destination type → transformer fires.
- Attribute pair + profile-scope transformer → transformer does NOT fire.

**7. `tests/Atlas.Tests/AttributeIntegrationTests.cs`** — end-to-end DI + multi-attribute (~6 tests):
- `services.AddAtlas(asm)` discovers attribute DTO + profile in same assembly.
- DI-resolved `IMapper` correctly maps via attribute-declared TypeMap.
- Complex DTO with `[AutoMap(ReverseMap=true, PreserveReferences=true)]` + multi-member attributes: forward and reverse both work end-to-end.
- Multi-attribute on one property: `[SourceMember("X.Y")] [NullSubstitute("def")]` chained — both apply correctly.
- `[Ignore]` + `[SourceMember]` on same property: `[Ignore]` short-circuits.
- Cycle scenario: `[AutoMap(PreserveReferences=true)]` with self-referential property maps a cyclic graph correctly.

**8. `tests/Atlas.Projections.Tests/AttributeProjectionTests.cs`** — IQueryable projection support (~6 tests):
- Attribute-declared TypeMap projects via `query.ProjectTo<DestDto>()`.
- `[Ignore]` member excluded from projection.
- `[SourceMember]` redirects in projection (incl. dotted path → SQL navigation property).
- `[NullSubstitute]` translates to SQL `COALESCE` in projection.
- `[AutoMap(PreserveReferences=true)]` typemap rejected by projection at projection-build (mirrors #11 dual-gate).
- Attribute-declared map with profile-attached hooks (mixed-mode) rejected by projection (existing #5 rule).

**Test baseline projection:** 634 → ~690-700 (≈58 net new tests). No existing tests should regress; the universal-duplicate-pair rule (§6.7) is the only behavior change. A grep of `tests/` for "two `CreateMap` calls on the same pair without `.ReverseMap()`" must be confirmed zero before tightening.

### 13.2 Test fixture conventions

- Top-level public test types in a dedicated namespace (`Atlas.Tests.AttributeFixtures`) so they don't pollute other tests' assembly scans. `cfg.AddMaps(typeof(this).Assembly)` would otherwise discover every test fixture as an attribute candidate, breaking unrelated tests.
- Each test file uses its own fixture types for isolation. No cross-file fixture sharing.
- Fixtures use auto-properties (Atlas convention scans properties only).
- Tests requiring a clean assembly scan use `cfg.AddMaps(typeof(SpecificFixture).Assembly)` only when that assembly is known to contain exactly the expected fixtures.

### 13.3 Coverage targets

- ≥ 90% line + branch on `Atlas.Internal.AttributeScanner`.
- ≥ 85% on the four public attribute classes (low because they're mostly auto-properties with `ArgumentNullException.ThrowIfNull`; branches are trivial).
- Existing v1 thresholds unchanged.

---

## 14. Appendix A — End-to-End Trace of Example B

The single most complex codepath: a property with both `[SourceMember]` and `[NullSubstitute]` on a class with `PreserveReferences = true` and `ReverseMap = true`. Trace is for `OrderDto.CustomerEmail` from Example B.

### 14.1 Inputs to the scanner

```csharp
[AutoMap(typeof(Order), MemberList = MemberList.Destination,
         ReverseMap = true, PreserveReferences = true)]
public class OrderDto
{
    [SourceMember("Customer.Email")]
    [NullSubstitute("(no email)")]
    public string CustomerEmail { get; init; } = "";

    // ... other properties ...
}
```

### 14.2 Scanner trace

```
AttributeScanner.Discover(asm, cfg)
  ├── For type OrderDto:
  │     ├── ValidateAutoMapTarget(OrderDto, autoMapAttr, errors):
  │     │     ├── OrderDto.IsEnum?         → no
  │     │     ├── OrderDto.IsInterface?    → no
  │     │     ├── OrderDto.IsAbstract?     → no
  │     │     ├── OrderDto.IsGeneric?      → no
  │     │     ├── Order.IsGenericTypeDef?  → no
  │     │     ├── Order is dynamic shape?  → no
  │     │     └── pass
  │     │
  │     ├── InvokeCreateMap:
  │     │     CreateMapOpenMethodInfo.MakeGenericMethod(typeof(Order), typeof(OrderDto))
  │     │       .Invoke(cfg, [MemberList.Destination])
  │     │       → expr (typed object, actually IMappingExpression<Order, OrderDto>)
  │     │
  │     ├── Resolve closed-interface MethodInfos:
  │     │     imappingExprClosed = IMappingExpression<Order, OrderDto>
  │     │     forMemberOpen      = imappingExprClosed.GetMethods().Single(...)
  │     │     reverseMapMethod   = imappingExprClosed.GetMethod("ReverseMap", [MemberList])
  │     │     preserveRefsMethod = imappingExprClosed.GetMethod("PreserveReferences", [])
  │     │
  │     ├── ApplyMemberAttributes:
  │     │     For property OrderDto.CustomerEmail (PropertyType = string):
  │     │       memberType         = typeof(string)
  │     │       imemberConfigClosed = IMemberConfigurationExpression<Order, OrderDto, string>
  │     │       optParam            = Parameter(imemberConfigClosed, "opt")
  │     │       statements          = []
  │     │
  │     │       [Ignore] present?    → no, skip
  │     │
  │     │       [SourceMember("Customer.Email")] present? → yes
  │     │         BuildSourcePathExpression(typeof(Order), "Customer.Email", "CustomerEmail", errors, out leaf):
  │     │           segments = ["Customer", "Email"]
  │     │           current  = Parameter(typeof(Order), "s")
  │     │           current  = Property(s, Order.Customer)  // Customer
  │     │           current  = Property(s.Customer, Customer.Email)  // string?
  │     │           leaf = typeof(string?)  // C# nullable string == typeof(string), but we track nullability via NullabilityInfoContext for accurate Expression typing
  │     │           return Lambda<Func<Order, string>>(Property(Property(s, Customer), Email), s)
  │     │             // s => s.Customer.Email
  │     │         sourceMemberType = typeof(string)
  │     │
  │     │         mapFromOpen = imemberConfigClosed.GetMethods().Single(m => m.Name == "MapFrom" && expression overload)
  │     │         mapFromClosed = mapFromOpen.MakeGenericMethod(typeof(string))
  │     │         statements.Add(Call(opt, mapFromClosed, Constant(sourceLambda)))
  │     │
  │     │       [NullSubstitute("(no email)")] present? → yes
  │     │         resolvedSourceType = sourceMemberType (= typeof(string))
  │     │         ValidateNullSubstituteCompatibility(typeof(string), "(no email)", "CustomerEmail", errors):
  │     │           string assignable to string? → yes → return true
  │     │         constantOverloadOpen = imemberConfigClosed.GetMethods().Single(m => m.Name == "NullSubstitute" && constant overload)
  │     │         constantOverloadClosed = constantOverloadOpen.MakeGenericMethod(typeof(string))
  │     │         statements.Add(Call(opt, constantOverloadClosed, Constant("(no email)", typeof(string))))
  │     │
  │     │       body = Block(statements)
  │     │       actionType = Action<IMemberConfigurationExpression<Order, OrderDto, string>>
  │     │       optionsCallback = Lambda(actionType, body, optParam).Compile()
  │     │
  │     │       selector = Lambda<Func<OrderDto, string>>(Property(d, OrderDto.CustomerEmail), d)
  │     │
  │     │       forMemberClosed = forMemberOpen.MakeGenericMethod(typeof(string))
  │     │       forMemberClosed.Invoke(expr, [selector, optionsCallback])
  │     │         // ◄── this is where the existing fluent path takes over
  │     │
  │     ├── ApplyClassLevelFlags:
  │     │     autoMapAttr.PreserveReferences == true:
  │     │       preserveRefsMethod.Invoke(expr, null)
  │     │     autoMapAttr.ReverseMap == true:
  │     │       reverseMapMethod.Invoke(expr, [MemberList.None])
  │     │
  │     └── done with OrderDto
  │
  └── End of types loop. errors.Count == 0, no exception thrown.
```

### 14.3 What the fluent layer does next (downstream of the scanner)

The `forMemberClosed.Invoke(expr, [selector, optionsCallback])` call inside the scanner triggers the existing fluent path:

```
MappingExpression<Order, OrderDto>.ForMember<string>(selector, optionsCallback)
  ├── Resolves PropertyInfo for OrderDto.CustomerEmail from selector
  ├── Looks up or creates PropertyMap for CustomerEmail
  ├── Invokes optionsCallback(memberConfigExpr):
  │     memberConfigExpr.MapFrom<string>(s => s.Customer.Email)
  │       └── PropertyMap.SourcePath = [Customer, Email]; IsExplicit = true
  │     memberConfigExpr.NullSubstitute<string>("(no email)")
  │       └── PropertyMap.NullSubstitute = ConstantExpression("(no email)")
  └── Returns this for chaining (chaining unused — scanner doesn't chain)
```

Then `expr.PreserveReferences()`:

```
MappingExpression<Order, OrderDto>.PreserveReferences()
  ├── _typeMap.PreserveReferences = true
  ├── If _linkedForwardTypeMap != null: linkedForward.PreserveReferences = true (bidirectional)
  └── Return this
```

Then `expr.ReverseMap(MemberList.None)`:

```
MappingExpression<Order, OrderDto>.ReverseMap(MemberList.None)
  ├── If _cachedReverseExpression != null: return it
  ├── reverseTypeMap = new TypeMap(typeof(OrderDto), typeof(Order), MemberList.None)
  │     PreserveReferences = _typeMap.PreserveReferences  // ◄── propagation
  │     RegistrationOrigin = "ReverseMap of (Order, OrderDto)"
  ├── ReverseMapMirror.Mirror(_typeMap, reverseTypeMap)
  │     // Conventions auto-flipped; source-side flattening inverted; member-level
  │     // explicit config NOT auto-flipped (matches fluent semantics).
  ├── _registerTypeMap(reverseTypeMap)
  │     // RegisterTypeMap throws if (OrderDto, Order) is already registered.
  ├── reverseExpression = new MappingExpression<OrderDto, Order>(reverseTypeMap, _registerTypeMap)
  ├── reverseExpression._linkedForwardTypeMap = _typeMap  // ◄── back-pointer for bidirectional
  ├── _cachedReverseExpression = reverseExpression
  └── return reverseExpression
```

The scanner doesn't capture the returned reverse expression — the user has no API to receive it. If they want reverse-side overrides, they must remove the attribute and use a fluent profile (per Q4).

### 14.4 At runtime

```csharp
var mapper = cfg.CreateMapper();
var customer = new Customer { Email = null };
var order = new Order { Id = 1, Customer = customer };
order.PreviousOrder = order;

var dto = mapper.Map<OrderDto>(order);
// IMapper.Map<OrderDto>(order):
//   ├── registry.GetTypeMap((Order, OrderDto)) → typeMap
//   ├── typeMap.PreserveReferences == true → ctx = new MappingContext()
//   ├── delegate.Invoke(order, ctx):
//   │     ├── if (ctx.TryGet(order, OrderDto, out cached)) return cached;  // miss on first call
//   │     ├── dst = new OrderDto();
//   │     ├── ctx.Register(order, OrderDto, dst);  // ◄── pre-population breaks cycles
//   │     ├── dst.Id = order.Id;
//   │     ├── dst.CustomerEmail = order.Customer.Email ?? "(no email)";   // ◄── attribute-derived
//   │     ├── dst.Total stays default (Ignore);
//   │     ├── dst.PreviousOrder = recursive Map<OrderDto>(order.PreviousOrder, ctx)
//   │     │     ├── if (ctx.TryGet(order, OrderDto, out cached)) return cached;  // ◄── HIT (pre-population)
//   │     │     └── return dst (the same dst we're populating)
//   │     ├── dst.Notes = order.Notes ?? "(none)";
//   │     └── return dst
//   └── return dst
```

End-to-end trace confirmed:
- `[SourceMember("Customer.Email")]` → `dst.CustomerEmail = order.Customer.Email`
- `[NullSubstitute("(no email)")]` → `?? "(no email)"`
- `[Ignore]` → `dst.Total` stays default
- `PreserveReferences = true` → `dst.PreviousOrder == dst` (cycle broken)
- `ReverseMap = true` → `(OrderDto, Order)` registered as a separate but propagated-flag pair

---

**End of design.**
