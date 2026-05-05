# Atlas v2 Conditional Mapping — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add per-member `PreCondition` and `Condition` predicate gates to Atlas so users can declare *"map this member only when …"* in a single fluent block, with the same predicates working both for compiled `Map<>()` calls and for `ProjectTo<>()` SQL translation.

**Architecture:** Two new nullable `LambdaExpression?` fields on `PropertyMap`. Two helpers added to `ExecutionPlanBuilder` (one for fresh-map / one for update-in-place) and one to `ProjectionPlanBuilder`. Inheritance propagation rides the existing `InheritanceMerger.CopyConfig` path. No new types, no new build-time pass, no validator rules, no new projection rejections.

**Tech Stack:** .NET 10, C# 14 (preview), xUnit v3 (no FluentAssertions — `Assert.X()` only), `System.Linq.Expressions`, EF Core (in-memory + SQLite for projection E2E).

**Branch & merge:** Cut `feat/conditional-mapping` from `main` HEAD (currently `a1bb20a`, the design-doc commit). All 10 tasks land on this branch; final review then PR to `main`.

**Specs to read alongside this plan:**
- `C:\Repos\Atlas\docs\Atlas-Design-ConditionalMapping.md` (the approved design — every code section in this plan implements something specified there).
- `C:\Repos\Atlas\docs\Atlas-Design.md` §4 (existing PropertyMap / IMemberConfigurationExpression conventions).
- `C:\Repos\Atlas\docs\Atlas-Design-ValueTransformers.md` (the pattern this feature most closely resembles in shape).

---

## File Map

**Production code modified:**
- `src/Atlas/Internal/PropertyMap.cs` — add two `LambdaExpression?` fields.
- `src/Atlas/Configuration/IMemberConfigurationExpression.cs` — add two interface methods with full XML docs.
- `src/Atlas/Configuration/MemberConfigurationExpression.cs` — implement two methods, extend `ApplyTo`.
- `src/Atlas/Internal/InheritanceMerger.cs` — extend `CopyConfig` with two field copies.
- `src/Atlas/Internal/ExecutionPlanBuilder.cs` — add helpers + wire into ctor-arg loop, property-assign loop, and `BuildUpdate`. Light refactor of `BuildNestedAssign` so the leaf-access can be reused by the gated update path.
- `src/Atlas.Projections/Internal/ProjectionPlanBuilder.cs` — add helper + wire into ctor-arg loop and property-binding loop in `BuildBody`.
- `README.md` — add Conditional Mapping subsection + refresh test count.

**Production code NOT modified (deliberate per design §2.2):**
- `src/Atlas/Internal/ConfigurationValidator.cs` — no new rules.
- `src/Atlas/Internal/TypeMap.cs` — no new fields.
- `src/Atlas/Internal/ConventionEngine.cs`, `ReverseMapMirror.cs`, `MapperRegistry.cs` — unchanged.
- `src/Atlas/MapperConfiguration.cs` — no new build-time call.
- `src/Atlas.Projections/Internal/ProjectionCompatibility.cs` — predicates are NOT a projection rejection.

**Test code added:**
- `tests/Atlas.Tests/Internal/PropertyMapConditionTests.cs` — 3 unit tests over the new fields.
- `tests/Atlas.Tests/MappingExpressionConditionTests.cs` — 7 tests over the fluent surface.
- `tests/Atlas.Tests/Internal/InheritanceMergerConditionTests.cs` — 3 tests over base→derived propagation.
- `tests/Atlas.Tests/ExecutionPlanBuilderConditionTests.cs` — 6 tests over fresh-map codegen behavior (executed via `IMapper.Map<>()`).
- `tests/Atlas.Tests/ExecutionPlanBuilderCtorParamConditionTests.cs` — 2 tests over ctor-param fallback semantics.
- `tests/Atlas.Tests/ExecutionPlanBuilderUpdateConditionTests.cs` — 4 tests over update-in-place semantics.
- `tests/Atlas.Tests/MapperConditionTests.cs` — 5 end-to-end tests over real `IMapper` (the headline reference-doc example + interaction tests).
- `tests/Atlas.Projections.Tests/Internal/ProjectionPlanBuilderConditionTests.cs` — 4 tests over projection-codegen expression-tree shape.
- `tests/Atlas.Projections.Tests.EFCore/ProjectTo_ConditionTests.cs` — 2 EF Core SQLite E2E tests.

**Total new tests: ~36**. Test baseline goes from **396 → ~432**.

---

## Task 0 — Branch setup (5 min, controller-only)

Cut the feature branch and verify clean starting state. Not a TDD task — purely environmental.

**Files:** none modified.

- [ ] **Step 1: Verify on main, clean tree, design committed.**

```bash
cd C:/Repos/Atlas
git status
git log --oneline -3
```
Expected: `On branch main`, working tree clean, top commit is the design doc (`a1bb20a docs: design for Atlas v2 #7 Conditional Mapping ...`).

- [ ] **Step 2: Cut feature branch.**

```bash
git checkout -b feat/conditional-mapping
```
Expected: `Switched to a new branch 'feat/conditional-mapping'`.

- [ ] **Step 3: Confirm full build + tests still green on the branch (regression baseline).**

```bash
dotnet build C:/Repos/Atlas/Atlas.slnx -c Debug
dotnet test C:/Repos/Atlas/Atlas.slnx -c Debug --no-build
```
Expected: build succeeds; **396** tests pass across all test projects (327 Atlas.Tests + 60 Atlas.Projections.Tests + 8 Atlas.Projections.Tests.EFCore + 1 Internal-private test).

If the count differs, **stop and reconcile** — the baseline must match before adding new tests.

---

## Task 1 — `PropertyMap` fields

Add the two nullable `LambdaExpression?` fields to the `PropertyMap` data shape. This is the smallest possible incremental change: the fields exist and default to null. No consumers yet.

**Files:**
- Modify: `src/Atlas/Internal/PropertyMap.cs`
- Create: `tests/Atlas.Tests/Internal/PropertyMapConditionTests.cs`

**Allowlist for the implementer:** ONLY the two files above. Do not touch `MemberConfigurationExpression`, `ExecutionPlanBuilder`, etc. — those are subsequent tasks.

- [ ] **Step 1: Write the failing tests.**

Create `tests/Atlas.Tests/Internal/PropertyMapConditionTests.cs`:

```csharp
using System.Linq.Expressions;
using Atlas.Internal;

namespace Atlas.Tests.Internal;

public class PropertyMapConditionTests
{
    private sealed class S { public int V { get; set; } }
    private sealed class D { public int V { get; set; } }

    [Fact]
    public void NewPropertyMap_PreCondition_DefaultsToNull()
    {
        var prop = typeof(D).GetProperty(nameof(D.V))!;
        var pm = PropertyMap.ForProperty(prop);

        Assert.Null(pm.PreCondition);
    }

    [Fact]
    public void NewPropertyMap_Condition_DefaultsToNull()
    {
        var prop = typeof(D).GetProperty(nameof(D.V))!;
        var pm = PropertyMap.ForProperty(prop);

        Assert.Null(pm.Condition);
    }

    [Fact]
    public void PropertyMap_AcceptsBothPredicates()
    {
        var prop = typeof(D).GetProperty(nameof(D.V))!;
        var pm = PropertyMap.ForProperty(prop);

        Expression<Func<S, bool>> pre = s => s.V > 0;
        Expression<Func<S, int, bool>> cond = (s, v) => v < 100;

        pm.PreCondition = pre;
        pm.Condition = cond;

        Assert.Same(pre, pm.PreCondition);
        Assert.Same(cond, pm.Condition);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj --filter "FullyQualifiedName~Atlas.Tests.Internal.PropertyMapConditionTests"
```
Expected: build error (`'PropertyMap' does not contain a definition for 'PreCondition'` and `'Condition'`).

- [ ] **Step 3: Add the two fields to `PropertyMap`.**

In `src/Atlas/Internal/PropertyMap.cs`, after the existing `DestinationPath` property (around line 40), add:

```csharp
    /// <summary>
    /// Predicate evaluated BEFORE source-side resolution. Null when no PreCondition was
    /// set on this binding. Stored as <see cref="LambdaExpression"/> so codegen can inline
    /// the body (parameter-substitution) for both in-memory mapping and IQueryable
    /// projection. Concrete signature: <c>Expression&lt;Func&lt;TSource, bool&gt;&gt;</c>.
    /// </summary>
    public LambdaExpression? PreCondition { get; set; }

    /// <summary>
    /// Predicate evaluated AFTER source-side resolution. Null when no Condition was set
    /// on this binding. Concrete signature:
    /// <c>Expression&lt;Func&lt;TSource, TMember, bool&gt;&gt;</c> — the second parameter
    /// receives the resolved-value sub-expression at codegen time.
    /// </summary>
    public LambdaExpression? Condition { get; set; }
```

- [ ] **Step 4: Run the tests to verify they pass.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj --filter "FullyQualifiedName~Atlas.Tests.Internal.PropertyMapConditionTests"
```
Expected: 3/3 PASS.

- [ ] **Step 5: Run the full Atlas.Tests project to confirm no regressions.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj
```
Expected: 327 pre-existing + 3 new = 330 PASS.

- [ ] **Step 6: Commit.**

```bash
git add src/Atlas/Internal/PropertyMap.cs tests/Atlas.Tests/Internal/PropertyMapConditionTests.cs
git commit -m "$(cat <<'EOF'
PropertyMap gains PreCondition and Condition fields (3 tests)

Two new LambdaExpression? fields. Default to null; no consumers yet —
subsequent tasks plumb the fluent surface, codegen, and inheritance.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 2 — Fluent surface (`PreCondition` / `Condition` methods)

Add the two methods to `IMemberConfigurationExpression`, implement in `MemberConfigurationExpression`, and extend `ApplyTo` so the predicates land on the `PropertyMap`.

**Files:**
- Modify: `src/Atlas/Configuration/IMemberConfigurationExpression.cs`
- Modify: `src/Atlas/Configuration/MemberConfigurationExpression.cs`
- Create: `tests/Atlas.Tests/MappingExpressionConditionTests.cs`

**Allowlist for the implementer:** ONLY the three files above.

- [ ] **Step 1: Write the failing tests.**

Create `tests/Atlas.Tests/MappingExpressionConditionTests.cs`:

```csharp
using System.Linq.Expressions;
using Atlas.Configuration;
using Atlas.Internal;

namespace Atlas.Tests;

public class MappingExpressionConditionTests
{
    public sealed class S { public int V { get; set; } }
    public sealed class D { public int V { get; set; } }

    private static MappingExpression<S, D> NewExpr() =>
        new(new TypeMap(typeof(S), typeof(D), MemberList.None));

    [Fact]
    public void PreCondition_StoredOnPropertyMap()
    {
        var expr = NewExpr();
        Expression<Func<S, bool>> pre = s => s.V > 0;

        expr.ForMember(d => d.V, opt => opt.PreCondition(pre));

        var pm = expr.TypeMap.PropertyMaps.Single(p => p.Name == nameof(D.V));
        Assert.Same(pre, pm.PreCondition);
    }

    [Fact]
    public void Condition_StoredOnPropertyMap()
    {
        var expr = NewExpr();
        Expression<Func<S, int, bool>> cond = (s, v) => v < 100;

        expr.ForMember(d => d.V, opt => opt.Condition(cond));

        var pm = expr.TypeMap.PropertyMaps.Single(p => p.Name == nameof(D.V));
        Assert.Same(cond, pm.Condition);
    }

    [Fact]
    public void BothPredicates_BothStored()
    {
        var expr = NewExpr();
        Expression<Func<S, bool>> pre = s => s.V > 0;
        Expression<Func<S, int, bool>> cond = (s, v) => v < 100;

        expr.ForMember(d => d.V, opt =>
        {
            opt.PreCondition(pre);
            opt.Condition(cond);
        });

        var pm = expr.TypeMap.PropertyMaps.Single(p => p.Name == nameof(D.V));
        Assert.Same(pre, pm.PreCondition);
        Assert.Same(cond, pm.Condition);
    }

    [Fact]
    public void PreCondition_LastCallWins()
    {
        var expr = NewExpr();
        Expression<Func<S, bool>> first = s => s.V > 0;
        Expression<Func<S, bool>> second = s => s.V > 10;

        expr.ForMember(d => d.V, opt =>
        {
            opt.PreCondition(first);
            opt.PreCondition(second);
        });

        var pm = expr.TypeMap.PropertyMaps.Single(p => p.Name == nameof(D.V));
        Assert.Same(second, pm.PreCondition);
    }

    [Fact]
    public void Condition_LastCallWins()
    {
        var expr = NewExpr();
        Expression<Func<S, int, bool>> first = (s, v) => v < 100;
        Expression<Func<S, int, bool>> second = (s, v) => v < 50;

        expr.ForMember(d => d.V, opt =>
        {
            opt.Condition(first);
            opt.Condition(second);
        });

        var pm = expr.TypeMap.PropertyMaps.Single(p => p.Name == nameof(D.V));
        Assert.Same(second, pm.Condition);
    }

    [Fact]
    public void PreCondition_NullPredicate_Throws()
    {
        var expr = NewExpr();
        Assert.Throws<ArgumentNullException>(() =>
            expr.ForMember(d => d.V, opt => opt.PreCondition(null!)));
    }

    [Fact]
    public void Condition_NullPredicate_Throws()
    {
        var expr = NewExpr();
        Assert.Throws<ArgumentNullException>(() =>
            expr.ForMember(d => d.V, opt => opt.Condition(null!)));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj --filter "FullyQualifiedName~Atlas.Tests.MappingExpressionConditionTests"
```
Expected: build error (`'IMemberConfigurationExpression<S, D, int>' does not contain a definition for 'PreCondition'`).

- [ ] **Step 3: Add the two interface methods.**

In `src/Atlas/Configuration/IMemberConfigurationExpression.cs`, replace the entire file body (within the existing `namespace Atlas.Configuration;` declaration) with:

```csharp
using System.Linq.Expressions;

namespace Atlas.Configuration;

/// <summary>
/// Per-member fluent surface inside a <c>ForMember</c> or <c>ForCtorParam</c> options callback.
/// </summary>
public interface IMemberConfigurationExpression<TSource, TDestination, TMember>
{
    /// <summary>Map this destination member from an arbitrary expression on the source.</summary>
    void MapFrom<TSourceMember>(Expression<Func<TSource, TSourceMember>> sourceMember);

    /// <summary>Map this destination member from a constant value.</summary>
    void MapFrom(TMember constantValue);

    /// <summary>Skip this destination member entirely (also removes it from validation).</summary>
    void Ignore();

    /// <summary>
    /// Predicate evaluated BEFORE source-side resolution. If the predicate returns false,
    /// the destination member is not mapped — for fresh <c>Map&lt;TDest&gt;(src)</c> the
    /// property remains at its default value; for update-in-place
    /// <c>Map&lt;TS,TD&gt;(src, existingDest)</c> the existing destination value is preserved.
    /// Use when source-side resolution is expensive and would be wasted work if the predicate
    /// fails.
    /// </summary>
    /// <remarks>
    /// Stored as <see cref="Expression{TDelegate}"/> so the predicate participates in both
    /// in-memory mapping and IQueryable projection. In <c>ProjectTo&lt;&gt;()</c>, the predicate
    /// becomes part of a LINQ <see cref="System.Linq.Expressions.ConditionalExpression"/> that the
    /// underlying provider translates to SQL (typically <c>CASE WHEN</c>). Untranslatable
    /// predicates fail at query-execution time with the provider's standard error — Atlas does
    /// not pre-inspect lambdas for translatability.
    /// <para>
    /// Multiple <c>PreCondition</c> calls on the same member: last-call-wins (matches
    /// <c>MapFrom</c>). Repeating clears the prior predicate.
    /// </para>
    /// <para>
    /// On a map configured with <see cref="IMappingExpression{TSource, TDestination}.ConvertUsing(Func{TSource, TDestination})"/>,
    /// per-member predicates are silently inactive (the converter replaces all per-member assigns).
    /// On a constructor-parameter binding (<c>ForCtorParam</c>), predicate-fail produces the
    /// parameter's declared default value (<c>p.HasDefaultValue ? p.DefaultValue : default(T)</c>)
    /// rather than skipping the assignment, because a constructor argument cannot be omitted.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="predicate"/> is null.</exception>
    void PreCondition(Expression<Func<TSource, bool>> predicate);

    /// <summary>
    /// Predicate evaluated AFTER source-side resolution but BEFORE assignment. The second
    /// argument is the resolved value (the result of <c>MapFrom</c> / source path / value
    /// transformers). If the predicate returns false, the destination member is not assigned
    /// — same skip semantics as <see cref="PreCondition"/>. Use when the predicate depends
    /// on the resolved value (e.g., "only assign if the resolved value is non-empty").
    /// </summary>
    /// <remarks>
    /// See <see cref="PreCondition"/> for storage, projection, multi-call, ConvertUsing,
    /// and ForCtorParam semantics — they apply identically.
    /// <para>
    /// The resolved sub-expression is hoisted into a local variable in the in-memory codegen
    /// so it is evaluated only once per call, regardless of how many times the predicate
    /// references the resolved value. In projection codegen the resolved expression is
    /// inlined twice (once for the predicate test, once for the assigned value); LINQ
    /// providers handle this fine.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="predicate"/> is null.</exception>
    void Condition(Expression<Func<TSource, TMember, bool>> predicate);
}
```

- [ ] **Step 4: Implement the two methods + extend `ApplyTo`.**

In `src/Atlas/Configuration/MemberConfigurationExpression.cs`, replace the entire file body with:

```csharp
using System.Linq.Expressions;
using Atlas.Internal;

namespace Atlas.Configuration;

/// <summary>
/// Captures the per-member configuration declared inside a single <c>ForMember</c> /
/// <c>ForCtorParam</c> options callback, then applies it to a <see cref="PropertyMap"/>.
/// Last-call-wins for repeated calls inside the same callback.
/// </summary>
internal sealed class MemberConfigurationExpression<TSource, TDestination, TMember>
    : IMemberConfigurationExpression<TSource, TDestination, TMember>
{
    private LambdaExpression? _customExpression;
    private object? _constantValue;
    private bool _hasConstant;
    private bool _ignored;
    private LambdaExpression? _preCondition;
    private LambdaExpression? _condition;

    public void MapFrom<TSourceMember>(Expression<Func<TSource, TSourceMember>> sourceMember)
    {
        _customExpression = sourceMember;
        _constantValue = null;
        _hasConstant = false;
        _ignored = false;
    }

    public void MapFrom(TMember constantValue)
    {
        _constantValue = constantValue;
        _hasConstant = true;
        _customExpression = null;
        _ignored = false;
    }

    public void Ignore()
    {
        _ignored = true;
        _customExpression = null;
        _constantValue = null;
        _hasConstant = false;
    }

    public void PreCondition(Expression<Func<TSource, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _preCondition = predicate;
    }

    public void Condition(Expression<Func<TSource, TMember, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _condition = predicate;
    }

    public void ApplyTo(PropertyMap propertyMap)
    {
        propertyMap.SourcePath = null;
        propertyMap.CustomExpression = _customExpression;
        propertyMap.ConstantValue = _constantValue;
        propertyMap.HasConstant = _hasConstant;
        propertyMap.Ignored = _ignored;
        propertyMap.PreCondition = _preCondition;
        propertyMap.Condition = _condition;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj --filter "FullyQualifiedName~Atlas.Tests.MappingExpressionConditionTests"
```
Expected: 7/7 PASS.

- [ ] **Step 6: Run all Atlas.Tests to confirm no regressions.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj
```
Expected: 330 pre-existing (incl. Task 1's 3) + 7 new = 337 PASS.

- [ ] **Step 7: Commit.**

```bash
git add src/Atlas/Configuration/IMemberConfigurationExpression.cs src/Atlas/Configuration/MemberConfigurationExpression.cs tests/Atlas.Tests/MappingExpressionConditionTests.cs
git commit -m "$(cat <<'EOF'
IMemberConfigurationExpression gains PreCondition and Condition (7 tests)

Both methods stored as Expression<Func<...>> so they participate in projection
translation. Last-call-wins matches MapFrom semantics. Null-arg checks via
ArgumentNullException.ThrowIfNull.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3 — Inheritance propagation (`InheritanceMerger.CopyConfig`)

Two-line extension to the existing config-merge helper. Base→derived predicate flow rides the existing `IsExplicit` precedence machinery — no new logic in `MergeBaseConfig`.

**Files:**
- Modify: `src/Atlas/Internal/InheritanceMerger.cs`
- Create: `tests/Atlas.Tests/Internal/InheritanceMergerConditionTests.cs`

**Allowlist for the implementer:** ONLY the two files above.

- [ ] **Step 1: Write the failing tests.**

Create `tests/Atlas.Tests/Internal/InheritanceMergerConditionTests.cs`:

```csharp
using System.Linq.Expressions;
using Atlas.Internal;

namespace Atlas.Tests.Internal;

public class InheritanceMergerConditionTests
{
    public class Animal { public int Legs { get; set; } }
    public class Dog : Animal { }
    public class AnimalDto { public int Legs { get; set; } }
    public class DogDto : AnimalDto { }

    [Fact]
    public void BasePreCondition_PropagatesToDerived_WhenDerivedHasNoExplicit()
    {
        // Base: explicit ForMember on Legs with PreCondition.
        var animalTm = new TypeMap(typeof(Animal), typeof(AnimalDto), MemberList.None);
        var basePm = PropertyMap.ForProperty(typeof(AnimalDto).GetProperty(nameof(AnimalDto.Legs))!);
        Expression<Func<Animal, bool>> basePre = s => s.Legs > 0;
        basePm.SourcePath = new SourceMemberPath(new[] { typeof(Animal).GetProperty(nameof(Animal.Legs))! });
        basePm.PreCondition = basePre;
        basePm.IsExplicit = true;
        animalTm.PropertyMaps.Add(basePm);

        // Derived: no PropertyMap for Legs yet (will get one via merger).
        var dogTm = new TypeMap(typeof(Dog), typeof(DogDto), MemberList.None);
        dogTm.IncludedBases.Add(animalTm.Pair);

        var typeMaps = new List<TypeMap> { animalTm, dogTm };
        var pairIndex = typeMaps.ToDictionary(t => t.Pair);

        InheritanceMerger.Resolve(typeMaps, pairIndex);

        var derivedPm = dogTm.PropertyMaps.Single(p => p.Name == nameof(AnimalDto.Legs));
        Assert.Same(basePre, derivedPm.PreCondition);
    }

    [Fact]
    public void BaseCondition_PropagatesToDerived_WhenDerivedHasNoExplicit()
    {
        var animalTm = new TypeMap(typeof(Animal), typeof(AnimalDto), MemberList.None);
        var basePm = PropertyMap.ForProperty(typeof(AnimalDto).GetProperty(nameof(AnimalDto.Legs))!);
        Expression<Func<Animal, int, bool>> baseCond = (s, v) => v < 100;
        basePm.SourcePath = new SourceMemberPath(new[] { typeof(Animal).GetProperty(nameof(Animal.Legs))! });
        basePm.Condition = baseCond;
        basePm.IsExplicit = true;
        animalTm.PropertyMaps.Add(basePm);

        var dogTm = new TypeMap(typeof(Dog), typeof(DogDto), MemberList.None);
        dogTm.IncludedBases.Add(animalTm.Pair);

        var typeMaps = new List<TypeMap> { animalTm, dogTm };
        var pairIndex = typeMaps.ToDictionary(t => t.Pair);

        InheritanceMerger.Resolve(typeMaps, pairIndex);

        var derivedPm = dogTm.PropertyMaps.Single(p => p.Name == nameof(AnimalDto.Legs));
        Assert.Same(baseCond, derivedPm.Condition);
    }

    [Fact]
    public void DerivedExplicit_OverridesBaseExplicit_ForBothPredicates()
    {
        // Base sets both predicates.
        var animalTm = new TypeMap(typeof(Animal), typeof(AnimalDto), MemberList.None);
        var basePm = PropertyMap.ForProperty(typeof(AnimalDto).GetProperty(nameof(AnimalDto.Legs))!);
        Expression<Func<Animal, bool>> basePre = s => s.Legs > 0;
        Expression<Func<Animal, int, bool>> baseCond = (s, v) => v < 100;
        basePm.SourcePath = new SourceMemberPath(new[] { typeof(Animal).GetProperty(nameof(Animal.Legs))! });
        basePm.PreCondition = basePre;
        basePm.Condition = baseCond;
        basePm.IsExplicit = true;
        animalTm.PropertyMaps.Add(basePm);

        // Derived sets its own predicates (IsExplicit = true wins).
        var dogTm = new TypeMap(typeof(Dog), typeof(DogDto), MemberList.None);
        dogTm.IncludedBases.Add(animalTm.Pair);
        var derivedPm = PropertyMap.ForProperty(typeof(DogDto).GetProperty(nameof(DogDto.Legs))!);
        Expression<Func<Dog, bool>> derivedPre = s => s.Legs > 2;
        Expression<Func<Dog, int, bool>> derivedCond = (s, v) => v == 4;
        derivedPm.SourcePath = new SourceMemberPath(new[] { typeof(Dog).GetProperty(nameof(Dog.Legs))! });
        derivedPm.PreCondition = derivedPre;
        derivedPm.Condition = derivedCond;
        derivedPm.IsExplicit = true;
        dogTm.PropertyMaps.Add(derivedPm);

        var typeMaps = new List<TypeMap> { animalTm, dogTm };
        var pairIndex = typeMaps.ToDictionary(t => t.Pair);

        InheritanceMerger.Resolve(typeMaps, pairIndex);

        var resultPm = dogTm.PropertyMaps.Single(p => p.Name == nameof(DogDto.Legs));
        Assert.Same(derivedPre, resultPm.PreCondition);
        Assert.Same(derivedCond, resultPm.Condition);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj --filter "FullyQualifiedName~Atlas.Tests.Internal.InheritanceMergerConditionTests"
```
Expected: tests run but FAIL — the merged derived PropertyMap has `null` for both predicates because `CopyConfig` doesn't copy them yet.

- [ ] **Step 3: Extend `CopyConfig`.**

In `src/Atlas/Internal/InheritanceMerger.cs`, find the existing `CopyConfig` method (around line 54) and add two field-copy lines so it reads:

```csharp
    private static void CopyConfig(PropertyMap source, PropertyMap target)
    {
        target.SourcePath = source.SourcePath;
        target.HasConstant = source.HasConstant;
        target.ConstantValue = source.ConstantValue;
        target.CustomExpression = source.CustomExpression;
        target.Ignored = source.Ignored;
        target.PreCondition = source.PreCondition;
        target.Condition = source.Condition;
        // Note: do NOT copy DestinationProperty / DestinationCtorParameter — those are
        // already correctly bound to the target's PropertyMap.
        // For Ignore-only bindings: source.SourcePath is null, which is fine — target gets null too.
    }
```

- [ ] **Step 4: Run the tests to verify they pass.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj --filter "FullyQualifiedName~Atlas.Tests.Internal.InheritanceMergerConditionTests"
```
Expected: 3/3 PASS.

- [ ] **Step 5: Run all Atlas.Tests to confirm no regressions.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj
```
Expected: 337 (Task 2 baseline) + 3 = 340 PASS.

- [ ] **Step 6: Commit.**

```bash
git add src/Atlas/Internal/InheritanceMerger.cs tests/Atlas.Tests/Internal/InheritanceMergerConditionTests.cs
git commit -m "$(cat <<'EOF'
InheritanceMerger.CopyConfig propagates predicates base->derived (3 tests)

Two field-copy lines added. Existing IsExplicit precedence rule in
MergeBaseConfig handles the derived-explicit-wins case automatically.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 4 — In-memory codegen: property-assign loop in `BuildPocoLambda`

Add the `WrapWithConditions` helper and the supporting `SubstituteOneParam` / `SubstituteTwoParams` helpers, then wire them into the property-assign loop. Test by exercising real `IMapper.Map<>()` calls so we observe end-to-end behavior, not just expression tree shape.

**Files:**
- Modify: `src/Atlas/Internal/ExecutionPlanBuilder.cs`
- Create: `tests/Atlas.Tests/ExecutionPlanBuilderConditionTests.cs`

**Allowlist for the implementer:** ONLY the two files above. Do NOT touch the ctor-arg loop or `BuildUpdate` (those are Tasks 5 and 6).

- [ ] **Step 1: Write the failing tests.**

Create `tests/Atlas.Tests/ExecutionPlanBuilderConditionTests.cs`:

```csharp
using Atlas.Configuration;

namespace Atlas.Tests;

public class ExecutionPlanBuilderConditionTests
{
    public class S
    {
        public int V { get; set; }
        public int? Maybe { get; set; }
        public string? Text { get; set; }
    }

    public class D
    {
        public int V { get; set; }
        public int Maybe { get; set; }
        public string Text { get; set; } = "";
    }

    [Fact]
    public void PreConditionTrue_AssignsResolvedValue()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .ForMember(d => d.V, opt =>
                {
                    opt.PreCondition(s => s.V > 0);
                    opt.MapFrom(s => s.V);
                }));
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<D>(new S { V = 42 });

        Assert.Equal(42, dst.V);
    }

    [Fact]
    public void PreConditionFalse_FreshMap_PropertyIsDefault()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .ForMember(d => d.V, opt =>
                {
                    opt.PreCondition(s => s.V > 0);
                    opt.MapFrom(s => s.V);
                }));
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<D>(new S { V = -5 });

        Assert.Equal(0, dst.V);
    }

    [Fact]
    public void PreConditionFalse_DoesNotInvokeMapFromExpression()
    {
        // Use a real-valued source with a side-effect counter inside MapFrom.
        // Even though we can't put state into the Expression, we use the only path
        // that lets us observe it: a property whose getter increments a counter.
        var counter = new SourceCounter();
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<SourceCounter, CounterDto>(MemberList.None)
                .ForMember(d => d.Probed, opt =>
                {
                    opt.PreCondition(s => false);              // always skip resolution
                    opt.MapFrom(s => s.IncrementAndReturn);    // would increment if invoked
                }));
        var mapper = cfg.CreateMapper();

        mapper.Map<CounterDto>(counter);

        Assert.Equal(0, counter.Probes);
    }

    public sealed class SourceCounter
    {
        public int Probes;
        public int IncrementAndReturn { get { Probes++; return 1; } }
    }
    public sealed class CounterDto { public int Probed { get; set; } }

    [Fact]
    public void ConditionTrueOnResolvedValue_AssignsValue()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .ForMember(d => d.V, opt =>
                {
                    opt.MapFrom(s => s.V);
                    opt.Condition((s, v) => v > 10);
                }));
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<D>(new S { V = 42 });

        Assert.Equal(42, dst.V);
    }

    [Fact]
    public void ConditionFalseOnResolvedValue_FreshMap_PropertyIsDefault()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .ForMember(d => d.V, opt =>
                {
                    opt.MapFrom(s => s.V);
                    opt.Condition((s, v) => v > 100);
                }));
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<D>(new S { V = 5 });

        Assert.Equal(0, dst.V);
    }

    [Fact]
    public void BothPredicates_BothPass_AssignsValue()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .ForMember(d => d.V, opt =>
                {
                    opt.PreCondition(s => s.V > 0);
                    opt.MapFrom(s => s.V * 2);
                    opt.Condition((s, v) => v < 100);
                }));
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<D>(new S { V = 10 });

        Assert.Equal(20, dst.V);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj --filter "FullyQualifiedName~Atlas.Tests.ExecutionPlanBuilderConditionTests"
```
Expected: tests run but several FAIL (e.g., `PreConditionFalse_FreshMap_PropertyIsDefault` returns -5 instead of 0 because the predicate is silently ignored at codegen).

- [ ] **Step 3: Add `SubstituteOneParam` and `SubstituteTwoParams` helpers.**

In `src/Atlas/Internal/ExecutionPlanBuilder.cs`, add the two helpers near the bottom of the class (just above the existing `private sealed class ParameterReplacer` declaration):

```csharp
    private static Expression SubstituteOneParam(LambdaExpression lambda, Expression param0Replacement)
        => new ParameterReplacer(lambda.Parameters[0], param0Replacement).Visit(lambda.Body)!;

    private static Expression SubstituteTwoParams(LambdaExpression lambda,
        Expression param0Replacement, Expression param1Replacement)
    {
        var afterFirst = new ParameterReplacer(lambda.Parameters[0], param0Replacement).Visit(lambda.Body)!;
        return new ParameterReplacer(lambda.Parameters[1], param1Replacement).Visit(afterFirst)!;
    }
```

- [ ] **Step 4: Add the `WrapWithConditions` helper.**

In `src/Atlas/Internal/ExecutionPlanBuilder.cs`, add the helper just above `WrapWithTransformers`:

```csharp
    private static Expression WrapWithConditions(
        Expression resolvedExpr,
        PropertyMap pm,
        ParameterExpression srcParam,
        Type valueType,
        Expression? fallbackExpr = null)
    {
        if (pm.PreCondition is null && pm.Condition is null)
            return resolvedExpr;

        var fallback = fallbackExpr ?? Expression.Default(valueType);

        // Inner: Condition gate (post-resolution).
        Expression inner = resolvedExpr;
        if (pm.Condition is not null)
        {
            // Hoist resolvedExpr into a local so it is evaluated once even if the
            // condition body references it multiple times.
            var resolvedVar = Expression.Variable(valueType, "r");
            var condBody = SubstituteTwoParams(pm.Condition, srcParam, resolvedVar);
            inner = Expression.Block(
                variables: new[] { resolvedVar },
                Expression.Assign(resolvedVar, resolvedExpr),
                Expression.Condition(condBody, resolvedVar, fallback));
        }

        // Outer: PreCondition gate (pre-resolution). Wraps the entire Condition block,
        // so resolvedExpr is not evaluated when PreCondition fails.
        if (pm.PreCondition is not null)
        {
            var preBody = SubstituteOneParam(pm.PreCondition, srcParam);
            inner = Expression.Condition(preBody, inner, fallback);
        }

        return inner;
    }
```

- [ ] **Step 5: Wire into the property-assign loop in `BuildPocoLambda`.**

In `src/Atlas/Internal/ExecutionPlanBuilder.cs`, find the property-assign loop in `BuildPocoLambda` (around line 235-255). Replace the `transformed` line and the assignment block with:

```csharp
        foreach (var pm in propertyMaps)
        {
            if (pm.Ignored) continue;
            if (pm.DestinationProperty is null) continue;

            var sourceExpr = BuildSourceExpression(pm, srcParam, registry, pm.DestinationProperty.PropertyType);
            if (sourceExpr is null) continue;

            var transformed = WrapWithTransformers(sourceExpr, pm.DestinationProperty.PropertyType, typeMap);
            var assignValue = WrapWithConditions(
                transformed, pm, srcParam, pm.DestinationProperty.PropertyType);

            if (pm.DestinationPath is { } path && path.Count > 1)
            {
                statements.Add(BuildNestedAssign(destVar, path, assignValue));
            }
            else
            {
                statements.Add(Expression.Assign(
                    Expression.Property(destVar, pm.DestinationProperty),
                    assignValue));
            }
        }
```

- [ ] **Step 6: Run the tests to verify they pass.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj --filter "FullyQualifiedName~Atlas.Tests.ExecutionPlanBuilderConditionTests"
```
Expected: 6/6 PASS.

- [ ] **Step 7: Run all Atlas.Tests to confirm no regressions.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj
```
Expected: 340 (Task 3 baseline) + 6 = 346 PASS.

- [ ] **Step 8: Commit.**

```bash
git add src/Atlas/Internal/ExecutionPlanBuilder.cs tests/Atlas.Tests/ExecutionPlanBuilderConditionTests.cs
git commit -m "$(cat <<'EOF'
ExecutionPlanBuilder gates property assigns with predicates (6 tests)

WrapWithConditions hoists the resolved value into a local so the Condition
body evaluates it once even when referenced multiple times. PreCondition
wraps the Condition block so resolution is skipped when the precondition
fails. No-op when neither predicate is set.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 5 — In-memory codegen: ctor-arg loop in `BuildPocoLambda`

Constructor arguments cannot be omitted, so predicate-fail produces the parameter's declared default value (or `default(T)` if no default). Reuses `WrapWithConditions` from Task 4 by passing a `fallbackExpr`.

**Files:**
- Modify: `src/Atlas/Internal/ExecutionPlanBuilder.cs`
- Create: `tests/Atlas.Tests/ExecutionPlanBuilderCtorParamConditionTests.cs`

**Allowlist for the implementer:** ONLY the two files above. The `WrapWithConditions` helper from Task 4 must NOT be modified.

- [ ] **Step 1: Write the failing tests.**

Create `tests/Atlas.Tests/ExecutionPlanBuilderCtorParamConditionTests.cs`:

```csharp
using Atlas.Configuration;

namespace Atlas.Tests;

public class ExecutionPlanBuilderCtorParamConditionTests
{
    public class S { public int V { get; set; } }

    // Destination with a ctor param that has a declared default.
    public class DWithDefault
    {
        public int V { get; }
        public DWithDefault(int v = 42) { V = v; }
    }

    // Destination with a ctor param that has no declared default.
    public class DNoDefault
    {
        public int V { get; }
        public DNoDefault(int v) { V = v; }
    }

    [Fact]
    public void CtorParam_PreConditionFalse_UsesDeclaredDefault()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, DWithDefault>(MemberList.None)
                .ForCtorParam("v", opt =>
                {
                    opt.PreCondition(s => s.V > 0);
                    opt.MapFrom(s => s.V);
                }));
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<DWithDefault>(new S { V = -5 });

        Assert.Equal(42, dst.V);   // ctor's declared default wins over default(int)
    }

    [Fact]
    public void CtorParam_NoDeclaredDefault_PreConditionFalse_UsesDefaultT()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, DNoDefault>(MemberList.None)
                .ForCtorParam("v", opt =>
                {
                    opt.PreCondition(s => s.V > 0);
                    opt.MapFrom(s => s.V);
                }));
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<DNoDefault>(new S { V = -5 });

        Assert.Equal(0, dst.V);   // default(int) — no declared default to fall back to
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj --filter "FullyQualifiedName~Atlas.Tests.ExecutionPlanBuilderCtorParamConditionTests"
```
Expected: tests FAIL — predicates aren't applied to ctor args yet, so `V` comes through as -5.

- [ ] **Step 3: Wire `WrapWithConditions` into the ctor-arg loop.**

In `src/Atlas/Internal/ExecutionPlanBuilder.cs`, find the ctor-arg loop in `BuildPocoLambda` (around line 205-225). Replace the `args = ctor.GetParameters().Select(p => ...)` body with:

```csharp
            var args = ctor.GetParameters().Select(p =>
            {
                Expression sourceExpr;
                var pm = ctorParamMaps.FirstOrDefault(m =>
                    string.Equals(m.Name, p.Name, StringComparison.OrdinalIgnoreCase));
                if (pm is null)
                {
                    sourceExpr = p.HasDefaultValue
                        ? Expression.Constant(p.DefaultValue, p.ParameterType)
                        : Expression.Default(p.ParameterType);
                }
                else
                {
                    sourceExpr = BuildSourceExpression(pm, srcParam, registry, p.ParameterType)
                        ?? Expression.Default(p.ParameterType);
                }
                var transformed = WrapWithTransformers(sourceExpr, p.ParameterType, typeMap);

                // NEW: gate ctor-arg with predicates. Skip semantics for ctor args:
                // p.DefaultValue if the param has one, else default(T) — a ctor argument
                // cannot be omitted.
                if (pm is not null)
                {
                    var fallback = p.HasDefaultValue
                        ? (Expression)Expression.Constant(p.DefaultValue, p.ParameterType)
                        : Expression.Default(p.ParameterType);
                    transformed = WrapWithConditions(
                        transformed, pm, srcParam, p.ParameterType, fallback);
                }
                return transformed;
            }).ToArray();
```

- [ ] **Step 4: Run the tests to verify they pass.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj --filter "FullyQualifiedName~Atlas.Tests.ExecutionPlanBuilderCtorParamConditionTests"
```
Expected: 2/2 PASS.

- [ ] **Step 5: Run all Atlas.Tests to confirm no regressions.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj
```
Expected: 346 (Task 4 baseline) + 2 = 348 PASS.

- [ ] **Step 6: Commit.**

```bash
git add src/Atlas/Internal/ExecutionPlanBuilder.cs tests/Atlas.Tests/ExecutionPlanBuilderCtorParamConditionTests.cs
git commit -m "$(cat <<'EOF'
ExecutionPlanBuilder gates ctor-args with predicates (2 tests)

Ctor-arg skip semantics use p.DefaultValue when declared, otherwise default(T).
A constructor argument cannot be omitted, so the same WrapWithConditions
helper is invoked with an explicit fallbackExpr.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 6 — In-memory codegen: update-in-place (`BuildUpdate`)

Update-in-place needs `IfThen` (preserve existing destination value on skip) rather than `Conditional` (assign default on skip). New helper `BuildUpdateAssignWithConditions`. Also includes a small refactor of `BuildNestedAssign` so the leaf-access can be reused by the gated update-path for `ForPath` bindings.

**Files:**
- Modify: `src/Atlas/Internal/ExecutionPlanBuilder.cs`
- Create: `tests/Atlas.Tests/ExecutionPlanBuilderUpdateConditionTests.cs`

**Allowlist for the implementer:** ONLY the two files above. The `WrapWithConditions` helper from Task 4 must NOT be modified.

- [ ] **Step 1: Write the failing tests.**

Create `tests/Atlas.Tests/ExecutionPlanBuilderUpdateConditionTests.cs`:

```csharp
using Atlas.Configuration;

namespace Atlas.Tests;

public class ExecutionPlanBuilderUpdateConditionTests
{
    public class S
    {
        public int V { get; set; }
        public string? Email { get; set; }
    }

    public class D
    {
        public int V { get; set; }
        public string Email { get; set; } = "";
    }

    [Fact]
    public void Update_PreConditionFalse_PreservesExistingDestValue()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .ForMember(d => d.V, opt =>
                {
                    opt.PreCondition(s => s.V > 0);
                    opt.MapFrom(s => s.V);
                }));
        var mapper = cfg.CreateMapper();

        var existing = new D { V = 99 };
        mapper.Map(new S { V = -5 }, existing);

        Assert.Equal(99, existing.V);   // preserved, NOT zeroed
    }

    [Fact]
    public void Update_ConditionFalse_PreservesExistingDestValue()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .ForMember(d => d.V, opt =>
                {
                    opt.MapFrom(s => s.V);
                    opt.Condition((s, v) => v > 100);   // 5 fails
                }));
        var mapper = cfg.CreateMapper();

        var existing = new D { V = 99 };
        mapper.Map(new S { V = 5 }, existing);

        Assert.Equal(99, existing.V);
    }

    [Fact]
    public void Update_BothPredicatesPass_OverwritesValue()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .ForMember(d => d.V, opt =>
                {
                    opt.PreCondition(s => s.V > 0);
                    opt.MapFrom(s => s.V);
                    opt.Condition((s, v) => v < 100);
                }));
        var mapper = cfg.CreateMapper();

        var existing = new D { V = 99 };
        mapper.Map(new S { V = 7 }, existing);

        Assert.Equal(7, existing.V);
    }

    [Fact]
    public void Update_PreConditionFalse_DoesNotInvokeMapFromExpression()
    {
        var counter = new SourceCounter();
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<SourceCounter, CounterDto>(MemberList.None)
                .ForMember(d => d.Probed, opt =>
                {
                    opt.PreCondition(s => false);
                    opt.MapFrom(s => s.IncrementAndReturn);
                }));
        var mapper = cfg.CreateMapper();

        var existing = new CounterDto { Probed = 99 };
        mapper.Map(counter, existing);

        Assert.Equal(0, counter.Probes);   // resolution skipped
        Assert.Equal(99, existing.Probed);  // value preserved
    }

    public sealed class SourceCounter
    {
        public int Probes;
        public int IncrementAndReturn { get { Probes++; return 1; } }
    }
    public sealed class CounterDto { public int Probed { get; set; } }
}
```

- [ ] **Step 2: Run the tests to verify they fail.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj --filter "FullyQualifiedName~Atlas.Tests.ExecutionPlanBuilderUpdateConditionTests"
```
Expected: tests FAIL — `BuildUpdate` doesn't apply predicates yet, so the existing destination is overwritten by `default` or by the unguarded resolved value.

- [ ] **Step 3: Add the `BuildUpdateAssignWithConditions` helper.**

In `src/Atlas/Internal/ExecutionPlanBuilder.cs`, add this helper just above `WrapWithConditions`:

```csharp
    private static Expression BuildUpdateAssignWithConditions(
        Expression resolvedExpr,
        PropertyMap pm,
        ParameterExpression srcParam,
        Expression dstAccess,
        Type valueType)
    {
        // Inner: assign (gated by Condition if present).
        Expression assign;
        if (pm.Condition is not null)
        {
            var resolvedVar = Expression.Variable(valueType, "r");
            var condBody = SubstituteTwoParams(pm.Condition, srcParam, resolvedVar);
            assign = Expression.Block(
                variables: new[] { resolvedVar },
                Expression.Assign(resolvedVar, resolvedExpr),
                Expression.IfThen(condBody, Expression.Assign(dstAccess, resolvedVar)));
        }
        else
        {
            assign = Expression.Assign(dstAccess, resolvedExpr);
        }

        // Outer: PreCondition gate.
        if (pm.PreCondition is not null)
        {
            var preBody = SubstituteOneParam(pm.PreCondition, srcParam);
            assign = Expression.IfThen(preBody, assign);
        }

        return assign;
    }
```

- [ ] **Step 4: Wire into the `BuildUpdate` property loop.**

In `src/Atlas/Internal/ExecutionPlanBuilder.cs`, find the `BuildUpdate` method's property-assign loop (around line 147-167). Replace the loop body so it reads:

```csharp
        foreach (var pm in typeMap.PropertyMaps)
        {
            if (pm.Ignored) continue;
            if (pm.DestinationProperty is null) continue;     // ctor params skipped on update

            var sourceExpr = BuildSourceExpression(pm, srcParam, registry, pm.DestinationProperty.PropertyType);
            if (sourceExpr is null) continue;

            var transformed = WrapWithTransformers(sourceExpr, pm.DestinationProperty.PropertyType, typeMap);

            Expression dstAccess;
            Expression? intermediates = null;
            if (pm.DestinationPath is { } path && path.Count > 1)
            {
                (intermediates, dstAccess) = BuildNestedPathAccess(destParam, path);
            }
            else
            {
                dstAccess = Expression.Property(destParam, pm.DestinationProperty);
            }

            var gatedAssign = BuildUpdateAssignWithConditions(
                transformed, pm, srcParam, dstAccess, pm.DestinationProperty.PropertyType);

            statements.Add(intermediates is null
                ? gatedAssign
                : Expression.Block(intermediates, gatedAssign));
        }
```

- [ ] **Step 5: Add the `BuildNestedPathAccess` helper alongside the existing `BuildNestedAssign`.**

In `src/Atlas/Internal/ExecutionPlanBuilder.cs`, just above the existing `BuildNestedAssign` method, add:

```csharp
    /// <summary>
    /// Splits a multi-level destination path into (a) the block of intermediate-coalesce
    /// statements and (b) the leaf-access MemberExpression. Used by the gated update-path
    /// so the predicate wraps just the leaf-assign, while intermediates are still
    /// auto-instantiated regardless of the predicate's value.
    /// </summary>
    private static (Expression Intermediates, Expression LeafAccess) BuildNestedPathAccess(
        Expression destRoot,
        IReadOnlyList<PropertyInfo> destPath)
    {
        var statements = new List<Expression>();
        Expression accessSoFar = destRoot;

        for (int i = 0; i < destPath.Count - 1; i++)
        {
            var intermediateProp = destPath[i];
            accessSoFar = Expression.Property(accessSoFar, intermediateProp);
            var ctor = intermediateProp.PropertyType.GetConstructor(Type.EmptyTypes)
                ?? throw new InvalidOperationException(
                    $"Cannot unflatten path through {intermediateProp.DeclaringType?.Name}.{intermediateProp.Name}: " +
                    $"intermediate type {intermediateProp.PropertyType.FullName} has no public parameterless constructor. " +
                    "Call AssertConfigurationIsValid() at startup to catch this at config time.");
            var coalesce = Expression.Coalesce(accessSoFar, Expression.New(ctor));
            statements.Add(Expression.Assign(accessSoFar, coalesce));
        }

        var leafAccess = Expression.Property(accessSoFar, destPath[^1]);
        return (Expression.Block(statements), leafAccess);
    }
```

The existing `BuildNestedAssign` method (used by `BuildPocoLambda` for fresh-map) is left untouched — it keeps building the full Block including the leaf-assign. The new `BuildNestedPathAccess` is only used by the update path.

- [ ] **Step 6: Run the tests to verify they pass.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj --filter "FullyQualifiedName~Atlas.Tests.ExecutionPlanBuilderUpdateConditionTests"
```
Expected: 4/4 PASS.

- [ ] **Step 7: Run all Atlas.Tests to confirm no regressions.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj
```
Expected: 348 (Task 5 baseline) + 4 = 352 PASS.

- [ ] **Step 8: Commit.**

```bash
git add src/Atlas/Internal/ExecutionPlanBuilder.cs tests/Atlas.Tests/ExecutionPlanBuilderUpdateConditionTests.cs
git commit -m "$(cat <<'EOF'
ExecutionPlanBuilder.BuildUpdate gates assigns with predicates (4 tests)

BuildUpdateAssignWithConditions emits IfThen so existing destination values
are preserved on skip (unlike fresh-map's Conditional-with-default). New
BuildNestedPathAccess helper splits ForPath bindings into intermediates +
leaf-access so the gate wraps only the leaf-assign while intermediates are
still auto-instantiated.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 7 — Projection codegen (`ProjectionPlanBuilder`)

Cross-package consumer of `PropertyMap`. This task closes the Bug-4 audit obligation from Task 1 (new fields on a shared shape must be handled by every consumer). LINQ providers reject `Expression.Block` and `Expression.Variable` in projection bindings, so the projection helper emits a single `Conditional` per binding and accepts double-evaluation of the resolved sub-expression in SQL.

**Files:**
- Modify: `src/Atlas.Projections/Internal/ProjectionPlanBuilder.cs`
- Create: `tests/Atlas.Projections.Tests/Internal/ProjectionPlanBuilderConditionTests.cs`

**Allowlist for the implementer:** ONLY the two files above. Do NOT touch `ProjectionCompatibility.cs` — predicates are NOT a projection rejection.

- [ ] **Step 1: Write the failing tests.**

Create `tests/Atlas.Projections.Tests/Internal/ProjectionPlanBuilderConditionTests.cs`:

```csharp
using System.Linq.Expressions;
using Atlas;
using Atlas.Configuration;
using Atlas.Internal;
using Atlas.Projections;
using Atlas.Projections.Internal;

namespace Atlas.Projections.Tests.Internal;

public class ProjectionPlanBuilderConditionTests
{
    public class S { public int V { get; set; } public string? Text { get; set; } }
    public class D { public int V { get; set; } public string Text { get; set; } = ""; }

    private static MapperRegistry BuildRegistry(Action<MapperConfigurationExpression> configure)
    {
        var cfg = new MapperConfiguration(configure);
        return cfg.Internal_Registry;
    }

    [Fact]
    public void Projection_NoPredicates_NoConditional()
    {
        var registry = BuildRegistry(c => c.CreateMap<S, D>(MemberList.None));
        var lambda = ProjectionPlanBuilder.Build(registry, new TypePair(typeof(S), typeof(D)), maxDepth: 5);

        // No member binding should be wrapped in a Conditional when no predicates are set.
        var memberInit = (MemberInitExpression)lambda.Body;
        foreach (var binding in memberInit.Bindings.Cast<MemberAssignment>())
        {
            Assert.IsNotType<ConditionalExpression>(binding.Expression);
        }
    }

    [Fact]
    public void Projection_PreConditionOnly_EmitsConditionalWithPredicate()
    {
        var registry = BuildRegistry(c =>
            c.CreateMap<S, D>(MemberList.None)
                .ForMember(d => d.V, opt =>
                {
                    opt.PreCondition(s => s.V > 0);
                    opt.MapFrom(s => s.V);
                }));
        var lambda = ProjectionPlanBuilder.Build(registry, new TypePair(typeof(S), typeof(D)), maxDepth: 5);

        var memberInit = (MemberInitExpression)lambda.Body;
        var vBinding = memberInit.Bindings.OfType<MemberAssignment>()
            .Single(b => b.Member.Name == nameof(D.V));

        var conditional = Assert.IsType<ConditionalExpression>(vBinding.Expression);
        // false-branch is Default(int) — i.e., Constant(0).
        var falseBranch = (ConstantExpression)conditional.IfFalse;
        Assert.Equal(0, falseBranch.Value);
    }

    [Fact]
    public void Projection_BothPredicates_AndAlsoComposed()
    {
        var registry = BuildRegistry(c =>
            c.CreateMap<S, D>(MemberList.None)
                .ForMember(d => d.V, opt =>
                {
                    opt.PreCondition(s => s.V > 0);
                    opt.MapFrom(s => s.V);
                    opt.Condition((s, v) => v < 100);
                }));
        var lambda = ProjectionPlanBuilder.Build(registry, new TypePair(typeof(S), typeof(D)), maxDepth: 5);

        var memberInit = (MemberInitExpression)lambda.Body;
        var vBinding = memberInit.Bindings.OfType<MemberAssignment>()
            .Single(b => b.Member.Name == nameof(D.V));

        var conditional = Assert.IsType<ConditionalExpression>(vBinding.Expression);
        // Test expression is AndAlso(pre, cond).
        var andAlso = Assert.IsType<BinaryExpression>(conditional.Test);
        Assert.Equal(ExpressionType.AndAlso, andAlso.NodeType);
    }

    [Fact]
    public void Projection_ConditionOnly_EmitsConditionalWithSubstitutedResolvedValue()
    {
        var registry = BuildRegistry(c =>
            c.CreateMap<S, D>(MemberList.None)
                .ForMember(d => d.V, opt =>
                {
                    opt.MapFrom(s => s.V * 2);
                    opt.Condition((s, v) => v > 0);
                }));
        var lambda = ProjectionPlanBuilder.Build(registry, new TypePair(typeof(S), typeof(D)), maxDepth: 5);

        var memberInit = (MemberInitExpression)lambda.Body;
        var vBinding = memberInit.Bindings.OfType<MemberAssignment>()
            .Single(b => b.Member.Name == nameof(D.V));

        var conditional = Assert.IsType<ConditionalExpression>(vBinding.Expression);
        // The Conditional must NOT contain Block or Variable — projection requires a single
        // pure expression per binding.
        Assert.False(AssertExpression.Contains<BlockExpression>(conditional));
        Assert.False(AssertExpression.Contains<ParameterExpression>(conditional)
                     // Allow the source parameter, just not internal Variables.
                     && Atlas.Projections.Tests.Internal.AssertExpression
                        .CountNodes<BlockExpression>(conditional) > 0);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Projections.Tests/Atlas.Projections.Tests.csproj --filter "FullyQualifiedName~Atlas.Projections.Tests.Internal.ProjectionPlanBuilderConditionTests"
```
Expected: tests FAIL — projection codegen ignores predicates, so bindings are not wrapped in `Conditional`.

- [ ] **Step 3: Add the `WrapProjectionWithConditions` helper.**

In `src/Atlas.Projections/Internal/ProjectionPlanBuilder.cs`, add the helper just above the existing `WrapProjectionWithTransformers`:

```csharp
    private static Expression WrapProjectionWithConditions(
        Expression resolvedExpr,
        PropertyMap pm,
        Expression srcExpr,
        Type valueType,
        Expression? fallbackExpr = null)
    {
        if (pm.PreCondition is null && pm.Condition is null)
            return resolvedExpr;

        var fallback = fallbackExpr ?? Expression.Default(valueType);
        Expression? testExpr = null;

        if (pm.PreCondition is not null)
        {
            var preBody = ParameterReplacer.Replace(
                pm.PreCondition.Body, pm.PreCondition.Parameters[0], srcExpr);
            testExpr = preBody;
        }

        if (pm.Condition is not null)
        {
            // Substitute BOTH parameters: param 0 = srcExpr, param 1 = resolvedExpr (inlined twice).
            var condBody = ParameterReplacer.Replace(
                pm.Condition.Body, pm.Condition.Parameters[0], srcExpr);
            condBody = ParameterReplacer.Replace(
                condBody, pm.Condition.Parameters[1], resolvedExpr);
            testExpr = testExpr is null ? condBody : Expression.AndAlso(testExpr, condBody);
        }

        return Expression.Condition(testExpr!, resolvedExpr, fallback);
    }
```

- [ ] **Step 4: Wire into the property-binding loop in `BuildBody`.**

In `src/Atlas.Projections/Internal/ProjectionPlanBuilder.cs`, find the property-binding loop (`foreach (var pm in propertyMaps)` around line 60-71). Replace the body so it reads:

```csharp
        foreach (var pm in propertyMaps)
        {
            if (pm.Ignored) continue;
            if (pm.DestinationProperty is null) continue;
            if (!ProjectionCompatibility.IsBindingProjectable(pm, out _)) continue;
            var binding = BuildBinding(srcExpr, pm, depth, pm.DestinationProperty.PropertyType, registry, maxDepth);
            if (binding is null) continue;

            binding = WrapProjectionWithTransformers(binding, pm.DestinationProperty.PropertyType, tm);
            binding = WrapProjectionWithConditions(
                binding, pm, srcExpr, pm.DestinationProperty.PropertyType);

            bindings.Add(Expression.Bind(pm.DestinationProperty, binding));
        }
```

- [ ] **Step 5: Wire into the ctor-arg loop in `BuildBody`.**

In `src/Atlas.Projections/Internal/ProjectionPlanBuilder.cs`, find the ctor-arg loop (`var args = ctor.GetParameters().Select(p => ...)` around line 38-56). Replace it so it reads:

```csharp
            var args = ctor.GetParameters().Select(p =>
            {
                Expression sourceExpr;
                var pm = ctorParamMaps.FirstOrDefault(m =>
                    string.Equals(m.Name, p.Name, StringComparison.OrdinalIgnoreCase));
                if (pm is null)
                {
                    sourceExpr = p.HasDefaultValue
                        ? Expression.Constant(p.DefaultValue, p.ParameterType)
                        : Expression.Default(p.ParameterType);
                }
                else
                {
                    sourceExpr = BuildBinding(srcExpr, pm, depth, p.ParameterType, registry, maxDepth)
                        ?? Expression.Default(p.ParameterType);
                }
                var transformed = WrapProjectionWithTransformers(sourceExpr, p.ParameterType, tm);

                if (pm is not null)
                {
                    var fallback = p.HasDefaultValue
                        ? (Expression)Expression.Constant(p.DefaultValue, p.ParameterType)
                        : Expression.Default(p.ParameterType);
                    transformed = WrapProjectionWithConditions(
                        transformed, pm, srcExpr, p.ParameterType, fallback);
                }
                return transformed;
            }).ToArray();
```

- [ ] **Step 6: Run the tests to verify they pass.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Projections.Tests/Atlas.Projections.Tests.csproj --filter "FullyQualifiedName~Atlas.Projections.Tests.Internal.ProjectionPlanBuilderConditionTests"
```
Expected: 4/4 PASS.

- [ ] **Step 7: Run all projection tests to confirm no regressions.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Projections.Tests/Atlas.Projections.Tests.csproj
```
Expected: 60 pre-existing + 4 new = 64 PASS.

- [ ] **Step 8: Commit.**

```bash
git add src/Atlas.Projections/Internal/ProjectionPlanBuilder.cs tests/Atlas.Projections.Tests/Internal/ProjectionPlanBuilderConditionTests.cs
git commit -m "$(cat <<'EOF'
Atlas.Projections gates bindings with predicates (4 tests)

Single Expression.Condition per gated binding; AndAlso composes both
predicates into the test expression with the resolved value substituted into
the Condition's second parameter. No Block/Variable used — LINQ providers
require a single pure expression per MemberAssignment. Resolved expression is
inlined twice (test + true-branch); SQL providers handle this fine.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 8 — End-to-end `IMapper` integration tests

Real `IMapper.Map<>()` calls covering the headline reference-doc example and key feature interactions (transformers + conditions, inheritance + conditions, collections + conditions). These tests demonstrate the feature works for users, not just at the codegen layer.

**Files:**
- Create: `tests/Atlas.Tests/MapperConditionTests.cs`

**Allowlist for the implementer:** ONLY the test file. No production code change in this task — if a production change is required, the implementer must report DONE_WITH_CONCERNS rather than make the change unilaterally (per the workflow's "implementer over-reach" rule).

- [ ] **Step 1: Write the tests.**

Create `tests/Atlas.Tests/MapperConditionTests.cs`:

```csharp
using Atlas.Configuration;

namespace Atlas.Tests;

public class MapperConditionTests
{
    public sealed class Order
    {
        public List<OrderItem>? Items { get; set; }
        public string? Description { get; set; }
    }
    public sealed class OrderItem
    {
        public decimal Price { get; set; }
        public int Quantity { get; set; }
    }
    public sealed class OrderDto
    {
        public decimal Total { get; set; }
        public string Description { get; set; } = "";
    }

    [Fact]
    public void HeadlineExample_PreConditionTrue_AndConditionTrue_AssignsTotal()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<Order, OrderDto>(MemberList.None)
                .ForMember(d => d.Total, opt =>
                {
                    opt.PreCondition(s => s.Items != null && s.Items.Count > 0);
                    opt.MapFrom(s => s.Items!.Sum(i => i.Price * i.Quantity));
                    opt.Condition((s, total) => total > 0);
                }));
        var mapper = cfg.CreateMapper();

        var dto = mapper.Map<OrderDto>(new Order
        {
            Items = new List<OrderItem>
            {
                new() { Price = 10m, Quantity = 2 },
                new() { Price = 5m,  Quantity = 1 },
            },
        });

        Assert.Equal(25m, dto.Total);
    }

    [Fact]
    public void HeadlineExample_PreConditionFalse_TotalIsZero()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<Order, OrderDto>(MemberList.None)
                .ForMember(d => d.Total, opt =>
                {
                    opt.PreCondition(s => s.Items != null && s.Items.Count > 0);
                    opt.MapFrom(s => s.Items!.Sum(i => i.Price * i.Quantity));
                    opt.Condition((s, total) => total > 0);
                }));
        var mapper = cfg.CreateMapper();

        var dto = mapper.Map<OrderDto>(new Order { Items = null });

        Assert.Equal(0m, dto.Total);
    }

    [Fact]
    public void Condition_ReadsResolvedValue_AfterTransformer()
    {
        // Transformer trims; Condition fires on the post-trim length.
        var cfg = new MapperConfiguration(c =>
        {
            c.ValueTransformers.Add<string>(s => s.Trim());
            c.CreateMap<Order, OrderDto>(MemberList.None)
                .ForMember(d => d.Description, opt =>
                {
                    opt.MapFrom(s => s.Description ?? "");
                    opt.Condition((s, desc) => desc.Length > 0);
                });
        });
        var mapper = cfg.CreateMapper();

        var emptyAfterTrim = mapper.Map<OrderDto>(new Order { Description = "  " });
        var realText = mapper.Map<OrderDto>(new Order { Description = "  hello  " });

        Assert.Equal("", emptyAfterTrim.Description);   // skipped, default(string) overwritten by ctor default ""
        Assert.Equal("hello", realText.Description);
    }

    [Fact]
    public void Collection_ElementMapHasConditions_AppliedPerElement()
    {
        // Atlas convention: List<S> -> List<D> requires an explicit CreateMap on the
        // collection types. The element map's predicates fire on each element via the
        // collection-mapping inner invoke.
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap<Order, OrderDto>(MemberList.None)
                .ForMember(d => d.Total, opt =>
                {
                    opt.PreCondition(s => s.Items != null && s.Items.Count > 0);
                    opt.MapFrom(s => s.Items!.Sum(i => i.Price * i.Quantity));
                });
            c.CreateMap<List<Order>, List<OrderDto>>(MemberList.None);
        });
        var mapper = cfg.CreateMapper();

        var orders = new List<Order>
        {
            new() { Items = new List<OrderItem> { new() { Price = 10m, Quantity = 1 } } },
            new() { Items = null },
            new() { Items = new List<OrderItem> { new() { Price = 5m, Quantity = 4 } } },
        };

        var dtos = mapper.Map<List<OrderDto>>(orders);

        Assert.Equal(3, dtos.Count);
        Assert.Equal(10m, dtos[0].Total);
        Assert.Equal(0m, dtos[1].Total);    // PreCondition false → default
        Assert.Equal(20m, dtos[2].Total);
    }

    [Fact]
    public void Inheritance_BasePredicate_FlowsToDerivedMap()
    {
        // Tests that base-map's predicate (set via ForMember) flows to derived map via
        // InheritanceMerger when derived doesn't override.
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap<Animal, AnimalDto>(MemberList.None)
                .Include<Dog, DogDto>()
                .ForMember(d => d.Legs, opt =>
                {
                    opt.PreCondition(s => s.Legs > 0);
                    opt.MapFrom(s => s.Legs);
                });
            c.CreateMap<Dog, DogDto>(MemberList.None);
        });
        var mapper = cfg.CreateMapper();

        var positive = mapper.Map<DogDto>(new Dog { Legs = 4 });
        var negative = mapper.Map<DogDto>(new Dog { Legs = -1 });

        Assert.Equal(4, positive.Legs);
        Assert.Equal(0, negative.Legs);   // base PreCondition flowed to derived
    }

    public class Animal { public int Legs { get; set; } }
    public class Dog : Animal { }
    public class AnimalDto { public int Legs { get; set; } }
    public class DogDto : AnimalDto { }
}
```

- [ ] **Step 2: Run the tests to verify they pass.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj --filter "FullyQualifiedName~Atlas.Tests.MapperConditionTests"
```
Expected: 5/5 PASS (the production code is already complete from Tasks 1-6; these are integration tests that exercise it).

- [ ] **Step 3: Run all Atlas.Tests to confirm no regressions.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj
```
Expected: 352 (Task 6 baseline) + 5 = 357 PASS.

- [ ] **Step 4: Commit.**

```bash
git add tests/Atlas.Tests/MapperConditionTests.cs
git commit -m "$(cat <<'EOF'
End-to-end Mapper conditional tests (5 tests)

Headline reference-doc example, transformer+condition interaction,
collection-element conditions, inheritance-flow.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 9 — EF Core projection E2E tests

End-to-end against in-memory EF Core SQLite. Verifies that the predicate genuinely translates to SQL `CASE WHEN` and that predicate-false rows surface `default(TMember)` for the gated column.

**Files:**
- Create: `tests/Atlas.Projections.Tests.EFCore/ProjectTo_ConditionTests.cs`

**Allowlist for the implementer:** ONLY the test file. The test reuses the existing `BlogContext.CreateInMemory()` fixture — no production code change in this task.

- [ ] **Step 1: Read the existing fixture so the new test uses the established pattern.**

Read `tests/Atlas.Projections.Tests.EFCore/Fixtures/BlogContext.cs` and `tests/Atlas.Projections.Tests.EFCore/Fixtures/BlogModels.cs` to learn the seeding conventions used by `ProjectionEFCoreTests`. The new test will use the same `Post` / `PostDto` shape so it fits naturally.

- [ ] **Step 2: Write the tests.**

Create `tests/Atlas.Projections.Tests.EFCore/ProjectTo_ConditionTests.cs`:

```csharp
using Atlas;
using Atlas.Configuration;
using Atlas.Projections;
using Atlas.Projections.Tests.EFCore.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Projections.Tests.EFCore;

public class ProjectTo_ConditionTests
{
    [Fact]
    public void ProjectTo_PredicateGeneratesCaseWhen()
    {
        var config = new MapperConfiguration(c =>
            c.CreateMap<Post, PostDto>(MemberList.None)
                .ForMember(d => d.Body, opt =>
                {
                    opt.PreCondition(s => s.WordCount > 0);
                    opt.MapFrom(s => s.Body);
                }));
        using var ctx = BlogContext.CreateInMemory();
        ctx.Seed();

        var sql = ctx.Posts.ProjectTo<PostDto>(config).ToQueryString();

        Assert.Contains("CASE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WHEN", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProjectTo_PredicateFalse_RowReturnsDefaultForGatedColumn()
    {
        // Configure a predicate that is false for at least one seeded row, then assert
        // that row's projected column comes back as default(string) (i.e., null).
        var config = new MapperConfiguration(c =>
            c.CreateMap<Post, PostDto>(MemberList.None)
                .ForMember(d => d.Body, opt =>
                {
                    opt.PreCondition(s => s.WordCount > 100);   // p1 has WordCount=100; predicate false
                    opt.MapFrom(s => s.Body);
                })
                .ForMember(d => d.WordCount, opt => opt.MapFrom(s => s.WordCount))
                .ForMember(d => d.Id, opt => opt.MapFrom(s => s.Id)));
        using var ctx = BlogContext.CreateInMemory();
        ctx.Seed();

        var posts = ctx.Posts.OrderBy(p => p.Id).ProjectTo<PostDto>(config).ToList();

        Assert.Equal(2, posts.Count);
        // The first seeded post has WordCount = 100 — predicate false → Body comes back as null/default.
        Assert.Null(posts[0].Body);
        // The second seeded post has WordCount = 200 — predicate true → Body is the real value.
        Assert.Equal("p2", posts[1].Body);
    }
}
```

- [ ] **Step 3: Verify the seeded `WordCount` values match the test assumptions.**

Read `tests/Atlas.Projections.Tests.EFCore/Fixtures/BlogContext.cs` and confirm the `Seed()` method creates two posts whose `WordCount` values are 100 and 200 (these are the values the existing `ProjectionEFCoreTests` rely on). If they differ, adjust the predicate threshold in Step 2 so the first seeded post fails the predicate and the second passes.

If the existing seed values are different (e.g., 100 and 100, 50 and 200, etc.), use `s => s.Id == 2` instead of `s => s.WordCount > 100` as the predicate so it cleanly separates the two seeded rows by Id.

- [ ] **Step 4: Run the tests to verify they pass.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Projections.Tests.EFCore/Atlas.Projections.Tests.EFCore.csproj --filter "FullyQualifiedName~Atlas.Projections.Tests.EFCore.ProjectTo_ConditionTests"
```
Expected: 2/2 PASS.

- [ ] **Step 5: Run all EFCore projection tests to confirm no regressions.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Projections.Tests.EFCore/Atlas.Projections.Tests.EFCore.csproj
```
Expected: 8 pre-existing + 2 new = 10 PASS.

- [ ] **Step 6: Commit.**

```bash
git add tests/Atlas.Projections.Tests.EFCore/ProjectTo_ConditionTests.cs
git commit -m "$(cat <<'EOF'
EF Core E2E tests for projected conditional mapping (2 tests)

Verifies the SQL CASE WHEN is generated and that predicate-false rows
return default(TMember) for the gated column.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 10 — README + final coverage check

Add the Conditional Mapping section to the README, refresh the test count, run a final coverage pass, and ensure the branch is ready for the holistic review.

**Files:**
- Modify: `README.md`

**Allowlist for the implementer:** ONLY the README. Production code and tests are frozen at this point.

- [ ] **Step 1: Run the full solution test suite to confirm the cumulative state.**

```bash
dotnet test C:/Repos/Atlas/Atlas.slnx
```
Expected: **~432 tests pass** across all test projects (396 baseline + 36 new across this feature).

- [ ] **Step 2: Open the existing README and locate insertion points.**

Read `C:/Repos/Atlas/README.md`. Find:
- The **test-count line** at the top (e.g. "396 tests passing"). Update it to the actual number you just observed in Step 1.
- The **value transformers section** — the new "Conditional mapping" subsection goes immediately AFTER it, since both are post-resolution member-level features and the natural reading order is transformers → conditions.

- [ ] **Step 3: Add the Conditional Mapping subsection.**

Insert the following after the existing Value Transformers section in `README.md`:

```markdown
### Conditional mapping (`PreCondition` / `Condition`)

Two per-member predicates that gate property mapping at runtime.
`PreCondition(s => predicate)` runs **before** source-side resolution — use it
when the resolution is expensive and would be wasted work if the predicate
fails. `Condition((s, value) => predicate)` runs **after** resolution — use it
when the predicate depends on the resolved value.

Pipeline order: **PreCondition → resolve → Condition → assign**.

```csharp
CreateMap<Order, OrderDto>()
    .ForMember(d => d.Total, opt =>
    {
        opt.PreCondition(s => s.Items != null && s.Items.Count > 0);
        opt.MapFrom(s => s.Items.Sum(i => i.Price * i.Quantity));
        opt.Condition((s, total) => total > 0);
    });
```

Skip semantics:
- **Fresh `Map<TDest>(src)`**: skipped property is `default(TMember)`.
- **Update-in-place `Map<TS,TD>(src, existingDest)`**: skipped property
  preserves the existing destination value.
- **`ProjectTo<TDest>(query)`**: skipped property is `default(TMember)` (a
  projection materializes a fresh row).

Both predicates are `Expression<Func<...>>` and translate to SQL `CASE WHEN`
in `ProjectTo`. Untranslatable predicates fail at query-execution time with
the LINQ provider's standard error.

Predicates flow base→derived through inheritance via the existing
explicit-config precedence rule. Predicates do NOT auto-flip across
`.ReverseMap()` — reconfigure on the reverse expression.
```

- [ ] **Step 4: If the README has a "What `ProjectTo` translates" or "Limitations" table, add a row confirming conditional mapping IS translatable.**

If no such table exists, omit this step.

- [ ] **Step 5: Run the full build to confirm the README change didn't break anything (it shouldn't — but defensive).**

```bash
dotnet build C:/Repos/Atlas/Atlas.slnx -c Debug
```
Expected: build succeeds with 0 warnings, 0 errors.

- [ ] **Step 6: Commit.**

```bash
git add README.md
git commit -m "$(cat <<'EOF'
docs: README — add conditional mapping section, refresh test count

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

- [ ] **Step 7: Coverage spot-check.**

Generate coverage on the changed assemblies and confirm gates are met (line ≥ 90%, branch ≥ 80% on Atlas core and Atlas.Projections):

```bash
dotnet test C:/Repos/Atlas/Atlas.slnx -c Debug --collect:"XPlat Code Coverage" --results-directory C:/Repos/Atlas/TestResults/conditional
```
Expected: produces a `coverage.cobertura.xml` per test project under `TestResults/conditional/<guid>/`.

If a coverage gate fails on `Atlas` or `Atlas.Projections`, **stop** and add the missing branch coverage in a follow-up commit on this branch — do not skip the gate. Likely missing-branch sites: the `pm.PreCondition is null && pm.Condition is null` short-circuit (no-predicate path), the ctor-arg-with-null-pm path, and the `pm.Condition is null` branch inside the update helper. If those are uncovered, add small targeted unit tests in the existing `*ConditionTests.cs` files.

---

## Final review (controller, before opening the PR)

- [ ] **Run the full holistic review using `superpowers:code-reviewer` on the entire `feat/conditional-mapping` branch vs. `main`.**

  This is non-negotiable per the established workflow rhythm — Value Transformers (#6) was the empirical proof case where holistic review caught a Critical reverse-map propagation bug despite all per-task reviews passing cleanly. Don't skip it.

- [ ] **Confirm cross-package consumer audit (Bug-4 lesson) was honoured.**

  Tasks 1, 4-6 (Atlas core) and Task 7 (Atlas.Projections) together cover every consumer of the new `PropertyMap.PreCondition` / `PropertyMap.Condition` fields. Verify that no other consumer exists (grep `pm\.PreCondition\|pm\.Condition` across the repo and confirm only the codegen sites match).

- [ ] **Confirm no scope-identifying TypeMap metadata was added (Bug-5 lesson).**

  Verify `TypeMap.cs` has NOT been modified — the predicates live on `PropertyMap`, not `TypeMap`. `git diff main...HEAD -- src/Atlas/Internal/TypeMap.cs` should show no output.

- [ ] **Confirm no validator rules were added.**

  `git diff main...HEAD -- src/Atlas/Internal/ConfigurationValidator.cs` should show no output. The design explicitly rejects pre-inspection.

- [ ] **Confirm `ProjectionCompatibility` was NOT modified.**

  Predicates translate; they are NOT a projection rejection. `git diff main...HEAD -- src/Atlas.Projections/Internal/ProjectionCompatibility.cs` should show no output.

- [ ] **Push and open the PR.**

  ```bash
  git push -u origin feat/conditional-mapping
  gh pr create --title "Atlas v2 #7 — Conditional Mapping (PreCondition / Condition)" --body "$(cat <<'EOF'
## Summary
- Adds per-member `PreCondition` and `Condition` predicates on `IMemberConfigurationExpression`
- Pipeline order: `PreCondition → resolve → Condition → assign`
- Both predicates are `Expression<>` so they translate to SQL `CASE WHEN` in `ProjectTo<>()`
- Update-in-place preserves the existing destination value when a predicate fails (in-memory)
- Inheritance propagates predicates base→derived via existing `IsExplicit` precedence

## Test plan
- [x] All existing tests still pass (396 → ~432)
- [x] Coverage gates met on Atlas core (line ≥ 90%, branch ≥ 80%)
- [x] Coverage gates met on Atlas.Projections
- [x] EF Core E2E confirms `CASE WHEN` SQL generation
- [x] Update-in-place preservation verified end-to-end
- [x] Inheritance flow verified end-to-end

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
  ```

  After the PR is opened, paste the PR URL into the controller's note and proceed with the `superpowers:finishing-a-development-branch` flow (Option 2 — open a PR).

---

## Implementer Notes (per-task ground rules)

These are repeated in the design's §11 but reproduced here so the implementer-subagent sees them in-context.

1. **Don't try to "optimize" projection codegen with `Block`/`Variable`.** LINQ providers reject these in projection bindings. The double-evaluation of `resolvedExpr` in `WrapProjectionWithConditions` is intentional — leave it alone.

2. **Wrap order matters.** Both helpers must wrap **after** transformers, not before. Conditions see the post-transform value. Reversing this would silently break the documented behavior.

3. **Cross-package consumer audit.** Tasks 4-6 cover `Atlas`; Task 7 covers `Atlas.Projections`. Both are required because `PropertyMap` is a shared shape consumed by both packages.

4. **Per-member only.** Don't add a per-typemap or per-call surface — those are explicitly deferred to v3 in the design.

5. **No new types.** No `ConditionResolver` class, no new `MapperProfile.Conditions` collection, no new build-time pass. The whole feature lives on `PropertyMap` plus codegen helpers.

6. **No validator rules.** The C# type system enforces predicate shape. Null-arg checks live in the fluent surface only.

7. **No projection rejection.** `ProjectionCompatibility` is unchanged.

8. **Watch for tests that quietly diverge from the plan.** If an assertion in this plan turns out to be wrong (e.g., the plan asserts a specific exception type that isn't actually thrown, or asserts a specific Expression node shape that the implementation produces differently), report DONE_WITH_CONCERNS rather than silently changing the test. The Hooks Task 10 review found exactly this anti-pattern.

9. **xUnit v3 only — no FluentAssertions.** All assertions use `Assert.X()` style (per memory `feedback_no_fluentassertions`).

10. **Holistic review is non-negotiable.** Even if every per-task review passes cleanly, the controller MUST run `superpowers:code-reviewer` over the whole branch before opening the PR. Value Transformers proved this catches Critical bugs that per-task review misses.
