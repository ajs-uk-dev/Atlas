# Atlas v2 Null Substitution — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add per-member `NullSubstitute<TSourceMember>` (constant + Expression overloads) so users can declare a source-typed fallback for null-resolved source values. The substitute participates in the existing conversion pipeline and translates to SQL `COALESCE` in `ProjectTo`.

**Architecture:** One nullable `LambdaExpression?` field on `PropertyMap`. One helper added to `ExecutionPlanBuilder` (`ApplyNullSubstitute`) and one to `ProjectionPlanBuilder` (`ApplyProjectionNullSubstitute`). Inheritance propagation rides the existing `InheritanceMerger.CopyConfig` path. Two new validator rules in `ConfigurationValidator`. No new types, no new build-time pass, no new projection rejection.

**Tech Stack:** .NET 10, C# 14 (preview), xUnit v3 (no FluentAssertions — `Assert.X()` only), `System.Linq.Expressions`, EF Core (in-memory + SQLite for projection E2E).

**Branch & merge:** Cut `feat/null-substitution` from `main` HEAD (currently `b34b0d3`, the design-doc commit). All 9 implementation tasks land on this branch; final review then PR to `main`.

**Specs to read alongside this plan:**
- `C:\Repos\Atlas\docs\Atlas-Design-NullSubstitution.md` — every code section in this plan implements something specified there.
- `C:\Repos\Atlas\docs\Atlas-Design-ConditionalMapping.md` — closest precedent feature; the codegen wrap pipeline is documented there.
- `C:\Repos\Atlas\docs\Atlas-Plan-ConditionalMapping.md` — structural template for this plan (same task rhythm).

---

## File Map

**Production code modified:**
- `src/Atlas/Internal/PropertyMap.cs` — add one `LambdaExpression?` field.
- `src/Atlas/Configuration/IMemberConfigurationExpression.cs` — add two interface methods with full XML docs.
- `src/Atlas/Configuration/MemberConfigurationExpression.cs` — implement two methods, extend `ApplyTo`.
- `src/Atlas/Internal/InheritanceMerger.cs` — extend `CopyConfig` with one field copy.
- `src/Atlas/Internal/ExecutionPlanBuilder.cs` — add `ApplyNullSubstitute` helper + wire into `BuildSourceExpression` (only).
- `src/Atlas/Internal/ConfigurationValidator.cs` — add `ValidateNullSubstitutes` method + `ResolveSourceMemberType` helper + call site in `Validate`.
- `src/Atlas.Projections/Internal/ProjectionPlanBuilder.cs` — add `ApplyProjectionNullSubstitute` helper + wire into `BuildBinding` (only).
- `README.md` — add Null Substitution subsection + remove the entry from "Deferred to v2" list + refresh test count.

**Production code NOT modified (deliberate per design §2.2):**
- `src/Atlas/Internal/TypeMap.cs` — no new fields.
- `src/Atlas/Internal/ConventionEngine.cs`, `ReverseMapMirror.cs`, `MapperRegistry.cs`, `TransformerResolver.cs` — unchanged.
- `src/Atlas/MapperConfiguration.cs` — no new build-time call.
- `src/Atlas.Projections/Internal/ProjectionCompatibility.cs` — substitutes are NOT a projection rejection (they translate via `Coalesce` → `COALESCE`).
- `BuildPocoLambda` ctor-arg / property-assign loops, `BuildUpdate` property loop — unchanged. They all call `BuildSourceExpression` which now applies the substitute internally.

**Test code added:**
- `tests/Atlas.Tests/Internal/PropertyMapNullSubstituteTests.cs` — 2 unit tests over the new field.
- `tests/Atlas.Tests/MappingExpressionNullSubstituteTests.cs` — 6 tests over the fluent surface.
- `tests/Atlas.Tests/Internal/InheritanceMergerNullSubstituteTests.cs` — 2 tests over base→derived propagation.
- `tests/Atlas.Tests/ExecutionPlanBuilderNullSubstituteTests.cs` — 8 tests over in-memory codegen behavior (executed via `IMapper.Map<>()`).
- `tests/Atlas.Tests/ConfigurationValidatorNullSubstituteTests.cs` — 5 tests over the two new validator rules.
- `tests/Atlas.Tests/MapperNullSubstituteTests.cs` — 3 end-to-end tests over real `IMapper`.
- `tests/Atlas.Projections.Tests/Internal/ProjectionPlanBuilderNullSubstituteTests.cs` — 2 tests over projection-codegen expression-tree shape.
- `tests/Atlas.Projections.Tests.EFCore/ProjectTo_NullSubstituteTests.cs` — 2 EF Core SQLite E2E tests.

**Total new tests: 30**. Test baseline goes from **432 → 462**.

---

## Task 0 — Branch setup (5 min, controller-only)

Cut the feature branch and verify clean starting state.

**Files:** none modified.

- [ ] **Step 1: Verify on main, clean tree, design committed.**

```bash
cd C:/Repos/Atlas
git status
git log --oneline -3
```
Expected: `On branch main`, working tree clean, top commit is `b34b0d3 docs: design for Atlas v2 #8 Null Substitution ...`.

- [ ] **Step 2: Cut feature branch.**

```bash
git checkout -b feat/null-substitution
```
Expected: `Switched to a new branch 'feat/null-substitution'`.

- [ ] **Step 3: Confirm full build + tests still green on the branch.**

```bash
dotnet build C:/Repos/Atlas/Atlas.slnx -c Debug
dotnet test C:/Repos/Atlas/Atlas.slnx -c Debug --no-build
```
Expected: build succeeds; **432** tests pass across all test projects (358 Atlas.Tests + 64 Atlas.Projections.Tests + 10 Atlas.Projections.Tests.EFCore).

If the count differs, **stop and reconcile** — the baseline must match before adding new tests.

---

## Task 1 — `PropertyMap.NullSubstitute` field

Add the single nullable `LambdaExpression?` field to the `PropertyMap` data shape.

**Files:**
- Modify: `src/Atlas/Internal/PropertyMap.cs`
- Create: `tests/Atlas.Tests/Internal/PropertyMapNullSubstituteTests.cs`

**Allowlist for the implementer:** ONLY the two files above.

- [ ] **Step 1: Write the failing tests.**

Create `tests/Atlas.Tests/Internal/PropertyMapNullSubstituteTests.cs`:

```csharp
using System.Linq.Expressions;
using Atlas.Internal;

namespace Atlas.Tests.Internal;

public class PropertyMapNullSubstituteTests
{
    private sealed class S { public string? V { get; set; } }
    private sealed class D { public string V { get; set; } = ""; }

    [Fact]
    public void NewPropertyMap_NullSubstitute_DefaultsToNull()
    {
        var prop = typeof(D).GetProperty(nameof(D.V))!;
        var pm = PropertyMap.ForProperty(prop);

        Assert.Null(pm.NullSubstitute);
    }

    [Fact]
    public void PropertyMap_AcceptsNullSubstituteLambda()
    {
        var prop = typeof(D).GetProperty(nameof(D.V))!;
        var pm = PropertyMap.ForProperty(prop);

        Expression<Func<string>> sub = () => "Unknown";
        pm.NullSubstitute = sub;

        Assert.Same(sub, pm.NullSubstitute);
        Assert.Equal(typeof(string), pm.NullSubstitute!.Body.Type);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj --filter "FullyQualifiedName~Atlas.Tests.Internal.PropertyMapNullSubstituteTests"
```
Expected: build error (`'PropertyMap' does not contain a definition for 'NullSubstitute'`).

- [ ] **Step 3: Add the field to `PropertyMap`.**

In `src/Atlas/Internal/PropertyMap.cs`, after the existing `Condition` property, add:

```csharp
    /// <summary>
    /// Source-typed fallback used when the resolved source member is null. Stored as
    /// <c>Expression&lt;Func&lt;TSourceMember&gt;&gt;</c>: the constant overload wraps as
    /// <c>() =&gt; constant</c>; the Expression overload stores the user's lambda directly.
    /// Codegen inlines the lambda body and wraps the resolved source expression in
    /// <see cref="System.Linq.Expressions.Expression.Coalesce(System.Linq.Expressions.Expression, System.Linq.Expressions.Expression)"/>
    /// upstream of <c>ConvertOrMap</c> / <c>ConvertOrInline</c>.
    /// </summary>
    public LambdaExpression? NullSubstitute { get; set; }
```

- [ ] **Step 4: Run the tests to verify they pass.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj --filter "FullyQualifiedName~Atlas.Tests.Internal.PropertyMapNullSubstituteTests"
```
Expected: 2/2 PASS.

- [ ] **Step 5: Run the full Atlas.Tests project to confirm no regressions.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj
```
Expected: 358 pre-existing + 2 new = 360 PASS.

- [ ] **Step 6: Commit.**

```bash
git add src/Atlas/Internal/PropertyMap.cs tests/Atlas.Tests/Internal/PropertyMapNullSubstituteTests.cs
git commit -m "$(cat <<'EOF'
PropertyMap gains NullSubstitute field (2 tests)

One new LambdaExpression? field. Defaults to null; no consumers yet —
subsequent tasks plumb the fluent surface, codegen, validator, and inheritance.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 2 — Fluent surface (`NullSubstitute` methods)

Add two methods to `IMemberConfigurationExpression` (constant + Expression overloads), implement in `MemberConfigurationExpression`, and extend `ApplyTo`.

**Files:**
- Modify: `src/Atlas/Configuration/IMemberConfigurationExpression.cs`
- Modify: `src/Atlas/Configuration/MemberConfigurationExpression.cs`
- Create: `tests/Atlas.Tests/MappingExpressionNullSubstituteTests.cs`

**Allowlist for the implementer:** ONLY the three files above.

- [ ] **Step 1: Write the failing tests.**

Create `tests/Atlas.Tests/MappingExpressionNullSubstituteTests.cs`:

```csharp
using System.Linq.Expressions;
using Atlas.Configuration;
using Atlas.Internal;

namespace Atlas.Tests;

public class MappingExpressionNullSubstituteTests
{
    public sealed class S { public string? Name { get; set; } public int? Score { get; set; } }
    public sealed class D { public string Name { get; set; } = ""; public int Score { get; set; } }

    private static MappingExpression<S, D> NewExpr() =>
        new(new TypeMap(typeof(S), typeof(D), MemberList.None));

    [Fact]
    public void NullSubstitute_ConstantOverload_StoredAsParameterlessLambda()
    {
        var expr = NewExpr();

        expr.ForMember(d => d.Name, opt => opt.NullSubstitute("Unknown"));

        var pm = expr.TypeMap.PropertyMaps.Single(p => p.Name == nameof(D.Name));
        Assert.NotNull(pm.NullSubstitute);
        Assert.Empty(pm.NullSubstitute!.Parameters);
        Assert.Equal(typeof(string), pm.NullSubstitute!.Body.Type);
    }

    [Fact]
    public void NullSubstitute_ExpressionOverload_StoredAsIs()
    {
        var expr = NewExpr();
        Expression<Func<string>> factory = () => "Computed";

        expr.ForMember(d => d.Name, opt => opt.NullSubstitute(factory));

        var pm = expr.TypeMap.PropertyMaps.Single(p => p.Name == nameof(D.Name));
        Assert.Same(factory, pm.NullSubstitute);
    }

    [Fact]
    public void NullSubstitute_ExpressionOverload_NullArg_Throws()
    {
        var expr = NewExpr();
        Assert.Throws<ArgumentNullException>(() =>
            expr.ForMember(d => d.Name, opt =>
                opt.NullSubstitute<string>((Expression<Func<string>>)null!)));
    }

    [Fact]
    public void NullSubstitute_LastCallWins_TwoConstants()
    {
        var expr = NewExpr();

        expr.ForMember(d => d.Name, opt =>
        {
            opt.NullSubstitute("First");
            opt.NullSubstitute("Second");
        });

        var pm = expr.TypeMap.PropertyMaps.Single(p => p.Name == nameof(D.Name));
        Assert.NotNull(pm.NullSubstitute);
        // The lambda body for "Second" must be the surviving substitute.
        var compiled = ((Expression<Func<string>>)pm.NullSubstitute!).Compile();
        Assert.Equal("Second", compiled());
    }

    [Fact]
    public void NullSubstitute_LastCallWins_ConstantThenExpression()
    {
        var expr = NewExpr();
        Expression<Func<string>> factory = () => "FromExpr";

        expr.ForMember(d => d.Name, opt =>
        {
            opt.NullSubstitute("FromConstant");
            opt.NullSubstitute(factory);
        });

        var pm = expr.TypeMap.PropertyMaps.Single(p => p.Name == nameof(D.Name));
        Assert.Same(factory, pm.NullSubstitute);
    }

    [Fact]
    public void NullSubstitute_BodyTypeMatchesGenericArg_NullableValueType()
    {
        var expr = NewExpr();

        expr.ForMember(d => d.Score, opt => opt.NullSubstitute(42));

        var pm = expr.TypeMap.PropertyMaps.Single(p => p.Name == nameof(D.Score));
        Assert.NotNull(pm.NullSubstitute);
        Assert.Equal(typeof(int), pm.NullSubstitute!.Body.Type);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj --filter "FullyQualifiedName~Atlas.Tests.MappingExpressionNullSubstituteTests"
```
Expected: build error (`IMemberConfigurationExpression<S, D, string>' does not contain a definition for 'NullSubstitute'`).

- [ ] **Step 3: Add the two interface methods.**

In `src/Atlas/Configuration/IMemberConfigurationExpression.cs`, after the existing `Condition` method, add:

```csharp
    /// <summary>
    /// Supplies a fallback value used when the resolved source value is <c>null</c>.
    /// The substitute is typed as the source member and runs through the same conversion
    /// pipeline as a real source value would (numeric / enum auto-conversion, registered
    /// TypeMaps).
    /// </summary>
    /// <typeparam name="TSourceMember">
    /// The source-member type. Compiler-inferred from the literal in the constant overload
    /// or the lambda body in the Expression overload.
    /// </typeparam>
    /// <param name="constant">The fallback value used when the resolved source is null.</param>
    /// <remarks>
    /// Only meaningful when the resolved source-member type is a reference type or
    /// <see cref="Nullable{T}"/>. <see cref="MapperConfiguration.AssertConfigurationIsValid"/>
    /// reports an error if <c>NullSubstitute</c> is configured on a non-nullable
    /// value-typed source member (the substitute would be unreachable). It also reports
    /// an error if the substitute's type is not assignable to the resolved source-member type.
    /// <para>
    /// Pipeline placement: <b>PreCondition → resolve → null-substitute → convert → transform →
    /// Condition → assign</b>. Value transformers and <c>Condition</c> see the substituted
    /// (non-null) value, never the original null.
    /// </para>
    /// <para>
    /// Multiple <c>NullSubstitute</c> calls on the same member: last-call-wins (matches
    /// <c>MapFrom</c>). Repeating clears the prior substitute.
    /// </para>
    /// <para>
    /// On a map configured with <see cref="IMappingExpression{TSource, TDestination}.ConvertUsing(Func{TSource, TDestination})"/>,
    /// per-member substitutes are silently inactive (the converter replaces all per-member assigns).
    /// Substitutes flow base→derived through inheritance via the existing explicit-config
    /// precedence rule. Substitutes do NOT auto-flip across <c>.ReverseMap()</c> —
    /// reconfigure on the reverse expression.
    /// </para>
    /// <para>
    /// Translates to SQL <c>COALESCE</c> in <c>ProjectTo&lt;&gt;()</c>.
    /// </para>
    /// </remarks>
    void NullSubstitute<TSourceMember>(TSourceMember constant);

    /// <summary>
    /// Expression form of <see cref="NullSubstitute{TSourceMember}(TSourceMember)"/>.
    /// Use for computed defaults (e.g., <c>() =&gt; DateTime.UtcNow</c>) that cannot be
    /// expressed as a literal constant.
    /// </summary>
    /// <typeparam name="TSourceMember">
    /// The source-member type. Compiler-inferred from the lambda body's return type.
    /// </typeparam>
    /// <param name="factory">A no-arg lambda that produces the fallback value.</param>
    /// <remarks>
    /// See <see cref="NullSubstitute{TSourceMember}(TSourceMember)"/> for storage,
    /// projection, multi-call, ConvertUsing, and inheritance semantics — they apply identically.
    /// <para>
    /// The factory is stored as <see cref="Expression{TDelegate}"/>. For projection
    /// translation, the body must be translatable by the underlying LINQ provider —
    /// untranslatable factories fail at query-execution time with the provider's standard
    /// error. Atlas does not pre-inspect lambdas for translatability.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="factory"/> is null.</exception>
    void NullSubstitute<TSourceMember>(Expression<Func<TSourceMember>> factory);
```

- [ ] **Step 4: Implement the two methods + extend `ApplyTo`.**

In `src/Atlas/Configuration/MemberConfigurationExpression.cs`:

1. Add a new private field after `_condition`:

```csharp
    private LambdaExpression? _nullSubstitute;
```

2. Add the two new methods after the existing `Condition` method:

```csharp
    public void NullSubstitute<TSourceMember>(TSourceMember constant)
    {
        // Wrap the constant as a parameterless lambda so storage is uniform with the Expression overload.
        Expression<Func<TSourceMember>> wrapped = () => constant;
        _nullSubstitute = wrapped;
    }

    public void NullSubstitute<TSourceMember>(Expression<Func<TSourceMember>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _nullSubstitute = factory;
    }
```

3. Extend `ApplyTo` with one new line at the end:

```csharp
    public void ApplyTo(PropertyMap propertyMap)
    {
        propertyMap.SourcePath = null;
        propertyMap.CustomExpression = _customExpression;
        propertyMap.ConstantValue = _constantValue;
        propertyMap.HasConstant = _hasConstant;
        propertyMap.Ignored = _ignored;
        propertyMap.PreCondition = _preCondition;
        propertyMap.Condition = _condition;
        propertyMap.NullSubstitute = _nullSubstitute;
    }
```

- [ ] **Step 5: Run the tests to verify they pass.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj --filter "FullyQualifiedName~Atlas.Tests.MappingExpressionNullSubstituteTests"
```
Expected: 6/6 PASS.

- [ ] **Step 6: Run all Atlas.Tests to confirm no regressions.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj
```
Expected: 360 (Task 1 baseline) + 6 = 366 PASS.

- [ ] **Step 7: Commit.**

```bash
git add src/Atlas/Configuration/IMemberConfigurationExpression.cs src/Atlas/Configuration/MemberConfigurationExpression.cs tests/Atlas.Tests/MappingExpressionNullSubstituteTests.cs
git commit -m "$(cat <<'EOF'
IMemberConfigurationExpression gains NullSubstitute (6 tests)

Two overloads: constant (wrapped as parameterless lambda for uniform storage)
and Expression<Func<TSourceMember>> for computed defaults. Last-call-wins
matches MapFrom. Null-arg checks via ArgumentNullException.ThrowIfNull on the
Expression overload.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3 — Inheritance propagation (`InheritanceMerger.CopyConfig`)

One-line extension to the existing config-merge helper. Base→derived substitute flow rides the existing `IsExplicit` precedence machinery — no new logic in `MergeBaseConfig`.

**Files:**
- Modify: `src/Atlas/Internal/InheritanceMerger.cs`
- Create: `tests/Atlas.Tests/Internal/InheritanceMergerNullSubstituteTests.cs`

**Allowlist for the implementer:** ONLY the two files above.

- [ ] **Step 1: Write the failing tests.**

Create `tests/Atlas.Tests/Internal/InheritanceMergerNullSubstituteTests.cs`:

```csharp
using System.Linq.Expressions;
using Atlas.Internal;

namespace Atlas.Tests.Internal;

public class InheritanceMergerNullSubstituteTests
{
    public class Animal { public string? Nickname { get; set; } }
    public class Dog : Animal { }
    public class AnimalDto { public string Nickname { get; set; } = ""; }
    public class DogDto : AnimalDto { }

    [Fact]
    public void BaseNullSubstitute_PropagatesToDerived_WhenDerivedHasNoExplicit()
    {
        var animalTm = new TypeMap(typeof(Animal), typeof(AnimalDto), MemberList.None);
        var basePm = PropertyMap.ForProperty(typeof(AnimalDto).GetProperty(nameof(AnimalDto.Nickname))!);
        Expression<Func<string>> baseSub = () => "Pet";
        basePm.SourcePath = new SourceMemberPath(new[] { typeof(Animal).GetProperty(nameof(Animal.Nickname))! });
        basePm.NullSubstitute = baseSub;
        basePm.IsExplicit = true;
        animalTm.PropertyMaps.Add(basePm);

        var dogTm = new TypeMap(typeof(Dog), typeof(DogDto), MemberList.None);
        dogTm.IncludedBases.Add(animalTm.Pair);

        var typeMaps = new List<TypeMap> { animalTm, dogTm };
        var pairIndex = typeMaps.ToDictionary(t => t.Pair);

        InheritanceMerger.Resolve(typeMaps, pairIndex);

        var derivedPm = dogTm.PropertyMaps.Single(p => p.Name == nameof(AnimalDto.Nickname));
        Assert.Same(baseSub, derivedPm.NullSubstitute);
    }

    [Fact]
    public void DerivedExplicit_OverridesBaseExplicit_NullSubstitute()
    {
        var animalTm = new TypeMap(typeof(Animal), typeof(AnimalDto), MemberList.None);
        var basePm = PropertyMap.ForProperty(typeof(AnimalDto).GetProperty(nameof(AnimalDto.Nickname))!);
        Expression<Func<string>> baseSub = () => "Pet";
        basePm.SourcePath = new SourceMemberPath(new[] { typeof(Animal).GetProperty(nameof(Animal.Nickname))! });
        basePm.NullSubstitute = baseSub;
        basePm.IsExplicit = true;
        animalTm.PropertyMaps.Add(basePm);

        var dogTm = new TypeMap(typeof(Dog), typeof(DogDto), MemberList.None);
        dogTm.IncludedBases.Add(animalTm.Pair);
        var derivedPm = PropertyMap.ForProperty(typeof(DogDto).GetProperty(nameof(DogDto.Nickname))!);
        Expression<Func<string>> derivedSub = () => "Rex";
        derivedPm.SourcePath = new SourceMemberPath(new[] { typeof(Dog).GetProperty(nameof(Dog.Nickname))! });
        derivedPm.NullSubstitute = derivedSub;
        derivedPm.IsExplicit = true;
        dogTm.PropertyMaps.Add(derivedPm);

        var typeMaps = new List<TypeMap> { animalTm, dogTm };
        var pairIndex = typeMaps.ToDictionary(t => t.Pair);

        InheritanceMerger.Resolve(typeMaps, pairIndex);

        var resultPm = dogTm.PropertyMaps.Single(p => p.Name == nameof(DogDto.Nickname));
        Assert.Same(derivedSub, resultPm.NullSubstitute);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj --filter "FullyQualifiedName~Atlas.Tests.Internal.InheritanceMergerNullSubstituteTests"
```
Expected: tests run but FAIL (the merged derived PropertyMap has `null` for `NullSubstitute` because `CopyConfig` doesn't copy it yet).

- [ ] **Step 3: Extend `CopyConfig`.**

In `src/Atlas/Internal/InheritanceMerger.cs`, find the existing `CopyConfig` method. Add one new line after `target.Condition = source.Condition;`:

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
        target.NullSubstitute = source.NullSubstitute;
        // Note: do NOT copy DestinationProperty / DestinationCtorParameter — those are
        // already correctly bound to the target's PropertyMap.
    }
```

- [ ] **Step 4: Run the tests to verify they pass.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj --filter "FullyQualifiedName~Atlas.Tests.Internal.InheritanceMergerNullSubstituteTests"
```
Expected: 2/2 PASS.

- [ ] **Step 5: Run all Atlas.Tests to confirm no regressions.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj
```
Expected: 366 (Task 2 baseline) + 2 = 368 PASS.

- [ ] **Step 6: Commit.**

```bash
git add src/Atlas/Internal/InheritanceMerger.cs tests/Atlas.Tests/Internal/InheritanceMergerNullSubstituteTests.cs
git commit -m "$(cat <<'EOF'
InheritanceMerger.CopyConfig propagates NullSubstitute base->derived (2 tests)

One field-copy line added. Existing IsExplicit precedence rule in
MergeBaseConfig handles the derived-explicit-wins case automatically.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 4 — In-memory codegen (`ApplyNullSubstitute` + `BuildSourceExpression` wire-in)

Add the `ApplyNullSubstitute` helper and insert the call in `BuildSourceExpression` between the resolve step and `ConvertOrMap`. Test by exercising real `IMapper.Map<>()` calls.

**Files:**
- Modify: `src/Atlas/Internal/ExecutionPlanBuilder.cs`
- Create: `tests/Atlas.Tests/ExecutionPlanBuilderNullSubstituteTests.cs`

**Allowlist for the implementer:** ONLY the two files above. Do NOT touch the ctor-arg loop, property-assign loop, `BuildUpdate`, or the existing `WrapWithConditions` / `WrapWithTransformers` helpers — they all consume `BuildSourceExpression`'s output and need no changes.

- [ ] **Step 1: Write the failing tests.**

Create `tests/Atlas.Tests/ExecutionPlanBuilderNullSubstituteTests.cs`:

```csharp
using Atlas.Configuration;

namespace Atlas.Tests;

public class ExecutionPlanBuilderNullSubstituteTests
{
    public class S
    {
        public string? Name { get; set; }
        public int? Score { get; set; }
        public Customer? Customer { get; set; }
    }
    public class Customer { public string? Nick { get; set; } }
    public class D
    {
        public string Name { get; set; } = "";
        public long Score { get; set; }
        public string Nick { get; set; } = "";
    }

    [Fact]
    public void ReferenceTypeSourceNull_UsesSubstitute()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .ForMember(d => d.Name, opt =>
                {
                    opt.MapFrom(s => s.Name);
                    opt.NullSubstitute("Anonymous");
                }));
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<D>(new S { Name = null });

        Assert.Equal("Anonymous", dst.Name);
    }

    [Fact]
    public void ReferenceTypeSourceNonNull_BypassesSubstitute()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .ForMember(d => d.Name, opt =>
                {
                    opt.MapFrom(s => s.Name);
                    opt.NullSubstitute("Anonymous");
                }));
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<D>(new S { Name = "Alice" });

        Assert.Equal("Alice", dst.Name);
    }

    [Fact]
    public void NullableValueTypeSourceNull_UsesSubstitute()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .ForMember(d => d.Score, opt =>
                {
                    opt.MapFrom(s => s.Score);
                    opt.NullSubstitute(0);
                }));
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<D>(new S { Score = null });

        Assert.Equal(0L, dst.Score);
    }

    [Fact]
    public void NullableValueTypeSourceNonNull_UsesValue()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .ForMember(d => d.Score, opt =>
                {
                    opt.MapFrom(s => s.Score);
                    opt.NullSubstitute(0);
                }));
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<D>(new S { Score = 42 });

        Assert.Equal(42L, dst.Score);
    }

    [Fact]
    public void SubstituteParticipatesInNumericConversion()
    {
        // Substitute is int (0); destination is long. ApplyNullSubstitute runs BEFORE
        // ConvertOrMap so the int → long widening is applied to the coalesced value.
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .ForMember(d => d.Score, opt =>
                {
                    opt.MapFrom(s => s.Score);
                    opt.NullSubstitute(7);
                }));
        var mapper = cfg.CreateMapper();

        var nullScore = mapper.Map<D>(new S { Score = null });
        var realScore = mapper.Map<D>(new S { Score = 99 });

        Assert.Equal(7L, nullScore.Score);
        Assert.Equal(99L, realScore.Score);
    }

    public class CtorD { public string Name { get; } public CtorD(string name) { Name = name; } }

    [Fact]
    public void CtorParam_WithNullSubstitute_Works()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, CtorD>(MemberList.None)
                .ForCtorParam("name", opt =>
                {
                    opt.MapFrom(s => s.Name);
                    opt.NullSubstitute("FromCtor");
                }));
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<CtorD>(new S { Name = null });

        Assert.Equal("FromCtor", dst.Name);
    }

    [Fact]
    public void Substitute_AppliesWhenSourcePathIsNullable()
    {
        // Auto-flattened path: s.Customer.Nick → d.Nick. Customer is null → substitute fires.
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .ForMember(d => d.Name, opt => opt.Ignore())
                .ForMember(d => d.Score, opt => opt.Ignore())
                .ForMember(d => d.Nick, opt =>
                {
                    opt.MapFrom(s => s.Customer!.Nick);
                    opt.NullSubstitute("NoCustomer");
                }));
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<D>(new S { Customer = null });

        Assert.Equal("NoCustomer", dst.Nick);
    }

    [Fact]
    public void Substitute_Combined_With_TransformerAndCondition()
    {
        // Pipeline order: substitute → transform → condition.
        // Substitute fires first (null source → "(none)"), transformer trims, condition gates.
        var cfg = new MapperConfiguration(c =>
        {
            c.ValueTransformers.Add<string>(s => s.Trim());
            c.CreateMap<S, D>(MemberList.None)
                .ForMember(d => d.Name, opt =>
                {
                    opt.MapFrom(s => s.Name);
                    opt.NullSubstitute("(none)");
                    opt.Condition((s, name) => name.Length > 0);
                });
        });
        var mapper = cfg.CreateMapper();

        var nullSrc = mapper.Map<D>(new S { Name = null });
        var spaces = mapper.Map<D>(new S { Name = "   " });
        var real = mapper.Map<D>(new S { Name = "  Alice  " });

        // Null source → substitute "(none)" → trim → length 6 → assigns "(none)".
        Assert.Equal("(none)", nullSrc.Name);
        // Spaces source → substitute bypassed (non-null) → trim → "" → length 0 → Condition fails → default(string) = null.
        Assert.Null(spaces.Name);
        // Real source → substitute bypassed → trim → "Alice" → length > 0 → assigns "Alice".
        Assert.Equal("Alice", real.Name);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj --filter "FullyQualifiedName~Atlas.Tests.ExecutionPlanBuilderNullSubstituteTests"
```
Expected: tests run but several FAIL — `ReferenceTypeSourceNull_UsesSubstitute` returns null instead of "Anonymous", etc.

- [ ] **Step 3: Add the `ApplyNullSubstitute` helper.**

In `src/Atlas/Internal/ExecutionPlanBuilder.cs`, add the helper just above the existing `WrapWithTransformers` method:

```csharp
    private static Expression ApplyNullSubstitute(Expression resolvedExpr, PropertyMap pm)
    {
        if (pm.NullSubstitute is null) return resolvedExpr;

        // The substitute is a parameterless lambda; inline its body directly.
        var substituteBody = pm.NullSubstitute.Body;

        // The substitute body's type is TSourceMember per the public API. resolvedExpr.Type
        // may be either the same TSourceMember or a wrapped form (e.g., Nullable<int>).
        // Coalesce handles Nullable<T> natively (returns the unwrapped T).
        if (substituteBody.Type != resolvedExpr.Type)
        {
            // Common case: resolvedExpr is Nullable<T>, substituteBody is T.
            // Expression.Coalesce handles this — pass substituteBody as-is.
            if (Nullable.GetUnderlyingType(resolvedExpr.Type) == substituteBody.Type)
            {
                return Expression.Coalesce(resolvedExpr, substituteBody);
            }
            substituteBody = Expression.Convert(substituteBody, resolvedExpr.Type);
        }

        return Expression.Coalesce(resolvedExpr, substituteBody);
    }
```

- [ ] **Step 4: Wire `ApplyNullSubstitute` into `BuildSourceExpression`.**

In `src/Atlas/Internal/ExecutionPlanBuilder.cs`, find the `BuildSourceExpression` method (around line 335-358). Replace the body so it reads:

```csharp
    private static Expression? BuildSourceExpression(
        PropertyMap pm,
        ParameterExpression srcParam,
        MapperRegistry registry,
        Type targetType)
    {
        if (pm.HasConstant)
            return Expression.Constant(pm.ConstantValue, targetType);

        Expression? resolved;
        if (pm.CustomExpression is not null)
        {
            var rebound = new ParameterReplacer(pm.CustomExpression.Parameters[0], srcParam)
                .Visit(pm.CustomExpression.Body);
            resolved = rebound;
        }
        else if (pm.SourcePath is not null)
        {
            resolved = BuildPathAccess(srcParam, pm.SourcePath.Members);
        }
        else
        {
            return null;
        }

        // NEW: apply NullSubstitute BEFORE ConvertOrMap so the substitute participates
        // in the conversion pipeline exactly like a real value (numeric / enum auto-conversion,
        // registered TypeMaps).
        resolved = ApplyNullSubstitute(resolved!, pm);

        return ConvertOrMap(resolved, targetType, registry);
    }
```

- [ ] **Step 5: Run the tests to verify they pass.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj --filter "FullyQualifiedName~Atlas.Tests.ExecutionPlanBuilderNullSubstituteTests"
```
Expected: 8/8 PASS.

- [ ] **Step 6: Run all Atlas.Tests to confirm no regressions.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj
```
Expected: 368 (Task 3 baseline) + 8 = 376 PASS.

- [ ] **Step 7: Commit.**

```bash
git add src/Atlas/Internal/ExecutionPlanBuilder.cs tests/Atlas.Tests/ExecutionPlanBuilderNullSubstituteTests.cs
git commit -m "$(cat <<'EOF'
ExecutionPlanBuilder applies NullSubstitute via Coalesce (8 tests)

ApplyNullSubstitute wraps the resolved source expression in Expression.Coalesce
upstream of ConvertOrMap, so the substitute participates in the existing
conversion pipeline (numeric widening, registered TypeMaps). Reference-type
and Nullable<T> source members both work natively. No-op when the field is null.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 5 — Projection codegen (`Atlas.Projections`)

Cross-package consumer of `PropertyMap`. This task closes the Bug-4 audit obligation from Task 1. Add `ApplyProjectionNullSubstitute` and wire it into `BuildBinding`. LINQ providers translate `Coalesce` to SQL `COALESCE` natively.

**Files:**
- Modify: `src/Atlas.Projections/Internal/ProjectionPlanBuilder.cs`
- Create: `tests/Atlas.Projections.Tests/Internal/ProjectionPlanBuilderNullSubstituteTests.cs`

**Allowlist for the implementer:** ONLY the two files above. Do NOT touch `ProjectionCompatibility.cs` — substitutes are NOT a projection rejection.

- [ ] **Step 1: Write the failing tests.**

Create `tests/Atlas.Projections.Tests/Internal/ProjectionPlanBuilderNullSubstituteTests.cs`:

```csharp
using System.Linq.Expressions;
using Atlas;
using Atlas.Configuration;
using Atlas.Internal;
using Atlas.Projections;
using Atlas.Projections.Internal;

namespace Atlas.Projections.Tests.Internal;

public class ProjectionPlanBuilderNullSubstituteTests
{
    public struct S { public string? Name { get; set; } public int? Score { get; set; } }
    public class D { public string Name { get; set; } = ""; public int Score { get; set; } }

    private static MapperRegistry BuildRegistry(Action<MapperConfigurationExpression> configure)
    {
        var cfg = new MapperConfiguration(configure);
        return cfg.Internal_Registry;
    }

    [Fact]
    public void Projection_BindingContainsCoalesce_WhenSubstituteSet()
    {
        var registry = BuildRegistry(c =>
            c.CreateMap<S, D>(MemberList.None)
                .ForMember(d => d.Name, opt =>
                {
                    opt.MapFrom(s => s.Name);
                    opt.NullSubstitute("Anonymous");
                }));
        var lambda = ProjectionPlanBuilder.Build(registry, new TypePair(typeof(S), typeof(D)), maxDepth: 5);

        var memberInit = (MemberInitExpression)lambda.Body;
        var nameBinding = memberInit.Bindings.OfType<MemberAssignment>()
            .Single(b => b.Member.Name == nameof(D.Name));

        // The binding must contain a Coalesce node somewhere.
        Assert.True(AssertExpression.Contains<BinaryExpression>(nameBinding.Expression));
        // More precise: top-level node should be Coalesce (or wrap a Convert containing one).
        var coalesce = FindCoalesce(nameBinding.Expression);
        Assert.NotNull(coalesce);
    }

    [Fact]
    public void Projection_BindingHasNoCoalesce_WhenSubstituteUnset()
    {
        var registry = BuildRegistry(c =>
            c.CreateMap<S, D>(MemberList.None)
                .ForMember(d => d.Name, opt => opt.MapFrom(s => s.Name)));
        var lambda = ProjectionPlanBuilder.Build(registry, new TypePair(typeof(S), typeof(D)), maxDepth: 5);

        var memberInit = (MemberInitExpression)lambda.Body;
        var nameBinding = memberInit.Bindings.OfType<MemberAssignment>()
            .Single(b => b.Member.Name == nameof(D.Name));

        // No Coalesce node should appear when no substitute is configured.
        Assert.Null(FindCoalesce(nameBinding.Expression));
    }

    private static BinaryExpression? FindCoalesce(Expression node)
    {
        var visitor = new CoalesceFinder();
        visitor.Visit(node);
        return visitor.Found;
    }

    private sealed class CoalesceFinder : ExpressionVisitor
    {
        public BinaryExpression? Found { get; private set; }
        protected override Expression VisitBinary(BinaryExpression node)
        {
            if (node.NodeType == ExpressionType.Coalesce && Found is null)
                Found = node;
            return base.VisitBinary(node);
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Projections.Tests/Atlas.Projections.Tests.csproj --filter "FullyQualifiedName~Atlas.Projections.Tests.Internal.ProjectionPlanBuilderNullSubstituteTests"
```
Expected: tests FAIL — projection codegen doesn't apply the substitute yet.

- [ ] **Step 3: Add the `ApplyProjectionNullSubstitute` helper.**

In `src/Atlas.Projections/Internal/ProjectionPlanBuilder.cs`, add the helper just above the existing `WrapProjectionWithTransformers` method:

```csharp
    private static Expression ApplyProjectionNullSubstitute(Expression resolvedExpr, PropertyMap pm)
    {
        if (pm.NullSubstitute is null) return resolvedExpr;

        var substituteBody = pm.NullSubstitute.Body;

        if (substituteBody.Type != resolvedExpr.Type)
        {
            if (Nullable.GetUnderlyingType(resolvedExpr.Type) == substituteBody.Type)
                return Expression.Coalesce(resolvedExpr, substituteBody);
            substituteBody = Expression.Convert(substituteBody, resolvedExpr.Type);
        }

        return Expression.Coalesce(resolvedExpr, substituteBody);
    }
```

- [ ] **Step 4: Wire `ApplyProjectionNullSubstitute` into `BuildBinding`.**

In `src/Atlas.Projections/Internal/ProjectionPlanBuilder.cs`, find the `BuildBinding` method. Replace its body so it reads:

```csharp
    private static Expression? BuildBinding(
        Expression srcExpr,
        PropertyMap pm,
        int depth,
        Type targetType,
        MapperRegistry registry,
        int maxDepth)
    {
        if (pm.HasConstant)
            return Expression.Constant(pm.ConstantValue, targetType);

        Expression resolved;
        if (pm.CustomExpression is not null)
        {
            resolved = ParameterReplacer.Replace(
                pm.CustomExpression.Body,
                pm.CustomExpression.Parameters[0],
                srcExpr);
        }
        else if (pm.SourcePath is not null)
        {
            resolved = BuildNullSafePath(srcExpr, pm.SourcePath.Members);
        }
        else
        {
            return null;
        }

        // NEW: apply NullSubstitute BEFORE ConvertOrInline.
        resolved = ApplyProjectionNullSubstitute(resolved, pm);

        return ConvertOrInline(resolved, targetType, depth, registry, maxDepth);
    }
```

- [ ] **Step 5: Run the tests to verify they pass.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Projections.Tests/Atlas.Projections.Tests.csproj --filter "FullyQualifiedName~Atlas.Projections.Tests.Internal.ProjectionPlanBuilderNullSubstituteTests"
```
Expected: 2/2 PASS.

- [ ] **Step 6: Run all projection tests to confirm no regressions.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Projections.Tests/Atlas.Projections.Tests.csproj
```
Expected: 64 pre-existing + 2 new = 66 PASS.

- [ ] **Step 7: Commit.**

```bash
git add src/Atlas.Projections/Internal/ProjectionPlanBuilder.cs tests/Atlas.Projections.Tests/Internal/ProjectionPlanBuilderNullSubstituteTests.cs
git commit -m "$(cat <<'EOF'
Atlas.Projections applies NullSubstitute via Coalesce (2 tests)

ApplyProjectionNullSubstitute wraps the resolved source expression in
Expression.Coalesce upstream of ConvertOrInline. Translates to SQL COALESCE
natively. No projection rejection — substitutes always translate.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 6 — Validator rules (`ValidateNullSubstitutes`)

Add two validation rules: unreachable substitute (non-nullable value-type source) and type-mismatch substitute. Both run during `AssertConfigurationIsValid()`.

**Files:**
- Modify: `src/Atlas/Internal/ConfigurationValidator.cs`
- Create: `tests/Atlas.Tests/ConfigurationValidatorNullSubstituteTests.cs`

**Allowlist for the implementer:** ONLY the two files above.

- [ ] **Step 1: Write the failing tests.**

Create `tests/Atlas.Tests/ConfigurationValidatorNullSubstituteTests.cs`:

```csharp
using Atlas.Configuration;
using Atlas.Internal;

namespace Atlas.Tests;

public class ConfigurationValidatorNullSubstituteTests
{
    public class WithInt { public int Value { get; set; } }
    public class WithIntDto { public int Value { get; set; } }
    public class WithNullableInt { public int? Value { get; set; } }
    public class WithNullableIntDto { public int Value { get; set; } }
    public class WithString { public string? Name { get; set; } }
    public class WithStringDto { public string Name { get; set; } = ""; }
    public class WithEnum { public DayOfWeek Day { get; set; } }
    public class WithEnumDto { public DayOfWeek Day { get; set; } }

    [Fact]
    public void Validator_NullSubstitute_OnNonNullableValueType_Errors()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<WithInt, WithIntDto>(MemberList.None)
                .ForMember(d => d.Value, opt =>
                {
                    opt.MapFrom(s => s.Value);
                    opt.NullSubstitute(0);
                }));

        var ex = Assert.Throws<AtlasConfigurationException>(() => cfg.AssertConfigurationIsValid());
        Assert.Contains("unreachable", ex.Message);
        Assert.Contains("Int32", ex.Message);
    }

    [Fact]
    public void Validator_NullSubstitute_OnEnum_Errors()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<WithEnum, WithEnumDto>(MemberList.None)
                .ForMember(d => d.Day, opt =>
                {
                    opt.MapFrom(s => s.Day);
                    opt.NullSubstitute(DayOfWeek.Monday);
                }));

        var ex = Assert.Throws<AtlasConfigurationException>(() => cfg.AssertConfigurationIsValid());
        Assert.Contains("unreachable", ex.Message);
        Assert.Contains("DayOfWeek", ex.Message);
    }

    [Fact]
    public void Validator_NullSubstitute_OnNullableValueType_Passes()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<WithNullableInt, WithNullableIntDto>(MemberList.None)
                .ForMember(d => d.Value, opt =>
                {
                    opt.MapFrom(s => s.Value);
                    opt.NullSubstitute(0);
                }));

        cfg.AssertConfigurationIsValid();   // no throw
    }

    [Fact]
    public void Validator_NullSubstitute_OnReferenceType_Passes()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<WithString, WithStringDto>(MemberList.None)
                .ForMember(d => d.Name, opt =>
                {
                    opt.MapFrom(s => s.Name);
                    opt.NullSubstitute("Default");
                }));

        cfg.AssertConfigurationIsValid();   // no throw
    }

    [Fact]
    public void Validator_NullSubstitute_TypeMismatch_Errors()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<WithNullableInt, WithNullableIntDto>(MemberList.None)
                .ForMember(d => d.Value, opt =>
                {
                    opt.MapFrom(s => s.Value);
                    opt.NullSubstitute("not-an-int");
                }));

        var ex = Assert.Throws<AtlasConfigurationException>(() => cfg.AssertConfigurationIsValid());
        Assert.Contains("not assignable", ex.Message);
        Assert.Contains("String", ex.Message);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj --filter "FullyQualifiedName~Atlas.Tests.ConfigurationValidatorNullSubstituteTests"
```
Expected: tests FAIL — `AssertConfigurationIsValid` doesn't yet check for substitute misuse, so no exception is thrown.

- [ ] **Step 3: Add the `ValidateNullSubstitutes` method and `ResolveSourceMemberType` helper.**

In `src/Atlas/Internal/ConfigurationValidator.cs`, add the two new methods at the bottom of the class (just before the closing brace):

```csharp
    private static void ValidateNullSubstitutes(TypeMap tm, List<ConfigurationError> errors)
    {
        foreach (var pm in tm.PropertyMaps)
        {
            if (pm.NullSubstitute is null) continue;
            if (pm.Ignored) continue;          // ignored members don't reach codegen
            if (pm.HasConstant) continue;      // literal MapFrom can never be null

            var sourceType = ResolveSourceMemberType(pm);
            if (sourceType is null) continue;  // unresolved — covered by other validator rules

            // Rule 1 — Unreachable: non-nullable value type can never be null.
            if (sourceType.IsValueType && Nullable.GetUnderlyingType(sourceType) is null)
            {
                errors.Add(new ConfigurationError(
                    tm.SourceType, tm.DestinationType, pm.Name,
                    $"NullSubstitute on member '{pm.Name}' is unreachable: source member type " +
                    $"{sourceType.Name} is a non-nullable value type and cannot be null."));
                continue;
            }

            // Rule 2 — Type mismatch: substitute must be assignable to source type.
            var substituteType = pm.NullSubstitute.Body.Type;
            var underlyingSourceType = Nullable.GetUnderlyingType(sourceType) ?? sourceType;

            if (!underlyingSourceType.IsAssignableFrom(substituteType)
                && !sourceType.IsAssignableFrom(substituteType)
                && !NumericConversions.HasImplicitConversion(substituteType, underlyingSourceType))
            {
                errors.Add(new ConfigurationError(
                    tm.SourceType, tm.DestinationType, pm.Name,
                    $"NullSubstitute on member '{pm.Name}' has type {substituteType.Name} " +
                    $"which is not assignable to source-member type {sourceType.Name}."));
            }
        }
    }

    private static Type? ResolveSourceMemberType(PropertyMap pm)
    {
        if (pm.CustomExpression is not null) return pm.CustomExpression.Body.Type;
        if (pm.SourcePath is { Members.Count: > 0 } sp) return sp.Members[^1].PropertyType;
        return null;
    }
```

- [ ] **Step 4: Wire `ValidateNullSubstitutes` into the main `Validate` loop.**

In `src/Atlas/Internal/ConfigurationValidator.cs`, find the `Validate` method's `foreach (var tm in registry.AllTypeMaps)` loop. Add the new call alongside the existing per-typemap validation calls (after `ValidateHooks`):

```csharp
        foreach (var tm in registry.AllTypeMaps)
        {
            // Enum rules (always-on; covers per-value overrides, fallback, foot-gun guard).
            ValidateEnum(tm, errors);

            // Path rules (always-on; covers ForPath / mirrored unflatten paths).
            ValidatePaths(tm, errors);

            // Hook rules (always-on; covers BeforeMap/AfterMap action-type validation).
            ValidateHooks(tm, serviceProvider, errors);

            // NullSubstitute rules (always-on; unreachable + type-mismatch).
            ValidateNullSubstitutes(tm, errors);

            // Strict-mode enum source-side coverage (Task 9).
            if (enumValidationEnabled)
                ValidateEnumStrict(tm, errors);

            // Inheritance rules (design §7.3).
            ValidateInheritance(tm, registry, errors);

            if (tm.MemberList == MemberList.None) continue;

            if (tm.CustomConverter is not null) continue;

            if (tm.MemberList == MemberList.Destination)
                ValidateDestination(tm, registry, errors);
            else
                ValidateSource(tm, registry, errors);
        }
```

- [ ] **Step 5: Run the tests to verify they pass.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj --filter "FullyQualifiedName~Atlas.Tests.ConfigurationValidatorNullSubstituteTests"
```
Expected: 5/5 PASS.

- [ ] **Step 6: Run all Atlas.Tests to confirm no regressions.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj
```
Expected: 376 (Task 4 baseline) + 5 = 381 PASS.

- [ ] **Step 7: Commit.**

```bash
git add src/Atlas/Internal/ConfigurationValidator.cs tests/Atlas.Tests/ConfigurationValidatorNullSubstituteTests.cs
git commit -m "$(cat <<'EOF'
ConfigurationValidator gains ValidateNullSubstitutes (5 tests)

Two new always-on rules:
- Unreachable: NullSubstitute on a non-nullable value-typed source member errors
  (the substitute can never fire).
- Type-mismatch: substitute's type must be assignable to source-member type
  (with numeric conversions and Nullable<T> lifting allowed).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 7 — End-to-end Mapper integration tests

Real `IMapper.Map<>()` calls covering the headline reference-doc example, update-in-place behavior, and inheritance flow.

**Files:**
- Create: `tests/Atlas.Tests/MapperNullSubstituteTests.cs`

**Allowlist for the implementer:** ONLY the test file. No production code change in this task — if a production change is required to make any test pass, the implementer must report DONE_WITH_CONCERNS.

- [ ] **Step 1: Write the tests.**

Create `tests/Atlas.Tests/MapperNullSubstituteTests.cs`:

```csharp
using Atlas.Configuration;

namespace Atlas.Tests;

public class MapperNullSubstituteTests
{
    public sealed class Customer
    {
        public string? Name { get; set; }
        public int? Score { get; set; }
        public DateTime? LastLogin { get; set; }
    }
    public sealed class CustomerDto
    {
        public string Name { get; set; } = "";
        public int Score { get; set; }
        public DateTime LastLogin { get; set; }
    }

    [Fact]
    public void HeadlineExample_FromReferenceDoc()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<Customer, CustomerDto>(MemberList.None)
                .ForMember(d => d.Name, opt =>
                {
                    opt.MapFrom(s => s.Name);
                    opt.NullSubstitute("Anonymous");
                })
                .ForMember(d => d.Score, opt =>
                {
                    opt.MapFrom(s => s.Score);
                    opt.NullSubstitute(0);
                })
                .ForMember(d => d.LastLogin, opt =>
                {
                    opt.MapFrom(s => s.LastLogin);
                    opt.NullSubstitute(() => DateTime.UnixEpoch);
                }));
        var mapper = cfg.CreateMapper();

        var allNull = mapper.Map<CustomerDto>(new Customer
        {
            Name = null,
            Score = null,
            LastLogin = null,
        });
        var allReal = mapper.Map<CustomerDto>(new Customer
        {
            Name = "Alice",
            Score = 42,
            LastLogin = new DateTime(2024, 6, 1),
        });

        Assert.Equal("Anonymous", allNull.Name);
        Assert.Equal(0, allNull.Score);
        Assert.Equal(DateTime.UnixEpoch, allNull.LastLogin);

        Assert.Equal("Alice", allReal.Name);
        Assert.Equal(42, allReal.Score);
        Assert.Equal(new DateTime(2024, 6, 1), allReal.LastLogin);
    }

    [Fact]
    public void Update_NullSubstitute_AppliesUniformly()
    {
        // Update-in-place uses BuildSourceExpression too, so the substitute applies
        // automatically via the same Coalesce wrap.
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<Customer, CustomerDto>(MemberList.None)
                .ForMember(d => d.Name, opt =>
                {
                    opt.MapFrom(s => s.Name);
                    opt.NullSubstitute("Anonymous");
                })
                .ForMember(d => d.Score, opt => opt.Ignore())
                .ForMember(d => d.LastLogin, opt => opt.Ignore()));
        var mapper = cfg.CreateMapper();

        var existing = new CustomerDto { Name = "OldValue" };
        mapper.Map(new Customer { Name = null }, existing);

        // Substitute fires on null source, overwriting the existing destination value.
        Assert.Equal("Anonymous", existing.Name);
    }

    [Fact]
    public void Inheritance_BaseSubstitute_FlowsToDerived()
    {
        // Base map sets NullSubstitute on Nickname; derived map (via Include) inherits it.
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap<Animal, AnimalDto>(MemberList.None)
                .Include<Dog, DogDto>()
                .ForMember(d => d.Nickname, opt =>
                {
                    opt.MapFrom(s => s.Nickname);
                    opt.NullSubstitute("Pet");
                });
            c.CreateMap<Dog, DogDto>(MemberList.None);
        });
        var mapper = cfg.CreateMapper();

        var nullNickname = mapper.Map<DogDto>(new Dog { Nickname = null });
        var realNickname = mapper.Map<DogDto>(new Dog { Nickname = "Rex" });

        Assert.Equal("Pet", nullNickname.Nickname);
        Assert.Equal("Rex", realNickname.Nickname);
    }

    public class Animal { public string? Nickname { get; set; } }
    public class Dog : Animal { }
    public class AnimalDto { public string Nickname { get; set; } = ""; }
    public class DogDto : AnimalDto { }
}
```

- [ ] **Step 2: Run the tests to verify they pass.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj --filter "FullyQualifiedName~Atlas.Tests.MapperNullSubstituteTests"
```
Expected: 3/3 PASS (the production code is already complete from Tasks 1-6; these are integration tests).

- [ ] **Step 3: Run all Atlas.Tests to confirm no regressions.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj
```
Expected: 381 (Task 6 baseline) + 3 = 384 PASS.

- [ ] **Step 4: Commit.**

```bash
git add tests/Atlas.Tests/MapperNullSubstituteTests.cs
git commit -m "$(cat <<'EOF'
End-to-end Mapper NullSubstitute tests (3 tests)

Headline reference-doc example, update-in-place behavior (substitute applies
uniformly via BuildSourceExpression), and base->derived inheritance flow.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 8 — EF Core projection E2E tests

End-to-end against in-memory EF Core SQLite. Verifies the SQL `COALESCE` is genuinely generated and that null-source rows materialize the substituted value.

**Files:**
- Create: `tests/Atlas.Projections.Tests.EFCore/ProjectTo_NullSubstituteTests.cs`

**Allowlist for the implementer:** ONLY the test file. The existing `BlogContext` fixture is reused — no production code change.

- [ ] **Step 1: Read the existing fixture so the test uses the established pattern.**

Read `tests/Atlas.Projections.Tests.EFCore/Fixtures/BlogContext.cs` and `tests/Atlas.Projections.Tests.EFCore/Fixtures/BlogModels.cs`. Confirm seeded post values: Post 1 has `WordCount=100`, Post 2 has `WordCount=null`. The `Post.WordCount` field is `int?` — perfect for testing NullSubstitute.

- [ ] **Step 2: Write the tests.**

Create `tests/Atlas.Projections.Tests.EFCore/ProjectTo_NullSubstituteTests.cs`:

```csharp
using Atlas;
using Atlas.Configuration;
using Atlas.Projections;
using Atlas.Projections.Tests.EFCore.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Projections.Tests.EFCore;

public class ProjectTo_NullSubstituteTests
{
    [Fact]
    public void ProjectTo_NullSubstitute_GeneratesCoalesceSql()
    {
        // Map Post.WordCount (int?) → PostDto.WordCount (long?), substituting -1 for null.
        var config = new MapperConfiguration(c =>
            c.CreateMap<Post, PostDto>(MemberList.None)
                .ForMember(d => d.WordCount, opt =>
                {
                    opt.MapFrom(s => s.WordCount);
                    opt.NullSubstitute(-1);
                })
                .ForMember(d => d.Body, opt => opt.MapFrom(s => s.Body))
                .ForMember(d => d.Id, opt => opt.MapFrom(s => s.Id)));
        using var ctx = BlogContext.CreateInMemory();
        ctx.Seed();

        var sql = ctx.Posts.ProjectTo<PostDto>(config).ToQueryString();

        Assert.Contains("COALESCE", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProjectTo_NullSubstitute_RowReturnsSubstitutedValue()
    {
        // Post 1 has WordCount = 100 → projected as 100.
        // Post 2 has WordCount = null → projected as -1 (substitute).
        var config = new MapperConfiguration(c =>
            c.CreateMap<Post, PostDto>(MemberList.None)
                .ForMember(d => d.WordCount, opt =>
                {
                    opt.MapFrom(s => s.WordCount);
                    opt.NullSubstitute(-1);
                })
                .ForMember(d => d.Body, opt => opt.MapFrom(s => s.Body))
                .ForMember(d => d.Id, opt => opt.MapFrom(s => s.Id)));
        using var ctx = BlogContext.CreateInMemory();
        ctx.Seed();

        var posts = ctx.Posts.OrderBy(p => p.Id).ProjectTo<PostDto>(config).ToList();

        Assert.Equal(2, posts.Count);
        Assert.Equal(100L, posts[0].WordCount);    // real value passes through
        Assert.Equal(-1L, posts[1].WordCount);     // null source → substitute -1 (widened to long)
    }
}
```

- [ ] **Step 3: Run the tests to verify they pass.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Projections.Tests.EFCore/Atlas.Projections.Tests.EFCore.csproj --filter "FullyQualifiedName~Atlas.Projections.Tests.EFCore.ProjectTo_NullSubstituteTests"
```
Expected: 2/2 PASS.

- [ ] **Step 4: Run all EFCore projection tests to confirm no regressions.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Projections.Tests.EFCore/Atlas.Projections.Tests.EFCore.csproj
```
Expected: 10 pre-existing + 2 new = 12 PASS.

- [ ] **Step 5: Commit.**

```bash
git add tests/Atlas.Projections.Tests.EFCore/ProjectTo_NullSubstituteTests.cs
git commit -m "$(cat <<'EOF'
EF Core E2E tests for NullSubstitute in projection (2 tests)

Verifies COALESCE is in the generated SQL and that null-source rows
materialize the substituted value end-to-end.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 9 — README + final coverage check

Add the Null Substitution section to the README, refresh the test count, remove the "Deferred to v2" entry, run a final coverage pass.

**Files:**
- Modify: `README.md`

**Allowlist for the implementer:** ONLY the README.

- [ ] **Step 1: Run the full solution test suite to confirm cumulative state.**

```bash
dotnet test C:/Repos/Atlas/Atlas.slnx
```
Expected: **462 tests pass** across all test projects (432 baseline + 30 new across this feature).

- [ ] **Step 2: Add the Null Substitution subsection to `README.md`.**

Insert the following after the existing "Conditional mapping" section and before "What's in v1":

```markdown
## Null substitution

`NullSubstitute` supplies a fallback value when the resolved source member is null.
The substitute is source-typed and runs through the same conversion pipeline as a
real source value.

```csharp
CreateMap<CustomerEntity, CustomerDto>()
    .ForMember(d => d.Name, opt => opt.NullSubstitute("Unknown"))
    .ForMember(d => d.Score, opt => opt.NullSubstitute(0))
    .ForMember(d => d.GeneratedId, opt => opt.NullSubstitute(() => Guid.NewGuid()));
```

Pipeline placement: **PreCondition → resolve → null-substitute → convert → transform →
Condition → assign**. Value transformers and `Condition` see the substituted (non-null)
value, never the original null.

Validator rules:
- **Unreachable substitute** on a non-nullable value-typed source member errors at
  `AssertConfigurationIsValid()`.
- **Type-mismatch** when the substitute's type isn't assignable to the source-member type errors.

Translates to SQL `COALESCE` in `ProjectTo<>()`. Substitutes flow base→derived through
inheritance via the existing explicit-config precedence rule. Substitutes do NOT
auto-flip across `.ReverseMap()`.
```

- [ ] **Step 3: Remove the deferred entry.**

Find the "Deferred to v2" list. Delete the line:

```
- Null substitution
```

Leave the rest of the bullet list intact.

- [ ] **Step 4: Sanity-check the build.**

```bash
dotnet build C:/Repos/Atlas/Atlas.slnx -c Debug
```
Expected: build succeeds with 0 warnings, 0 errors.

- [ ] **Step 5: Final test run.**

```bash
dotnet test C:/Repos/Atlas/Atlas.slnx
```
Expected: 462 PASS.

- [ ] **Step 6: Commit.**

```bash
git add README.md
git commit -m "$(cat <<'EOF'
docs: README — add null substitution section, remove from deferred list

Null substitution (NullSubstitute constant + Expression overloads) is now
shipped (Atlas v2 #8). Documents the source-typed semantics, pipeline
placement, validator rules, and SQL COALESCE translation.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

- [ ] **Step 7: Coverage spot-check.**

```bash
dotnet test C:/Repos/Atlas/Atlas.slnx -c Debug --collect:"XPlat Code Coverage" --results-directory C:/Repos/Atlas/TestResults/null-substitution
```
Expected: produces `coverage.cobertura.xml` per test project.

If a coverage gate fails on `Atlas` or `Atlas.Projections`, **stop** and add the missing branch coverage in a follow-up commit on this branch — do not skip the gate. Likely missing-branch sites: the `pm.NullSubstitute is null` short-circuit, the `Nullable.GetUnderlyingType` lifted-type branch, the `pm.Ignored` / `pm.HasConstant` validator skip branches.

---

## Final review (controller, before opening the PR)

- [ ] **Run the full holistic review using `superpowers:code-reviewer` on the entire `feat/null-substitution` branch vs. `main`.**

  Non-negotiable per the established workflow rhythm. Value Transformers (#6) caught a Critical reverse-map bug at this stage; Conditional Mapping (#7) had a clean holistic pass. Don't skip.

- [ ] **Confirm cross-package consumer audit (Bug-4 lesson) was honoured.**

  Tasks 1-6 (Atlas core) and Task 5 (Atlas.Projections) together cover every consumer of the new `PropertyMap.NullSubstitute` field. Verify via `git grep -n "pm\.NullSubstitute\|propertyMap\.NullSubstitute\|\.NullSubstitute" src/`.

- [ ] **Confirm no scope-identifying TypeMap metadata was added (Bug-5 lesson).**

  `git diff main...HEAD -- src/Atlas/Internal/TypeMap.cs` should show no output.

- [ ] **Confirm `ProjectionCompatibility` was NOT modified.**

  Substitutes translate; they are NOT a projection rejection. `git diff main...HEAD -- src/Atlas.Projections/Internal/ProjectionCompatibility.cs` should show no output.

- [ ] **Push and open the PR.**

  ```bash
  git push -u origin feat/null-substitution
  gh pr create --title "Atlas v2 #8 — Null Substitution (NullSubstitute)" --body "$(cat <<'EOF'
## Summary
- Adds two per-member `NullSubstitute<TSourceMember>` overloads on `IMemberConfigurationExpression` (constant + Expression-factory).
- Pipeline: `PreCondition → resolve → null-substitute → convert → transform → Condition → assign`.
- Translates to SQL `COALESCE` in `ProjectTo<>()`.
- Two new validator rules: unreachable substitute (non-nullable value-type source) and type-mismatch substitute.
- Inheritance propagates base→derived via existing `IsExplicit` precedence; reverse-map intentionally does not (scope-A).

## Test plan
- [x] All existing tests still pass (432 → 462)
- [x] Coverage gates met on Atlas core (line ≥ 90%, branch ≥ 80%)
- [x] Coverage gates met on Atlas.Projections
- [x] EF Core E2E confirms `COALESCE` SQL generation
- [x] Update-in-place semantics verified end-to-end
- [x] Inheritance flow verified end-to-end
- [x] Validator unreachable + type-mismatch rules verified

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
  ```

---

## Implementer Notes (per-task ground rules)

These are repeated in the design's §11 but reproduced here so the implementer-subagent sees them in-context.

1. **Don't try to "optimize" the constant overload by skipping the lambda wrap.** The constant overload wraps as `() => constant`. Storing the constant directly via `Expression.Constant` would diverge from the Expression overload's storage shape, forcing codegen to branch. Keep the uniform `LambdaExpression` storage.

2. **Cross-package consumer audit.** Tasks 4 and 6 cover `Atlas` core; Task 5 covers `Atlas.Projections`. Both are required because `PropertyMap` is a shared shape consumed by both packages.

3. **Per-member only.** Don't add a per-typemap or per-call surface — those are explicitly deferred to v3 in the design.

4. **Don't modify `ProjectionCompatibility`.** Substitutes translate via `Coalesce`/`COALESCE`; no rejection rule.

5. **Don't modify `BuildPocoLambda` / `BuildUpdate` / `BuildBinding` outside Task 4 / Task 5's specific wire-ins.** All three call `BuildSourceExpression` (or `BuildBinding` in projection), and the helper applies the substitute internally. No additional wiring needed in those callers.

6. **`Expression.Coalesce` handles `Nullable<T>` natively.** The helper checks `Nullable.GetUnderlyingType(resolvedExpr.Type) == substituteBody.Type` — that's the lifted case. For exact-type matches, `Coalesce` works too. For genuinely-mismatched types, the validator catches the configuration error before codegen runs.

7. **Watch for tests that quietly diverge from the plan.** If an assertion in this plan turns out to be wrong (e.g., the plan asserts a specific exception type that isn't actually thrown, or asserts a specific Expression node shape that the implementation produces differently), report DONE_WITH_CONCERNS rather than silently changing the test. The Hooks Task 10 review and ConditionalMapping Task 8 review both found this anti-pattern.

8. **xUnit v3 only — no FluentAssertions.** All assertions use `Assert.X()` style (per memory `feedback_no_fluentassertions`).

9. **Holistic review is non-negotiable.** Even if every per-task review passes cleanly, the controller MUST run `superpowers:code-reviewer` over the whole branch before opening the PR. Value Transformers proved this catches Critical bugs that per-task review misses.
