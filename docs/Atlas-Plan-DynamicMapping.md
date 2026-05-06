# Atlas v2 Dynamic Mapping Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship Atlas v2 feature #10 — convention-only mapping between strongly-typed POCOs and three recognized dynamic shapes (`IDictionary<string, object>`, `ExpandoObject`, `Dictionary<string, object>`) with zero new fluent surface.

**Architecture:** Lazy materialization via single insertion point in `MapperRegistry.GetTypeMap` (stage 3, after closed-pair cache and open-generic template scan). Synthesized `PropertyMap[]` on a TypeMap with `IsDynamic = true`; new `DynamicKey` field on `PropertyMap` discriminates the dict-key from the (always-null on the dict side) `DestinationProperty`/`SourcePath`. Codegen forks at the top of `ExecutionPlanBuilder.Build` into a new `DynamicPlanBuilder`. Mirrors OpenGenerics' lazy-materialization architecture from PR #9.

**Tech Stack:** C# 14 preview, `System.Linq.Expressions`, `System.Dynamic.ExpandoObject`, `System.Collections.Concurrent.ConcurrentDictionary`, xUnit v3 (plain `Assert.X()` only — NO FluentAssertions per project convention).

**Branch:** `feat/dynamic-mapping`, cut from `main` HEAD `0d93436` (the design commit).

**Reference design:** `C:\Repos\Atlas\docs\Atlas-Design-DynamicMapping.md` — primary spec. All section references (e.g., "design §5.3") point at it.

---

## File Map

### New files (production)

- `C:\Repos\Atlas\src\Atlas\Internal\DynamicShape.cs` — `IsDynamicShape(Type)`, `IsDynamicPair(TypePair)`, `MaterializeTypeMap(...)` factory.
- `C:\Repos\Atlas\src\Atlas\Internal\DynamicPlanBuilder.cs` — `Build(TypeMap, MapperRegistry)`, `BuildDictToPocoLambda(TypeMap, MapperRegistry)`, `BuildPocoToDictLambda(TypeMap, MapperRegistry)` and the supporting `BuildUpdate*Lambda` overloads.

### Modified files (production)

- `C:\Repos\Atlas\src\Atlas\Internal\TypeMap.cs` — add `bool IsDynamic { get; init; }` field.
- `C:\Repos\Atlas\src\Atlas\Internal\PropertyMap.cs` — add `string? DynamicKey { get; set; }` field + 2 new static factories `ForDictKey(...)` and `ForPocoSource(...)`.
- `C:\Repos\Atlas\src\Atlas\Internal\MapperRegistry.cs` — extend `GetTypeMap` with stage 3 (dynamic-shape detection).
- `C:\Repos\Atlas\src\Atlas\Internal\ExecutionPlanBuilder.cs` — add `if (typeMap.IsDynamic) return DynamicPlanBuilder.Build(...)` branch at the top of `Build`/`BuildBaseBody`. Add `IsDynamicSelfPair` predicate + `BuildDynamicVerbatimCopyLambda` for self-pair routing (only invoked when both sides are dynamic AND an explicit `CreateMap` registered them).
- `C:\Repos\Atlas\src\Atlas\Internal\MappingInvoker.cs` — add 6 runtime helpers: `ConvertObjectTo<T>`, `ScanPrefix`, `ConvertObjectToList<T>`, `ConvertObjectToArray<T>`, `SerializeValue`, `SerializeCollection<T>`, `SerializeDictionary<TKey, TValue>`.
- `C:\Repos\Atlas\src\Atlas\Internal\ConfigurationValidator.cs` — add `if (tm.IsDynamic) continue;` skip in `Validate`'s TypeMap loop.
- `C:\Repos\Atlas\src\Atlas.Projections\Internal\ProjectionPlanBuilder.cs` — add `RejectDynamicOrThrow(TypeMap)` mirror of `RejectHooksOrThrow`, called from `BuildBody` (line 25).

### New files (tests)

- `C:\Repos\Atlas\tests\Atlas.Tests\DynamicShapeTests.cs` — predicates only (~5 tests).
- `C:\Repos\Atlas\tests\Atlas.Tests\MapperRegistryDynamicMappingTests.cs` — materialization + caching + closed-pair-precedence + non-firing for self-pairs (~7 tests).
- `C:\Repos\Atlas\tests\Atlas.Tests\MapperDictToPocoTests.cs` — primitives + nested + dot-notation + collections + ctor + update-in-place (~25 tests).
- `C:\Repos\Atlas\tests\Atlas.Tests\MapperPocoToDictTests.cs` — primitives + nested + collections + typed-POCO-dictionary + enums + concrete-type contract + update-in-place (~17 tests).
- `C:\Repos\Atlas\tests\Atlas.Tests\MapperDynamicMappingIntegrationTests.cs` — `NameComparison`, transformers, threading, collection-of-dynamic recursion, inheritance inert, non-public properties (~10 tests).
- `C:\Repos\Atlas\tests\Atlas.Tests\ConfigurationValidatorDynamicMappingTests.cs` — validator skip-rule (~3 tests).
- `C:\Repos\Atlas\tests\Atlas.Projections.Tests\ProjectionRejectsDynamicMappingTests.cs` — projection rejection (~2 tests).

### Modified files (docs)

- `C:\Repos\Atlas\README.md` — add a "Dynamic / dictionary mapping" section before deferred-features list. Remove #10 from the deferred list.
- `C:\Repos\Atlas\docs\Atlas-Design-DynamicMapping.md` (already on `main`) — no changes during implementation.

### Test count delta target

Baseline from PR #9: **489 PASS** (406 Atlas.Tests + 69 Projections + 14 EFCore).

After this feature: **~559 PASS** (≈ +70 net):
- +5 in `DynamicShapeTests` (new file)
- +7 in `MapperRegistryDynamicMappingTests` (new file)
- +25 in `MapperDictToPocoTests` (new file)
- +17 in `MapperPocoToDictTests` (new file)
- +10 in `MapperDynamicMappingIntegrationTests` (new file)
- +3 in `ConfigurationValidatorDynamicMappingTests` (new file)
- +2 in `ProjectionRejectsDynamicMappingTests` (new file)
- +1 expected backfill (closed-pair precedence collision behavior tested existed elsewhere; one regression assertion may need updating)

Per-feature plan-arithmetic-drift discipline (memory feedback): the implementer's actual count is authoritative; treat ~70 as approximate.

---

## Task 0 — Branch setup

**Files:** none (controller-only operation).

- [ ] **Step 0.1: Verify clean state on `main`**

```pwsh
cd C:\Repos\Atlas
git status
git log --oneline -3
```

Expected: working tree clean; HEAD at `0d93436` ("docs: design for Atlas v2 #10 Dynamic / Dictionary / ExpandoObject mapping") or further if you've pulled the design commit.

- [ ] **Step 0.2: Cut feature branch**

```pwsh
git checkout -b feat/dynamic-mapping
```

Expected: switched to a new branch `feat/dynamic-mapping`.

- [ ] **Step 0.3: Confirm baseline test count**

```pwsh
dotnet test --nologo 2>&1 | Select-String -Pattern "Passed:|Failed:|Skipped:"
```

Expected: `Passed: 489, Failed: 0, Skipped: 0` across the three test projects.

---

## Task 1 — `DynamicShape` predicates

**Goal:** Stand up the gating predicates the lookup pipeline will consult later. No materialization yet.

**Files:**
- Create: `C:\Repos\Atlas\src\Atlas\Internal\DynamicShape.cs`
- Create: `C:\Repos\Atlas\tests\Atlas.Tests\DynamicShapeTests.cs`

**Allowlist for the implementer subagent:** the two files above, no others.

- [ ] **Step 1.1: Write `DynamicShapeTests.cs` (failing)**

```csharp
namespace Atlas.Tests;

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Dynamic;
using Atlas.Internal;

public class DynamicShapeTests
{
    [Fact]
    public void IsDynamicShape_ReturnsTrue_ForIDictionaryStringObject()
    {
        Assert.True(DynamicShape.IsDynamicShape(typeof(IDictionary<string, object>)));
    }

    [Fact]
    public void IsDynamicShape_ReturnsTrue_ForExpandoObject()
    {
        Assert.True(DynamicShape.IsDynamicShape(typeof(ExpandoObject)));
    }

    [Fact]
    public void IsDynamicShape_ReturnsTrue_ForDictionaryStringObject()
    {
        Assert.True(DynamicShape.IsDynamicShape(typeof(Dictionary<string, object>)));
    }

    [Theory]
    [InlineData(typeof(Dictionary<string, int>))]
    [InlineData(typeof(IDictionary<int, object>))]
    [InlineData(typeof(ConcurrentDictionary<string, object>))]
    [InlineData(typeof(List<KeyValuePair<string, object>>))]
    [InlineData(typeof(string))]
    [InlineData(typeof(object))]
    public void IsDynamicShape_ReturnsFalse_ForNonRecognizedTypes(Type t)
    {
        Assert.False(DynamicShape.IsDynamicShape(t));
    }

    [Fact]
    public void IsDynamicPair_TrueWhenSourceDynamicDestinationPoco()
    {
        var pair = new TypePair(typeof(IDictionary<string, object>), typeof(SamplePoco));
        Assert.True(DynamicShape.IsDynamicPair(pair));
    }

    [Fact]
    public void IsDynamicPair_TrueWhenSourcePocoDestinationDynamic()
    {
        var pair = new TypePair(typeof(SamplePoco), typeof(ExpandoObject));
        Assert.True(DynamicShape.IsDynamicPair(pair));
    }

    [Fact]
    public void IsDynamicPair_FalseWhenBothDynamic()
    {
        var pair = new TypePair(typeof(ExpandoObject), typeof(Dictionary<string, object>));
        Assert.False(DynamicShape.IsDynamicPair(pair));
    }

    [Fact]
    public void IsDynamicPair_FalseWhenNeitherDynamic()
    {
        var pair = new TypePair(typeof(SamplePoco), typeof(SampleOtherPoco));
        Assert.False(DynamicShape.IsDynamicPair(pair));
    }

    private sealed class SamplePoco { public int Id { get; set; } }
    private sealed class SampleOtherPoco { public int Id { get; set; } }
}
```

- [ ] **Step 1.2: Run tests — verify they fail**

```pwsh
dotnet test tests/Atlas.Tests/Atlas.Tests.csproj --filter FullyQualifiedName~DynamicShapeTests --nologo
```

Expected: 9 failures with `error CS0103: The name 'DynamicShape' does not exist...` or similar (compile failure is fine; we have not written `DynamicShape.cs` yet).

- [ ] **Step 1.3: Create `DynamicShape.cs`**

```csharp
namespace Atlas.Internal;

using System.Collections.Generic;
using System.Dynamic;

/// <summary>
/// Gating predicates and lazy-materialization factory for Atlas v2 #10 dynamic mapping.
/// Detects when a TypePair has exactly one side as a recognized dynamic shape
/// (<see cref="IDictionary{TKey, TValue}"/> with TKey=string TValue=object,
/// <see cref="ExpandoObject"/>, or <see cref="Dictionary{TKey, TValue}"/> with TKey=string TValue=object)
/// and the other side as a POCO. See docs/Atlas-Design-DynamicMapping.md §4.3.
/// </summary>
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
}
```

- [ ] **Step 1.4: Run tests — verify they pass**

```pwsh
dotnet test tests/Atlas.Tests/Atlas.Tests.csproj --filter FullyQualifiedName~DynamicShapeTests --nologo
```

Expected: `Passed: 9` (the `[Theory]` expands to 6 + the 3 named `[Fact]`s for IsDynamicShape true cases + the 4 IsDynamicPair tests = 13; xUnit reports `[Theory]` as N test cases). Adjust the assertion count if xUnit collapses theories — what matters is zero failures.

- [ ] **Step 1.5: Run full suite — verify no regression**

```pwsh
dotnet test --nologo 2>&1 | Select-String -Pattern "Passed:|Failed:"
```

Expected: total Passed increased by ≥9 vs. Task 0 baseline; Failed: 0.

- [ ] **Step 1.6: Commit**

```pwsh
git add src/Atlas/Internal/DynamicShape.cs tests/Atlas.Tests/DynamicShapeTests.cs
git commit -m "DynamicShape predicates: IsDynamicShape + IsDynamicPair (Task 1)"
```

---

## Task 2 — `TypeMap.IsDynamic` + `PropertyMap.DynamicKey` fields

**Goal:** Plumb the two new fields into the existing data shapes plus add static factories on `PropertyMap` so synthesized PMs are constructed via the same factory pattern existing PMs use.

**Files:**
- Modify: `C:\Repos\Atlas\src\Atlas\Internal\TypeMap.cs` — add `IsDynamic` field
- Modify: `C:\Repos\Atlas\src\Atlas\Internal\PropertyMap.cs` — add `DynamicKey` field + 2 new static factories `ForDictKey`, `ForPocoSource`
- Create: `C:\Repos\Atlas\tests\Atlas.Tests\Internal\PropertyMapDynamicFactoryTests.cs` — verify new factories produce correctly-shaped PMs (~3 tests)

**Allowlist for the implementer subagent:** the three files above, no others.

- [ ] **Step 2.1: Write `PropertyMapDynamicFactoryTests.cs` (failing)**

```csharp
namespace Atlas.Tests.Internal;

using Atlas.Internal;

public class PropertyMapDynamicFactoryTests
{
    [Fact]
    public void ForDictKey_PopulatesDynamicKeyAndDestinationProperty()
    {
        var member = typeof(SamplePoco).GetProperty(nameof(SamplePoco.Name))!;
        var pm = PropertyMap.ForDictKey(member, "Name");
        Assert.NotNull(pm.DestinationProperty);
        Assert.Equal(nameof(SamplePoco.Name), pm.DestinationProperty!.Name);
        Assert.Equal("Name", pm.DynamicKey);
        Assert.Null(pm.SourcePath);
    }

    [Fact]
    public void ForPocoSource_PopulatesDynamicKeyAndSourcePath()
    {
        var member = typeof(SamplePoco).GetProperty(nameof(SamplePoco.Name))!;
        var pm = PropertyMap.ForPocoSource(member, "Name");
        Assert.Null(pm.DestinationProperty);
        Assert.Equal("Name", pm.DynamicKey);
        Assert.NotNull(pm.SourcePath);
        Assert.Single(pm.SourcePath!);
        Assert.Same(member, pm.SourcePath![0]);
    }

    [Fact]
    public void DynamicKey_DefaultsToNull_ForRegularFactories()
    {
        var member = typeof(SamplePoco).GetProperty(nameof(SamplePoco.Name))!;
        var pm = PropertyMap.ForProperty(member);
        Assert.Null(pm.DynamicKey);
    }

    private sealed class SamplePoco { public string Name { get; set; } = ""; }
}
```

- [ ] **Step 2.2: Run tests — verify they fail (compile errors expected)**

```pwsh
dotnet test tests/Atlas.Tests/Atlas.Tests.csproj --filter FullyQualifiedName~PropertyMapDynamicFactoryTests --nologo
```

Expected: compile failure, `'PropertyMap' does not contain a definition for 'ForDictKey'` (etc.).

- [ ] **Step 2.3: Add `IsDynamic` to `TypeMap`**

In `C:\Repos\Atlas\src\Atlas\Internal\TypeMap.cs`, after the `OriginatingProfile` property (currently around line 107), add:

```csharp
/// <summary>
/// True for lazily-materialized dynamic TypeMaps (Atlas v2 #10 — see DynamicShape).
/// Causes ExecutionPlanBuilder to dispatch to DynamicPlanBuilder, ConfigurationValidator
/// to skip, and Atlas.Projections to reject this TypeMap.
/// </summary>
public bool IsDynamic { get; init; }
```

(Use `init`-only — only `DynamicShape.MaterializeTypeMap` should set this, never reassign.)

- [ ] **Step 2.4: Add `DynamicKey` field and two factories to `PropertyMap`**

In `C:\Repos\Atlas\src\Atlas\Internal\PropertyMap.cs`, after the `NullSubstitute` property (currently around line 66), add:

```csharp
/// <summary>
/// Non-null iff this PropertyMap belongs to a dynamic TypeMap (TypeMap.IsDynamic == true).
/// The value is the dictionary key under which to read (dict→POCO direction) or write
/// (POCO→dict direction). See docs/Atlas-Design-DynamicMapping.md §4.2.
/// </summary>
public string? DynamicKey { get; set; }
```

After the existing `ForPath` factory (currently around line 92), add:

```csharp
/// <summary>
/// Factory for synthesized PropertyMaps in dict→POCO direction of a dynamic TypeMap.
/// The PropertyMap targets a writable POCO destination property and reads its value
/// from <paramref name="dynamicKey"/> in the source IDictionary&lt;string, object&gt;.
/// </summary>
internal static PropertyMap ForDictKey(PropertyInfo destinationMember, string dynamicKey)
{
    var pm = ForProperty(destinationMember);
    pm.DynamicKey = dynamicKey;
    return pm;
}

/// <summary>
/// Factory for synthesized PropertyMaps in POCO→dict direction of a dynamic TypeMap.
/// The PropertyMap reads its value from a readable POCO source property and writes
/// to <paramref name="dynamicKey"/> in the destination IDictionary&lt;string, object&gt;.
/// </summary>
internal static PropertyMap ForPocoSource(PropertyInfo sourceMember, string dynamicKey)
{
    var pm = new PropertyMap
    {
        Name = dynamicKey,
        SourcePath = new[] { sourceMember },
        DynamicKey = dynamicKey,
    };
    return pm;
}
```

(If `Name` and other constructor-pattern fields require different initialization — check the existing `ForProperty`/`ForPath` factories at lines 78 and 92 of `PropertyMap.cs` — match their style. The plan body above is the intent; literal field assignments adjust to match the actual `PropertyMap` constructor surface as observed during implementation.)

- [ ] **Step 2.5: Run new tests — verify they pass**

```pwsh
dotnet test tests/Atlas.Tests/Atlas.Tests.csproj --filter FullyQualifiedName~PropertyMapDynamicFactoryTests --nologo
```

Expected: `Passed: 3, Failed: 0`.

- [ ] **Step 2.6: Run full suite — verify no regression**

```pwsh
dotnet test --nologo 2>&1 | Select-String -Pattern "Passed:|Failed:"
```

Expected: total Passed increased by ≥3 vs. previous step; Failed: 0.

- [ ] **Step 2.7: Commit**

```pwsh
git add src/Atlas/Internal/TypeMap.cs src/Atlas/Internal/PropertyMap.cs tests/Atlas.Tests/Internal/PropertyMapDynamicFactoryTests.cs
git commit -m "TypeMap.IsDynamic + PropertyMap.DynamicKey + 2 factories (Task 2)"
```

---

## Task 3 — `DynamicShape.MaterializeTypeMap` + `MapperRegistry.GetTypeMap` stage 3

**Goal:** Wire lazy materialization. After this task, `mapperRegistry.GetTypeMap(...)` returns a sealed dynamic TypeMap with synthesized PMs for any (dict, POCO) or (POCO, dict) pair. Codegen integration comes in Task 4 — for now, materialized maps cannot be invoked via `mapper.Map<>()`.

**Files:**
- Modify: `C:\Repos\Atlas\src\Atlas\Internal\DynamicShape.cs` — add `MaterializeTypeMap` factory
- Modify: `C:\Repos\Atlas\src\Atlas\Internal\MapperRegistry.cs` — extend `GetTypeMap` with stage 3 detection
- Create: `C:\Repos\Atlas\tests\Atlas.Tests\MapperRegistryDynamicMappingTests.cs` — caching, OriginatingProfile = null, closed-pair precedence (~7 tests)

**Allowlist for the implementer subagent:** the three files above, no others.

**Cross-task dependency note:** This task creates a `DynamicPlanBuilder` STUB at `src/Atlas/Internal/DynamicPlanBuilder.cs` containing only `public static LambdaExpression Build(TypeMap, MapperRegistry) => throw new NotImplementedException("DynamicPlanBuilder.Build will be implemented in Task 4");`. **The stub IS in the allowlist for this task.** Task 4 replaces the stub with the real implementation. Tests in this task NEVER call `mapper.Map<>()` — they only inspect `GetTypeMap` return values and the cache.

- [ ] **Step 3.1: Write `MapperRegistryDynamicMappingTests.cs` (failing)**

```csharp
namespace Atlas.Tests;

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Dynamic;
using Atlas;
using Atlas.Internal;

public class MapperRegistryDynamicMappingTests
{
    [Fact]
    public void GetTypeMap_MaterializesDictToPoco_OnFirstCall()
    {
        var cfg = new MapperConfiguration(_ => { });
        var registry = GetRegistry(cfg);

        var pair = new TypePair(typeof(IDictionary<string, object>), typeof(SamplePoco));
        var tm = registry.GetTypeMap(pair);

        Assert.NotNull(tm);
        Assert.True(tm!.IsDynamic);
        Assert.Equal(typeof(IDictionary<string, object>), tm.SourceType);
        Assert.Equal(typeof(SamplePoco), tm.DestinationType);
        Assert.True(tm.IsSealed);
    }

    [Fact]
    public void GetTypeMap_MaterializesPocoToDict_OnFirstCall()
    {
        var cfg = new MapperConfiguration(_ => { });
        var registry = GetRegistry(cfg);

        var pair = new TypePair(typeof(SamplePoco), typeof(ExpandoObject));
        var tm = registry.GetTypeMap(pair);

        Assert.NotNull(tm);
        Assert.True(tm!.IsDynamic);
    }

    [Fact]
    public void GetTypeMap_ReturnsCachedInstance_OnSecondCall()
    {
        var cfg = new MapperConfiguration(_ => { });
        var registry = GetRegistry(cfg);

        var pair = new TypePair(typeof(IDictionary<string, object>), typeof(SamplePoco));
        var first = registry.GetTypeMap(pair);
        var second = registry.GetTypeMap(pair);

        Assert.Same(first, second);
    }

    [Fact]
    public void GetTypeMap_DoesNotFire_WhenBothSidesDynamic()
    {
        var cfg = new MapperConfiguration(_ => { });
        var registry = GetRegistry(cfg);

        var pair = new TypePair(typeof(ExpandoObject), typeof(Dictionary<string, object>));
        var tm = registry.GetTypeMap(pair);

        // Detector requires XOR — both-dynamic falls through; no map registered → null.
        Assert.Null(tm);
    }

    [Fact]
    public void GetTypeMap_OriginatingProfileIsNull_ForDynamicMaps()
    {
        var cfg = new MapperConfiguration(_ => { });
        var registry = GetRegistry(cfg);

        var pair = new TypePair(typeof(IDictionary<string, object>), typeof(SamplePoco));
        var tm = registry.GetTypeMap(pair);

        Assert.Null(tm!.OriginatingProfile);
    }

    [Fact]
    public void GetTypeMap_SynthesizesOnePropertyMapPerWritablePocoMember_ForDictToPoco()
    {
        var cfg = new MapperConfiguration(_ => { });
        var registry = GetRegistry(cfg);

        var pair = new TypePair(typeof(IDictionary<string, object>), typeof(SamplePoco));
        var tm = registry.GetTypeMap(pair);

        Assert.Equal(2, tm!.PropertyMaps.Count);  // SamplePoco has Id and Name (both writable)
        Assert.Contains(tm.PropertyMaps, pm => pm.DynamicKey == "Id");
        Assert.Contains(tm.PropertyMaps, pm => pm.DynamicKey == "Name");
    }

    [Fact]
    public void GetTypeMap_ExplicitClosedRegistration_TakesPrecedenceOverDetector()
    {
        // Per design §7.1: explicit registration wins; detector never fires for this pair.
        // (V1 limitation: the explicit registration produces broken codegen, but the
        //  detector-skip behaviour is the property under test here, not the codegen.)
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<SamplePoco, IDictionary<string, object>>());
        var registry = GetRegistry(cfg);

        var pair = new TypePair(typeof(SamplePoco), typeof(IDictionary<string, object>));
        var tm = registry.GetTypeMap(pair);

        Assert.NotNull(tm);
        Assert.False(tm!.IsDynamic);   // explicit registration is NOT marked dynamic
    }

    private static MapperRegistry GetRegistry(MapperConfiguration cfg)
    {
        var field = typeof(MapperConfiguration).GetField("_registry",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (MapperRegistry)field.GetValue(cfg)!;
    }

    private sealed class SamplePoco
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }
}
```

- [ ] **Step 3.2: Run tests — verify they fail (compile failure expected)**

```pwsh
dotnet test tests/Atlas.Tests/Atlas.Tests.csproj --filter FullyQualifiedName~MapperRegistryDynamicMappingTests --nologo
```

Expected: compile failures referencing `DynamicShape.MaterializeTypeMap` not existing OR test failures asserting against current behavior (current `GetTypeMap` returns null for unregistered dynamic pairs).

- [ ] **Step 3.3: (Skipped — `DynamicPlanBuilder.cs` is created in Task 4)**

Task 3 does NOT touch `ExecutionPlanBuilder.Build`. Tests in Task 3 exercise only `MapperRegistry.GetTypeMap` and inspect the returned TypeMap directly — they never call `mapper.Map<>()`, so no codegen integration is required. The `DynamicPlanBuilder.cs` file is created in Task 4 alongside the codegen wiring. Allowlist for Task 3 is exactly the 3 files listed above; do not add `DynamicPlanBuilder.cs`.

- [ ] **Step 3.4: Implement `DynamicShape.MaterializeTypeMap`**

In `C:\Repos\Atlas\src\Atlas\Internal\DynamicShape.cs`, after the `IsDynamicPair` method, add:

```csharp
/// <summary>
/// Materializes a dynamic TypeMap on demand. Called by MapperRegistry.GetTypeMap when the
/// closed-pair cache and open-generic template scan both miss and IsDynamicPair returns true.
/// Synthesizes one PropertyMap per public writable POCO member (dict→POCO direction) or
/// one per public readable POCO member (POCO→dict direction).
/// </summary>
internal static TypeMap MaterializeTypeMap(
    TypePair pair,
    ValueTransformerCollection? globalTransformers,
    ConventionOptions conventions)
{
    if (IsDynamicShape(pair.Source))
        return BuildDictToPocoTypeMap(pair, globalTransformers, conventions);
    else
        return BuildPocoToDictTypeMap(pair, globalTransformers, conventions);
}

private static TypeMap BuildDictToPocoTypeMap(
    TypePair pair,
    ValueTransformerCollection? globalTransformers,
    ConventionOptions conventions)
{
    var pocoType = pair.Destination;
    var tm = new TypeMap(pair.Source, pair.Destination, MemberList.None)
    {
        IsDynamic = true,
        OriginatingProfile = null,
        RegistrationOrigin = "<dynamic>",
    };

    foreach (var prop in pocoType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
    {
        if (!prop.CanWrite) continue;
        if (prop.GetIndexParameters().Length > 0) continue;
        tm.PropertyMaps.Add(PropertyMap.ForDictKey(prop, prop.Name));
    }

    if (globalTransformers is not null)
        TransformerResolver.Resolve(new[] { tm }, globalTransformers);

    tm.Seal();
    return tm;
}

private static TypeMap BuildPocoToDictTypeMap(
    TypePair pair,
    ValueTransformerCollection? globalTransformers,
    ConventionOptions conventions)
{
    var pocoType = pair.Source;
    var tm = new TypeMap(pair.Source, pair.Destination, MemberList.None)
    {
        IsDynamic = true,
        OriginatingProfile = null,
        RegistrationOrigin = "<dynamic>",
    };

    foreach (var prop in pocoType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
    {
        if (!prop.CanRead) continue;
        if (prop.GetIndexParameters().Length > 0) continue;
        tm.PropertyMaps.Add(PropertyMap.ForPocoSource(prop, prop.Name));
    }

    if (globalTransformers is not null)
        TransformerResolver.Resolve(new[] { tm }, globalTransformers);

    tm.Seal();
    return tm;
}
```

(Member discovery rule: public, instance, non-indexer, non-static. Matches Atlas's v1 convention engine. The `PropertyMaps.Add(...)` line assumes `TypeMap.PropertyMaps` is a mutable List in the unsealed state. If it's `IReadOnlyList`, use a temporary `var list` and assign via reflection or extend `TypeMap` with an internal `AddPropertyMap` method. Inspect the actual `TypeMap.PropertyMaps` type at implementation time — `TypeMap.cs` line 14 — and follow the same pattern existing factories use to populate it.)

- [ ] **Step 3.5: Extend `MapperRegistry.GetTypeMap` with stage 3**

In `C:\Repos\Atlas\src\Atlas\Internal\MapperRegistry.cs`, locate `GetTypeMap` at line 57. After the existing open-generic template-scan branch (lines 64–73), add the stage-3 detector branch BEFORE the final `return null;` (or whatever `GetTypeMap` ends with):

```csharp
// Stage 3: dynamic-shape detector (Atlas v2 #10 — see docs/Atlas-Design-DynamicMapping.md §2.1)
if (DynamicShape.IsDynamicPair(pair))
    return _typeMaps.GetOrAdd(pair, _ =>
        DynamicShape.MaterializeTypeMap(pair, _globalTransformers, _conventionOptions));
```

(Match the existing open-generic stage's coding style at lines 64–73 — same `_typeMaps.GetOrAdd(pair, _ => ...)` pattern.)

- [ ] **Step 3.6: Run new tests — verify they pass**

```pwsh
dotnet test tests/Atlas.Tests/Atlas.Tests.csproj --filter FullyQualifiedName~MapperRegistryDynamicMappingTests --nologo
```

Expected: `Passed: 7, Failed: 0`.

- [ ] **Step 3.7: Run full suite — verify no regression**

```pwsh
dotnet test --nologo 2>&1 | Select-String -Pattern "Passed:|Failed:"
```

Expected: total Passed increased by 7 vs. previous step; Failed: 0.

- [ ] **Step 3.8: Commit**

```pwsh
git add src/Atlas/Internal/DynamicShape.cs src/Atlas/Internal/MapperRegistry.cs tests/Atlas.Tests/MapperRegistryDynamicMappingTests.cs
git commit -m "DynamicShape.MaterializeTypeMap + MapperRegistry stage-3 detection (Task 3)"
```

---

## Task 4 — Dict→POCO codegen (primitives)

**Goal:** Wire `DynamicPlanBuilder.BuildDictToPocoLambda` for primitive-typed POCO destination properties. After this task, `mapper.Map<MyPoco>(someDict)` works for POCOs whose properties are all primitives, strings, Guids, DateTimes, decimals, doubles, longs, ints, bools, and nullable variants of these. Nested POCO destinations and collections are deferred to Tasks 5 and 6.

**Files:**
- Create: `C:\Repos\Atlas\src\Atlas\Internal\DynamicPlanBuilder.cs` — `Build`, `BuildDictToPocoLambda`, helper expression builders
- Modify: `C:\Repos\Atlas\src\Atlas\Internal\MappingInvoker.cs` — add `ConvertObjectTo<T>` runtime helper
- Modify: `C:\Repos\Atlas\src\Atlas\Internal\ExecutionPlanBuilder.cs` — add `if (typeMap.IsDynamic)` branch at top of `Build`/`BuildBaseBody`
- Create: `C:\Repos\Atlas\tests\Atlas.Tests\MapperDictToPocoTests.cs` — primitive-only tests (~10 tests)

**Allowlist for the implementer subagent:** the four files above, no others.

- [ ] **Step 4.1: Write `MapperDictToPocoTests.cs` (failing) — primitives only**

```csharp
namespace Atlas.Tests;

using System.Collections.Generic;
using Atlas;

public class MapperDictToPocoTests
{
    [Fact]
    public void Map_DictWithIntValue_PopulatesIntProperty()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var dict = new Dictionary<string, object> { ["Id"] = 42 };
        var p = mapper.Map<SimplePoco>(dict);
        Assert.Equal(42, p.Id);
    }

    [Fact]
    public void Map_DictWithStringValue_PopulatesStringProperty()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var dict = new Dictionary<string, object> { ["Name"] = "alice" };
        var p = mapper.Map<SimplePoco>(dict);
        Assert.Equal("alice", p.Name);
    }

    [Fact]
    public void Map_DictWithLongValue_WidensToInt_NumericConversion()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var dict = new Dictionary<string, object> { ["Id"] = 42L };
        var p = mapper.Map<SimplePoco>(dict);
        Assert.Equal(42, p.Id);
    }

    [Fact]
    public void Map_DictWithStringValue_ParsesToGuid()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var dict = new Dictionary<string, object> { ["Token"] = "550e8400-e29b-41d4-a716-446655440000" };
        var p = mapper.Map<GuidPoco>(dict);
        Assert.Equal(Guid.Parse("550e8400-e29b-41d4-a716-446655440000"), p.Token);
    }

    [Fact]
    public void Map_DictWithStringValue_ParsesToDateTime()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var dict = new Dictionary<string, object> { ["When"] = "2026-05-06" };
        var p = mapper.Map<DatePoco>(dict);
        Assert.Equal(new DateTime(2026, 5, 6), p.When);
    }

    [Fact]
    public void Map_DictMissingKey_LeavesDestinationAtDefault()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var dict = new Dictionary<string, object> { };
        var p = mapper.Map<SimplePoco>(dict);
        Assert.Equal(0, p.Id);
        Assert.Null(p.Name);
    }

    [Fact]
    public void Map_DictWithNullValue_AssignsNullToReferenceType()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var dict = new Dictionary<string, object> { ["Name"] = null! };
        var p = mapper.Map<SimplePoco>(dict);
        Assert.Null(p.Name);
    }

    [Fact]
    public void Map_DictWithNullValue_AssignsDefaultToNonNullableValueType()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var dict = new Dictionary<string, object> { ["Id"] = null! };
        var p = mapper.Map<SimplePoco>(dict);
        Assert.Equal(0, p.Id);
    }

    [Fact]
    public void Map_DictWithNullValue_AssignsNullToNullableValueType()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var dict = new Dictionary<string, object> { ["MaybeAge"] = null! };
        var p = mapper.Map<NullableIntPoco>(dict);
        Assert.Null(p.MaybeAge);
    }

    [Fact]
    public void Map_DictWithIncompatibleType_ThrowsAtlasMappingException()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var dict = new Dictionary<string, object> { ["Id"] = "not-a-number" };
        var ex = Assert.Throws<AtlasMappingException>(() => mapper.Map<SimplePoco>(dict));
        Assert.Contains("Id", ex.Message);
    }

    private sealed class SimplePoco
    {
        public int Id { get; set; }
        public string? Name { get; set; }
    }
    private sealed class GuidPoco { public Guid Token { get; set; } }
    private sealed class DatePoco { public DateTime When { get; set; } }
    private sealed class NullableIntPoco { public int? MaybeAge { get; set; } }
}
```

- [ ] **Step 4.2: Run tests — verify they fail**

```pwsh
dotnet test tests/Atlas.Tests/Atlas.Tests.csproj --filter FullyQualifiedName~MapperDictToPocoTests --nologo
```

Expected: 10 failures, most likely `NotImplementedException` from the stub OR `AtlasMappingException("no map registered")` if the stub wasn't created.

- [ ] **Step 4.3: Add `MappingInvoker.ConvertObjectTo<T>` runtime helper**

In `C:\Repos\Atlas\src\Atlas\Internal\MappingInvoker.cs`, after the existing `InvokeToDictionary` method (line 96+), add:

```csharp
/// <summary>
/// Per-key value coercion helper for dict→POCO codegen (Atlas v2 #10).
/// Tries identity → numeric/IConvertible widening → string parsing (Guid/DateTime/TimeSpan/enum)
/// → recursive dict→POCO → registered TypeMap (srcRuntimeType, dstType).
/// Throws AtlasMappingException with diagnostic info when no path applies.
/// Nested map dispatch goes through reflection on MappingInvoker.Invoke&lt;TSrc, TDest&gt; so
/// no IMapper instance flows through the compiled lambda.
/// See docs/Atlas-Design-DynamicMapping.md §5.3.
/// </summary>
public static T? ConvertObjectTo<T>(object? value, MapperRegistry registry, string keyForDiagnostics)
{
    if (value is null) return default;

    var dstType = typeof(T);

    if (value is T direct) return direct;

    var srcType = value.GetType();

    // Numeric / IConvertible widening
    if (TryNumericOrConvertible(value, srcType, dstType, out var numericConverted))
        return (T?)numericConverted;

    // String parsing for Guid / DateTime / TimeSpan / enum / numeric-from-string
    if (value is string s && TryParseString(s, dstType, out var parsed))
        return (T?)parsed;

    // Recursive dict→POCO via reflection on MappingInvoker.Invoke<IDictionary<string, object>, T>
    if (value is IDictionary<string, object> sub && IsPocoLike(dstType))
    {
        var invoke = typeof(MappingInvoker)
            .GetMethod(nameof(Invoke))!
            .MakeGenericMethod(typeof(IDictionary<string, object>), dstType);
        return (T?)invoke.Invoke(null, new object?[] { registry, sub });
    }

    // Registered (srcRuntimeType, dstType) TypeMap — dispatch via reflection on Invoke<srcType, T>
    if (registry.GetTypeMap(new TypePair(srcType, dstType)) is not null)
    {
        var invoke = typeof(MappingInvoker)
            .GetMethod(nameof(Invoke))!
            .MakeGenericMethod(srcType, dstType);
        return (T?)invoke.Invoke(null, new object?[] { registry, value });
    }

    throw new AtlasMappingException(
        $"Cannot convert value of type '{srcType}' at key '{keyForDiagnostics}' to '{dstType}'.");
}

private static bool TryNumericOrConvertible(object value, Type srcType, Type dstType, out object? converted)
{
    // Use Atlas's existing NumericConversions helper if available; else delegate to Convert.ChangeType.
    // For nullable destinations, unwrap with Nullable.GetUnderlyingType.
    var underlyingDst = Nullable.GetUnderlyingType(dstType) ?? dstType;
    if (underlyingDst.IsPrimitive || underlyingDst == typeof(decimal) || underlyingDst == typeof(string))
    {
        try
        {
            converted = Convert.ChangeType(value, underlyingDst);
            return true;
        }
        catch { converted = null; return false; }
    }
    converted = null;
    return false;
}

private static bool TryParseString(string s, Type dstType, out object? parsed)
{
    var underlyingDst = Nullable.GetUnderlyingType(dstType) ?? dstType;
    if (underlyingDst == typeof(Guid)) { if (Guid.TryParse(s, out var g)) { parsed = g; return true; } }
    if (underlyingDst == typeof(DateTime)) { if (DateTime.TryParse(s, out var d)) { parsed = d; return true; } }
    if (underlyingDst == typeof(DateTimeOffset)) { if (DateTimeOffset.TryParse(s, out var dt)) { parsed = dt; return true; } }
    if (underlyingDst == typeof(TimeSpan)) { if (TimeSpan.TryParse(s, out var t)) { parsed = t; return true; } }
    if (underlyingDst.IsEnum) { if (Enum.TryParse(underlyingDst, s, ignoreCase: true, out var e)) { parsed = e; return true; } }
    if (underlyingDst.IsPrimitive || underlyingDst == typeof(decimal))
    {
        try { parsed = Convert.ChangeType(s, underlyingDst); return true; } catch { /* fall through */ }
    }
    parsed = null;
    return false;
}

private static bool IsPocoLike(Type t)
    => !t.IsPrimitive
    && t != typeof(string)
    && t != typeof(Guid)
    && t != typeof(DateTime)
    && t != typeof(DateTimeOffset)
    && t != typeof(TimeSpan)
    && t != typeof(decimal)
    && !t.IsEnum
    && !DynamicShape.IsDynamicShape(t);
```

- [ ] **Step 4.4: Replace stub with real `DynamicPlanBuilder.cs`**

Replace the contents of `C:\Repos\Atlas\src\Atlas\Internal\DynamicPlanBuilder.cs` (or create it if Task 3 didn't):

```csharp
namespace Atlas.Internal;

using System.Linq.Expressions;
using System.Reflection;

internal static class DynamicPlanBuilder
{
    public static LambdaExpression Build(TypeMap typeMap, MapperRegistry registry)
    {
        if (DynamicShape.IsDynamicShape(typeMap.SourceType))
            return BuildDictToPocoLambda(typeMap, registry);
        else
            return BuildPocoToDictLambda(typeMap, registry);
    }

    private static LambdaExpression BuildDictToPocoLambda(TypeMap typeMap, MapperRegistry registry)
    {
        var srcParam = Expression.Parameter(typeMap.SourceType, "src");

        // Coerce src parameter to IDictionary<string, object> for uniform handling.
        var dictType = typeof(IDictionary<string, object>);
        var srcAsDict = typeMap.SourceType == dictType
            ? (Expression)srcParam
            : Expression.Convert(srcParam, dictType);

        var dst = Expression.Variable(typeMap.DestinationType, "dst");
        var body = new List<Expression> { Expression.Assign(dst, Expression.New(typeMap.DestinationType)) };

        var tryGetValue = dictType.GetMethod(
            nameof(IDictionary<string, object>.TryGetValue))!;
        var convertMethodGeneric = typeof(MappingInvoker)
            .GetMethod(nameof(MappingInvoker.ConvertObjectTo), BindingFlags.Public | BindingFlags.Static)!;
        var registryConst = Expression.Constant(registry);

        // Note: ConvertObjectTo<T> internally dispatches nested map calls via reflection on
        // MappingInvoker.Invoke<TSrc, TDest> (the same pattern v1 uses for nested POCO mapping).
        // No IMapper instance needs to flow through the compiled lambda — the registry is enough.

        foreach (var pm in typeMap.PropertyMaps)
        {
            if (pm.DynamicKey is null || pm.DestinationProperty is null) continue;

            var keyExpr = Expression.Constant(pm.DynamicKey, typeof(string));
            var valueVar = Expression.Variable(typeof(object), "v_" + pm.DynamicKey);
            var hasValue = Expression.Variable(typeof(bool), "h_" + pm.DynamicKey);
            var dstProp = pm.DestinationProperty;

            // dst.Prop = MappingInvoker.ConvertObjectTo<TProp>(v, registry, "Prop");
            var convertCall = Expression.Call(
                convertMethodGeneric.MakeGenericMethod(dstProp.PropertyType),
                valueVar, registryConst, keyExpr);

            var assign = Expression.Assign(
                Expression.Property(dst, dstProp),
                convertCall);

            // if (src.TryGetValue(key, out v)) dst.Prop = ...
            body.Add(Expression.Block(
                new[] { valueVar, hasValue },
                Expression.Assign(hasValue, Expression.Call(srcAsDict, tryGetValue, keyExpr, valueVar)),
                Expression.IfThen(hasValue, assign)
            ));
        }

        body.Add(dst);

        var block = Expression.Block(new[] { dst }, body);
        return Expression.Lambda(block, srcParam);
    }

    private static LambdaExpression BuildPocoToDictLambda(TypeMap typeMap, MapperRegistry registry)
        => throw new NotImplementedException(
            "POCO→Dict codegen lands in Task 7; this branch is intentionally unreachable for Tasks 4–6.");
}
```

(Nested map dispatch from inside `ConvertObjectTo<T>` is via reflection on `MappingInvoker.Invoke<TSrc, TDest>` — see the `ConvertObjectTo<T>` helper body in step 4.3 for the pattern. The compiled lambda holds only the `MapperRegistry` reference; no `IMapper` instance flows through. Inspect `ExecutionPlanBuilder.cs` line 421 area where `BuildNestedInvoke` is called for the equivalent v1 nested-call pattern.)

- [ ] **Step 4.5: Wire `ExecutionPlanBuilder.Build` to dispatch on `IsDynamic`**

In `C:\Repos\Atlas\src\Atlas\Internal\ExecutionPlanBuilder.cs` at the top of `Build` (line 12) — BEFORE the existing enum dispatch and inheritance dispatch branches — add:

```csharp
if (typeMap.IsDynamic)
    return DynamicPlanBuilder.Build(typeMap, registry);
```

This is the SINGLE production-code call site for `DynamicPlanBuilder.Build`. The build pipeline branches off here for both directions; the dispatch inside `DynamicPlanBuilder.Build` chooses dict→POCO or POCO→dict based on `DynamicShape.IsDynamicShape(typeMap.SourceType)`.

- [ ] **Step 4.6: Run new tests — verify they pass**

```pwsh
dotnet test tests/Atlas.Tests/Atlas.Tests.csproj --filter FullyQualifiedName~MapperDictToPocoTests --nologo
```

Expected: `Passed: 10, Failed: 0`.

- [ ] **Step 4.7: Run full suite — verify no regression**

```pwsh
dotnet test --nologo 2>&1 | Select-String -Pattern "Passed:|Failed:"
```

Expected: total Passed increased by 10; Failed: 0. **Watch for regressions** in `ExecutionPlanBuilder*Tests.cs`, `MapperConfiguration*Tests.cs` — the new `IsDynamic` branch in `Build` should be a no-op for non-dynamic TypeMaps.

- [ ] **Step 4.8: Commit**

```pwsh
git add src/Atlas/Internal/DynamicPlanBuilder.cs src/Atlas/Internal/MappingInvoker.cs src/Atlas/Internal/ExecutionPlanBuilder.cs tests/Atlas.Tests/MapperDictToPocoTests.cs
git commit -m "Dict→POCO primitives codegen + ConvertObjectTo<T> runtime helper (Task 4)"
```

---

## Task 5 — Dict→POCO nested POCO + dot-notation + collections

**Goal:** Extend `BuildDictToPocoLambda` to handle nested POCO destinations (via top-level nested-dict, top-level POCO instance, or dot-notation prefix scan) and collection destinations (`List<T>`, `T[]`).

**Files:**
- Modify: `C:\Repos\Atlas\src\Atlas\Internal\DynamicPlanBuilder.cs` — extend per-property switch with nested-POCO and collection branches
- Modify: `C:\Repos\Atlas\src\Atlas\Internal\MappingInvoker.cs` — add `ScanPrefix`, `ConvertObjectToList<T>`, `ConvertObjectToArray<T>`
- Modify: `C:\Repos\Atlas\tests\Atlas.Tests\MapperDictToPocoTests.cs` — append ~10 nested + dot-notation + collection tests

**Allowlist for the implementer subagent:** the three files above, no others.

- [ ] **Step 5.1: Append failing tests to `MapperDictToPocoTests.cs`**

```csharp
// (Append inside the existing class)

[Fact]
public void Map_NestedPocoFromTopLevelNestedDict()
{
    var mapper = new MapperConfiguration(_ => { }).CreateMapper();
    var dict = new Dictionary<string, object>
    {
        ["Customer"] = new Dictionary<string, object> { ["Name"] = "alice" }
    };
    var p = mapper.Map<OrderPoco>(dict);
    Assert.NotNull(p.Customer);
    Assert.Equal("alice", p.Customer!.Name);
}

[Fact]
public void Map_NestedPocoFromTopLevelTypedInstance()
{
    var mapper = new MapperConfiguration(_ => { }).CreateMapper();
    var customer = new CustomerPoco { Name = "alice" };
    var dict = new Dictionary<string, object> { ["Customer"] = customer };
    var p = mapper.Map<OrderPoco>(dict);
    Assert.Same(customer, p.Customer);
}

[Fact]
public void Map_NestedPocoFromDotNotationFallback()
{
    var mapper = new MapperConfiguration(_ => { }).CreateMapper();
    var dict = new Dictionary<string, object> { ["Customer.Name"] = "alice" };
    var p = mapper.Map<OrderPoco>(dict);
    Assert.NotNull(p.Customer);
    Assert.Equal("alice", p.Customer!.Name);
}

[Fact]
public void Map_TopLevelKeyWinsOverDotNotationSiblings()
{
    var mapper = new MapperConfiguration(_ => { }).CreateMapper();
    var dict = new Dictionary<string, object>
    {
        ["Customer"] = new Dictionary<string, object> { ["Name"] = "from-nested" },
        ["Customer.Name"] = "from-dot-notation"  // ignored — top-level wins
    };
    var p = mapper.Map<OrderPoco>(dict);
    Assert.Equal("from-nested", p.Customer!.Name);
}

[Fact]
public void Map_DeepDotNotationMultiplePrefixes()
{
    var mapper = new MapperConfiguration(_ => { }).CreateMapper();
    var dict = new Dictionary<string, object> { ["Customer.Address.City"] = "NYC" };
    var p = mapper.Map<DeepOrderPoco>(dict);
    Assert.Equal("NYC", p.Customer!.Address!.City);
}

[Fact]
public void Map_ListOfPrimitives_FromIEnumerableSource()
{
    var mapper = new MapperConfiguration(_ => { }).CreateMapper();
    var dict = new Dictionary<string, object> { ["Numbers"] = new[] { 1, 2, 3 } };
    var p = mapper.Map<NumberListPoco>(dict);
    Assert.Equal(new[] { 1, 2, 3 }, p.Numbers!);
}

[Fact]
public void Map_ListOfPocos_FromListOfDicts()
{
    var mapper = new MapperConfiguration(_ => { }).CreateMapper();
    var dict = new Dictionary<string, object>
    {
        ["Lines"] = new List<IDictionary<string, object>>
        {
            new Dictionary<string, object> { ["Sku"] = "X" },
            new Dictionary<string, object> { ["Sku"] = "Y" }
        }
    };
    var p = mapper.Map<OrderWithLinesPoco>(dict);
    Assert.Equal(2, p.Lines!.Count);
    Assert.Equal("X", p.Lines[0].Sku);
    Assert.Equal("Y", p.Lines[1].Sku);
}

[Fact]
public void Map_ArrayDestination_FromIEnumerableSource()
{
    var mapper = new MapperConfiguration(_ => { }).CreateMapper();
    var dict = new Dictionary<string, object> { ["Tags"] = new[] { "a", "b" } };
    var p = mapper.Map<TagsArrayPoco>(dict);
    Assert.Equal(new[] { "a", "b" }, p.Tags!);
}

[Fact]
public void Map_ExcessDictKeys_AreSilentlyIgnored()
{
    var mapper = new MapperConfiguration(_ => { }).CreateMapper();
    var dict = new Dictionary<string, object>
    {
        ["Id"] = 1,
        ["Name"] = "alice",
        ["UnknownExtra"] = "ignored"
    };
    var p = mapper.Map<SimplePoco>(dict);
    Assert.Equal(1, p.Id);
    Assert.Equal("alice", p.Name);
}

[Fact]
public void Map_OuterCollectionPair_RecursesIntoDynamicElementMap()
{
    var mapper = new MapperConfiguration(_ => { }).CreateMapper();
    var src = new List<IDictionary<string, object>>
    {
        new Dictionary<string, object> { ["Id"] = 1, ["Name"] = "a" },
        new Dictionary<string, object> { ["Id"] = 2, ["Name"] = "b" }
    };
    var result = mapper.Map<List<IDictionary<string, object>>, List<SimplePoco>>(src);
    Assert.Equal(2, result.Count);
    Assert.Equal(1, result[0].Id);
    Assert.Equal("b", result[1].Name);
}

private sealed class CustomerPoco { public string? Name { get; set; } }
private sealed class OrderPoco { public CustomerPoco? Customer { get; set; } }
private sealed class AddressPoco { public string? City { get; set; } }
private sealed class DeepCustomerPoco { public AddressPoco? Address { get; set; } }
private sealed class DeepOrderPoco { public DeepCustomerPoco? Customer { get; set; } }
private sealed class NumberListPoco { public List<int>? Numbers { get; set; } }
private sealed class OrderLinePoco { public string? Sku { get; set; } }
private sealed class OrderWithLinesPoco { public List<OrderLinePoco>? Lines { get; set; } }
private sealed class TagsArrayPoco { public string[]? Tags { get; set; } }
```

- [ ] **Step 5.2: Run new tests — verify they fail**

```pwsh
dotnet test tests/Atlas.Tests/Atlas.Tests.csproj --filter "FullyQualifiedName~MapperDictToPocoTests.Map_NestedPocoFromTopLevelNestedDict|FullyQualifiedName~MapperDictToPocoTests.Map_NestedPocoFromDotNotation|FullyQualifiedName~MapperDictToPocoTests.Map_ListOfPrimitives" --nologo
```

Expected: failures with `AtlasMappingException`, NRE, or assertion mismatches.

- [ ] **Step 5.3: Add `ScanPrefix` to `MappingInvoker.cs`**

```csharp
/// <summary>
/// Dot-notation fallback: scans <paramref name="src"/> for keys starting with
/// <paramref name="prefix"/>, strips the prefix, and returns a synthesized nested dict.
/// Returns null when no matching keys exist. See docs/Atlas-Design-DynamicMapping.md §5.4.
/// </summary>
public static IDictionary<string, object>? ScanPrefix(
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
        }
    }
    return result;
}
```

- [ ] **Step 5.4: Add `ConvertObjectToList<T>` and `ConvertObjectToArray<T>` to `MappingInvoker.cs`**

```csharp
/// <summary>POCO collection materialization helper for dict→POCO codegen.</summary>
public static List<T>? ConvertObjectToList<T>(object? value, MapperRegistry registry, string keyForDiagnostics)
{
    if (value is null) return null;
    if (value is IEnumerable enumerable)
    {
        var list = new List<T>();
        foreach (var item in enumerable)
            list.Add(ConvertObjectTo<T>(item, registry, keyForDiagnostics)!);
        return list;
    }
    throw new AtlasMappingException(
        $"Cannot convert value of type '{value.GetType()}' at key '{keyForDiagnostics}' to 'List<{typeof(T)}>'.");
}

public static T[]? ConvertObjectToArray<T>(object? value, MapperRegistry registry, string keyForDiagnostics)
{
    var list = ConvertObjectToList<T>(value, registry, keyForDiagnostics);
    return list?.ToArray();
}
```

- [ ] **Step 5.5: Extend `BuildDictToPocoLambda` to switch on destination property type**

Replace the per-property emit block in `BuildDictToPocoLambda` (added in Task 4) with a switch that detects:
1. **Primitive / scalar** — call `ConvertObjectTo<TProp>` (existing path from Task 4)
2. **Nested POCO** — emit the nested-POCO branch with prefix-fallback (see below)
3. **Collection (`List<T>`, `T[]`, `IEnumerable<T>`)** — call `ConvertObjectToList<T>` / `ConvertObjectToArray<T>`

The nested-POCO branch emits roughly:

```csharp
// Pseudocode for the emitted Expression tree:
if (src.TryGetValue("Customer", out v_Customer))
{
    if (v_Customer is null)
        dst.Customer = null;
    else if (v_Customer is IDictionary<string, object> nested)
        dst.Customer = MappingInvoker.Invoke<IDictionary<string, object>, Customer>(registry, nested);
    else if (v_Customer is Customer typed)
        dst.Customer = typed;
    else
        throw new AtlasMappingException(...);
}
else
{
    var nested = MappingInvoker.ScanPrefix(src, "Customer.", caseComparison);
    if (nested is not null)
        dst.Customer = MappingInvoker.Invoke<IDictionary<string, object>, Customer>(registry, nested);
}
```

Implementation guidance:
- Use a private `EmitNestedPocoBlock(PropertyMap, ParameterExpression src, ParameterExpression dst, MapperRegistry)` helper inside `DynamicPlanBuilder` to keep the main loop readable.
- The `is IDictionary<string, object> nested` test maps to `Expression.TypeIs` + `Expression.Convert`.
- The recursive call to `MappingInvoker.Invoke<IDictionary<string, object>, TPropType>(registry, nested)` is emitted as `Expression.Call(invokeMethod.MakeGenericMethod(typeof(IDictionary<string, object>), tProp), registryConst, nestedExpr)`. The recursive `Invoke` lookup hits the registry, fires the dynamic detector for the nested type, materializes a nested dynamic TypeMap, and runs its compiled delegate. No `IMapper` instance needed.
- The `StringComparison` for the prefix scan reads from `MapperConfigurationExpression.CaseSensitive` (line 19) — pass through to the codegen by reading `registry._conventionOptions.CaseSensitive` (or whatever accessor exists; verify at implementation).

The collection branch is straightforward — for `List<T>`, dispatch to `ConvertObjectToList<T>` via reflection on the property type's generic argument; same for `T[]` → `ConvertObjectToArray<T>`.

- [ ] **Step 5.6: Run full new-test set — verify they pass**

```pwsh
dotnet test tests/Atlas.Tests/Atlas.Tests.csproj --filter FullyQualifiedName~MapperDictToPocoTests --nologo
```

Expected: `Passed: 20, Failed: 0` (10 from Task 4 + 10 new). If any nested-POCO or dot-notation tests fail, debug by enabling `Expression.Lambda(...).ToString()` on the generated lambda before compilation and inspect.

- [ ] **Step 5.7: Run full suite — verify no regression**

```pwsh
dotnet test --nologo 2>&1 | Select-String -Pattern "Passed:|Failed:"
```

- [ ] **Step 5.8: Commit**

```pwsh
git add src/Atlas/Internal/DynamicPlanBuilder.cs src/Atlas/Internal/MappingInvoker.cs tests/Atlas.Tests/MapperDictToPocoTests.cs
git commit -m "Dict→POCO nested + dot-notation + collections (Task 5)"
```

---

## Task 6 — Dict→POCO ctor-using POCOs + update-in-place

**Goal:** Extend dict→POCO codegen for records, primary constructors, and `required`-property POCOs. Add update-in-place semantics: missing keys preserve existing destination value.

**Files:**
- Modify: `C:\Repos\Atlas\src\Atlas\Internal\DynamicPlanBuilder.cs` — add ctor-mapping branch + `BuildDictToPocoUpdateLambda`
- Modify: `C:\Repos\Atlas\src\Atlas\Internal\ExecutionPlanBuilder.cs` — wire `BuildUpdate` to `DynamicPlanBuilder` for dynamic TypeMaps (mirror of Step 4.5 for the update direction)
- Modify: `C:\Repos\Atlas\tests\Atlas.Tests\MapperDictToPocoTests.cs` — append ~5 ctor + update-in-place tests

**Allowlist for the implementer subagent:** the three files above, no others.

- [ ] **Step 6.1: Append failing tests**

```csharp
// (Append inside the existing class)

[Fact]
public void Map_RecordConstructor_PopulatesAllParams()
{
    var mapper = new MapperConfiguration(_ => { }).CreateMapper();
    var dict = new Dictionary<string, object> { ["Id"] = 42, ["Name"] = "alice" };
    var p = mapper.Map<RecordPoco>(dict);
    Assert.Equal(42, p.Id);
    Assert.Equal("alice", p.Name);
}

[Fact]
public void Map_RequiredProperty_PopulatesFromDictKey()
{
    var mapper = new MapperConfiguration(_ => { }).CreateMapper();
    var dict = new Dictionary<string, object> { ["Name"] = "alice" };
    var p = mapper.Map<RequiredPoco>(dict);
    Assert.Equal("alice", p.Name);
}

[Fact]
public void Map_RequiredProperty_ThrowsWhenKeyMissing()
{
    var mapper = new MapperConfiguration(_ => { }).CreateMapper();
    var dict = new Dictionary<string, object> { };
    var ex = Assert.Throws<AtlasMappingException>(() => mapper.Map<RequiredPoco>(dict));
    Assert.Contains("Name", ex.Message);
}

[Fact]
public void Map_UpdateInPlace_PreservesExistingValueWhenKeyMissing()
{
    var mapper = new MapperConfiguration(_ => { }).CreateMapper();
    var existing = new SimplePoco { Id = 99, Name = "preserved" };
    var dict = new Dictionary<string, object> { ["Id"] = 42 };
    mapper.Map(dict, existing);
    Assert.Equal(42, existing.Id);
    Assert.Equal("preserved", existing.Name);
}

[Fact]
public void Map_UpdateInPlace_PreservesExistingNestedPocoWhenNestedKeyMissing()
{
    var mapper = new MapperConfiguration(_ => { }).CreateMapper();
    var existing = new OrderPoco { Customer = new CustomerPoco { Name = "preserved" } };
    var dict = new Dictionary<string, object> { };  // no "Customer" key
    mapper.Map(dict, existing);
    Assert.NotNull(existing.Customer);
    Assert.Equal("preserved", existing.Customer!.Name);
}

private sealed record RecordPoco(int Id, string Name);

private sealed class RequiredPoco
{
    public required string Name { get; set; }
}
```

- [ ] **Step 6.2: Run new tests — verify they fail**

Expected: failures, likely with NRE on `new RecordPoco()` or `AtlasMappingException` for required props.

- [ ] **Step 6.3: Implement ctor-using POCO branch in `BuildDictToPocoLambda`**

At the top of `BuildDictToPocoLambda`, branch:

```csharp
var hasParameterless = typeMap.DestinationType.GetConstructor(Type.EmptyTypes) is not null
    && !HasRequiredProperties(typeMap.DestinationType);

if (hasParameterless)
    return BuildDictToPocoLambda_PropertyInit(typeMap, registry);
else
    return BuildDictToPocoLambda_CtorInit(typeMap, registry);
```

`BuildDictToPocoLambda_CtorInit` emits:

```csharp
(IDictionary<string, object> src, MappingContext ctx) =>
{
    var p_Id   = src.TryGetValue("Id", out var v0) ? ConvertObjectTo<int>(v0, ...) : default;
    var p_Name = src.TryGetValue("Name", out var v1) ? ConvertObjectTo<string>(v1, ...) : default;
    var dst = new RecordPoco(p_Id, p_Name);
    // init-only / required props beyond the ctor: per-property emit (same as PropertyInit branch)
    return dst;
}
```

For `required` properties whose key is missing AND no default value applies, emit a runtime check:

```csharp
if (!src.TryGetValue("Name", out var v))
    throw new AtlasMappingException("'Name' is required but missing from source dictionary.");
```

Use `RequiredMemberAttribute` detection on `PropertyInfo` to find `required` properties.

- [ ] **Step 6.4: Implement `BuildDictToPocoUpdateLambda` for update-in-place**

For each property emit, wrap in `IfThen` (no else-branch — this is the design's "missing-key preserves existing" semantics):

```csharp
if (src.TryGetValue("Id", out var v))
    dst.Id = ConvertObjectTo<int>(v, ...);
// no else → existing dst.Id preserved
```

For nested POCO destinations under update-in-place, recurse via `MappingInvoker.InvokeUpdate<IDictionary<string, object>, Customer>(registry, nested, dst.Customer ?? new Customer())` — preserves the existing nested instance and update-in-place'es into it. Verify `MappingInvoker.InvokeUpdate<TSrc, TDest>` exists with this signature (line 40 of `MappingInvoker.cs` per the structural inventory) — if so, the codegen emits `Expression.Call(invokeUpdateMethod.MakeGenericMethod(...), registryConst, nestedExpr, existingNestedExpr)`; if the signature differs, adapt accordingly.

- [ ] **Step 6.5: Wire `ExecutionPlanBuilder.BuildUpdate` to dispatch to `DynamicPlanBuilder` for dynamic TypeMaps**

In `ExecutionPlanBuilder.cs` at the top of `BuildUpdate` (line 136):

```csharp
if (typeMap.IsDynamic)
    return DynamicPlanBuilder.BuildUpdate(typeMap, registry);
```

Add `DynamicPlanBuilder.BuildUpdate(TypeMap, MapperRegistry)` that dispatches to `BuildDictToPocoUpdateLambda` (or, in Task 7's POCO→dict, to `BuildPocoToDictUpdateLambda`).

- [ ] **Step 6.6: Run new tests — verify they pass**

```pwsh
dotnet test tests/Atlas.Tests/Atlas.Tests.csproj --filter FullyQualifiedName~MapperDictToPocoTests --nologo
```

Expected: `Passed: 25, Failed: 0` (20 from Tasks 4-5 + 5 new).

- [ ] **Step 6.7: Run full suite — verify no regression**

- [ ] **Step 6.8: Commit**

```pwsh
git add src/Atlas/Internal/DynamicPlanBuilder.cs src/Atlas/Internal/ExecutionPlanBuilder.cs tests/Atlas.Tests/MapperDictToPocoTests.cs
git commit -m "Dict→POCO ctor-using POCOs + update-in-place (Task 6)"
```

---

## Task 7 — POCO→Dict primitives + concrete-type contract

**Goal:** Wire `BuildPocoToDictLambda` for primitive-typed source POCO properties. Verify the concrete-type contract: `ExpandoObject` destination returns `ExpandoObject`; `Dictionary<string, object>` returns `Dictionary<string, object>`; `IDictionary<string, object>` returns an `ExpandoObject` typed as the abstraction.

**Files:**
- Modify: `C:\Repos\Atlas\src\Atlas\Internal\DynamicPlanBuilder.cs` — replace the POCO→Dict stub with primitive-emit logic
- Modify: `C:\Repos\Atlas\src\Atlas\Internal\MappingInvoker.cs` — add `SerializeValue` runtime helper
- Create: `C:\Repos\Atlas\tests\Atlas.Tests\MapperPocoToDictTests.cs` — primitive emit + concrete-type contract + null source (~7 tests)

**Allowlist for the implementer subagent:** the three files above, no others.

- [ ] **Step 7.1: Write `MapperPocoToDictTests.cs` (failing) — primitives only**

```csharp
namespace Atlas.Tests;

using System.Collections.Generic;
using System.Dynamic;
using Atlas;

public class MapperPocoToDictTests
{
    [Fact]
    public void Map_PocoToExpandoObject_ReturnsExpandoObject()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var p = new SimplePoco { Id = 42, Name = "alice" };
        var d = mapper.Map<ExpandoObject>(p);
        Assert.IsType<ExpandoObject>(d);
        var dict = (IDictionary<string, object>)d;
        Assert.Equal(42, dict["Id"]);
        Assert.Equal("alice", dict["Name"]);
    }

    [Fact]
    public void Map_PocoToDictionaryStringObject_ReturnsDictionaryStringObject()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var p = new SimplePoco { Id = 42, Name = "alice" };
        var d = mapper.Map<Dictionary<string, object>>(p);
        Assert.IsType<Dictionary<string, object>>(d);
        Assert.Equal(42, d["Id"]);
        Assert.Equal("alice", d["Name"]);
    }

    [Fact]
    public void Map_PocoToIDictionaryStringObject_ReturnsExpandoObjectAsAbstraction()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var p = new SimplePoco { Id = 42, Name = "alice" };
        IDictionary<string, object> d = mapper.Map<IDictionary<string, object>>(p);
        Assert.IsType<ExpandoObject>(d);
        Assert.Equal(42, d["Id"]);
    }

    [Fact]
    public void Map_NullPropertyValue_WrittenAsNullDictValue()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var p = new SimplePoco { Id = 0, Name = null };
        var d = mapper.Map<Dictionary<string, object>>(p);
        Assert.True(d.ContainsKey("Name"));
        Assert.Null(d["Name"]);
    }

    [Fact]
    public void Map_DateTimeProperty_EmitsAsBoxedDateTime()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var p = new DatePoco { When = new DateTime(2026, 5, 6) };
        var d = mapper.Map<Dictionary<string, object>>(p);
        Assert.Equal(new DateTime(2026, 5, 6), d["When"]);
    }

    [Fact]
    public void Map_GuidProperty_EmitsAsBoxedGuid()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var p = new GuidPoco { Token = Guid.Parse("550e8400-e29b-41d4-a716-446655440000") };
        var d = mapper.Map<Dictionary<string, object>>(p);
        Assert.Equal(Guid.Parse("550e8400-e29b-41d4-a716-446655440000"), d["Token"]);
    }

    [Fact]
    public void Map_NullPocoSource_ReturnsDefault()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        SimplePoco? p = null;
        var d = mapper.Map<SimplePoco?, ExpandoObject?>(p);
        Assert.Null(d);
    }

    private sealed class SimplePoco
    {
        public int Id { get; set; }
        public string? Name { get; set; }
    }
    private sealed class DatePoco { public DateTime When { get; set; } }
    private sealed class GuidPoco { public Guid Token { get; set; } }
}
```

- [ ] **Step 7.2: Run new tests — verify they fail**

```pwsh
dotnet test tests/Atlas.Tests/Atlas.Tests.csproj --filter FullyQualifiedName~MapperPocoToDictTests --nologo
```

Expected: 7 failures with `NotImplementedException` from the POCO→dict stub.

- [ ] **Step 7.3: Add `SerializeValue` to `MappingInvoker.cs`**

```csharp
/// <summary>
/// POCO→dict per-property emit helper. Boxes primitives, recurses through
/// MappingInvoker.Invoke&lt;TDecl, ExpandoObject&gt; for nested POCOs (Atlas v2 #10).
/// See docs/Atlas-Design-DynamicMapping.md §6.4.
/// </summary>
public static object? SerializeValue(object? value, Type declaredType, MapperRegistry registry)
{
    if (value is null) return null;

    var underlying = Nullable.GetUnderlyingType(declaredType) ?? declaredType;

    if (IsPrimitiveOrString(underlying)) return value;
    if (underlying.IsEnum) return Convert.ChangeType(value, Enum.GetUnderlyingType(underlying));

    // Nested POCO: recurse via MappingInvoker.Invoke<declaredType, ExpandoObject>
    var invoke = typeof(MappingInvoker)
        .GetMethod(nameof(Invoke))!
        .MakeGenericMethod(declaredType, typeof(ExpandoObject));
    return invoke.Invoke(null, new object?[] { registry, value });
}

private static bool IsPrimitiveOrString(Type t)
    => t.IsPrimitive
    || t == typeof(string)
    || t == typeof(decimal)
    || t == typeof(Guid)
    || t == typeof(DateTime)
    || t == typeof(DateTimeOffset)
    || t == typeof(TimeSpan)
    || t == typeof(byte[]);
```

- [ ] **Step 7.4: Replace `BuildPocoToDictLambda` stub in `DynamicPlanBuilder.cs`**

```csharp
private static LambdaExpression BuildPocoToDictLambda(TypeMap typeMap, MapperRegistry registry)
{
    var srcParam = Expression.Parameter(typeMap.SourceType, "src");
    var dstType = typeMap.DestinationType;

    // Concrete-type contract per design §3.3:
    //   ExpandoObject              -> new ExpandoObject()
    //   Dictionary<string, object> -> new Dictionary<string, object>(capacity)
    //   IDictionary<string, object> (abstraction) -> new ExpandoObject() declared as IDictionary
    var dstConcreteType = dstType == typeof(Dictionary<string, object>)
        ? typeof(Dictionary<string, object>)
        : typeof(ExpandoObject);

    var dstAsConcrete = Expression.Variable(dstConcreteType, "dstConcrete");
    var dstAsDict = Expression.Variable(typeof(IDictionary<string, object>), "dst");

    var newDst = dstConcreteType == typeof(Dictionary<string, object>)
        ? Expression.New(typeof(Dictionary<string, object>).GetConstructor(new[] { typeof(int) })!,
            Expression.Constant(typeMap.PropertyMaps.Count))
        : (Expression)Expression.New(typeof(ExpandoObject));

    var body = new List<Expression>
    {
        Expression.Assign(dstAsConcrete, newDst),
        Expression.Assign(dstAsDict, dstAsConcrete is { Type: var t } && t == typeof(Dictionary<string, object>)
            ? (Expression)dstAsConcrete
            : Expression.Convert(dstAsConcrete, typeof(IDictionary<string, object>)))
    };

    var indexer = typeof(IDictionary<string, object>).GetProperty("Item")!;
    var serializeMethod = typeof(MappingInvoker).GetMethod(nameof(MappingInvoker.SerializeValue))!;
    var registryConst = Expression.Constant(registry);

    foreach (var pm in typeMap.PropertyMaps)
    {
        if (pm.DynamicKey is null || pm.SourcePath is null || pm.SourcePath.Count == 0) continue;
        var srcMember = pm.SourcePath[0];
        var srcMemberType = ((PropertyInfo)srcMember).PropertyType;

        // dst[key] = MappingInvoker.SerializeValue(src.Member, typeof(MemberType), registry);
        var memberAccess = Expression.Property(srcParam, (PropertyInfo)srcMember);
        var boxed = Expression.Convert(memberAccess, typeof(object));
        var serializeCall = Expression.Call(serializeMethod, boxed,
            Expression.Constant(srcMemberType, typeof(Type)), registryConst);

        var assign = Expression.Assign(
            Expression.MakeIndex(dstAsDict, indexer, new[] { Expression.Constant(pm.DynamicKey, typeof(string)) }),
            serializeCall);

        body.Add(assign);
    }

    body.Add(dstType == dstConcreteType
        ? (Expression)dstAsConcrete
        : Expression.Convert(dstAsConcrete, dstType));

    var block = Expression.Block(new[] { dstAsConcrete, dstAsDict }, body);
    return Expression.Lambda(block, srcParam);
}
```

- [ ] **Step 7.5: Run new tests — verify they pass**

```pwsh
dotnet test tests/Atlas.Tests/Atlas.Tests.csproj --filter FullyQualifiedName~MapperPocoToDictTests --nologo
```

Expected: `Passed: 7, Failed: 0`.

- [ ] **Step 7.6: Run full suite — verify no regression**

- [ ] **Step 7.7: Commit**

```pwsh
git add src/Atlas/Internal/DynamicPlanBuilder.cs src/Atlas/Internal/MappingInvoker.cs tests/Atlas.Tests/MapperPocoToDictTests.cs
git commit -m "POCO→Dict primitives + concrete-type contract + SerializeValue (Task 7)"
```

---

## Task 8 — POCO→Dict nested + collections + dictionaries + enums

**Goal:** Extend POCO→dict codegen for nested POCOs (always emit nested `ExpandoObject`), collections (`List<object?>`), typed-POCO dictionaries (recurse element-wise), enums (underlying integer), and update-in-place.

**Files:**
- Modify: `C:\Repos\Atlas\src\Atlas\Internal\DynamicPlanBuilder.cs` — extend POCO→dict per-property switch + add `BuildPocoToDictUpdateLambda`
- Modify: `C:\Repos\Atlas\src\Atlas\Internal\MappingInvoker.cs` — add `SerializeCollection<T>`, `SerializeDictionary<TKey, TValue>`
- Modify: `C:\Repos\Atlas\tests\Atlas.Tests\MapperPocoToDictTests.cs` — append ~10 nested + collection + dictionary + enum + update-in-place tests

**Allowlist for the implementer subagent:** the three files above, no others.

- [ ] **Step 8.1: Append failing tests**

```csharp
[Fact]
public void Map_NestedPoco_EmitsAsNestedExpandoObject_RegardlessOfOuterShape()
{
    var mapper = new MapperConfiguration(_ => { }).CreateMapper();
    var p = new OrderPoco { Customer = new CustomerPoco { Name = "alice" } };

    var asDict = mapper.Map<Dictionary<string, object>>(p);
    Assert.IsType<ExpandoObject>(asDict["Customer"]);
    Assert.Equal("alice", ((IDictionary<string, object>)asDict["Customer"])["Name"]);

    var asExpando = mapper.Map<ExpandoObject>(p);
    Assert.IsType<ExpandoObject>(((IDictionary<string, object>)asExpando)["Customer"]);
}

[Fact]
public void Map_NullNestedPoco_EmitsAsNullDictValue()
{
    var mapper = new MapperConfiguration(_ => { }).CreateMapper();
    var p = new OrderPoco { Customer = null };
    var d = mapper.Map<Dictionary<string, object>>(p);
    Assert.True(d.ContainsKey("Customer"));
    Assert.Null(d["Customer"]);
}

[Fact]
public void Map_ListOfPrimitives_EmitsAsListOfBoxedObjects()
{
    var mapper = new MapperConfiguration(_ => { }).CreateMapper();
    var p = new NumberListPoco { Numbers = new List<int> { 1, 2, 3 } };
    var d = mapper.Map<Dictionary<string, object>>(p);
    Assert.IsAssignableFrom<List<object?>>(d["Numbers"]);
    Assert.Equal(new object?[] { 1, 2, 3 }, (List<object?>)d["Numbers"]!);
}

[Fact]
public void Map_ListOfPocos_EmitsAsListOfExpandoObjects()
{
    var mapper = new MapperConfiguration(_ => { }).CreateMapper();
    var p = new OrderWithLinesPoco { Lines = new List<OrderLinePoco>
    {
        new() { Sku = "X" }, new() { Sku = "Y" }
    }};
    var d = mapper.Map<Dictionary<string, object>>(p);
    var list = (List<object?>)d["Lines"]!;
    Assert.Equal(2, list.Count);
    Assert.IsType<ExpandoObject>(list[0]);
    Assert.Equal("X", ((IDictionary<string, object>)list[0]!)["Sku"]);
}

[Fact]
public void Map_TypedPocoDictionary_EmitsAsExpandoObjectKeyedByStringification()
{
    var mapper = new MapperConfiguration(_ => { }).CreateMapper();
    var p = new InventoryPoco
    {
        Items = new Dictionary<string, OrderLinePoco>
        {
            ["A"] = new() { Sku = "X" },
            ["B"] = new() { Sku = "Y" }
        }
    };
    var d = mapper.Map<Dictionary<string, object>>(p);
    var nested = (IDictionary<string, object>)d["Items"]!;
    Assert.IsType<ExpandoObject>(nested);
    Assert.Equal("X", ((IDictionary<string, object>)nested["A"])["Sku"]);
}

[Fact]
public void Map_DictionaryWithIntKeys_EmitsKeysAsStringRepresentation()
{
    var mapper = new MapperConfiguration(_ => { }).CreateMapper();
    var p = new IntKeyedPoco { ById = new Dictionary<int, OrderLinePoco>
    {
        [1] = new() { Sku = "X" }, [2] = new() { Sku = "Y" }
    }};
    var d = mapper.Map<Dictionary<string, object>>(p);
    var nested = (IDictionary<string, object>)d["ById"]!;
    Assert.True(nested.ContainsKey("1"));
    Assert.True(nested.ContainsKey("2"));
}

[Fact]
public void Map_EnumProperty_EmitsAsUnderlyingInteger()
{
    var mapper = new MapperConfiguration(_ => { }).CreateMapper();
    var p = new StatusPoco { Status = Status.Active };
    var d = mapper.Map<Dictionary<string, object>>(p);
    Assert.Equal((int)Status.Active, d["Status"]);
}

[Fact]
public void Map_ReadOnlyProperty_IsEmittedOnPocoToDict()
{
    var mapper = new MapperConfiguration(_ => { }).CreateMapper();
    var p = new ReadOnlyPropPoco("alice");
    var d = mapper.Map<Dictionary<string, object>>(p);
    Assert.Equal("alice", d["Name"]);
}

[Fact]
public void Map_UpdateInPlace_OverwritesMatchingKeysPreservesOthers()
{
    var mapper = new MapperConfiguration(_ => { }).CreateMapper();
    var existing = new Dictionary<string, object>
    {
        ["UnrelatedKey"] = "preserved",
        ["Id"] = 0
    };
    var p = new SimplePoco { Id = 42, Name = "alice" };
    mapper.Map(p, existing);

    Assert.Equal(42, existing["Id"]);
    Assert.Equal("alice", existing["Name"]);
    Assert.Equal("preserved", existing["UnrelatedKey"]);
}

[Fact]
public void RoundTrip_PocoToExpandoToPoco_ProducesEquivalentObject()
{
    var mapper = new MapperConfiguration(_ => { }).CreateMapper();
    var original = new SimplePoco { Id = 42, Name = "alice" };
    var asExpando = mapper.Map<ExpandoObject>(original);
    var roundTripped = mapper.Map<SimplePoco>((IDictionary<string, object>)asExpando);
    Assert.Equal(42, roundTripped.Id);
    Assert.Equal("alice", roundTripped.Name);
}

private sealed class CustomerPoco { public string? Name { get; set; } }
private sealed class OrderPoco { public CustomerPoco? Customer { get; set; } }
private sealed class NumberListPoco { public List<int>? Numbers { get; set; } }
private sealed class OrderLinePoco { public string? Sku { get; set; } }
private sealed class OrderWithLinesPoco { public List<OrderLinePoco>? Lines { get; set; } }
private sealed class InventoryPoco { public Dictionary<string, OrderLinePoco>? Items { get; set; } }
private sealed class IntKeyedPoco { public Dictionary<int, OrderLinePoco>? ById { get; set; } }
private enum Status { Inactive = 0, Active = 1 }
private sealed class StatusPoco { public Status Status { get; set; } }
private sealed class ReadOnlyPropPoco
{
    public ReadOnlyPropPoco(string name) { Name = name; }
    public string Name { get; }
}
```

- [ ] **Step 8.2: Run new tests — verify they fail**

- [ ] **Step 8.3: Add `SerializeCollection<T>` and `SerializeDictionary<TKey, TValue>` to `MappingInvoker.cs`**

```csharp
public static List<object?>? SerializeCollection<T>(IEnumerable<T>? src, MapperRegistry registry)
{
    if (src is null) return null;
    var list = new List<object?>();
    foreach (var item in src)
        list.Add(SerializeValue(item, typeof(T), registry));
    return list;
}

public static IDictionary<string, object>? SerializeDictionary<TKey, TValue>(
    IDictionary<TKey, TValue>? src,
    MapperRegistry registry) where TKey : notnull
{
    if (src is null) return null;
    IDictionary<string, object> dst = new ExpandoObject();
    foreach (var kv in src)
        dst[kv.Key.ToString()!] = SerializeValue(kv.Value, typeof(TValue), registry)!;
    return dst;
}
```

- [ ] **Step 8.4: Extend `BuildPocoToDictLambda` per-property switch**

For each `PropertyMap`'s source-member type, classify and emit:

1. **Primitive / scalar / enum / nested POCO** — call `SerializeValue` (Task 7 path).
2. **Generic collection (`List<T>`, `T[]`, `IEnumerable<T>`)** — call `SerializeCollection<T>` via reflection on the element type.
3. **Generic dictionary (`Dictionary<TKey, TValue>` where TValue is a POCO)** — call `SerializeDictionary<TKey, TValue>` via reflection.

Implementation guidance: extract a private `EmitPocoToDictPropertyAssign(PropertyMap, MemberAccess, IndexExpression dst[key])` helper that returns the full per-property `Expression` block. Type-classify on `((PropertyInfo)pm.SourcePath[0]).PropertyType`.

- [ ] **Step 8.5: Implement `BuildPocoToDictUpdateLambda`**

Differs from `BuildPocoToDictLambda` only in: takes the destination dict from a parameter (not a freshly-allocated one). Other keys are preserved (the codegen only writes to keys it knows about).

Wire from `DynamicPlanBuilder.BuildUpdate` (added in Task 6's Step 6.5):

```csharp
public static LambdaExpression BuildUpdate(TypeMap typeMap, MapperRegistry registry)
{
    if (DynamicShape.IsDynamicShape(typeMap.SourceType))
        return BuildDictToPocoUpdateLambda(typeMap, registry);  // from Task 6
    else
        return BuildPocoToDictUpdateLambda(typeMap, registry);  // new in this task
}
```

- [ ] **Step 8.6: Run new tests — verify they pass**

Expected: `Passed: 17, Failed: 0` (7 from Task 7 + 10 new).

- [ ] **Step 8.7: Run full suite — verify no regression**

- [ ] **Step 8.8: Commit**

```pwsh
git add src/Atlas/Internal/DynamicPlanBuilder.cs src/Atlas/Internal/MappingInvoker.cs tests/Atlas.Tests/MapperPocoToDictTests.cs
git commit -m "POCO→Dict nested + collections + dicts + enums + update-in-place (Task 8)"
```

---

## Task 9 — Validator skip + Atlas.Projections rejection

**Goal:** Wire two single-line gates: `ConfigurationValidator` skips dynamic TypeMaps; `ProjectionPlanBuilder` rejects them with `AtlasProjectionException`.

**Files:**
- Modify: `C:\Repos\Atlas\src\Atlas\Internal\ConfigurationValidator.cs` — add `if (tm.IsDynamic) continue;` to the iteration in `Validate`
- Modify: `C:\Repos\Atlas\src\Atlas.Projections\Internal\ProjectionPlanBuilder.cs` — add `RejectDynamicOrThrow` mirror of `RejectHooksOrThrow`, called from `BuildBody`
- Create: `C:\Repos\Atlas\tests\Atlas.Tests\ConfigurationValidatorDynamicMappingTests.cs` — ~3 tests
- Create: `C:\Repos\Atlas\tests\Atlas.Projections.Tests\ProjectionRejectsDynamicMappingTests.cs` — ~2 tests

**Allowlist for the implementer subagent:** the four files above, no others.

- [ ] **Step 9.1: Write `ConfigurationValidatorDynamicMappingTests.cs` (failing)**

```csharp
namespace Atlas.Tests;

using System.Collections.Generic;
using Atlas;

public class ConfigurationValidatorDynamicMappingTests
{
    [Fact]
    public void AssertConfigurationIsValid_PassesWhenNoDynamicTypeMapsMaterialized()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<RegularA, RegularB>());
        cfg.AssertConfigurationIsValid();  // no exception
    }

    [Fact]
    public void AssertConfigurationIsValid_PassesAfterDynamicTypeMapMaterialized()
    {
        var cfg = new MapperConfiguration(_ => { });
        var mapper = cfg.CreateMapper();
        // Materialize a dynamic TypeMap by performing a map call:
        _ = mapper.Map<RegularA>(new Dictionary<string, object> { ["Id"] = 1 });

        // Validator should skip the dynamic TypeMap, no exception.
        cfg.AssertConfigurationIsValid();
    }

    [Fact]
    public void AssertConfigurationIsValid_DoesNotComplain_AboutUnmappedDestinationMembers_ForDynamicTypeMaps()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<RegularA, RegularB>(MemberList.Destination));
        var mapper = cfg.CreateMapper();
        // Materialize a dynamic TypeMap with destination members the validator would normally consider unmapped:
        _ = mapper.Map<RegularC>(new Dictionary<string, object> { });

        cfg.AssertConfigurationIsValid();  // no exception
    }

    private sealed class RegularA { public int Id { get; set; } }
    private sealed class RegularB { public int Id { get; set; } }
    private sealed class RegularC
    {
        public int X { get; set; }
        public string? Y { get; set; }
    }
}
```

- [ ] **Step 9.2: Write `ProjectionRejectsDynamicMappingTests.cs` (failing)**

```csharp
namespace Atlas.Projections.Tests;

using System.Collections.Generic;
using System.Linq;
using Atlas;
using Atlas.Projections;

public class ProjectionRejectsDynamicMappingTests
{
    [Fact]
    public void ProjectTo_FromDynamicShapeQueryable_ThrowsAtlasProjectionException()
    {
        var cfg = new MapperConfiguration(_ => { });
        var queryable = new List<IDictionary<string, object>>().AsQueryable();
        var ex = Assert.Throws<AtlasProjectionException>(
            () => queryable.ProjectTo<TargetPoco>(cfg).ToList());
        Assert.Contains("dynamic", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProjectTo_NonDynamicMappings_StillWork()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<SourcePoco, TargetPoco>());
        var queryable = new List<SourcePoco> { new() { Id = 1 } }.AsQueryable();
        var result = queryable.ProjectTo<TargetPoco>(cfg).ToList();
        Assert.Single(result);
        Assert.Equal(1, result[0].Id);
    }

    private sealed class SourcePoco { public int Id { get; set; } }
    private sealed class TargetPoco { public int Id { get; set; } }
}
```

- [ ] **Step 9.3: Run new tests — verify they fail**

```pwsh
dotnet test --nologo 2>&1 | Select-String -Pattern "DynamicMapping|RejectsDynamic"
```

Expected: `ConfigurationValidatorDynamicMappingTests` may pass already (validator iteration may walk only registered closed maps and not see the dynamic ones — depends on implementation). `ProjectionRejectsDynamicMappingTests.ProjectTo_FromDynamicShapeQueryable_ThrowsAtlasProjectionException` should fail (no rejection wired yet).

- [ ] **Step 9.4: Add validator skip in `ConfigurationValidator.cs`**

In `C:\Repos\Atlas\src\Atlas\Internal\ConfigurationValidator.cs` at the top of the per-TypeMap loop in `Validate` (line 19), add:

```csharp
foreach (var tm in registry.AllTypeMaps)
{
    if (tm.IsDynamic) continue;     // Atlas v2 #10 — dynamic TypeMaps are convention-only

    // ... existing per-TypeMap validation logic ...
}
```

- [ ] **Step 9.5: Add `RejectDynamicOrThrow` in `ProjectionPlanBuilder.cs`**

In `C:\Repos\Atlas\src\Atlas.Projections\Internal\ProjectionPlanBuilder.cs`, after the existing `RejectHooksOrThrow` method (line 328), add:

```csharp
private static void RejectDynamicOrThrow(TypeMap tm)
{
    if (!tm.IsDynamic) return;
    throw new AtlasProjectionException(new List<ProjectionDiagnostic>
    {
        new(tm.SourceType, tm.DestinationType, "(Dynamic mapping)",
            $"map is a dynamic-shape mapping ({tm.SourceType} → {tm.DestinationType}); " +
            $"LINQ providers cannot translate runtime dictionary key lookups against arbitrary keys. " +
            $"Use mapper.Map<>() instead.")
    });
}
```

In `BuildBody` at line 25 (just below the existing `RejectHooksOrThrow(tm)` call), add:

```csharp
RejectDynamicOrThrow(tm);
```

- [ ] **Step 9.6: Run new tests — verify they pass**

Expected: `Passed: 5, Failed: 0` across the two new files.

- [ ] **Step 9.7: Run full suite — verify no regression**

- [ ] **Step 9.8: Commit**

```pwsh
git add src/Atlas/Internal/ConfigurationValidator.cs src/Atlas.Projections/Internal/ProjectionPlanBuilder.cs tests/Atlas.Tests/ConfigurationValidatorDynamicMappingTests.cs tests/Atlas.Projections.Tests/ProjectionRejectsDynamicMappingTests.cs
git commit -m "Validator skip + Atlas.Projections rejection for dynamic TypeMaps (Task 9)"
```

---

## Task 10 — Integration tests (NameComparison, transformers, threading, edge cases)

**Goal:** Cover the remaining behavior surface in one integration test file: `NameComparison` toggle (case-sensitive default + opt-in case-insensitive), global vs profile transformer composition, threading safety, collection-of-dynamic recursion, inheritance inert, non-public properties not emitted.

**Files:**
- Create: `C:\Repos\Atlas\tests\Atlas.Tests\MapperDynamicMappingIntegrationTests.cs` — ~10 tests

**Allowlist for the implementer subagent:** the one file above, no others. (NO production-code changes for this task — if a test fails because production needs a fix, escalate via DONE_WITH_CONCERNS.)

- [ ] **Step 10.1: Write `MapperDynamicMappingIntegrationTests.cs` (some passing, some failing — see notes)**

```csharp
namespace Atlas.Tests;

using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Threading.Tasks;
using Atlas;

public class MapperDynamicMappingIntegrationTests
{
    [Fact]
    public void NameComparison_CaseSensitiveDefault_LowerCaseDictKeyDoesNotPopulateMixedCaseProperty()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var dict = new Dictionary<string, object> { ["age"] = 42 };
        var p = mapper.Map<AgePoco>(dict);
        Assert.Equal(0, p.Age);  // case-sensitive default; "age" doesn't match "Age"
    }

    [Fact]
    public void NameComparison_OptInCaseInsensitive_LowerCaseDictKeyPopulatesMixedCaseProperty()
    {
        var mapper = new MapperConfiguration(c => c.CaseSensitive = false).CreateMapper();
        var dict = new Dictionary<string, object> { ["age"] = 42 };
        var p = mapper.Map<AgePoco>(dict);
        Assert.Equal(42, p.Age);
    }

    [Fact]
    public void DotNotationPrefixScan_RespectsCaseInsensitiveSetting()
    {
        var mapper = new MapperConfiguration(c => c.CaseSensitive = false).CreateMapper();
        var dict = new Dictionary<string, object> { ["customer.name"] = "alice" };
        var p = mapper.Map<OrderPoco>(dict);
        Assert.Equal("alice", p.Customer!.Name);
    }

    [Fact]
    public void GlobalScopeValueTransformer_FiresDuringDictToPocoConversion()
    {
        var mapper = new MapperConfiguration(c =>
            c.ValueTransformers.Add<string>(s => s == null ? null! : s.Trim())).CreateMapper();
        var dict = new Dictionary<string, object> { ["Name"] = "  alice  " };
        var p = mapper.Map<NamePoco>(dict);
        Assert.Equal("alice", p.Name);
    }

    [Fact]
    public void ProfileScopeValueTransformer_DoesNotFireForDynamicTypeMap()
    {
        // Per design §7.4: dynamic TypeMaps are global-scope only.
        var mapper = new MapperConfiguration(c => c.AddProfile(new TrimmingProfile())).CreateMapper();
        var dict = new Dictionary<string, object> { ["Name"] = "  alice  " };
        var p = mapper.Map<NamePoco>(dict);
        Assert.Equal("  alice  ", p.Name);  // NOT trimmed — profile transformer didn't fire
    }

    [Fact]
    public async Task ConcurrentMapCalls_DoNotDuplicateMaterialization()
    {
        var cfg = new MapperConfiguration(_ => { });
        var mapper = cfg.CreateMapper();

        var tasks = Enumerable.Range(0, 16).Select(i => Task.Run(() =>
            mapper.Map<NamePoco>(new Dictionary<string, object> { ["Name"] = $"user{i}" }))).ToArray();

        var results = await Task.WhenAll(tasks);
        Assert.Equal(16, results.Length);
        Assert.All(results, r => Assert.NotNull(r.Name));
    }

    [Fact]
    public void CollectionOfDynamic_RecursesIntoDynamicElementMap()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var src = new List<IDictionary<string, object>>
        {
            new Dictionary<string, object> { ["Name"] = "alice" },
            new Dictionary<string, object> { ["Name"] = "bob" }
        };
        var result = mapper.Map<List<IDictionary<string, object>>, List<NamePoco>>(src);
        Assert.Equal(2, result.Count);
        Assert.Equal("bob", result[1].Name);
    }

    [Fact]
    public void CollectionOfPocoToDynamic_RecursesIntoDynamicElementMap()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var src = new List<NamePoco> { new() { Name = "alice" }, new() { Name = "bob" } };
        var result = mapper.Map<List<NamePoco>, List<ExpandoObject>>(src);
        Assert.Equal(2, result.Count);
        Assert.Equal("alice", ((IDictionary<string, object>)result[0])["Name"]);
    }

    [Fact]
    public void Inheritance_IsInert_ForDynamicTypeMaps()
    {
        // A registered Include<Base, Derived> should not influence a separate dynamic Map<Derived>(dict) call.
        var mapper = new MapperConfiguration(c =>
        {
            c.CreateMap<BasePoco, BaseDto>().Include<DerivedPoco, DerivedDto>();
            c.CreateMap<DerivedPoco, DerivedDto>();
        }).CreateMapper();

        var dict = new Dictionary<string, object> { ["BaseName"] = "alice", ["DerivedName"] = "bob" };
        var p = mapper.Map<DerivedPoco>(dict);
        Assert.Equal("alice", p.BaseName);
        Assert.Equal("bob", p.DerivedName);
    }

    [Fact]
    public void NonPublicProperties_AreNotEmittedOnPocoToDict()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var p = new PocoWithPrivateGetter("alice", "secret");
        var d = mapper.Map<Dictionary<string, object>>(p);
        Assert.True(d.ContainsKey("Public"));
        Assert.False(d.ContainsKey("Internal"));
        Assert.False(d.ContainsKey("Private"));
    }

    private sealed class AgePoco { public int Age { get; set; } }
    private sealed class CustomerPoco { public string? Name { get; set; } }
    private sealed class OrderPoco { public CustomerPoco? Customer { get; set; } }
    private sealed class NamePoco { public string? Name { get; set; } }
    private class BasePoco { public string? BaseName { get; set; } }
    private class DerivedPoco : BasePoco { public string? DerivedName { get; set; } }
    private class BaseDto { public string? BaseName { get; set; } }
    private class DerivedDto : BaseDto { public string? DerivedName { get; set; } }

    private sealed class PocoWithPrivateGetter
    {
        public PocoWithPrivateGetter(string pub, string priv) { Public = pub; Private = priv; Internal = priv; }
        public string Public { get; }
        internal string Internal { get; }
        private string Private { get; }
    }

    private sealed class TrimmingProfile : MapperProfile
    {
        public TrimmingProfile()
        {
            ValueTransformers.Add<string>(s => s == null ? null! : s.Trim());
        }
    }
}
```

- [ ] **Step 10.2: Run tests — verify pass/fail breakdown**

```pwsh
dotnet test tests/Atlas.Tests/Atlas.Tests.csproj --filter FullyQualifiedName~MapperDynamicMappingIntegrationTests --nologo
```

Expected: `Passed: 10, Failed: 0`. Most tests should pass without production-code changes if Tasks 1-9 were implemented per spec. **If `ProfileScopeValueTransformer_DoesNotFireForDynamicTypeMap` fails (i.e., the profile transformer DID fire),** revisit Task 3's `MaterializeTypeMap`: it must pass `globalTransformers` only (not `profile.ValueTransformers`) into the `TransformerResolver.Resolve` call. **If `ConcurrentMapCalls_DoNotDuplicateMaterialization` fails,** revisit Task 3's `_typeMaps.GetOrAdd` use — `ConcurrentDictionary.GetOrAdd` is the correct API and was already in place from #9.

- [ ] **Step 10.3: Run full suite — verify no regression**

- [ ] **Step 10.4: Commit**

```pwsh
git add tests/Atlas.Tests/MapperDynamicMappingIntegrationTests.cs
git commit -m "Integration tests: NameComparison, transformers, threading, edge cases (Task 10)"
```

---

## Task 11 — README + final coverage check

**Goal:** Add the README section (mirror the OpenGenerics section style); confirm coverage targets met; final-pass full test count.

**Files:**
- Modify: `C:\Repos\Atlas\README.md` — add "Dynamic / dictionary mapping" section, remove #10 from deferred list

**Allowlist for the implementer subagent:** the one file above. (No production-code changes.)

- [ ] **Step 11.1: Add README section**

In `C:\Repos\Atlas\README.md`, after the "Open Generics" section and before the deferred-features list, insert:

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
- Honors the configuration's case-sensitivity setting for both top-level key match and dot-notation prefix scan.
- Missing keys leave the destination at `default(T)` for fresh `Map`; preserve existing for update-in-place `Map(src, existing)`.
- Excess dict keys silently ignored.
- Nested POCOs read from nested-dict values OR from dot-notation keys (`"Customer.Email"`); top-level wins.
- Nested POCOs emit as nested `ExpandoObject` regardless of outer destination shape.
- Enums emit as underlying integer; read via `Convert.ChangeType`.
- `Atlas.Projections` rejects dynamic-shape mappings (LINQ providers can't translate
  arbitrary key lookups).
- Self-pair round-trips (`ExpandoObject → ExpandoObject`, etc.) require an explicit `CreateMap` registration.
- Profile-scoped value transformers do NOT fire on dynamic TypeMaps; only global-scope transformers compose.

See `docs/Atlas-Design-DynamicMapping.md` for the full specification.
```

In the deferred-features list further down in the README, remove `#10. Dynamic / ExpandoObject / Dictionary<string, object> mapping` (or mark it as shipped, matching the convention used for prior shipped features).

- [ ] **Step 11.2: Run full test suite — verify total count**

```pwsh
dotnet test --nologo 2>&1 | Select-String -Pattern "Passed:|Failed:|Skipped:"
```

Expected: total `Passed: ~559` (489 baseline + ~70 new); `Failed: 0`. **Implementer reports the actual count** — plan-arithmetic-drift discipline says don't block on a 1-4 test mismatch.

- [ ] **Step 11.3: Verify coverage targets**

```pwsh
dotnet test --nologo --collect:"XPlat Code Coverage" --settings tests/coverlet.runsettings 2>&1 | Select-String -Pattern "Atlas|Atlas.Projections"
```

(Or whatever the project's existing coverage command is — check `Directory.Build.props` and existing CI scripts at implementation time.)

Expected: line coverage ≥ 90%, branch coverage ≥ 80% on `Atlas` and `Atlas.Projections` assemblies. **Specifically check the new files** — `DynamicShape.cs`, `DynamicPlanBuilder.cs`, the new `MappingInvoker` helpers, the `MapperRegistry.GetTypeMap` extension, the `TypeMap.IsDynamic` plumbing — these should be fully covered by the new test files. If any branch is uncovered, add a regression test before declaring done.

- [ ] **Step 11.4: Commit**

```pwsh
git add README.md
git commit -m "docs: README — add dynamic/dictionary mapping section, remove from deferred list (Task 11)"
```

---

## Final review (controller, before opening the PR)

After all 12 tasks (0–11) are complete and committed:

- [ ] **Verify branch state**

```pwsh
git log --oneline main..HEAD
```

Expected: ~12 commits (one per task, plus the design commit which already landed on `main` and is NOT in this branch's diff). Verify each commit message names its task.

- [ ] **Run full test suite, full pass**

```pwsh
dotnet test --nologo 2>&1 | Select-String -Pattern "Passed:|Failed:|Skipped:"
```

Expected: `Passed: ~559, Failed: 0, Skipped: 0`. Record the exact count in the PR body.

- [ ] **Dispatch holistic code review**

Use `superpowers:code-reviewer` against the entire branch's diff. Per memory feedback (`feedback_atlas_v2_workflow.md`): "**Don't skip the holistic review under any circumstances.** ConditionalMapping (#7), NullSubstitution (#8), and OpenGenerics (#9) are three-in-a-row counter-examples proving 'clean holistic' is achievable when the cross-package consumer audit and scope discipline are correctly applied AND when per-task reviewers are skeptical of test deviations."

Specific holistic-review focus areas for #10:
1. **Cross-package consumer audit (Bug-4 lesson):** verify `Atlas.Projections` rejects dynamic TypeMaps before walking PMs (Task 9). Verify `ConfigurationValidator` skip happens BEFORE any per-PM validation (Task 9).
2. **Scope-identifying metadata propagation (Bug-5 lesson):** verify dynamic TypeMaps consistently set `OriginatingProfile = null` (Task 3). Verify no path materializes a dynamic TypeMap with a non-null `OriginatingProfile`.
3. **Coalesce/Nullable interaction (Bug-6 lesson):** dict→POCO codegen for `int?` destinations from `long` source values — confirm `ConvertObjectTo<int?>(42L)` correctly produces `int?(42)`, not throws or returns null. (This exercises the asymmetric-nullable widening branches added in #8.) Spot-check via running the suite filtered to `MapperDictToPocoTests.Map_DictWithLongValue_WidensToInt_NumericConversion` and similar.
4. **Test deviation scrutiny:** if any implementer's self-review notes a test deviation from the plan's prescribed assertion, trace through why — per the NullSubstitution Task 8 lesson. Don't accept "it worked" without understanding what code path is exercised.
5. **Self-pair routing:** verify `(Dictionary<string, object>, Dictionary<string, object>)`, `(ExpandoObject, ExpandoObject)`, `(IDictionary<string, object>, IDictionary<string, object>)`, and mixed-shape pairs all behave per design §7.2 (require explicit registration; XOR detector does NOT fire). Verify via `MapperRegistryDynamicMappingTests.GetTypeMap_DoesNotFire_WhenBothSidesDynamic`.

- [ ] **Address any holistic-review findings**

Per memory: "Final-review minor follow-ups (README cleanup, validator gaps surfaced during the holistic review) get folded into the feature PR before merge — not deferred to a separate cleanup commit."

- [ ] **Push branch, open PR**

```pwsh
git push -u origin feat/dynamic-mapping
gh pr create --base main --title "feat: dynamic / dictionary / ExpandoObject mapping (#10)" --body @"
Implements Atlas v2 deferred feature #10. See docs/Atlas-Design-DynamicMapping.md for the full specification.

## Summary

Convention-only mapping between strongly-typed POCOs and three recognized dynamic shapes (`IDictionary<string, object>`, `ExpandoObject`, `Dictionary<string, object>`) with zero new fluent surface. Lazy materialization via single insertion point in `MapperRegistry.GetTypeMap` (stage 3, after closed-pair cache and open-generic template scan). Mirrors OpenGenerics' (#9) lazy-materialization architecture.

## Test count

- Baseline (post-#9): 489 PASS
- This PR: <ACTUAL_COUNT> PASS (≈ +70 net)

## Files changed

- 2 new production files (`DynamicShape.cs`, `DynamicPlanBuilder.cs`)
- 7 modified production files (`TypeMap.cs`, `PropertyMap.cs`, `MapperRegistry.cs`, `ExecutionPlanBuilder.cs`, `MappingInvoker.cs`, `ConfigurationValidator.cs`, `ProjectionPlanBuilder.cs`)
- 7 new test files
- 1 modified doc (`README.md`)

## Holistic review

Clean (or list any findings folded back into this PR before merge).

🤖 Generated with [Claude Code](https://claude.com/claude-code)
"@
```

(Adjust the `gh pr create` command for PowerShell heredoc syntax — likely use a `.txt` file body or `--body` with escaped string.)

---

## Summary

**Total tasks:** 12 (Task 0 through Task 11) plus final review.

**Total checkboxes:** ~80 across all task steps.

**Estimated wall-clock (per memory's `~6 hours per feature` baseline):** 5–7 hours for subagent-driven execution. Major uncertainty on Task 4 (dict→POCO primitive codegen) and Task 7 (POCO→dict primitive codegen) — these are the foundation-laying tasks that subsequent tasks build on; if either misfires, downstream tasks may need rework.

**Model selection guidance** (per `superpowers:subagent-driven-development` model-selection table):

| Task | Suggested model | Rationale |
|---|---|---|
| 0 | controller-only | branch setup |
| 1 | haiku | mechanical: 2 predicates, 9 trivial tests |
| 2 | haiku | mechanical: 2 fields + 2 factories, 3 tests |
| 3 | sonnet | integration: synthesizing TypeMap + PMs, lifecycle (Seal, OriginatingProfile, transformers) |
| 4 | sonnet | algorithm-heavy: codegen branching + runtime helper coordination |
| 5 | sonnet | algorithm-heavy: nested-POCO branch + dot-notation prefix scan + collection element-mapping |
| 6 | sonnet | integration: ctor-using POCOs + update-in-place semantics |
| 7 | sonnet | algorithm-heavy: POCO→dict codegen + concrete-type contract |
| 8 | sonnet | algorithm-heavy: nested + collections + dictionary recursion + enums |
| 9 | haiku | mechanical: 2 single-line gates + 5 tests |
| 10 | haiku | mostly tests-only; production unchanged |
| 11 | haiku | docs-only |

(Cross-task review: spec reviewer + code-quality reviewer per task per memory's review-catch frequency baseline.)

---

## Implementation notes

### Cross-task stub-and-replace pattern (Task 3 → Task 4 dependency)

Task 3 wires `MapperRegistry.GetTypeMap` stage 3 to call `DynamicShape.MaterializeTypeMap`, which produces a sealed dynamic TypeMap. **Tests in Task 3 only inspect the TypeMap; they do NOT call `mapper.Map<>()` — codegen is wired in Task 4.** If Task 3's tests accidentally call `mapper.Map<>()` against a freshly-materialized dynamic TypeMap, the call goes through `ExecutionPlanBuilder.Build` which (in Task 3, before Task 4) doesn't yet branch on `IsDynamic` — it would fall through to `BuildPocoLambda`, which would generate broken codegen for an `IDictionary<string, object>` destination. **Discipline: Task 3 tests assert via `_typeMaps[pair]` reflective access or `GetTypeMap`'s return value, never via `Map<>`.**

### Cross-task stub-and-replace pattern (Task 7 → Task 8 dependency)

Task 7 implements POCO→dict codegen for primitive properties only. Nested POCOs, collections, dictionaries, and enums fall through to a runtime exception inside `BuildPocoToDictLambda`'s per-property switch (not yet handled). **Tests in Task 7 use POCOs with primitive properties only.** Task 8 extends the switch to handle the rest.

### Test-with-deferred-greening pattern (none in this plan)

Unlike OpenGenerics Task 3 (which wrote a test that intentionally failed until Task 4 greened it), this plan's tests pass at the end of their own task. No deferred-greening tests.

### Closed-pair-takes-precedence v1 limitation

Per design §7.5: explicit `CreateMap<MyPoco, IDictionary<string, object>>()` registers a non-dynamic TypeMap that produces broken codegen for the dynamic destination type. Task 3's `GetTypeMap_ExplicitClosedRegistration_TakesPrecedenceOverDetector` test confirms the precedence rule (the explicit TypeMap wins, the detector does not fire) but does NOT exercise codegen against the explicit TypeMap. This v1 limitation is documented in the README; v3 follow-up adds first-class fluent customization for dynamic TypeMaps.

### Bug audit reminders

Apply Bug-4 / Bug-5 / Bug-6 lesson rigor at each task with a "shared shape" change:

- **Task 2 (PropertyMap.DynamicKey field)** — Bug-4 lesson: grep every consumer of `PropertyMap` (`ExecutionPlanBuilder`, `ProjectionPlanBuilder`, `ConfigurationValidator`, `InheritanceMerger`, `TransformerResolver`, `ReverseMapMirror`) and verify each one either (a) is short-circuited by `tm.IsDynamic` BEFORE walking PMs, or (b) handles `pm.DynamicKey != null` correctly. Spec reviewer enforces this audit.
- **Task 3 (TypeMap.IsDynamic field + materialization factory)** — Bug-5 lesson: dynamic TypeMaps don't have sibling/derived/reverse pairs, so `OriginatingProfile = null` is correct. Verify that `ReverseMapMirror.Mirror` (existing v1 helper) doesn't fire on dynamic TypeMaps (they're never registered via `CreateMap`, so they have no `ReverseMapPair`).
- **Task 4 (Dict→POCO codegen with `Coalesce`-like value extraction)** — Bug-6 lesson: dict→POCO does NOT emit `Expression.Coalesce` over `Nullable<T>` source — the source is `object`, never `Nullable<T>`. So Bug-6's specific failure mode doesn't reproduce. But the asymmetric-nullable widening branches added in #8 to `ConvertOrMap`/`ConvertOrInline` ARE consumed via `ConvertObjectTo<T>` for cases like `int? destination + long source value`. Verify by running `MapperDictToPocoTests.Map_DictWithLongValue_WidensToInt_NumericConversion` and a similar test for `int? Age` from `long`.

---

## Test plan (categorized recap)

| File | Direction | Count | Subject |
|---|---|---|---|
| `DynamicShapeTests.cs` | both | ~9 | Predicates `IsDynamicShape` + `IsDynamicPair` (positive, negative, theory cases) |
| `MapperRegistryDynamicMappingTests.cs` | both | ~7 | Materialization + caching + closed-pair precedence + non-firing for self-pairs |
| `MapperDictToPocoTests.cs` | dict→POCO | ~25 | Primitives + nested + dot-notation + collections + ctor + update-in-place |
| `MapperPocoToDictTests.cs` | POCO→dict | ~17 | Primitives + concrete-type contract + nested + collections + dicts + enums + update-in-place |
| `MapperDynamicMappingIntegrationTests.cs` | both | ~10 | NameComparison, transformers, threading, collection-of-dynamic, inheritance inert, non-public |
| `ConfigurationValidatorDynamicMappingTests.cs` | both | ~3 | Validator skip rule (passes with/without dynamic TypeMaps materialized) |
| `ProjectionRejectsDynamicMappingTests.cs` | both | ~2 | Projection rejection with `AtlasProjectionException` + non-dynamic regression |
| `Internal/PropertyMapDynamicFactoryTests.cs` | n/a | ~3 | New factories `ForDictKey`/`ForPocoSource`; default `DynamicKey == null` |
| **TOTAL** | | **~76** | (vs. design's "~50–60" — the actual count is on the higher end since the dict→POCO direction has more rich coverage paths) |

Coverage targets: line ≥ 90%, branch ≥ 80% on the changed files. Has held on every prior feature.

---

## Implementer notes (per-task ground rules)

These rules apply to every implementer-subagent dispatched against this plan:

1. **Read the design first.** Open `C:\Repos\Atlas\docs\Atlas-Design-DynamicMapping.md` and read at minimum the sections referenced in your task's description. The design is authoritative — when this plan disagrees with the design, follow the design.

2. **Stay inside the allowlist.** Each task lists exactly which files you may create or modify. **Files outside the allowlist are off-limits unless you escalate via DONE_WITH_CONCERNS.** Per memory's "common over-reach" pattern from ReverseMap and Hooks, three documented cases of unauthorized changes have happened — the spec reviewer will catch them, but escalation is faster.

3. **Disclose test deviations.** Per memory's "undisclosed test deviation pattern" (Hooks Task 10) and "test deviation scrutiny" (NullSubstitution Task 8): if the planned test text doesn't match what the production code produces, **do NOT silently fix the test text**. Report DONE_WITH_CONCERNS naming the discrepancy. The discrepancy may be a real production bug (NullSubstitution Task 8 caught one this way).

4. **Use plain `Assert.X()` only.** No FluentAssertions per `feedback_no_fluentassertions` memory. xUnit v3's `Assert.NotNull`, `Assert.Equal`, `Assert.Same`, `Assert.IsType<T>`, `Assert.Throws<T>`, `Assert.Single`, `Assert.Contains`, `Assert.True`/`False`, `Assert.Null`, `Assert.All`, etc.

5. **Run tests at every step.** TDD discipline: write failing test, see it fail, write minimum implementation, see it pass, run full suite, commit.

6. **One commit per task.** Each task ends in a single commit. Don't squash; don't split.

7. **Match existing code style.** Follow the conventions in the file you're editing. Look at the existing code's brace style, naming, comment density, indentation. Atlas uses C# 14 preview, file-scoped namespaces, expression-bodied members where natural, XML doc-comments on public/internal types.

8. **Plan-arithmetic drift is fine.** If your task ends with a different test count than the plan predicts (off by 1-4), report the actual count and continue. Don't churn the plan doc to match.

9. **Verify Atlas APIs at first reference.** This plan references `IMapper.Map(object, Type, Type)` and `MapperConfigurationExpression.CaseSensitive` — verify these exist with the exact signatures during implementation; adapt if they differ. The Explore agent's structural inventory has line numbers but you should re-verify before relying on a property name or method signature.

10. **Branch state checkpoint before re-dispatch.** Per memory's "Detached-HEAD incident": if a task fails midway and the controller re-dispatches an implementer for the fix, the controller verifies branch state with `git log --oneline -5` first. If the latest commit isn't what the prior implementer reported, the prior commit may have been orphaned in detached-HEAD; check reflog.
