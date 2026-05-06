# Atlas v2 Reference Handling Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship Atlas v2 feature #11 — opt-in cycle-safe and shared-reference-preserving mapping via `cfg.CreateMap<TSrc, TDst>().PreserveReferences()`. Default OFF; per-typemap fluent activation; pre-population cache breaks cycles and dedupes shared references.

**Architecture:** Per-call `MappingContext` allocated by `IMapper.Map` at the public-API boundary when the top-level typemap has `PreserveReferences = true`. Threaded through every nested map call via a `MappingContext? ctx` parameter on every compiled lambda (universal). Cache shape: `Dictionary<(object source, Type destinationType), object>` with `ReferenceEquals`-on-source equality. Pre-population semantics — destination registered before its members are populated. Projection rejects via dual-gate matching the Hooks #5 / DynamicMapping #10 pattern.

**Tech Stack:** C# 14 preview, `System.Linq.Expressions`, `System.Runtime.CompilerServices.RuntimeHelpers`, xUnit v3 (plain `Assert.X()` only — NO FluentAssertions per project convention).

**Branch:** `feat/reference-handling`, cut from `main` HEAD `38d1e4c` (the design commit for #11).

**Reference design:** `C:\Repos\Atlas\docs\Atlas-Design-ReferenceHandling.md` — primary spec. All section references (e.g., "design §5.3") point at it.

---

## File Map

### New files (production)

- `C:\Repos\Atlas\src\Atlas\Internal\MappingContext.cs` — `MappingContext` sealed class with `TryGet`/`Register`, custom `RefEqComparer` for the composite cache key.
- `C:\Repos\Atlas\src\Atlas\Configuration\IOpenGenericMappingExpression.cs` — new interface with one method `PreserveReferences()` so the open-generic registration's fluent surface can opt into reference handling.
- `C:\Repos\Atlas\src\Atlas\Configuration\OpenGenericMappingExpression.cs` — implementation class wrapping an `OpenGenericTypeMap`.

### Modified files (production)

- `C:\Repos\Atlas\src\Atlas\Internal\TypeMap.cs` — add `bool PreserveReferences { get; set; }` field after `IsDynamic` (line 114).
- `C:\Repos\Atlas\src\Atlas\Internal\PropertyMap.cs` — no changes (PreserveReferences is TypeMap-level).
- `C:\Repos\Atlas\src\Atlas\Internal\OpenGenericTypeMap.cs` — add `bool PreserveReferences { get; set; }` field.
- `C:\Repos\Atlas\src\Atlas\Configuration\IMappingExpression.cs` — add `IMappingExpression<TSrc, TDst> PreserveReferences()` method declaration after `WithFallback` (line 182).
- `C:\Repos\Atlas\src\Atlas\Configuration\MappingExpression.cs` — implement `PreserveReferences()` method that sets `TypeMap.PreserveReferences = true`. Also extend `ReverseMap` (line 149-154) to propagate the flag to the reverse pair.
- `C:\Repos\Atlas\src\Atlas\Configuration\MapperConfigurationExpression.cs` — change `CreateMap(Type, Type, MemberList)` to return `IOpenGenericMappingExpression` instead of `void`.
- `C:\Repos\Atlas\src\Atlas\MapperProfile.cs` — same change to `CreateMap(Type, Type, MemberList)`.
- `C:\Repos\Atlas\src\Atlas\Internal\MappingInvoker.cs` — add `MappingContext? ctx` parameter to **every** public static method (12 methods total). Thread `ctx` through internal dispatch.
- `C:\Repos\Atlas\src\Atlas\Mapper.cs` — update all 3 `Map` overloads: allocate `MappingContext` based on `tm.PreserveReferences`; pass `ctx` through to `MappingInvoker.Invoke*` calls (including the reflection-dispatch path).
- `C:\Repos\Atlas\src\Atlas\IMapper.cs` — no signature change (the `MappingContext` allocation is internal).
- `C:\Repos\Atlas\src\Atlas\Internal\MapperRegistry.cs` — no signature change to `_delegates`/`_updateDelegates` (already typed as `Delegate`). Update `MaterializeClosed` (line 147–167) to propagate `PreserveReferences` from open-generic template to closed pair.
- `C:\Repos\Atlas\src\Atlas\Internal\ExecutionPlanBuilder.cs` — update **5 `Expression.Call` emit sites** (lines 112, 435, 450, 484, 505) to pass `ctxParam` to `MappingInvoker.Invoke*`. Add cache preamble emission at the top of `Build` and `BuildUpdate` for POCO-typed lambdas where `srcType.IsClass`.
- `C:\Repos\Atlas\src\Atlas\Internal\DynamicPlanBuilder.cs` — update **all `Expression.Call` sites** that target `MappingInvoker.*` (via cached `_invokeMethod`, `_invokeUpdateMethod`, `_serializeValue`, `_serializeCollection`, `_serializeDictionary`, `_convertObjectTo`, `_convertObjectToList`, `_convertObjectToArray`, `_scanPrefix`) to pass the lambda's `ctxParam`.
- `C:\Repos\Atlas\src\Atlas\Internal\InheritanceMerger.cs` — extend the TypeMap-level merge logic (where `BeforeHooks`/`AfterHooks` are merged around lines 43–51) to propagate `PreserveReferences` base→derived: `derivedTm.PreserveReferences = derivedTm.PreserveReferences || baseTm.PreserveReferences;`.
- `C:\Repos\Atlas\src\Atlas\Internal\ConfigurationValidator.cs` — add `ValidatePreserveReferences` static method; call it from the per-typemap loop in `Validate` (around line 33, after `ValidateNullSubstitutes`).
- `C:\Repos\Atlas\src\Atlas.Projections\Internal\ProjectionPlanBuilder.cs` — add `RejectPreserveReferencesOrThrow` method (mirror of `RejectHooksOrThrow` and `RejectDynamicOrThrow`); call it from `BuildBody` adjacent to the existing rejection calls (line 27–28 area).
- `C:\Repos\Atlas\src\Atlas.Projections\Internal\ProjectionCompatibility.cs` — add `if (tm.PreserveReferences)` block to `IsTypeMapProjectable` (after the existing `IsDynamic` check around line 19).

### New files (tests)

- `C:\Repos\Atlas\tests\Atlas.Tests\Internal\MappingContextTests.cs` — pure unit tests on `MappingContext` (~6 tests).
- `C:\Repos\Atlas\tests\Atlas.Tests\Internal\TypeMapPreserveReferencesFieldTests.cs` — fluent-method-sets-flag tests (~3 tests).
- `C:\Repos\Atlas\tests\Atlas.Tests\MapperPreserveReferencesTests.cs` — end-to-end cycle-breaking + shared-reference dedup (~20 tests).
- `C:\Repos\Atlas\tests\Atlas.Tests\MapperPreserveReferencesPropagationTests.cs` — propagation across Inheritance/ReverseMap/OpenGenerics + down-propagation runtime semantics (~9 tests).
- `C:\Repos\Atlas\tests\Atlas.Tests\MapperPreserveReferencesUpdateInPlaceTests.cs` — update-in-place + nested-existing semantics (~5 tests).
- `C:\Repos\Atlas\tests\Atlas.Tests\ConfigurationValidatorPreserveReferencesTests.cs` — `PreserveReferences + ConvertUsing` rejection (~3 tests).
- `C:\Repos\Atlas\tests\Atlas.Tests\MapperPreserveReferencesIntegrationTests.cs` — hooks-fire-once, threading, OFF-path no-allocation (~10 tests).
- `C:\Repos\Atlas\tests\Atlas.Projections.Tests\ProjectionRejectsPreserveReferencesTests.cs` — projection rejection (~2 tests).

### Modified files (docs)

- `C:\Repos\Atlas\README.md` — add a "Reference handling for cycles" section before the deferred-features list. Remove #11 from the deferred list.
- `C:\Repos\Atlas\docs\Atlas-Design-ReferenceHandling.md` (already on `main`) — no changes during implementation.

### Test count delta target

Baseline from PR #10: **575 PASS** (490 Atlas.Tests + 71 Projections + 14 EFCore).

After this feature: **~633 PASS** (≈ +58 net):
- +6 in `MappingContextTests`
- +3 in `TypeMapPreserveReferencesFieldTests`
- +20 in `MapperPreserveReferencesTests`
- +9 in `MapperPreserveReferencesPropagationTests`
- +5 in `MapperPreserveReferencesUpdateInPlaceTests`
- +3 in `ConfigurationValidatorPreserveReferencesTests`
- +10 in `MapperPreserveReferencesIntegrationTests`
- +2 in `ProjectionRejectsPreserveReferencesTests`

Per-feature plan-arithmetic-drift discipline (memory feedback): the implementer's actual count is authoritative; treat ~58 as approximate.

---

## Task 0 — Branch setup

**Files:** none (controller-only operation).

- [ ] **Step 0.1: Verify clean state on `main`**

```pwsh
cd C:\Repos\Atlas
git status
git log --oneline -3
```

Expected: working tree clean; HEAD at `38d1e4c` ("docs: design for Atlas v2 #11 Reference handling for cycles") or further if you've pulled the design commit.

- [ ] **Step 0.2: Cut feature branch**

```pwsh
git checkout -b feat/reference-handling
```

Expected: switched to a new branch `feat/reference-handling`.

- [ ] **Step 0.3: Confirm baseline test count**

```pwsh
dotnet test --nologo 2>&1 | Select-String -Pattern "Passed:|Failed:|Skipped:"
```

Expected: total `Passed: 575, Failed: 0, Skipped: 0` across the three test projects (490 Atlas.Tests + 71 Atlas.Projections.Tests + 14 Atlas.Projections.Tests.EFCore).

---

## Task 1 — `MappingContext` class

**Goal:** Stand up the per-call instance cache. Pure data-shape work; no `IMapper` integration yet.

**Files:**
- Create: `C:\Repos\Atlas\src\Atlas\Internal\MappingContext.cs`
- Create: `C:\Repos\Atlas\tests\Atlas.Tests\Internal\MappingContextTests.cs`

**Allowlist for the implementer subagent:** the two files above, no others.

- [ ] **Step 1.1: Write `MappingContextTests.cs` (failing)**

```csharp
namespace Atlas.Tests.Internal;

using Atlas.Internal;

public class MappingContextTests
{
    [Fact]
    public void TryGet_ReturnsFalse_WhenSourceNotRegistered()
    {
        var ctx = new MappingContext();
        var result = ctx.TryGet(new object(), typeof(string), out var dst);
        Assert.False(result);
        Assert.Null(dst);
    }

    [Fact]
    public void Register_ThenTryGet_ReturnsRegisteredInstance()
    {
        var ctx = new MappingContext();
        var src = new object();
        var dst = "alice";
        ctx.Register(src, typeof(string), dst);

        var found = ctx.TryGet(src, typeof(string), out var result);
        Assert.True(found);
        Assert.Same(dst, result);
    }

    [Fact]
    public void Register_SameSource_DifferentDestinationTypes_StoresSeparately()
    {
        var ctx = new MappingContext();
        var src = new object();
        ctx.Register(src, typeof(string), "as-string");
        ctx.Register(src, typeof(int), 42);

        Assert.True(ctx.TryGet(src, typeof(string), out var asString));
        Assert.True(ctx.TryGet(src, typeof(int), out var asInt));
        Assert.Equal("as-string", asString);
        Assert.Equal(42, asInt);
    }

    [Fact]
    public void Register_TwoSourceInstances_WithEqualEqualsButDifferentReferences_StoresSeparately()
    {
        var ctx = new MappingContext();
        var src1 = new ValueEqPerson { Id = 42 };
        var src2 = new ValueEqPerson { Id = 42 };
        // src1.Equals(src2) is true (overridden), but ReferenceEquals(src1, src2) is false
        Assert.True(src1.Equals(src2));
        Assert.False(ReferenceEquals(src1, src2));

        ctx.Register(src1, typeof(string), "first");
        ctx.Register(src2, typeof(string), "second");

        Assert.True(ctx.TryGet(src1, typeof(string), out var found1));
        Assert.True(ctx.TryGet(src2, typeof(string), out var found2));
        Assert.Equal("first", found1);
        Assert.Equal("second", found2);
    }

    [Fact]
    public void Register_OverwriteSameKey_KeepsLastValue()
    {
        var ctx = new MappingContext();
        var src = new object();
        ctx.Register(src, typeof(string), "first");
        ctx.Register(src, typeof(string), "second");

        Assert.True(ctx.TryGet(src, typeof(string), out var found));
        Assert.Equal("second", found);
    }

    [Fact]
    public void TryGet_AfterMultipleRegisters_FindsAll()
    {
        var ctx = new MappingContext();
        var sources = new object[5];
        for (int i = 0; i < 5; i++)
        {
            sources[i] = new object();
            ctx.Register(sources[i], typeof(int), i);
        }
        for (int i = 0; i < 5; i++)
        {
            Assert.True(ctx.TryGet(sources[i], typeof(int), out var found));
            Assert.Equal(i, found);
        }
    }

    private sealed class ValueEqPerson
    {
        public int Id { get; set; }
        public override bool Equals(object? obj) => obj is ValueEqPerson p && p.Id == Id;
        public override int GetHashCode() => Id;
    }
}
```

- [ ] **Step 1.2: Run tests — verify they fail (compile errors expected)**

```pwsh
dotnet test tests/Atlas.Tests/Atlas.Tests.csproj --filter FullyQualifiedName~MappingContextTests --nologo
```

Expected: compile failure, `'MappingContext' does not exist in namespace 'Atlas.Internal'`.

- [ ] **Step 1.3: Create `MappingContext.cs`**

```csharp
namespace Atlas.Internal;

using System.Collections.Generic;
using System.Runtime.CompilerServices;

/// <summary>
/// Per-call instance cache for cycle-safe mapping (Atlas v2 #11 — see PreserveReferences).
/// Allocated by IMapper.Map at the public-API boundary when typeMap.PreserveReferences is true;
/// threaded through every nested map call as a MappingContext? parameter on compiled lambdas.
/// One MappingContext instance lives for the duration of one top-level Map call; abandoned afterward.
/// Not thread-safe — each call gets its own instance.
/// See docs/Atlas-Design-ReferenceHandling.md §4.1.
/// </summary>
internal sealed class MappingContext
{
    private readonly Dictionary<CacheKey, object> _cache = new(CacheKey.Comparer);

    /// <summary>
    /// Look up the destination instance previously registered for (<paramref name="source"/>,
    /// <paramref name="destinationType"/>). Returns true on hit; the caller skips body execution
    /// and returns the cached destination.
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
    /// Register a freshly-allocated (or update-in-place existing) destination BEFORE its members
    /// are populated. Pre-population registration is what breaks cycles: any nested map call that
    /// resolves back to <paramref name="source"/> finds <paramref name="destination"/> in the
    /// cache and returns it (partially-populated at that moment, fully-populated by the time
    /// control returns to the user).
    /// </summary>
    internal void Register(object source, Type destinationType, object destination)
    {
        _cache[new CacheKey(source, destinationType)] = destination;
    }

    /// <summary>
    /// Cache key: source instance (by reference) + destination type. Two calls with the same
    /// source and different destination types get separate slots.
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
                    RuntimeHelpers.GetHashCode(obj.Source),
                    obj.DestinationType);
        }
    }
}
```

- [ ] **Step 1.4: Run tests — verify they pass**

```pwsh
dotnet test tests/Atlas.Tests/Atlas.Tests.csproj --filter FullyQualifiedName~MappingContextTests --nologo
```

Expected: `Passed: 6, Failed: 0`.

- [ ] **Step 1.5: Run full suite — verify no regression**

```pwsh
dotnet test --nologo 2>&1 | Select-String -Pattern "Passed:|Failed:"
```

Expected: total Passed = 575 + 6 = 581; Failed: 0.

- [ ] **Step 1.6: Commit**

```pwsh
git add src/Atlas/Internal/MappingContext.cs tests/Atlas.Tests/Internal/MappingContextTests.cs
git commit -m "MappingContext: per-call instance cache with reference-equality keys (Task 1)"
```

---

## Task 2 — `TypeMap.PreserveReferences` field + fluent method

**Goal:** Plumb the new `PreserveReferences` flag through the data shape and the fluent surface. After this task, `cfg.CreateMap<S, D>().PreserveReferences()` sets `tm.PreserveReferences = true`. The flag has no runtime effect yet — codegen wiring comes in Tasks 3–5.

**Files:**
- Modify: `C:\Repos\Atlas\src\Atlas\Internal\TypeMap.cs` — add `PreserveReferences` field after `IsDynamic` (line 114).
- Modify: `C:\Repos\Atlas\src\Atlas\Configuration\IMappingExpression.cs` — declare `PreserveReferences()` method.
- Modify: `C:\Repos\Atlas\src\Atlas\Configuration\MappingExpression.cs` — implement `PreserveReferences()`.
- Create: `C:\Repos\Atlas\tests\Atlas.Tests\Internal\TypeMapPreserveReferencesFieldTests.cs` — verify fluent method sets the flag (~3 tests).

**Allowlist for the implementer subagent:** the four files above, no others.

- [ ] **Step 2.1: Write `TypeMapPreserveReferencesFieldTests.cs` (failing)**

```csharp
namespace Atlas.Tests.Internal;

using Atlas;
using Atlas.Internal;

public class TypeMapPreserveReferencesFieldTests
{
    [Fact]
    public void DefaultTypeMap_HasPreserveReferencesFalse()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<SamplePoco, SamplePoco>());
        var tm = GetTypeMap(cfg, typeof(SamplePoco), typeof(SamplePoco));
        Assert.False(tm.PreserveReferences);
    }

    [Fact]
    public void CreateMapWithPreserveReferences_SetsFlagOnTypeMap()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<SamplePoco, SamplePoco>().PreserveReferences());
        var tm = GetTypeMap(cfg, typeof(SamplePoco), typeof(SamplePoco));
        Assert.True(tm.PreserveReferences);
    }

    [Fact]
    public void PreserveReferences_ReturnsExpressionForFluentChaining()
    {
        var cfg = new MapperConfiguration(c =>
        {
            // Verify that PreserveReferences() returns IMappingExpression so chains work.
            c.CreateMap<SamplePoco, SamplePoco>()
                .PreserveReferences()
                .ForMember(d => d.Name, opt => opt.MapFrom(s => s.Name));
        });
        var tm = GetTypeMap(cfg, typeof(SamplePoco), typeof(SamplePoco));
        Assert.True(tm.PreserveReferences);
    }

    private static TypeMap GetTypeMap(MapperConfiguration cfg, Type src, Type dst)
        => cfg.Internal_Registry.GetTypeMap(new TypePair(src, dst))
           ?? throw new InvalidOperationException($"No typemap for ({src}, {dst})");

    private sealed class SamplePoco
    {
        public string? Name { get; set; }
    }
}
```

- [ ] **Step 2.2: Run tests — verify they fail (compile errors expected)**

```pwsh
dotnet test tests/Atlas.Tests/Atlas.Tests.csproj --filter FullyQualifiedName~TypeMapPreserveReferencesFieldTests --nologo
```

Expected: compile failures referencing `PreserveReferences` not existing on `IMappingExpression` or `TypeMap`.

- [ ] **Step 2.3: Add `PreserveReferences` field to `TypeMap`**

In `C:\Repos\Atlas\src\Atlas\Internal\TypeMap.cs`, after the `IsDynamic` property (line 114), add:

```csharp
/// <summary>
/// True when this typemap was registered with <see cref="IMappingExpression{TSrc, TDst}.PreserveReferences"/>
/// (Atlas v2 #11). Causes IMapper.Map to allocate a MappingContext at the public-API boundary; causes
/// ExecutionPlanBuilder to emit cache-check + cache-register instructions in the compiled lambda;
/// causes ConfigurationValidator to reject ConvertUsing combos; causes Atlas.Projections to reject
/// the typemap at projection-build time.
/// </summary>
public bool PreserveReferences { get; set; }
```

(Use `{ get; set; }` to match the surrounding fields' style. Propagation paths in Task 6 will write the field after construction.)

- [ ] **Step 2.4: Add `PreserveReferences()` to `IMappingExpression`**

In `C:\Repos\Atlas\src\Atlas\Configuration\IMappingExpression.cs`, after the `WithFallback` method (around line 182, before `BeforeMap`), add:

```csharp
/// <summary>
/// Marks this typemap as cycle-safe. When the user calls <see cref="IMapper.Map{TSrc, TDst}(TSrc)"/>
/// (or any sibling overload) and the typemap matched at the top level has PreserveReferences enabled,
/// Atlas allocates a per-call instance cache and threads it through every nested map call. Cycles
/// (e.g. <c>person.Boss = person</c>) terminate; multiply-referenced source instances produce a single
/// destination instance shared across all back-references in the destination graph.
/// <para>
/// The flag propagates through <c>.ReverseMap()</c>, <c>Include&lt;Base, Derived&gt;()</c>, and
/// open-generic templates. Cannot be combined with <c>ConvertUsing&lt;TConverter&gt;()</c>; the
/// validator throws <see cref="AtlasConfigurationException"/> at <c>AssertConfigurationIsValid()</c>
/// time.
/// </para>
/// <para>
/// <see cref="Atlas.Projections.ProjectionExtensions"/> rejects PreserveReferences typemaps —
/// LINQ providers cannot model identity tracking. Use <see cref="IMapper.Map{T}(object)"/> for
/// cycle-safe in-memory mapping; use ProjectTo only for non-cyclic projections.
/// </para>
/// See docs/Atlas-Design-ReferenceHandling.md §3.2.
/// </summary>
/// <returns>This expression, for fluent chaining.</returns>
IMappingExpression<TSource, TDestination> PreserveReferences();
```

- [ ] **Step 2.5: Implement `PreserveReferences()` in `MappingExpression.cs`**

In `C:\Repos\Atlas\src\Atlas\Configuration\MappingExpression.cs`, add the implementation method (place it adjacent to other simple flag-setting fluent methods like `WithFallback`):

```csharp
public IMappingExpression<TSource, TDestination> PreserveReferences()
{
    TypeMap.EnsureMutable();
    TypeMap.PreserveReferences = true;
    return this;
}
```

(Adapt the body if `TypeMap` is exposed under a different name in the actual file — inspect the existing fluent methods like `WithFallback` for the exact pattern. The existing `MapByValue` / `MapByName` methods are likely the closest template.)

- [ ] **Step 2.6: Run new tests — verify they pass**

```pwsh
dotnet test tests/Atlas.Tests/Atlas.Tests.csproj --filter FullyQualifiedName~TypeMapPreserveReferencesFieldTests --nologo
```

Expected: `Passed: 3, Failed: 0`.

- [ ] **Step 2.7: Run full suite — verify no regression**

```pwsh
dotnet test --nologo 2>&1 | Select-String -Pattern "Passed:|Failed:"
```

Expected: total Passed = 581 + 3 = 584; Failed: 0.

- [ ] **Step 2.8: Commit**

```pwsh
git add src/Atlas/Internal/TypeMap.cs src/Atlas/Configuration/IMappingExpression.cs src/Atlas/Configuration/MappingExpression.cs tests/Atlas.Tests/Internal/TypeMapPreserveReferencesFieldTests.cs
git commit -m "TypeMap.PreserveReferences field + IMappingExpression.PreserveReferences() (Task 2)"
```

---

## Task 3 — Universal `MappingContext?` parameter on every signature

**Goal:** Add the `MappingContext? ctx` parameter to **every** `MappingInvoker.Invoke*` static helper, **every** `Mapper.Map` overload, and **every** `Expression.Call` emit site. After this task, the signature change is complete; all 584 existing tests still pass with `ctx = null` everywhere; PreserveReferences still has no runtime effect (cache preamble emission lands in Task 5).

This is the single biggest task in the plan. It's wide but mechanical.

**Files:**
- Modify: `C:\Repos\Atlas\src\Atlas\Internal\MappingInvoker.cs` — add `MappingContext? ctx` parameter to all 12 public static methods.
- Modify: `C:\Repos\Atlas\src\Atlas\Mapper.cs` — pass `null` for ctx in all 3 `Map` overloads (Task 4 changes this to allocate a real context).
- Modify: `C:\Repos\Atlas\src\Atlas\Internal\ExecutionPlanBuilder.cs` — update 5 `Expression.Call` emit sites to pass `ctxParam` (the lambda's MappingContext? parameter). Also add the parameter to the lambda signatures.
- Modify: `C:\Repos\Atlas\src\Atlas\Internal\DynamicPlanBuilder.cs` — update all `Expression.Call` emit sites that target `MappingInvoker.*` to pass `ctxParam`. Update reflection-dispatch in `ConvertObjectTo<T>` and `SerializeValue` (the `MakeGenericMethod(...).Invoke(...)` call sites) to include `ctx` in the args array. Add the parameter to the lambda signatures.

**Allowlist for the implementer subagent:** the four files above only.

**Important:** This task lands the SIGNATURE CHANGE. No behavioral change. The OFF path (existing tests) must continue to pass with `null` ctx. Cycle-safety doesn't activate yet (Mapper.cs still passes null in this task; Task 4 changes that).

- [ ] **Step 3.1: Update `MappingInvoker.cs` — all 12 method signatures**

For each of the following methods, add `MappingContext? ctx` as the parameter immediately AFTER `source` (or `value` / `src` / first non-registry parameter):

```csharp
// Before:
public static TDestination Invoke<TSource, TDestination>(MapperRegistry registry, TSource source) { ... }
// After:
public static TDestination Invoke<TSource, TDestination>(MapperRegistry registry, TSource source, MappingContext? ctx) { ... }

// Before:
public static void InvokeUpdate<TSource, TDestination>(MapperRegistry registry, TSource source, TDestination destination) { ... }
// After:
public static void InvokeUpdate<TSource, TDestination>(MapperRegistry registry, TSource source, MappingContext? ctx, TDestination destination) { ... }

// Before:
public static List<TDestination> InvokeToList<TSource, TDestination>(MapperRegistry registry, IEnumerable<TSource>? source) { ... }
// After:
public static List<TDestination> InvokeToList<TSource, TDestination>(MapperRegistry registry, IEnumerable<TSource>? source, MappingContext? ctx) { ... }

// Before:
public static TDestination[] InvokeToArray<TSource, TDestination>(MapperRegistry registry, IEnumerable<TSource>? source) { ... }
// After:
public static TDestination[] InvokeToArray<TSource, TDestination>(MapperRegistry registry, IEnumerable<TSource>? source, MappingContext? ctx) { ... }

// Before:
public static Dictionary<TKDest, TVDest> InvokeToDictionary<TKSrc, TVSrc, TKDest, TVDest>(MapperRegistry registry, Dictionary<TKSrc, TVSrc>? source) where TKSrc : notnull where TKDest : notnull { ... }
// After:
public static Dictionary<TKDest, TVDest> InvokeToDictionary<TKSrc, TVSrc, TKDest, TVDest>(MapperRegistry registry, Dictionary<TKSrc, TVSrc>? source, MappingContext? ctx) where TKSrc : notnull where TKDest : notnull { ... }

// Before:
public static T? ConvertObjectTo<T>(object? value, MapperRegistry registry, string keyForDiagnostics) { ... }
// After:
public static T? ConvertObjectTo<T>(object? value, MapperRegistry registry, MappingContext? ctx, string keyForDiagnostics) { ... }

// Before:
public static List<T>? ConvertObjectToList<T>(object? value, MapperRegistry registry, string keyForDiagnostics) { ... }
// After:
public static List<T>? ConvertObjectToList<T>(object? value, MapperRegistry registry, MappingContext? ctx, string keyForDiagnostics) { ... }

// Before:
public static T[]? ConvertObjectToArray<T>(object? value, MapperRegistry registry, string keyForDiagnostics) { ... }
// After:
public static T[]? ConvertObjectToArray<T>(object? value, MapperRegistry registry, MappingContext? ctx, string keyForDiagnostics) { ... }

// Before:
public static List<object?>? SerializeCollection<T>(IEnumerable<T>? src, MapperRegistry registry) { ... }
// After:
public static List<object?>? SerializeCollection<T>(IEnumerable<T>? src, MapperRegistry registry, MappingContext? ctx) { ... }

// Before:
public static IDictionary<string, object?>? SerializeDictionary<TKey, TValue>(IDictionary<TKey, TValue>? src, MapperRegistry registry) where TKey : notnull { ... }
// After:
public static IDictionary<string, object?>? SerializeDictionary<TKey, TValue>(IDictionary<TKey, TValue>? src, MapperRegistry registry, MappingContext? ctx) where TKey : notnull { ... }

// Before:
public static object? SerializeValue(object? value, Type declaredType, MapperRegistry registry) { ... }
// After:
public static object? SerializeValue(object? value, Type declaredType, MapperRegistry registry, MappingContext? ctx) { ... }

// ScanPrefix does NOT take a MappingContext (it's a pure dict-walk helper, no nested map calls).
// Leave its signature unchanged.
```

**Inside each method's body**, update internal nested calls to pass `ctx` through. Specifically:
- `Invoke<TS, TD>` — the cached delegate cast changes: `(Func<TSource, TDestination>)cached` becomes `(Func<TSource, MappingContext?, TDestination>)cached`. The invocation becomes `cached(source, ctx)` instead of `cached(source)`.
- `InvokeUpdate<TS, TD>` — same. `Action<TSource, TDestination>` becomes `Action<TSource, MappingContext?, TDestination>`. Invocation: `cached(source, ctx, destination)`.
- `InvokeToList<TS, TD>` and `InvokeToArray<TS, TD>` — when iterating elements, call `Invoke<TS, TD>(registry, item, ctx)` (passing the same ctx per element).
- `InvokeToDictionary<TKS, TVS, TKD, TVD>` — when iterating entries, call `Invoke<TKS, TKD>(registry, kv.Key, ctx)` and `Invoke<TVS, TVD>(registry, kv.Value, ctx)`.
- `ConvertObjectTo<T>` — its reflection-based dispatch (the `MakeGenericMethod(...).Invoke(null, [...])` site) appends `ctx` to the args array: `new object?[] { registry, value, ctx }`.
- `ConvertObjectToList<T>` and `ConvertObjectToArray<T>` — same propagation; pass `ctx` to inner `ConvertObjectTo<T>` calls.
- `SerializeValue` — its reflection-based dispatch appends `ctx`: `new object?[] { registry, value, ctx }`.
- `SerializeCollection<T>` — pass `ctx` to inner `SerializeValue` calls.
- `SerializeDictionary<TKey, TValue>` — pass `ctx` to inner `SerializeValue` calls.

**For each method, the signature change AND the body propagation must happen together** — a half-changed file won't compile.

- [ ] **Step 3.2: Update `Mapper.cs` — pass `null` for ctx in all 3 overloads**

In `C:\Repos\Atlas\src\Atlas\Mapper.cs`:

```csharp
// Map<TSource, TDestination>(TSource source) — line 21–22 area:
// Before:
public TDestination Map<TSource, TDestination>(TSource source) =>
    MappingInvoker.Invoke<TSource, TDestination>(_registry, source);
// After (Task 3 — passes null; Task 4 will allocate a real context):
public TDestination Map<TSource, TDestination>(TSource source) =>
    MappingInvoker.Invoke<TSource, TDestination>(_registry, source, null);

// Map<TDestination>(object source) — line 24–41 area:
// In the reflection-dispatch path, the args array passed to Invoke (via MakeGenericMethod) gains a null:
// Before:
var result = invokeMethod.Invoke(null, new object?[] { _registry, source });
// After:
var result = invokeMethod.Invoke(null, new object?[] { _registry, source, null });
// (And: when the reflection target is MappingInvoker.Invoke<,>, its signature now requires the ctx parameter,
//  so the args array MUST include the third null entry.)

// Map<TSource, TDestination>(TSource source, TDestination destination) — line 43–44 area:
// Before:
public void Map<TSource, TDestination>(TSource source, TDestination destination) =>
    MappingInvoker.InvokeUpdate(_registry, source, destination);
// After:
public void Map<TSource, TDestination>(TSource source, TDestination destination) =>
    MappingInvoker.InvokeUpdate(_registry, source, null, destination);
```

- [ ] **Step 3.3: Update `ExecutionPlanBuilder.cs` — 5 emit sites + lambda parameter**

In `C:\Repos\Atlas\src\Atlas\Internal\ExecutionPlanBuilder.cs`:

(a) Add `MappingContext? ctx` parameter to compiled lambdas. Where the existing code does:

```csharp
var srcParam = Expression.Parameter(typeMap.SourceType, "src");
// ... emit body referencing srcParam ...
var lambda = Expression.Lambda(body, srcParam);
```

Change to:

```csharp
var srcParam = Expression.Parameter(typeMap.SourceType, "src");
var ctxParam = Expression.Parameter(typeof(MappingContext), "ctx");
// ... emit body referencing srcParam (and ctxParam will be referenced in Task 5's cache preamble) ...
var lambda = Expression.Lambda(body, srcParam, ctxParam);
```

(b) For `BuildUpdate`-style lambdas that already have a `dstParam`, add `ctxParam` between `srcParam` and `dstParam`:

```csharp
var lambda = Expression.Lambda(body, srcParam, ctxParam, dstParam);
```

(c) Update each of the 5 `Expression.Call` emit sites (around lines 112, 435, 450, 484, 505) to include `ctxParam` as the third argument (after `Expression.Constant(registry)` and `sourceMemberAccess`):

```csharp
// Before (around line 435 — BuildNestedInvoke):
return Expression.Call(invokeMethod,
    Expression.Constant(registry),
    sourceExpr);
// After:
return Expression.Call(invokeMethod,
    Expression.Constant(registry),
    sourceExpr,
    ctxParam);

// Same pattern for the other 4 emit sites (lines 112, 450, 484, 505).
// In every case: ctxParam is the 3rd argument to the Expression.Call.
// The closed MethodInfo (invokeMethod or its MakeGenericMethod variants) now resolves to a method
// that accepts (MapperRegistry, TSource, MappingContext?) since Task 3.1 changed the static
// helpers' signatures.
```

The `ctxParam` reference must be visible in the helper-method scope. If `BuildNestedInvoke` (or similar) takes `ctxParam` from an enclosing closure, ensure the enclosing method passes it through.

**For helper methods that currently take a `srcParam` parameter**, add a `ctxParam` parameter alongside. Verify by inspection that `BuildNestedInvoke`, `BuildCollectionInvoke`, `BuildCollectionLambda`, `BuildDictionaryLambda`, and the inheritance-dispatch emit (line 112) all have access to the lambda's `ctxParam`.

- [ ] **Step 3.4: Update `DynamicPlanBuilder.cs` — all emit sites + lambda parameter**

In `C:\Repos\Atlas\src\Atlas\Internal\DynamicPlanBuilder.cs`:

(a) Add `ctxParam` to the dict→POCO and POCO→dict lambda signatures. Same pattern as ExecutionPlanBuilder.

(b) Update every `Expression.Call` site that targets one of the cached `MethodInfo` fields (`_invokeMethod`, `_invokeUpdateMethod`, `_serializeValue`, `_serializeCollection`, `_serializeDictionary`, `_convertObjectTo`, `_convertObjectToList`, `_convertObjectToArray`) to pass `ctxParam` as the appropriate argument. For example:

```csharp
// Before (EmitPropertyAssign — Invoke for dict-value dispatch):
var convertCall = Expression.Call(
    convertMethodGeneric.MakeGenericMethod(dstProp.PropertyType),
    valueVar, registryConst, keyExpr);
// After:
var convertCall = Expression.Call(
    convertMethodGeneric.MakeGenericMethod(dstProp.PropertyType),
    valueVar, registryConst, ctxParam, keyExpr);
// (The argument order matches the new ConvertObjectTo<T> signature: value, registry, ctx, key.)
```

```csharp
// Before (EmitNestedPocoAssign — typed-instance recursion):
var nestedInvoke = Expression.Call(
    _invokeMethod.MakeGenericMethod(_dictType, propType),
    registryConst, nestedDictCastVar);
// After:
var nestedInvoke = Expression.Call(
    _invokeMethod.MakeGenericMethod(_dictType, propType),
    registryConst, nestedDictCastVar, ctxParam);
```

(c) Update the reflection-based dispatch in `MappingInvoker.ConvertObjectTo<T>` and `SerializeValue` (which DynamicPlanBuilder doesn't directly emit but its runtime helpers consume) — done in Task 3.1 already; verify the args arrays are correctly extended.

- [ ] **Step 3.5: Build verification — must compile clean**

```pwsh
dotnet build --nologo
```

Expected: zero errors, zero warnings (with `<TreatWarningsAsErrors>` enabled).

- [ ] **Step 3.6: Run full suite — must pass with no regressions**

```pwsh
dotnet test --nologo 2>&1 | Select-String -Pattern "Passed:|Failed:"
```

Expected: total Passed = 584; Failed: 0. **The signature change is purely additive — all existing behaviors must continue to work because `ctx` is null everywhere.**

- [ ] **Step 3.7: Commit**

```pwsh
git add src/Atlas/Internal/MappingInvoker.cs src/Atlas/Mapper.cs src/Atlas/Internal/ExecutionPlanBuilder.cs src/Atlas/Internal/DynamicPlanBuilder.cs
git commit -m "MappingContext? ctx parameter threaded through every MappingInvoker.* signature and every codegen emit site (Task 3 — wide signature change, no behavior change)"
```

---

## Task 4 — `IMapper.Map` allocates `MappingContext` based on `tm.PreserveReferences`

**Goal:** Wire the public-API boundary to allocate a fresh `MappingContext` when the top-level typemap has `PreserveReferences = true`. After this task, the context flows through the lambda chain — but the cache preamble doesn't yet exist (Task 5), so cycle-breaking still doesn't work end-to-end. This task is the integration point.

**Files:**
- Modify: `C:\Repos\Atlas\src\Atlas\Mapper.cs` — change all 3 `Map` overloads to allocate context based on `tm.PreserveReferences`.

**Allowlist for the implementer subagent:** the one file above only.

- [ ] **Step 4.1: Update `Map<TSource, TDestination>(TSource)` overload**

In `C:\Repos\Atlas\src\Atlas\Mapper.cs`, find the typed two-generic overload (around line 21):

```csharp
// Replace:
public TDestination Map<TSource, TDestination>(TSource source) =>
    MappingInvoker.Invoke<TSource, TDestination>(_registry, source, null);

// With:
public TDestination Map<TSource, TDestination>(TSource source)
{
    var pair = new TypePair(typeof(TSource), typeof(TDestination));
    var tm = _registry.GetTypeMap(pair);
    var ctx = tm is { PreserveReferences: true } ? new MappingContext() : null;
    return MappingInvoker.Invoke<TSource, TDestination>(_registry, source, ctx);
}
```

(Add `using Atlas.Internal;` if `MappingContext` isn't already imported. The `_registry` field is `MapperRegistry` per the structural inventory.)

- [ ] **Step 4.2: Update `Map<TDestination>(object source)` overload**

The reflection-dispatch overload (around line 24–41):

```csharp
public TDestination Map<TDestination>(object source)
{
    if (source is null)
        throw new ArgumentNullException(nameof(source));

    var srcType = source.GetType();
    var pair = new TypePair(srcType, typeof(TDestination));
    var tm = _registry.GetTypeMap(pair);
    var ctx = tm is { PreserveReferences: true } ? new MappingContext() : null;

    var invokeMethod = typeof(MappingInvoker)
        .GetMethod(nameof(MappingInvoker.Invoke))!
        .MakeGenericMethod(srcType, typeof(TDestination));

    try
    {
        return (TDestination)invokeMethod.Invoke(null, new object?[] { _registry, source, ctx })!;
    }
    catch (TargetInvocationException tie) when (tie.InnerException is not null)
    {
        ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
        throw;  // unreachable — satisfies compiler
    }
}
```

(Preserve the existing `TargetInvocationException` unwrap from PR #10. The args array now contains `ctx` instead of `null`.)

- [ ] **Step 4.3: Update `Map<TSource, TDestination>(TSource, TDestination)` overload**

The update-in-place overload (around line 43):

```csharp
// Replace:
public void Map<TSource, TDestination>(TSource source, TDestination destination) =>
    MappingInvoker.InvokeUpdate(_registry, source, null, destination);

// With:
public void Map<TSource, TDestination>(TSource source, TDestination destination)
{
    var pair = new TypePair(typeof(TSource), typeof(TDestination));
    var tm = _registry.GetTypeMap(pair);
    var ctx = tm is { PreserveReferences: true } ? new MappingContext() : null;
    MappingInvoker.InvokeUpdate(_registry, source, ctx, destination);
}
```

- [ ] **Step 4.4: Run full suite — verify no regression**

```pwsh
dotnet test --nologo 2>&1 | Select-String -Pattern "Passed:|Failed:"
```

Expected: total Passed = 584; Failed: 0. No new tests yet; this task is integration plumbing. Cycle-safety still doesn't work end-to-end because the cache preamble isn't emitted yet.

- [ ] **Step 4.5: Commit**

```pwsh
git add src/Atlas/Mapper.cs
git commit -m "IMapper.Map allocates MappingContext when typemap has PreserveReferences (Task 4)"
```

---

## Task 5 — Cache preamble codegen + end-to-end cycle-breaking tests

**Goal:** Emit the cache-check + cache-register block at the top of every POCO-typed compiled lambda where `srcType.IsClass`. After this task, cycles work end-to-end. This is the algorithmic heart of the feature.

**Files:**
- Modify: `C:\Repos\Atlas\src\Atlas\Internal\ExecutionPlanBuilder.cs` — add cache preamble emission to `Build` (fresh-map) and `BuildUpdate` (update-in-place).
- Create: `C:\Repos\Atlas\tests\Atlas.Tests\MapperPreserveReferencesTests.cs` — 20 end-to-end tests.

**Allowlist for the implementer subagent:** the two files above only.

- [ ] **Step 5.1: Write `MapperPreserveReferencesTests.cs` (failing — most tests fail at this point)**

```csharp
namespace Atlas.Tests;

using System.Collections.Generic;
using System.Linq;
using Atlas;

public class MapperPreserveReferencesTests
{
    // ─── Cycle-breaking ─────────────────────────────────────────────────────────

    [Fact]
    public void SelfCycle_PersonBossEqualsSelf_TerminatesAndPreservesIdentity()
    {
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>().PreserveReferences()).CreateMapper();
        var alice = new Person { Name = "Alice" };
        alice.Boss = alice;

        var dto = mapper.Map<PersonDto>(alice);

        Assert.Equal("Alice", dto.Name);
        Assert.NotNull(dto.Boss);
        Assert.Same(dto, dto.Boss);
    }

    [Fact]
    public void MutualCycle_TwoPeoplePointAtEachOther_BothEdgesPreserved()
    {
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>().PreserveReferences()).CreateMapper();
        var alice = new Person { Name = "Alice" };
        var bob = new Person { Name = "Bob" };
        alice.Boss = bob;
        bob.Boss = alice;

        var aliceDto = mapper.Map<PersonDto>(alice);

        Assert.Equal("Alice", aliceDto.Name);
        Assert.Equal("Bob", aliceDto.Boss!.Name);
        Assert.Same(aliceDto, aliceDto.Boss.Boss);
    }

    [Fact]
    public void LongerCycle_ABThenBA_TerminatesAndPreservesIdentity()
    {
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>().PreserveReferences()).CreateMapper();
        var a = new Person { Name = "A" };
        var b = new Person { Name = "B" };
        var ccc = new Person { Name = "C" };
        a.Boss = b;
        b.Boss = ccc;
        ccc.Boss = a;

        var aDto = mapper.Map<PersonDto>(a);

        Assert.Equal("A", aDto.Name);
        Assert.Equal("B", aDto.Boss!.Name);
        Assert.Equal("C", aDto.Boss.Boss!.Name);
        Assert.Same(aDto, aDto.Boss.Boss.Boss);
    }

    [Fact]
    public void SelfCycleViaCollection_PersonFriendsContainsSelf()
    {
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>().PreserveReferences()).CreateMapper();
        var alice = new Person { Name = "Alice" };
        alice.Friends = new List<Person> { alice };

        var dto = mapper.Map<PersonDto>(alice);

        Assert.Single(dto.Friends!);
        Assert.Same(dto, dto.Friends![0]);
    }

    [Fact]
    public void CycleAcrossCollectionElements_BothElementsReferenceEachOther()
    {
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>().PreserveReferences()).CreateMapper();
        var alice = new Person { Name = "Alice" };
        var bob = new Person { Name = "Bob" };
        alice.Friends = new List<Person> { bob };
        bob.Friends = new List<Person> { alice };

        var aliceDto = mapper.Map<PersonDto>(alice);

        Assert.Same(aliceDto, aliceDto.Friends![0].Friends![0]);
    }

    // ─── Shared-reference deduplication ─────────────────────────────────────────

    [Fact]
    public void SharedReference_DepartmentAcrossManyEmployees_AllocatedOnce()
    {
        var mapper = new MapperConfiguration(c =>
        {
            c.CreateMap<Department, DepartmentDto>().PreserveReferences();
            c.CreateMap<Employee, EmployeeDto>();
        }).CreateMapper();

        var sales = new Department { Name = "Sales" };
        var emp1 = new Employee { Name = "Alice", Department = sales };
        var emp2 = new Employee { Name = "Bob", Department = sales };
        var emp3 = new Employee { Name = "Carol", Department = sales };
        sales.Employees = new List<Employee> { emp1, emp2, emp3 };

        var dto = mapper.Map<DepartmentDto>(sales);

        Assert.Same(dto.Employees![0].Department, dto.Employees[1].Department);
        Assert.Same(dto.Employees[1].Department, dto.Employees[2].Department);
        Assert.Same(dto, dto.Employees[0].Department);
    }

    [Fact]
    public void SharedReference_TwoElementsInSameList_DedupedOnSecondOccurrence()
    {
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>().PreserveReferences()).CreateMapper();
        var alice = new Person { Name = "Alice" };
        var bob = new Person { Name = "Bob" };

        // alice appears twice in the source list
        var src = new Person { Name = "Root", Friends = new List<Person> { alice, bob, alice } };

        var dto = mapper.Map<PersonDto>(src);

        Assert.Equal(3, dto.Friends!.Count);
        Assert.Equal("Alice", dto.Friends[0].Name);
        Assert.Equal("Bob", dto.Friends[1].Name);
        Assert.Same(dto.Friends[0], dto.Friends[2]);  // second alice deduped
    }

    [Fact]
    public void SharedReference_TwoCollectionsReferencingSameInstance_PreservesIdentity()
    {
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Group, GroupDto>().PreserveReferences()).CreateMapper();
        var alice = new Person { Name = "Alice" };
        var grp = new Group
        {
            Members = new List<Person> { alice },
            Admins = new List<Person> { alice }
        };

        var dto = mapper.Map<GroupDto>(grp);

        Assert.Same(dto.Members![0], dto.Admins![0]);  // alice deduped across two lists
    }

    [Fact]
    public void SharedReference_AcrossNestedAndOuterScope()
    {
        var mapper = new MapperConfiguration(c =>
        {
            c.CreateMap<Department, DepartmentDto>().PreserveReferences();
            c.CreateMap<Employee, EmployeeDto>();
        }).CreateMapper();

        var dept = new Department { Name = "Engineering" };
        var emp = new Employee { Name = "Alice", Department = dept };
        dept.Employees = new List<Employee> { emp };

        var dto = mapper.Map<DepartmentDto>(dept);

        Assert.Same(dto, dto.Employees![0].Department);
    }

    // ─── Fresh-map allocation ───────────────────────────────────────────────────

    [Fact]
    public void Map_FreshSimpleCycle_ReturnsNewInstance_NotSourceReference()
    {
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>().PreserveReferences()).CreateMapper();
        var alice = new Person { Name = "Alice" };
        alice.Boss = alice;

        var dto = mapper.Map<PersonDto>(alice);

        Assert.NotSame(alice, dto);
    }

    [Fact]
    public void Map_FreshGraph_AllPropertiesPopulatedCorrectly_DespiteCycle()
    {
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>().PreserveReferences()).CreateMapper();
        var alice = new Person { Name = "Alice", Age = 30 };
        alice.Boss = alice;

        var dto = mapper.Map<PersonDto>(alice);

        Assert.Equal("Alice", dto.Name);
        Assert.Equal(30, dto.Age);
        Assert.Same(dto, dto.Boss);
    }

    [Fact]
    public void Map_NullSource_ReturnsDefault_RegardlessOfPreserveReferences()
    {
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>().PreserveReferences()).CreateMapper();
        Person? src = null;

        var dto = mapper.Map<Person?, PersonDto?>(src);

        Assert.Null(dto);
    }

    [Fact]
    public void Map_NullCycleField_LeavesDestinationCycleFieldNull()
    {
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>().PreserveReferences()).CreateMapper();
        var alice = new Person { Name = "Alice", Boss = null };

        var dto = mapper.Map<PersonDto>(alice);

        Assert.Equal("Alice", dto.Name);
        Assert.Null(dto.Boss);
    }

    // ─── OFF path ───────────────────────────────────────────────────────────────

    [Fact]
    public void WithoutPreserveReferences_NormalCycleFails_AsExpected()
    {
        // Sanity: confirms the v1 behavior is unchanged when flag is off.
        // A self-cycle without PreserveReferences will exhaust the stack at some bounded depth.
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>()).CreateMapper();    // NO .PreserveReferences()
        var alice = new Person { Name = "Alice" };
        alice.Boss = alice;

        // The exact exception type depends on stack-overflow handling; just verify it throws.
        Assert.ThrowsAny<Exception>(() => mapper.Map<PersonDto>(alice));
    }

    [Fact]
    public void WithoutPreserveReferences_NonCyclicGraph_StillMapsCorrectly()
    {
        // Verifies the cache preamble is a no-op when ctx is null (i.e., flag is off).
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>()).CreateMapper();
        var alice = new Person { Name = "Alice", Age = 30 };

        var dto = mapper.Map<PersonDto>(alice);

        Assert.Equal("Alice", dto.Name);
        Assert.Equal(30, dto.Age);
    }

    // ─── Multiple top-level calls ──────────────────────────────────────────────

    [Fact]
    public void MultipleTopLevelCalls_EachAllocatesFreshContext()
    {
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>().PreserveReferences()).CreateMapper();
        var alice = new Person { Name = "Alice" };
        alice.Boss = alice;

        var dto1 = mapper.Map<PersonDto>(alice);
        var dto2 = mapper.Map<PersonDto>(alice);

        Assert.NotSame(dto1, dto2);  // each call gets a fresh context, fresh destination
        Assert.Same(dto1, dto1.Boss);
        Assert.Same(dto2, dto2.Boss);
    }

    // ─── Reference vs value-type sources ───────────────────────────────────────

    [Fact]
    public void ValueTypeSource_PreserveReferences_WorksWithoutCachePreamble()
    {
        // struct sources skip the cache preamble at codegen time. The flag is harmless.
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<PointStruct, PointDto>().PreserveReferences()).CreateMapper();
        var p = new PointStruct { X = 1, Y = 2 };

        var dto = mapper.Map<PointDto>(p);

        Assert.Equal(1, dto.X);
        Assert.Equal(2, dto.Y);
    }

    [Fact]
    public void ReferenceTypeSource_CachePreambleEmittedForReferenceTypeSources()
    {
        // Sanity check on the codegen rule: class-typed sources get the cache preamble.
        // We verify behaviorally (cycle gets resolved) rather than via codegen inspection.
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>().PreserveReferences()).CreateMapper();
        var alice = new Person { Name = "Alice" };
        alice.Boss = alice;

        var dto = mapper.Map<PersonDto>(alice);  // would stack-overflow without cache

        Assert.Same(dto, dto.Boss);
    }

    // ─── Fresh-map with primitive properties ───────────────────────────────────

    [Fact]
    public void Map_PrimitiveProperties_PopulatedCorrectly_WithCycle()
    {
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>().PreserveReferences()).CreateMapper();
        var alice = new Person { Name = "Alice", Age = 30 };
        alice.Boss = alice;

        var dto = mapper.Map<PersonDto>(alice);

        Assert.Equal("Alice", dto.Name);
        Assert.Equal(30, dto.Age);
    }

    [Fact]
    public void Map_DeepGraphWithoutCycles_PreserveReferencesOff_StillWorks()
    {
        // Control: deep non-cyclic graph; no PreserveReferences; should map fine.
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>()).CreateMapper();
        var src = new Person
        {
            Name = "A",
            Boss = new Person { Name = "B", Boss = new Person { Name = "C" } }
        };

        var dto = mapper.Map<PersonDto>(src);

        Assert.Equal("A", dto.Name);
        Assert.Equal("B", dto.Boss!.Name);
        Assert.Equal("C", dto.Boss.Boss!.Name);
    }

    // ─── Test fixtures ──────────────────────────────────────────────────────────

    private sealed class Person
    {
        public string? Name { get; set; }
        public int Age { get; set; }
        public Person? Boss { get; set; }
        public List<Person>? Friends { get; set; }
    }

    private sealed class PersonDto
    {
        public string? Name { get; set; }
        public int Age { get; set; }
        public PersonDto? Boss { get; set; }
        public List<PersonDto>? Friends { get; set; }
    }

    private sealed class Department
    {
        public string? Name { get; set; }
        public List<Employee>? Employees { get; set; }
    }

    private sealed class DepartmentDto
    {
        public string? Name { get; set; }
        public List<EmployeeDto>? Employees { get; set; }
    }

    private sealed class Employee
    {
        public string? Name { get; set; }
        public Department? Department { get; set; }
    }

    private sealed class EmployeeDto
    {
        public string? Name { get; set; }
        public DepartmentDto? Department { get; set; }
    }

    private sealed class Group
    {
        public List<Person>? Members { get; set; }
        public List<Person>? Admins { get; set; }
    }

    private sealed class GroupDto
    {
        public List<PersonDto>? Members { get; set; }
        public List<PersonDto>? Admins { get; set; }
    }

    private struct PointStruct
    {
        public int X;
        public int Y;
    }

    private sealed class PointDto
    {
        public int X { get; set; }
        public int Y { get; set; }
    }
}
```

- [ ] **Step 5.2: Run new tests — verify they fail (cycles cause stack-overflow / runtime failure)**

```pwsh
dotnet test tests/Atlas.Tests/Atlas.Tests.csproj --filter FullyQualifiedName~MapperPreserveReferencesTests --nologo
```

Expected: most tests fail with stack-overflow or recursion errors. Tests that don't exercise cycles (`Map_NullSource_*`, `WithoutPreserveReferences_NonCyclicGraph_*`, `Map_PrimitiveProperties_*`, `Map_DeepGraphWithoutCycles_*`, `ValueTypeSource_*`) pass.

- [ ] **Step 5.3: Implement cache preamble in `ExecutionPlanBuilder.Build`**

In `C:\Repos\Atlas\src\Atlas\Internal\ExecutionPlanBuilder.cs`, find `Build(TypeMap, MapperRegistry)` (around line 12). At the top of `BuildPocoLambda` (or wherever the POCO body is constructed — this is the lambda that allocates the destination via `Expression.New`), insert the cache preamble BEFORE the existing destination allocation:

```csharp
// ── Inside BuildPocoLambda (or wherever the POCO destination is allocated) ──

var srcType = typeMap.SourceType;
var dstType = typeMap.DestinationType;

// (existing parameter binding for srcParam, ctxParam — added in Task 3)

var dstVar = Expression.Variable(dstType, "dst");
var bodyStatements = new List<Expression>();

// ── NEW: cache preamble (only when source is a reference type) ──
if (srcType.IsClass)
{
    var ctxNotNullCheck = Expression.NotEqual(ctxParam, Expression.Constant(null, typeof(MappingContext)));
    var tryGetMethod = typeof(MappingContext).GetMethod(
        nameof(MappingContext.TryGet),
        BindingFlags.NonPublic | BindingFlags.Instance)!;
    var cachedVar = Expression.Variable(typeof(object), "cached");

    var cacheCheckBlock = Expression.Block(
        new[] { cachedVar },
        Expression.IfThen(
            Expression.AndAlso(
                ctxNotNullCheck,
                Expression.Call(ctxParam, tryGetMethod,
                    Expression.Convert(srcParam, typeof(object)),
                    Expression.Constant(dstType, typeof(Type)),
                    cachedVar)),
            Expression.Return(returnLabel, Expression.Convert(cachedVar, dstType))));
    bodyStatements.Add(cacheCheckBlock);
}

// ── Existing: allocate destination ──
bodyStatements.Add(Expression.Assign(dstVar, Expression.New(dstType)));

// ── NEW: register destination into cache (only when source is a reference type) ──
if (srcType.IsClass)
{
    var registerMethod = typeof(MappingContext).GetMethod(
        nameof(MappingContext.Register),
        BindingFlags.NonPublic | BindingFlags.Instance)!;
    var registerCall = Expression.IfThen(
        Expression.NotEqual(ctxParam, Expression.Constant(null, typeof(MappingContext))),
        Expression.Call(ctxParam, registerMethod,
            Expression.Convert(srcParam, typeof(object)),
            Expression.Constant(dstType, typeof(Type)),
            Expression.Convert(dstVar, typeof(object))));
    bodyStatements.Add(registerCall);
}

// ── Existing: BeforeMap hooks, member emit, AfterMap hooks, return dst ──
// ... existing body emission unchanged ...
bodyStatements.Add(Expression.Label(returnLabel, dstVar));
```

(Adapt the implementation to match the exact existing structure of `BuildPocoLambda`. The key invariants are: cache check happens FIRST, BEFORE destination allocation; destination allocation; cache register happens BEFORE BeforeMap hooks and member emit. If the existing body uses `Expression.Block` with explicit return label, integrate the cache-check `Expression.Return(returnLabel, ...)` accordingly.)

**The `Expression.Convert(srcParam, typeof(object))` boxing is necessary** because `MappingContext.TryGet`/`Register` accept `object source`, but `srcParam` is typed as `TSrc` (which may be a reference type that the JIT happily upcasts, but `Expression.Call` requires explicit conversion).

- [ ] **Step 5.4: Implement cache preamble in `ExecutionPlanBuilder.BuildUpdate`**

Mirror the same logic in the `BuildUpdate` method (around line 136 per structural inventory) — but use the `existingDest` parameter instead of allocating fresh:

```csharp
// Inside BuildUpdate (or BuildBaseBody for update):
// dstVar is bound to the existingDest parameter, not Expression.New

// Cache preamble — same shape as in Build:
if (srcType.IsClass)
{
    // 1. Cache check: if hit, return cached (NOT existingDest)
    // 2. After existingDest is bound to dstVar, register existingDest into cache
}
```

The key difference: in update-in-place, `dstVar = dstParam` (the user-supplied existing destination). The cache register uses `dstParam`, not a fresh allocation.

- [ ] **Step 5.5: Run new tests — verify they pass**

```pwsh
dotnet test tests/Atlas.Tests/Atlas.Tests.csproj --filter FullyQualifiedName~MapperPreserveReferencesTests --nologo
```

Expected: `Passed: 20, Failed: 0`.

- [ ] **Step 5.6: Run full suite — verify no regression**

```pwsh
dotnet test --nologo 2>&1 | Select-String -Pattern "Passed:|Failed:"
```

Expected: total Passed = 584 + 20 = 604; Failed: 0.

- [ ] **Step 5.7: Commit**

```pwsh
git add src/Atlas/Internal/ExecutionPlanBuilder.cs tests/Atlas.Tests/MapperPreserveReferencesTests.cs
git commit -m "Cache preamble codegen + 20 end-to-end cycle-breaking tests (Task 5)"
```

---

## Task 6 — Propagation: Inheritance, ReverseMap, OpenGenerics

**Goal:** Wire the `PreserveReferences` flag through the three propagation sites: `InheritanceMerger` (base→derived), `MappingExpression.ReverseMap` (forward→reverse), and `MapperRegistry.MaterializeClosed` (open-generic template→closed pair). The OpenGenerics propagation requires also adding `OpenGenericTypeMap.PreserveReferences` field AND a minimal `IOpenGenericMappingExpression` interface with `PreserveReferences()` so the open-generic registration can opt in.

**Files:**
- Modify: `C:\Repos\Atlas\src\Atlas\Internal\InheritanceMerger.cs` — add TypeMap-level propagation: `derived.PreserveReferences = base.PreserveReferences || derived.PreserveReferences`.
- Modify: `C:\Repos\Atlas\src\Atlas\Configuration\MappingExpression.cs` — extend `ReverseMap` (line 149-154) to copy `PreserveReferences` to the reverse pair.
- Modify: `C:\Repos\Atlas\src\Atlas\Internal\OpenGenericTypeMap.cs` — add `bool PreserveReferences { get; set; }` field.
- Create: `C:\Repos\Atlas\src\Atlas\Configuration\IOpenGenericMappingExpression.cs` — new interface with `PreserveReferences()`.
- Create: `C:\Repos\Atlas\src\Atlas\Configuration\OpenGenericMappingExpression.cs` — implementation wrapping `OpenGenericTypeMap`.
- Modify: `C:\Repos\Atlas\src\Atlas\Configuration\MapperConfigurationExpression.cs` — change `CreateMap(Type, Type, MemberList)` to return `IOpenGenericMappingExpression`.
- Modify: `C:\Repos\Atlas\src\Atlas\MapperProfile.cs` — same change for `CreateMap(Type, Type, MemberList)`.
- Modify: `C:\Repos\Atlas\src\Atlas\Internal\MapperRegistry.cs` — extend `MaterializeClosed` (line 147–167) to propagate `PreserveReferences` from template to closed pair.
- Create: `C:\Repos\Atlas\tests\Atlas.Tests\MapperPreserveReferencesPropagationTests.cs` — 9 tests.

**Allowlist for the implementer subagent:** the eight files above only.

- [ ] **Step 6.1: Write `MapperPreserveReferencesPropagationTests.cs` (failing)**

```csharp
namespace Atlas.Tests;

using System.Collections.Generic;
using Atlas;
using Atlas.Internal;

public class MapperPreserveReferencesPropagationTests
{
    [Fact]
    public void Inheritance_BasePreserveReferences_PropagatesToDerivedViaInclude()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap<Person, PersonDto>().PreserveReferences().Include<Manager, ManagerDto>();
            c.CreateMap<Manager, ManagerDto>();
        });

        var derivedTm = cfg.Internal_Registry.GetTypeMap(new TypePair(typeof(Manager), typeof(ManagerDto)));
        Assert.NotNull(derivedTm);
        Assert.True(derivedTm!.PreserveReferences);
    }

    [Fact]
    public void Inheritance_BaseWithoutPreserveReferences_DoesNotForcePreserveReferencesOnDerived()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap<Person, PersonDto>().Include<Manager, ManagerDto>();   // no PR
            c.CreateMap<Manager, ManagerDto>();
        });

        var derivedTm = cfg.Internal_Registry.GetTypeMap(new TypePair(typeof(Manager), typeof(ManagerDto)));
        Assert.NotNull(derivedTm);
        Assert.False(derivedTm!.PreserveReferences);
    }

    [Fact]
    public void ReverseMap_PropagatesPreserveReferencesToReversePair()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>().PreserveReferences().ReverseMap());

        var forwardTm = cfg.Internal_Registry.GetTypeMap(new TypePair(typeof(Person), typeof(PersonDto)));
        var reverseTm = cfg.Internal_Registry.GetTypeMap(new TypePair(typeof(PersonDto), typeof(Person)));
        Assert.NotNull(forwardTm);
        Assert.NotNull(reverseTm);
        Assert.True(forwardTm!.PreserveReferences);
        Assert.True(reverseTm!.PreserveReferences);
    }

    [Fact]
    public void OpenGeneric_PropagatesFromTemplateToClosedMaterialization()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>)).PreserveReferences());

        var mapper = cfg.CreateMapper();
        // Trigger materialization by performing a map call:
        var src = new Wrapper<int> { Value = 42 };
        var dst = mapper.Map<Wrapper<int>, WrapperDto<int>>(src);

        var closedTm = cfg.Internal_Registry.GetTypeMap(new TypePair(typeof(Wrapper<int>), typeof(WrapperDto<int>)));
        Assert.NotNull(closedTm);
        Assert.True(closedTm!.PreserveReferences);
    }

    [Fact]
    public void DownPropagation_OuterFlagged_InnerNotFlagged_InnerStillCacheActive()
    {
        // Department has PR; Employee does not. Cycle inside Employee subgraph still resolves
        // because the runtime ctx is propagated from outer Department call.
        var mapper = new MapperConfiguration(c =>
        {
            c.CreateMap<Department, DepartmentDto>().PreserveReferences();
            c.CreateMap<Employee, EmployeeDto>();   // NOT flagged
        }).CreateMapper();

        var dept = new Department { Name = "Engineering" };
        var alice = new Employee { Name = "Alice", Department = dept };
        var bob = new Employee { Name = "Bob", Department = dept, Manager = null };
        alice.Manager = bob;
        bob.Manager = alice;   // cycle inside Employee subgraph
        dept.Employees = new List<Employee> { alice, bob };

        var dto = mapper.Map<DepartmentDto>(dept);

        Assert.Same(dto.Employees![0], dto.Employees[1].Manager);  // bob.Manager == alice (same instance)
        Assert.Same(dto.Employees![1], dto.Employees[0].Manager);  // alice.Manager == bob (same instance)
    }

    [Fact]
    public void DownPropagation_OuterNotFlagged_InnerFlagged_NoCacheActive()
    {
        // Outer Department NOT flagged, inner Employee flagged. mapper.Map<DepartmentDto>(dept) does
        // not allocate context, so the inner Employee call gets ctx = null. Cycle inside Employee
        // subgraph stack-overflows (documented v1 limitation).
        var mapper = new MapperConfiguration(c =>
        {
            c.CreateMap<Department, DepartmentDto>();   // NOT flagged
            c.CreateMap<Employee, EmployeeDto>().PreserveReferences();
        }).CreateMapper();

        var dept = new Department { Name = "Sales" };
        var alice = new Employee { Name = "Alice", Department = dept };
        alice.Manager = alice;   // self-cycle
        dept.Employees = new List<Employee> { alice };

        Assert.ThrowsAny<Exception>(() => mapper.Map<DepartmentDto>(dept));
    }

    [Fact]
    public void DownPropagation_InnerCallExplicitly_AllocatesContextWhenInnerFlagged()
    {
        // Calling mapper.Map<EmployeeDto>(alice) directly → outer typemap is Employee → flagged →
        // context allocated → cycles resolved.
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Employee, EmployeeDto>().PreserveReferences()).CreateMapper();

        var alice = new Employee { Name = "Alice" };
        alice.Manager = alice;

        var dto = mapper.Map<EmployeeDto>(alice);

        Assert.Same(dto, dto.Manager);
    }

    [Fact]
    public void Hooks_FireOnFirstAllocation_NotOnCacheHit()
    {
        var beforeCount = 0;
        var afterCount = 0;
        var mapper = new MapperConfiguration(c =>
        {
            c.CreateMap<Person, PersonDto>()
                .PreserveReferences()
                .BeforeMap((s, d) => beforeCount++)
                .AfterMap((s, d) => afterCount++);
        }).CreateMapper();

        var alice = new Person { Name = "Alice" };
        alice.Boss = alice;

        mapper.Map<PersonDto>(alice);

        // alice appears twice in the call graph (top-level + as Boss), but hooks should fire only ONCE.
        Assert.Equal(1, beforeCount);
        Assert.Equal(1, afterCount);
    }

    [Fact]
    public void ValueTransformers_FireOnFirstAllocation_NotOnCacheHit()
    {
        var transformCount = 0;
        var mapper = new MapperConfiguration(c =>
        {
            c.ValueTransformers.Add<string>(s => { transformCount++; return s == null ? null! : s.ToUpper(); });
            c.CreateMap<Person, PersonDto>().PreserveReferences();
        }).CreateMapper();

        var alice = new Person { Name = "alice" };
        alice.Boss = alice;

        var dto = mapper.Map<PersonDto>(alice);

        // The Name transformer fires once for the cycle-resolved dto.
        Assert.Equal("ALICE", dto.Name);
        Assert.Equal(1, transformCount);
    }

    // ─── Test fixtures ──────────────────────────────────────────────────────────

    private class Person
    {
        public string? Name { get; set; }
        public Person? Boss { get; set; }
    }

    private class PersonDto
    {
        public string? Name { get; set; }
        public PersonDto? Boss { get; set; }
    }

    private class Manager : Person
    {
        public string? Department { get; set; }
    }

    private class ManagerDto : PersonDto
    {
        public string? Department { get; set; }
    }

    private sealed class Department
    {
        public string? Name { get; set; }
        public List<Employee>? Employees { get; set; }
    }

    private sealed class DepartmentDto
    {
        public string? Name { get; set; }
        public List<EmployeeDto>? Employees { get; set; }
    }

    private sealed class Employee
    {
        public string? Name { get; set; }
        public Department? Department { get; set; }
        public Employee? Manager { get; set; }
    }

    private sealed class EmployeeDto
    {
        public string? Name { get; set; }
        public DepartmentDto? Department { get; set; }
        public EmployeeDto? Manager { get; set; }
    }

    private sealed class Wrapper<T> { public T Value { get; set; } = default!; }
    private sealed class WrapperDto<T> { public T Value { get; set; } = default!; }
}
```

- [ ] **Step 6.2: Run new tests — verify they fail**

Most tests fail because propagation isn't wired yet.

- [ ] **Step 6.3: Add `OpenGenericTypeMap.PreserveReferences` field**

In `C:\Repos\Atlas\src\Atlas\Internal\OpenGenericTypeMap.cs`, add a new property:

```csharp
public bool PreserveReferences { get; set; }
```

Place it adjacent to existing properties (e.g., after `MemberList` or `OriginatingProfile`).

- [ ] **Step 6.4: Create `IOpenGenericMappingExpression.cs`**

```csharp
namespace Atlas.Configuration;

/// <summary>
/// Fluent surface returned by <see cref="MapperConfigurationExpression.CreateMap(Type, Type, MemberList)"/>
/// (and the matching <see cref="MapperProfile"/> overload). Provides a minimal opt-in surface for
/// open-generic registrations. v1 exposes only PreserveReferences; future versions may add per-template
/// configuration (e.g. ConvertUsing for open generics — currently a v3 follow-up).
/// </summary>
public interface IOpenGenericMappingExpression
{
    /// <summary>
    /// Marks the open-generic template as cycle-safe. Every closed-pair materialization derived
    /// from this template will inherit <see cref="Internal.TypeMap.PreserveReferences"/> = true.
    /// See docs/Atlas-Design-ReferenceHandling.md §7.7.
    /// </summary>
    /// <returns>This expression, for fluent chaining.</returns>
    IOpenGenericMappingExpression PreserveReferences();
}
```

- [ ] **Step 6.5: Create `OpenGenericMappingExpression.cs`**

```csharp
namespace Atlas.Configuration;

using Atlas.Internal;

/// <summary>
/// Concrete fluent expression wrapping an <see cref="OpenGenericTypeMap"/>. Implements
/// <see cref="IOpenGenericMappingExpression"/>. Returned by
/// <see cref="MapperConfigurationExpression.CreateMap(Type, Type, MemberList)"/> and the matching
/// <see cref="MapperProfile"/> overload.
/// </summary>
internal sealed class OpenGenericMappingExpression : IOpenGenericMappingExpression
{
    private readonly OpenGenericTypeMap _template;

    internal OpenGenericMappingExpression(OpenGenericTypeMap template)
    {
        _template = template;
    }

    public IOpenGenericMappingExpression PreserveReferences()
    {
        _template.PreserveReferences = true;
        return this;
    }
}
```

- [ ] **Step 6.6: Update `MapperConfigurationExpression.CreateMap(Type, Type, MemberList)` to return `IOpenGenericMappingExpression`**

In `C:\Repos\Atlas\src\Atlas\Configuration\MapperConfigurationExpression.cs` (around line 70 per structural inventory), change the signature:

```csharp
// Before:
public void CreateMap(Type sourceType, Type destinationType, MemberList memberList = MemberList.None)
{
    // ... validation, creates OpenGenericTypeMap, adds to _openGenericMaps ...
}

// After:
public IOpenGenericMappingExpression CreateMap(Type sourceType, Type destinationType, MemberList memberList = MemberList.None)
{
    // ... existing validation ...
    var template = new OpenGenericTypeMap(sourceType, destinationType, memberList) { ... };
    _openGenericMaps.Add(template);
    return new OpenGenericMappingExpression(template);
}
```

(Adapt the body to match the actual existing implementation — preserve all existing validation rules and registration logic; just add the return.)

- [ ] **Step 6.7: Update `MapperProfile.CreateMap(Type, Type, MemberList)` similarly**

Same change in `C:\Repos\Atlas\src\Atlas\MapperProfile.cs`. Returns `IOpenGenericMappingExpression`.

- [ ] **Step 6.8: Wire propagation in `MapperRegistry.MaterializeClosed`**

In `C:\Repos\Atlas\src\Atlas\Internal\MapperRegistry.cs` (around line 147–167), after the closed `TypeMap` is constructed and its existing fields populated:

```csharp
private TypeMap MaterializeClosed(OpenGenericTypeMap template, TypePair closedPair)
{
    var tm = new TypeMap(closedPair.Source, closedPair.Destination, template.MemberList)
    {
        OriginatingProfile = template.OriginatingProfile,
        RegistrationOrigin = $"{template.RegistrationOrigin} " +
                             $"(closed at runtime as ({closedPair.Source.Name}, {closedPair.Destination.Name}))",
        PreserveReferences = template.PreserveReferences,   // NEW — propagate from template
    };
    // ... existing convention/transformer resolution, Seal() ...
    return tm;
}
```

- [ ] **Step 6.9: Wire propagation in `MappingExpression.ReverseMap`**

In `C:\Repos\Atlas\src\Atlas\Configuration\MappingExpression.cs` (around line 149–154), update the reverse-pair construction:

```csharp
// Before:
var reverseTm = new TypeMap(typeof(TDestination), typeof(TSource), memberList)
{
    ReverseMapPair = TypeMap.Pair,
    RegistrationOrigin = $"CreateMap<{typeof(TSource).Name}, {typeof(TDestination).Name}>().ReverseMap()",
    OriginatingProfile = TypeMap.OriginatingProfile,
};

// After:
var reverseTm = new TypeMap(typeof(TDestination), typeof(TSource), memberList)
{
    ReverseMapPair = TypeMap.Pair,
    RegistrationOrigin = $"CreateMap<{typeof(TSource).Name}, {typeof(TDestination).Name}>().ReverseMap()",
    OriginatingProfile = TypeMap.OriginatingProfile,
    PreserveReferences = TypeMap.PreserveReferences,   // NEW — propagate to reverse pair
};
```

- [ ] **Step 6.10: Wire propagation in `InheritanceMerger`**

In `C:\Repos\Atlas\src\Atlas\Internal\InheritanceMerger.cs`, find the TypeMap-level merging logic (where `BeforeHooks` and `AfterHooks` are merged base→derived around lines 43–51 per structural inventory). Add propagation for `PreserveReferences`:

```csharp
// Where the existing per-TypeMap merge happens (e.g. inside MergeBaseConfig or similar method):

// Existing:
foreach (var hook in baseTm.BeforeHooks)
    derivedTm.BeforeHooks.Insert(0, hook);
// ... after-hooks merge ...

// NEW: propagate PreserveReferences from base to derived (OR semantics — once set, stays set)
if (baseTm.PreserveReferences)
    derivedTm.PreserveReferences = true;
```

(Adapt to match the actual structure. The intent: if the base has PR, the derived gets PR. If the derived already had PR, it stays PR.)

- [ ] **Step 6.11: Run new tests — verify they pass**

```pwsh
dotnet test tests/Atlas.Tests/Atlas.Tests.csproj --filter FullyQualifiedName~MapperPreserveReferencesPropagationTests --nologo
```

Expected: `Passed: 9, Failed: 0`.

- [ ] **Step 6.12: Run full suite — verify no regression**

```pwsh
dotnet test --nologo 2>&1 | Select-String -Pattern "Passed:|Failed:"
```

Expected: total Passed = 604 + 9 = 613; Failed: 0.

- [ ] **Step 6.13: Commit**

```pwsh
git add src/Atlas/Internal/InheritanceMerger.cs src/Atlas/Configuration/MappingExpression.cs src/Atlas/Internal/OpenGenericTypeMap.cs src/Atlas/Configuration/IOpenGenericMappingExpression.cs src/Atlas/Configuration/OpenGenericMappingExpression.cs src/Atlas/Configuration/MapperConfigurationExpression.cs src/Atlas/MapperProfile.cs src/Atlas/Internal/MapperRegistry.cs tests/Atlas.Tests/MapperPreserveReferencesPropagationTests.cs
git commit -m "Propagation: Inheritance + ReverseMap + OpenGenerics + 9 tests (Task 6)"
```

---

## Task 7 — Update-in-place tests + verification

**Goal:** Add the update-in-place test suite. The codegen for update-in-place was completed in Task 5 (`BuildUpdate`'s cache preamble); this task adds the test coverage.

**Files:**
- Create: `C:\Repos\Atlas\tests\Atlas.Tests\MapperPreserveReferencesUpdateInPlaceTests.cs` — 5 tests.

**Allowlist for the implementer subagent:** the one file above only.

- [ ] **Step 7.1: Write `MapperPreserveReferencesUpdateInPlaceTests.cs`**

```csharp
namespace Atlas.Tests;

using Atlas;

public class MapperPreserveReferencesUpdateInPlaceTests
{
    [Fact]
    public void UpdateInPlace_FreshDestination_CycleResolvedToExisting()
    {
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>().PreserveReferences()).CreateMapper();
        var existingDto = new PersonDto();
        var alice = new Person { Name = "Alice" };
        alice.Boss = alice;

        mapper.Map(alice, existingDto);

        Assert.Equal("Alice", existingDto.Name);
        Assert.Same(existingDto, existingDto.Boss);
    }

    [Fact]
    public void UpdateInPlace_PreservesNonMappedFields()
    {
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>().PreserveReferences()).CreateMapper();
        var existingDto = new PersonDto { LocalNote = "do not overwrite" };
        var alice = new Person { Name = "Alice" };

        mapper.Map(alice, existingDto);

        Assert.Equal("Alice", existingDto.Name);
        Assert.Equal("do not overwrite", existingDto.LocalNote);
    }

    [Fact]
    public void UpdateInPlace_NoCycle_BehavesLikeFreshMap()
    {
        // Without a cycle, update-in-place should set fields normally.
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>().PreserveReferences()).CreateMapper();
        var existingDto = new PersonDto();
        var alice = new Person { Name = "Alice", Age = 30 };

        mapper.Map(alice, existingDto);

        Assert.Equal("Alice", existingDto.Name);
        Assert.Equal(30, existingDto.Age);
    }

    [Fact]
    public void UpdateInPlace_AcrossDifferentDestinationTypes_SeparateContextPerCall()
    {
        // Each top-level Map call allocates its own MappingContext, so two calls with the same
        // source instance and different destination types don't interfere.
        var mapper = new MapperConfiguration(c =>
        {
            c.CreateMap<Person, PersonDto>().PreserveReferences();
            c.CreateMap<Person, PersonSummary>().PreserveReferences();
        }).CreateMapper();
        var alice = new Person { Name = "Alice" };
        alice.Boss = alice;

        var dto = new PersonDto();
        var summary = new PersonSummary();
        mapper.Map(alice, dto);
        mapper.Map(alice, summary);

        Assert.Same(dto, dto.Boss);
        Assert.Equal("Alice", summary.Name);
    }

    [Fact]
    public void UpdateInPlace_WithoutPreserveReferences_NormalCycleFails()
    {
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>()).CreateMapper();   // NO .PreserveReferences()
        var existingDto = new PersonDto();
        var alice = new Person { Name = "Alice" };
        alice.Boss = alice;

        Assert.ThrowsAny<Exception>(() => mapper.Map(alice, existingDto));
    }

    private sealed class Person
    {
        public string? Name { get; set; }
        public int Age { get; set; }
        public Person? Boss { get; set; }
    }

    private sealed class PersonDto
    {
        public string? Name { get; set; }
        public int Age { get; set; }
        public PersonDto? Boss { get; set; }
        public string? LocalNote { get; set; }
    }

    private sealed class PersonSummary
    {
        public string? Name { get; set; }
    }
}
```

- [ ] **Step 7.2: Run tests — verify they pass (Task 5's BuildUpdate codegen handles this)**

```pwsh
dotnet test tests/Atlas.Tests/Atlas.Tests.csproj --filter FullyQualifiedName~MapperPreserveReferencesUpdateInPlaceTests --nologo
```

Expected: `Passed: 5, Failed: 0`. **If any test fails**, revisit Task 5's `BuildUpdate` cache preamble — likely the `existingDest` is not being registered into the cache before population.

- [ ] **Step 7.3: Run full suite — verify no regression**

Expected: total Passed = 613 + 5 = 618; Failed: 0.

- [ ] **Step 7.4: Commit**

```pwsh
git add tests/Atlas.Tests/MapperPreserveReferencesUpdateInPlaceTests.cs
git commit -m "Update-in-place tests for PreserveReferences (Task 7)"
```

---

## Task 8 — Validator rule: `PreserveReferences + ConvertUsing` rejected

**Goal:** Add the `ValidatePreserveReferences` rule that rejects the `PreserveReferences + ConvertUsing` combination at config time with a clear `AtlasConfigurationException`.

**Files:**
- Modify: `C:\Repos\Atlas\src\Atlas\Internal\ConfigurationValidator.cs` — add `ValidatePreserveReferences` method; call it from per-typemap loop.
- Create: `C:\Repos\Atlas\tests\Atlas.Tests\ConfigurationValidatorPreserveReferencesTests.cs` — 3 tests.

**Allowlist for the implementer subagent:** the two files above only.

- [ ] **Step 8.1: Write tests (failing)**

```csharp
namespace Atlas.Tests;

using Atlas;

public class ConfigurationValidatorPreserveReferencesTests
{
    [Fact]
    public void AssertConfigurationIsValid_PreserveReferencesOnly_Passes()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>().PreserveReferences());

        cfg.AssertConfigurationIsValid();   // no exception
    }

    [Fact]
    public void AssertConfigurationIsValid_PreserveReferencesPlusConvertUsing_ThrowsAtlasConfigurationException()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>()
                .PreserveReferences()
                .ConvertUsing(src => new PersonDto { Name = src.Name }));

        var ex = Assert.Throws<AtlasConfigurationException>(() => cfg.AssertConfigurationIsValid());
        Assert.Contains("PreserveReferences", ex.Message);
        Assert.Contains("ConvertUsing", ex.Message);
    }

    [Fact]
    public void AssertConfigurationIsValid_PreserveReferencesPlusOtherFeatures_Passes()
    {
        // Verifies PR + Hooks + AddTransform + Condition + NullSubstitute is allowed.
        // Only ConvertUsing is the conflict.
        var cfg = new MapperConfiguration(c =>
        {
            c.ValueTransformers.Add<string>(s => s == null ? null! : s.Trim());
            c.CreateMap<Person, PersonDto>()
                .PreserveReferences()
                .BeforeMap((s, d) => { })
                .AfterMap((s, d) => { });
        });

        cfg.AssertConfigurationIsValid();   // no exception
    }

    private sealed class Person
    {
        public string? Name { get; set; }
    }

    private sealed class PersonDto
    {
        public string? Name { get; set; }
    }
}
```

- [ ] **Step 8.2: Run tests — verify they fail**

The third test should pass; the second should fail (no validation rule yet).

- [ ] **Step 8.3: Add `ValidatePreserveReferences` method to `ConfigurationValidator.cs`**

```csharp
private static void ValidatePreserveReferences(TypeMap tm, List<ConfigurationError> errors)
{
    if (!tm.PreserveReferences) return;

    if (tm.CustomConverter is not null)
    {
        errors.Add(new ConfigurationError(
            tm.SourceType, tm.DestinationType,
            $"TypeMap {tm.SourceType.Name} → {tm.DestinationType.Name} has both PreserveReferences " +
            $"and ConvertUsing. These are incompatible: ConvertUsing replaces the mapping body, " +
            "leaving no member-emit pipeline for the cycle-cache to wrap. Remove one of the two " +
            "registrations. (Atlas v2 #11 — see docs/Atlas-Design-ReferenceHandling.md §6.1.)"));
    }
}
```

(Adapt the `ConfigurationError` shape to match the actual existing type — inspect adjacent validation methods like `ValidateNullSubstitutes` for the exact constructor signature.)

- [ ] **Step 8.4: Wire the call from `Validate`'s per-typemap loop**

In the per-typemap loop in `Validate` (around line 19–40), add:

```csharp
foreach (var tm in registry.AllTypeMaps)
{
    if (tm.IsDynamic) continue;     // existing — Atlas v2 #10

    ValidateEnum(tm, errors);
    ValidatePaths(tm, errors);
    ValidateHooks(tm, ...);
    ValidateNullSubstitutes(tm, errors);
    ValidatePreserveReferences(tm, errors);   // NEW — Atlas v2 #11

    if (enumValidationEnabled)
        ValidateEnumStrict(tm, errors);

    ValidateInheritance(tm, registry, errors);

    // ... MemberList mode dispatch
}
```

- [ ] **Step 8.5: Run new tests — verify they pass**

```pwsh
dotnet test tests/Atlas.Tests/Atlas.Tests.csproj --filter FullyQualifiedName~ConfigurationValidatorPreserveReferencesTests --nologo
```

Expected: `Passed: 3, Failed: 0`.

- [ ] **Step 8.6: Run full suite — verify no regression**

Expected: total Passed = 618 + 3 = 621; Failed: 0.

- [ ] **Step 8.7: Commit**

```pwsh
git add src/Atlas/Internal/ConfigurationValidator.cs tests/Atlas.Tests/ConfigurationValidatorPreserveReferencesTests.cs
git commit -m "Validator rule: PreserveReferences + ConvertUsing rejected at config time (Task 8)"
```

---

## Task 9 — Atlas.Projections rejection (dual-gate)

**Goal:** Wire dual-gate rejection in `Atlas.Projections`: `ProjectionCompatibility.IsTypeMapProjectable` returns false for PR typemaps; `ProjectionPlanBuilder.RejectPreserveReferencesOrThrow` is the runtime backstop.

**Files:**
- Modify: `C:\Repos\Atlas\src\Atlas.Projections\Internal\ProjectionCompatibility.cs` — add `if (tm.PreserveReferences)` block.
- Modify: `C:\Repos\Atlas\src\Atlas.Projections\Internal\ProjectionPlanBuilder.cs` — add `RejectPreserveReferencesOrThrow` method + call from `BuildBody`.
- Create: `C:\Repos\Atlas\tests\Atlas.Projections.Tests\ProjectionRejectsPreserveReferencesTests.cs` — 2 tests.

**Allowlist for the implementer subagent:** the three files above only.

- [ ] **Step 9.1: Write tests (failing)**

```csharp
namespace Atlas.Projections.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using Atlas;
using Atlas.Projections;

public class ProjectionRejectsPreserveReferencesTests
{
    [Fact]
    public void ProjectTo_PreserveReferencesTypeMap_ThrowsAtlasProjectionException()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<Source, Target>().PreserveReferences());
        var queryable = new List<Source> { new() { Id = 1 } }.AsQueryable();

        var ex = Assert.Throws<AtlasProjectionException>(
            () => queryable.ProjectTo<Target>(cfg).ToList());

        Assert.True(
            ex.Message.Contains("PreserveReferences", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("identity tracking", StringComparison.OrdinalIgnoreCase),
            $"Expected message to mention PreserveReferences or identity tracking; got: {ex.Message}");
    }

    [Fact]
    public void ProjectTo_NonPreserveReferencesMap_StillWorks()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<Source, Target>());   // NOT flagged
        var queryable = new List<Source> { new() { Id = 1 } }.AsQueryable();

        var result = queryable.ProjectTo<Target>(cfg).ToList();

        Assert.Single(result);
        Assert.Equal(1, result[0].Id);
    }

    private sealed class Source { public int Id { get; set; } }
    private sealed class Target { public int Id { get; set; } }
}
```

- [ ] **Step 9.2: Run tests — verify they fail (rejection not yet wired)**

- [ ] **Step 9.3: Add rejection block to `ProjectionCompatibility.IsTypeMapProjectable`**

In `C:\Repos\Atlas\src\Atlas.Projections\Internal\ProjectionCompatibility.cs` (around line 13–35), add the block AFTER the existing `IsDynamic` check (around line 19):

```csharp
if (tm.PreserveReferences)   // NEW — Atlas v2 #11
{
    reason = "PreserveReferences is not projectable — LINQ providers cannot model identity tracking";
    return false;
}
```

- [ ] **Step 9.4: Add `RejectPreserveReferencesOrThrow` to `ProjectionPlanBuilder.cs`**

In `C:\Repos\Atlas\src\Atlas.Projections\Internal\ProjectionPlanBuilder.cs`, after the existing `RejectDynamicOrThrow` method (around line 339–349):

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

In `BuildBody` (around line 27–28), add the call:

```csharp
RejectHooksOrThrow(tm);
RejectDynamicOrThrow(tm);
RejectPreserveReferencesOrThrow(tm);   // NEW
```

- [ ] **Step 9.5: Run new tests — verify they pass**

```pwsh
dotnet test --nologo --filter FullyQualifiedName~ProjectionRejectsPreserveReferencesTests
```

Expected: `Passed: 2, Failed: 0`.

- [ ] **Step 9.6: Run full suite — verify no regression**

Expected: total Passed = 621 + 2 = 623; Failed: 0.

- [ ] **Step 9.7: Commit**

```pwsh
git add src/Atlas.Projections/Internal/ProjectionCompatibility.cs src/Atlas.Projections/Internal/ProjectionPlanBuilder.cs tests/Atlas.Projections.Tests/ProjectionRejectsPreserveReferencesTests.cs
git commit -m "Atlas.Projections rejects PreserveReferences typemaps (dual-gate) (Task 9)"
```

---

## Task 10 — Integration tests (NameComparison no-op, threading, OFF-path no-allocation)

**Goal:** Cover remaining behavior surface in one new test file. Tests-only task; NO production-code changes.

**Files:**
- Create: `C:\Repos\Atlas\tests\Atlas.Tests\MapperPreserveReferencesIntegrationTests.cs` — 10 tests.

**Allowlist for the implementer subagent:** the one file above only.

- [ ] **Step 10.1: Write the integration test file**

```csharp
namespace Atlas.Tests;

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Atlas;

public class MapperPreserveReferencesIntegrationTests
{
    [Fact]
    public async Task ConcurrentTopLevelCalls_DoNotShareContext()
    {
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>().PreserveReferences()).CreateMapper();

        var tasks = Enumerable.Range(0, 16).Select(i => Task.Run(() =>
        {
            var p = new Person { Name = $"user{i}" };
            p.Boss = p;
            return mapper.Map<PersonDto>(p);
        })).ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.Equal(16, results.Length);
        for (int i = 0; i < 16; i++)
        {
            Assert.Equal($"user{i}", results[i].Name);
            Assert.Same(results[i], results[i].Boss);
        }
    }

    [Fact]
    public void DeepGraphWithoutCycles_PreservesReferencesIsCorrect_NoIdentityChange()
    {
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>().PreserveReferences()).CreateMapper();

        var c = new Person { Name = "C" };
        var b = new Person { Name = "B", Boss = c };
        var a = new Person { Name = "A", Boss = b };

        var dto = mapper.Map<PersonDto>(a);

        Assert.Equal("A", dto.Name);
        Assert.Equal("B", dto.Boss!.Name);
        Assert.Equal("C", dto.Boss.Boss!.Name);
        Assert.Null(dto.Boss.Boss.Boss);
    }

    [Fact]
    public void Cycle_AcrossThreeWayMutualReference_AllResolved()
    {
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>().PreserveReferences()).CreateMapper();

        var a = new Person { Name = "A" };
        var b = new Person { Name = "B" };
        var c = new Person { Name = "C" };
        a.Friends = new List<Person> { b, c };
        b.Friends = new List<Person> { a, c };
        c.Friends = new List<Person> { a, b };

        var aDto = mapper.Map<PersonDto>(a);

        Assert.Same(aDto, aDto.Friends![0].Friends![0]);  // a → b → a
        Assert.Same(aDto.Friends[1], aDto.Friends[0].Friends[1]);  // b.Friends[1] == c == a.Friends[1]
    }

    [Fact]
    public void BeforeMapHook_ObservesSourceCorrectly_FiresOnceForCyclicSource()
    {
        var observed = new List<string>();
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>()
                .PreserveReferences()
                .BeforeMap((s, d) => observed.Add(s.Name!))).CreateMapper();

        var alice = new Person { Name = "Alice" };
        alice.Boss = alice;

        mapper.Map<PersonDto>(alice);

        // alice appears twice in graph (top-level + as Boss); BeforeMap fires once.
        Assert.Single(observed);
        Assert.Equal("Alice", observed[0]);
    }

    [Fact]
    public void AfterMapHook_FiresOnceForCyclicSource_AfterDestinationFullyPopulated()
    {
        var observed = new List<(string srcName, string? dstName)>();
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>()
                .PreserveReferences()
                .AfterMap((s, d) => observed.Add((s.Name!, d.Name)))).CreateMapper();

        var alice = new Person { Name = "Alice" };
        alice.Boss = alice;

        mapper.Map<PersonDto>(alice);

        Assert.Single(observed);
        Assert.Equal("Alice", observed[0].srcName);
        Assert.Equal("Alice", observed[0].dstName);
    }

    [Fact]
    public void NestedCollectionMap_SharedElementInTwoLists_DedupedAcrossLists()
    {
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Container, ContainerDto>().PreserveReferences()).CreateMapper();

        var alice = new Person { Name = "Alice" };
        var src = new Container
        {
            ListA = new List<Person> { alice },
            ListB = new List<Person> { alice }
        };

        var dto = mapper.Map<ContainerDto>(src);

        Assert.Same(dto.ListA![0], dto.ListB![0]);
    }

    [Fact]
    public void NoCycle_NoPreserveReferences_PerformanceParityWithV1()
    {
        // Sanity: a non-cyclic graph maps correctly without PR; verifies the universal-parameter
        // change in Task 3 didn't break anything.
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>()).CreateMapper();
        var p = new Person { Name = "Alice" };

        var dto = mapper.Map<PersonDto>(p);

        Assert.Equal("Alice", dto.Name);
    }

    [Fact]
    public void Map_IntoExistingNestedDestination_CycleResolvedToOuterExisting()
    {
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>().PreserveReferences()).CreateMapper();
        var existing = new PersonDto { LocalNote = "preserved" };
        var alice = new Person { Name = "Alice" };
        alice.Boss = alice;

        mapper.Map(alice, existing);

        Assert.Equal("Alice", existing.Name);
        Assert.Same(existing, existing.Boss);
        Assert.Equal("preserved", existing.LocalNote);
    }

    [Fact]
    public void OffPath_AcrossManyMapCalls_DoesNotAccumulateState()
    {
        // Sanity: many calls without PR don't accumulate any state (no cache leaks).
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<Person, PersonDto>()).CreateMapper();

        for (int i = 0; i < 100; i++)
        {
            var p = new Person { Name = $"user{i}" };
            var dto = mapper.Map<PersonDto>(p);
            Assert.Equal($"user{i}", dto.Name);
        }
    }

    [Fact]
    public void MixedCycleAndSharedReference_BothResolvedCorrectly()
    {
        var mapper = new MapperConfiguration(c =>
        {
            c.CreateMap<Department, DepartmentDto>().PreserveReferences();
            c.CreateMap<Employee, EmployeeDto>();
        }).CreateMapper();

        var dept = new Department { Name = "Engineering" };
        var alice = new Employee { Name = "Alice", Department = dept };
        var bob = new Employee { Name = "Bob", Department = dept };
        alice.Manager = bob;
        bob.Manager = alice;
        dept.Employees = new List<Employee> { alice, bob };

        var dto = mapper.Map<DepartmentDto>(dept);

        // Shared department
        Assert.Same(dto, dto.Employees![0].Department);
        Assert.Same(dto, dto.Employees[1].Department);

        // Mutual cycle resolved
        Assert.Same(dto.Employees[0], dto.Employees[1].Manager);
        Assert.Same(dto.Employees[1], dto.Employees[0].Manager);
    }

    private sealed class Person
    {
        public string? Name { get; set; }
        public Person? Boss { get; set; }
        public List<Person>? Friends { get; set; }
    }

    private sealed class PersonDto
    {
        public string? Name { get; set; }
        public PersonDto? Boss { get; set; }
        public List<PersonDto>? Friends { get; set; }
        public string? LocalNote { get; set; }
    }

    private sealed class Container
    {
        public List<Person>? ListA { get; set; }
        public List<Person>? ListB { get; set; }
    }

    private sealed class ContainerDto
    {
        public List<PersonDto>? ListA { get; set; }
        public List<PersonDto>? ListB { get; set; }
    }

    private sealed class Department
    {
        public string? Name { get; set; }
        public List<Employee>? Employees { get; set; }
    }

    private sealed class DepartmentDto
    {
        public string? Name { get; set; }
        public List<EmployeeDto>? Employees { get; set; }
    }

    private sealed class Employee
    {
        public string? Name { get; set; }
        public Department? Department { get; set; }
        public Employee? Manager { get; set; }
    }

    private sealed class EmployeeDto
    {
        public string? Name { get; set; }
        public DepartmentDto? Department { get; set; }
        public EmployeeDto? Manager { get; set; }
    }
}
```

- [ ] **Step 10.2: Run new tests — verify they pass**

```pwsh
dotnet test tests/Atlas.Tests/Atlas.Tests.csproj --filter FullyQualifiedName~MapperPreserveReferencesIntegrationTests --nologo
```

Expected: `Passed: 10, Failed: 0`. **If any test fails revealing a production issue, mark it `[Fact(Skip = "...")]` and report DONE_WITH_CONCERNS — DO NOT touch production code.**

- [ ] **Step 10.3: Run full suite — verify no regression**

Expected: total Passed = 623 + 10 = 633; Failed: 0.

- [ ] **Step 10.4: Commit**

```pwsh
git add tests/Atlas.Tests/MapperPreserveReferencesIntegrationTests.cs
git commit -m "Integration tests: threading, hooks-fire-once, off-path, mixed cycles (Task 10)"
```

---

## Task 11 — README + final coverage check

**Goal:** Add the Reference Handling section to the README; remove #11 from the deferred list. No production-code changes.

**Files:**
- Modify: `C:\Repos\Atlas\README.md`

**Allowlist for the implementer subagent:** the one file above only. NO production code changes.

- [ ] **Step 11.1: Inspect the current README**

Find the "Dynamic / dictionary mapping" section (added in PR #10). Identify where it ends — that's where the new "Reference handling for cycles" section goes.

Find the deferred-features list. Find the entry for #11 (Reference handling for cycles) — should be marked as "next up" or similar.

- [ ] **Step 11.2: Insert the new section AFTER "Dynamic / dictionary mapping" and BEFORE the deferred-features list**

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

(Match the existing README's markdown style — code-fence triple-backticks with `csharp` language tag.)

- [ ] **Step 11.3: Remove #11 from the deferred-features list**

Mark it as shipped or remove it, matching the convention used for prior shipped features (#1–#10).

- [ ] **Step 11.4: Run full suite — sanity check**

```pwsh
dotnet test --nologo 2>&1 | Select-String -Pattern "Passed:|Failed:"
```

Expected: 633 PASS, 0 FAIL. (No production code changed; this is a sanity check.)

- [ ] **Step 11.5: Verify diff**

```pwsh
git diff --stat
```

Expected: ONLY `README.md` modified.

- [ ] **Step 11.6: Verify coverage**

```pwsh
dotnet test --nologo --collect:"XPlat Code Coverage" --settings tests/coverlet.runsettings 2>&1
```

(Use whatever the project's existing coverage command is — check `Directory.Build.props` and existing CI scripts at implementation time.)

Expected: line coverage ≥ 90%, branch coverage ≥ 80% on `Atlas` and `Atlas.Projections` assemblies. Specifically check the new files: `MappingContext.cs`, `OpenGenericMappingExpression.cs`, `ExecutionPlanBuilder.cs` cache-preamble emission, `MappingInvoker.cs` signature changes, `ConfigurationValidator.cs` new rule, `ProjectionPlanBuilder.cs` new rejection method.

- [ ] **Step 11.7: Commit**

```pwsh
git add README.md
git commit -m "docs: README — add reference handling section, remove from deferred list (Task 11)"
```

---

## Final review (controller, before opening the PR)

After all 12 tasks (0–11) are complete and committed:

- [ ] **Verify branch state**

```pwsh
git log --oneline main..HEAD
```

Expected: 11+ commits (one per task, plus any review fix-ups). Verify each commit message names its task.

- [ ] **Run full test suite, full pass**

```pwsh
dotnet test --nologo 2>&1 | Select-String -Pattern "Passed:|Failed:|Skipped:"
```

Expected: `Passed: 633, Failed: 0, Skipped: 0` (or similar; record the exact count for the PR body).

- [ ] **Dispatch holistic code review**

Use `superpowers:code-reviewer` against the entire branch's diff. Per memory feedback (`feedback_atlas_v2_workflow.md`): the holistic review catches cross-task and whole-feature concerns even when per-task reviews pass cleanly. **Don't skip this step.**

Specific holistic-review focus areas for #11:
1. **Cross-package consumer audit (Bug-4 lesson):** verify every reader of `TypeMap.PreserveReferences` is enumerated and behaves correctly. Grep targets: `PreserveReferences` usages across `src/`. Each consumer either acts on it (Mapper.Map, ExecutionPlanBuilder, validator, projection rejection, propagation sites) or correctly ignores it.
2. **Scope-identifying metadata propagation (Bug-5 lesson):** verify the three propagation sites (`InheritanceMerger`, `ReverseMap`, `MaterializeClosed`) are all wired and tested.
3. **Coalesce/Nullable interaction (Bug-6 lesson):** not directly applicable here, but verify the cache preamble's `Expression.Convert(srcParam, typeof(object))` boxing doesn't introduce any nullable-related foot-guns.
4. **Multi-stage routing claims (Bug-7 lesson):** the propagation rule "context allocated iff top-level typemap is flagged, threaded uniformly to nested calls" is single-stage and explicit. Verify the `ctx is null` check appears in every nested call site that the propagation rule applies to.
5. **Test deviation scrutiny:** if any implementer's self-review notes a test deviation from the plan's prescribed assertion, trace through why before accepting.
6. **Wide signature change correctness:** verify EVERY emit site that calls `MappingInvoker.*` was updated. Grep `MappingInvoker.Invoke` and `MappingInvoker.InvokeUpdate` and the rest across `src/` to confirm no missed sites.
7. **OFF-path performance:** verify the OFF path doesn't allocate `MappingContext` (`mapper.Map<TDst>(src)` with no `PreserveReferences` flag). Inspect `Mapper.Map`'s allocation gate.

- [ ] **Address any holistic-review findings**

Per memory: "Final-review minor follow-ups (README cleanup, validator gaps surfaced during the holistic review) get folded into the feature PR before merge — not deferred to a separate cleanup commit." Design-doc fixes go on `main` post-merge.

- [ ] **Push branch, open PR**

```pwsh
git push -u origin feat/reference-handling
gh pr create --base main --title "feat: reference handling for cycles (#11)" --body @"
Implements Atlas v2 deferred feature #11. See docs/Atlas-Design-ReferenceHandling.md for the full specification.

## Summary

Opt-in cycle-safe and shared-reference-preserving mapping via cfg.CreateMap<TSrc, TDst>().PreserveReferences(). Default OFF; per-typemap fluent activation; pre-population cache breaks cycles and dedupes shared references. Universal MappingContext? parameter on every compiled lambda signature. Mirrors Hooks #5 / DynamicMapping #10 projection-rejection pattern (dual-gate).

## Test count

- Baseline (post-#10): 575 PASS
- This PR: <ACTUAL_COUNT> PASS (≈ +58 net)

## Files changed

- 3 new production files (MappingContext.cs, IOpenGenericMappingExpression.cs, OpenGenericMappingExpression.cs)
- ~12 modified production files (TypeMap, IMappingExpression, MappingExpression, MapperConfigurationExpression, MapperProfile, MappingInvoker, Mapper, ExecutionPlanBuilder, DynamicPlanBuilder, MapperRegistry, OpenGenericTypeMap, InheritanceMerger, ConfigurationValidator, ProjectionPlanBuilder, ProjectionCompatibility)
- 7 new test files (~58 net new test methods)
- 1 modified doc (README.md)

## Holistic review

Clean (or list any findings folded back into this PR before merge).

🤖 Generated with [Claude Code](https://claude.com/claude-code)
"@
```

---

## Summary

**Total tasks:** 12 (Task 0 through Task 11) plus final review.

**Total checkboxes:** ~85 across all task steps.

**Estimated wall-clock (per memory's `~6 hours per feature` baseline):** 5–7 hours for subagent-driven execution. Major uncertainty on Task 3 (signature change) and Task 5 (cache preamble codegen) — these are the foundation-laying tasks that subsequent tasks build on; if either misfires, downstream tasks may need rework.

**Model selection guidance** (per `superpowers:subagent-driven-development` model-selection table):

| Task | Suggested model | Rationale |
|---|---|---|
| 0 | controller-only | branch setup |
| 1 | haiku | mechanical: a class + 6 trivial unit tests |
| 2 | haiku | mechanical: 1 field + 1 fluent method + 3 tests |
| 3 | sonnet | wide-but-mechanical: every Invoke* site, every emit site |
| 4 | sonnet | integration: IMapper allocates context, threads through (3 overloads incl. reflection-dispatch) |
| 5 | sonnet | algorithm-heavy: codegen Expression-tree emit + 20 end-to-end tests |
| 6 | sonnet | algorithm-heavy: 3 propagation sites + new fluent surface for open generics |
| 7 | haiku | tests-only (5 update-in-place tests; codegen done in Task 5) |
| 8 | haiku | mechanical: validator rule + 3 tests |
| 9 | haiku | mechanical: dual-gate rejection + 2 tests |
| 10 | haiku | tests-only |
| 11 | haiku | docs-only |

(Cross-task review: spec reviewer + code-quality reviewer per task per memory's review-catch frequency baseline.)

---

## Implementation notes

### Cross-task signature-change discipline (Task 3 → Task 4 → Task 5)

Task 3 changes every signature but threads `null` for `ctx` everywhere — nothing yet allocates a `MappingContext`. After Task 3, all 584 existing tests still pass.

Task 4 introduces the `MappingContext` allocation in `IMapper.Map` based on `tm.PreserveReferences`. After Task 4, the context flows through but the cache preamble doesn't yet exist — no behavioral change for tests yet.

Task 5 emits the cache preamble. After Task 5, cycle-safety works end-to-end and the 20 cycle/shared-reference tests pass.

This three-step ordering is deliberate. Don't skip ahead — each step lands a complete, testable surface.

### No test-with-deferred-greening pattern in this plan

Unlike OpenGenerics (which had Task 3 → Task 4 deferred-greening), this plan's tests pass at the end of each task that introduces them. Task 5's tests pass after Task 5; Task 6's tests pass after Task 6; etc.

### Bug audit reminders

Apply Bug-4 / Bug-5 / Bug-6 / Bug-7 lesson rigor at each task with a "shared shape" change:

- **Task 2 (TypeMap.PreserveReferences field)** — Bug-4 lesson: grep every reader of `TypeMap` and verify each one is either (a) responsible for honoring the flag (Mapper.Map, ExecutionPlanBuilder, ConfigurationValidator, ProjectionCompatibility, ProjectionPlanBuilder, InheritanceMerger, ReverseMap, MaterializeClosed) or (b) correctly ignores it (e.g., codegen for non-PR maps just doesn't fire the cache preamble). Verified by spec reviewer.
- **Task 6 (propagation)** — Bug-5 lesson: three propagation sites (Inheritance, ReverseMap, MaterializeClosed) all explicitly write the field. Tests pin each individually.
- **Task 5 (codegen)** — Bug-6 lesson: not directly applicable (no Coalesce/Nullable interaction). The cache preamble's `Expression.Convert(srcParam, typeof(object))` is plain reference-type boxing, not nullable widening.
- **Task 5 (cache preamble) + Task 6 (propagation)** — Bug-7 lesson: the propagation rule is single-stage. The plan explicitly states what allocates the context and what threads it. No "naturally handles itself" claims.

### Test-deviation discipline

Per memory's "test deviation scrutiny" lesson: if an implementer's self-review notes a test deviation from the plan's prescribed assertion, the spec reviewer should TRACE THROUGH why before accepting. The implementer's report's most-load-bearing claim (file list, test count, key behavior) gets independently verified by the controller via `git`/`dotnet test` BEFORE handing off to the spec reviewer.

### Validate-before-trusting subagent reports

Per memory's "validate-before-trusting subagent reports" lesson: subagent reports are not facts. After every task commit, run `git show --stat <SHA>` and `dotnet test --nologo` and verify the claimed file list and test count match reality. The Task 10 catastrophic regression in the DynamicMapping feature (where an implementer deleted 700 lines of prior work and claimed "1 file changed") was caught by validating test count math. Apply the same vigilance here, especially after Tasks 3 and 5 (the largest changes).

### Allowlist boundaries — strictly enforced

Each task lists exactly which files the implementer subagent may touch. Files outside the allowlist are off-limits unless the implementer escalates via DONE_WITH_CONCERNS. The "common over-reach" pattern from prior features (ReverseMap, Hooks, DynamicMapping) shows ~3 cases per feature where implementers touched files outside the allowlist; spec reviewers caught and either accepted (genuine necessity) or reverted (unauthorized scope creep).

### Open-generic API surface change is a v1 commitment

Task 6 introduces `IOpenGenericMappingExpression` — a new public interface. This changes `MapperConfigurationExpression.CreateMap(Type, Type)` and `MapperProfile.CreateMap(Type, Type)` from `void` returns to `IOpenGenericMappingExpression` returns. This is a non-breaking API change (existing call sites that ignore the return are still valid C#) but adds a new public type to the surface area. Document in the README (Task 11) as part of the open-generic limitations: "Open generics now expose a fluent surface for `PreserveReferences()`; future versions may add more open-generic configuration."

---

## Test plan (categorized recap)

| File | Direction | Count | Subject |
|---|---|---|---|
| `MappingContextTests.cs` | unit | ~6 | TryGet/Register/RefEq semantics |
| `TypeMapPreserveReferencesFieldTests.cs` | unit | ~3 | Fluent flag plumbing |
| `MapperPreserveReferencesTests.cs` | E2E | ~20 | Cycle-breaking + shared-reference dedup + fresh-map + value-type sources |
| `MapperPreserveReferencesPropagationTests.cs` | integration | ~9 | Inheritance/ReverseMap/OpenGenerics propagation + down-propagation runtime semantics + hooks-fire-once |
| `MapperPreserveReferencesUpdateInPlaceTests.cs` | E2E | ~5 | Update-in-place + nested-existing semantics |
| `ConfigurationValidatorPreserveReferencesTests.cs` | validator | ~3 | PreserveReferences + ConvertUsing rejection |
| `MapperPreserveReferencesIntegrationTests.cs` | integration | ~10 | Threading, deep graphs, mixed cycles, off-path |
| `ProjectionRejectsPreserveReferencesTests.cs` | projection | ~2 | Projection rejection with AtlasProjectionException |
| **TOTAL** | | **~58** | |

Coverage targets: line ≥ 90%, branch ≥ 80% on the changed files. Has held on every prior feature.

---

## Implementer notes (per-task ground rules)

These rules apply to every implementer-subagent dispatched against this plan:

1. **Read the design first.** Open `C:\Repos\Atlas\docs\Atlas-Design-ReferenceHandling.md` and read at minimum the sections referenced in your task's description. The design is authoritative — when this plan disagrees with the design, follow the design.

2. **Stay inside the allowlist.** Each task lists exactly which files you may create or modify. **Files outside the allowlist are off-limits unless you escalate via DONE_WITH_CONCERNS.** Prior features documented multiple cases of unauthorized scope creep; spec reviewers will catch these but escalation is faster.

3. **Disclose test deviations.** If the planned test text doesn't match what the production code produces, **do NOT silently fix the test text**. Report DONE_WITH_CONCERNS naming the discrepancy. The discrepancy may be a real production bug (NullSubstitution Task 8 caught one this way; DynamicMapping Task 8 caught another).

4. **Use plain `Assert.X()` only.** No FluentAssertions per `feedback_no_fluentassertions` memory. xUnit v3's `Assert.NotNull`, `Assert.Equal`, `Assert.Same`, `Assert.IsType<T>`, `Assert.Throws<T>`, `Assert.Single`, `Assert.Contains`, `Assert.True`/`False`, `Assert.Null`, `Assert.All`, `Assert.NotSame`, `Assert.ThrowsAny<T>`, etc.

5. **Run tests at every step.** TDD discipline: write failing test, see it fail, write minimum implementation, see it pass, run full suite, commit.

6. **One commit per task.** Each task ends in a single commit. Don't squash; don't split.

7. **Match existing code style.** Follow the conventions in the file you're editing. Look at the existing code's brace style, naming, comment density, indentation. Atlas uses C# 14 preview, file-scoped namespaces, expression-bodied members where natural, XML doc-comments on public/internal types.

8. **Plan-arithmetic drift is fine.** If your task ends with a different test count than the plan predicts (off by 1-4), report the actual count and continue. Don't churn the plan doc to match.

9. **Verify Atlas APIs at first reference.** This plan references `IMapper.Map(object)`, `MapperConfigurationExpression.CreateMap(Type, Type)`, `OpenGenericTypeMap.PreserveReferences`, `MappingContext.TryGet/Register`, etc. — verify these exist with the exact signatures during implementation; adapt if they differ. The Explore agent's structural inventory has line numbers but you should re-verify before relying on a property name or method signature.

10. **Branch state checkpoint before re-dispatch.** If a task fails midway and the controller re-dispatches an implementer for the fix, the controller verifies branch state with `git log --oneline -5` first. If the latest commit isn't what the prior implementer reported, the prior commit may have been orphaned in detached-HEAD; check reflog.

11. **No GUID-shaped strings near "Token"-shaped property names in tests.** Per memory's GitGuardian false-positive lesson, avoid property names like `Token`/`ApiKey`/`Secret` in test fixtures, even with synthetic GUID values. Use innocuous names (`Identifier`, `RefId`, `Tag`) and clearly-synthetic values (e.g., `11111111-2222-3333-4444-555555555555`).
