# Atlas v2 — Reverse Mapping & Unflattening

> **Status:** Design approved 2026-05-04. Implementation plan: `Atlas-Plan-ReverseMap.md` (to be written).
> **Spec input:** `Object-Mapping-Functional-Reference.md` §11 (Reverse Mapping & Unflattening), `AutoMapper-Analysis.md` §10.1 (`ReverseMap()` / `ForPath`).
> **Position in v2 roadmap:** Feature #4 of 13 deferred groups. Builds on v1 (`MapperConfiguration`, `MapperProfile`, `IMappingExpression`, `ConventionEngine`), follows ProjectTo (#1), Inheritance (#2), Enum Surface (#3).

---

## 1. Goals & Non-Goals

### 1.1 Goal

Add `.ReverseMap()` to Atlas's fluent surface so a single declaration produces both directions of an entity↔DTO map. Forward conventions and **flattening** (`Customer.Name → CustomerName`) are auto-inverted to **unflattening** (`CustomerName → Customer.Name`) on the reverse direction. Add `.ForPath` so users can target nested destination chains (e.g., `d => d.Customer.Name`) on either direction.

### 1.2 In scope (v2 MVP)

1. New fluent method `ReverseMap(MemberList memberList = MemberList.None)` on `IMappingExpression<TSource, TDestination>`. Returns `IMappingExpression<TDestination, TSource>`. Reverse map defaults to `MemberList.None` (validation off) per the prior-art convention.
2. New fluent method `ForPath<TMember>(Expression<Func<TDestination, TMember>>, Action<...>)` on `IMappingExpression<TSource, TDestination>`. Accepts nested destination chains. Available on both forward and reverse maps. Single-level paths are equivalent to `ForMember`.
3. Internal `ReverseMapMirror` build-pipeline phase that mirrors the forward map's resolved bindings into the reverse map with directions flipped (flattening becomes unflattening). Runs after `ConventionEngine.Apply` and before `InheritanceMerger.Resolve`.
4. Compilation of nested-destination writes with **automatic intermediate instantiation** via public parameterless constructors.
5. Config-time validation: every intermediate type in any `DestinationPath` must have a public parameterless constructor and a public setter on the property used as the chain step. Detect duplicate registration of the same `(D, S)` pair (one via `CreateMap`, one via `.ReverseMap()`) — throw with both registration sites in the message.

### 1.3 Out of scope (deferred to a future v3 design doc)

- Inverting `ForMember(MapFrom(expression))`. The reverse map does not auto-derive an inverse expression for forward `MapFrom` calls. User reconfigures on the reverse expression.
- Auto-propagating `Ignore`. A forward `Ignore` does not silence the corresponding reverse binding.
- Reversing `Include` / `IncludeBase` chains. The reverse map is registered as a flat pair; user manually configures inheritance on the reverse if needed.
- Reversing enum per-value overrides. A forward `MapValue(A1, B1)` does not auto-create a reverse `MapValue(B1, A1)` (could be many-to-one). User reconfigures on the reverse.
- Reversing constructor-parameter mappings.
- Inverse custom converters (`ConvertUsing` is generally not invertible).
- A `ForSourceMember`-style API for source-side ignore (separate gap, unrelated to reverse mapping).

### 1.4 Non-goals (out of scope permanently for this feature)

- Discovering reverse maps by attribute or convention without an explicit `.ReverseMap()` call. The design philosophy throughout Atlas is "explicit configuration wins; implicit behavior is opt-in." `.ReverseMap()` IS the opt-in.
- Bidirectional update-in-place semantics beyond what `Map<S, D>(src, existingDst)` already provides.

---

## 2. Architecture Overview

### 2.1 What changes

- **`IMappingExpression<TSource, TDestination>`** gains two methods: `ReverseMap` and `ForPath`. No existing methods change.
- **`TypeMap`** gains one nullable property: `ReverseMapPair`.
- **`PropertyMap`** gains one nullable property: `DestinationPath`, plus a static factory `ForPath(IReadOnlyList<PropertyInfo>)`.
- **`ExecutionPlanBuilder`** gains a private helper `BuildNestedAssign` that emits a coalesce-and-assign chain when `DestinationPath` is set.
- **`ConfigurationValidator`** gains two new always-on rules: parameterless-ctor on each intermediate, public setter on each chain step.
- **`MapperConfiguration`** wires a new build phase `ReverseMapMirror.Mirror(registry)` between the existing `ConventionEngine.Apply(registry)` and `InheritanceMerger.Resolve(registry)` calls.
- New file: `src\Atlas\Internal\ReverseMapMirror.cs` (internal static class).

### 2.2 Build-time sequence (revised)

The current v1 order in `MapperConfiguration.cs` is `InheritanceMerger.Resolve → ConventionEngine.ResolveMissingMembers → tm.Seal()`. Mirror reads forward maps' RESOLVED bindings (the post-convention state), so it must run AFTER ConventionEngine. Inheritance is not relevant to reverse maps in scope A (Include is not auto-inverted), so Mirror's position relative to InheritanceMerger does not matter for correctness — but placing Mirror AFTER both keeps the rule simple ("Mirror is the last fill-in pass before Seal").

```
1. Profile.Configure() ─ TypeMaps registered; .ReverseMap() registers reverse pair
                         and stores ReverseMapPair on the new TypeMap.
                         ForMember/ForPath/Ignore mark IsExplicit = true.
2. ConfigExpression conflict-guard ─ NEW. At each TypeMap registration into the
                                     ConfigExpression dictionary (the harvest from
                                     profiles AND direct CreateMap calls), detect
                                     duplicate-pair declarations where at least one
                                     side has ReverseMapPair != null. Throw immediately
                                     with both registration origins. v1 last-write-wins
                                     contract preserved when neither side is a reverse.
3. InheritanceMerger.Resolve(typeMaps) ─ unchanged.
4. ConventionEngine.ResolveMissingMembers(tm) ─ unchanged. Populates non-explicit
                                      PropertyMaps on every TypeMap (forward AND
                                      reverse). Discovers direct and source-side
                                      flattening matches as today.
5. ReverseMapMirror.Mirror(typeMaps) ─ NEW. For each TypeMap with non-null
                                       ReverseMapPair, fill remaining unmapped reverse
                                       bindings from the forward map's resolved
                                       PropertyMaps with directions flipped.
6. tm.Seal() for each TypeMap.
7. ConfigurationValidator.Validate(registry, enumValidationEnabled) ─ called explicitly
                                       by the user via AssertConfigurationIsValid().
                                       Extended with intermediate-ctor and
                                       intermediate-setter checks on any PropertyMap
                                       with DestinationPath.
8. CompileMappings() builds delegate cache (lazy or eager).
```

### 2.3 Runtime path

Unchanged at the dispatch level. `IMapper.Map<TDest>(source)` is still a dictionary lookup → cached delegate invoke. The compiled delegate body for a reverse map differs only in that some assignments emit a coalesce-and-assign chain (intermediate auto-init) instead of a direct property assign.

### 2.4 Why a separate Mirror phase rather than extending ConventionEngine

Two architectures were considered:

- **(Recommended)** Mirror-from-forward: the unflattening logic lives in a dedicated phase that runs only on reverse maps. ConventionEngine's responsibility is unchanged (source-side flattening only).
- (Rejected) Extend `ConventionEngine` to also walk destination chains for unflattening. Would affect every map, not just reverse maps. A v1 forward map that previously left a property unresolved (because no source-side chain matched) might suddenly resolve via an unflatten path — silent behavior change. Also expands the convention engine's ambiguity surface (which direction wins when both could match?).

The mirror approach also naturally propagates the user's *choices*: a forward `MapFrom(s => s.X.Y)` translates to an unflatten path on the reverse even where convention couldn't have inferred it. (Currently rejected in scope per §1.3 — `MapFrom` expressions are not mirrored — but the architecture leaves the door open to mirror them in v3 without restructuring.)

---

## 3. Solution & Project Layout

No new project. All additions land in `src\Atlas\` (the core library). Test additions land in `tests\Atlas.Tests\`.

```
src/Atlas/
├── Configuration/
│   ├── IMappingExpression.cs      ← MODIFIED: add ReverseMap + ForPath methods
│   ├── MappingExpression.cs       ← MODIFIED: implement both; cache reverse expr
│   └── ...
├── Internal/
│   ├── PropertyMap.cs             ← MODIFIED: add DestinationPath + ForPath factory
│   ├── TypeMap.cs                 ← MODIFIED: add ReverseMapPair
│   ├── ExecutionPlanBuilder.cs    ← MODIFIED: BuildNestedAssign + DestinationPath route
│   ├── ConfigurationValidator.cs  ← MODIFIED: path-ctor + setter checks; conflict guard
│   ├── ConventionEngine.cs        ← UNCHANGED
│   ├── ReverseMapMirror.cs        ← NEW: mirror algorithm
│   └── ...
├── MapperConfiguration.cs         ← MODIFIED: call ReverseMapMirror.Mirror; conflict-guard hook
└── ...

tests/Atlas.Tests/
├── Internal/
│   ├── PropertyMapDestinationPathTests.cs   ← NEW
│   └── ReverseMapMirrorTests.cs             ← NEW
├── MappingExpressionForPathTests.cs         ← NEW
├── MappingExpressionReverseMapTests.cs      ← NEW
├── ConfigurationValidatorPathTests.cs       ← NEW
├── ExecutionPlanBuilderNestedAssignTests.cs ← NEW
├── ReverseMapConflictTests.cs               ← NEW
└── MapperReverseMapTests.cs                 ← NEW (end-to-end)
```

No NuGet additions. xUnit v3 + built-in `Assert.X()` only (no FluentAssertions, per project convention).

---

## 4. Public API Additions

Two new methods on `IMappingExpression<TSource, TDestination>` and one optional override path. Full XML docs below — these are the source of truth for the implementer.

### 4.1 `ReverseMap`

```csharp
/// <summary>
/// Registers the inverse (TDestination, TSource) map and returns its fluent surface.
/// Conventions and source-side flattening are auto-inverted (so that
/// CustomerName ↔ Customer.Name) via the ReverseMapMirror build phase.
/// </summary>
/// <remarks>
/// What IS auto-inverted on the reverse direction:
/// <list type="bullet">
///   <item>Conventions (PascalCase name match, naming-style toggle).</item>
///   <item>Source-side flattening: forward <c>SourcePath = [Customer, Name]</c> writing
///         <c>dst.CustomerName</c> becomes a reverse binding writing <c>dst.Customer.Name</c>
///         from <c>src.CustomerName</c>.</item>
/// </list>
///
/// What IS NOT auto-inverted (reconfigure on the returned reverse expression if needed):
/// <list type="bullet">
///   <item><c>ForMember(MapFrom(expression))</c> — the forward expression is not inverted.</item>
///   <item><c>Ignore()</c> — does not propagate to the reverse direction.</item>
///   <item><c>ConvertUsing</c> — custom converters generally are not invertible.</item>
///   <item><c>Include</c>/<c>IncludeBase</c> — inheritance chains are not reversed.</item>
///   <item>Enum per-value overrides (<c>MapValue</c>, <c>Ignore(TSource)</c>, <c>WithFallback</c>) —
///         the reverse pair gets default ByValue strategy with no overrides.</item>
///   <item>Constructor parameter mappings (<c>ForCtorParam</c>).</item>
/// </list>
///
/// The reverse map defaults to <see cref="MemberList.None"/> (validation off) because the
/// reverse direction's "complete" expectations rarely match the forward direction's
/// (entities typically carry Id, audit fields, navigation properties not present on the DTO).
/// Pass a different <paramref name="memberList"/> to opt into stricter validation.
///
/// Calling <c>ReverseMap()</c> twice on the same forward map returns the same reverse
/// expression instance (idempotent). The <paramref name="memberList"/> from the FIRST
/// call is locked; calling <c>ReverseMap(MemberList.X)</c> a second time with a different
/// value throws <see cref="AtlasConfigurationException"/>.
/// </remarks>
/// <exception cref="AtlasConfigurationException">
/// Thrown at configuration time (registration of the reverse pair) if a TypeMap for
/// <c>(TDestination, TSource)</c> is also registered elsewhere via <see cref="MapperProfile.CreateMap"/>
/// (or via another forward map's <c>.ReverseMap()</c>). The error message names both
/// registration sites. Symmetric: a <c>CreateMap&lt;TDest, TSource&gt;()</c> after a
/// <c>.ReverseMap()</c> on <c>(TSource, TDest)</c> throws with the same message shape.
/// Also thrown if a second <c>.ReverseMap()</c> call passes a different
/// <paramref name="memberList"/> than the first.
/// </exception>
IMappingExpression<TDestination, TSource> ReverseMap(MemberList memberList = MemberList.None);
```

### 4.2 `ForPath`

```csharp
/// <summary>
/// Configures a binding for a nested destination path (e.g., <c>d => d.Customer.Name</c>).
/// At runtime, intermediates are auto-instantiated via their public parameterless
/// constructor (<c>dst.Customer ??= new Customer(); dst.Customer.Name = ...;</c>).
/// </summary>
/// <remarks>
/// Available on both forward and reverse maps with identical semantics. A single-level
/// path (<c>d => d.Foo</c>) is equivalent to <see cref="ForMember"/> — both methods
/// remove any prior binding for the same target and replace it (last-call-wins).
///
/// Every intermediate type in the path must have a public parameterless constructor
/// AND every intermediate property must have a public setter (the constructor is
/// invoked via <c>Expression.New</c>; the setter is invoked via the assign step in
/// the coalesce-and-assign chain). <see cref="MapperConfiguration.AssertConfigurationIsValid"/>
/// throws naming the offending type and path if either is missing.
///
/// The leaf (last property in the chain) must be a writable property; this is checked
/// by <see cref="MapperConfiguration.AssertConfigurationIsValid"/> and matches the existing
/// <c>ForMember</c> requirement.
/// </remarks>
/// <exception cref="ArgumentException">
/// Thrown at configuration time if <paramref name="destinationPath"/> is not a chain
/// of property accesses — e.g., it contains method calls, indexers, arithmetic, or
/// any non-<see cref="MemberExpression"/> node. The error message shows the offending
/// expression fragment.
/// </exception>
IMappingExpression<TSource, TDestination> ForPath<TMember>(
    Expression<Func<TDestination, TMember>> destinationPath,
    Action<IMemberConfigurationExpression<TSource, TDestination, TMember>> memberOptions);
```

### 4.3 Surface NOT changed

For clarity (these are unchanged, despite the temptation to revisit them as part of this work):

- `MapperConfiguration`, `MapperProfile`, `MapperConfigurationExpression`, `IMapper` — no new methods.
- `IMemberConfigurationExpression<TSource, TDestination, TMember>` — `MapFrom`, `Ignore`, `MapFrom(constant)` apply identically to a path target as to a member target. No `IPathConfigurationExpression` is introduced.
- `ITypeConverter`, `MemberList`, `AtlasConfigurationException`, `AtlasMappingException` — unchanged.
- `ForMember` — unchanged. Continues to require a single-level destination expression. Users who try `ForMember(d => d.Customer.Name, ...)` continue to get the existing "must be a single property access expression" error. (We intentionally do NOT loosen this; the new path semantics live behind `ForPath`.)

### 4.4 Worked-example fluent

```csharp
public class OrderProfile : MapperProfile
{
    public OrderProfile()
    {
        CreateMap<Order, OrderDto>()
            .ForMember(d => d.OrderTotal, opt => opt.MapFrom(s => s.Subtotal + s.Tax))
            .ReverseMap()                              // returns IMappingExpression<OrderDto, Order>
            .ForPath(d => d.Pricing.Total,             // override the unflatten target
                     opt => opt.MapFrom(s => s.OrderTotal))
            .ForMember(d => d.Subtotal, opt => opt.Ignore());
    }
}
```

---

## 5. Internal Architecture

### 5.1 `TypeMap` additions

```csharp
// In src\Atlas\Internal\TypeMap.cs
internal sealed class TypeMap
{
    // ... existing properties ...

    /// <summary>
    /// When this map was created via <c>.ReverseMap()</c> on another map, points back
    /// to that forward pair. Used by <see cref="ReverseMapMirror"/> to know which
    /// forward to read from, AND by the conflict guard in
    /// <see cref="MapperConfigurationExpression"/> to detect duplicate registrations.
    /// Null for maps registered directly via
    /// <see cref="MapperProfile.CreateMap{TSource,TDestination}"/>.
    /// </summary>
    public TypePair? ReverseMapPair { get; set; }

    /// <summary>
    /// Cached reverse <c>MappingExpression</c> instance for idempotent <c>.ReverseMap()</c>
    /// calls. Boxed as <c>object?</c> because the generic args differ from the forward map's.
    /// Set by the first <c>.ReverseMap()</c> call on the corresponding forward
    /// <c>MappingExpression</c>; null on the reverse TypeMap and on TypeMaps that
    /// were never reversed.
    /// </summary>
    public object? CachedReverseExpression { get; set; }

    /// <summary>
    /// Human-readable origin string for diagnostic messages
    /// (<c>"CreateMap&lt;Order, OrderDto&gt;()"</c> or
    /// <c>"CreateMap&lt;Order, OrderDto&gt;().ReverseMap()"</c>). Set at construction in
    /// <see cref="MapperProfile.CreateMap"/>, <see cref="MapperConfigurationExpression.CreateMap"/>,
    /// and <c>MappingExpression.ReverseMap</c>. Empty string for TypeMaps constructed
    /// in tests that don't care about the origin.
    /// </summary>
    public string RegistrationOrigin { get; set; } = string.Empty;
}
```

### 5.2 `PropertyMap` additions

```csharp
// In src\Atlas\Internal\PropertyMap.cs
internal sealed class PropertyMap
{
    // ... existing properties ...

    /// <summary>
    /// Non-null when this binding writes into a nested destination chain (e.g.,
    /// Customer.Name) rather than a single property. The leaf is the writable
    /// target; intermediates are auto-instantiated at runtime via parameterless
    /// constructor. When null, <see cref="DestinationProperty"/> is used —
    /// single-level write, current behavior.
    /// </summary>
    public IReadOnlyList<PropertyInfo>? DestinationPath { get; set; }

    /// <summary>
    /// Factory for nested-path bindings. Produces a PropertyMap whose
    /// <see cref="Name"/> is the dotted path ("Customer.Name") for diagnostics,
    /// whose <see cref="DestinationProperty"/> is the leaf (so existing consumers
    /// like <see cref="ConventionEngine"/> and <see cref="ConfigurationValidator"/>
    /// see a stable "single property" view), and whose <see cref="DestinationPath"/>
    /// carries the full chain.
    /// </summary>
    public static PropertyMap ForPath(IReadOnlyList<PropertyInfo> path)
    {
        if (path is null || path.Count == 0)
            throw new ArgumentException("Path must contain at least one property.", nameof(path));
        var leaf = path[^1];
        var pm = new PropertyMap(string.Join('.', path.Select(p => p.Name)),
                                 leaf.PropertyType, leaf, null);
        pm.DestinationPath = path;
        return pm;
    }
}
```

The leaf is also stored in `DestinationProperty` so existing PropertyMap consumers (validator's "MemberList.Destination" coverage walk, convention engine's "look up by name") continue to work without branch-checking. `ExecutionPlanBuilder` is the one consumer that *does* branch on `DestinationPath != null` — see §7.

### 5.3 `ReverseMapMirror`

```csharp
// NEW: src\Atlas\Internal\ReverseMapMirror.cs
namespace Atlas.Internal;

/// <summary>
/// Mirrors forward TypeMap bindings into reverse TypeMaps. Runs after
/// <see cref="ConventionEngine.Apply"/> and before
/// <see cref="InheritanceMerger.Resolve"/>.
/// </summary>
internal static class ReverseMapMirror
{
    /// <summary>
    /// For every TypeMap with a non-null <see cref="TypeMap.ReverseMapPair"/>:
    /// look up the forward TypeMap, then for each forward PropertyMap that is
    /// eligible for mirroring (see the algorithm in §6) AND not already covered
    /// on the reverse map, create a reverse PropertyMap with directions flipped.
    /// </summary>
    public static void Mirror(MapperRegistry registry);
}
```

### 5.4 `MapperConfiguration` integration

The current v1 build sequence in `MapperConfiguration.cs:39-45` is `InheritanceMerger.Resolve → ConventionEngine.ResolveMissingMembers → tm.Seal`. The new `ReverseMapMirror.Mirror` slots in between Convention and Seal. The conflict guard runs in `MapperConfigurationExpression.RegisterTypeMap` (the harvest step) — it does NOT run inside `MapperConfiguration` constructor.

```csharp
// In src\Atlas\MapperConfiguration.cs (revised constructor body)
public MapperConfiguration(MapperConfigurationExpression expression)
{
    // ... existing setup elided ...

    var typeMaps = expression.GetTypeMaps().ToList();
    var pairIndex = typeMaps.ToDictionary(t => t.Pair);
    bool HasRegisteredMap(Type s, Type d) => pairIndex.ContainsKey(new TypePair(s, d));

    InheritanceMerger.Resolve(typeMaps, pairIndex);

    foreach (var tm in typeMaps)
        ConventionEngine.ResolveMissingMembers(tm, _conventionOptions, HasRegisteredMap);

    ReverseMapMirror.Mirror(typeMaps);          // NEW

    foreach (var tm in typeMaps)
        tm.Seal();

    expression.MarkBuilt();
    _registry = new MapperRegistry(typeMaps, _stringToEnumCache);
}
```

### 5.5 Conflict guard

The conflict guard runs at the harvest step into `MapperConfigurationExpression._typeMaps`. There is no `MapperRegistry` at the time of `.ReverseMap()` call — the registry is constructed inside `MapperConfiguration`'s constructor after all profiles have been processed. So the guard runs at the point where TypeMaps are assembled into the ConfigExpression's dictionary:

```csharp
// In src\Atlas\MapperConfigurationExpression.cs — new private helper
private void RegisterTypeMap(TypeMap newTm)
{
    if (_typeMaps.TryGetValue(newTm.Pair, out var existing))
    {
        var existingIsReverse = existing.ReverseMapPair is not null;
        var newIsReverse = newTm.ReverseMapPair is not null;
        if (existingIsReverse || newIsReverse)
        {
            throw new AtlasConfigurationException(
                $"Type pair ({newTm.SourceType.Name}, {newTm.DestinationType.Name}) is registered twice: " +
                $"{existing.RegistrationOrigin} and {newTm.RegistrationOrigin}. " +
                $"Pick one — either remove the duplicate, or rely solely on .ReverseMap() to produce the inverse.");
        }
        // Otherwise: preserve v1 last-write-wins behavior (silent overwrite).
    }
    _typeMaps[newTm.Pair] = newTm;
}
```

All paths that add to `_typeMaps` are routed through `RegisterTypeMap`:
- `CreateMap<TSource, TDestination>(memberList)` directly on the expression.
- `AddProfile(profile)` — iterates `profile.GetTypeMaps()` and registers each.
- `AddMaps(assemblies)` — same as `AddProfile` after scanning.
- The reverse TypeMap created by `MappingExpression<,>.ReverseMap()` — added via a sink delegate plumbed at construction time (see §6.5).

`TypeMap.RegistrationOrigin` is a new string field set at TypeMap construction:
- `CreateMap<S, D>()` sets `"CreateMap<S, D>()"`.
- `MappingExpression<X, Y>.ReverseMap()` sets `"CreateMap<X, Y>().ReverseMap()"` (uses the forward pair's type names so the user can find it in their profile).

---

## 6. Mirror Algorithm

The mirror phase reads forward TypeMaps and writes into reverse TypeMaps. It is a single pass over `registry.AllTypeMaps()`.

### 6.1 Per-reverse-map algorithm

```
For each tm in registry where tm.ReverseMapPair is not null:
    let forwardPair = tm.ReverseMapPair
    let forward = registry[forwardPair]                  // guaranteed to exist (set at .ReverseMap() time)

    For each fwdPm in forward.PropertyMaps:
        if not IsMirrorEligible(fwdPm): continue          // see §6.2
        let mirrored = FlipBinding(fwdPm)                  // see §6.3
        if mirrored is null: continue                      // could not flip (no readable source on reverse)

        // Skip rule 1: an exact-name binding already exists on the reverse
        // (covers user ForPath(d => d.Customer.Name, ...) and convention duplicates).
        if tm.PropertyMaps any pm with pm.Name == mirrored.Name: continue

        // Skip rule 2 (multi-level only): the user has explicitly mapped the TOP
        // intermediate as a whole (e.g., ForMember(d => d.Customer, opt => opt.MapFrom(...))).
        // Adding Customer.Name and Customer.Email bindings would overwrite the user's
        // wholesale assignment of Customer and is almost never the intent.
        if mirrored.DestinationPath is not null and mirrored.DestinationPath.Count > 1:
            let topName = mirrored.DestinationPath[0].Name
            if tm.PropertyMaps any pm with pm.Name == topName and pm.IsExplicit == true: continue

        tm.PropertyMaps.Add(mirrored)
```

### 6.2 `IsMirrorEligible(fwdPm)` — what gets mirrored

Mirror a forward PropertyMap if and only if:

- `fwdPm.SourcePath is not null` (it has a resolved source-side member chain — convention, flattening, or explicit single-member `ForMember(MapFrom(s => s.X))`), AND
- `fwdPm.Ignored == false`, AND
- `fwdPm.HasConstant == false`, AND
- `fwdPm.CustomExpression == null` (no `MapFrom(expression)` — non-invertible per scope), AND
- `fwdPm.DestinationProperty != null` (skip ctor-param bindings — non-invertible per scope), AND
- `fwdPm.DestinationProperty.CanRead == true` (can read the dest property to use it as the reverse source).

Skip otherwise. Skipped forward bindings simply do not produce reverse bindings — the reverse may end up unmapped on those slots, which `MemberList.None` (default) tolerates.

### 6.3 `FlipBinding(fwdPm)` — how the flip works

```
let fwdSourceChain = fwdPm.SourcePath.Members          // e.g., [Customer, Name]
let fwdDestProp    = fwdPm.DestinationProperty         // e.g., CustomerName

if fwdSourceChain.Count == 1:
    // Single-level forward (direct convention or explicit single-member MapFrom).
    // Reverse is also single-level: dst.<fwdSourceChain[0]> = src.<fwdDestProp>.
    let revDestProp = fwdSourceChain[0]
    if not revDestProp.CanWrite: return null            // forward source is read-only on reverse dest
    let revPm = PropertyMap.ForProperty(revDestProp)
    revPm.SourcePath = new SourceMemberPath(new[] { fwdDestProp })
    return revPm

else:
    // Multi-level forward: forward was flattening, reverse is unflattening.
    // dst.<fwdSourceChain[0]>.<fwdSourceChain[1]>...<fwdSourceChain[^1]> = src.<fwdDestProp>.
    // Each intermediate must satisfy parameterless-ctor + setter — checked by validator (§8), not here.
    let revPath = fwdSourceChain                         // the full chain becomes the reverse DestinationPath
    if not revPath[^1].CanWrite: return null             // can't write the leaf
    let revPm = PropertyMap.ForPath(revPath)
    revPm.SourcePath = new SourceMemberPath(new[] { fwdDestProp })
    return revPm
```

Note: the mirrored reverse `PropertyMap` does NOT have `IsExplicit = true`. It is a derived binding (like a convention binding). User-explicit reverse bindings (set via `ForMember`/`ForPath`/`Ignore` on the reverse expression) win because the algorithm checks `tm.PropertyMaps already contains a binding for mirrored.Name` before adding — and user-explicit bindings are added in step 1 of the build sequence, before the mirror runs.

### 6.4 Conflict-guard algorithm — both ordering cases

The guard runs at `MapperConfigurationExpression.RegisterTypeMap` time (the harvest step). Two cases produce the conflict:

**Case A: `CreateMap<D,S>()` somewhere, then `CreateMap<S,D>().ReverseMap()` somewhere.**
- Step: profile A's `CreateMap<D,S>()` appends `(D, S)` with `ReverseMapPair == null` to profile A's local list.
- Step: profile B's `CreateMap<S,D>()` appends `(S, D)` with `ReverseMapPair == null` to profile B's local list. `.ReverseMap()` then appends `(D, S)` with `ReverseMapPair = (S, D)` to profile B's local list (via the sink — see §6.5).
- Step: ConfigExpression `.AddProfile(profileA)` → `RegisterTypeMap((D, S) {ReverseMapPair=null})` → no existing entry, dict[(D,S)] := this.
- Step: ConfigExpression `.AddProfile(profileB)` → `RegisterTypeMap((S, D) {ReverseMapPair=null})` → no existing entry, dict[(S,D)] := this. → `RegisterTypeMap((D, S) {ReverseMapPair=(S,D)})` → existing entry has `ReverseMapPair == null`, new has `ReverseMapPair != null` → throw.

**Case B: `CreateMap<S,D>().ReverseMap()` somewhere, then `CreateMap<D,S>()` somewhere.**
- Step: ConfigExpression `.AddProfile(profileB)` → `RegisterTypeMap((S, D) {ReverseMapPair=null})` → no existing entry. → `RegisterTypeMap((D, S) {ReverseMapPair=(S,D)})` → no existing entry, dict[(D,S)] := this.
- Step: ConfigExpression `.AddProfile(profileA)` → `RegisterTypeMap((D, S) {ReverseMapPair=null})` → existing entry has `ReverseMapPair != null`, new has `ReverseMapPair == null` → throw.

The guard condition is symmetric: `existingIsReverse || newIsReverse` (i.e., at least one side has `ReverseMapPair != null`). Both being null is the existing v1 "register twice" case — preserved (silent last-write-wins). Both being non-null is a "two reverse maps for the same pair" case — throw with both forward-pair names in the message.

**Within a single profile:** if a profile contains both `CreateMap<D,S>()` AND `CreateMap<S,D>().ReverseMap()`, the conflict is detected when `.AddProfile` harvests the profile's list — the third item triggers the guard. The user sees the same error message regardless of which order they wrote the calls within their profile.

### 6.5 Idempotency of `.ReverseMap()` and the sink

`MappingExpression<TSource, TDestination>` gets a new constructor parameter: an `Action<TypeMap>?` sink. The sink is the function that "appends a TypeMap to whatever collection the parent owns." Profile passes `_typeMaps.Add` (the list's Add); ConfigExpression passes `RegisterTypeMap` (with the conflict guard). When `.ReverseMap()` creates a reverse TypeMap, it invokes the sink to make the parent take ownership.

```csharp
internal sealed class MappingExpression<TSource, TDestination> : IMappingExpression<TSource, TDestination>
{
    public TypeMap TypeMap { get; }
    private readonly Action<TypeMap>? _sink;

    public MappingExpression(TypeMap typeMap, Action<TypeMap>? sink = null)
    {
        TypeMap = typeMap;
        _sink = sink;
    }

    public IMappingExpression<TDestination, TSource> ReverseMap(MemberList memberList = MemberList.None)
    {
        TypeMap.EnsureMutable();

        if (TypeMap.CachedReverseExpression is MappingExpression<TDestination, TSource> existing)
        {
            var existingMemberList = existing.TypeMap.MemberList;
            if (existingMemberList != memberList)
                throw new AtlasConfigurationException(
                    $"ReverseMap on ({typeof(TSource).Name}, {typeof(TDestination).Name}) was previously " +
                    $"called with MemberList.{existingMemberList}; cannot now call with MemberList.{memberList}.");
            return existing;
        }

        if (_sink is null)
            throw new InvalidOperationException(
                "ReverseMap can only be called on a MappingExpression created via MapperProfile.CreateMap " +
                "or MapperConfigurationExpression.CreateMap (which provide a sink for the reverse TypeMap).");

        var reverseTm = new TypeMap(typeof(TDestination), typeof(TSource), memberList)
        {
            ReverseMapPair = TypeMap.Pair,
            RegistrationOrigin = $"CreateMap<{typeof(TSource).Name}, {typeof(TDestination).Name}>().ReverseMap()",
        };
        _sink(reverseTm);

        var reverseExpr = new MappingExpression<TDestination, TSource>(reverseTm, _sink);
        TypeMap.CachedReverseExpression = reverseExpr;
        return reverseExpr;
    }
}
```

**Sink wiring** at construction sites:

```csharp
// MapperProfile.cs
protected IMappingExpression<TSource, TDestination> CreateMap<TSource, TDestination>(MemberList memberList = MemberList.Destination)
{
    var map = new TypeMap(typeof(TSource), typeof(TDestination), memberList)
    {
        RegistrationOrigin = $"CreateMap<{typeof(TSource).Name}, {typeof(TDestination).Name}>()"
    };
    _typeMaps.Add(map);
    return new MappingExpression<TSource, TDestination>(map, _typeMaps.Add);
}

// MapperConfigurationExpression.cs
public IMappingExpression<TSource, TDestination> CreateMap<TSource, TDestination>(MemberList memberList = MemberList.Destination)
{
    EnsureMutable();
    var map = new TypeMap(typeof(TSource), typeof(TDestination), memberList)
    {
        RegistrationOrigin = $"CreateMap<{typeof(TSource).Name}, {typeof(TDestination).Name}>()"
    };
    RegisterTypeMap(map);
    return new MappingExpression<TSource, TDestination>(map, RegisterTypeMap);
}
```

(Existing tests that construct `MappingExpression` directly without a sink continue to work — the sink defaults to `null` and only `.ReverseMap()` requires it. Existing tests don't call `.ReverseMap()`.)

---

## 7. Compilation & Path-Write Algorithm

### 7.1 Routing

`ExecutionPlanBuilder` builds the assignment for each `PropertyMap`. Existing logic produces something like:

```csharp
Expression assign = Expression.Assign(
    Expression.Property(destParam, pm.DestinationProperty!),
    sourceValueExpression);
```

New routing:

```csharp
if (pm.DestinationPath is { } path && path.Count > 1)
    assign = BuildNestedAssign(destParam, path, sourceValueExpression);
else
    assign = Expression.Assign(
        Expression.Property(destParam, pm.DestinationProperty!),
        sourceValueExpression);
```

Single-level paths (`path.Count == 1`) flow through the existing single-property assign. The wrapper exists only to cover the multi-level case.

### 7.2 `BuildNestedAssign`

```csharp
private static Expression BuildNestedAssign(
    Expression destRoot,                       // dst (parameter)
    IReadOnlyList<PropertyInfo> destPath,      // [Customer, Address, City]
    Expression valueExpr)                      // src.CustomerCity (already built)
{
    var statements = new List<Expression>();
    Expression accessSoFar = destRoot;

    // Walk all intermediates (destPath[0..^2]) emitting a coalesce-and-assign per step.
    for (int i = 0; i < destPath.Count - 1; i++)
    {
        var intermediateProp = destPath[i];
        accessSoFar = Expression.Property(accessSoFar, intermediateProp);

        // emit: accessSoFar = accessSoFar ?? new IntermediateType();
        // Validator has already verified parameterless ctor exists; safe to call .GetConstructor(...).
        var ctor = intermediateProp.PropertyType.GetConstructor(Type.EmptyTypes)
            ?? throw new InvalidOperationException(
                $"Internal error: validator should have caught missing parameterless ctor on " +
                $"{intermediateProp.PropertyType.FullName}.");
        var coalesce = Expression.Coalesce(accessSoFar, Expression.New(ctor));
        statements.Add(Expression.Assign(accessSoFar, coalesce));
    }

    // Final step: leaf assign.
    var leafAccess = Expression.Property(accessSoFar, destPath[^1]);
    statements.Add(Expression.Assign(leafAccess, valueExpr));

    return Expression.Block(statements);
}
```

### 7.3 Compiled output — concrete trace

Forward map `Order { Customer { Name }, OrderTotal } → OrderDto { CustomerName, OrderTotal }` reversed. After Mirror:

```
Reverse PropertyMaps:
  [0] DestinationPath=[Customer, Name],  SourcePath=[CustomerName]
  [1] DestinationProperty=OrderTotal,    SourcePath=[OrderTotal]      (single-level convention)
```

Compiled lambda body (pseudocode):

```csharp
(OrderDto src) => {
    var dst = new Order();
    dst.Customer = dst.Customer ?? new Customer();   // intermediate auto-instantiation
    dst.Customer.Name = src.CustomerName;            // leaf assign
    dst.OrderTotal = src.OrderTotal;                 // single-level (existing path)
    return dst;
}
```

For a 3-level path `[Customer, Address, City]`:

```csharp
dst.Customer = dst.Customer ?? new Customer();
dst.Customer.Address = dst.Customer.Address ?? new Address();
dst.Customer.Address.City = src.CustomerCity;
```

### 7.4 Performance note

When two reverse bindings share a prefix (`Customer.Name` and `Customer.Email`), the compiled output emits the `dst.Customer ??= new Customer();` step twice — once per binding. The second is a no-op at runtime (Coalesce returns the existing non-null value). A future micro-optimization could deduplicate per-prefix emit; not in MVP. The benchmark harness should include a 2-binding-shared-prefix case so future optimization can be measured.

---

## 8. TDD Plan

Roughly 10 implementation tasks, ~45 new tests. Same TDD-first cadence as Inheritance / Enum. **Test files** (under `tests/Atlas.Tests/`):

### 8.1 `Internal/PropertyMapDestinationPathTests.cs` — 3 tests

- `ForPath_StoresPathAndLeafInDestinationProperty`
- `ForPath_NameIsDottedJoin`
- `ForPath_EmptyPath_Throws`

### 8.2 `MappingExpressionForPathTests.cs` — 6 tests

- `ForPath_SingleLevel_EquivalentToForMember`
- `ForPath_TwoLevelChain_StoresFullPath`
- `ForPath_ThreeLevelChain_StoresFullPath`
- `ForPath_MethodCallInChain_Throws`
- `ForPath_ArithmeticInChain_Throws`
- `ForPath_LastCallWins_OnSamePath`

### 8.3 `ExecutionPlanBuilderNestedAssignTests.cs` — 4 tests

- `BuildNestedAssign_SingleLevel_NoCoalesceEmitted`
- `BuildNestedAssign_TwoLevel_EmitsCoalesceThenAssign`
- `BuildNestedAssign_ThreeLevel_EmitsTwoCoalescesThenAssign`
- `BuildNestedAssign_PreservesValueExpression`

### 8.4 `ConfigurationValidatorPathTests.cs` — 5 tests

- `Validate_IntermediateMissingParameterlessCtor_Throws_NamingPath`
- `Validate_IntermediateMissingSetter_Throws_NamingProperty`
- `Validate_LeafMissingSetter_Throws`
- `Validate_AllValid_ReturnsCleanly`
- `Validate_MultiLevel_AllValid_ReturnsCleanly`

### 8.5 `MappingExpressionReverseMapTests.cs` — 6 tests

- `ReverseMap_ReturnsExpression_OfReverseGenericArgs`
- `ReverseMap_DefaultMemberListIsNone`
- `ReverseMap_ExplicitMemberListHonoured`
- `ReverseMap_CalledTwice_ReturnsSameInstance`
- `ReverseMap_TwoCallsWithDifferentMemberList_Throws`
- `ReverseMap_RegistersTypeMapAndChainsForMember`

### 8.6 `ReverseMapConflictTests.cs` — 4 tests

- `CreateDestSrc_ThenReverseMapOnSrcDest_Throws_NamingBothSites`
- `ReverseMapOnSrcDest_ThenCreateDestSrc_Throws_NamingBothSites`
- `ReverseMapTwiceOnSameMap_DoesNotThrow`
- `TwoProfilesEachReversingTheSamePair_Throws`

### 8.7 `Internal/ReverseMapMirrorTests.cs` — 10 tests

- `Mirror_SingleLevelConvention_FlipsToSingleLevelOnReverse`
- `Mirror_TwoLevelChain_FlipsToUnflattenPath`
- `Mirror_ThreeLevelChain_FlipsToThreeLevelUnflattenPath`
- `Mirror_ReverseExplicitBinding_NotOverwritten` (skip-rule-1 — exact-name)
- `Mirror_UserExplicitTopLevelBinding_SuppressesMultiLevelMirror` (skip-rule-2 — top-intermediate user-explicit)
- `Mirror_ForwardIgnored_NotMirrored`
- `Mirror_ForwardCustomExpression_NotMirrored`
- `Mirror_ForwardConstant_NotMirrored`
- `Mirror_ForwardDestPropertyNoGetter_NotMirrored`
- `Mirror_ForwardSourceLeafNoSetterOnReverseDest_NotMirrored`

### 8.8 `MapperReverseMapTests.cs` — 8 end-to-end tests

- `RoundTrip_OrderDtoToOrder_FlattenedThenUnflattened`
- `Reverse_UnflatteningWritesNestedIntermediate`
- `Reverse_ThreeLevelUnflattenWorks`
- `Reverse_ForPathOverride_BeatsMirroredBinding`
- `Reverse_IgnoreOnReverse_Honoured`
- `Reverse_MemberListDestination_TriggersValidationErrors`
- `Reverse_TwoLevelChain_PreservesValueViaRoundTrip`
- `Reverse_UpdateInPlace_Map_S_D_ExistingDest_Works`

**Implementation tasks (commit-by-commit):**

| # | Task | Tests | Model |
|---|---|---|---|
| 1 | Data model: `TypeMap.ReverseMapPair`, `TypeMap.CachedReverseExpression`, `PropertyMap.DestinationPath`, `PropertyMap.ForPath` factory | 3 | haiku |
| 2 | `ForPath` fluent surface on `IMappingExpression`/`MappingExpression`; `ExtractPath` helper walks `MemberExpression` chains | 6 | sonnet |
| 3 | `ExecutionPlanBuilder.BuildNestedAssign` + `DestinationPath` route | 4 | sonnet |
| 4 | `ConfigurationValidator` path-ctor + setter checks | 5 | sonnet |
| 5 | `ReverseMap` fluent surface; cache reverse expression on forward TypeMap; idempotent | 6 | sonnet |
| 6 | Conflict guard at `MapperConfigurationExpression.RegisterTypeMap`; `RegistrationOrigin` field on TypeMap | 4 | haiku |
| 7 | `Internal/ReverseMapMirror.cs` — Mirror algorithm covering all skip conditions | 10 | sonnet |
| 8 | Wire `ReverseMapMirror.Mirror` into `MapperConfiguration` build sequence | 0 | haiku |
| 9 | End-to-end `MapperReverseMapTests` (round-trip, unflattening, ForPath override, MemberList interactions) | 8 | sonnet |
| 10 | README + design-doc cleanup; coverage check | 0 | haiku |

**Total: ~46 new tests.** Baseline 273 → ~319 after this lands. Coverage targets carried forward: line ≥ 90%, branch ≥ 80% on `Atlas` core.

---

## 9. Risks & Open Questions

### 9.1 Things to trace concretely during plan-writing (per pseudocode-trace memory)

1. **Mirror ordering in the build sequence.** The current v1 order in `MapperConfiguration.cs:39-45` is `InheritanceMerger.Resolve → ConventionEngine.ResolveMissingMembers → tm.Seal()`. Mirror reads forward maps' RESOLVED bindings (the post-convention state), so it must run AFTER `ConventionEngine.ResolveMissingMembers`. In scope A, `Include` is not auto-inverted, so reverse maps have no `IncludedDerived`/`IncludedBases` relations — `InheritanceMerger.Resolve` is effectively a no-op on reverse maps and Mirror's position relative to it does not matter for correctness. The plan places Mirror AFTER both Inheritance and Convention, just before Seal: simplest rule, no surprises. Concrete trace required: a 2-level forward inheritance chain (Base→BaseDto, Derived→DerivedDto with Derived map having .ReverseMap()) — verify the Derived reverse gets only Derived's flattening bindings mirrored, not Base's (because Mirror reads `forward.PropertyMaps` which after Inheritance+Convention contains both base-inherited and derived-explicit bindings — that's actually what we WANT, but the trace needs to confirm the test assertion matches reality).

2. **Conflict-guard for both ordering cases.** Pseudocode in §6.4 enumerates Case A (CreateMap-then-ReverseMap) and Case B (ReverseMap-then-CreateMap). The plan must trace BOTH cases through `MapperConfigurationExpression.RegisterTypeMap` to verify the symmetric condition `existingIsReverse || newIsReverse` triggers both. ALSO trace the within-single-profile case: a profile that contains both `CreateMap<D,S>()` and `CreateMap<S,D>().ReverseMap()` — the harvest into ConfigExpression detects the conflict at the third item (the reverse-pair add).

3. **Idempotency cache — different MemberList second call.** §6.5 shows the throw on different MemberList. Trace required: first call `.ReverseMap()` (default `None`), second call `.ReverseMap(MemberList.Destination)` — must throw with both MemberLists in the message. If the second call passes the same MemberList as the first, return the cached expression silently.

4. **DestinationPath leaf vs DestinationProperty semantics for `MemberList.Destination`.** The validator's "every dest member covered" check on `MemberList.Destination` walks the destination type's properties. For a path `Customer.Name`, the binding's `DestinationProperty` is `Name` (the leaf), but the destination type the validator is walking is `OrderDto` whose top-level property is `Customer`. The plan must specify: a `DestinationPath` binding counts as covering `path[0]` (the TOP intermediate property) for `MemberList.Destination` purposes — the user's intent is "I'm writing into Customer." Concretely, the validator's coverage walk needs to look at `pm.DestinationPath?[0] ?? pm.DestinationProperty` when computing what's covered.

5. **ConventionEngine running on the reverse pre-mirror.** Step 2 (ConventionEngine) runs on ALL TypeMaps including reverse. ConventionEngine populates non-explicit bindings — for the reverse, this picks up direct/single-level matches like `OrderTotal → OrderTotal`. Then mirror fills the gaps. The skip-rule-1 in §6.1 (`pm.Name == mirrored.Name`) handles this: convention-resolved single-level matches survive and mirror does not double-write. Concrete trace: forward `Order → OrderDto` with `OrderTotal` as a direct convention match AND `Customer.Name → CustomerName` as flattening. After ConventionEngine, reverse has `[OrderTotal direct]`; after Mirror, reverse has `[OrderTotal direct, Customer.Name from CustomerName via mirror]`. Verify mirror does not double-write OrderTotal.

6. **Mirror does not overwrite user-explicit top-level bindings.** Skip-rule-2 in §6.1 handles `ReverseMap().ForMember(d => d.Customer, opt => opt.MapFrom(s => GetCustomerById(s.Id)))` — the user wholesale-replaced Customer and does not want mirror to add `Customer.Name`/`Customer.Email` bindings that would overwrite Name/Email on the customer they just constructed. Concrete trace required: forward Order→OrderDto with two flattening bindings and a reverse `ForMember(d => d.Customer, ...)`. Verify mirror skips both `Customer.Name` and `Customer.Email` because skip-rule-2 finds a user-explicit binding for `Customer`. The skip rule does NOT fire if the top-level binding is convention-resolved (only IsExplicit==true triggers the skip), so a future case where convention happens to resolve the top intermediate doesn't accidentally suppress mirror.

7. **Foot-gun guard placement.** The intermediate parameterless-ctor check fires in `ConfigurationValidator.Validate`, which is called by `AssertConfigurationIsValid()`. But `CompileMappings()` calls `Expression.New(ctor)` unconditionally — if the user did not call `AssertConfigurationIsValid()`, compilation throws an `InvalidOperationException` from `BuildNestedAssign`'s null-check fallback (see §7.2) rather than a clear validator message. The plan should add the path-ctor check INSIDE the always-on portion of `ConfigurationValidator.Validate` (not gated on `enumValidationEnabled`), AND keep the defense-in-depth `InvalidOperationException` in `BuildNestedAssign` so even users who skip validation get a comprehensible runtime error rather than an opaque ExpressionTree exception.

8. **Reverse map's `MemberList.Destination` and the DestinationPath leaf-vs-top question.** Following from (4): if the user opts a reverse map into `MemberList.Destination`, what counts as covered by the unflattening bindings? Must trace concretely: reverse map of `OrderDto → Order`, MemberList=Destination, Order has `{ Id, Customer, Subtotal, Tax }`, mirror produced `{ Customer.Name <- CustomerName, Customer.Email <- CustomerEmail }`. Coverage walk should consider `Customer` covered (because `path[0] == Customer`), Id/Subtotal/Tax unmapped — three errors. Without this rule, ALL of Order's members would show as unmapped including `Customer` — the user would see a confusing "Customer is unmapped" error even though they're writing into it.

### 9.2 Explicitly deferred to v3

- Inverting `ForMember(MapFrom(expr))` for invertible expressions.
- Auto-propagating `Ignore`.
- Reversing `Include` / `IncludeBase` chains.
- Reversing enum per-value overrides.
- Reversing ctor-param mappings.
- Reversing `ConvertUsing`.
- A `ForSourceMember` API for source-side ignore.
- Per-prefix deduplication of intermediate `??=` emit (perf micro-optimization).

### 9.3 Open questions for the implementing session to push back on

- **v1 duplicate-pair behavior — verified.** Reading `MapperConfigurationExpression.cs:34` confirms v1 is last-write-wins: `_typeMaps[map.Pair] = map; // last call wins`. The new conflict guard preserves this for the `existingIsReverse == false && newIsReverse == false` case (silent overwrite) and adds the throw only when at least one side is a reverse-mapping. No behavior change for v1 users who don't touch `.ReverseMap()`.

---

## 10. Appendix A — Worked Example

### 10.1 User code

```csharp
public class Customer { public string Name { get; set; } public string Email { get; set; } }
public class Order { public int Id { get; set; } public Customer Customer { get; set; } public decimal Subtotal { get; set; } public decimal Tax { get; set; } }
public class OrderDto { public string CustomerName { get; set; } public string CustomerEmail { get; set; } public decimal OrderTotal { get; set; } }

public class OrderProfile : MapperProfile
{
    public OrderProfile()
    {
        CreateMap<Order, OrderDto>()
            .ForMember(d => d.OrderTotal, opt => opt.MapFrom(s => s.Subtotal + s.Tax))
            .ReverseMap();
    }
}
```

### 10.2 Build trace

**Step 1 — Profile.Configure().** Forward `(Order, OrderDto)` TypeMap registered:

```
PropertyMaps:
  OrderTotal: { IsExplicit=true, CustomExpression={ s => s.Subtotal + s.Tax } }
```

`.ReverseMap()` registers reverse `(OrderDto, Order)` TypeMap with `MemberList=None`, `ReverseMapPair=(Order, OrderDto)`. Returns reverse `MappingExpression` (cached on forward TypeMap). Reverse PropertyMaps: empty.

**Step 2 — ConventionEngine.Apply.** Forward map: discovers `CustomerName → [Customer, Name]` (recursive PascalCase flattening), `CustomerEmail → [Customer, Email]`. Forward PropertyMaps now:

```
  OrderTotal:    { IsExplicit=true,  CustomExpression={ s => s.Subtotal + s.Tax } }
  CustomerName:  { IsExplicit=false, SourcePath=[Customer, Name] }
  CustomerEmail: { IsExplicit=false, SourcePath=[Customer, Email] }
```

Reverse map: ConventionEngine looks for source matches for each Order property — `Id`, `Customer`, `Subtotal`, `Tax`. None of them have direct or flattening matches on `OrderDto`. Reverse PropertyMaps stay empty.

**Step 3 — ReverseMapMirror.Mirror.** Iterates forward PropertyMaps:

- `OrderTotal` — has `CustomExpression`, skip per §6.2.
- `CustomerName` — eligible. `SourcePath.Count == 2`, so flip via §6.3 multi-level branch. Reverse binding: `{ DestinationPath=[Customer, Name], SourcePath=[CustomerName] }`. Reverse map does not contain a binding for `"Customer.Name"` — added.
- `CustomerEmail` — same as above. Reverse binding: `{ DestinationPath=[Customer, Email], SourcePath=[CustomerEmail] }`. Added.

Reverse PropertyMaps now:

```
  Customer.Name:  { DestinationPath=[Customer, Name],  SourcePath=[CustomerName] }
  Customer.Email: { DestinationPath=[Customer, Email], SourcePath=[CustomerEmail] }
```

`Id`, `Subtotal`, `Tax` remain unmapped on the reverse — fine because `MemberList.None`.

**Step 4 — InheritanceMerger.Resolve (runs before Convention in v1; shown here in execution order for clarity).** No `Include` calls; no-op. (Steps 2+3 above describe Convention and Mirror — Inheritance actually ran BEFORE them; the section is laid out logically here, not chronologically. See §2.2 for the actual chronological order.)

**Step 5 — ConfigurationValidator.Validate** (called by `AssertConfigurationIsValid()`).

- Forward map (`MemberList.Destination`): every OrderDto member covered. Pass.
- Reverse map (`MemberList.None`): no coverage check.
- Path validation on reverse: intermediate `Customer` requires public parameterless ctor. `Customer` IS a class with default ctor (not a record with positional params, in this example) — pass. `Customer.Name` and `Customer.Email` setters exist — pass.

(If `Customer` had been declared as `public record Customer(string Name, string Email)` with no parameterless ctor, validation would fail at this point with: `"Cannot unflatten src.CustomerName → dst.Customer.Name: intermediate type Customer has no public parameterless constructor."` The user fix is to either add a parameterless ctor to `Customer` or pre-populate `entity.Customer` outside the mapper.)

**Step 6 — Compile.** Cached delegates:

```csharp
// Forward (Order → OrderDto)
(Order src) => new OrderDto {
    OrderTotal    = src.Subtotal + src.Tax,
    CustomerName  = src.Customer.Name,
    CustomerEmail = src.Customer.Email,
};

// Reverse (OrderDto → Order)
(OrderDto src) => {
    var dst = new Order();
    dst.Customer = dst.Customer ?? new Customer();
    dst.Customer.Name = src.CustomerName;
    dst.Customer = dst.Customer ?? new Customer();   // emitted again per binding; harmless
    dst.Customer.Email = src.CustomerEmail;
    return dst;
};
```

### 10.3 Runtime use

```csharp
var entity = new Order { Id = 7, Customer = new Customer { Name = "Alice", Email = "a@x" }, Subtotal = 90m, Tax = 10m };
var dto = mapper.Map<OrderDto>(entity);          // { CustomerName="Alice", CustomerEmail="a@x", OrderTotal=100 }
var roundTripped = mapper.Map<Order>(dto);        // { Id=0, Customer={ "Alice", "a@x" }, Subtotal=0, Tax=0 }
```

Note that `Id`, `Subtotal`, `Tax` are zero on the round-trip because the reverse direction does not map them — `OrderDto` doesn't carry that data. The user can opt into stricter reverse validation via `.ReverseMap(MemberList.Destination)` if they want to be told about the gap; they can configure overrides on the reverse expression to populate from elsewhere (e.g., `ForMember(d => d.Id, opt => opt.Ignore())` to silence specific properties).

---

## 11. Implementation Checklist

For the implementing Claude session. Each row is a self-contained commit.

- [ ] **Task 1 — Data model.** Add `TypeMap.ReverseMapPair`, `TypeMap.CachedReverseExpression`, `PropertyMap.DestinationPath`, `PropertyMap.ForPath` factory. Tests: `Internal/PropertyMapDestinationPathTests.cs` (3 tests).
- [ ] **Task 2 — `ForPath` fluent surface.** Add `ForPath` to `IMappingExpression`/`MappingExpression`; new `ExtractPath` helper walks `MemberExpression` chains. Tests: `MappingExpressionForPathTests.cs` (6 tests).
- [ ] **Task 3 — Compile nested writes.** Extend `ExecutionPlanBuilder` with `BuildNestedAssign`; route via `DestinationPath`. Tests: `ExecutionPlanBuilderNestedAssignTests.cs` (4 tests).
- [ ] **Task 4 — Validator path guards.** Intermediate parameterless-ctor + setter checks in `ConfigurationValidator`. Tests: `ConfigurationValidatorPathTests.cs` (5 tests).
- [ ] **Task 5 — `ReverseMap` fluent surface.** Add `ReverseMap(MemberList = None)` to `IMappingExpression`/`MappingExpression`; cache reverse expression on forward TypeMap; idempotent with same-MemberList check. Plumb `_registry` into `MappingExpression` constructor. Tests: `MappingExpressionReverseMapTests.cs` (6 tests).
- [ ] **Task 6 — Conflict guard.** At `MapperRegistry.Register`, detect duplicate-pair declarations per §6.4. Add `DescribeOrigin` helper. Tests: `ReverseMapConflictTests.cs` (4 tests).
- [ ] **Task 7 — `ReverseMapMirror`.** New `Internal/ReverseMapMirror.cs`; mirror algorithm covering all skip conditions. Tests: `Internal/ReverseMapMirrorTests.cs` (10 tests).
- [ ] **Task 8 — Wire into build sequence.** Insert `ReverseMapMirror.Mirror` between `ConventionEngine.Apply` and `InheritanceMerger.Resolve` in `MapperConfiguration`. Verify integration tests from Task 7 still pass.
- [ ] **Task 9 — End-to-end MapperReverseMap.** Round-trip, unflattening, multi-level intermediates, ForPath override, MemberList interactions. Tests: `MapperReverseMapTests.cs` (8 tests).
- [ ] **Task 10 — README + coverage.** Add a `## Reverse mapping` section with the worked example; remove "Reverse mapping & unflattening" from the deferred list; verify line ≥ 90% / branch ≥ 80% on `Atlas` core.

**Final holistic review** by `superpowers:code-reviewer` over the whole branch before merge — per the established workflow rhythm.
