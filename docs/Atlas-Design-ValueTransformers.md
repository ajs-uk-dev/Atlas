# Atlas v2 — Value Transformers

> **Status:** Design approved 2026-05-04. Implementation plan: `Atlas-Plan-ValueTransformers.md` (to be written next).
> **Spec inputs:** `Object-Mapping-Functional-Reference.md` §6.2 (Value transformers), `AutoMapper-Analysis.md` §5.7 (`AddTransform<T>`).
> **Position in v2 roadmap:** Feature #6 of 13 deferred groups. Builds on v1 + ProjectTo (#1) + Inheritance (#2) + Enum (#3) + Reverse Mapping (#4) + Hooks (#5).

---

## 1. Goals & Non-Goals

### 1.1 Goal

Add value transformers to Atlas: post-processing functions per type, applied at three scopes — **global** on `MapperConfigurationExpression`, **profile** on `MapperProfile`, **type-map** on `IMappingExpression`. Composition is broad-first: `global → profile → type-map`. Within each scope, multiple transformers run in registration order (FIFO).

The API takes `Expression<Func<T, T>>` so the same registration works for both in-memory `Map<>()` (compiled to a delegate) and `query.ProjectTo<>()` (inlined into the projection lambda for SQL translation by the underlying provider). The headline use case from the functional reference — "a global `string => string.Trim()` transformer can be added once and apply everywhere a string is mapped, including in queryable projections" — works under this design.

### 1.2 In scope (v2 MVP)

1. New public class `ValueTransformerCollection` (a typed registry with `Add<T>(Expression<Func<T, T>>)` method).
2. Three new registry endpoints:
   - `MapperConfigurationExpression.ValueTransformers` (global scope).
   - `MapperProfile.ValueTransformers` (profile scope).
   - `IMappingExpression<TSource, TDestination>.AddTransform<T>(Expression<Func<T, T>>)` (type-map scope).
3. New internal `HookEntry`-style data on `TypeMap`:
   - `TypeMapTransformers: Dictionary<Type, List<LambdaExpression>>` — populated by `AddTransform`.
   - `EffectiveTransformers: Dictionary<Type, IReadOnlyList<LambdaExpression>>` — populated by `TransformerResolver` at config-build time, composed broad-first.
   - `OriginatingProfile: MapperProfile?` — backref so `TransformerResolver` knows which profile's transformers apply.
4. New internal static class `TransformerResolver`: walks global ∪ profile ∪ type-map per TypeMap and produces the effective dictionary. Runs in `MapperConfiguration` ctor between `ReverseMapMirror.Mirror` and `tm.Seal()`.
5. `ExecutionPlanBuilder` extension: when emitting each property assign in `BuildPocoLambda` and `BuildUpdate`, wrap the source-side expression with composed `Expression`-tree calls (via parameter substitution, NOT `Expression.Invoke` — required for projection translatability).
6. `Atlas.Projections.ProjectionPlanBuilder` extension: same wrap logic applied to projection bindings so transformers translate to SQL via the LINQ provider.

### 1.3 Out of scope (deferred to a future v3 design doc)

- **Member-level scope** (a dedicated `opt.AddTransform<T>(...)` API on `IMemberConfigurationExpression`). Users can already achieve per-member transformations via `ForMember(opt.MapFrom(s => transform(s.X)))`.
- **DI integration via `IValueTransformer<T>` interface.** Lambda-only for v2. If a real user need surfaces, an interface variant can be added later with the same `ActivatorUtilities`-from-root-SP pattern as Hooks (feature #5).
- **Reverse-map auto-propagation of transformers.** Per scope-A discipline: transformers do NOT auto-flip via `.ReverseMap()`. User reconfigures on the reverse expression.
- **Inheritance propagation of transformers (base → derived).** Each TypeMap declares its own type-map-level transformers. The profile-level scope already handles "applies to everything in this profile"; inheriting type-map-level transformers would compose unpredictably with profile-level.
- **Translatability inspection / friendly rejection in ProjectTo.** Untranslatable lambdas fail at query-execution time with the LINQ provider's standard "expression cannot be translated" error. Atlas does not pre-inspect lambdas (would essentially reimplement EF Core's expression visitor).
- **Type matching beyond exact equality.** `Add<string>` matches `string` destinations only, not `object` or other assignable types. For value types, `Add<int>` matches `int` only — register `Add<int?>` separately if needed for nullable destinations.

### 1.4 Non-goals (out of scope permanently for this feature)

- Discovering transformers by attribute or convention without an explicit `Add<T>` call. Transformers are opt-in.
- Per-call (per `Map<>` invocation) transformer overrides.
- Transformers that depend on the SOURCE value (`Func<TSource, T, T>`). Transformers see only the destination value (post-resolution, pre-assign).

---

## 2. Architecture Overview

### 2.1 What changes

- **`ValueTransformerCollection`** — new public class in `src/Atlas/`.
- **`MapperConfigurationExpression`** gains a `ValueTransformers` property (one collection instance per config).
- **`MapperProfile`** gains a `ValueTransformers` property (one collection instance per profile).
- **`IMappingExpression<,>`** gains `AddTransform<T>(Expression<Func<T, T>>)`.
- **`TypeMap`** gains three fields: `TypeMapTransformers`, `EffectiveTransformers`, `OriginatingProfile`.
- **`MapperProfile.CreateMap`** sets `tm.OriginatingProfile = this` on each new TypeMap.
- **`TransformerResolver`** — new internal static class. Composes the effective dictionary per TypeMap.
- **`ExecutionPlanBuilder`** gains `WrapWithTransformers` helper. `BuildPocoLambda` and `BuildUpdate` route property + ctor-arg assignments through it.
- **`Atlas.Projections.ProjectionPlanBuilder`** gains the same wrap logic (separate implementation in the projection package — see §4.6).
- **`MapperConfiguration`** wires `TransformerResolver.Resolve(typeMaps, expression.ValueTransformers)` between `ReverseMapMirror.Mirror` and `tm.Seal()`.

### 2.2 Build-time sequence (revised, NEW step in **bold**)

The current order (after features #1-5) is `InheritanceMerger.Resolve → ConventionEngine.ResolveMissingMembers → ReverseMapMirror.Mirror → tm.Seal()`. `TransformerResolver` slots in just before `tm.Seal()`:

```
1. Profile.Configure() — TypeMaps registered;
                         per-typemap AddTransform appends to TypeMap.TypeMapTransformers;
                         per-profile transformers stored on MapperProfile.ValueTransformers;
                         MapperProfile.CreateMap sets tm.OriginatingProfile = this.
2. ConfigExpression conflict-guard (unchanged from feature #4).
3. AddProfile harvests profile maps into ConfigExpression — tm.OriginatingProfile already set.
4. InheritanceMerger.Resolve(typeMaps) — unchanged (does NOT propagate transformers).
5. ConventionEngine.ResolveMissingMembers(tm) — unchanged.
6. ReverseMapMirror.Mirror(typeMaps) — unchanged (does NOT propagate transformers).
7. TransformerResolver.Resolve(typeMaps, expression.ValueTransformers) — NEW.
   For each TypeMap, compose EffectiveTransformers from global ∪ profile ∪ type-map.
8. tm.Seal() for each TypeMap.
9. (On AssertConfigurationIsValid) ConfigurationValidator.Validate — unchanged (transformers are
   opaque to validation; lambda translatability is a runtime concern in ProjectTo).
10. CompileMappings — codegen reads EffectiveTransformers and wraps source-side expressions.
```

### 2.3 Runtime path

Unchanged at the dispatch level. `IMapper.Map<TDest>(source)` is still a dictionary lookup → cached delegate invoke. The compiled delegate body for a TypeMap with transformers differs only in that property assigns are wrapped: `dst.X = transformerN(... transformer1(sourceExpr))` (inlined via parameter substitution, not `Expression.Invoke` — see §4 for why this matters for ProjectTo).

### 2.4 Why precompute at config-build time

Two architectures were considered:

- **(Recommended)** Precompute `EffectiveTransformers` per TypeMap at config-build time. Codegen reads the dictionary directly. Same architecture as `InheritanceMerger`'s PropertyMap merging and feature #5's hook merging.
- (Rejected) Resolve at codegen time per-property. No precomputation; codegen walks the global/profile/type-map scopes for each property assignment. More code in `ExecutionPlanBuilder`, harder to inspect in tests.

Precompute-at-config matches Atlas's "everything resolved at config-build" architecture and makes the resolved transformer pipeline directly observable in tests (assert `tm.EffectiveTransformers[typeof(string)].Count == 2` etc.).

### 2.5 Why exact-type matching, not assignable

Three matching policies considered:

- **(Recommended)** Exact destination type match. `Add<string>` fires for `string` destinations only.
- (Rejected) Assignable destination type. `Add<object>` would fire for every property of every reference type — a hard-to-debug performance and correctness pitfall.
- (Rejected) Match destination type OR underlying nullable type. `Add<int>` would fire for `int` AND `int?` — convenient but inconsistent (treats value-type nullability as special; reference types are already unified via NRT erasure).

Exact match is what AutoMapper does and matches user mental model. Documented limitation: register `Add<int>` and `Add<int?>` separately if needed.

---

## 3. Solution & Project Layout

No new project. Additions land in `src/Atlas/` (core), `src/Atlas.Projections/` (projection integration). Test additions land in `tests/Atlas.Tests/` and `tests/Atlas.Projections.Tests/`.

```
src/Atlas/
├── ValueTransformerCollection.cs        ← NEW: public registry class
├── Internal/
│   ├── TypeMap.cs                       ← MODIFIED: add 3 fields
│   ├── TransformerResolver.cs           ← NEW: compose global+profile+type-map per TypeMap
│   ├── ExecutionPlanBuilder.cs          ← MODIFIED: WrapWithTransformers + 4 routing call sites
│   └── ...
├── Configuration/
│   ├── IMappingExpression.cs            ← MODIFIED: AddTransform<T> method
│   ├── MappingExpression.cs             ← MODIFIED: implement AddTransform<T>
│   └── ...
├── MapperProfile.cs                     ← MODIFIED: ValueTransformers property + set OriginatingProfile
├── MapperConfigurationExpression.cs     ← MODIFIED: ValueTransformers property
├── MapperConfiguration.cs               ← MODIFIED: call TransformerResolver.Resolve
└── ...

src/Atlas.Projections/
└── Internal/
    └── ProjectionPlanBuilder.cs         ← MODIFIED: wrap projection bindings with transformers

tests/Atlas.Tests/
├── Internal/
│   ├── ValueTransformerCollectionTests.cs        ← NEW
│   └── TransformerResolverTests.cs               ← NEW
├── MapperConfigurationExpressionValueTransformersTests.cs  ← NEW
├── MapperProfileValueTransformersTests.cs        ← NEW
├── MappingExpressionAddTransformTests.cs         ← NEW
├── ExecutionPlanBuilderTransformerTests.cs       ← NEW
└── MapperValueTransformerTests.cs                ← NEW (end-to-end)

tests/Atlas.Projections.Tests/
└── ProjectionTransformerTests.cs        ← NEW
```

No NuGet additions. xUnit v3 + built-in `Assert.X()` only (no FluentAssertions, per project convention).

---

## 4. Public API Additions

Three new surface points, all on existing types, plus one new public class.

### 4.1 `ValueTransformerCollection` (new public class)

```csharp
namespace Atlas;

/// <summary>
/// A registry of value transformers keyed by destination type. Used at global
/// (<see cref="MapperConfigurationExpression.ValueTransformers"/>) and profile
/// (<see cref="MapperProfile.ValueTransformers"/>) scopes.
/// </summary>
/// <remarks>
/// Type matching is exact: <c>Add&lt;string&gt;</c> matches destination properties of type
/// <c>string</c> (and <c>string?</c>, which is the same runtime type after nullable
/// reference type erasure). It does NOT match <c>object</c> or other assignable types.
/// For value types, <c>Add&lt;int&gt;</c> matches <c>int</c> only — register
/// <c>Add&lt;int?&gt;</c> separately if needed for nullable int destinations.
/// <para>
/// Multiple <c>Add&lt;T&gt;</c> calls for the same <typeparamref name="T"/> append in
/// FIFO order. When composed with other scopes (broad-first: global → profile → type-map),
/// the entire FIFO list of this scope appears in order in the effective pipeline.
/// </para>
/// </remarks>
public sealed class ValueTransformerCollection
{
    /// <summary>
    /// Registers a transformer for destination type <typeparamref name="T"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// Thrown if <paramref name="transformer"/> is null.
    /// </exception>
    public ValueTransformerCollection Add<T>(Expression<Func<T, T>> transformer);

    /// <summary>
    /// Internal accessor used by <c>TransformerResolver</c> at config-build time.
    /// Exposes the underlying per-type lists as read-only views.
    /// </summary>
    internal IReadOnlyDictionary<Type, IReadOnlyList<LambdaExpression>> AllTransformers { get; }
}
```

### 4.2 `MapperConfigurationExpression.ValueTransformers`

```csharp
// In src/Atlas/MapperConfigurationExpression.cs

/// <summary>
/// Global value transformers — post-processing functions applied to every value of a
/// given destination type, regardless of which map produces it. Composed broad-first
/// (global → profile → type-map) with finer-scope transformers running after broader
/// ones. Within this scope, transformers run in registration order (FIFO).
/// </summary>
/// <remarks>
/// Transformers are stored as <c>Expression&lt;Func&lt;T, T&gt;&gt;</c> so the same
/// declaration works for both in-memory <see cref="IMapper.Map{TDestination}"/> (compiled
/// to a delegate) and <c>query.ProjectTo&lt;T&gt;()</c> (inlined into the projection lambda
/// for SQL translation by the underlying provider).
/// </remarks>
public ValueTransformerCollection ValueTransformers { get; } = new();
```

### 4.3 `MapperProfile.ValueTransformers`

```csharp
// In src/Atlas/MapperProfile.cs

/// <summary>
/// Profile-scoped value transformers — apply only to TypeMaps registered in this profile.
/// See <see cref="MapperConfigurationExpression.ValueTransformers"/> for global scope and
/// <c>IMappingExpression.AddTransform</c> for type-map scope.
/// </summary>
public ValueTransformerCollection ValueTransformers { get; } = new();
```

### 4.4 `IMappingExpression<TSource, TDestination>.AddTransform`

```csharp
/// <summary>
/// Registers a value transformer scoped to this map only. Composed AFTER any global
/// (<see cref="MapperConfigurationExpression.ValueTransformers"/>) and profile-level
/// (<see cref="MapperProfile.ValueTransformers"/>) transformers for the same type.
/// Multiple <c>AddTransform&lt;T&gt;</c> calls on the same map run in registration
/// order (FIFO) within the type-map scope.
/// </summary>
/// <remarks>
/// The transformer is stored as <c>Expression&lt;Func&lt;T, T&gt;&gt;</c> so it
/// participates in both in-memory mapping and IQueryable projection. Lambdas using
/// constructs the underlying LINQ provider can't translate (custom static method calls,
/// captures of mutable state, etc.) will fail at query execution time with the
/// provider's standard "expression cannot be translated" error — Atlas does not
/// pre-inspect lambdas for translatability.
/// <para>
/// Transformers fire on every property assignment of type <typeparamref name="T"/>
/// within this map's compiled lambda, including constructor parameter assignments and
/// nested-path destination writes (<c>ForPath</c>). Transformers do NOT auto-propagate
/// across <c>.ReverseMap()</c> or <c>Include</c>/<c>IncludeBase</c>; reconfigure on the
/// reverse expression or derived map separately if needed.
/// </para>
/// </remarks>
/// <exception cref="ArgumentNullException">Thrown if <paramref name="transformer"/> is null.</exception>
IMappingExpression<TSource, TDestination> AddTransform<T>(Expression<Func<T, T>> transformer);
```

### 4.5 What's NOT changed

- `IMapper`, `MapperConfiguration` (no new methods; constructor unchanged from feature #5).
- `IMemberConfigurationExpression` — no new methods.
- `ITypeConverter`, `MemberList`, `IMappingAction`, `AtlasConfigurationException`, `AtlasMappingException`, `AtlasProjectionException` — unchanged.
- `ForMember`, `ForCtorParam`, `ForPath`, `ReverseMap`, `Include`, `IncludeBase`, enum methods, `BeforeMap`/`AfterMap` — unchanged.

### 4.6 Worked-example fluent

```csharp
public sealed class TrimAndLowerProfile : MapperProfile
{
    public TrimAndLowerProfile()
    {
        // Profile-level: applies to every map in this profile.
        ValueTransformers.Add<string>(s => s == null ? null! : s.Trim());

        CreateMap<Order, OrderDto>()
            .ForMember(d => d.OrderTotal, opt => opt.MapFrom(s => s.Subtotal + s.Tax))
            .AddTransform<decimal>(d => Math.Round(d, 2));   // Type-map level
    }
}

// Wiring:
var cfg = new MapperConfiguration(c =>
{
    // Global: applies to EVERY map in the entire configuration.
    c.ValueTransformers.Add<string>(s => s == null ? null! : s.ToLowerInvariant());
    c.AddProfile<TrimAndLowerProfile>();
});
```

---

## 5. Internal Architecture

### 5.1 `TypeMap` additions

```csharp
// In src/Atlas/Internal/TypeMap.cs (additions)

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
/// FIFO order. The list is the application order: <c>Effective[T][0]</c> runs first on the
/// raw source value; <c>Effective[T][^1]</c> runs last and produces the final value
/// assigned to the destination property. Empty (no entry) means no transformers apply for
/// that type.
/// </summary>
public Dictionary<Type, IReadOnlyList<LambdaExpression>> EffectiveTransformers { get; } = new();

/// <summary>
/// Backref to the <see cref="MapperProfile"/> that registered this map (when registered
/// via <c>AddProfile</c>). Null for maps registered directly via
/// <see cref="MapperConfigurationExpression.CreateMap"/>. Used by
/// <c>TransformerResolver</c> to identify which profile-level transformers apply.
/// </summary>
public MapperProfile? OriginatingProfile { get; set; }
```

### 5.2 `MapperProfile.CreateMap` modification

The existing `CreateMap` constructs a `TypeMap` and adds it to the profile's `_typeMaps` list. After Task 3 of the implementation plan, it ALSO sets `tm.OriginatingProfile = this`:

```csharp
protected IMappingExpression<TSource, TDestination> CreateMap<TSource, TDestination>(
    MemberList memberList = MemberList.Destination)
{
    var map = new TypeMap(typeof(TSource), typeof(TDestination), memberList)
    {
        RegistrationOrigin = $"CreateMap<{typeof(TSource).Name}, {typeof(TDestination).Name}>()",
        OriginatingProfile = this,   // NEW
    };
    _typeMaps.Add(map);
    return new Atlas.Configuration.MappingExpression<TSource, TDestination>(map, _typeMaps.Add);
}
```

`MapperConfigurationExpression.CreateMap` does NOT set `OriginatingProfile` — directly-registered TypeMaps have no profile. `TransformerResolver` correctly handles the null case (§5.5).

### 5.3 `MappingExpression<,>.AddTransform` implementation

```csharp
public IMappingExpression<TSource, TDestination> AddTransform<T>(Expression<Func<T, T>> transformer)
{
    TypeMap.EnsureMutable();
    ArgumentNullException.ThrowIfNull(transformer);

    var key = typeof(T);
    if (!TypeMap.TypeMapTransformers.TryGetValue(key, out var list))
    {
        list = new List<LambdaExpression>();
        TypeMap.TypeMapTransformers[key] = list;
    }
    list.Add(transformer);
    return this;
}
```

### 5.4 `ValueTransformerCollection` implementation

```csharp
namespace Atlas;

public sealed class ValueTransformerCollection
{
    private readonly Dictionary<Type, List<LambdaExpression>> _byType = new();

    public ValueTransformerCollection Add<T>(Expression<Func<T, T>> transformer)
    {
        ArgumentNullException.ThrowIfNull(transformer);
        var key = typeof(T);
        if (!_byType.TryGetValue(key, out var list))
        {
            list = new List<LambdaExpression>();
            _byType[key] = list;
        }
        list.Add(transformer);
        return this;
    }

    internal IReadOnlyDictionary<Type, IReadOnlyList<LambdaExpression>> AllTransformers
    {
        get
        {
            // Snapshot view for the resolver. Cheap because configs are built once.
            return _byType.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<LambdaExpression>)kv.Value);
        }
    }
}
```

### 5.5 `TransformerResolver`

```csharp
// NEW: src/Atlas/Internal/TransformerResolver.cs
namespace Atlas.Internal;

/// <summary>
/// Composes global + profile + type-map value transformers into each TypeMap's
/// <c>EffectiveTransformers</c> dictionary. Runs at config-build time, after
/// <c>ReverseMapMirror.Mirror</c>, before <c>tm.Seal()</c>.
/// </summary>
internal static class TransformerResolver
{
    public static void Resolve(
        IReadOnlyList<TypeMap> typeMaps,
        ValueTransformerCollection globalTransformers)
    {
        var globalLookup = globalTransformers.AllTransformers;

        foreach (var tm in typeMaps)
        {
            // Collect every destination type referenced by ANY scope for this TypeMap.
            var allTypes = new HashSet<Type>();
            foreach (var t in globalLookup.Keys) allTypes.Add(t);
            if (tm.OriginatingProfile is { } profile)
                foreach (var t in profile.ValueTransformers.AllTransformers.Keys) allTypes.Add(t);
            foreach (var t in tm.TypeMapTransformers.Keys) allTypes.Add(t);

            foreach (var destType in allTypes)
            {
                var composed = new List<LambdaExpression>();

                // Broad-first compose: global → profile → type-map. FIFO within each scope.
                if (globalLookup.TryGetValue(destType, out var g))
                    composed.AddRange(g);

                if (tm.OriginatingProfile is { } prof
                    && prof.ValueTransformers.AllTransformers.TryGetValue(destType, out var p))
                    composed.AddRange(p);

                if (tm.TypeMapTransformers.TryGetValue(destType, out var t))
                    composed.AddRange(t);

                if (composed.Count > 0)
                    tm.EffectiveTransformers[destType] = composed;
            }
        }
    }
}
```

### 5.6 `MapperConfiguration` integration

```csharp
// In src/Atlas/MapperConfiguration.cs (excerpt of constructor body)

InheritanceMerger.Resolve(typeMaps, pairIndex);

foreach (var tm in typeMaps)
    ConventionEngine.ResolveMissingMembers(tm, _conventionOptions, HasRegisteredMap);

ReverseMapMirror.Mirror(typeMaps);

TransformerResolver.Resolve(typeMaps, expression.ValueTransformers);   // NEW

foreach (var tm in typeMaps)
    tm.Seal();
```

---

## 6. Compilation Algorithm

### 6.1 Where the change lives

`ExecutionPlanBuilder` produces a per-PropertyMap source-side expression (via `BuildSourceExpression`) and assigns it to the destination property. The transformer-application step wraps the source expression with composed inlined transformer bodies BEFORE the assign:

```
Existing:                dst.X = sourceExpr
With one transformer:    dst.X = transformer1(sourceExpr)
With three composed:     dst.X = transformer3(transformer2(transformer1(sourceExpr)))
```

Composition is left-to-right per `EffectiveTransformers[destType]`: index `[0]` is the innermost (runs first), index `[^1]` is the outermost (runs last). This matches the broad-first scope order: global runs first on the raw value; type-map runs last on the transformed value.

### 6.2 `WrapWithTransformers` helper

```csharp
private static Expression WrapWithTransformers(
    Expression sourceExpr,
    Type destType,
    TypeMap typeMap)
{
    if (!typeMap.EffectiveTransformers.TryGetValue(destType, out var transformers))
        return sourceExpr;

    Expression current = sourceExpr;
    foreach (var transformer in transformers)
    {
        // Inline the transformer's body, substituting the lambda parameter with `current`.
        // CRITICAL: this MUST be inlining via parameter substitution, NOT
        // Expression.Invoke(transformer, current). EF Core (and most LINQ providers) cannot
        // translate Expression.Invoke nodes to SQL; they CAN translate the inlined body
        // (e.g., a substituted s.Trim() reads as a direct method call EF maps to LTRIM/RTRIM).
        // The same pattern is used today by ProjectionPlanBuilder for MapFrom lambdas.
        var paramSubst = new ParameterReplacer(transformer.Parameters[0], current);
        current = paramSubst.Visit(transformer.Body)!;
    }
    return current;
}
```

`ParameterReplacer` is the existing private nested class in `ExecutionPlanBuilder` (used today for `MapFrom` lambda inlining — same pattern).

### 6.3 Routing in `BuildPocoLambda` and `BuildUpdate`

**`BuildPocoLambda`** has two assignment sites that need the wrap:

(a) The constructor-argument block (when `dstType` has a non-parameterless ctor):

```csharp
// Replace the existing Select(p => ...) body with a transformer-aware version:
var args = ctor.GetParameters().Select(p =>
{
    var pm = ctorParamMaps.FirstOrDefault(m =>
        string.Equals(m.Name, p.Name, StringComparison.OrdinalIgnoreCase));
    Expression sourceExpr;
    if (pm is null)
        sourceExpr = p.HasDefaultValue
            ? Expression.Constant(p.DefaultValue, p.ParameterType)
            : Expression.Default(p.ParameterType);
    else
        sourceExpr = BuildSourceExpression(pm, srcParam, registry, p.ParameterType)
            ?? Expression.Default(p.ParameterType);

    return WrapWithTransformers(sourceExpr, p.ParameterType, typeMap);   // NEW
}).ToArray();
```

(b) The property-binding loop (single-level + nested-path):

```csharp
foreach (var pm in propertyMaps)
{
    if (pm.Ignored) continue;
    if (pm.DestinationProperty is null) continue;

    var sourceExpr = BuildSourceExpression(pm, srcParam, registry, pm.DestinationProperty.PropertyType);
    if (sourceExpr is null) continue;

    var transformed = WrapWithTransformers(sourceExpr, pm.DestinationProperty.PropertyType, typeMap);   // NEW

    if (pm.DestinationPath is { } path && path.Count > 1)
        statements.Add(BuildNestedAssign(destVar, path, transformed));
    else
        statements.Add(Expression.Assign(
            Expression.Property(destVar, pm.DestinationProperty),
            transformed));
}
```

**`BuildUpdate`** has the same property-binding shape (no ctor-args because update-in-place reuses the existing destination instance). Same one-line wrap insertion before the assign.

### 6.4 Codegen paths that do NOT apply transformers

- **`BuildEnumLambda`** (enum dispatch): enums have their own per-value resolution semantics. Transformers do not apply — the destination is set by `EnumResolver` per source value.
- **`BuildConverterLambda`** (whole-map `ConvertUsing`): the user's converter IS the entire map; transformers would interfere with the converter's intent.
- **`BuildCollectionLambda`** / **`BuildDictionaryLambda`**: these route per-element through `MappingInvoker.Invoke<S, D>`, so per-element transformers fire naturally on the element TypeMap. No additional wrap at the collection-level.

### 6.5 Concrete trace — global + profile + type-map

User config from §4.6: global `s => s.ToLowerInvariant()`, profile `s => s.Trim()`, type-map `(Order, OrderDto)` `d => Math.Round(d, 2)`.

`(Order, OrderDto)` TypeMap's `EffectiveTransformers` after `TransformerResolver`:

```
typeof(string)  → [global_lower, profile_trim]      (no type-map string transformer)
typeof(decimal) → [type-map_round]                   (no global/profile decimal transformer)
```

For PropertyMap `CustomerName` (string) sourced from `src.Customer.Name`:

```
sourceExpr   = src.Customer.Name
After step 1 (global_lower):   src.Customer.Name.ToLowerInvariant()
After step 2 (profile_trim):   src.Customer.Name.ToLowerInvariant().Trim()
emit:        dst.CustomerName = src.Customer.Name.ToLowerInvariant().Trim()
```

For PropertyMap `OrderTotal` (decimal):

```
sourceExpr   = src.Subtotal + src.Tax    (from forward MapFrom)
After step 1 (type-map_round): Math.Round(src.Subtotal + src.Tax, 2)
emit:        dst.OrderTotal = Math.Round(src.Subtotal + src.Tax, 2)
```

### 6.6 ProjectTo integration

`Atlas.Projections.ProjectionPlanBuilder` already inlines `MapFrom` lambdas via `ParameterReplacer.Replace` (see line 81-85 of the current file). The transformer wrap follows the SAME pattern — apply the same `ParameterReplacer.Replace` call once per transformer in the effective list.

Sketch in `ProjectionPlanBuilder.cs`:

```csharp
var binding = BuildBinding(srcExpr, pm, depth, pm.DestinationProperty.PropertyType, registry, maxDepth);
if (binding is null) continue;

binding = WrapProjectionWithTransformers(binding, pm.DestinationProperty.PropertyType, tm);

bindings.Add(Expression.Bind(pm.DestinationProperty, binding));
```

`WrapProjectionWithTransformers` is a small private static method in `ProjectionPlanBuilder` that mirrors `WrapWithTransformers` but uses the existing `Atlas.Projections.Internal.ParameterReplacer` static helper (which already exposes a `Replace(body, param, replacement)` API used elsewhere in this file).

The constructor-argument loop in `ProjectionPlanBuilder.BuildBody` also needs the same wrap (matches the equivalent change in `ExecutionPlanBuilder.BuildPocoLambda` ctor-args block).

### 6.7 Implementation note on shared helper

A shared `Atlas.Internal.TransformerComposer` could extract the wrap logic to avoid duplicating `ParameterReplacer` walks across `Atlas` and `Atlas.Projections`. Decision: **do not extract**. Each consumer's wrap implementation is ~10 lines; the projection package already has its own `ParameterReplacer.Replace` static API, and the in-memory codegen has a private `ParameterReplacer` nested class. Keeping each consumer self-contained matches the existing style (e.g., neither package's nested-path emission shares code with the other) and avoids visibility-changes on the existing `ParameterReplacer` types. If a third consumer ever appears, refactor at that point.

---

## 7. TDD Plan

10 implementation tasks, ~32 new tests. Same TDD-first cadence as features #2-5.

### 7.1 `Internal/ValueTransformerCollectionTests.cs` — 4 tests

- `Add_ReturnsThis_ForChaining`
- `Add_MultipleSameType_AppendsInFifoOrder`
- `Add_MultipleDifferentTypes_StoredSeparately`
- `Add_NullTransformer_Throws`

### 7.2 `MapperConfigurationExpressionValueTransformersTests.cs` — 2 tests

- `ValueTransformers_PropertyExposedAndAccessible`
- `ValueTransformers_RegistrationsPersistOnExpression`

### 7.3 `MapperProfileValueTransformersTests.cs` — 2 tests

- `ValueTransformers_PropertyExposedAndAccessible`
- `ValueTransformers_RegisteredInConstructor_Persists`

### 7.4 `MappingExpressionAddTransformTests.cs` — 4 tests

- `AddTransform_AppendsToTypeMapTransformers`
- `AddTransform_MultipleSameType_PreservesFifoOrder`
- `AddTransform_NullTransformer_Throws`
- `AddTransform_ReturnsExpression_ForChaining`

### 7.5 `Internal/TransformerResolverTests.cs` — 6 tests

- `Resolve_GlobalOnly_EffectiveContainsGlobal`
- `Resolve_ProfileOnly_EffectiveContainsProfile`
- `Resolve_TypeMapOnly_EffectiveContainsTypeMap`
- `Resolve_AllThreeScopes_ComposedBroadFirst` (the §5.5 trace — assert `Effective[string]` is exactly `[global, profile, type-map]` in order)
- `Resolve_NoTransformers_EffectiveEmpty`
- `Resolve_OriginatingProfileNull_OnlyGlobalAndTypeMap`

### 7.6 `ExecutionPlanBuilderTransformerTests.cs` — 5 tests

- `WrapWithTransformers_NoTransformer_ReturnsSourceUntouched`
- `WrapWithTransformers_SingleTransformer_WrapsSource`
- `WrapWithTransformers_TwoTransformers_ComposeLeftToRight`
- `BuildPocoLambda_CtorArg_AlsoTransformed`
- `BuildPocoLambda_NestedPath_AlsoTransformed` (for ForPath destinations)

### 7.7 `MapperValueTransformerTests.cs` — 6 end-to-end tests

- `Global_StringTrim_AppliesEverywhere`
- `Profile_StringLower_ComposesAfterGlobal`
- `TypeMap_DecimalRound_AppliesOnlyToConfiguredMap`
- `Collection_PerElementTransformerFires`
- `UpdateInPlace_TransformersApply`
- `NestedMap_DestinationPropertyTransformed` (transformer on the OUTER map applies to nested-map destination property whose result type matches)

### 7.8 `Atlas.Projections.Tests/ProjectionTransformerTests.cs` — 3 tests

- `ProjectTo_TranslatableTransformer_TranslatesToExpression` (e.g., `s => s + "!"` produces a translatable LINQ expression — assert via expression-tree inspection or by running against an in-memory IQueryable)
- `ProjectTo_TwoComposedTransformers_BothInlined`
- `ProjectTo_NonTranslatableTransformer_FailsAtQueryExecutionTime` (assert that the AT-RUNTIME failure path produces a sensible error from the LINQ provider — Atlas does not pre-reject)

### 7.9 Implementation tasks (commit-by-commit)

| # | Task | Tests | Model |
|---|---|---|---|
| 1 | Branch setup (`feat/value-transformers` from main HEAD) | 0 | manual |
| 2 | Data model: `ValueTransformerCollection` (public class) + `TypeMap.TypeMapTransformers` / `EffectiveTransformers` / `OriginatingProfile` | 4 | haiku |
| 3 | Global registry on `MapperConfigurationExpression` + Profile registry on `MapperProfile` + set `OriginatingProfile` in `MapperProfile.CreateMap` | 4 (2+2) | haiku |
| 4 | `AddTransform<T>` on `IMappingExpression`/`MappingExpression` | 4 | haiku |
| 5 | `TransformerResolver` (new internal static) | 6 | sonnet |
| 6 | Wire `TransformerResolver.Resolve` into `MapperConfiguration` build sequence between `ReverseMapMirror.Mirror` and `tm.Seal()` | 0 | haiku |
| 7 | `ExecutionPlanBuilder.WrapWithTransformers` helper + routing in `BuildPocoLambda` (property + ctor-args + nested-path) and `BuildUpdate` | 5 | sonnet |
| 8 | End-to-end `MapperValueTransformerTests` | 6 | sonnet |
| 9 | `Atlas.Projections.ProjectionPlanBuilder` integrates the same compose logic (private `WrapProjectionWithTransformers` helper + routing) | 3 | sonnet |
| 10 | README "Value transformers" section + remove deferred entry + coverage check | 0 | haiku |

**Total: ~32 new tests.** Baseline 363 → ~395 after this lands. Coverage targets carried forward: line ≥ 90%, branch ≥ 80% on `Atlas` core.

---

## 8. Risks & Open Questions

### 8.1 Things to trace concretely during plan-writing (per pseudocode-trace memory)

1. **`ParameterReplacer` inlining vs `Expression.Invoke` for ProjectTo translatability.** This is THE single most important detail in the entire feature. If `WrapWithTransformers` uses `Expression.Invoke(transformer, current)`, EF Core cannot translate the result to SQL — the user gets a runtime translation error for the simplest cases (e.g., `s => s.Trim()`). Inlining via parameter substitution produces a flat expression EF Core can read. Plan must include a concrete test (in `ProjectionTransformerTests`) that a global `s => s.Trim()` transformer projects to an EF Core query with `Trim()` translated as `LTRIM(RTRIM(...))` (or equivalent). The `ProjectionPlanBuilder` already uses parameter substitution for `MapFrom` lambdas (see line 81-85 in current `ProjectionPlanBuilder.cs`); transformer inlining must follow the SAME pattern.

2. **Composition-direction trace.** §5.5 builds `composed = [global_FIFO..., profile_FIFO..., typeMap_FIFO...]`. The wrap loop in §6.2 iterates left-to-right and wraps each transformer around the accumulating `current`. Trace: global=`s => s.ToLower()`, profile=`s => s.Trim()`, type-map=`s => s + "!"`. Loop step 1: `current = src.X.ToLower()`. Step 2: `current = src.X.ToLower().Trim()`. Step 3: `current = src.X.ToLower().Trim() + "!"`. Result matches §1 design intent. Plan must include this exact trace as a test in `ExecutionPlanBuilderTransformerTests`.

3. **Cross-package consumer audit (Bug 4 lesson).** The new `TypeMap.EffectiveTransformers` field is consumed by:
   - `Atlas.Internal.ExecutionPlanBuilder.BuildPocoLambda`/`BuildUpdate` (Task 7)
   - `Atlas.Projections.Internal.ProjectionPlanBuilder.BuildBody` and `BuildBinding` (Task 9 — both top-level entry AND recursive nested-map call site)
   
   Plan must explicitly grep `Atlas.Projections` for every `tm.` access and confirm Task 9 covers both paths so transformers also apply to nested map results during projection.

4. **Type matching is EXACT.** §4.1 specifies `Add<string>` matches `string` destinations only — NOT `object`, NOT assignable types. Plan must include test: configuration with `Add<object>(o => o)` and a `string` destination property → transformer does NOT fire. For value types: `Add<int>` and an `int?` destination → does NOT fire (different runtime types). Documented limitation; user registers both if needed.

5. **Hooks + transformers coexistence.** Hooks fire around the WHOLE map (Before/After). Transformers fire PER PROPERTY. They're independent and stack. Plan must include test in `MapperValueTransformerTests`: a TypeMap with both `BeforeMap((s, d) => trace.Add("before"))` and `AddTransform<string>(s => s + "!")` — the hook fires once around the property mapping; the transformer wraps each string property's source-side. Order at runtime: `before` → property assigns (each with transformer applied). Verify no ordering surprise.

6. **Inheritance non-propagation.** Per §1.3, transformers do NOT inherit base → derived. Plan must include test: `CreateMap<Animal, AnimalDto>().AddTransform<string>(s => s + "!")` PLUS `CreateMap<Dog, DogDto>().IncludeBase<Animal, AnimalDto>()` — when mapping `Dog → DogDto`, the Animal-level type-map string transformer does NOT fire (because type-map scope is per-TypeMap; only profile + global propagate via "every map" semantics). This is a deliberate design choice; test ensures we don't accidentally inherit.

7. **Reverse-map non-propagation.** Forward map with `.AddTransform<string>(...)` and `.ReverseMap()` — the reverse map's `TypeMapTransformers` should be empty (modulo any global/profile-scoped transformers that apply to both directions because both maps are in the same profile/config). Plan must include test verifying `ReverseMapMirror.Mirror` doesn't touch transformer fields.

8. **`OriginatingProfile` null path.** TypeMaps registered directly via `MapperConfigurationExpression.CreateMap` (not through a profile) have `OriginatingProfile == null`. `TransformerResolver`'s `if (tm.OriginatingProfile is { } prof)` correctly handles this. Plan must include test: directly-registered TypeMap with a global string transformer + a directly-set type-map string transformer → effective is `[global, type-map]` (no profile entries because no profile).

9. **`MapperConfiguration(expression, sp)` ctor interaction.** The Hooks-feature SP-aware ctor (added in feature #5 Task 4) chains through the parameterless ctor (which runs `TransformerResolver.Resolve` before sealing) then re-creates `_registry` with SP. Resolved transformers live on `TypeMap` (NOT the registry), so they survive the registry re-creation. Plan must verify by trace.

10. **Profile transformer mutation after `MarkBuilt`.** `MapperConfigurationExpression.MarkBuilt` is called when the configuration is consumed. After that, `MapperProfile.ValueTransformers.Add(...)` would still succeed (the collection itself isn't sealed) but the resolver has already run, so additions are silently lost. This is consistent with how other profile-level config behaves once `BuildExpression` is called. Worth a doc note in `MapperProfile.ValueTransformers` XML — "Mutations after configuration is built do not take effect on the already-compiled mapper." No special enforcement.

### 8.2 Explicitly deferred to v3

- Member-level scope as a dedicated API (`opt.AddTransform<T>(...)` on `IMemberConfigurationExpression`).
- DI integration via `IValueTransformer<T>` interface (mirrors Hooks `IMappingAction`).
- Reverse-map auto-propagation of transformers.
- Inheritance auto-propagation (base → derived) at the type-map scope.
- Translatability inspection / friendly rejection in ProjectTo.
- Type-matching beyond exact equality (assignable, nullable-of-T, etc.).

### 8.3 Open questions for the implementing session to push back on

- **Snapshot `AllTransformers` vs live view.** §5.4's `AllTransformers` getter constructs a fresh dictionary on each access. `TransformerResolver` calls it once per-TypeMap which means O(typeMaps × transformers) dictionary allocations. For large configs, a cached snapshot would reduce that to O(transformers). Implementer can cache lazily on first access, or `TransformerResolver` can call once and reuse. Either is fine; the optimization is invisible to users.
- **`OriginatingProfile` exposure.** Currently exposed as a public property on `TypeMap` (which is internal). All access is from `Atlas.Internal` and `Atlas.Tests`. If the implementer prefers, this can be `internal set` to harden against external mutation while staying readable internally. Trivial change; flag if implementer thinks it matters.
- **`WrapWithTransformers` placement.** §6.2 places it as a private static method in `ExecutionPlanBuilder` alongside `BuildNestedAssign` (added in feature #4). Implementer can place it anywhere within the file; convention is "near other build helpers."

---

## 9. Appendix A — Worked Example

### 9.1 User code

```csharp
public class Customer
{
    public string? Name { get; set; }
    public string? Email { get; set; }
}

public class Order
{
    public Customer? Customer { get; set; }
    public decimal Subtotal { get; set; }
    public decimal Tax { get; set; }
}

public class OrderDto
{
    public string? CustomerName { get; set; }
    public string? CustomerEmail { get; set; }
    public decimal OrderTotal { get; set; }
}

public sealed class TrimAndLowerProfile : MapperProfile
{
    public TrimAndLowerProfile()
    {
        // Profile-level: applies to every map in this profile.
        ValueTransformers.Add<string>(s => s == null ? null! : s.Trim());

        CreateMap<Order, OrderDto>()
            .ForMember(d => d.OrderTotal, opt => opt.MapFrom(s => s.Subtotal + s.Tax))
            .AddTransform<decimal>(d => Math.Round(d, 2));
    }
}

var cfg = new MapperConfiguration(c =>
{
    // Global: applies to every map in the entire configuration.
    c.ValueTransformers.Add<string>(s => s == null ? null! : s.ToLowerInvariant());
    c.AddProfile<TrimAndLowerProfile>();
});
var mapper = cfg.CreateMapper();
```

### 9.2 Build trace

1. **Profile.Configure().** Profile constructor runs:
   - `ValueTransformers.Add<string>(profile_trim)` → profile-scope dict gains `{ string: [profile_trim] }`.
   - `CreateMap<Order, OrderDto>()` constructs `TypeMap` with `OriginatingProfile = this`, `RegistrationOrigin = "CreateMap<Order, OrderDto>()"`. Adds to profile's `_typeMaps` list.
   - `.ForMember(...)` populates `PropertyMaps[OrderTotal]` with `IsExplicit=true`, `CustomExpression={s.Subtotal+s.Tax}`.
   - `.AddTransform<decimal>(typemap_round)` appends to `TypeMap.TypeMapTransformers[decimal]`.
   
   ConfigExpression-level: `ValueTransformers.Add<string>(global_lower)` → global-scope dict gains `{ string: [global_lower] }`.
   
   `AddProfile<TrimAndLowerProfile>()` instantiates the profile, runs its constructor, then iterates `profile.GetTypeMaps()` and registers each via `RegisterTypeMap` (no conflict; no-op for the conflict guard). The TypeMap arrives in ConfigExpression's `_typeMaps` with `OriginatingProfile` already set by `MapperProfile.CreateMap`.

2. **`InheritanceMerger.Resolve`.** No `Include`/`IncludeBase` calls; no-op.

3. **`ConventionEngine.ResolveMissingMembers`.** Discovers:
   - `CustomerName → [Customer, Name]` (PascalCase flattening)
   - `CustomerEmail → [Customer, Email]`
   - PropertyMaps now: `[OrderTotal explicit, CustomerName conv, CustomerEmail conv]`.

4. **`ReverseMapMirror.Mirror`.** No `.ReverseMap()` call; no-op.

5. **`TransformerResolver.Resolve`** runs over `[OrderTm]`:
   - Collect referenced types: `string` (via global+profile), `decimal` (via type-map). Set: `{string, decimal}`.
   - For `string`:
     - `composed.AddRange(global[string])` → `[global_lower]`.
     - `tm.OriginatingProfile != null`, `composed.AddRange(profile[string])` → `[global_lower, profile_trim]`.
     - `tm.TypeMapTransformers` has no `string` entry → skip.
     - `EffectiveTransformers[string] = [global_lower, profile_trim]`.
   - For `decimal`:
     - `composed.AddRange(global[decimal])` → empty (no global decimal transformer); skip via `TryGetValue`.
     - `composed.AddRange(profile[decimal])` → empty; skip.
     - `composed.AddRange(typeMap[decimal])` → `[typemap_round]`.
     - `EffectiveTransformers[decimal] = [typemap_round]`.

6. **`tm.Seal()`.**

7. **`AssertConfigurationIsValid()`** (if called): no transformer-specific validation; existing rules (paths, hooks, enum, inheritance) pass cleanly.

8. **`CompileMappings()`.** Codegen for `(Order, OrderDto)`:
   - Property `OrderTotal` (decimal): `sourceExpr = src.Subtotal + src.Tax` (from `CustomExpression`). `WrapWithTransformers` finds `EffectiveTransformers[decimal] = [typemap_round]`. After wrap: `Math.Round(src.Subtotal + src.Tax, 2)`. Emit: `dst.OrderTotal = Math.Round(src.Subtotal + src.Tax, 2);`
   - Property `CustomerName` (string): `sourceExpr = src.Customer.Name` (null-safe path access). `WrapWithTransformers` finds `EffectiveTransformers[string] = [global_lower, profile_trim]`.
     - Step 1: `current = src.Customer.Name.ToLowerInvariant()`.
     - Step 2: `current = src.Customer.Name.ToLowerInvariant().Trim()`.
     - Emit: `dst.CustomerName = src.Customer.Name.ToLowerInvariant().Trim();`
   - Property `CustomerEmail` (string): same pattern as CustomerName.

   Compiled lambda body (pseudocode):
   ```csharp
   (Order src) => {
       var dst = new OrderDto();
       dst.OrderTotal = Math.Round(src.Subtotal + src.Tax, 2);
       dst.CustomerName = src.Customer.Name.ToLowerInvariant().Trim();
       dst.CustomerEmail = src.Customer.Email.ToLowerInvariant().Trim();
       return dst;
   }
   ```

### 9.3 Runtime use

```csharp
var entity = new Order
{
    Customer = new Customer { Name = "  ALICE  ", Email = "  X@Y.COM  " },
    Subtotal = 90.123m,
    Tax = 10.456m,
};

var dto = mapper.Map<OrderDto>(entity);

// dto.CustomerName  == "alice"          (global ToLowerInvariant → profile Trim)
// dto.CustomerEmail == "x@y.com"
// dto.OrderTotal    == 100.58m          (Math.Round of 100.579 to 2 places)
```

### 9.4 ProjectTo path

Same configuration, used via `query.ProjectTo<OrderDto>()`:

```csharp
var ordersDb = dbContext.Orders;   // IQueryable<Order> from EF Core
var dtos = ordersDb.ProjectTo<OrderDto>(cfg).ToList();
```

`ProjectionPlanBuilder.BuildBinding` for each property:
- `OrderTotal`: `binding = src.Subtotal + src.Tax`. `WrapProjectionWithTransformers` inlines `Math.Round(.., 2)` → `Math.Round(src.Subtotal + src.Tax, 2)`. EF Core translates as `ROUND(SUM(Subtotal+Tax), 2)`.
- `CustomerName`: `binding = src.Customer.Name`. Wrap inlines `ToLowerInvariant()` then `Trim()` → `src.Customer.Name.ToLowerInvariant().Trim()`. EF Core translates as `LTRIM(RTRIM(LOWER(c.Name)))` (or equivalent depending on provider).

EF Core handles `Trim`, string concat, member access, arithmetic, and casts natively — so this projection runs entirely in SQL.

If a user adds a transformer EF Core can't translate (`s => MyHelpers.Reverse(s)`), the projection-build succeeds (Atlas does not pre-inspect), but EF Core throws at query execution time with its standard "Expression cannot be translated" error. The user can then either remove the transformer, simplify it, or switch to in-memory `Map<>`.

---

## 10. Implementation Checklist

For the implementing Claude session. Each row is a self-contained commit.

- [ ] **Task 1 — Branch setup.** Cut `feat/value-transformers` from `main`. Verify clean baseline (363 tests).
- [ ] **Task 2 — Data model.** New `ValueTransformerCollection` public class; `TypeMap.TypeMapTransformers`/`EffectiveTransformers`/`OriginatingProfile` fields. Tests: `Internal/ValueTransformerCollectionTests.cs` (4 tests).
- [ ] **Task 3 — Global + profile registries.** `MapperConfigurationExpression.ValueTransformers`, `MapperProfile.ValueTransformers`. Set `tm.OriginatingProfile = this` in `MapperProfile.CreateMap`. Tests: `MapperConfigurationExpressionValueTransformersTests.cs` (2) + `MapperProfileValueTransformersTests.cs` (2).
- [ ] **Task 4 — `AddTransform<T>` fluent surface.** Add to `IMappingExpression`/`MappingExpression`. Tests: `MappingExpressionAddTransformTests.cs` (4 tests).
- [ ] **Task 5 — `TransformerResolver`.** New `Internal/TransformerResolver.cs` with the §5.5 compose algorithm. Tests: `Internal/TransformerResolverTests.cs` (6 tests).
- [ ] **Task 6 — Wire into build sequence.** Insert `TransformerResolver.Resolve` between `ReverseMapMirror.Mirror` and `tm.Seal()` in `MapperConfiguration`. No new tests — integration covered by Tasks 5, 7, 8.
- [ ] **Task 7 — Codegen wrap.** `ExecutionPlanBuilder.WrapWithTransformers` helper + routing in `BuildPocoLambda` (property + ctor-args + nested-path) and `BuildUpdate`. Tests: `ExecutionPlanBuilderTransformerTests.cs` (5 tests).
- [ ] **Task 8 — End-to-end.** `MapperValueTransformerTests.cs` (6 tests covering global trim, profile compose, type-map decimal round, collection per-element, update-in-place, nested-map property).
- [ ] **Task 9 — `Atlas.Projections` integration.** Extend `ProjectionPlanBuilder` with `WrapProjectionWithTransformers`; route property-binding loop and ctor-args through it. Tests: `Atlas.Projections.Tests/ProjectionTransformerTests.cs` (3 tests).
- [ ] **Task 10 — README + coverage.** Add `## Value transformers` section; remove "Value transformers" from deferred list; verify line ≥ 90% / branch ≥ 80% on `Atlas` core.

**Final holistic review** by `superpowers:code-reviewer` over the whole branch before merge — per the established workflow rhythm. All five prior features have surfaced 1+ critical/important issue at this stage that per-task reviews missed. Don't skip.
