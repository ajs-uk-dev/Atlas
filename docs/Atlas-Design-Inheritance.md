# Plan: Atlas v2 — Inheritance & Polymorphism

> **Status:** Approved design, ready to implement.
> **Depends on:** `Atlas` v1 (already shipped per `docs/Atlas-Design.md`, 113 tests green after `Atlas.Projections` adds `NumericConversions` extraction).
> **Output of this doc:** Inheritance dispatch and member-config inheritance added to the core `Atlas` package. No new packages.

---

## 1. Goals & Non-Goals

### 1.1 Goals
- `mapper.Map<TBaseDest>(actuallyADerivedSource)` and `mapper.Map<TBaseSrc, TBaseDest>(derivedSrc)` both dispatch to the most-specific registered map at runtime.
- Polymorphic collections work: `List<Animal>` containing a `Dog` and a `Cat` maps element-by-element to a `List<AnimalDto>` containing a `DogDto` and a `CatDto`.
- Member configuration on a base map flows to derived maps with AutoMapper's §6.3 precedence (derived explicit > base explicit > derived convention).
- Two fluent declaration sites: `.Include<TDerivedSrc, TDerivedDst>()` on the base map, and `.IncludeBase<TBaseSrc, TBaseDst>()` on the derived map. Same data, two ergonomic surfaces.
- Validator catches every common misconfiguration up-front with one aggregated exception.
- Zero performance impact on maps that don't use inheritance (no `is`-tests in the lambda when `IncludedDerived` is empty).
- All changes are **additive** — no public type signatures change shape; no existing test should regress.

### 1.2 Non-Goals (explicit out-of-scope; future design docs)
- **`Atlas.Projections` integration.** Today's projection package is unaware of `IncludedDerived`. A `query.ProjectTo<AnimalDto>(cfg)` against a polymorphic `DbSet<Animal>` projects every row as the base shape. That's a known limitation, documented in this design's §10. A future v3 design ("ProjectTo + inheritance") will add discriminator-aware projection emission; the data this design lays down on `TypeMap` is already projection-ready.
- `IncludeAllDerived()` — convenience scan that auto-includes every derived map. Defer until evidence of demand.
- `As<TDerivedDest>()` — point-at-existing-map shortcut. Defer.
- Annotation-driven dispatch (Mapperly-style `[MapDerivedType]`). Conflicts with Atlas's fluent-only configuration approach; no plan to add.
- Constructor-arg inheritance for record destinations. v1 inheritance ships for class destinations with property setters; record inheritance via constructor args is a separate (small) follow-up if needed.

---

## 2. Architecture Overview

```
┌────────────────────────────────────────────────────────────┐
│                        Atlas (core)                        │
│                                                            │
│  Configuration phase:                                      │
│    MappingExpression.Include<TDS, TDD>()                   │
│      → typeMap.IncludedDerived.Add(...)                    │
│    MappingExpression.IncludeBase<TBS, TBD>()               │
│      → typeMap.IncludedBases.Add(...)                      │
│                                                            │
│  Build phase (in MapperConfiguration ctor):                │
│    1. ResolveInheritance pass:                             │
│       - propagate IncludedBases entries into the           │
│         corresponding base TypeMap's IncludedDerived list  │
│       - merge base PropertyMaps into derived (§6.2)        │
│       - sort each IncludedDerived list most-derived-first  │
│    2. Existing ConventionEngine.ResolveMissingMembers      │
│    3. Existing TypeMap.Seal()                              │
│                                                            │
│  Runtime phase:                                            │
│    ExecutionPlanBuilder.Build(typeMap, registry):          │
│      if typeMap.IncludedDerived.Count > 0:                 │
│        emit prologue: chain of Conditional(is TDerived)    │
│          each branch calls MappingInvoker.Invoke<TDS,TDD>  │
│        fall-through: existing per-base codegen             │
│      else: existing codegen unchanged                      │
└────────────────────────────────────────────────────────────┘
```

The change is **purely additive** to v1. No existing public type changes shape. No new packages. The `Atlas.Projections` package is untouched.

---

## 3. Solution & Project Layout

No new projects. Files modified:

```
src/Atlas/
  Configuration/
    IMappingExpression.cs           ← add Include + IncludeBase methods
    MappingExpression.cs            ← implement them
  Internal/
    TypeMap.cs                      ← add IncludedDerived + IncludedBases lists
    PropertyMap.cs                  ← add IsExplicit flag (~3 lines)
    InheritanceMerger.cs            ← NEW: MergeBaseConfig + ResolveInheritance pass
    ExecutionPlanBuilder.cs         ← prologue emission for IncludedDerived
    ConfigurationValidator.cs       ← three new rules (§7.3)
    MappingInvoker.cs               ← guard the Unsafe.As short-circuit (§9 #5)
  MapperConfiguration.cs            ← call InheritanceMerger.ResolveInheritance before seal

tests/Atlas.Tests/
  Internal/
    InheritanceMergerTests.cs       ← NEW: 8 tests (§8.1)
    ExecutionPlanBuilderInheritanceTests.cs  ← NEW: 8 tests (§8.4)
  MapperConfigurationInheritanceTests.cs  ← NEW: 8 tests (§8.2)
  ValidationInheritanceTests.cs     ← NEW: 6 tests (§8.3)
  MapperInheritanceTests.cs         ← NEW: 10 tests (§8.5)
```

No changes to `Directory.Packages.props`, `Atlas.slnx`, csproj files, or Atlas.Projections.

---

## 4. Public API Additions

```csharp
namespace Atlas.Configuration;

public interface IMappingExpression<TSource, TDestination>
{
    // ... existing methods (ForMember, ForCtorParam, ConvertUsing, etc.) ...

    /// <summary>
    /// Declares that <typeparamref name="TDerivedSource"/> (which must derive from
    /// <typeparamref name="TSource"/>) should map to <typeparamref name="TDerivedDestination"/>
    /// (which must derive from <typeparamref name="TDestination"/>) when the runtime source
    /// is the derived type. The derived map must be registered separately via its own
    /// <c>CreateMap&lt;TDerivedSource, TDerivedDestination&gt;()</c> call.
    /// </summary>
    /// <remarks>
    /// At runtime, the compiled lambda for the base map starts with an inline type-test
    /// chain: any registered derived dispatch is checked before the base body runs.
    /// </remarks>
    IMappingExpression<TSource, TDestination> Include<TDerivedSource, TDerivedDestination>()
        where TDerivedSource : TSource
        where TDerivedDestination : TDestination;

    /// <summary>
    /// Declares that this map participates in the runtime dispatch of a base map and inherits
    /// member configuration from it. Equivalent to declaring
    /// <c>.Include&lt;TSource, TDestination&gt;()</c> on the base map — useful when the base
    /// map lives in a different profile.
    /// </summary>
    IMappingExpression<TSource, TDestination> IncludeBase<TBaseSource, TBaseDestination>()
        where TSource : TBaseSource
        where TDestination : TBaseDestination;
}
```

Both methods return `this` for chaining. Generic constraints catch obvious mistakes at compile time. The validator (§7.3) covers cases that bypass the constraint (e.g. reflection-driven map registration).

No changes to `IMapper`, `MapperConfiguration`, `MapperConfigurationExpression`, `MapperProfile`, or any other public type.

---

## 5. Internal Architecture

### 5.1 `TypeMap` data model additions

```csharp
internal sealed class TypeMap
{
    // ... existing fields ...

    /// <summary>
    /// (TDerivedSource, TDerivedDestination) pairs declared via <c>Include</c> on this map,
    /// or via <c>IncludeBase</c> on a derived map (resolved into this list at config-build time).
    /// Sorted most-derived-first after <see cref="Seal"/>. Empty when inheritance isn't used.
    /// </summary>
    public List<TypePair> IncludedDerived { get; } = new();

    /// <summary>
    /// (TBaseSource, TBaseDestination) pairs declared via <c>IncludeBase</c> on this map.
    /// Used at config-build time to propagate this pair into each base's
    /// <see cref="IncludedDerived"/>, and to merge base config into this map's
    /// <see cref="PropertyMaps"/>.
    /// </summary>
    public List<TypePair> IncludedBases { get; } = new();
}
```

Both mutable until `Seal()` is called (same pattern as `PropertyMaps`). After seal, both are effectively read-only.

### 5.2 `PropertyMap` additions

```csharp
internal sealed class PropertyMap
{
    // ... existing fields ...

    /// <summary>
    /// True when this binding was configured via <c>ForMember</c> / <c>ForCtorParam</c>
    /// (an explicit user choice). False when populated by <c>ConventionEngine</c>.
    /// Used as the precedence discriminator during inheritance merge.
    /// </summary>
    public bool IsExplicit { get; set; }
}
```

`MappingExpression.ForMember` / `ForCtorParam` set `IsExplicit = true` on the binding they create or modify. `ConventionEngine` leaves `IsExplicit = false`.

### 5.3 `MapperConfiguration` ctor — new pass

```csharp
public MapperConfiguration(MapperConfigurationExpression expression)
{
    // ... existing setup ...

    var typeMaps = expression.GetTypeMaps().ToList();
    var pairIndex = typeMaps.ToDictionary(t => t.Pair);

    // NEW: resolve inheritance BEFORE convention resolution and Seal.
    InheritanceMerger.Resolve(typeMaps, pairIndex);

    // existing pass:
    bool HasRegisteredMap(Type s, Type d) => pairIndex.ContainsKey(new TypePair(s, d));
    foreach (var tm in typeMaps)
        ConventionEngine.ResolveMissingMembers(tm, _conventionOptions, HasRegisteredMap);

    foreach (var tm in typeMaps)
        tm.Seal();

    // ... existing finalization ...
}
```

Order matters:
1. **Inheritance** — propagates `IncludedBases` → base's `IncludedDerived`, merges base PropertyMaps into derived (only `IsExplicit = true` ones flow). After this pass, derived TypeMaps have inherited explicit config attached as `IsExplicit = true` PropertyMaps.
2. **Convention** — fills in any remaining unresolved members on each TypeMap. Inherited bindings already exist, so convention skips them.
3. **Seal** — freezes everything.

---

## 6. Inheritance Merge Algorithm

### 6.1 `InheritanceMerger.Resolve`

```
input: typeMaps: List<TypeMap>, pairIndex: Dictionary<TypePair, TypeMap>

1. // Propagate IncludeBase declarations to base TypeMaps.
   For each tm in typeMaps:
     For each (baseSrc, baseDst) in tm.IncludedBases:
       Find baseTm in pairIndex by (baseSrc, baseDst)
       If baseTm is null: continue   # validator will report the dangling reference
       If baseTm.IncludedDerived contains tm.Pair: continue   # idempotent
       baseTm.IncludedDerived.Add(tm.Pair)

2. // Merge base config into derived. Two-pass to handle multi-level inheritance correctly.
   //
   // First pass: merge from each base into its direct derived. After this pass, each derived
   // map has its base's config attached as IsExplicit=true PropertyMaps.
   //
   // The "second pass" is implicit: when grandchild D inherits from C and C inherits from B,
   // the first pass merges B→C, then merges C→D. Since C's PropertyMaps now include B's
   // (with IsExplicit=true), D's merge from C correctly carries B's config too.
   //
   // To make this work, the loop processes base-before-derived:
   topoSorted = TopologicalSort(typeMaps, by: tm => tm.IncludedBases)
   For each tm in topoSorted:
     For each derivedPair in tm.IncludedDerived:
       Find derivedTm in pairIndex by derivedPair
       If derivedTm is null: continue   # validator will report
       MergeBaseConfig(tm, derivedTm)

3. // Sort each IncludedDerived list most-derived-first for runtime dispatch.
   For each tm in typeMaps:
     tm.IncludedDerived.Sort(MostDerivedFirstComparer)
```

**`MostDerivedFirstComparer`**: for two `TypePair`s `a` and `b`, `a` precedes `b` iff `b.Source.IsAssignableFrom(a.Source)` (a is more derived). Two unrelated derived types compare equal — their relative order is arbitrary but stable.

**TopologicalSort**: sorts so that for every edge `derived.IncludedBases → base`, the base appears before the derived in the result. Standard Kahn's algorithm. Cycles are impossible by C# type-system construction (`A : B : A` would be a compile error), so no cycle handling needed.

### 6.2 `MergeBaseConfig(baseTm, derivedTm)`

```
For each basePm in baseTm.PropertyMaps:
  If !basePm.IsExplicit: continue
    # Convention-resolved base bindings don't propagate (per §6.3 of this doc — only configured
    # decisions inherit, matching AutoMapper §6.3 semantics).

  derivedPm = derivedTm.PropertyMaps.FirstOrDefault(p => p.Name == basePm.Name)

  If derivedPm is null:
    # Base member not yet on derived. Copy if the derived destination has the property.
    If derivedTm.DestinationType.GetProperty(basePm.Name) is not null:
      var clone = ClonePropertyMapForDerived(basePm)
      clone.IsExplicit = true
      derivedTm.PropertyMaps.Add(clone)

  Else if !derivedPm.IsExplicit:
    # Derived has a convention-resolved binding but no explicit override. Base wins.
    OverwriteWithBase(derivedPm, basePm)
    derivedPm.IsExplicit = true

  # Else: derivedPm.IsExplicit is true — derived's explicit choice stands. Skip.
```

`ClonePropertyMapForDerived` produces a new `PropertyMap` with the same `Name`, `DestinationType`, `DestinationProperty` resolved against `derivedTm.DestinationType` (different `PropertyInfo` instance), and the same `Ignored` / `HasConstant` / `ConstantValue` / `CustomExpression` / `SourcePath` from the base.

`OverwriteWithBase` copies the same fields onto an existing `derivedPm` in place, preserving `derivedPm.DestinationProperty`.

### 6.3 Why convention-resolved base config doesn't propagate

If Animal has `Name` resolved by convention (no explicit `ForMember`), and Dog also has `Name`, Dog's convention will independently resolve `Name` against its own type. Propagating the base's convention path would be redundant and could introduce subtle wrongness (e.g. if Dog's source has a different naming convention, the inherited path would point at the wrong member).

Only **configured decisions** flow:
- `ForMember(d => d.X, o => o.MapFrom(...))` — flows
- `ForMember(d => d.X, o => o.Ignore())` — flows
- Convention path — does NOT flow (Dog re-resolves on its own)

This matches AutoMapper's documented semantics ("Ignored properties on the base override conventions on the derived" — but note: only **explicit** Ignores).

---

## 7. Compilation Algorithm

### 7.1 `ExecutionPlanBuilder.Build` — inheritance dispatch prologue

```
Build(typeMap, registry) -> LambdaExpression:
  // Existing per-base body generation (POCO / collection / dictionary / converter).
  // Returns a Func<TSource, TDestination> shape regardless of how it was built.
  baseBody = BuildBaseBody(typeMap, registry)

  if typeMap.IncludedDerived.Count == 0:
    return baseBody    // unchanged from v1 — zero overhead for non-inheritance maps

  // Build the dispatch chain, most-derived-first.
  srcParam = baseBody.Parameters[0]
  Expression body = InvokeLambdaInline(baseBody, srcParam)   // the fall-through

  // IncludedDerived is already sorted most-derived-first by InheritanceMerger.
  for derivedPair in typeMap.IncludedDerived (already sorted):
    derivedSrc = derivedPair.Source
    derivedDst = derivedPair.Destination

    // src is TDerivedSrc d
    typeIsExpr = Expression.TypeIs(srcParam, derivedSrc)

    // (TDestination)MappingInvoker.Invoke<TDerivedSrc, TDerivedDst>(registry, (TDerivedSrc)src)
    invoke = Expression.Call(
      MappingInvokerInvokeGenericMethod(derivedSrc, derivedDst),
      Expression.Constant(registry),
      Expression.Convert(srcParam, derivedSrc))
    upcast = Expression.Convert(invoke, typeMap.DestinationType)

    body = Expression.Condition(typeIsExpr, upcast, body)

  // Wrap a null guard outside the dispatch chain (matches v1 idiom).
  if typeMap.SourceType.IsClass:
    body = Expression.Condition(
      Expression.ReferenceEqual(srcParam, Expression.Constant(null, typeMap.SourceType)),
      Expression.Default(typeMap.DestinationType),
      body)

  return Expression.Lambda<Func<TSource, TDestination>>(body, srcParam)
```

`InvokeLambdaInline(baseBody, srcParam)` substitutes `srcParam` for `baseBody.Parameters[0]` in `baseBody.Body`, producing an inline expression that doesn't require an extra delegate invocation. (Equivalent to v1 ExecutionPlanBuilder's existing `ParameterReplacer` idiom.)

The chain runs as `is`-tests in order, with the **last** branch being the existing base body. Most-derived-first ordering means a `Beagle` source hits the `is Beagle` branch before reaching `is Dog`.

### 7.2 What happens for nested polymorphic collections

`mapper.Map<List<Animal>, List<AnimalDto>>(animals)` calls v1's `MappingInvoker.InvokeToList<Animal, AnimalDto>`, which iterates and calls `Invoke<Animal, AnimalDto>(animal)` per element. Each call invokes the compiled `Animal → AnimalDto` lambda — which now starts with the dispatch chain. So each element automatically gets dispatched to its derived map.

**Critical**: `MappingInvoker.Invoke<Animal, AnimalDto>` currently has a short-circuit: if `typeof(TSource) == typeof(TDestination)`, it returns the source via `Unsafe.As<TSource, TDestination>`. For `Animal → Animal` self-maps with Includes, that short-circuit must NOT fire — the dispatch chain has to run. See §9 #5.

### 7.3 Configuration validator additions

`ConfigurationValidator.Validate` gains three rules (added to the existing per-typemap loop, errors aggregate into the existing single-throw exception):

1. **Abstract type without Include**:
   - If `tm.SourceType.IsAbstract || tm.DestinationType.IsAbstract` AND `tm.IncludedDerived.Count == 0`:
     - `errors.Add(new ConfigurationError(tm.SourceType, tm.DestinationType, "(map)", "Abstract type used without any Include — map is unreachable."));`

2. **Include points at unregistered map**:
   - For each `derivedPair in tm.IncludedDerived`:
     - If `registry.GetTypeMap(derivedPair) is null`:
       - `errors.Add(new ConfigurationError(tm.SourceType, tm.DestinationType, "(include)", $"Include declares {derivedPair.Source.Name} -> {derivedPair.Destination.Name} but no such map is registered."));`

3. **Include with non-derived types** (catches reflection-driven misconfig that bypasses the generic constraint):
   - For each `derivedPair in tm.IncludedDerived`:
     - If `!tm.SourceType.IsAssignableFrom(derivedPair.Source) || !tm.DestinationType.IsAssignableFrom(derivedPair.Destination)`:
       - `errors.Add(new ConfigurationError(tm.SourceType, tm.DestinationType, "(include)", $"Include's source/destination type does not derive from the base map's source/destination type."));`

Same loop also processes `tm.IncludedBases`:
- If a base reference points at a missing TypeMap, it's already covered by the InheritanceMerger silently skipping it; the validator surfaces the misconfig:
  - For each `basePair in tm.IncludedBases`:
    - If `registry.GetTypeMap(basePair) is null`:
      - `errors.Add(new ConfigurationError(tm.SourceType, tm.DestinationType, "(include-base)", $"IncludeBase references {basePair.Source.Name} -> {basePair.Destination.Name} but no such map is registered."));`

---

## 8. TDD Plan

The implementer writes each test failing first, then the minimum production code to make it pass, in file order. Test counts are floors; add edge cases as encountered.

### 8.1 `tests/Atlas.Tests/Internal/InheritanceMergerTests.cs` (~8 tests)

Whitebox tests against `MergeBaseConfig`. Construct `TypeMap`s directly, call the merger, assert resulting `PropertyMaps`.

1. `Merge_BaseHasExplicitMapFrom_DerivedInheritsIt`
2. `Merge_DerivedHasExplicitMapFrom_BaseDoesNotOverride`
3. `Merge_BaseHasIgnore_DerivedConventionPathIsOverridden` *(load-bearing precedence test)*
4. `Merge_DerivedExplicitlyIgnores_BaseMapFromIsIgnored`
5. `Merge_BaseMemberAbsentFromDerivedDestination_NotCopied`
6. `Merge_DerivedHasOnlyConvention_BaseMapFromOverwrites`
7. `Merge_BaseAndDerivedBothExplicit_DerivedWins`
8. `Merge_NoBaseConfig_DerivedConventionPreserved`

### 8.2 `tests/Atlas.Tests/MapperConfigurationInheritanceTests.cs` (~8 tests)

Tests the full `MapperConfiguration` build pass — Include/IncludeBase resolution, IncludedDerived population, ordering.

1. `Include_OnBase_PopulatesIncludedDerivedOnBase`
2. `IncludeBase_OnDerived_PopulatesIncludedDerivedOnBase` *(cross-profile case)*
3. `Include_TwoLevels_BaseSeesGrandchild_NotJustChild`
4. `IncludeBase_DerivedRegisteredInDifferentProfile_ResolvesCorrectly`
5. `Include_DerivedDispatchOrder_MostDerivedFirst` *(Beagle ordered before Dog)*
6. `Include_DuplicateDeclaration_IsIdempotent`
7. `Include_DerivedMapNotRegistered_FailsValidation`
8. `Include_TypeNotActuallyDerived_FailsValidation`

### 8.3 `tests/Atlas.Tests/ValidationInheritanceTests.cs` (~6 tests)

The four new validator rules from §7.3.

1. `AssertConfigurationIsValid_AbstractSourceWithNoInclude_Throws`
2. `AssertConfigurationIsValid_AbstractDestinationWithNoInclude_Throws`
3. `AssertConfigurationIsValid_AbstractWithInclude_Passes`
4. `AssertConfigurationIsValid_IncludePointsAtUnregisteredMap_Throws`
5. `AssertConfigurationIsValid_IncludeWithNonDerivedTypes_Throws` *(reflection-driven config; bypasses generic constraint)*
6. `AssertConfigurationIsValid_AggregatesAllInheritanceErrors_NotJustFirst`

### 8.4 `tests/Atlas.Tests/Internal/ExecutionPlanBuilderInheritanceTests.cs` (~8 tests)

Whitebox shape tests against the emitted `LambdaExpression`. Use the existing `AssertExpression` visitor pattern from `Atlas.Projections.Tests/Internal/AssertExpression.cs` (copy or hoist into `Atlas.Tests/Internal/`).

1. `Build_BaseWithSingleInclude_LambdaContainsTypeIs`
2. `Build_BaseWithThreeIncludes_LambdaHasThreeChainedConditionals`
3. `Build_BaseWithIncludes_FallsThroughToOriginalBaseBody`
4. `Build_DispatchOrder_MostDerivedFirst`
5. `Build_NullSource_StillReturnsDefault`
6. `Build_NoIncludes_NoTypeIsConditionalsEmitted` *(zero-overhead invariant)*
7. `Build_DerivedDispatchCallsMappingInvoker`
8. `Build_DerivedDestinationCastToBase_EmitsConvert`

### 8.5 `tests/Atlas.Tests/MapperInheritanceTests.cs` (~10 tests)

End-to-end behavior via `IMapper.Map`. The user-facing verification.

1. `Map_TypedOverload_BaseDeclared_RuntimeIsDerived_DispatchesToDerivedMap`
2. `Map_TypedOverload_BaseDeclared_RuntimeIsBase_UsesBaseMap`
3. `Map_NestedDerivedInBaseCollection_DispatchesElementByElement` *(`List<Animal>` mixed Dog/Cat)*
4. `Map_TwoLevelInheritance_BeagleViaAnimal_UsesBeagleMap`
5. `Map_BaseWithIgnore_DerivedDoesNotPopulate`
6. `Map_DerivedOverridesBaseMapFrom_DerivedValueAppears`
7. `Map_DerivedInheritsBaseMapFrom_BaseValueAppears`
8. `Map_AbstractBase_RuntimeDerivedDispatched_Succeeds`
9. `Map_RuntimeTypeNotIncluded_FallsThroughToBase`
10. `Map_SelfMapWithIncludes_DispatchChainExecutes_NotUnsafeAsShortCircuit` *(verifies §9 #5 fix — `Animal → Animal` Includes work correctly)*

**Total: ~40 tests across 5 files.**

### 8.6 Coverage targets

Same gates as v1 (line ≥ 90%, branch ≥ 80%) on the changed files. `InheritanceMerger` is small and exhaustively tested by §8.1; expect 100% line and ≥95% branch. `ExecutionPlanBuilder`'s new prologue is covered by §8.4 + §8.5 combined.

---

## 9. Risks & Open Questions

1. **Performance of the `is`-test chain for many Includes.** A base with 20+ derived classes generates a 20-branch `Conditional` chain. JIT optimizes sequential `isinst` reasonably, but at extreme widths the per-call cost is O(n). Mitigation: most-derived-first ordering minimizes the common case. For very wide hierarchies a future optimization could swap to a `RuntimeTypeHandle`-keyed `Dictionary` lookup. Out of scope for v1; flag for v3 if a real consumer hits it.

2. **"Base Ignore beats derived convention" is a known foot-gun.** AutoMapper's #1 inheritance gotcha. Validator can't help (the binding is "resolved" — to do nothing). Mitigation: README inheritance section must call this out with a worked example. Implementer should add the callout when writing the README update.

3. **`IsExplicit` flag on `PropertyMap` adds mutable state.** Set true by `MappingExpression.ForMember` / `ForCtorParam`, false by `ConventionEngine`. Trivial to maintain, but it's a new field on a v1 type. Validator and merger both depend on it; if either misreads the flag, precedence inverts. Tests §8.1 #6 + #7 together pin the contract.

4. **Inheritance pass order: only explicit base config flows.** `ResolveInheritance` runs before `ResolveMissingMembers`, so base convention-resolved members don't auto-propagate. This is consistent with AutoMapper's "only configured decisions inherit" semantics. Verified by §8.5 #7. If a counterexample surfaces in implementation, reconsider pass ordering.

5. **`MappingInvoker.Invoke<TSrc, TDst>` `Unsafe.As` short-circuit must be guarded.** v1 currently returns `Unsafe.As<TSource, TDestination>(ref source)` when `typeof(TSource) == typeof(TDestination)`. For `Animal → Animal` maps with Includes, that short-circuit would skip the dispatch chain. The fix is to skip the short-circuit when the registered TypeMap has Includes. This is one extra `&& tm.IncludedDerived.Count == 0` check on the existing branch in `MappingInvoker.Invoke`. Test §8.5 #10 pins this.

6. **`Atlas.Projections` interaction (deferred to v3).** Today's projection package is unaware of `IncludedDerived`. `query.ProjectTo<AnimalDto>(cfg)` against a polymorphic `DbSet<Animal>` projects all rows as `AnimalDto` — derived rows lose their derived shape silently. README inheritance section must document this clearly when the implementation lands; v3 design will lift the limitation by emitting `OfType<>` / `Cast<>` switches that EF Core translates.

---

## 10. Appendix A — Worked Example

```csharp
public abstract class Animal { public string Name { get; set; } = ""; public int Age { get; set; } }
public class Dog : Animal { public string Breed { get; set; } = ""; }
public class Cat : Animal { public bool IsIndoor { get; set; } }

public abstract class AnimalDto { public string DisplayName { get; set; } = ""; public int Age { get; set; } }
public class DogDto : AnimalDto { public string Breed { get; set; } = ""; }
public class CatDto : AnimalDto { public bool IsIndoor { get; set; } }

public class AnimalProfile : MapperProfile
{
    public AnimalProfile()
    {
        CreateMap<Animal, AnimalDto>()
            .ForMember(d => d.DisplayName, o => o.MapFrom(s => s.Name))
            .Include<Dog, DogDto>()
            .Include<Cat, CatDto>();

        CreateMap<Dog, DogDto>();   // inherits DisplayName/Age config from Animal -> AnimalDto
        CreateMap<Cat, CatDto>();   // inherits same
    }
}

// Usage:
var config = new MapperConfiguration(cfg => cfg.AddProfile<AnimalProfile>());
config.AssertConfigurationIsValid();
var mapper = config.CreateMapper();

Animal a = new Dog { Name = "Rex", Age = 5, Breed = "Beagle" };
AnimalDto dto = mapper.Map<Animal, AnimalDto>(a);
// dto is actually a DogDto with DisplayName="Rex", Age=5, Breed="Beagle"
```

The compiled lambda for `Animal → AnimalDto` (most-derived-first dispatch with `Cat` and `Dog` arbitrary order since they're siblings):

```csharp
src =>
  src is null ? (AnimalDto)null
  : src is Cat c ? (AnimalDto)MappingInvoker.Invoke<Cat, CatDto>(registry, c)
  : src is Dog d ? (AnimalDto)MappingInvoker.Invoke<Dog, DogDto>(registry, d)
  : <existing Animal -> AnimalDto base body>
```

---

## 11. Implementation Checklist

A future Claude session can execute this top-to-bottom.

- [ ] Add `IsExplicit` field to `PropertyMap`. Update `MappingExpression.ForMember` / `ForCtorParam` to set it.
- [ ] Add `IncludedDerived` and `IncludedBases` lists to `TypeMap`.
- [ ] Add `Include<TDerivedSrc, TDerivedDst>` and `IncludeBase<TBaseSrc, TBaseDst>` to `IMappingExpression` and `MappingExpression`.
- [ ] Implement §8.1 tests, then `InheritanceMerger.MergeBaseConfig`. Green.
- [ ] Implement §8.2 tests, then `InheritanceMerger.Resolve` + the new pass in `MapperConfiguration` ctor. Green.
- [ ] Implement §8.3 tests, then the four validator rules in `ConfigurationValidator`. Green.
- [ ] Implement §8.4 tests, then the dispatch prologue in `ExecutionPlanBuilder.Build`. Green.
- [ ] Apply the `MappingInvoker.Invoke` short-circuit guard (§9 #5) — add `&& tm.IncludedDerived.Count == 0` to the `Unsafe.As` branch.
- [ ] Implement §8.5 tests. All 10 should pass; if §8.5 #10 fails, the `MappingInvoker` guard above wasn't applied.
- [ ] Run coverage; verify §8.6 targets.
- [ ] Update root `README.md` with a short "Inheritance & polymorphism" section linking to this doc. **Must** include the "base Ignore beats derived convention" foot-gun callout (§9 #2) and the ProjectTo limitation callout (§9 #6).
- [ ] Update memory: cross out item #2 in `atlas_v2_design_docs_deferred.md` with `~~text~~ — **shipped**` annotation per the established convention.
