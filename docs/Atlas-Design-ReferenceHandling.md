# Atlas v2 — Reference Handling for Cycles

**Status:** Approved design (2026-05-06).
**Implementation target:** v2 feature group #11 (post-MVP, post-DynamicMapping).
**Predecessor designs:** `docs/Atlas-Design-DynamicMapping.md` (single-insertion-point projection rejection pattern; dual-gate via `ProjectionCompatibility` + `ProjectionPlanBuilder.RejectXxxOrThrow`), `docs/Atlas-Design-BeforeAfterHooks.md` (per-typemap fluent flag + projection rejection precedent), `docs/Atlas-Design.md` (v1 baseline — `MapperConfiguration`, `MapperRegistry`, `ExecutionPlanBuilder`, `MappingInvoker`).

This document specifies Atlas's eleventh post-MVP feature: **opt-in cycle-safe and shared-reference-preserving mapping** via a per-typemap fluent registration `cfg.CreateMap<TSrc, TDst>().PreserveReferences()`. When activated, Atlas allocates a per-call instance cache at the public-API boundary and threads it through every nested map call, breaking cycles and preserving shared destination identity.

---

## 1. Goals & Non-Goals

### 1.1 Goals

1. **Opt-in cycle-safe mapping via `cfg.CreateMap<TSrc, TDst>().PreserveReferences()`.** Per-typemap fluent activation. Default OFF — zero new cost on existing v1 code paths beyond a single nullable check per nested call.

2. **Break cycles AND preserve shared references within a single top-level `Map` call.** Once activated, recursive `src.Boss.Boss` chains terminate; multiply-referenced source instances produce a single destination instance reused across all back-references in the destination graph.

3. **Cache shape: `Dictionary<(object source, Type destinationType), object>` with `ReferenceEquals`-on-source equality.** Pre-population semantics — destination is registered into the cache BEFORE its members are populated, which is what breaks cycles. Same source mapped to two destination types within one call gets two cache slots (rare in practice but correct).

4. **Cycle-safety propagates DOWN automatically.** When the top-level typemap has `PreserveReferences = true`, a `MappingContext` is allocated at the public-API boundary and threaded through every nested map call. Inner non-PR-flagged typemaps inherit the protection — they consult and register the cache without needing their own flag. Single mental model: "I marked `Department`; my whole Department graph is cycle-safe."

5. **Universal `MappingContext?` parameter on every compiled lambda.** Every typemap's compiled lambda — flagged or not — accepts a nullable `MappingContext?`. `null` means "no PreserveReferences active" → fast path with zero cache work. Non-null means "active" → cache lookup + register before body. The OFF path adds one nullable check per nested map call (≤1 ns).

6. **Atlas.Projections rejects** PreserveReferences typemaps via dual-gate (`ProjectionCompatibility.IsTypeMapProjectable` + `ProjectionPlanBuilder.RejectPreserveReferencesOrThrow`) matching the established Hooks #5 / DynamicMapping #10 pattern.

7. **`ValidatePreserveReferences` validator rule:** rejects `PreserveReferences + ConvertUsing` combination at config time with a clear `AtlasConfigurationException`. The combination is meaningless — `ConvertUsing` replaces the body that the cache would wrap.

8. **Cache scope: nested POCO map calls only** (POCO destinations + collection-element POCO destinations + dictionary-value POCO destinations). Primitives, strings, enums, value-type sources skip the cache. Hooks (`BeforeMap`/`AfterMap`) and per-property transformations (`AddTransform`, `Condition`, `PreCondition`, `NullSubstitute`) fire on FIRST allocation only — cache hits skip the body entirely (no double-invocation of side effects).

9. **Propagation through related-typemap creators.** The flag flows through `InheritanceMerger.CopyConfig` (base→derived), `MappingExpression.ReverseMap` (forward→reverse), and `OpenGenericTypeMap.Materialize` (template→closed pair). Bug-5 lesson applied: scope-identifying metadata propagates.

### 1.2 Non-Goals (deferred to v3)

- **Custom reference-handler interface** (`IReferenceHandler`) — v1 ships only the built-in `Dictionary<(object, Type), object>` handler. Pluggability deferred.
- **Per-call opt-in** (`mapper.Map<TDest>(src, opts => opts.PreserveReferences())`) — would require a new `MapOptions` parameter on every IMapper overload. Defer to v3 alongside the context-bag work.
- **Global toggle** (`cfg.PreserveReferences = true` on `MapperConfigurationExpression`) — explicit per-typemap is cleaner; users who want global can mark every map by hand or wait for v3.
- **`MappingContext` exposure to user code** — hooks, resolvers, transformers don't see the cache. Internal-only in v1. Exposing to user code is the v3 context-bag work.
- **Reference equality semantics for value-type sources** — value types boxed into the cache would each be a separate boxed instance, defeating reference-equality. Cache is effectively only consulted for reference-type sources (codegen branches on `srcType.IsClass`).
- **Sharing a single cache across multiple top-level Map calls** — each top-level call allocates a fresh cache. Sharing across calls would require user-managed lifetime, which is the per-call options work.
- **Cycle detection without preservation** (i.e., throw on cycle instead of break it) — v1 is "PreserveReferences" semantics only. A "DetectCycle and throw" mode is conceivable but not requested.
- **Inner-only PreserveReferences without outer-also-PR.** Flagging `Employee → EmployeeDto` but NOT `Department → DepartmentDto`, then calling `mapper.Map<DepartmentDto>(dept)` — the outer call doesn't allocate a context, so inner Employee maps run without cycle-safety. Documented as a v1 limitation; user must put the flag on the OUTERMOST typemap of any potentially-cyclic graph. v3 may relax this with call-graph reachability analysis at config time.
- **`MappingContext` as a public type for direct user instantiation.** Users do not create `MappingContext` instances; they only see the effects of cycle-safe mapping. v1 keeps the type internal.

---

## 2. Architecture Overview

### 2.1 Lazy context allocation, universal threading, single insertion point at the public API boundary

```
mapper.Map<TDest>(src) / Map<TSrc, TDest>(src) / Map<TSrc, TDest>(src, dest)
   │
   ▼
IMapper.Map dispatch
   │
   ├── registry.GetTypeMap(typePair) → TypeMap (existing)
   │
   ├── if (typeMap.PreserveReferences)
   │       ctx = new MappingContext()
   │   else
   │       ctx = null
   │
   ├── delegate.Invoke(src, ctx) → invoke compiled lambda
   │   │
   │   ▼
   │   Compiled lambda body (per typemap):
   │   │
   │   ├── if (ctx is not null && srcType.IsClass)
   │   │       if (ctx.TryGet(src, dstType, out cached))
   │   │           return cached;       ◄─── cache hit: skip body
   │   │
   │   ├── dst = new TDst();             // allocate (or use existing for update-in-place)
   │   │
   │   ├── if (ctx is not null && srcType.IsClass)
   │   │       ctx.Register(src, dstType, dst);   ◄─── pre-population: breaks cycles
   │   │
   │   ├── BeforeMap hooks (existing)
   │   ├── per-property emit (existing) — nested calls pass `ctx` through
   │   ├── AfterMap hooks (existing)
   │   │
   │   └── return dst;
   │
   └── return result
```

### 2.2 Key design decisions

1. **Context allocated at the public-API boundary, not inside compiled lambdas.** When `IMapper.Map` is called, it inspects `typeMap.PreserveReferences`. If `true`, allocate fresh `MappingContext`. If `false`, pass `null`. This keeps the allocation off the hot path of non-PR maps.

2. **Universal `MappingContext?` parameter on every compiled lambda signature.** Today's signature `Func<TSrc, TDst>` becomes `Func<TSrc, MappingContext?, TDst>`. Public Map calls always pass either a fresh context or null. Cost on the OFF path: one parameter passed by reference + one nullable check per nested invoke (≈1-2 ns per call; well below v1's existing per-call overhead).

3. **Cache lookup gated on `srcType.IsClass`.** Value-type sources bypass the cache (boxed value types each get separate identities — cache would never hit). Codegen knows the source type at build time, so this is a compile-time branch (no runtime check). For `Func<TStruct, …>` typemaps the cache code is omitted entirely from the lambda body.

4. **Pre-population registration is what breaks cycles.** The order inside each compiled lambda body is: cache check → allocate dst → register cache → BeforeMap → populate → AfterMap → return. The recursive nested call from the populate phase finds `src` already registered, returns the partially-constructed `dst` immediately, and the outer call continues populating. By the time control returns to the user, `dst` is fully populated and back-references point at it.

5. **Update-in-place** (`Map(src, existingDest)`) variant: skip the allocate step (use `existingDest` as `dst`); cache registration still seeds the cache with `(src, existingDest)`. Back-references resolve to `existingDest`. Existing v1 update-in-place codegen for non-PR maps is untouched.

### 2.3 New components

| Component | Type | Lives in | Responsibility |
|---|---|---|---|
| `MappingContext` | `internal sealed class` | `src/Atlas/Internal/MappingContext.cs` (new) | Holds the per-call instance cache. Two methods: `bool TryGet(object src, Type dstType, out object? dst)` and `void Register(object src, Type dstType, object dst)`. Backed by `Dictionary<CacheKey, object>` with custom reference-equality comparer. |
| `IMappingExpression<TSrc, TDst>.PreserveReferences()` | instance method | `src/Atlas/Configuration/IMappingExpression.cs` + `MappingExpression.cs` | Sets `TypeMap.PreserveReferences = true`. Returns the same `IMappingExpression` for fluent chaining. |
| `TypeMap.PreserveReferences` | `bool` field | `src/Atlas/Internal/TypeMap.cs` | New field. Defaults to false. Read by every consumer listed below. |
| `ConfigurationValidator.ValidatePreserveReferences` | static method | `src/Atlas/Internal/ConfigurationValidator.cs` | Rejects `PreserveReferences + ConvertUsing` combination. |
| `ProjectionPlanBuilder.RejectPreserveReferencesOrThrow` | static method | `src/Atlas.Projections/Internal/ProjectionPlanBuilder.cs` | Mirror of `RejectHooksOrThrow` and `RejectDynamicOrThrow`. Called from `BuildBody`. |

### 2.4 Modified components

| Component | Change |
|---|---|
| Every compiled lambda signature | Adds `MappingContext? ctx` as the second parameter. |
| `IMapper.Map<TSrc, TDst>` overloads | Allocate `MappingContext` if `typeMap.PreserveReferences`; pass through to compiled lambda. |
| `MappingInvoker.Invoke<TSrc, TDst>` and `InvokeUpdate<TSrc, TDst>` | Accept and pass through `MappingContext?`. |
| `MappingInvoker.InvokeToList`, `InvokeToArray`, `InvokeToDictionary` | Accept and pass through `MappingContext?`. |
| `MappingInvoker.ConvertObjectTo<T>`, `SerializeValue`, `SerializeCollection<T>`, `SerializeDictionary<,>` | Accept and pass through `MappingContext?` (DynamicMapping #10 helpers; their reflection-based dispatch updates pass `ctx` to the closed `Invoke<,>` call). |
| `ExecutionPlanBuilder.Build` / `BuildUpdate` | Emits the cache check + register block at the top of POCO-typed lambdas when `srcType.IsClass`. (Omitted for value-type sources.) |
| `DynamicPlanBuilder` codegen | Threads `ctx` through every nested `MappingInvoker.Invoke*` emit; reflection-based dispatch in helpers updated. |
| `ProjectionPlanBuilder.BuildBody` | Calls `RejectPreserveReferencesOrThrow(tm)` adjacent to existing `RejectHooksOrThrow`/`RejectDynamicOrThrow`. |
| `ProjectionCompatibility.IsTypeMapProjectable` | Returns false for `tm.PreserveReferences == true` with reason "PreserveReferences is not projectable — LINQ providers cannot model identity tracking." |
| `ConfigurationValidator.Validate` | Calls `ValidatePreserveReferences(tm, errors)` in the per-typemap loop. |
| `InheritanceMerger.CopyConfig` | Propagates `PreserveReferences` flag base → derived. |
| `MappingExpression.ReverseMap` | Propagates `PreserveReferences` flag to the reverse pair. |
| `OpenGenericTypeMap.Materialize` | Propagates `PreserveReferences` from template to materialized closed pair. |
| `DynamicShape.MaterializeTypeMap` | N/A — dynamic TypeMaps are convention-only and have no fluent surface; cannot have PreserveReferences set. |

### 2.5 Bug-N audit reminders

**Bug-4 (cross-package consumer audit):** the new `TypeMap.PreserveReferences` field is read by:
- `IMapper.Map` (allocates context)
- `ExecutionPlanBuilder.Build` / `BuildUpdate` (emits cache code; respected by all current emit-site helpers)
- `InheritanceMerger.CopyConfig` (propagates base→derived)
- `MappingExpression.ReverseMap` (propagates to reverse pair)
- `OpenGenericTypeMap.Materialize` (propagates template→closed)
- `ConfigurationValidator.ValidatePreserveReferences` (validates the ConvertUsing-combo rule)
- `ProjectionCompatibility.IsTypeMapProjectable` (rejects)
- `ProjectionPlanBuilder.RejectPreserveReferencesOrThrow` (rejects)

Audit at implementation time: grep for `PreserveReferences` after each task; verify each consumer behaves correctly.

**Bug-5 (scope-identifying TypeMap metadata propagation):** `PreserveReferences` is scope-identifying; default propagation = "yes propagate to siblings/derived." Three propagation sites:
- `InheritanceMerger.CopyConfig`: base.PreserveReferences → derived.PreserveReferences
- `MappingExpression.ReverseMap`: forward.PreserveReferences → reverse.PreserveReferences
- `OpenGenericTypeMap.Materialize`: template.PreserveReferences → closedPair.PreserveReferences

All three sites must write the field. Implementation tasks include explicit propagation tests for each.

**Bug-7 (multi-stage routing claims):** the propagation rule "context allocated iff top-level typemap is flagged" is single-stage and explicit. No "naturally handles itself" claims. The `ctx is null` check is the explicit signal.

---

## 3. Public API Surface

### 3.1 No new public types beyond the fluent method

The feature ships **one new public method** (`IMappingExpression<TSrc, TDst>.PreserveReferences()`). No other public type or extension changes. `MappingContext` is `internal sealed` — users do not see it.

### 3.2 `IMappingExpression<TSrc, TDst>.PreserveReferences()`

```csharp
namespace Atlas;

public interface IMappingExpression<TSrc, TDst>
{
    // ... existing members ...

    /// <summary>
    /// Marks this typemap as cycle-safe. When the user calls IMapper.Map (any overload) and the
    /// typemap matched at the top level has PreserveReferences enabled, Atlas allocates a per-call
    /// instance cache and threads it through every nested map call. Cycles (person.Boss = person)
    /// terminate; multiply-referenced source instances produce a single destination instance shared
    /// across all back-references in the destination graph.
    ///
    /// The flag propagates through:
    /// - .ReverseMap() — the reverse typemap inherits the flag.
    /// - Include&lt;Base, Derived&gt;() — derived typemaps inherit the flag.
    /// - Open-generic templates → closed-pair materializations.
    ///
    /// Cannot be combined with ConvertUsing&lt;TConverter&gt;(); the validator throws
    /// AtlasConfigurationException at AssertConfigurationIsValid() time.
    ///
    /// Atlas.Projections rejects PreserveReferences typemaps — LINQ providers cannot model identity
    /// tracking. Use IMapper.Map for cycle-safe in-memory mapping; use ProjectTo only for
    /// non-cyclic projections.
    /// </summary>
    /// <returns>This expression, for fluent chaining.</returns>
    IMappingExpression<TSrc, TDst> PreserveReferences();
}
```

### 3.3 Recognized call patterns

```csharp
// Self-cycle — alice.Boss = alice
cfg.CreateMap<Person, PersonDto>().PreserveReferences();
var alice = new Person { Name = "Alice" };
alice.Boss = alice;
var dto = mapper.Map<PersonDto>(alice);   // dto.Boss == dto (same instance)

// Mutual cycle — manager and report point at each other
cfg.CreateMap<Employee, EmployeeDto>().PreserveReferences();
var manager = new Employee();
var report = new Employee();
manager.DirectReports = [report];
report.Manager = manager;
var dto = mapper.Map<EmployeeDto>(manager);
// dto.DirectReports[0].Manager == dto (same instance)

// Shared reference — single Department shared by many Employees
cfg.CreateMap<Department, DepartmentDto>().PreserveReferences();
cfg.CreateMap<Employee, EmployeeDto>();   // no flag needed — propagates from Department
var sales = new Department();
var emp1 = new Employee { Department = sales };
var emp2 = new Employee { Department = sales };
sales.Employees = [emp1, emp2];
var dto = mapper.Map<DepartmentDto>(sales);
// dto.Employees[0].Department == dto.Employees[1].Department (same instance, single allocation)

// ReverseMap propagation
cfg.CreateMap<Person, PersonDto>().PreserveReferences().ReverseMap();
// Both forward (Person → PersonDto) and reverse (PersonDto → Person) typemaps have PreserveReferences.

// Inheritance propagation
cfg.CreateMap<Person, PersonDto>()
   .PreserveReferences()
   .Include<Manager, ManagerDto>();
cfg.CreateMap<Manager, ManagerDto>();
// Manager → ManagerDto inherits PreserveReferences via InheritanceMerger.CopyConfig.

// Update-in-place
var existingDto = new PersonDto();
mapper.Map(alice, existingDto);
// alice.Boss == alice → existingDto.Boss == existingDto (the existing instance, not a fresh one)
```

### 3.4 No public `MappingContext` type

`MappingContext` is `internal sealed`. Users do NOT see it. The compiled lambda signature `Func<TSrc, MappingContext?, TDst>` is an implementation detail of `MappingInvoker.Invoke` and friends — those static helpers expose only the user-facing signatures.

### 3.5 Exception surface — additive only

| Exception | When | Phase |
|---|---|---|
| `AtlasConfigurationException` (existing) | `cfg.CreateMap<S, D>().PreserveReferences().ConvertUsing<TConv>()` (or vice versa) — the combo is invalid. Raised by `AssertConfigurationIsValid()`. | Config-time |
| `AtlasMappingException` (existing) | NOT raised by PreserveReferences in v1. Cycles that the cache breaks → no exception, just normal mapping. | n/a |
| `AtlasProjectionException` (existing) | `ProjectTo<TDst>()` against a PreserveReferences typemap. Single rejection at projection-build time. | Build-time |

### 3.6 No `MemberList` validation surface change

`AssertConfigurationIsValid()` runs the existing per-member checks normally on PreserveReferences typemaps; the flag only changes runtime behavior, not what counts as "covered." Plus the new `ValidatePreserveReferences` rule (§3.5 row 1).

### 3.7 No new `IMapper` overloads

The `IMapper.Map<TSrc, TDst>(TSrc)`, `Map<TDst>(object)`, `Map<TSrc, TDst>(TSrc, TDst)` signatures are unchanged. The `MappingContext` allocation is internal — users don't pass anything.

---

## 4. Internal Data Shape

### 4.1 `MappingContext` — the per-call instance cache

```csharp
namespace Atlas.Internal;

using System.Collections.Generic;

/// <summary>
/// Per-call instance cache for cycle-safe mapping (Atlas v2 #11 — see PreserveReferences).
/// Allocated by IMapper.Map at the public-API boundary when typeMap.PreserveReferences is true;
/// threaded through every nested map call as a MappingContext? parameter on compiled lambdas.
/// One MappingContext instance lives for the duration of one top-level Map call; abandoned afterward.
/// Not thread-safe — each call gets its own instance.
/// </summary>
internal sealed class MappingContext
{
    private readonly Dictionary<CacheKey, object> _cache = new(CacheKey.Comparer);

    /// <summary>
    /// Look up the destination instance previously registered for (source, destinationType).
    /// Returns true on hit; the caller skips body execution and returns the cached destination.
    /// </summary>
    internal bool TryGet(object source, Type destinationType, out object? destination)
    {
        if (_cache.TryGetValue(new CacheKey(source, destinationType), out var found))
        {
            destination = found;
            return true;
        }
        destination = null;
        return false;
    }

    /// <summary>
    /// Register a freshly-allocated (or update-in-place existing) destination BEFORE its members are
    /// populated. Pre-population registration is what breaks cycles: any nested map call that resolves
    /// back to source finds destination in the cache and returns it (partially-populated at that
    /// moment, fully-populated by the time control returns to the user).
    /// </summary>
    internal void Register(object source, Type destinationType, object destination)
    {
        _cache[new CacheKey(source, destinationType)] = destination;
    }

    /// <summary>
    /// Cache key: source instance (by reference) + destination type. Two calls with the same source
    /// and different destination types get separate slots.
    /// </summary>
    private readonly record struct CacheKey(object Source, Type DestinationType)
    {
        internal static readonly IEqualityComparer<CacheKey> Comparer = new RefEqComparer();

        private sealed class RefEqComparer : IEqualityComparer<CacheKey>
        {
            public bool Equals(CacheKey x, CacheKey y) =>
                ReferenceEquals(x.Source, y.Source) && x.DestinationType == y.DestinationType;

            public int GetHashCode(CacheKey obj) =>
                HashCode.Combine(
                    System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj.Source),
                    obj.DestinationType);
        }
    }
}
```

### 4.2 Why a dedicated record-struct key + custom comparer

- `Dictionary<(object, Type), object>` with a default comparer would call `object.Equals` on the source — wrong (could trigger user-defined equality, which on a domain object is usually value-equality, breaking the "by-reference" guarantee).
- `ReferenceEqualityComparer.Instance` exists in .NET ≥5 but only handles `IEqualityComparer<object?>`; we need a composite key. Hence the custom `RefEqComparer`.
- `RuntimeHelpers.GetHashCode` returns the identity hash code regardless of any user-defined `GetHashCode` override.

### 4.3 `TypeMap.PreserveReferences` field

```csharp
internal sealed class TypeMap
{
    // ... existing fields ...

    /// <summary>
    /// True when this typemap was registered with IMappingExpression.PreserveReferences()
    /// (Atlas v2 #11). Causes IMapper.Map to allocate a MappingContext at the public-API boundary;
    /// causes ExecutionPlanBuilder to emit cache-check + cache-register instructions in the compiled
    /// lambda; causes ConfigurationValidator to reject ConvertUsing combos; causes Atlas.Projections
    /// to reject the typemap at projection-build time.
    /// </summary>
    public bool PreserveReferences { get; set; }
}
```

`{ get; set; }` (not init-only) — propagation paths (`InheritanceMerger.CopyConfig`, `MappingExpression.ReverseMap`, `OpenGenericTypeMap.Materialize`) need to write the field after construction.

### 4.4 Compiled lambda signature change

**Before (v1 / current):**
```csharp
Func<TSrc, TDst>           // for Map<TSrc, TDst>(TSrc)
Action<TSrc, TDst>         // for Map<TSrc, TDst>(TSrc, TDst) — update-in-place
```

**After (post-#11):**
```csharp
Func<TSrc, MappingContext?, TDst>           // signature on every compiled lambda
Action<TSrc, MappingContext?, TDst>         // signature on every update-in-place compiled lambda
```

`MappingContext?` is the SECOND parameter (after `src`, before `dst` if applicable). Always nullable; null means "no PreserveReferences active anywhere in this call chain."

**Migration impact:** every `MappingInvoker.Invoke<…>` and `InvokeUpdate<…>` static helper must be updated to accept and pass through the parameter. Every `MappingInvoker.InvokeToList<…>`, `InvokeToArray<…>`, `InvokeToDictionary<…>` collection helper does too. Every `Expression.Call(typeof(MappingInvoker), nameof(Invoke), …)` emit site in `ExecutionPlanBuilder`, `DynamicPlanBuilder`, `ProjectionPlanBuilder` must also be updated.

This is a wide-but-mechanical change. Tests for non-PR code paths must continue passing because the lambda body is unchanged when `ctx is null` (the cache-check block is gated on `ctx != null && srcType.IsClass`).

### 4.5 `MappingContext?` flow through the call graph

```
mapper.Map<DepartmentDto>(dept)
  │
  ├── tm = registry.GetTypeMap((Department, DepartmentDto))
  ├── ctx = tm.PreserveReferences ? new MappingContext() : null
  ├── delegate.Invoke(dept, ctx)                                    // top-level call
  │     │
  │     ├── (cache check — ctx not null + class source → check cache, miss)
  │     ├── new DepartmentDto()
  │     ├── ctx.Register(dept, typeof(DepartmentDto), dst)
  │     │
  │     ├── populate dst.Manager:
  │     │     MappingInvoker.Invoke<Employee, EmployeeDto>(registry, dept.Manager, ctx)
  │     │       │
  │     │       ├── (cache check — same ctx, finds nothing on first call)
  │     │       ├── new EmployeeDto()
  │     │       ├── ctx.Register(dept.Manager, typeof(EmployeeDto), nestedDst)
  │     │       ├── populate nestedDst.Department:
  │     │       │     MappingInvoker.Invoke<Department, DepartmentDto>(registry, dept.Manager.Department, ctx)
  │     │       │       │
  │     │       │       └── if dept.Manager.Department === dept (shared ref)
  │     │       │             ctx.TryGet(dept, typeof(DepartmentDto)) HITS
  │     │       │             return outer dst (the partially-populated DepartmentDto)
  │     │       │             ◄─── cycle broken, shared reference preserved
  │     │       │
  │     │       └── return nestedDst
  │     │
  │     └── return dst
  │
  └── (ctx abandoned — eligible for GC)
```

### 4.6 Codegen-emit conditions for cache logic

**Question:** does EVERY compiled lambda always emit the cache-check + cache-register block (even non-PR typemaps), or only PR typemaps?

**Answer (v1):** the cache-check + cache-register block is emitted **on every POCO-typed compiled lambda where the source type is a reference type.** Even non-PR typemaps emit it.

**Why universal emit:** because `PreserveReferences` propagates DOWN at runtime (per the activation rule in §1.1 Goal 4), a non-PR typemap can be invoked with a non-null `ctx` from an outer PR call. To honor the propagation, the non-PR typemap must consult and register the cache.

**Cost on the OFF path:** the emitted block looks like:
```csharp
if (ctx is not null /* && srcType.IsClass — compile-time-known true */)
    if (ctx.TryGet(src, typeof(TDst), out var cached))
        return (TDst)cached;
```
On the OFF path (`ctx is null`), the JIT short-circuits the `is not null` check on the first instruction; cost ≈ 1 ns. Acceptable.

**For value-type sources** (`TSrc : struct` with `srcType.IsClass == false`): the entire cache-check + cache-register block is **omitted at codegen time**. No runtime cost.

---

## 5. Codegen — cache emit + propagation

### 5.1 Universal lambda body shape

The compiled lambda body for every POCO-typed typemap (when `srcType.IsClass`) gets a 3-line preamble inserted at the top, between parameter binding and the existing body:

```csharp
(TSrc src, MappingContext? ctx) =>
{
    // ── Atlas v2 #11 cache preamble (emitted only when srcType.IsClass) ──
    if (ctx is not null && ctx.TryGet(src, typeof(TDst), out var cached))
        return (TDst)cached;

    var dst = new TDst();          // existing v1 allocation (or ctor-init for records)

    if (ctx is not null)
        ctx.Register(src, typeof(TDst), dst);

    // ── existing v1 body (BeforeMap, member emit, AfterMap, return dst) ──
    // ... unchanged ...

    return dst;
}
```

For value-type sources (`TSrc : struct`): the cache preamble is omitted entirely — no `ctx is not null` check, no registration. The lambda is `Func<TSrc, MappingContext?, TDst>` but the parameter is unused.

### 5.2 Update-in-place variant

For `Map(src, existingDest)`:

```csharp
(TSrc src, MappingContext? ctx, TDst dst) =>
{
    if (ctx is not null && ctx.TryGet(src, typeof(TDst), out var cached))
        return (TDst)cached;       // cache hit — return cached, IGNORE existingDest

    if (ctx is not null)
        ctx.Register(src, typeof(TDst), dst);   // seed cache with existing dest

    // ... existing v1 update-in-place body ...

    return dst;
}
```

**Note on cache-hit-vs-existingDest semantics:** if the same source has already been mapped in this call chain, the cache hit returns the previously-cached destination, NOT `existingDest`. This is correct: cycle-breaking back-references should resolve to whatever was registered first in this call (the cache-registered instance), regardless of what `existingDest` happens to be. In practice this only matters in pathological setups; the common case is that update-in-place is called once at the top level and the registered cache instance IS `existingDest`.

### 5.3 Nested map call emit

Every `Expression.Call(typeof(MappingInvoker), nameof(Invoke), …)` site in the codegen passes `ctx` through:

**Before (v1):**
```csharp
Expression.Call(invokeMethodInfo,
    Expression.Constant(registry),
    sourceMemberAccess);
```

**After (post-#11):**
```csharp
Expression.Call(invokeMethodInfo,    // signature: (MapperRegistry, TSrc, MappingContext?) → TDst
    Expression.Constant(registry),
    sourceMemberAccess,
    ctxParam);                        // the lambda's MappingContext? parameter
```

`ctxParam` is the second `ParameterExpression` of the enclosing lambda (every codegen site already accepts both source and now context). This propagates the same `ctx` through every nested call without copy or transformation.

Equivalent for `InvokeUpdate<…>` (update-in-place), `InvokeToList<…>` (collection elements), `InvokeToArray<…>`, `InvokeToDictionary<…>` (typed-POCO dictionary values).

### 5.4 Helpers updated to accept `MappingContext?`

`MappingInvoker.cs` signatures change:

```csharp
// Before:
public static TDestination Invoke<TSource, TDestination>(MapperRegistry registry, TSource source) { ... }
public static TDestination InvokeUpdate<TSource, TDestination>(MapperRegistry registry, TSource source, TDestination destination) { ... }
public static List<TDestination> InvokeToList<TSource, TDestination>(MapperRegistry registry, IEnumerable<TSource>? source) { ... }
public static TDestination[] InvokeToArray<TSource, TDestination>(MapperRegistry registry, IEnumerable<TSource>? source) { ... }
public static Dictionary<TKDest, TVDest> InvokeToDictionary<TKSrc, TVSrc, TKDest, TVDest>(MapperRegistry registry, Dictionary<TKSrc, TVSrc>? source) where TKSrc : notnull where TKDest : notnull { ... }

// After:
public static TDestination Invoke<TSource, TDestination>(MapperRegistry registry, TSource source, MappingContext? ctx) { ... }
public static TDestination InvokeUpdate<TSource, TDestination>(MapperRegistry registry, TSource source, MappingContext? ctx, TDestination destination) { ... }
public static List<TDestination> InvokeToList<TSource, TDestination>(MapperRegistry registry, IEnumerable<TSource>? source, MappingContext? ctx) { ... }
public static TDestination[] InvokeToArray<TSource, TDestination>(MapperRegistry registry, IEnumerable<TSource>? source, MappingContext? ctx) { ... }
public static Dictionary<TKDest, TVDest> InvokeToDictionary<TKSrc, TVSrc, TKDest, TVDest>(MapperRegistry registry, Dictionary<TKSrc, TVSrc>? source, MappingContext? ctx) where TKSrc : notnull where TKDest : notnull { ... }
```

The `ctx` parameter goes immediately after `source` in every signature for consistency. Each helper's body:
1. Looks up the cached delegate from `registry`.
2. Casts to the appropriate `Func<TSource, MappingContext?, TDestination>` (or `Action<...>` for update).
3. Invokes with `(source, ctx)` — for collection helpers, iterates and calls per element with the same `ctx`.

`InvokeToList` body (the cycle-safety propagation through collections):

```csharp
public static List<TDestination> InvokeToList<TSource, TDestination>(
    MapperRegistry registry,
    IEnumerable<TSource>? source,
    MappingContext? ctx)
{
    if (source is null) return [];
    var list = new List<TDestination>();
    foreach (var item in source)
        list.Add(Invoke<TSource, TDestination>(registry, item, ctx));   // same ctx per element
    return list;
}
```

The `ctx` flows uniformly through every element call. So `List<Person>` containing `[alice, bob, alice]` deduplicates: the second `alice` element resolves via cache hit, returning the same `PersonDto` instance allocated for the first one.

### 5.5 Public-API boundary: `IMapper.Map` allocates the context

In `Mapper.cs`, the existing public Map overloads change to allocate `MappingContext` based on the typemap's flag:

```csharp
public TDestination Map<TSource, TDestination>(TSource source)
{
    if (source is null) return default!;
    var pair = new TypePair(typeof(TSource), typeof(TDestination));
    var tm = _registry.GetTypeMap(pair) ?? throw new AtlasMappingException(...);
    var ctx = tm.PreserveReferences ? new MappingContext() : null;
    return MappingInvoker.Invoke<TSource, TDestination>(_registry, source, ctx);
}
```

Three overloads change:
- `Map<TSrc, TDst>(TSrc)` — fresh-map.
- `Map<TSrc, TDst>(TSrc, TDst)` — update-in-place. Calls `InvokeUpdate` with the allocated context.
- `Map<TDst>(object)` — runtime-typed source. Allocates context based on the typemap fetched via reflection.

The `ctx` allocation happens ONCE per top-level Map call. Every nested call inside the compiled lambda graph reuses the same instance.

### 5.6 Reflection-based dispatch in `MappingInvoker.ConvertObjectTo<T>` and `SerializeValue`

(From DynamicMapping #10.) These already do nested dispatch via reflection on `MappingInvoker.Invoke<,>`. The signature change requires updating:

```csharp
// In MappingInvoker.ConvertObjectTo<T> and SerializeValue:
var invoke = typeof(MappingInvoker)
    .GetMethod(nameof(Invoke))!
    .MakeGenericMethod(srcType, dstType);
return (T?)invoke.Invoke(null, new object?[] { registry, value, ctx });   // pass ctx through
```

These helpers must be updated to accept and pass `MappingContext? ctx` themselves. Their callers (DynamicPlanBuilder's emitted lambda bodies) pass through the same `ctx` parameter. So DynamicMapping and PreserveReferences compose naturally — a PreserveReferences-flagged dynamic-shaped call would thread `ctx` through both layers.

### 5.7 Allocation budget when PreserveReferences is OFF

Zero new allocations on the OFF path:
- No `MappingContext` allocation (gated by `tm.PreserveReferences`).
- The `ctx is not null` branch in compiled lambdas falls through immediately; JIT eliminates downstream `ctx.TryGet` / `ctx.Register` calls.
- `InvokeToList` and friends pass `null` through.

The OFF-path cost is: one extra parameter on every nested-call frame + one nullable-check per nested call. Both are < 1 ns each on modern CPUs.

### 5.8 Allocation budget when PreserveReferences is ON

- One `MappingContext` per top-level Map call.
- One `Dictionary<CacheKey, object>` per top-level call (lazy-initialized inside `MappingContext`).
- One dictionary entry per nested POCO map call (cache.Register).
- Otherwise identical to OFF path.

For a graph with N nested POCO maps: N+1 allocations beyond the OFF baseline (1 context + N entries + 1 dict's internal storage).

---

## 6. Validator + Atlas.Projections rejection

### 6.1 New validator rule: `ValidatePreserveReferences`

`ConfigurationValidator.Validate` walks `registry.AllTypeMaps`. For each typemap with `PreserveReferences = true`, it checks that no `ConvertUsing` configuration is set:

```csharp
// In ConfigurationValidator.cs, called from Validate's per-TypeMap loop:
private static void ValidatePreserveReferences(TypeMap tm, List<string> errors)
{
    if (!tm.PreserveReferences) return;

    if (tm.CustomConverter is not null)
    {
        errors.Add(
            $"TypeMap {tm.SourceType} → {tm.DestinationType} has both PreserveReferences " +
            $"and ConvertUsing<{tm.CustomConverter}>(). These are incompatible: ConvertUsing " +
            "replaces the mapping body, leaving no member-emit pipeline for the cycle-cache to " +
            "wrap. Remove one of the two registrations. (Atlas v2 #11 — see " +
            "docs/Atlas-Design-ReferenceHandling.md §3.5 for v3 follow-up to expose context " +
            "to converters.)");
    }
}
```

Called from the per-typemap loop in `Validate`:

```csharp
foreach (var tm in registry.AllTypeMaps)
{
    if (tm.IsDynamic) continue;     // existing — Atlas v2 #10
    // ... existing validation calls ...
    ValidatePreserveReferences(tm, errors);   // NEW
}
```

The error is surfaced via `AtlasConfigurationException` at `AssertConfigurationIsValid()` time, just like every other validator rule.

### 6.2 Atlas.Projections — dual-gate rejection

Mirrors the established pattern from Hooks (#5), DynamicMapping (#10):

**Gate 1 — `ProjectionCompatibility.IsTypeMapProjectable`** (called by `ProjectionValidator.Walk`):

```csharp
internal static bool IsTypeMapProjectable(TypeMap tm, out string reason)
{
    if (tm.IsDynamic)
    {
        reason = "dynamic-shape mapping is not projectable";
        return false;
    }

    if (tm.PreserveReferences)   // NEW — Atlas v2 #11
    {
        reason = "PreserveReferences is not projectable — LINQ providers cannot model identity tracking";
        return false;
    }

    // ... existing checks (hooks, etc.) ...

    reason = "";
    return true;
}
```

**Gate 2 — `ProjectionPlanBuilder.RejectPreserveReferencesOrThrow`** (called from `BuildBody` adjacent to `RejectHooksOrThrow` and `RejectDynamicOrThrow`):

```csharp
private static void RejectPreserveReferencesOrThrow(TypeMap tm)
{
    if (!tm.PreserveReferences) return;
    throw new AtlasProjectionException(new List<ProjectionDiagnostic>
    {
        new(tm.SourceType, tm.DestinationType, "(PreserveReferences)",
            $"map has PreserveReferences set; LINQ providers cannot model identity tracking. " +
            $"Use mapper.Map<>() instead, or remove PreserveReferences for this typemap.")
    });
}
```

In `ProjectionPlanBuilder.BuildBody`, called immediately after `RejectHooksOrThrow(tm); RejectDynamicOrThrow(tm);`:

```csharp
RejectHooksOrThrow(tm);
RejectDynamicOrThrow(tm);
RejectPreserveReferencesOrThrow(tm);   // NEW
```

### 6.3 Why dual-gate

- `ProjectionValidator.Walk` runs first when a user calls `queryable.ProjectTo<TDst>(cfg)`. Gate 1 catches the issue and produces a clean `AtlasProjectionException` with all incompatibility reasons aggregated across the typemap graph.
- `ProjectionPlanBuilder.BuildBody` is the runtime backstop for callers that bypass the validator path. Gate 2 catches it there.

This is the EXACT pattern locked in for DynamicMapping #10 and traced empirically by that feature's spec reviewer (Task 9 of #10). PreserveReferences inherits the architecture verbatim.

### 6.4 No changes to projection codegen for non-PR typemaps

Non-PreserveReferences typemaps continue to project via the existing `ProjectionPlanBuilder` machinery without modification. The new `MappingContext?` parameter on compiled lambda signatures does NOT affect projection codegen — projection emits a member-init `Expression.MemberInit` tree consumed by LINQ providers, not a delegate that needs invocation arguments.

### 6.5 Summary table

| Component | Behavior on PreserveReferences typemap |
|---|---|
| `IMapper.Map` | Allocates `MappingContext`; threads through. ✅ Cycle-safe. |
| `ConfigurationValidator.Validate` | Walks typemap normally; adds `ValidatePreserveReferences` check (rejects ConvertUsing combo). |
| `ProjectionValidator.Walk` | Rejects via `ProjectionCompatibility.IsTypeMapProjectable` returning false. |
| `ProjectionPlanBuilder.BuildBody` | Rejects via `RejectPreserveReferencesOrThrow` (defensive backstop). |
| `Atlas.Extensions.DependencyInjection.AddAtlas` | No change — DI shape unaffected. |

---

## 7. Edge cases & contract details

### 7.1 Interaction matrix

| Interaction | Behavior |
|---|---|
| `Include<Base, Derived>` (Inheritance #2) | Flag propagates base → derived via existing `InheritanceMerger.CopyConfig`. If `Person → PersonDto` is flagged, `Manager → ManagerDto` (where Manager : Person) inherits the flag. |
| `ReverseMap()` (#4) | Flag propagates to the reverse pair (per memory's Bug-5 lesson default for scope-identifying metadata). |
| `BeforeMap` / `AfterMap` (Hooks #5) | Hooks fire on FIRST allocation only; cache hits skip both hook and body. |
| `AddTransform<T>` (ValueTransformers #6) | Per-property transformers fire normally on first allocation, skipped on cache hit. |
| `Condition` / `PreCondition` (#7) | Per-property predicates fire normally on first allocation, skipped on cache hit. |
| `NullSubstitute` (#8) | Per-property substitutes fire normally on first allocation, skipped on cache hit. |
| Open generics (#9) | Open templates can be marked PreserveReferences; flag propagates to closed-pair materializations via `OpenGenericTypeMap.Materialize`. |
| Dynamic mapping (#10) | Dynamic TypeMaps cannot have PreserveReferences (no fluent surface). Already enforced by design. |
| `ConvertUsing<TConverter>` | **REJECT at config time** with `AtlasConfigurationException`: `"PreserveReferences cannot be combined with ConvertUsing — the converter replaces the body, leaving no mapping pipeline for the cache to wrap."` |
| `Atlas.Projections` | TypeMaps with PreserveReferences rejected at projection-build time via `AtlasProjectionException`. Single insertion-point matching Hooks/DynamicMapping pattern. |

### 7.2 Edge case: null source

`mapper.Map<TDest>(null)` — existing v1 short-circuit returns `default(TDest)` before any compiled lambda runs. PreserveReferences doesn't change this. Even with the flag, `null` source returns `null` destination, no context allocated.

### 7.3 Edge case: null nested source

Inside a compiled lambda body, a nested map call where the source member is null:
```csharp
if (src.Customer is null) dst.Customer = null;   // no nested call, no cache lookup
else dst.Customer = MappingInvoker.Invoke<Customer, CustomerDto>(registry, src.Customer, ctx);
```

The existing v1 codegen already handles null nested sources. Cache lookup only happens when there's a non-null source to look up.

### 7.4 Edge case: value-type sources

For typemaps with value-type sources (`struct`), the cache preamble is omitted at codegen time. Value types boxed into the cache would each be a separate boxed instance, defeating the purpose. The cache is meaningless for value types.

```csharp
// struct Point; cfg.CreateMap<Point, PointDto>().PreserveReferences();
// Compiled lambda for (Point, PointDto):
(Point src, MappingContext? ctx) =>
{
    // ── NO cache preamble emitted (srcType.IsClass == false) ──
    var dst = new PointDto();
    dst.X = src.X;
    dst.Y = src.Y;
    return dst;
}
```

The flag is silently ignored for value-type sources. (Validator could warn, but this is overly noisy for a benign no-op. Documented as a non-goal.)

### 7.5 Edge case: concurrent top-level calls

`MappingContext` is not thread-safe. Each top-level `Map` call allocates its own instance. Two threads calling `mapper.Map<PersonDto>(alice)` concurrently each get their own cache; no cross-call sharing.

The compiled delegate (`Func<TSrc, MappingContext?, TDst>`) IS thread-safe (immutable, compiled once at config-build time). Multiple threads invoking the same delegate with different `ctx` instances run independently.

### 7.6 Edge case: exception inside body after cache registration

If the lambda body throws after `ctx.Register(src, dstType, dst)` but before completing population, `dst` remains in the cache as a partially-populated object. The exception propagates out of `Map`; the cache is abandoned with the call. Next `Map` call gets a fresh cache.

**Implication:** if user code catches the exception INSIDE Atlas's compiled lambda flow (e.g., a `BeforeMap` hook that catches and continues), the partially-populated `dst` may be visible. This is documented as a known limitation; users should not catch exceptions inside hooks unless they understand the partial-state implications.

### 7.7 Edge case: open-generic with PreserveReferences

```csharp
cfg.CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>)).PreserveReferences();
```

The open-generic template's `PreserveReferences = true` flag is stored on `OpenGenericTypeMap`. When `MapperRegistry.GetTypeMap` materializes a closed pair (say `(Wrapper<int>, WrapperDto<int>)`), the materialization copies the flag onto the closed `TypeMap`. The closed pair is then a regular cycle-safe typemap.

**API surface concern:** Atlas v1's open-generic registration `cfg.CreateMap(typeof, typeof)` returns a different fluent-expression type (or `void` per design §3.6 of OpenGenerics) that may not expose `PreserveReferences()`. Implementation-time decision: either (a) extend the open-generic fluent return to include `PreserveReferences()`, or (b) require users to set the flag at the closed-pair level.

**Decision (v1):** option (a) — extend the open-generic fluent surface to include `PreserveReferences()`. Single one-line method addition. Closed pairs materialized from the template inherit the flag.

### 7.8 Edge case: hook observation of partial state

Per §8.3 of the Risks section: a `BeforeMap` or `AfterMap` hook firing during the populate phase of a cyclic source may observe destinations that are partially-populated. Documented as: "if your hooks inspect cyclically-referenced destinations, expect to see partial state at hook-fire time."

Workaround: hooks should only inspect their own typemap's source/destination, not transitively-referenced instances.

### 7.9 Threading: cache lifetime per call

The cache is allocated at the top of `IMapper.Map` and abandoned when the call returns. Long-lived application state cannot accumulate cache entries — each call is independent. This is a deliberate design choice (no cross-call deduplication; each call's memory cost is bounded by its graph size).

If a user wants cross-call deduplication (e.g., a long-running service that maps many graphs sharing common sub-objects), v3's per-call options will provide explicit cache injection.

---

## 8. Test plan

**Test count target:** ~45–55 net new tests. Brings the v2 baseline of 575 → ~625 after merge.

### 8.1 File layout

```
tests/Atlas.Tests/
  ├── Internal/
  │   └── MappingContextTests.cs                          (~6 tests)
  ├── MapperPreserveReferencesTests.cs                    (~20 tests)
  ├── MapperPreserveReferencesPropagationTests.cs         (~9 tests)
  ├── MapperPreserveReferencesUpdateInPlaceTests.cs       (~5 tests)
  ├── ConfigurationValidatorPreserveReferencesTests.cs    (~3 tests)
tests/Atlas.Projections.Tests/
  └── ProjectionRejectsPreserveReferencesTests.cs         (~2 tests)
```

### 8.2 `MappingContextTests` (~6 tests, internal unit tests)

Pure-unit tests on the `MappingContext` class itself, no `IMapper` involvement:

- `TryGet_ReturnsFalse_WhenSourceNotRegistered` — fresh context, lookup misses.
- `Register_ThenTryGet_ReturnsRegisteredInstance` — happy path.
- `Register_SameSource_DifferentDestinationTypes_StoresSeparately` — `(alice, typeof(PersonDto))` ≠ `(alice, typeof(PersonSummary))`.
- `Register_TwoSourceInstances_WithEqualEqualsButDifferentReferences_StoresSeparately` — verifies `ReferenceEquals` not `Equals`. Uses two `Person` instances with `Id = 42` (assuming `Person.Equals` is value-based).
- `Register_OverwriteSameKey_KeepsLastValue` — second `Register` for the same `(src, dstType)` overwrites first. Documents the contract.
- `TryGet_AfterMultipleRegisters_FindsAll` — sanity stress test with ~5 registrations.

### 8.3 `MapperPreserveReferencesTests` (~20 tests, end-to-end behavior)

The headline test file. Covers cycle-breaking, shared-reference dedup, fresh-map semantics, OFF-path verification.

**Cycle-breaking (~5):**
- `SelfCycle_PersonBossEqualsSelf_TerminatesAndPreservesIdentity` — `alice.Boss = alice`; `dto.Boss == dto` (same instance via `Assert.Same`).
- `MutualCycle_ManagerAndReportPointAtEachOther_BothEdgesPreserved` — `manager.Reports[0] == report; report.Manager == manager` → `dtoManager.Reports[0].Manager == dtoManager`.
- `LongerCycle_ABThenBA_TerminatesAndPreservesIdentity` — 3-node cycle.
- `SelfCycleViaCollection_PersonFriendsContainsSelf` — `alice.Friends.Add(alice)` → `dto.Friends[0] == dto`.
- `CycleAcrossCollectionElements_BothElementsReferenceEachOther` — `[a, b]` where `a.Other = b` and `b.Other = a`.

**Shared-reference deduplication (~4):**
- `SharedReference_DepartmentAcrossManyEmployees_AllocatedOnce` — single `Department` referenced by 5 employees → `dto.Employees[i].Department` is the same instance for all `i`.
- `SharedReference_AcrossNestedAndOuterScope` — `dept.Employees[0].Department == dept` (employee back-references its own department); cycle resolved.
- `SharedReference_TwoElementsInSameList_DedupedOnSecondOccurrence` — `[alice, bob, alice]` source list → `[aliceDto, bobDto, aliceDto]` destination list with `result[0] == result[2]`.
- `SharedReference_TwoCollectionsReferencingSameInstance_PreservesIdentity` — two `List<Person>` collections share an element; both destination lists point at the same `PersonDto`.

**OFF-path performance / no-cost (~3):**
- `WithoutPreserveReferences_NormalCycleStillFails_AsExpected` — sanity: confirms the v1 behavior is unchanged when flag is off (cycle causes some bounded-depth runtime exception).
- `WithoutPreserveReferences_NoMappingContextAllocated` — verifies `tm.PreserveReferences == false` means no allocation. Indirect verification via instrumentation.
- `WithoutPreserveReferences_NestedMapCallsPassNullContext` — verifies the `ctx is null` path is taken on non-PR typemaps.

**Fresh-map allocation (~4):**
- `Map_FreshSimpleCycle_ReturnsNewInstance_NotSourceReference` — `result is not src` (cycle-safety doesn't accidentally return the source).
- `Map_FreshGraph_AllPropertiesPopulatedCorrectly_DespiteCycle` — primitives, nested objects, and the cycle field all correct on the destination.
- `Map_NullSource_ReturnsDefault_RegardlessOfPreserveReferences` — `mapper.Map<PersonDto?>(null)` is null.
- `Map_NullCycleField_LeavesDestinationCycleFieldNull` — `alice.Boss = null` → `dto.Boss == null`, no exception.

**Multiple top-level calls (~2):**
- `MultipleTopLevelCalls_EachAllocatesFreshContext` — call `Map` twice; cycles in both calls work; the second call doesn't see the first call's cache.
- `ConcurrentTopLevelCalls_DoNotShareContext` — 16 parallel `Map` calls each with its own cycle; no race conditions, no cross-pollution.

**Reference-vs-value-type sources (~2):**
- `ValueTypeSource_NoCachePreambleEmitted_LambdaCompilesAndRuns` — `struct Foo { ... }` source. Even with PreserveReferences, the lambda runs without the cache check.
- `ReferenceTypeSource_CachePreambleAlwaysEmittedWhenSourceIsClass` — sanity check on the codegen rule.

### 8.4 `MapperPreserveReferencesPropagationTests` (~9 tests)

Verifies the propagation rules:

- `Inheritance_BasePreserveReferences_PropagatesToDerivedViaInclude` — `cfg.CreateMap<Person, PersonDto>().PreserveReferences().Include<Manager, ManagerDto>(); cfg.CreateMap<Manager, ManagerDto>();` → derived typemap has `PreserveReferences = true`.
- `Inheritance_BaseWithoutPreserveReferences_DoesNotForcePreserveReferencesOnDerived` — control case.
- `ReverseMap_PropagatesPreserveReferencesToReversePair` — `cfg.CreateMap<P, PDto>().PreserveReferences().ReverseMap();` → reverse typemap has the flag too.
- `OpenGeneric_PropagatesFromTemplateToClosedMaterialization` — `cfg.CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>)).PreserveReferences();` → closed pair `(Wrapper<int>, WrapperDto<int>)` materialized with the flag.
- `DownPropagation_OuterFlagged_InnerNotFlagged_InnerStillCacheActive` — `Department → DepartmentDto` flagged; `Employee → EmployeeDto` not flagged. Calling `mapper.Map<DepartmentDto>(dept)` with cycles inside Employee subgraph → cycles resolved (inner typemap inherits cache via runtime `ctx` propagation).
- `DownPropagation_OuterNotFlagged_InnerFlagged_NoCacheActive` — control: outer unflagged, inner flagged. Calling `mapper.Map<DepartmentDto>(dept)` → no `MappingContext` allocated → inner Employee call runs without cache → cycles within Employee subgraph stack-overflow as in v1. Documents the v1 limitation.
- `DownPropagation_InnerCallExplicitly_AllocatesContextWhenInnerFlagged` — calling `mapper.Map<EmployeeDto>(employee)` directly with Employee flagged → context allocated, cycles resolved.
- `Hooks_FireOnFirstAllocation_NotOnCacheHit` — `BeforeMap` and `AfterMap` invocation counters; verifies one fire per unique source instance, regardless of how many times the source appears in the graph.
- `ValueTransformers_FireOnFirstAllocation_NotOnCacheHit` — same pattern for `AddTransform<T>`.

### 8.5 `MapperPreserveReferencesUpdateInPlaceTests` (~5 tests)

- `UpdateInPlace_FreshDestination_CycleResolvedToExisting` — `existingDto = new PersonDto(); alice.Boss = alice; mapper.Map(alice, existingDto);` → `existingDto.Boss == existingDto`.
- `UpdateInPlace_PreservesNonMappedFields` — fields not present in source POCO are preserved on `existingDto` (matches v1 update-in-place semantics).
- `UpdateInPlace_NestedExistingPocoPreserved_WhenInnerKeyMissing` — extends DynamicMapping #10 Task 6 update-in-place into the cycle scenario.
- `UpdateInPlace_SecondCallToSameSource_ReturnsCachedNotExisting` — pathological: registering `(src, existingDest)` first, then a NEW call with the same `src` and a different existing — cache hits return the FIRST one. Documents this edge case.
- `UpdateInPlace_AcrossDifferentDestinationTypes_TwoSeparateCacheSlots` — `Map(alice, existingDto1); Map(alice, existingSummary);` → both populated correctly without cross-contamination, but each is its own top-level call → each gets its own MappingContext anyway. Confirms.

### 8.6 `ConfigurationValidatorPreserveReferencesTests` (~3 tests)

- `AssertConfigurationIsValid_PreserveReferencesOnly_Passes` — sanity.
- `AssertConfigurationIsValid_PreserveReferencesPlusConvertUsing_ThrowsAtlasConfigurationException` — the validator rule from §6.1.
- `AssertConfigurationIsValid_PreserveReferencesPlusOtherFeatures_Passes` — verifies PR + Hooks + AddTransform + Condition + NullSubstitute is allowed (only ConvertUsing is the conflict).

### 8.7 `ProjectionRejectsPreserveReferencesTests` (~2 tests)

- `ProjectTo_PreserveReferencesTypeMap_ThrowsAtlasProjectionException` — message contains "PreserveReferences" or "identity tracking."
- `ProjectTo_NonPreserveReferencesMap_StillWorks` — regression check.

### 8.8 TDD discipline

Each test file is written failing first. The test ordering across the implementation tasks (refined in plan stage):
1. `MappingContextTests` — green after Task 1 (the MappingContext class).
2. `ConfigurationValidatorPreserveReferencesTests` — green after the validator rule is added.
3. `MapperPreserveReferencesTests` — green after the codegen lands.
4. Propagation, update-in-place, projection rejection — last.

### 8.9 Coverage targets

Line ≥ 90%, branch ≥ 80% on the changed files: `MappingContext.cs`, the `MappingExpression.PreserveReferences` method, `TypeMap.PreserveReferences` field plumbing, the codegen extension in `ExecutionPlanBuilder.Build`/`BuildUpdate`, the `MappingInvoker.Invoke*` signature changes, the validator rule, the projection rejection.

---

## 9. README updates

For the implementation plan's documentation task. Mirrors the structure of OpenGenerics, ConditionalMapping, DynamicMapping sections in the existing README:

```markdown
## Reference handling for cycles

Atlas can map graphs with cycles or shared references safely, opt-in per typemap.
Without this opt-in, mapping a cyclic graph stack-overflows — by design, since
cycle detection has runtime cost.

```csharp
class Person
{
    public string Name { get; set; }
    public Person Boss { get; set; }            // self-cycle: alice.Boss = alice
}

cfg.CreateMap<Person, PersonDto>().PreserveReferences();

var alice = new Person { Name = "Alice" };
alice.Boss = alice;                              // cycle
var dto = mapper.Map<PersonDto>(alice);          // works — no stack overflow
// dto.Boss == dto (same instance; identity preserved)
```

Behavior summary:

- Convention: ONE flag on the OUTERMOST typemap of a potentially-cyclic graph
  is enough. Inner typemaps inherit cycle-safety at runtime via a per-call
  cache threaded through the call chain.
- Pre-population semantics: a destination is registered into the cache BEFORE
  its members are populated, which is what breaks cycles. Back-references
  resolve to the partially-constructed destination, fully populated by the
  time control returns to the caller.
- Shared references are also preserved: a `Department` referenced by 5
  `Employee` instances produces ONE `DepartmentDto` shared across all 5
  destination back-references.
- Hooks (`BeforeMap`/`AfterMap`), value transformers, conditional predicates,
  and null substitutes fire on the FIRST allocation only — cache hits skip
  the body entirely (no double-invocation of side effects).
- Propagates through `.ReverseMap()`, `Include<>` inheritance, and open-generic
  template materializations.
- `Atlas.Projections` rejects PreserveReferences typemaps — LINQ providers
  cannot model identity tracking. Use `mapper.Map<>()` for cycle-safe in-memory
  mapping; use `ProjectTo` only for non-cyclic projections.

Limitations (v1):

- Cannot be combined with `ConvertUsing<TConverter>()` — the converter replaces
  the body that the cache would wrap. Validator rejects the combination at
  `AssertConfigurationIsValid()` time.
- The cycle-safety flag must be on the OUTERMOST typemap of a potentially-cyclic
  graph. Marking only an INNER typemap (e.g., Employee → EmployeeDto) without
  marking its OUTER caller (e.g., Department → DepartmentDto) means the inner
  cycle protection is unreachable from the outer call. v3 may relax this.
- No custom reference-handler interface in v1 — built-in handler only.
- No per-call opt-in (`mapper.Map(src, opts => ...)`) in v1 — per-typemap only.
- Hooks and transformers cannot inspect the cycle-cache directly; they see
  destinations they create. Cyclically-referenced destinations may appear
  partially-populated to a hook fired during their own allocation phase
  (a known and documented limitation).

See `docs/Atlas-Design-ReferenceHandling.md` for the full specification.
```

---

## 10. Risks & Implementer Notes

### 10.1 Risk: signature change is wide-but-mechanical

The `MappingContext?` parameter addition to every `MappingInvoker.Invoke*` static helper, every compiled lambda, every `Expression.Call` emit site, is a textbook "small change applied many places." The risk isn't bugs in any one site; it's missing a site and ending up with a broken call chain.

**Mitigation:** the implementation plan should list every emit site explicitly. Grep for `MappingInvoker.Invoke` and `MappingInvoker.InvokeUpdate` and `MappingInvoker.InvokeToList` and `MappingInvoker.InvokeToArray` and `MappingInvoker.InvokeToDictionary` across `src/`. Each match is either:
- A static helper definition → update signature, add `ctx` parameter
- An `Expression.Call(...)` emit → update emit to pass `ctx`
- A reflection-based dispatch (DynamicMapping's `MakeGenericMethod(...).Invoke(null, [registry, value, ctx])`) → update args array

Implementer task per file: edit and verify with full-suite test pass. Subagent prompt should explicitly list the grep targets.

### 10.2 Risk: passing `null` from public Map invocations breaks existing tests

Every existing v1 test calls `mapper.Map<TS, TD>(src)` and expects the v1-shaped lambda. After the signature change, `Map` allocates `null` as the second arg and the compiled lambda accepts `null`. Existing v1 lambda bodies don't reference the parameter, so the null is ignored. Compatible.

But the COMPILED LAMBDA changes shape. If anything caches compiled lambdas across runs (e.g., a test that does `var f = (Func<TS, TD>)compiledLambda; f(src);`), it will fail with `InvalidCastException`. Audit `Atlas.Tests` for direct delegate-cast tests. None expected (Atlas's compile-cache is internal).

### 10.3 Risk: cycle-breaking returns partially-populated destination

The pre-population registration means a back-reference resolved during cycle-breaking returns a destination that doesn't yet have all members populated. Example:

```csharp
class A { public B Other { get; set; } }
class B { public A Other { get; set; } }
var a = new A();
var b = new B();
a.Other = b;
b.Other = a;
mapper.Map<ADto>(a);
```

When mapping `a → aDto`:
1. Allocate `aDto`. Register `(a → aDto)`.
2. Populate `aDto.Other`: nested call `b → bDto`.
3. Allocate `bDto`. Register `(b → bDto)`.
4. Populate `bDto.Other`: nested call `a → aDto'`. **Cache hit.** Return `aDto` — but `aDto` doesn't have `.Other` populated yet (we're still in step 2's populate phase).
5. `bDto.Other = aDto` (the same `aDto` instance, currently missing `.Other`).
6. Continue populating `bDto`'s other fields.
7. Return from step 2's nested call: `aDto.Other = bDto`. Now `aDto` is fully populated.
8. By the time the user has `aDto`, both `aDto.Other = bDto` and `bDto.Other = aDto` are set. ✅

This is **correct by construction**: the C# memory model guarantees that all writes complete before the outer `return aDto` happens. From the user's perspective, they receive a fully-populated graph.

**However:** if `BeforeMap`/`AfterMap` hooks observe the partial state, they could see the temporarily-incomplete `aDto`. Atlas's hooks fire OUTSIDE the cache-check gate, so a cache hit doesn't fire hooks. But a hook firing at step 5 (during `bDto`'s body, before its `Other` is assigned) could see `aDto.Other == null` if it inspects `aDto` directly. This is a known limitation with the pre-population approach. **Documented in the README as: "if your hooks inspect cyclically-referenced destinations, expect to see partial state at hook-fire time."** Workaround: hooks should only inspect their own typemap's source/destination, not transitively-referenced instances.

### 10.4 Risk: reference-equality on the source instance

The cache uses `ReferenceEquals` on the source. A typical domain object's `Equals` and `GetHashCode` overrides are value-based (e.g., comparing `Id` properties). Using value-equality would mean two distinct `Person` instances with the same `Id` would collapse into the same cache slot, returning the WRONG destination. Reference-equality is correct.

`ReferenceEqualityComparer.Instance` (.NET 5+) handles this for `IEqualityComparer<object>`. For our composite key, the custom `RefEqComparer` does it explicitly via `ReferenceEquals` and `RuntimeHelpers.GetHashCode`.

### 10.5 Risk: `PreserveReferences + ConvertUsing` validator rule misses the inheritance case

If `BasePerson → BaseDto` has `ConvertUsing` and `Manager → ManagerDto` has `Include<Manager>` from base AND `PreserveReferences`, the inheritance merger could combine them in a way that `tm.PreserveReferences == true` and `tm.CustomConverter is not null` simultaneously. Need to verify:
- `InheritanceMerger.CopyConfig` does NOT copy `CustomConverter` from base to derived (would be weird — derived would inherit base's converter even if derived doesn't want it).
- The validator rule's check happens AFTER inheritance merging, so it catches the resolved state of the typemap.

**Implementation task to verify:** add a test `Inheritance_BaseHasConvertUsing_DerivedHasPreserveReferences_ValidatorRejects` (if the InheritanceMerger combines them) OR `Inheritance_DoesNotPropagateConvertUsing_ValidatorPasses` (if it doesn't). The plan-stage decision determines which test we need.

### 10.6 Risk: `MappingContext` allocation cost when feature is OFF

The OFF-path cost is the second parameter on every nested call frame + one nullable check. Both should be ≤1 ns. **Benchmark task in the implementation plan** (the existing `Atlas.Benchmarks` project): rerun the cold-call, warm-call, configuration-build benchmarks and verify regression < 5% (~1-2% expected).

### 10.7 Risk: invocations that bypass the public `IMapper.Map` allocate context

If anyone calls `MappingInvoker.Invoke<TS, TD>(registry, src, null)` directly (i.e., reflection-based dispatch from another internal helper), they pass null context. If the typemap has `PreserveReferences = true`, the cache is unavailable and cycles will stack-overflow. This is technically a bug but only reachable via internal helpers; users go through `IMapper.Map`.

**Mitigation:** the design's audit table (§2.5) lists every reader of `TypeMap.PreserveReferences`. Each reader either allocates context or is on a path that's been audited.

### 10.8 Implementer notes — cross-task ordering

Suggested task decomposition (refined in plan stage):

1. **Task 0:** Cut branch from main.
2. **Task 1:** `MappingContext` class + unit tests (`MappingContextTests`). Pure data-shape work.
3. **Task 2:** `TypeMap.PreserveReferences` field + `IMappingExpression.PreserveReferences()` fluent method + plumbing through `MappingExpression`.
4. **Task 3:** Update `MappingInvoker.Invoke*` static helpers to accept `MappingContext?`. Update every existing emit-site in `ExecutionPlanBuilder` and `DynamicPlanBuilder` to pass through. Update existing v1 tests to confirm no regression. **This task is mechanical-but-wide; allowlist is large.** Note: this task must update emit sites in ProjectionPlanBuilder, MappingInvoker reflection-dispatch helpers (ConvertObjectTo, SerializeValue), and any other callers found via grep.
5. **Task 4:** `IMapper.Map` allocates `MappingContext` based on `tm.PreserveReferences`. Threads through to `MappingInvoker`. Cycle-safety begins to work end-to-end.
6. **Task 5:** Codegen — emit cache preamble (`if (ctx != null && ctx.TryGet(...)) return cached; ... ctx.Register(...);`) at top of compiled POCO lambda bodies. Cycle-breaking tests start passing.
7. **Task 6:** Propagation rules — `InheritanceMerger.CopyConfig`, `MappingExpression.ReverseMap`, `OpenGenericTypeMap` materialization. Each propagation test pins one rule.
8. **Task 7:** Update-in-place codegen + tests. Reuses Task 5's cache preamble.
9. **Task 8:** `ValidatePreserveReferences` validator rule + tests.
10. **Task 9:** Atlas.Projections rejection (dual-gate) + tests.
11. **Task 10:** Integration tests — hooks/transformers/value-transformers fire-on-first-only, threading, OFF-path no-allocation.
12. **Task 11:** README update + final coverage check.
13. **Final review.**

Estimated 11–13 tasks, ~45–55 net new tests, ~6 hours wall-clock per memory's per-feature baseline.

### 10.9 Per-task model selection guidance

| Task | Suggested model | Rationale |
|---|---|---|
| 0 | controller | branch setup |
| 1 | haiku | mechanical: a class + 6 unit tests |
| 2 | haiku | mechanical: 1 field + 1 fluent method + tests |
| 3 | sonnet | wide-but-mechanical: every Invoke* site |
| 4 | sonnet | integration: IMapper allocates context, threads through |
| 5 | sonnet | algorithm-heavy: codegen Expression-tree emit |
| 6 | sonnet | algorithm-heavy: 3 propagation sites with subtleties |
| 7 | sonnet | integration: update-in-place semantics |
| 8 | haiku | mechanical: validator rule + 3 tests |
| 9 | haiku | mechanical: dual-gate rejection + 2 tests |
| 10 | haiku | tests-only |
| 11 | haiku | docs-only |

### 10.10 Pseudocode-trace discipline (per memory's Bug-7 lesson)

Concrete worked examples to trace through during plan signing-off:

1. **Self-cycle:** `alice.Boss = alice`. Trace through allocation, registration, recursive call, cache hit, return. Verify: `dto.Boss == dto`.
2. **Mutual cycle:** `a.Other = b; b.Other = a`. Trace through both populate phases. Verify: both back-references resolve.
3. **Shared reference:** `dept.Employees = [emp1, emp2]; emp1.Department = dept; emp2.Department = dept`. Trace through: outer `dept` registered before inner Employee maps; both employees' `.Department` resolves to cache → same `deptDto` instance.
4. **No-cycle, no-PR:** `mapper.Map<PersonDto>(alice)` with no flag set, no cycles. Verify: `MappingContext` not allocated; existing v1 lambda body shape with the new `MappingContext?` parameter passed `null`, no functional change.
5. **OFF-path call into ON-path:** `cfg.CreateMap<Department, DepartmentDto>(); cfg.CreateMap<Employee, EmployeeDto>().PreserveReferences();` (only inner has flag). `mapper.Map<DepartmentDto>(dept)` — outer typemap not flagged, so no context allocated. Inner Employee call runs with `ctx == null`. Cycle inside `Employee.Manager == employee` will stack-overflow. **This is the v1 limitation locked in §1.2** — the user must put the flag on the OUTERMOST typemap of any potentially-cyclic graph.
6. **Inheritance propagation:** `cfg.CreateMap<Person, PersonDto>().PreserveReferences().Include<Manager, ManagerDto>(); cfg.CreateMap<Manager, ManagerDto>();` — verify after `InheritanceMerger.CopyConfig` runs, `Manager → ManagerDto.PreserveReferences == true`.

These six worked examples should walk through every component (`IMapper.Map` allocation, codegen preamble, propagation, runtime cache flow). Plan stage uses them as acceptance criteria for the design's correctness.

---

## 11. Final Feature Summary

Atlas v2 #11 — Reference handling for cycles:

- **Opt-in cycle-safe mapping** via `cfg.CreateMap<TSrc, TDst>().PreserveReferences()`. Per-typemap fluent registration. Default OFF — zero new cost on existing v1 code paths beyond a single nullable check per nested call.
- **Cache shape:** `Dictionary<(object source, Type destinationType), object>` with `ReferenceEquals`-on-source equality and `RuntimeHelpers.GetHashCode`. Pre-population semantics — destination registered before its members are populated, which is what breaks cycles.
- **Cycle-safety propagates DOWN automatically.** Top-level typemap flag activates a `MappingContext` allocated by `IMapper.Map`; the context threads through every nested call via a `MappingContext? ctx` parameter on every compiled lambda. Inner non-PR-flagged typemaps inherit the protection at runtime.
- **Universal `MappingContext?` parameter** on every compiled lambda signature. `null` means "no PreserveReferences active" → fast path. Non-null means "active" → cache lookup + register before body. The OFF path adds one nullable check per nested map call (≤1 ns).
- **Cache scope:** nested POCO map calls only — POCO destinations, collection-element POCO destinations, dictionary-value POCO destinations. Primitives, strings, enums, value-type sources skip the cache (codegen omits the preamble for value-type sources).
- **Hooks fire on first allocation only.** `BeforeMap`/`AfterMap`/`AddTransform`/`Condition`/`PreCondition`/`NullSubstitute` skip on cache hits — cache hits return the previously-cached destination directly without re-running the body.
- **Validator rule:** `ValidatePreserveReferences` rejects `PreserveReferences + ConvertUsing` combination at config time with a clear `AtlasConfigurationException`.
- **Atlas.Projections dual-gate rejection:** `ProjectionCompatibility.IsTypeMapProjectable` returns false; `ProjectionPlanBuilder.RejectPreserveReferencesOrThrow` is the runtime backstop. Mirrors Hooks #5 / DynamicMapping #10 patterns exactly.
- **Propagation:** the flag flows through `InheritanceMerger.CopyConfig` (base→derived), `MappingExpression.ReverseMap` (forward→reverse), and `OpenGenericTypeMap.Materialize` (template→closed pair). Bug-5 lesson applied: scope-identifying metadata propagates.
- **Test count:** ~45–55 net new tests across 6 test files. Brings v2 baseline from 575 to ~625 after merge.
- **11–13 implementation tasks** estimated; ~6 hours wall-clock per the per-feature baseline.

The next step is `superpowers:writing-plans` against this design to produce `docs/Atlas-Plan-ReferenceHandling.md`. Both docs commit directly to `main` per the established v2 workflow rhythm; implementation goes through a `feat/reference-handling` branch and a single PR.
