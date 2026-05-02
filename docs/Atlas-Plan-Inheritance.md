# Atlas Inheritance & Polymorphism Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add runtime polymorphism to the core `Atlas` package per `docs/Atlas-Design-Inheritance.md`: `mapper.Map<TBaseDest>(actuallyDerived)` dispatches to the most-specific registered map, derived maps inherit member configuration from base maps with AutoMapper §6.3 precedence, and validator catches all common misconfigurations up-front.

**Architecture:** Purely additive to `Atlas` core. Two new fluent methods (`Include` / `IncludeBase`) on `IMappingExpression`. Two new lists on `TypeMap` (`IncludedDerived` / `IncludedBases`). One new bit on `PropertyMap` (`IsExplicit`). One new internal helper (`InheritanceMerger`). One new pass in `MapperConfiguration` ctor (runs before convention resolution). Inline `is`-test dispatch chain in `ExecutionPlanBuilder.Build` (zero overhead when unused). Three new validator rules. One subtle guard on `MappingInvoker.Invoke`'s `Unsafe.As` short-circuit. No new packages, no public type signatures change shape, no `Atlas.Projections` changes.

**Tech Stack:** .NET 10, xUnit v3 (built-in `Assert.X()`, no FluentAssertions), coverlet.

**Spec reference:** `docs/Atlas-Design-Inheritance.md`. Section numbers in this plan (e.g. "§6.2") refer to the spec.

**v1 conventions to mirror (do not deviate):**
- File-scoped namespaces.
- Internal types under `Internal/` subfolder.
- `internal sealed class` / `internal static class` unless otherwise noted.
- Test naming: `MethodOrFeature_Condition_ExpectedResult`.
- xUnit v3, `[Fact]` / `[Theory]` + `[InlineData]`.
- `System.Threading.Lock` (.NET 9+ type) for mutual exclusion (none needed in this plan, but be aware).
- `TreatWarningsAsErrors=true` is on globally; `GenerateDocumentationFile=true` is on; `CS1591` is suppressed.

**Branching:** Implement on a new branch `feat/inheritance` cut from current `main` (HEAD `5f19f83`). Each task ends in a commit. After all tasks land, the implementer runs the finishing-a-development-branch flow (push + PR) per the same pattern used for `Atlas.Projections`.

**Key files in v1 to read first** (for context, not to modify outside the plan):
- `src/Atlas/Internal/TypeMap.cs` — fields you'll add to
- `src/Atlas/Internal/PropertyMap.cs` — `IsExplicit` flag added here
- `src/Atlas/Internal/MappingInvoker.cs` — `Unsafe.As` short-circuit at line ~33
- `src/Atlas/Internal/ExecutionPlanBuilder.cs` — `Build` dispatch added here
- `src/Atlas/Internal/ConventionEngine.cs` — `ResolveMissingMembers` runs after `InheritanceMerger.Resolve`
- `src/Atlas/Internal/ConfigurationValidator.cs` — three new rules added here
- `src/Atlas/MapperConfiguration.cs` — new pass wired into ctor
- `src/Atlas/Configuration/IMappingExpression.cs` + `MappingExpression.cs` — fluent surface

---

## Task 1: Set up branch

**Files:** none modified; branch creation only.

- [ ] **Step 1: Create the feature branch**

```powershell
git checkout main
git pull
git checkout -b feat/inheritance
```

- [ ] **Step 2: Verify clean baseline**

Run: `dotnet test --nologo`

Expected: all tests pass (current count is 173 from the merged Atlas.Projections work — record the actual number for the final-task count check).

If any test fails, stop and report — the baseline must be green before changes start.

- [ ] **Step 3: No commit** — branching only.

---

## Task 2: Add `IsExplicit` flag to `PropertyMap`

**Files:**
- Modify: `src/Atlas/Internal/PropertyMap.cs`
- Modify: `src/Atlas/Configuration/MappingExpression.cs` (set `IsExplicit = true` from `ForMember` / `ForCtorParam`)
- Modify: `src/Atlas/Configuration/MemberConfigurationExpression.cs` (set `IsExplicit = true` in its `ApplyTo(PropertyMap)` method)

This is data-model groundwork. No new tests yet — the flag is exercised by Task 4's `InheritanceMerger` tests. v1 tests must continue passing because the flag defaults to `false` and existing reads don't check it.

- [ ] **Step 1: Add the field**

Open `src/Atlas/Internal/PropertyMap.cs`. After the existing `bool Ignored` field (around the existing flags block), add:

```csharp
    /// <summary>
    /// True when this binding was configured via <c>ForMember</c> / <c>ForCtorParam</c>
    /// (an explicit user choice). False when populated by <c>ConventionEngine</c>.
    /// Used by <c>InheritanceMerger</c> as the precedence discriminator: derived explicit
    /// beats base explicit beats derived convention.
    /// </summary>
    public bool IsExplicit { get; set; }
```

- [ ] **Step 2: Mark explicit in `MappingExpression.ForMember`**

Open `src/Atlas/Configuration/MappingExpression.cs`. Find the `ForMember<TMember>` method (it creates or fetches a `PropertyMap` for the target property and applies a configuration). At the end of that method — after the binding has been populated by the user's lambda — add:

```csharp
        propertyMap.IsExplicit = true;
```

(If `ForMember` reuses a helper that loops over multiple property maps, set the flag on each one it touches.)

- [ ] **Step 3: Mark explicit in `MappingExpression.ForCtorParam`**

In the same file, find `ForCtorParam`. After the propertyMap has been populated, add the same line:

```csharp
        propertyMap.IsExplicit = true;
```

- [ ] **Step 4: Verify `MemberConfigurationExpression.ApplyTo` doesn't reset the flag**

Open `src/Atlas/Configuration/MemberConfigurationExpression.cs`. Read the `ApplyTo(PropertyMap pm)` method. It mutates `pm.SourcePath`, `pm.HasConstant`, `pm.Ignored`, etc. Confirm it does NOT touch `pm.IsExplicit`. (If it does, remove the assignment — `IsExplicit` is owned by the caller of `ApplyTo`, not by `ApplyTo` itself.)

- [ ] **Step 5: Verify the build is clean**

Run: `dotnet build src/Atlas/Atlas.csproj --nologo`

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 6: Run the full test suite — must remain green**

Run: `dotnet test --nologo`

Expected: same test count as Task 1 baseline, all passing. The `IsExplicit` flag is additive and has no readers yet.

- [ ] **Step 7: Commit**

```powershell
git add src/Atlas/Internal/PropertyMap.cs src/Atlas/Configuration/MappingExpression.cs
git commit -m "Add PropertyMap.IsExplicit flag, set by ForMember/ForCtorParam"
```

(Add `src/Atlas/Configuration/MemberConfigurationExpression.cs` to the commit only if Step 4 required a change.)

---

## Task 3: Add `IncludedDerived` and `IncludedBases` lists to `TypeMap`

**Files:**
- Modify: `src/Atlas/Internal/TypeMap.cs`

Pure data-model change. No tests yet — these lists are exercised by Task 5's MapperConfiguration tests.

- [ ] **Step 1: Add the two list fields**

Open `src/Atlas/Internal/TypeMap.cs`. After the existing `List<PropertyMap> PropertyMaps` field, add:

```csharp
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
```

- [ ] **Step 2: Verify `EnsureMutable` covers both lists**

Read the existing `EnsureMutable()` method. Confirm it throws if `IsSealed` — that single check protects both new lists too (callers should call `EnsureMutable()` before mutating). If `EnsureMutable` is private and only called from `PropertyMaps`-mutation sites, that's fine; the new fluent methods (Task 4) will call it before mutating the new lists.

- [ ] **Step 3: Build clean**

Run: `dotnet build src/Atlas/Atlas.csproj --nologo`

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 4: Run the full test suite**

Run: `dotnet test --nologo`

Expected: baseline count, all passing.

- [ ] **Step 5: Commit**

```powershell
git add src/Atlas/Internal/TypeMap.cs
git commit -m "Add TypeMap.IncludedDerived and IncludedBases lists"
```

---

## Task 4: Add `Include` and `IncludeBase` to fluent surface

**Files:**
- Modify: `src/Atlas/Configuration/IMappingExpression.cs`
- Modify: `src/Atlas/Configuration/MappingExpression.cs`
- Test: deferred — full surface is exercised by Task 5's MapperConfiguration tests and Task 8's mapper tests.

- [ ] **Step 1: Add the two method signatures to `IMappingExpression`**

Open `src/Atlas/Configuration/IMappingExpression.cs`. After the existing methods (`ForMember`, `ForCtorParam`, `ConvertUsing`), add:

```csharp
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
    /// Most-derived-first ordering is computed at config-build time.
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
```

- [ ] **Step 2: Implement both methods in `MappingExpression`**

Open `src/Atlas/Configuration/MappingExpression.cs`. After the existing implementations of `ForMember` / `ForCtorParam` / `ConvertUsing`, add:

```csharp
    public IMappingExpression<TSource, TDestination> Include<TDerivedSource, TDerivedDestination>()
        where TDerivedSource : TSource
        where TDerivedDestination : TDestination
    {
        _typeMap.EnsureMutable();
        var pair = new TypePair(typeof(TDerivedSource), typeof(TDerivedDestination));
        if (!_typeMap.IncludedDerived.Contains(pair))
            _typeMap.IncludedDerived.Add(pair);
        return this;
    }

    public IMappingExpression<TSource, TDestination> IncludeBase<TBaseSource, TBaseDestination>()
        where TSource : TBaseSource
        where TDestination : TBaseDestination
    {
        _typeMap.EnsureMutable();
        var pair = new TypePair(typeof(TBaseSource), typeof(TBaseDestination));
        if (!_typeMap.IncludedBases.Contains(pair))
            _typeMap.IncludedBases.Add(pair);
        return this;
    }
```

(`_typeMap` is the existing field on `MappingExpression`. `EnsureMutable` is the existing method on `TypeMap`. `TypePair` is `Atlas.Internal.TypePair`.)

- [ ] **Step 3: Build clean**

Run: `dotnet build src/Atlas/Atlas.csproj --nologo`

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 4: Run the full suite**

Run: `dotnet test --nologo`

Expected: baseline count + 0 (no behavior added that any test exercises yet — `IncludedDerived` and `IncludedBases` are populated but unread). All tests pass.

- [ ] **Step 5: Commit**

```powershell
git add src/Atlas/Configuration/IMappingExpression.cs src/Atlas/Configuration/MappingExpression.cs
git commit -m "Add Include/IncludeBase to IMappingExpression fluent surface"
```

---

## Task 5: `InheritanceMerger.MergeBaseConfig` (8 tests)

**Files:**
- Create: `tests/Atlas.Tests/Internal/InheritanceMergerTests.cs`
- Create: `src/Atlas/Internal/InheritanceMerger.cs` (this task adds only `MergeBaseConfig`; Task 6 adds `Resolve`)

TDD: write 8 tests for the merge helper, watch them fail, implement `MergeBaseConfig`, watch them pass.

- [ ] **Step 1: Write all 8 tests**

Write `tests/Atlas.Tests/Internal/InheritanceMergerTests.cs`:
```csharp
using System.Linq.Expressions;
using System.Reflection;
using Atlas.Internal;

namespace Atlas.Tests.Internal;

public class InheritanceMergerTests
{
    private static TypeMap MapFor(Type src, Type dst, MemberList memberList = MemberList.None) =>
        new(src, dst, memberList);

    private static PropertyInfo Prop<T>(string name) => typeof(T).GetProperty(name)!;

    [Fact]
    public void Merge_BaseHasExplicitMapFrom_DerivedInheritsIt()
    {
        var baseTm = MapFor(typeof(BaseSrc), typeof(BaseDst));
        var basePm = PropertyMap.ForProperty(Prop<BaseDst>(nameof(BaseDst.Name)));
        basePm.SourcePath = new SourceMemberPath([Prop<BaseSrc>(nameof(BaseSrc.Title))]);
        basePm.IsExplicit = true;
        baseTm.PropertyMaps.Add(basePm);

        var derivedTm = MapFor(typeof(DerivedSrc), typeof(DerivedDst));

        InheritanceMerger.MergeBaseConfig(baseTm, derivedTm);

        var inherited = derivedTm.PropertyMaps.Single(p => p.Name == nameof(BaseDst.Name));
        Assert.NotNull(inherited.SourcePath);
        Assert.Equal(nameof(BaseSrc.Title), inherited.SourcePath!.Members[0].Name);
        Assert.True(inherited.IsExplicit);
    }

    [Fact]
    public void Merge_DerivedHasExplicitMapFrom_BaseDoesNotOverride()
    {
        var baseTm = MapFor(typeof(BaseSrc), typeof(BaseDst));
        var basePm = PropertyMap.ForProperty(Prop<BaseDst>(nameof(BaseDst.Name)));
        basePm.SourcePath = new SourceMemberPath([Prop<BaseSrc>(nameof(BaseSrc.Title))]);
        basePm.IsExplicit = true;
        baseTm.PropertyMaps.Add(basePm);

        var derivedTm = MapFor(typeof(DerivedSrc), typeof(DerivedDst));
        var derivedPm = PropertyMap.ForProperty(Prop<DerivedDst>(nameof(DerivedDst.Name)));
        derivedPm.SourcePath = new SourceMemberPath([Prop<DerivedSrc>(nameof(DerivedSrc.OtherField))]);
        derivedPm.IsExplicit = true;
        derivedTm.PropertyMaps.Add(derivedPm);

        InheritanceMerger.MergeBaseConfig(baseTm, derivedTm);

        var kept = derivedTm.PropertyMaps.Single(p => p.Name == nameof(BaseDst.Name));
        Assert.Equal(nameof(DerivedSrc.OtherField), kept.SourcePath!.Members[0].Name);
    }

    [Fact]
    public void Merge_BaseHasIgnore_DerivedConventionPathIsOverridden()
    {
        // Load-bearing precedence test: base Ignore beats derived convention.
        var baseTm = MapFor(typeof(BaseSrc), typeof(BaseDst));
        var basePm = PropertyMap.ForProperty(Prop<BaseDst>(nameof(BaseDst.Name)));
        basePm.Ignored = true;
        basePm.IsExplicit = true;
        baseTm.PropertyMaps.Add(basePm);

        var derivedTm = MapFor(typeof(DerivedSrc), typeof(DerivedDst));
        var derivedConvPm = PropertyMap.ForProperty(Prop<DerivedDst>(nameof(DerivedDst.Name)));
        derivedConvPm.SourcePath = new SourceMemberPath([Prop<DerivedSrc>(nameof(DerivedSrc.Name))]);
        derivedConvPm.IsExplicit = false; // convention-resolved
        derivedTm.PropertyMaps.Add(derivedConvPm);

        InheritanceMerger.MergeBaseConfig(baseTm, derivedTm);

        var merged = derivedTm.PropertyMaps.Single(p => p.Name == nameof(BaseDst.Name));
        Assert.True(merged.Ignored);
        Assert.True(merged.IsExplicit);
    }

    [Fact]
    public void Merge_DerivedExplicitlyIgnores_BaseMapFromIsIgnored()
    {
        var baseTm = MapFor(typeof(BaseSrc), typeof(BaseDst));
        var basePm = PropertyMap.ForProperty(Prop<BaseDst>(nameof(BaseDst.Name)));
        basePm.SourcePath = new SourceMemberPath([Prop<BaseSrc>(nameof(BaseSrc.Title))]);
        basePm.IsExplicit = true;
        baseTm.PropertyMaps.Add(basePm);

        var derivedTm = MapFor(typeof(DerivedSrc), typeof(DerivedDst));
        var derivedPm = PropertyMap.ForProperty(Prop<DerivedDst>(nameof(DerivedDst.Name)));
        derivedPm.Ignored = true;
        derivedPm.IsExplicit = true;
        derivedTm.PropertyMaps.Add(derivedPm);

        InheritanceMerger.MergeBaseConfig(baseTm, derivedTm);

        var kept = derivedTm.PropertyMaps.Single(p => p.Name == nameof(BaseDst.Name));
        Assert.True(kept.Ignored);
        Assert.Null(kept.SourcePath);
    }

    [Fact]
    public void Merge_BaseMemberAbsentFromDerivedDestination_NotCopied()
    {
        // BaseDst has Name; DerivedOnlyDst doesn't. Don't copy a binding for a property that
        // doesn't exist on the derived destination.
        var baseTm = MapFor(typeof(BaseSrc), typeof(BaseDst));
        var basePm = PropertyMap.ForProperty(Prop<BaseDst>(nameof(BaseDst.Name)));
        basePm.SourcePath = new SourceMemberPath([Prop<BaseSrc>(nameof(BaseSrc.Title))]);
        basePm.IsExplicit = true;
        baseTm.PropertyMaps.Add(basePm);

        var derivedTm = MapFor(typeof(DerivedSrc), typeof(DerivedOnlyDst));

        InheritanceMerger.MergeBaseConfig(baseTm, derivedTm);

        Assert.Empty(derivedTm.PropertyMaps);
    }

    [Fact]
    public void Merge_DerivedHasOnlyConvention_BaseMapFromOverwrites()
    {
        // Derived has a convention-resolved binding (IsExplicit=false). Base's explicit
        // MapFrom wins — overwrite in place.
        var baseTm = MapFor(typeof(BaseSrc), typeof(BaseDst));
        var basePm = PropertyMap.ForProperty(Prop<BaseDst>(nameof(BaseDst.Name)));
        basePm.SourcePath = new SourceMemberPath([Prop<BaseSrc>(nameof(BaseSrc.Title))]);
        basePm.IsExplicit = true;
        baseTm.PropertyMaps.Add(basePm);

        var derivedTm = MapFor(typeof(DerivedSrc), typeof(DerivedDst));
        var derivedConvPm = PropertyMap.ForProperty(Prop<DerivedDst>(nameof(DerivedDst.Name)));
        derivedConvPm.SourcePath = new SourceMemberPath([Prop<DerivedSrc>(nameof(DerivedSrc.Name))]);
        derivedConvPm.IsExplicit = false;
        derivedTm.PropertyMaps.Add(derivedConvPm);

        InheritanceMerger.MergeBaseConfig(baseTm, derivedTm);

        var merged = derivedTm.PropertyMaps.Single(p => p.Name == nameof(BaseDst.Name));
        // Base path won.
        Assert.Equal(nameof(BaseSrc.Title), merged.SourcePath!.Members[0].Name);
        Assert.True(merged.IsExplicit);
    }

    [Fact]
    public void Merge_BaseAndDerivedBothExplicit_DerivedWins()
    {
        var baseTm = MapFor(typeof(BaseSrc), typeof(BaseDst));
        var basePm = PropertyMap.ForProperty(Prop<BaseDst>(nameof(BaseDst.Name)));
        basePm.SourcePath = new SourceMemberPath([Prop<BaseSrc>(nameof(BaseSrc.Title))]);
        basePm.IsExplicit = true;
        baseTm.PropertyMaps.Add(basePm);

        var derivedTm = MapFor(typeof(DerivedSrc), typeof(DerivedDst));
        var derivedPm = PropertyMap.ForProperty(Prop<DerivedDst>(nameof(DerivedDst.Name)));
        derivedPm.SourcePath = new SourceMemberPath([Prop<DerivedSrc>(nameof(DerivedSrc.OtherField))]);
        derivedPm.IsExplicit = true;
        derivedTm.PropertyMaps.Add(derivedPm);

        InheritanceMerger.MergeBaseConfig(baseTm, derivedTm);

        var merged = derivedTm.PropertyMaps.Single(p => p.Name == nameof(BaseDst.Name));
        Assert.Equal(nameof(DerivedSrc.OtherField), merged.SourcePath!.Members[0].Name);
    }

    [Fact]
    public void Merge_NoBaseConfig_DerivedConventionPreserved()
    {
        // Base has only convention-resolved (IsExplicit=false) bindings. Don't propagate.
        var baseTm = MapFor(typeof(BaseSrc), typeof(BaseDst));
        var baseConvPm = PropertyMap.ForProperty(Prop<BaseDst>(nameof(BaseDst.Name)));
        baseConvPm.SourcePath = new SourceMemberPath([Prop<BaseSrc>(nameof(BaseSrc.Title))]);
        baseConvPm.IsExplicit = false;
        baseTm.PropertyMaps.Add(baseConvPm);

        var derivedTm = MapFor(typeof(DerivedSrc), typeof(DerivedDst));
        var derivedConvPm = PropertyMap.ForProperty(Prop<DerivedDst>(nameof(DerivedDst.Name)));
        derivedConvPm.SourcePath = new SourceMemberPath([Prop<DerivedSrc>(nameof(DerivedSrc.Name))]);
        derivedConvPm.IsExplicit = false;
        derivedTm.PropertyMaps.Add(derivedConvPm);

        InheritanceMerger.MergeBaseConfig(baseTm, derivedTm);

        var kept = derivedTm.PropertyMaps.Single(p => p.Name == nameof(BaseDst.Name));
        Assert.Equal(nameof(DerivedSrc.Name), kept.SourcePath!.Members[0].Name);
        Assert.False(kept.IsExplicit);
    }
}

// ---- Test fixtures ----
public class BaseSrc { public string Title { get; set; } = ""; }
public class DerivedSrc : BaseSrc { public string Name { get; set; } = ""; public string OtherField { get; set; } = ""; }

public class BaseDst { public string Name { get; set; } = ""; }
public class DerivedDst : BaseDst { }

public class DerivedOnlyDst { public string OtherProperty { get; set; } = ""; }
```

- [ ] **Step 2: Run the tests; expect compile failure**

Run: `dotnet test tests/Atlas.Tests/Atlas.Tests.csproj --filter "FullyQualifiedName~InheritanceMergerTests" --nologo`

Expected: build error referencing `Atlas.Internal.InheritanceMerger` (does not exist).

- [ ] **Step 3: Implement `MergeBaseConfig`**

Write `src/Atlas/Internal/InheritanceMerger.cs`:
```csharp
namespace Atlas.Internal;

/// <summary>
/// Resolves inheritance relationships between TypeMaps at config-build time. See
/// design §6 (algorithm) and §7 (codegen interaction).
/// </summary>
internal static class InheritanceMerger
{
    /// <summary>
    /// Copies explicit base config (ForMember / ForCtorParam / Ignore) onto the derived TypeMap
    /// per AutoMapper §6.3 precedence: derived explicit beats base explicit beats derived
    /// convention. Convention-resolved base bindings (IsExplicit=false) do NOT propagate —
    /// the derived map re-resolves its own conventions.
    /// </summary>
    public static void MergeBaseConfig(TypeMap baseTm, TypeMap derivedTm)
    {
        foreach (var basePm in baseTm.PropertyMaps)
        {
            if (!basePm.IsExplicit) continue;

            var derivedPm = derivedTm.PropertyMaps.FirstOrDefault(p => p.Name == basePm.Name);

            if (derivedPm is null)
            {
                // Base member not yet on derived. Copy if the derived destination has the property.
                var derivedProp = derivedTm.DestinationType.GetProperty(basePm.Name);
                if (derivedProp is null) continue;

                var clone = PropertyMap.ForProperty(derivedProp);
                CopyConfig(basePm, clone);
                clone.IsExplicit = true;
                derivedTm.PropertyMaps.Add(clone);
            }
            else if (!derivedPm.IsExplicit)
            {
                // Derived has a convention-resolved binding. Base's explicit choice wins.
                CopyConfig(basePm, derivedPm);
                derivedPm.IsExplicit = true;
            }
            // else: derived is explicit — keep it as-is.
        }
    }

    private static void CopyConfig(PropertyMap source, PropertyMap target)
    {
        target.SourcePath = source.SourcePath;
        target.HasConstant = source.HasConstant;
        target.ConstantValue = source.ConstantValue;
        target.CustomExpression = source.CustomExpression;
        target.Ignored = source.Ignored;
        // Note: do NOT copy DestinationProperty / DestinationCtorParameter — those are
        // already correctly bound to the target's PropertyMap.
        // For Ignore-only bindings: source.SourcePath is null, which is fine — target gets null too.
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Atlas.Tests/Atlas.Tests.csproj --filter "FullyQualifiedName~InheritanceMergerTests" --nologo`

Expected: `Passed!  - Failed: 0, Passed: 8`.

If a test fails, read the failure carefully — most likely cause is a missing field copy in `CopyConfig` or a wrong precedence order. Fix the algorithm, not the test.

- [ ] **Step 5: Run the full suite — must remain green**

Run: `dotnet test --nologo`

Expected: baseline + 8, all passing.

- [ ] **Step 6: Commit**

```powershell
git add tests/Atlas.Tests/Internal/InheritanceMergerTests.cs src/Atlas/Internal/InheritanceMerger.cs
git commit -m "Add InheritanceMerger.MergeBaseConfig (8 tests)"
```

---

## Task 6: `InheritanceMerger.Resolve` + wire into `MapperConfiguration` (8 tests)

**Files:**
- Modify: `src/Atlas/Internal/InheritanceMerger.cs` (add `Resolve` method)
- Modify: `src/Atlas/MapperConfiguration.cs` (call `InheritanceMerger.Resolve` before `ConventionEngine.ResolveMissingMembers`)
- Create: `tests/Atlas.Tests/MapperConfigurationInheritanceTests.cs`

TDD: write 8 tests against the full `MapperConfiguration` build pass, watch them fail, implement `Resolve` + wire it into the ctor.

- [ ] **Step 1: Write all 8 tests**

Write `tests/Atlas.Tests/MapperConfigurationInheritanceTests.cs`:
```csharp
using Atlas.Internal;

namespace Atlas.Tests;

public class MapperConfigurationInheritanceTests
{
    [Fact]
    public void Include_OnBase_PopulatesIncludedDerivedOnBase()
    {
        var config = new MapperConfiguration(c =>
        {
            c.CreateMap<MciAnimal, MciAnimalDto>()
                .Include<MciDog, MciDogDto>();
            c.CreateMap<MciDog, MciDogDto>();
        });
        var basePair = new TypePair(typeof(MciAnimal), typeof(MciAnimalDto));
        var baseTm = config.Internal_Registry.GetTypeMap(basePair)!;
        Assert.Contains(new TypePair(typeof(MciDog), typeof(MciDogDto)), baseTm.IncludedDerived);
    }

    [Fact]
    public void IncludeBase_OnDerived_PopulatesIncludedDerivedOnBase()
    {
        var config = new MapperConfiguration(c =>
        {
            c.CreateMap<MciAnimal, MciAnimalDto>();
            c.CreateMap<MciDog, MciDogDto>().IncludeBase<MciAnimal, MciAnimalDto>();
        });
        var basePair = new TypePair(typeof(MciAnimal), typeof(MciAnimalDto));
        var baseTm = config.Internal_Registry.GetTypeMap(basePair)!;
        Assert.Contains(new TypePair(typeof(MciDog), typeof(MciDogDto)), baseTm.IncludedDerived);
    }

    [Fact]
    public void Include_TwoLevels_BaseSeesGrandchild_NotJustChild()
    {
        var config = new MapperConfiguration(c =>
        {
            c.CreateMap<MciAnimal, MciAnimalDto>().Include<MciDog, MciDogDto>();
            c.CreateMap<MciDog, MciDogDto>().Include<MciBeagle, MciBeagleDto>();
            c.CreateMap<MciBeagle, MciBeagleDto>();
        });
        var animalTm = config.Internal_Registry.GetTypeMap(new TypePair(typeof(MciAnimal), typeof(MciAnimalDto)))!;
        var dogTm = config.Internal_Registry.GetTypeMap(new TypePair(typeof(MciDog), typeof(MciDogDto)))!;
        Assert.Contains(new TypePair(typeof(MciDog), typeof(MciDogDto)), animalTm.IncludedDerived);
        Assert.Contains(new TypePair(typeof(MciBeagle), typeof(MciBeagleDto)), dogTm.IncludedDerived);
    }

    [Fact]
    public void IncludeBase_DerivedRegisteredInDifferentProfile_ResolvesCorrectly()
    {
        var config = new MapperConfiguration(c =>
        {
            c.AddProfile(new BaseProfile());
            c.AddProfile(new DerivedProfile());
        });
        var basePair = new TypePair(typeof(MciAnimal), typeof(MciAnimalDto));
        var baseTm = config.Internal_Registry.GetTypeMap(basePair)!;
        Assert.Contains(new TypePair(typeof(MciDog), typeof(MciDogDto)), baseTm.IncludedDerived);
    }

    [Fact]
    public void Include_DerivedDispatchOrder_MostDerivedFirst()
    {
        var config = new MapperConfiguration(c =>
        {
            c.CreateMap<MciAnimal, MciAnimalDto>()
                .Include<MciDog, MciDogDto>()
                .Include<MciBeagle, MciBeagleDto>();
            c.CreateMap<MciDog, MciDogDto>();
            c.CreateMap<MciBeagle, MciBeagleDto>();
        });
        var animalTm = config.Internal_Registry.GetTypeMap(new TypePair(typeof(MciAnimal), typeof(MciAnimalDto)))!;
        var beagleIdx = animalTm.IncludedDerived.IndexOf(new TypePair(typeof(MciBeagle), typeof(MciBeagleDto)));
        var dogIdx = animalTm.IncludedDerived.IndexOf(new TypePair(typeof(MciDog), typeof(MciDogDto)));
        Assert.True(beagleIdx >= 0 && dogIdx >= 0);
        Assert.True(beagleIdx < dogIdx, "Beagle (most-derived) must come before Dog");
    }

    [Fact]
    public void Include_DuplicateDeclaration_IsIdempotent()
    {
        var config = new MapperConfiguration(c =>
        {
            c.CreateMap<MciAnimal, MciAnimalDto>()
                .Include<MciDog, MciDogDto>()
                .Include<MciDog, MciDogDto>(); // duplicate
            c.CreateMap<MciDog, MciDogDto>();
        });
        var animalTm = config.Internal_Registry.GetTypeMap(new TypePair(typeof(MciAnimal), typeof(MciAnimalDto)))!;
        Assert.Single(animalTm.IncludedDerived);
    }

    [Fact]
    public void Include_DerivedMapNotRegistered_FailsValidation()
    {
        var config = new MapperConfiguration(c =>
        {
            c.CreateMap<MciAnimal, MciAnimalDto>()
                .Include<MciDog, MciDogDto>();
            // MciDog->MciDogDto NOT registered
        });
        Assert.Throws<AtlasConfigurationException>(() => config.AssertConfigurationIsValid());
    }

    [Fact]
    public void Include_TypeNotActuallyDerived_FailsValidation()
    {
        // Bypass the generic constraint by registering through an internal entry point — emulate
        // a reflection-driven bug. Easiest: directly add the wrong pair to IncludedDerived after
        // configuration but before validation. Since IncludedDerived is internal-list mutable,
        // the validator must catch this case.
        var config = new MapperConfiguration(c =>
        {
            c.CreateMap<MciAnimal, MciAnimalDto>();
            c.CreateMap<MciUnrelated, MciUnrelatedDto>();
        });
        var animalTm = config.Internal_Registry.GetTypeMap(new TypePair(typeof(MciAnimal), typeof(MciAnimalDto)))!;
        // Force the bad entry directly (simulating reflection-driven misconfig).
        animalTm.IncludedDerived.Add(new TypePair(typeof(MciUnrelated), typeof(MciUnrelatedDto)));
        Assert.Throws<AtlasConfigurationException>(() => config.AssertConfigurationIsValid());
    }
}

// ---- Profiles for cross-profile test ----
public class BaseProfile : MapperProfile
{
    public BaseProfile() { CreateMap<MciAnimal, MciAnimalDto>(); }
}

public class DerivedProfile : MapperProfile
{
    public DerivedProfile() { CreateMap<MciDog, MciDogDto>().IncludeBase<MciAnimal, MciAnimalDto>(); }
}

// ---- Test fixtures ----
public class MciAnimal { public string Name { get; set; } = ""; }
public class MciDog : MciAnimal { public string Breed { get; set; } = ""; }
public class MciBeagle : MciDog { public bool ShortLegs { get; set; } }

public class MciAnimalDto { public string Name { get; set; } = ""; }
public class MciDogDto : MciAnimalDto { public string Breed { get; set; } = ""; }
public class MciBeagleDto : MciDogDto { public bool ShortLegs { get; set; } }

public class MciUnrelated { public int X { get; set; } }
public class MciUnrelatedDto { public int X { get; set; } }
```

- [ ] **Step 2: Run the tests; expect mostly fail / one to compile-fail**

Run: `dotnet test tests/Atlas.Tests/Atlas.Tests.csproj --filter "FullyQualifiedName~MapperConfigurationInheritanceTests" --nologo`

Expected: tests fail because `InheritanceMerger.Resolve` doesn't exist yet AND because `ConfigurationValidator` doesn't yet enforce the new rules. Tests #7 and #8 will fail at the `Throws<AtlasConfigurationException>` line — the validation rules land in Task 7.

For now it's fine if some tests fail with `Throws` not throwing — they'll go green once Task 7 lands. The OTHER 6 tests must pass as soon as `Resolve` is implemented and wired in.

- [ ] **Step 3: Add `Resolve` to `InheritanceMerger`**

Edit `src/Atlas/Internal/InheritanceMerger.cs`. Add this method to the existing class:
```csharp
    /// <summary>
    /// Two-phase pass over the registered TypeMaps:
    /// 1. Propagate <see cref="TypeMap.IncludedBases"/> entries onto the corresponding base
    ///    TypeMap's <see cref="TypeMap.IncludedDerived"/>.
    /// 2. In topological order (base-before-derived), merge each base's explicit config into
    ///    each derived TypeMap.
    /// 3. Sort each <see cref="TypeMap.IncludedDerived"/> list most-derived-first for runtime
    ///    dispatch ordering.
    /// </summary>
    public static void Resolve(IReadOnlyList<TypeMap> typeMaps, IReadOnlyDictionary<TypePair, TypeMap> pairIndex)
    {
        // Phase 1: propagate IncludeBase declarations.
        foreach (var tm in typeMaps)
        {
            foreach (var basePair in tm.IncludedBases)
            {
                if (!pairIndex.TryGetValue(basePair, out var baseTm)) continue; // validator reports
                if (baseTm.IncludedDerived.Contains(tm.Pair)) continue;          // idempotent
                baseTm.IncludedDerived.Add(tm.Pair);
            }
        }

        // Phase 2: merge in topological order. Cycles impossible by C# type system.
        var sorted = TopologicalSort(typeMaps);
        foreach (var tm in sorted)
        {
            foreach (var derivedPair in tm.IncludedDerived)
            {
                if (!pairIndex.TryGetValue(derivedPair, out var derivedTm)) continue;
                MergeBaseConfig(tm, derivedTm);
            }
        }

        // Phase 3: sort each IncludedDerived list most-derived-first.
        foreach (var tm in typeMaps)
        {
            tm.IncludedDerived.Sort(MostDerivedFirstComparer);
        }
    }

    /// <summary>
    /// Returns typeMaps in an order where every TypeMap's bases (per IncludedBases) appear
    /// before it. Edges are tm -> base for each entry in tm.IncludedBases. Standard Kahn's
    /// algorithm; cycles are impossible because IncludedBases edges follow C# inheritance.
    /// </summary>
    private static List<TypeMap> TopologicalSort(IReadOnlyList<TypeMap> typeMaps)
    {
        // Build reverse adjacency: for each base, list of derived TypeMaps that include it.
        var baseToDerived = new Dictionary<TypePair, List<TypeMap>>();
        var inDegree = new Dictionary<TypeMap, int>();
        foreach (var tm in typeMaps) inDegree[tm] = 0;

        foreach (var tm in typeMaps)
        {
            foreach (var basePair in tm.IncludedBases)
            {
                if (!baseToDerived.TryGetValue(basePair, out var list))
                {
                    list = new List<TypeMap>();
                    baseToDerived[basePair] = list;
                }
                list.Add(tm);
                inDegree[tm]++;
            }
        }

        var queue = new Queue<TypeMap>(typeMaps.Where(tm => inDegree[tm] == 0));
        var result = new List<TypeMap>(typeMaps.Count);
        while (queue.Count > 0)
        {
            var tm = queue.Dequeue();
            result.Add(tm);
            if (baseToDerived.TryGetValue(tm.Pair, out var children))
            {
                foreach (var child in children)
                {
                    inDegree[child]--;
                    if (inDegree[child] == 0) queue.Enqueue(child);
                }
            }
        }

        // Any remaining (impossible by type-system construction) — append to keep total count.
        foreach (var tm in typeMaps.Where(tm => !result.Contains(tm)))
            result.Add(tm);
        return result;
    }

    private static int MostDerivedFirstComparer(TypePair a, TypePair b)
    {
        if (a.Source == b.Source) return 0;
        // a is more derived if b's source is assignable from a's source.
        if (b.Source.IsAssignableFrom(a.Source)) return -1;
        if (a.Source.IsAssignableFrom(b.Source)) return 1;
        return 0; // unrelated siblings — stable order
    }
```

- [ ] **Step 4: Wire `Resolve` into `MapperConfiguration` ctor**

Open `src/Atlas/MapperConfiguration.cs`. Find the ctor that takes `MapperConfigurationExpression`. Currently it does:

```csharp
        var typeMaps = expression.GetTypeMaps().ToList();
        var pairIndex = typeMaps.ToDictionary(t => t.Pair);
        bool HasRegisteredMap(Type s, Type d) => pairIndex.ContainsKey(new TypePair(s, d));

        foreach (var tm in typeMaps)
            ConventionEngine.ResolveMissingMembers(tm, _conventionOptions, HasRegisteredMap);

        foreach (var tm in typeMaps)
            tm.Seal();
```

Insert the inheritance pass BEFORE convention resolution:

```csharp
        var typeMaps = expression.GetTypeMaps().ToList();
        var pairIndex = typeMaps.ToDictionary(t => t.Pair);
        bool HasRegisteredMap(Type s, Type d) => pairIndex.ContainsKey(new TypePair(s, d));

        // NEW: resolve inheritance before convention so derived maps see inherited
        // explicit config as already-attached (and convention then fills gaps).
        InheritanceMerger.Resolve(typeMaps, pairIndex);

        foreach (var tm in typeMaps)
            ConventionEngine.ResolveMissingMembers(tm, _conventionOptions, HasRegisteredMap);

        foreach (var tm in typeMaps)
            tm.Seal();
```

- [ ] **Step 5: Run the inheritance tests — expect 6/8 passing**

Run: `dotnet test tests/Atlas.Tests/Atlas.Tests.csproj --filter "FullyQualifiedName~MapperConfigurationInheritanceTests" --nologo`

Expected: 6 passing, 2 failing (`Include_DerivedMapNotRegistered_FailsValidation` and `Include_TypeNotActuallyDerived_FailsValidation`). Those go green in Task 7.

- [ ] **Step 6: Run the full suite — should remain green except those 2**

Run: `dotnet test --nologo`

Expected: previous baseline + 8 new merger tests + 6 of the 8 new MapperConfig tests = green; 2 failures pending Task 7.

If any v1 test now fails, the new ResolveInheritance pass has a bug — investigate before continuing. Most likely culprit: a TypeMap that uses convention-only bindings is having its bindings overwritten because of a missed `IsExplicit` check in `MergeBaseConfig` or `Resolve`.

- [ ] **Step 7: Commit**

```powershell
git add tests/Atlas.Tests/MapperConfigurationInheritanceTests.cs src/Atlas/Internal/InheritanceMerger.cs src/Atlas/MapperConfiguration.cs
git commit -m "Add InheritanceMerger.Resolve, wire into MapperConfiguration (6/8 tests; 2 await validator)"
```

---

## Task 7: Validator rules (6 tests)

**Files:**
- Create: `tests/Atlas.Tests/ValidationInheritanceTests.cs`
- Modify: `src/Atlas/Internal/ConfigurationValidator.cs` (add 4 new rules — see §7.3 of spec)

After this task, the 2 deferred Task-6 tests should also pass.

- [ ] **Step 1: Write all 6 validator tests**

Write `tests/Atlas.Tests/ValidationInheritanceTests.cs`:
```csharp
using Atlas.Internal;

namespace Atlas.Tests;

public class ValidationInheritanceTests
{
    [Fact]
    public void AssertConfigurationIsValid_AbstractSourceWithNoInclude_Throws()
    {
        var config = new MapperConfiguration(c => c.CreateMap<ViAbstractAnimal, ViAnimalDto>());
        var ex = Assert.Throws<AtlasConfigurationException>(() => config.AssertConfigurationIsValid());
        Assert.Contains(ex.Errors, e => e.Reason.Contains("Abstract type used without any Include", StringComparison.Ordinal));
    }

    [Fact]
    public void AssertConfigurationIsValid_AbstractDestinationWithNoInclude_Throws()
    {
        var config = new MapperConfiguration(c => c.CreateMap<ViAnimal, ViAbstractAnimalDto>());
        var ex = Assert.Throws<AtlasConfigurationException>(() => config.AssertConfigurationIsValid());
        Assert.Contains(ex.Errors, e => e.Reason.Contains("Abstract type used without any Include", StringComparison.Ordinal));
    }

    [Fact]
    public void AssertConfigurationIsValid_AbstractWithInclude_Passes()
    {
        var config = new MapperConfiguration(c =>
        {
            c.CreateMap<ViAbstractAnimal, ViAnimalDto>().Include<ViDog, ViDogDto>();
            c.CreateMap<ViDog, ViDogDto>();
        });
        config.AssertConfigurationIsValid(); // does not throw
    }

    [Fact]
    public void AssertConfigurationIsValid_IncludePointsAtUnregisteredMap_Throws()
    {
        var config = new MapperConfiguration(c =>
        {
            c.CreateMap<ViAnimal, ViAnimalDto>().Include<ViDog, ViDogDto>();
            // ViDog -> ViDogDto NOT registered
        });
        var ex = Assert.Throws<AtlasConfigurationException>(() => config.AssertConfigurationIsValid());
        Assert.Contains(ex.Errors, e => e.Reason.Contains("Include declares", StringComparison.Ordinal));
    }

    [Fact]
    public void AssertConfigurationIsValid_IncludeWithNonDerivedTypes_Throws()
    {
        // Force the bad entry directly via the internal list (emulating reflection-driven misconfig).
        var config = new MapperConfiguration(c =>
        {
            c.CreateMap<ViAnimal, ViAnimalDto>();
            c.CreateMap<ViUnrelated, ViUnrelatedDto>();
        });
        var animalTm = config.Internal_Registry.GetTypeMap(new TypePair(typeof(ViAnimal), typeof(ViAnimalDto)))!;
        animalTm.IncludedDerived.Add(new TypePair(typeof(ViUnrelated), typeof(ViUnrelatedDto)));

        var ex = Assert.Throws<AtlasConfigurationException>(() => config.AssertConfigurationIsValid());
        Assert.Contains(ex.Errors, e => e.Reason.Contains("does not derive from the base map's", StringComparison.Ordinal));
    }

    [Fact]
    public void AssertConfigurationIsValid_AggregatesAllInheritanceErrors_NotJustFirst()
    {
        var config = new MapperConfiguration(c =>
        {
            c.CreateMap<ViAnimal, ViAnimalDto>()
                .Include<ViDog, ViDogDto>()
                .Include<ViCat, ViCatDto>();
            // Neither derived map is registered.
        });
        var ex = Assert.Throws<AtlasConfigurationException>(() => config.AssertConfigurationIsValid());
        Assert.Equal(2, ex.Errors.Count(e => e.Reason.Contains("Include declares", StringComparison.Ordinal)));
    }
}

// ---- Test fixtures ----
public class ViAnimal { public string Name { get; set; } = ""; }
public class ViDog : ViAnimal { }
public class ViCat : ViAnimal { }
public abstract class ViAbstractAnimal { public string Name { get; set; } = ""; }

public class ViAnimalDto { public string Name { get; set; } = ""; }
public class ViDogDto : ViAnimalDto { }
public class ViCatDto : ViAnimalDto { }
public abstract class ViAbstractAnimalDto { public string Name { get; set; } = ""; }

public class ViUnrelated { public int X { get; set; } }
public class ViUnrelatedDto { public int X { get; set; } }
```

- [ ] **Step 2: Run the tests; expect 6 fails**

Run: `dotnet test tests/Atlas.Tests/Atlas.Tests.csproj --filter "FullyQualifiedName~ValidationInheritanceTests" --nologo`

Expected: 6/6 fail because the validator doesn't yet enforce the rules.

- [ ] **Step 3: Add the four new rules to `ConfigurationValidator`**

Open `src/Atlas/Internal/ConfigurationValidator.cs`. Find the existing `Validate` method's main per-typemap loop. Inside that loop (after the `MemberList.None` skip and the existing destination/source dispatch but BEFORE the loop tail), add:

```csharp
            // Inheritance rules (design §7.3).
            ValidateInheritance(tm, registry, errors);
```

Then add this helper method to the class:

```csharp
    private static void ValidateInheritance(TypeMap tm, MapperRegistry registry, List<ConfigurationError> errors)
    {
        // Rule 1: abstract type without any Include is unreachable.
        if ((tm.SourceType.IsAbstract || tm.DestinationType.IsAbstract) && tm.IncludedDerived.Count == 0)
        {
            errors.Add(new ConfigurationError(
                tm.SourceType, tm.DestinationType, "(map)",
                "Abstract type used without any Include — map is unreachable."));
        }

        // Rule 2: each Include must point at a registered map.
        foreach (var derivedPair in tm.IncludedDerived)
        {
            if (registry.GetTypeMap(derivedPair) is null)
            {
                errors.Add(new ConfigurationError(
                    tm.SourceType, tm.DestinationType, "(include)",
                    $"Include declares {derivedPair.Source.Name} -> {derivedPair.Destination.Name} but no such map is registered."));
            }
        }

        // Rule 3: each Include's types must derive from the base map's types.
        foreach (var derivedPair in tm.IncludedDerived)
        {
            if (!tm.SourceType.IsAssignableFrom(derivedPair.Source) ||
                !tm.DestinationType.IsAssignableFrom(derivedPair.Destination))
            {
                errors.Add(new ConfigurationError(
                    tm.SourceType, tm.DestinationType, "(include)",
                    $"Include's source/destination type ({derivedPair.Source.Name} -> {derivedPair.Destination.Name}) does not derive from the base map's source/destination type."));
            }
        }

        // Rule 4: each IncludeBase must point at a registered map.
        foreach (var basePair in tm.IncludedBases)
        {
            if (registry.GetTypeMap(basePair) is null)
            {
                errors.Add(new ConfigurationError(
                    tm.SourceType, tm.DestinationType, "(include-base)",
                    $"IncludeBase references {basePair.Source.Name} -> {basePair.Destination.Name} but no such map is registered."));
            }
        }
    }
```

- [ ] **Step 4: Run the new tests — expect 6/6 pass**

Run: `dotnet test tests/Atlas.Tests/Atlas.Tests.csproj --filter "FullyQualifiedName~ValidationInheritanceTests" --nologo`

Expected: `Passed!  - Failed: 0, Passed: 6`.

- [ ] **Step 5: Re-run the deferred Task-6 tests**

Run: `dotnet test tests/Atlas.Tests/Atlas.Tests.csproj --filter "FullyQualifiedName~MapperConfigurationInheritanceTests" --nologo`

Expected: 8/8 — the previously failing `Include_DerivedMapNotRegistered_FailsValidation` and `Include_TypeNotActuallyDerived_FailsValidation` should now pass.

- [ ] **Step 6: Run the full suite — must be green**

Run: `dotnet test --nologo`

Expected: baseline + 8 (Task 5) + 8 (Task 6) + 6 (Task 7) = baseline + 22 tests, all passing.

- [ ] **Step 7: Commit**

```powershell
git add tests/Atlas.Tests/ValidationInheritanceTests.cs src/Atlas/Internal/ConfigurationValidator.cs
git commit -m "Add 4 inheritance validator rules (6 tests; unblocks 2 Task-6 tests)"
```

---

## Task 8: Hoist `AssertExpression` helper into `Atlas.Tests`

**Files:**
- Create: `tests/Atlas.Tests/Internal/AssertExpression.cs`

The `AssertExpression` whitebox visitor was created in `Atlas.Projections.Tests/Internal/AssertExpression.cs` for Task 7 of the projections plan. We need the same helper for Task 9 here. Two options: cross-project reference (ugly), or duplicate (acceptable — it's 50 lines and stable).

Do the duplicate. Future cleanup: hoist to a shared `Atlas.TestKit` internal package if a third consumer appears.

- [ ] **Step 1: Copy the file**

Read `tests/Atlas.Projections.Tests/Internal/AssertExpression.cs` and write the same content to `tests/Atlas.Tests/Internal/AssertExpression.cs`, with the namespace changed to `Atlas.Tests.Internal`:

```csharp
using System.Linq.Expressions;

namespace Atlas.Tests.Internal;

/// <summary>
/// Small whitebox assertions over an expression tree. Used by ExecutionPlanBuilder tests
/// (and ProjectionPlanBuilder tests in Atlas.Projections.Tests) to verify the SHAPE of
/// emitted lambdas, not just their execution result.
/// </summary>
internal static class AssertExpression
{
    public static bool Contains<TNode>(Expression expression) where TNode : Expression
    {
        var found = false;
        var visitor = new PredicateVisitor(node => { if (node is TNode) { found = true; return true; } return false; });
        visitor.Visit(expression);
        return found;
    }

    public static bool ContainsCallTo(Expression expression, string declaringTypeName, string methodName)
    {
        var found = false;
        var visitor = new PredicateVisitor(node =>
        {
            if (node is MethodCallExpression mc &&
                mc.Method.DeclaringType?.Name == declaringTypeName &&
                mc.Method.Name == methodName)
            {
                found = true;
                return true;
            }
            return false;
        });
        visitor.Visit(expression);
        return found;
    }

    public static int CountNodes<TNode>(Expression expression) where TNode : Expression
    {
        var count = 0;
        var visitor = new PredicateVisitor(node => { if (node is TNode) count++; return false; });
        visitor.Visit(expression);
        return count;
    }

    private sealed class PredicateVisitor : ExpressionVisitor
    {
        private readonly Func<Expression, bool> _stopWhen;
        public PredicateVisitor(Func<Expression, bool> stopWhen) { _stopWhen = stopWhen; }
        public override Expression? Visit(Expression? node)
        {
            if (node is null) return null;
            if (_stopWhen(node)) return node;
            return base.Visit(node);
        }
    }
}
```

- [ ] **Step 2: Build clean**

Run: `dotnet build tests/Atlas.Tests/Atlas.Tests.csproj --nologo`

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 3: Commit**

```powershell
git add tests/Atlas.Tests/Internal/AssertExpression.cs
git commit -m "Hoist AssertExpression helper into Atlas.Tests for builder tests"
```

---

## Task 9: ExecutionPlanBuilder dispatch prologue (8 tests)

**Files:**
- Create: `tests/Atlas.Tests/Internal/ExecutionPlanBuilderInheritanceTests.cs`
- Modify: `src/Atlas/Internal/ExecutionPlanBuilder.cs` (add inheritance prologue to `Build`)

This is the load-bearing codegen task. Existing v1 codegen for non-inheritance maps must be **identical** after the change (zero overhead invariant — Test #6).

- [ ] **Step 1: Write all 8 tests**

Write `tests/Atlas.Tests/Internal/ExecutionPlanBuilderInheritanceTests.cs`:
```csharp
using System.Linq.Expressions;
using Atlas.Internal;

namespace Atlas.Tests.Internal;

public class ExecutionPlanBuilderInheritanceTests
{
    private static (MapperRegistry registry, LambdaExpression lambda) Build<TSource, TDestination>(
        Action<MapperConfigurationExpression> configure)
    {
        var config = new MapperConfiguration(configure);
        var registry = config.Internal_Registry;
        var tm = registry.GetTypeMap(new TypePair(typeof(TSource), typeof(TDestination)))!;
        return (registry, ExecutionPlanBuilder.Build(tm, registry));
    }

    [Fact]
    public void Build_BaseWithSingleInclude_LambdaContainsTypeIs()
    {
        var (_, lambda) = Build<EpAnimal, EpAnimalDto>(c =>
        {
            c.CreateMap<EpAnimal, EpAnimalDto>().Include<EpDog, EpDogDto>();
            c.CreateMap<EpDog, EpDogDto>();
        });
        Assert.True(AssertExpression.Contains<TypeBinaryExpression>(lambda.Body));
    }

    [Fact]
    public void Build_BaseWithThreeIncludes_LambdaHasThreeChainedConditionals()
    {
        var (_, lambda) = Build<EpAnimal, EpAnimalDto>(c =>
        {
            c.CreateMap<EpAnimal, EpAnimalDto>()
                .Include<EpDog, EpDogDto>()
                .Include<EpCat, EpCatDto>()
                .Include<EpBird, EpBirdDto>();
            c.CreateMap<EpDog, EpDogDto>();
            c.CreateMap<EpCat, EpCatDto>();
            c.CreateMap<EpBird, EpBirdDto>();
        });
        // Three TypeBinaryExpressions (one per Include) plus possibly one ReferenceEqual for null guard.
        Assert.Equal(3, AssertExpression.CountNodes<TypeBinaryExpression>(lambda.Body));
    }

    [Fact]
    public void Build_BaseWithIncludes_FallsThroughToOriginalBaseBody()
    {
        // Test that compiling and invoking with a non-derived base instance still maps via base.
        var (_, lambda) = Build<EpAnimal, EpAnimalDto>(c =>
        {
            c.CreateMap<EpAnimal, EpAnimalDto>().Include<EpDog, EpDogDto>();
            c.CreateMap<EpDog, EpDogDto>();
        });
        var fn = (Func<EpAnimal, EpAnimalDto>)lambda.Compile();
        var dst = fn(new EpAnimal { Name = "x" });
        Assert.NotNull(dst);
        Assert.Equal("x", dst.Name);
        Assert.IsType<EpAnimalDto>(dst);
        Assert.IsNotType<EpDogDto>(dst);
    }

    [Fact]
    public void Build_DispatchOrder_MostDerivedFirst()
    {
        // Compile + invoke with a Beagle should hit the Beagle branch (returning EpBeagleDto),
        // not the Dog branch (returning EpDogDto).
        var (_, lambda) = Build<EpAnimal, EpAnimalDto>(c =>
        {
            c.CreateMap<EpAnimal, EpAnimalDto>()
                .Include<EpDog, EpDogDto>()
                .Include<EpBeagle, EpBeagleDto>();
            c.CreateMap<EpDog, EpDogDto>();
            c.CreateMap<EpBeagle, EpBeagleDto>();
        });
        var fn = (Func<EpAnimal, EpAnimalDto>)lambda.Compile();
        var dst = fn(new EpBeagle { Name = "rex" });
        Assert.IsType<EpBeagleDto>(dst);
    }

    [Fact]
    public void Build_NullSource_StillReturnsDefault()
    {
        var (_, lambda) = Build<EpAnimal, EpAnimalDto>(c =>
        {
            c.CreateMap<EpAnimal, EpAnimalDto>().Include<EpDog, EpDogDto>();
            c.CreateMap<EpDog, EpDogDto>();
        });
        var fn = (Func<EpAnimal, EpAnimalDto>)lambda.Compile();
        Assert.Null(fn(null!));
    }

    [Fact]
    public void Build_NoIncludes_NoTypeIsConditionalsEmitted()
    {
        // Zero-overhead invariant: a TypeMap with no IncludedDerived must have the same shape
        // as v1's compiled lambda (no TypeBinaryExpression nodes from inheritance prologue).
        var (_, lambda) = Build<EpFlatSrc, EpFlatDst>(c => c.CreateMap<EpFlatSrc, EpFlatDst>());
        Assert.False(AssertExpression.Contains<TypeBinaryExpression>(lambda.Body));
    }

    [Fact]
    public void Build_DerivedDispatchCallsMappingInvoker()
    {
        // Each derived branch should call MappingInvoker.Invoke<TDerived, TDerivedDst>.
        var (_, lambda) = Build<EpAnimal, EpAnimalDto>(c =>
        {
            c.CreateMap<EpAnimal, EpAnimalDto>().Include<EpDog, EpDogDto>();
            c.CreateMap<EpDog, EpDogDto>();
        });
        Assert.True(AssertExpression.ContainsCallTo(lambda.Body, "MappingInvoker", "Invoke"));
    }

    [Fact]
    public void Build_DerivedDestinationCastToBase_EmitsConvert()
    {
        // The Map<Dog, DogDto>(d) result must be cast to AnimalDto for the base lambda's return type.
        var (_, lambda) = Build<EpAnimal, EpAnimalDto>(c =>
        {
            c.CreateMap<EpAnimal, EpAnimalDto>().Include<EpDog, EpDogDto>();
            c.CreateMap<EpDog, EpDogDto>();
        });
        Assert.True(AssertExpression.Contains<UnaryExpression>(lambda.Body));
    }
}

// ---- Test fixtures ----
public class EpAnimal { public string Name { get; set; } = ""; }
public class EpDog : EpAnimal { public string Breed { get; set; } = ""; }
public class EpBeagle : EpDog { public bool ShortLegs { get; set; } }
public class EpCat : EpAnimal { }
public class EpBird : EpAnimal { }

public class EpAnimalDto { public string Name { get; set; } = ""; }
public class EpDogDto : EpAnimalDto { public string Breed { get; set; } = ""; }
public class EpBeagleDto : EpDogDto { public bool ShortLegs { get; set; } }
public class EpCatDto : EpAnimalDto { }
public class EpBirdDto : EpAnimalDto { }

public class EpFlatSrc { public int Id { get; set; } public string Name { get; set; } = ""; }
public class EpFlatDst { public int Id { get; set; } public string Name { get; set; } = ""; }
```

- [ ] **Step 2: Run the tests; expect 7 fails (test #6 passes since it's the "no inheritance" case)**

Run: `dotnet test tests/Atlas.Tests/Atlas.Tests.csproj --filter "FullyQualifiedName~ExecutionPlanBuilderInheritanceTests" --nologo`

Expected: 7/8 fail because the prologue isn't emitted yet. Test #6 (no Includes) should pass — its v1 lambda has no `TypeBinaryExpression`.

- [ ] **Step 3: Add the dispatch prologue to `ExecutionPlanBuilder.Build`**

Open `src/Atlas/Internal/ExecutionPlanBuilder.cs`. The existing `Build(typeMap, registry)` method dispatches to `BuildPocoLambda` / `BuildCollectionLambda` / `BuildDictionaryLambda` / `BuildConverterLambda` and returns the resulting `LambdaExpression`. We need to wrap that result with an inheritance prologue when `typeMap.IncludedDerived.Count > 0`.

Refactor the existing `Build` to delegate the per-base body work, then wrap:

```csharp
    public static LambdaExpression Build(TypeMap typeMap, MapperRegistry registry)
    {
        // Per-base body (existing v1 codegen).
        var baseLambda = BuildBaseBody(typeMap, registry);

        if (typeMap.IncludedDerived.Count == 0)
            return baseLambda;

        return BuildWithInheritanceDispatch(baseLambda, typeMap, registry);
    }

    private static LambdaExpression BuildBaseBody(TypeMap typeMap, MapperRegistry registry)
    {
        // This is the body of the OLD Build method (the dispatcher to BuildPoco/Collection/Dictionary/Converter).
        if (typeMap.CustomConverter is not null)
            return BuildConverterLambda(typeMap);

        if (IsCollection(typeMap.SourceType) && IsCollection(typeMap.DestinationType))
            return BuildCollectionLambda(typeMap, registry);

        if (IsDictionary(typeMap.SourceType) && IsDictionary(typeMap.DestinationType))
            return BuildDictionaryLambda(typeMap, registry);

        return BuildPocoLambda(typeMap, registry);
    }

    private static LambdaExpression BuildWithInheritanceDispatch(
        LambdaExpression baseLambda,
        TypeMap typeMap,
        MapperRegistry registry)
    {
        var srcParam = baseLambda.Parameters[0];

        // Inline the base body (substitute baseLambda's parameter for our srcParam).
        // baseLambda.Parameters[0] IS srcParam after our wrap, so the body already references it.
        // No replacement needed — we use baseLambda.Body directly as the fall-through.
        Expression body = baseLambda.Body;

        // IncludedDerived is already sorted most-derived-first by InheritanceMerger.
        foreach (var derivedPair in typeMap.IncludedDerived)
        {
            var derivedSrc = derivedPair.Source;
            var derivedDst = derivedPair.Destination;

            // src is TDerivedSrc
            var typeIsExpr = Expression.TypeIs(srcParam, derivedSrc);

            // MappingInvoker.Invoke<TDerivedSrc, TDerivedDst>(registry, (TDerivedSrc)src)
            var method = typeof(MappingInvoker)
                .GetMethod(nameof(MappingInvoker.Invoke), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!
                .MakeGenericMethod(derivedSrc, derivedDst);
            var invoke = Expression.Call(
                method,
                Expression.Constant(registry),
                Expression.Convert(srcParam, derivedSrc));

            // Cast to base destination.
            var upcast = Expression.Convert(invoke, typeMap.DestinationType);

            // Conditional: src is TDerivedSrc ? upcast : body
            body = Expression.Condition(typeIsExpr, upcast, body);
        }

        // Null guard outside the dispatch chain (matches v1 idiom).
        if (typeMap.SourceType.IsClass)
        {
            body = Expression.Condition(
                Expression.ReferenceEqual(srcParam, Expression.Constant(null, typeMap.SourceType)),
                Expression.Default(typeMap.DestinationType),
                body);
        }

        var funcType = typeof(Func<,>).MakeGenericType(typeMap.SourceType, typeMap.DestinationType);
        return Expression.Lambda(funcType, body, srcParam);
    }
```

**Important**: do NOT replace the existing helper methods (`BuildPocoLambda`, `BuildCollectionLambda`, etc.) — only refactor the public `Build` to call them via `BuildBaseBody` and conditionally wrap.

If `BuildPocoLambda` already wraps in its own null guard, you'll have a double null guard for inheritance maps. That's harmless (the outer one short-circuits before the inner runs), but a cleaner option is to skip the inner null guard when `IncludedDerived.Count > 0`. For v1 of inheritance, leave the double guard — it's correct and makes the diff smaller. Optimization deferred.

- [ ] **Step 4: Run the tests — expect 8/8 pass**

Run: `dotnet test tests/Atlas.Tests/Atlas.Tests.csproj --filter "FullyQualifiedName~ExecutionPlanBuilderInheritanceTests" --nologo`

Expected: `Passed!  - Failed: 0, Passed: 8`.

If a test fails:
- Test #1 / #7 / #8 fail: `BuildWithInheritanceDispatch` not invoked. Check the `IncludedDerived.Count == 0` check in `Build`.
- Test #4 (`Build_DispatchOrder_MostDerivedFirst`): the most-derived-first ordering from Task 6 isn't being respected. Verify `InheritanceMerger.Resolve` sorts.
- Test #6 (zero-overhead): you accidentally added `TypeBinaryExpression` to the non-inheritance path. Don't apply the wrap when `IncludedDerived` is empty.

- [ ] **Step 5: Run the full suite**

Run: `dotnet test --nologo`

Expected: baseline + 8 (Task 5) + 8 (Task 6) + 6 (Task 7) + 8 (Task 9) = baseline + 30, all passing.

If a v1 test fails (collection/dictionary/POCO tests in MapperTests etc.), the refactor of `Build` introduced a bug. Investigate before proceeding.

- [ ] **Step 6: Commit**

```powershell
git add tests/Atlas.Tests/Internal/ExecutionPlanBuilderInheritanceTests.cs src/Atlas/Internal/ExecutionPlanBuilder.cs
git commit -m "Add inheritance dispatch prologue to ExecutionPlanBuilder (8 tests)"
```

---

## Task 10: `MappingInvoker.Invoke` short-circuit guard + end-to-end mapper tests (10 tests)

**Files:**
- Create: `tests/Atlas.Tests/MapperInheritanceTests.cs`
- Modify: `src/Atlas/Internal/MappingInvoker.cs` (guard the `Unsafe.As` short-circuit)

The end-to-end behavioral tests, including the load-bearing `Map_SelfMapWithIncludes` test that pins the `MappingInvoker` guard fix.

- [ ] **Step 1: Write all 10 tests**

Write `tests/Atlas.Tests/MapperInheritanceTests.cs`:
```csharp
using Atlas;

namespace Atlas.Tests;

public class MapperInheritanceTests
{
    private static IMapper BuildMapper(Action<MapperConfigurationExpression> configure)
    {
        var config = new MapperConfiguration(configure);
        return config.CreateMapper();
    }

    [Fact]
    public void Map_TypedOverload_BaseDeclared_RuntimeIsDerived_DispatchesToDerivedMap()
    {
        var mapper = BuildMapper(c =>
        {
            c.CreateMap<MiAnimal, MiAnimalDto>()
                .ForMember(d => d.DisplayName, o => o.MapFrom(s => s.Name))
                .Include<MiDog, MiDogDto>();
            c.CreateMap<MiDog, MiDogDto>();
        });
        MiAnimal a = new MiDog { Name = "rex", Breed = "Beagle" };
        var dto = mapper.Map<MiAnimal, MiAnimalDto>(a);
        Assert.IsType<MiDogDto>(dto);
        var dogDto = (MiDogDto)dto;
        Assert.Equal("rex", dogDto.DisplayName);
        Assert.Equal("Beagle", dogDto.Breed);
    }

    [Fact]
    public void Map_TypedOverload_BaseDeclared_RuntimeIsBase_UsesBaseMap()
    {
        var mapper = BuildMapper(c =>
        {
            c.CreateMap<MiAnimal, MiAnimalDto>()
                .ForMember(d => d.DisplayName, o => o.MapFrom(s => s.Name))
                .Include<MiDog, MiDogDto>();
            c.CreateMap<MiDog, MiDogDto>();
        });
        var dto = mapper.Map<MiAnimal, MiAnimalDto>(new MiAnimal { Name = "rex" });
        Assert.IsType<MiAnimalDto>(dto);
        Assert.Equal("rex", dto.DisplayName);
    }

    [Fact]
    public void Map_NestedDerivedInBaseCollection_DispatchesElementByElement()
    {
        var mapper = BuildMapper(c =>
        {
            c.CreateMap<MiAnimal, MiAnimalDto>()
                .ForMember(d => d.DisplayName, o => o.MapFrom(s => s.Name))
                .Include<MiDog, MiDogDto>()
                .Include<MiCat, MiCatDto>();
            c.CreateMap<MiDog, MiDogDto>();
            c.CreateMap<MiCat, MiCatDto>();
            c.CreateMap<List<MiAnimal>, List<MiAnimalDto>>(MemberList.None);
        });
        var animals = new List<MiAnimal>
        {
            new MiDog { Name = "rex", Breed = "Beagle" },
            new MiCat { Name = "whiskers", IsIndoor = true },
        };
        var dtos = mapper.Map<List<MiAnimal>, List<MiAnimalDto>>(animals);
        Assert.IsType<MiDogDto>(dtos[0]);
        Assert.IsType<MiCatDto>(dtos[1]);
        Assert.Equal("Beagle", ((MiDogDto)dtos[0]).Breed);
        Assert.True(((MiCatDto)dtos[1]).IsIndoor);
    }

    [Fact]
    public void Map_TwoLevelInheritance_BeagleViaAnimal_UsesBeagleMap()
    {
        var mapper = BuildMapper(c =>
        {
            c.CreateMap<MiAnimal, MiAnimalDto>()
                .ForMember(d => d.DisplayName, o => o.MapFrom(s => s.Name))
                .Include<MiDog, MiDogDto>()
                .Include<MiBeagle, MiBeagleDto>();
            c.CreateMap<MiDog, MiDogDto>().Include<MiBeagle, MiBeagleDto>();
            c.CreateMap<MiBeagle, MiBeagleDto>();
        });
        MiAnimal a = new MiBeagle { Name = "rex", Breed = "Beagle", ShortLegs = true };
        var dto = mapper.Map<MiAnimal, MiAnimalDto>(a);
        Assert.IsType<MiBeagleDto>(dto);
        Assert.True(((MiBeagleDto)dto).ShortLegs);
    }

    [Fact]
    public void Map_BaseWithIgnore_DerivedDoesNotPopulate()
    {
        // Load-bearing precedence test: base Ignore beats derived convention.
        // MiAnimal.Name normally maps to MiAnimalDto.Name by convention. Base Ignore should kill it.
        var mapper = BuildMapper(c =>
        {
            c.CreateMap<MiAnimal, MiAnimalDtoNamed>()
                .ForMember(d => d.Name, o => o.Ignore())
                .Include<MiDog, MiDogDtoNamed>();
            c.CreateMap<MiDog, MiDogDtoNamed>();
        });
        MiAnimal a = new MiDog { Name = "rex" };
        var dto = mapper.Map<MiAnimal, MiAnimalDtoNamed>(a);
        Assert.Equal("", dto.Name); // default — Ignore inherited, convention overridden
    }

    [Fact]
    public void Map_DerivedOverridesBaseMapFrom_DerivedValueAppears()
    {
        var mapper = BuildMapper(c =>
        {
            c.CreateMap<MiAnimal, MiAnimalDto>()
                .ForMember(d => d.DisplayName, o => o.MapFrom(s => s.Name))
                .Include<MiDog, MiDogDto>();
            c.CreateMap<MiDog, MiDogDto>()
                .ForMember(d => d.DisplayName, o => o.MapFrom(s => "DOG-" + s.Name));
        });
        var dto = mapper.Map<MiAnimal, MiAnimalDto>(new MiDog { Name = "rex" });
        Assert.Equal("DOG-rex", dto.DisplayName); // derived wins
    }

    [Fact]
    public void Map_DerivedInheritsBaseMapFrom_BaseValueAppears()
    {
        var mapper = BuildMapper(c =>
        {
            c.CreateMap<MiAnimal, MiAnimalDto>()
                .ForMember(d => d.DisplayName, o => o.MapFrom(s => "BASE-" + s.Name))
                .Include<MiDog, MiDogDto>();
            c.CreateMap<MiDog, MiDogDto>(); // no override
        });
        var dto = mapper.Map<MiAnimal, MiAnimalDto>(new MiDog { Name = "rex" });
        Assert.Equal("BASE-rex", dto.DisplayName); // inherited
    }

    [Fact]
    public void Map_AbstractBase_RuntimeDerivedDispatched_Succeeds()
    {
        var mapper = BuildMapper(c =>
        {
            c.CreateMap<MiAbstractAnimal, MiAnimalDto>()
                .ForMember(d => d.DisplayName, o => o.MapFrom(s => s.Name))
                .Include<MiAbstractDog, MiDogDto>();
            c.CreateMap<MiAbstractDog, MiDogDto>();
        });
        MiAbstractAnimal a = new MiAbstractDog { Name = "rex", Breed = "Beagle" };
        var dto = mapper.Map<MiAbstractAnimal, MiAnimalDto>(a);
        Assert.IsType<MiDogDto>(dto);
    }

    [Fact]
    public void Map_RuntimeTypeNotIncluded_FallsThroughToBase()
    {
        var mapper = BuildMapper(c =>
        {
            c.CreateMap<MiAnimal, MiAnimalDto>()
                .ForMember(d => d.DisplayName, o => o.MapFrom(s => s.Name))
                .Include<MiDog, MiDogDto>();
            c.CreateMap<MiDog, MiDogDto>();
            // MiCat NOT included
        });
        MiAnimal a = new MiCat { Name = "whiskers" };
        var dto = mapper.Map<MiAnimal, MiAnimalDto>(a);
        // Falls through to base Animal -> AnimalDto map.
        Assert.IsType<MiAnimalDto>(dto);
        Assert.Equal("whiskers", dto.DisplayName);
    }

    [Fact]
    public void Map_SelfMapWithIncludes_DispatchChainExecutes_NotUnsafeAsShortCircuit()
    {
        // CRITICAL: MiAnimal -> MiAnimal (same type both sides) with Include<MiDog, MiDog>().
        // v1's MappingInvoker.Invoke short-circuits on typeof(TSource) == typeof(TDestination)
        // via Unsafe.As, returning the source unchanged. With Includes, that short-circuit
        // would skip the dispatch chain. The fix (Step 4) guards the short-circuit.
        var mapper = BuildMapper(c =>
        {
            c.CreateMap<MiAnimal, MiAnimal>(MemberList.None)
                .Include<MiDog, MiDog>();
            c.CreateMap<MiDog, MiDog>(MemberList.None)
                .ForMember(d => d.Name, o => o.MapFrom(s => "CLONED-" + s.Name));
        });
        MiAnimal a = new MiDog { Name = "rex" };
        var result = mapper.Map<MiAnimal, MiAnimal>(a);
        Assert.NotSame(a, result); // dispatch chain ran, NOT identity short-circuit
        Assert.Equal("CLONED-rex", result.Name);
    }
}

// ---- Test fixtures ----
public class MiAnimal { public string Name { get; set; } = ""; }
public class MiDog : MiAnimal { public string Breed { get; set; } = ""; }
public class MiBeagle : MiDog { public bool ShortLegs { get; set; } }
public class MiCat : MiAnimal { public bool IsIndoor { get; set; } }

public class MiAnimalDto { public string DisplayName { get; set; } = ""; }
public class MiDogDto : MiAnimalDto { public string Breed { get; set; } = ""; }
public class MiBeagleDto : MiDogDto { public bool ShortLegs { get; set; } }
public class MiCatDto : MiAnimalDto { public bool IsIndoor { get; set; } }

// For the Ignore test, separate DTO with Name (matching source) instead of DisplayName.
public class MiAnimalDtoNamed { public string Name { get; set; } = ""; }
public class MiDogDtoNamed : MiAnimalDtoNamed { public string Breed { get; set; } = ""; }

public abstract class MiAbstractAnimal { public string Name { get; set; } = ""; }
public class MiAbstractDog : MiAbstractAnimal { public string Breed { get; set; } = ""; }
```

- [ ] **Step 2: Run the tests; expect 9/10 fail (test #10 fails specifically due to MappingInvoker)**

Run: `dotnet test tests/Atlas.Tests/Atlas.Tests.csproj --filter "FullyQualifiedName~MapperInheritanceTests" --nologo`

Expected: 9 fail (the typed `Map<TSrc, TDst>` overload doesn't yet pick up the ExecutionPlanBuilder dispatch — wait, actually it DOES, since the lambda for the base map IS the one with the dispatch prologue). Let me re-read.

Actually: `mapper.Map<MiAnimal, MiAnimalDto>(actuallyADog)` calls `MappingInvoker.Invoke<MiAnimal, MiAnimalDto>(registry, source)`. That looks up the `(MiAnimal, MiAnimalDto)` TypeMap, gets its compiled delegate (the lambda with the dispatch prologue from Task 9), and invokes it with the dog as parameter. The dispatch prologue runs `is MiDog` and routes to `Invoke<MiDog, MiDogDto>` accordingly. So tests 1-8 should pass once Tasks 5-9 are landed.

Test #10 specifically targets `MiAnimal -> MiAnimal` (self-map). `MappingInvoker.Invoke<MiAnimal, MiAnimal>` short-circuits because `typeof(TSource) == typeof(TDestination)`, returning the source via `Unsafe.As`. The compiled lambda — including the dispatch chain — is never invoked. So test #10 fails until Step 4.

**Expected actual outcome at Step 2**: tests 1-9 PASS, test 10 FAILS. (Tests 1-9 don't exercise self-mapping.)

If tests 1-9 fail with you, something in Tasks 5-9 was wrong. Investigate before applying the Step 4 fix.

- [ ] **Step 3: Run only test #10 to confirm the self-map issue**

Run: `dotnet test tests/Atlas.Tests/Atlas.Tests.csproj --filter "FullyQualifiedName=Atlas.Tests.MapperInheritanceTests.Map_SelfMapWithIncludes_DispatchChainExecutes_NotUnsafeAsShortCircuit" --nologo`

Expected: FAIL with `Assert.NotSame` failing — the result IS the source object, because `Unsafe.As` short-circuited.

- [ ] **Step 4: Guard the `MappingInvoker.Invoke` short-circuit**

Open `src/Atlas/Internal/MappingInvoker.cs`. Find the `Invoke<TSource, TDestination>` method. The current short-circuit logic is around line 30-34:
```csharp
        // No map registered. Identity short-circuit covers nested-call sites (typically primitives
        // appearing as collection/dictionary elements where the user didn't register an explicit map).
        // Allocation-free via Unsafe.As<,> for both reference and value types.
        if (typeof(TSource) == typeof(TDestination))
            return System.Runtime.CompilerServices.Unsafe.As<TSource, TDestination>(ref source);
```

This block is inside an `if (registry.GetTypeMap(pair) is not null)` ELSE branch — i.e., it only fires when no map is registered for the pair. **However**, there's a subtler issue: the order of checks matters. Currently:

```csharp
        if (registry.TryGetDelegate(pair, out var cached) && cached is Func<TSource, TDestination> typed)
            return typed(source);

        if (registry.GetTypeMap(pair) is not null)
        {
            // compile + invoke
        }

        if (typeof(TSource) == typeof(TDestination))
            return Unsafe.As<TSource, TDestination>(ref source);
```

If `(MiAnimal, MiAnimal)` IS registered as a TypeMap, the first OR second block fires correctly — the compiled lambda runs the dispatch chain. So actually the short-circuit only fires when NO map is registered. Test #10 above DOES register `MiAnimal -> MiAnimal`, so the short-circuit shouldn't fire.

**Re-verify**: read the current `Invoke` carefully. If the test fails at Step 3, find the actual culprit:

- (a) The map IS registered but `TryGetDelegate` returns false (delegate cache miss) AND `GetOrCompile` somehow doesn't run AND the short-circuit fires.
- (b) The dispatch chain IS being run but the inner `MappingInvoker.Invoke<MiDog, MiDog>` is the one short-circuiting (since MiDog -> MiDog is also a self-pair).

**Most likely**: it's case (b). The dispatch chain calls `MappingInvoker.Invoke<MiDog, MiDog>(registry, dog)` for the derived dispatch. That call falls through to the `Unsafe.As<MiDog, MiDog>` short-circuit IF `(MiDog, MiDog)` doesn't have a registered TypeMap. But the test registers `(MiDog, MiDog)`, so the second branch should fire and compile/invoke the derived lambda.

If the test still fails, the issue is that `(MiDog, MiDog)` is registered but doesn't have its own dispatch prologue (it has no Includes), so its lambda runs the existing v1 POCO body — which for self-map types produces what? Let me trace: `BuildPocoLambda` for `MiDog -> MiDog` constructs a new `MiDog`, copies all properties via `ForMember`. The test's `ForMember(Name -> "CLONED-" + s.Name)` is on the derived map. So the derived lambda should execute that ForMember. The result should NOT be the original `dog` instance.

If after re-tracing and running Step 3 the test still fails, apply this defensive guard regardless. Replace:

```csharp
        if (typeof(TSource) == typeof(TDestination))
            return System.Runtime.CompilerServices.Unsafe.As<TSource, TDestination>(ref source);
```

with:

```csharp
        if (typeof(TSource) == typeof(TDestination))
        {
            // Defensive: if a TypeMap was registered for this self-pair (typically with Includes
            // or custom config), it should already have been hit by the cached-delegate or
            // GetOrCompile branches above. Fall-through to short-circuit only when no map exists.
            return System.Runtime.CompilerServices.Unsafe.As<TSource, TDestination>(ref source);
        }
```

**If after careful tracing the existing logic is already correct and test #10 passes WITHOUT a fix, that's even better.** Skip Step 4's code change and just commit the test as a regression-locking guard. Note this in the commit message.

- [ ] **Step 5: Run all 10 tests**

Run: `dotnet test tests/Atlas.Tests/Atlas.Tests.csproj --filter "FullyQualifiedName~MapperInheritanceTests" --nologo`

Expected: `Passed!  - Failed: 0, Passed: 10`.

- [ ] **Step 6: Run the full suite — must be all green**

Run: `dotnet test --nologo`

Expected: baseline + 8 + 8 + 6 + 8 + 10 = baseline + 40 tests, all passing.

- [ ] **Step 7: Commit**

```powershell
git add tests/Atlas.Tests/MapperInheritanceTests.cs
# Add MappingInvoker.cs ONLY if Step 4 modified it.
git add src/Atlas/Internal/MappingInvoker.cs 2>$null
git commit -m "Add MapperInheritanceTests (10 e2e tests; pins MappingInvoker self-map behavior)"
```

---

## Task 11: Coverage check + README + memory updates

**Files:**
- Modify: `README.md` (add inheritance section)
- Modify: `C:\Users\ajsde\.claude\projects\C--Repos-Atlas\memory\atlas_v2_design_docs_deferred.md` (cross out item #2)

- [ ] **Step 1: Run coverage**

Run: `dotnet test tests/Atlas.Tests/Atlas.Tests.csproj --collect:"XPlat Code Coverage" --nologo`

Then: `reportgenerator -reports:tests/Atlas.Tests/TestResults/**/coverage.cobertura.xml -targetdir:coverage-inheritance -reporttypes:TextSummary`

Read `coverage-inheritance/Summary.txt`. Confirm:
- `Atlas` line coverage ≥ 90%
- `Atlas` branch coverage close to ≥ 80% (will likely improve from 75.49% baseline because the new InheritanceMerger has high branch coverage)

If branch coverage drops below 75%, look for unreachable arms in `InheritanceMerger.Resolve` or `ConfigurationValidator.ValidateInheritance`. Add targeted tests if needed.

- [ ] **Step 2: Update root README**

Open `README.md`. After the "Dependency injection" section (or wherever inheritance fits in the doc structure — probably right after "Queryable projection" since both are advanced features), insert a new section:

```markdown
## Inheritance & polymorphism

Atlas dispatches on runtime type when you declare derived maps via `Include` (on the base map) or `IncludeBase` (on the derived map):

```csharp
cfg.CreateMap<Animal, AnimalDto>()
   .Include<Dog, DogDto>()
   .Include<Cat, CatDto>();
cfg.CreateMap<Dog, DogDto>();
cfg.CreateMap<Cat, CatDto>();

Animal a = new Dog { Name = "rex", Breed = "Beagle" };
AnimalDto dto = mapper.Map<Animal, AnimalDto>(a);
// dto is actually a DogDto.
```

Polymorphic collections work transparently — `List<Animal>` containing mixed Dog/Cat instances maps element-by-element to a `List<AnimalDto>` containing DogDto/CatDto.

Member configuration on the base map flows to derived maps with the standard precedence:
1. Derived's explicit `MapFrom` / `Ignore` wins
2. Base's explicit `MapFrom` / `Ignore` is inherited
3. Convention-based match on the derived map fills the rest

**Foot-gun**: an explicit `Ignore` on the base **overrides** convention on the derived. If you ignore `Animal.Name` on the base map, Dog will also ignore `Name` even if Dog has a matching `Name` property. This is the standard semantics (consistent with AutoMapper) but commonly catches people out — keep it in mind when refactoring inheritance.

**ProjectTo limitation (v1)**: today's `Atlas.Projections` package is unaware of `Include` declarations. A `query.ProjectTo<AnimalDto>(cfg)` against a polymorphic `DbSet<Animal>` projects every row as `AnimalDto` — derived rows lose their derived shape silently. A future v3 design will lift this limitation.

See `docs/Atlas-Design-Inheritance.md` for the full design.
```

Also update the Coverage table at the bottom of README to reflect post-inheritance numbers (use the actual numbers from Step 1):

```markdown
| `Atlas` | <line%> | <branch%> | Met. The `HasImplicitNumericConversion` switch was consolidated into `Atlas.Internal.NumericConversions` and is now exercised once via `[Theory]`. Inheritance support adds the `InheritanceMerger` and `ValidateInheritance` paths. |
```

- [ ] **Step 3: Update memory**

Read `C:\Users\ajsde\.claude\projects\C--Repos-Atlas\memory\atlas_v2_design_docs_deferred.md`. Change item #2 from:
```markdown
2. Inheritance & polymorphism (`Include` / `IncludeBase`, runtime type dispatch).
```
to:
```markdown
2. ~~Inheritance & polymorphism (`Include` / `IncludeBase`, runtime type dispatch)~~ — **shipped** (see `docs/Atlas-Design-Inheritance.md`).
```

- [ ] **Step 4: Commit README**

```powershell
git add README.md
git commit -m "docs: README — add inheritance section, refresh coverage numbers"
```

(Memory file lives outside the git repo — no `git add` for it.)

- [ ] **Step 5: Final sanity check**

Run: `dotnet test --nologo`

Expected: baseline + 40 tests, all passing.

Run: `git log --oneline main..HEAD | head -20`

Expected: a clean sequence of ~10 commits (one per task), most TDD-shaped (tests + impl + sometimes a small fix).

---

## Done

When this plan is complete:
- `Atlas` core ships with full inheritance & polymorphism support per spec.
- ~40 new tests pass (8 merger + 8 config + 6 validation + 8 builder + 10 e2e), full suite green.
- README documents the feature, including the foot-gun callout and the ProjectTo limitation.
- 11 v2 features remain in the deferred memory list (item #1 ProjectTo and item #2 inheritance both shipped).
- Branch `feat/inheritance` is ready for the finishing-a-development-branch flow (push + PR).
