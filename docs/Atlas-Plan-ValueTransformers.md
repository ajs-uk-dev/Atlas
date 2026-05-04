# Atlas Value Transformers Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add value transformers to Atlas per `docs/Atlas-Design-ValueTransformers.md` — post-processing functions per type, registered at three scopes (global on `MapperConfigurationExpression`, profile on `MapperProfile`, type-map on `IMappingExpression`). Composition is broad-first (`global → profile → type-map`); within each scope, FIFO. The API takes `Expression<Func<T, T>>` so the same registration works for both in-memory `Map<>()` and `query.ProjectTo<>()`.

**Architecture:** Purely additive. One new public class (`ValueTransformerCollection`). Two new properties on existing public types (`MapperConfigurationExpression.ValueTransformers`, `MapperProfile.ValueTransformers`). One new fluent method on `IMappingExpression<,>` (`AddTransform<T>`). Three new fields on `TypeMap` (`TypeMapTransformers`, `EffectiveTransformers`, `OriginatingProfile`). One new internal static class (`TransformerResolver`) that runs in `MapperConfiguration` between `ReverseMapMirror.Mirror` and `tm.Seal()`. `ExecutionPlanBuilder` gains a `WrapWithTransformers` helper used at all property + ctor-arg assignment sites in `BuildPocoLambda` and `BuildUpdate`. `Atlas.Projections.ProjectionPlanBuilder` gains a sister helper for projection bindings (uses parameter substitution, NOT `Expression.Invoke`, so EF Core can translate). No new packages.

**Tech Stack:** .NET 10, xUnit v3 (built-in `Assert.X()`, no FluentAssertions), coverlet.

**Spec reference:** `docs/Atlas-Design-ValueTransformers.md`. Section numbers in this plan (e.g. "§5.5") refer to the spec.

**v1 conventions to mirror (do NOT deviate):**
- File-scoped namespaces.
- Internal types under `Internal/` subfolder.
- `internal sealed class` / `internal sealed record` / `internal static class` unless otherwise noted.
- Test naming: `MethodOrFeature_Condition_ExpectedResult`.
- xUnit v3, `[Fact]` / `[Theory]` + `[InlineData]`.
- `TreatWarningsAsErrors=true` is on globally; `GenerateDocumentationFile=true` is on; `CS1591` is suppressed.
- **NEVER use FluentAssertions.** xUnit v3 built-in `Assert.X()` only.
- **`AtlasConfigurationException` only takes `IReadOnlyList<ConfigurationError>`** — wrap a single error in a 1-element list (not relevant for this feature, but mentioned as a project-wide convention).
- **Forward refs in XML docs:** for types not yet introduced (e.g., `TransformerResolver` referenced before Task 5 lands), use `<c>TypeName</c>` (literal text) instead of `<see cref="TypeName"/>` to avoid CS1574 build errors.
- Run tests with `dotnet test --nologo` (PowerShell on Windows).

**Branching:** Implement on a new branch `feat/value-transformers` cut from current `main` (HEAD `8c1bffe` after the design + this plan land). Each task ends in a commit. After all tasks land, the implementer runs `superpowers:finishing-a-development-branch` Option 2 (push + PR) per the established pattern.

**Key files in the codebase to read first** (for context):
- `src/Atlas/Internal/TypeMap.cs` — three fields added in Task 2
- `src/Atlas/MapperConfigurationExpression.cs` — property added in Task 3 (insertion near `EnumValidationEnabled`)
- `src/Atlas/MapperProfile.cs` — property added in Task 3; `CreateMap` modified in Task 3 to set `OriginatingProfile`
- `src/Atlas/Configuration/IMappingExpression.cs` — `AddTransform<T>` added in Task 4
- `src/Atlas/Configuration/MappingExpression.cs` — `AddTransform<T>` implementation added in Task 4
- `src/Atlas/Internal/ExecutionPlanBuilder.cs` — `WrapWithTransformers` + 4 routing sites added in Task 7
- `src/Atlas/MapperConfiguration.cs` — `TransformerResolver.Resolve` call added in Task 6 (insertion between `ReverseMapMirror.Mirror(typeMaps)` and `foreach (var tm in typeMaps) tm.Seal()`)
- `src/Atlas.Projections/Internal/ProjectionPlanBuilder.cs` — `WrapProjectionWithTransformers` + routing sites added in Task 9

**Test count baseline:** 363 tests pre-feature (298 Atlas + 57 Projections + 8 Projections.EFCore) — verified at HEAD `8c1bffe` before this plan commit. Expected after this plan: ~395 (≈32 new transformer tests).

**Coverage targets:** line ≥ 90%, branch ≥ 80% on `Atlas` core. Verified by Task 10.

---

## Task 1: Set up branch

**Files:** none modified; branch creation only.

- [ ] **Step 1: Create the feature branch**

```powershell
git checkout main
git pull
git checkout -b feat/value-transformers
```

- [ ] **Step 2: Verify clean baseline**

Run: `dotnet test --nologo`

Expected: 363 tests pass (298 Atlas + 57 Projections + 8 Projections.EFCore). If any test fails, stop and report — the baseline must be green before changes start.

- [ ] **Step 3: No commit** — branching only.

---

## Task 2: Data model — `ValueTransformerCollection` + `TypeMap` fields

**Files:**
- Create: `src/Atlas/ValueTransformerCollection.cs`
- Modify: `src/Atlas/Internal/TypeMap.cs`
- Create: `tests/Atlas.Tests/Internal/ValueTransformerCollectionTests.cs`

Spec references: §4.1, §5.1, §5.4.

### Step 1: Write failing tests

Create `tests/Atlas.Tests/Internal/ValueTransformerCollectionTests.cs`:

```csharp
using System.Linq.Expressions;

namespace Atlas.Tests.Internal;

public class ValueTransformerCollectionTests
{
    [Fact]
    public void Add_ReturnsThis_ForChaining()
    {
        var collection = new ValueTransformerCollection();
        Expression<Func<string, string>> transformer = s => s;

        var returned = collection.Add(transformer);

        Assert.Same(collection, returned);
    }

    [Fact]
    public void Add_MultipleSameType_AppendsInFifoOrder()
    {
        var collection = new ValueTransformerCollection();
        Expression<Func<string, string>> first = s => s + "1";
        Expression<Func<string, string>> second = s => s + "2";
        Expression<Func<string, string>> third = s => s + "3";

        collection.Add(first);
        collection.Add(second);
        collection.Add(third);

        var all = collection.AllTransformers;
        var stringEntries = all[typeof(string)];
        Assert.Equal(3, stringEntries.Count);
        Assert.Same(first, stringEntries[0]);
        Assert.Same(second, stringEntries[1]);
        Assert.Same(third, stringEntries[2]);
    }

    [Fact]
    public void Add_MultipleDifferentTypes_StoredSeparately()
    {
        var collection = new ValueTransformerCollection();
        Expression<Func<string, string>> stringT = s => s;
        Expression<Func<int, int>> intT = i => i + 1;

        collection.Add(stringT);
        collection.Add(intT);

        var all = collection.AllTransformers;
        Assert.Equal(2, all.Count);
        Assert.Single(all[typeof(string)]);
        Assert.Single(all[typeof(int)]);
        Assert.Same(stringT, all[typeof(string)][0]);
        Assert.Same(intT, all[typeof(int)][0]);
    }

    [Fact]
    public void Add_NullTransformer_Throws()
    {
        var collection = new ValueTransformerCollection();
        Assert.Throws<ArgumentNullException>(() => collection.Add<string>(null!));
    }
}
```

### Step 2: Run tests to verify failure

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~ValueTransformerCollectionTests" --nologo`

Expected: 4 failures — `ValueTransformerCollection` does not exist.

### Step 3: Create `ValueTransformerCollection` public class

Create `src/Atlas/ValueTransformerCollection.cs`:

```csharp
using System.Linq.Expressions;

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
    private readonly Dictionary<Type, List<LambdaExpression>> _byType = new();

    /// <summary>
    /// Registers a transformer for destination type <typeparamref name="T"/>. Multiple
    /// calls for the same <typeparamref name="T"/> append in FIFO order.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// Thrown if <paramref name="transformer"/> is null.
    /// </exception>
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

    /// <summary>
    /// Internal accessor used by <c>TransformerResolver</c> at config-build time.
    /// Exposes the underlying per-type lists as read-only views.
    /// </summary>
    internal IReadOnlyDictionary<Type, IReadOnlyList<LambdaExpression>> AllTransformers
    {
        get
        {
            return _byType.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<LambdaExpression>)kv.Value);
        }
    }
}
```

### Step 4: Run tests to verify pass

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~ValueTransformerCollectionTests" --nologo`

Expected: 4/4 pass.

### Step 5: Add three fields to `TypeMap`

Edit `src/Atlas/Internal/TypeMap.cs`. Add three new properties after the existing `BeforeHooks` / `AfterHooks` properties (added in feature #5). The exact insertion point: after the `AfterHooks` declaration and before `CustomConverter`.

```csharp
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
    /// FIFO order. The list is the application order: <c>Effective[T][0]</c> runs first on
    /// the raw source value; <c>Effective[T][^1]</c> runs last and produces the final value
    /// assigned to the destination property. Empty (no entry) means no transformers apply
    /// for that type.
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

You will also need to add `using System.Linq.Expressions;` at the top of `TypeMap.cs` if it's not already there.

### Step 6: Run full test suite

Run: `dotnet test --nologo`

Expected: 367 tests pass (363 baseline + 4 new). Existing tests unaffected — purely additive change.

### Step 7: Commit

```powershell
git add src/Atlas/ValueTransformerCollection.cs src/Atlas/Internal/TypeMap.cs tests/Atlas.Tests/Internal/ValueTransformerCollectionTests.cs
git commit -m "Add ValueTransformerCollection + TypeMap fields (TypeMapTransformers, EffectiveTransformers, OriginatingProfile) (4 tests)"
```

---

## Task 3: Global + profile registries; set `OriginatingProfile` in `MapperProfile.CreateMap`

**Files:**
- Modify: `src/Atlas/MapperConfigurationExpression.cs`
- Modify: `src/Atlas/MapperProfile.cs`
- Create: `tests/Atlas.Tests/MapperConfigurationExpressionValueTransformersTests.cs`
- Create: `tests/Atlas.Tests/MapperProfileValueTransformersTests.cs`

Spec references: §4.2, §4.3, §5.2.

### Step 1: Write failing tests for `MapperConfigurationExpression`

Create `tests/Atlas.Tests/MapperConfigurationExpressionValueTransformersTests.cs`:

```csharp
namespace Atlas.Tests;

public class MapperConfigurationExpressionValueTransformersTests
{
    [Fact]
    public void ValueTransformers_PropertyExposedAndAccessible()
    {
        var expr = new MapperConfigurationExpression();

        Assert.NotNull(expr.ValueTransformers);
    }

    [Fact]
    public void ValueTransformers_RegistrationsPersistOnExpression()
    {
        var expr = new MapperConfigurationExpression();

        expr.ValueTransformers.Add<string>(s => s.Trim());
        expr.ValueTransformers.Add<int>(i => i + 1);

        var all = expr.ValueTransformers.AllTransformers;
        Assert.Equal(2, all.Count);
        Assert.Single(all[typeof(string)]);
        Assert.Single(all[typeof(int)]);
    }
}
```

### Step 2: Write failing tests for `MapperProfile`

Create `tests/Atlas.Tests/MapperProfileValueTransformersTests.cs`:

```csharp
namespace Atlas.Tests;

public class MapperProfileValueTransformersTests
{
    private sealed class EmptyProfile : MapperProfile { }

    private sealed class TrimAndLowerProfile : MapperProfile
    {
        public TrimAndLowerProfile()
        {
            ValueTransformers.Add<string>(s => s.Trim());
            ValueTransformers.Add<string>(s => s.ToLowerInvariant());
        }
    }

    [Fact]
    public void ValueTransformers_PropertyExposedAndAccessible()
    {
        var profile = new EmptyProfile();

        Assert.NotNull(profile.ValueTransformers);
    }

    [Fact]
    public void ValueTransformers_RegisteredInConstructor_Persists()
    {
        var profile = new TrimAndLowerProfile();

        var stringEntries = profile.ValueTransformers.AllTransformers[typeof(string)];
        Assert.Equal(2, stringEntries.Count);
    }
}
```

### Step 3: Run tests to verify failure

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~ValueTransformersTests" --nologo`

Expected: 4 failures across both test classes — `ValueTransformers` property does not exist on either type.

### Step 4: Add `ValueTransformers` to `MapperConfigurationExpression`

Edit `src/Atlas/MapperConfigurationExpression.cs`. Add the following property after the existing `EnumValidationEnabled` property (around line 19-20):

```csharp
    /// <summary>
    /// Global value transformers — post-processing functions applied to every value of a
    /// given destination type, regardless of which map produces it. Composed broad-first
    /// (global → profile → type-map) with finer-scope transformers running after broader
    /// ones. Within this scope, transformers run in registration order (FIFO).
    /// </summary>
    /// <remarks>
    /// Transformers are stored as <c>Expression&lt;Func&lt;T, T&gt;&gt;</c> so the same
    /// declaration works for both in-memory <see cref="IMapper.Map{TDestination}"/> (compiled
    /// to a delegate) and <c>query.ProjectTo&lt;T&gt;()</c> (inlined into the projection
    /// lambda for SQL translation by the underlying provider).
    /// </remarks>
    public ValueTransformerCollection ValueTransformers { get; } = new();
```

### Step 5: Add `ValueTransformers` to `MapperProfile` and modify `CreateMap` to set `OriginatingProfile`

Edit `src/Atlas/MapperProfile.cs`.

(a) Add the new property at the end of the class body (alongside `SourceMemberNamingConvention` etc.):

```csharp
    /// <summary>
    /// Profile-scoped value transformers — apply only to TypeMaps registered in this profile.
    /// See <see cref="MapperConfigurationExpression.ValueTransformers"/> for global scope and
    /// <c>IMappingExpression.AddTransform</c> for type-map scope.
    /// </summary>
    public ValueTransformerCollection ValueTransformers { get; } = new();
```

(b) Modify `CreateMap` to set `OriginatingProfile` on the new TypeMap. Locate the current method body (it should look approximately like):

```csharp
    protected IMappingExpression<TSource, TDestination> CreateMap<TSource, TDestination>(
        MemberList memberList = MemberList.Destination)
    {
        var map = new TypeMap(typeof(TSource), typeof(TDestination), memberList)
        {
            RegistrationOrigin = $"CreateMap<{typeof(TSource).Name}, {typeof(TDestination).Name}>()"
        };
        _typeMaps.Add(map);
        return new Atlas.Configuration.MappingExpression<TSource, TDestination>(map, _typeMaps.Add);
    }
```

Replace with:

```csharp
    protected IMappingExpression<TSource, TDestination> CreateMap<TSource, TDestination>(
        MemberList memberList = MemberList.Destination)
    {
        var map = new TypeMap(typeof(TSource), typeof(TDestination), memberList)
        {
            RegistrationOrigin = $"CreateMap<{typeof(TSource).Name}, {typeof(TDestination).Name}>()",
            OriginatingProfile = this,
        };
        _typeMaps.Add(map);
        return new Atlas.Configuration.MappingExpression<TSource, TDestination>(map, _typeMaps.Add);
    }
```

(Note: `MapperConfigurationExpression.CreateMap` does NOT set `OriginatingProfile` — directly-registered TypeMaps have no profile. `TransformerResolver` correctly handles the null case in Task 5.)

### Step 6: Run tests to verify pass

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~ValueTransformersTests" --nologo`

Expected: 4/4 pass.

### Step 7: Run full test suite

Run: `dotnet test --nologo`

Expected: 371 tests pass (367 + 4). Existing tests remain green — `OriginatingProfile` defaults to null and is unused yet.

### Step 8: Commit

```powershell
git add src/Atlas/MapperConfigurationExpression.cs src/Atlas/MapperProfile.cs tests/Atlas.Tests/MapperConfigurationExpressionValueTransformersTests.cs tests/Atlas.Tests/MapperProfileValueTransformersTests.cs
git commit -m "Add ValueTransformers property to MapperConfigurationExpression + MapperProfile; set OriginatingProfile in MapperProfile.CreateMap (4 tests)"
```

---

## Task 4: `AddTransform<T>` fluent surface

**Files:**
- Modify: `src/Atlas/Configuration/IMappingExpression.cs`
- Modify: `src/Atlas/Configuration/MappingExpression.cs`
- Create: `tests/Atlas.Tests/MappingExpressionAddTransformTests.cs`

Spec references: §4.4, §5.3.

### Step 1: Write failing tests

Create `tests/Atlas.Tests/MappingExpressionAddTransformTests.cs`:

```csharp
using System.Linq.Expressions;
using Atlas.Configuration;
using Atlas.Internal;

namespace Atlas.Tests;

public class MappingExpressionAddTransformTests
{
    public sealed class S { public string? V { get; set; } }
    public sealed class D { public string? V { get; set; } }

    private static MappingExpression<S, D> NewExpr() =>
        new(new TypeMap(typeof(S), typeof(D), MemberList.None));

    [Fact]
    public void AddTransform_AppendsToTypeMapTransformers()
    {
        var expr = NewExpr();
        Expression<Func<string, string>> transformer = s => s.Trim();

        expr.AddTransform(transformer);

        Assert.Single(expr.TypeMap.TypeMapTransformers);
        Assert.Single(expr.TypeMap.TypeMapTransformers[typeof(string)]);
        Assert.Same(transformer, expr.TypeMap.TypeMapTransformers[typeof(string)][0]);
    }

    [Fact]
    public void AddTransform_MultipleSameType_PreservesFifoOrder()
    {
        var expr = NewExpr();
        Expression<Func<string, string>> first = s => s + "1";
        Expression<Func<string, string>> second = s => s + "2";
        Expression<Func<string, string>> third = s => s + "3";

        expr.AddTransform(first);
        expr.AddTransform(second);
        expr.AddTransform(third);

        var entries = expr.TypeMap.TypeMapTransformers[typeof(string)];
        Assert.Equal(3, entries.Count);
        Assert.Same(first, entries[0]);
        Assert.Same(second, entries[1]);
        Assert.Same(third, entries[2]);
    }

    [Fact]
    public void AddTransform_NullTransformer_Throws()
    {
        var expr = NewExpr();
        Assert.Throws<ArgumentNullException>(() =>
            expr.AddTransform<string>(null!));
    }

    [Fact]
    public void AddTransform_ReturnsExpression_ForChaining()
    {
        var expr = NewExpr();
        var returned = expr.AddTransform<string>(s => s);

        Assert.Same(expr, returned);
    }
}
```

### Step 2: Run tests to verify failure

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~MappingExpressionAddTransformTests" --nologo`

Expected: 4 failures — `AddTransform` does not exist.

### Step 3: Add `AddTransform<T>` to `IMappingExpression<TSource, TDestination>`

Edit `src/Atlas/Configuration/IMappingExpression.cs`. Add the following method declaration after the last `AfterMap` declaration (added in feature #5) and before the closing brace:

```csharp
    // ---- Value transformers ----

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

### Step 4: Implement `AddTransform<T>` in `MappingExpression<TSource, TDestination>`

Edit `src/Atlas/Configuration/MappingExpression.cs`. Add the following method at the end of the class body (after the last `AfterMap` implementation from feature #5, before the private helpers):

```csharp
    // ---- Value transformers ----

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

You may need `using System.Linq.Expressions;` at the top of `MappingExpression.cs` if it's not already imported.

### Step 5: Run tests to verify pass

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~MappingExpressionAddTransformTests" --nologo`

Expected: 4/4 pass.

### Step 6: Run full test suite

Run: `dotnet test --nologo`

Expected: 375 tests pass (371 + 4).

### Step 7: Commit

```powershell
git add src/Atlas/Configuration/IMappingExpression.cs src/Atlas/Configuration/MappingExpression.cs tests/Atlas.Tests/MappingExpressionAddTransformTests.cs
git commit -m "Add AddTransform<T> fluent surface (4 tests)"
```

---

## Task 5: `TransformerResolver`

**Files:**
- Create: `src/Atlas/Internal/TransformerResolver.cs`
- Create: `tests/Atlas.Tests/Internal/TransformerResolverTests.cs`

Spec references: §5.5.

### Step 1: Write failing tests

Create `tests/Atlas.Tests/Internal/TransformerResolverTests.cs`:

```csharp
using System.Linq.Expressions;
using Atlas.Internal;

namespace Atlas.Tests.Internal;

public class TransformerResolverTests
{
    public class TestProfile : MapperProfile { }

    private static TypeMap NewTypeMap(MapperProfile? profile = null) =>
        new(typeof(string), typeof(string), MemberList.None) { OriginatingProfile = profile };

    [Fact]
    public void Resolve_GlobalOnly_EffectiveContainsGlobal()
    {
        var global = new ValueTransformerCollection();
        Expression<Func<string, string>> g = s => s + "g";
        global.Add(g);
        var tm = NewTypeMap();

        TransformerResolver.Resolve(new[] { tm }, global);

        var effective = tm.EffectiveTransformers[typeof(string)];
        Assert.Single(effective);
        Assert.Same(g, effective[0]);
    }

    [Fact]
    public void Resolve_ProfileOnly_EffectiveContainsProfile()
    {
        var global = new ValueTransformerCollection();
        var profile = new TestProfile();
        Expression<Func<string, string>> p = s => s + "p";
        profile.ValueTransformers.Add(p);
        var tm = NewTypeMap(profile);

        TransformerResolver.Resolve(new[] { tm }, global);

        var effective = tm.EffectiveTransformers[typeof(string)];
        Assert.Single(effective);
        Assert.Same(p, effective[0]);
    }

    [Fact]
    public void Resolve_TypeMapOnly_EffectiveContainsTypeMap()
    {
        var global = new ValueTransformerCollection();
        var tm = NewTypeMap();
        Expression<Func<string, string>> t = s => s + "t";
        tm.TypeMapTransformers[typeof(string)] = new List<LambdaExpression> { t };

        TransformerResolver.Resolve(new[] { tm }, global);

        var effective = tm.EffectiveTransformers[typeof(string)];
        Assert.Single(effective);
        Assert.Same(t, effective[0]);
    }

    [Fact]
    public void Resolve_AllThreeScopes_ComposedBroadFirst()
    {
        var global = new ValueTransformerCollection();
        Expression<Func<string, string>> g = s => s + "g";
        global.Add(g);

        var profile = new TestProfile();
        Expression<Func<string, string>> p = s => s + "p";
        profile.ValueTransformers.Add(p);

        var tm = NewTypeMap(profile);
        Expression<Func<string, string>> t = s => s + "t";
        tm.TypeMapTransformers[typeof(string)] = new List<LambdaExpression> { t };

        TransformerResolver.Resolve(new[] { tm }, global);

        var effective = tm.EffectiveTransformers[typeof(string)];
        Assert.Equal(3, effective.Count);
        Assert.Same(g, effective[0]);   // global first (broadest)
        Assert.Same(p, effective[1]);
        Assert.Same(t, effective[2]);   // type-map last (narrowest)
    }

    [Fact]
    public void Resolve_NoTransformers_EffectiveEmpty()
    {
        var global = new ValueTransformerCollection();
        var tm = NewTypeMap();

        TransformerResolver.Resolve(new[] { tm }, global);

        Assert.Empty(tm.EffectiveTransformers);
    }

    [Fact]
    public void Resolve_OriginatingProfileNull_OnlyGlobalAndTypeMap()
    {
        var global = new ValueTransformerCollection();
        Expression<Func<string, string>> g = s => s + "g";
        global.Add(g);

        var tm = NewTypeMap(profile: null);   // No profile.
        Expression<Func<string, string>> t = s => s + "t";
        tm.TypeMapTransformers[typeof(string)] = new List<LambdaExpression> { t };

        TransformerResolver.Resolve(new[] { tm }, global);

        var effective = tm.EffectiveTransformers[typeof(string)];
        Assert.Equal(2, effective.Count);
        Assert.Same(g, effective[0]);
        Assert.Same(t, effective[1]);
    }
}
```

### Step 2: Run tests to verify failure

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~TransformerResolverTests" --nologo`

Expected: 6 failures — `TransformerResolver` does not exist.

### Step 3: Create `TransformerResolver`

Create `src/Atlas/Internal/TransformerResolver.cs`:

```csharp
using System.Linq.Expressions;

namespace Atlas.Internal;

/// <summary>
/// Composes global + profile + type-map value transformers into each TypeMap's
/// <see cref="TypeMap.EffectiveTransformers"/> dictionary. Runs at config-build time, after
/// <c>ReverseMapMirror.Mirror</c>, before <c>tm.Seal()</c>.
/// </summary>
internal static class TransformerResolver
{
    public static void Resolve(
        IEnumerable<TypeMap> typeMaps,
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

### Step 4: Run tests to verify pass

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~TransformerResolverTests" --nologo`

Expected: 6/6 pass.

### Step 5: Run full test suite

Run: `dotnet test --nologo`

Expected: 381 tests pass (375 + 6).

### Step 6: Commit

```powershell
git add src/Atlas/Internal/TransformerResolver.cs tests/Atlas.Tests/Internal/TransformerResolverTests.cs
git commit -m "Add TransformerResolver — broad-first compose of global+profile+type-map per TypeMap (6 tests)"
```

---

## Task 6: Wire `TransformerResolver.Resolve` into `MapperConfiguration`

**Files:**
- Modify: `src/Atlas/MapperConfiguration.cs`

This is a tiny wiring task — insert one line in the constructor between `ReverseMapMirror.Mirror(typeMaps)` and the `tm.Seal()` foreach loop. No new tests — Tasks 5, 7, and 8 cover the integration. Spec references: §2.2, §5.6.

### Step 1: Locate the insertion point

Open `src/Atlas/MapperConfiguration.cs`. Find the constructor body. After feature #4 (ReverseMap) and feature #5 (Hooks), it should look approximately like:

```csharp
        InheritanceMerger.Resolve(typeMaps, pairIndex);

        foreach (var tm in typeMaps)
            ConventionEngine.ResolveMissingMembers(tm, _conventionOptions, HasRegisteredMap);

        ReverseMapMirror.Mirror(typeMaps);

        foreach (var tm in typeMaps)
            tm.Seal();

        expression.MarkBuilt();
        _registry = new MapperRegistry(typeMaps, _stringToEnumCache);
```

### Step 2: Insert the `TransformerResolver.Resolve` call

Replace the snippet with:

```csharp
        InheritanceMerger.Resolve(typeMaps, pairIndex);

        foreach (var tm in typeMaps)
            ConventionEngine.ResolveMissingMembers(tm, _conventionOptions, HasRegisteredMap);

        ReverseMapMirror.Mirror(typeMaps);

        TransformerResolver.Resolve(typeMaps, expression.ValueTransformers);

        foreach (var tm in typeMaps)
            tm.Seal();

        expression.MarkBuilt();
        _registry = new MapperRegistry(typeMaps, _stringToEnumCache);
```

### Step 3: Run full test suite

Run: `dotnet test --nologo`

Expected: 381 tests still pass. The `TransformerResolver` call is a no-op for any TypeMap without transformers configured at any scope (the algorithm produces empty `EffectiveTransformers` and writes nothing).

### Step 4: Commit

```powershell
git add src/Atlas/MapperConfiguration.cs
git commit -m "Wire TransformerResolver.Resolve into MapperConfiguration build sequence (no new tests)"
```

---

## Task 7: `ExecutionPlanBuilder` codegen wrap

**Files:**
- Modify: `src/Atlas/Internal/ExecutionPlanBuilder.cs`
- Create: `tests/Atlas.Tests/ExecutionPlanBuilderTransformerTests.cs`

Add `WrapWithTransformers` helper. Route property + ctor-arg assignments in `BuildPocoLambda` and the property loop in `BuildUpdate` through it. Spec references: §6.1, §6.2, §6.3.

### Step 1: Write failing tests

Create `tests/Atlas.Tests/ExecutionPlanBuilderTransformerTests.cs`:

```csharp
using System.Linq.Expressions;

namespace Atlas.Tests;

public class ExecutionPlanBuilderTransformerTests
{
    public class S { public string? Name { get; set; } public int Count { get; set; } }
    public class D { public string? Name { get; set; } public int Count { get; set; } }

    public class CtorD
    {
        public string Name { get; }
        public CtorD(string name) { Name = name; }
    }

    public sealed class Inner { public string? Name { get; set; } }
    public sealed class Outer { public Inner? Child { get; set; } }

    [Fact]
    public void NoTransformer_PropertyAssign_SourceUntouched()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None));
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<D>(new S { Name = "  hello  " });

        Assert.Equal("  hello  ", dst.Name);   // untouched
    }

    [Fact]
    public void SingleTransformer_WrapsSource()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .AddTransform<string>(s => s == null ? null! : s.Trim()));
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<D>(new S { Name = "  hello  " });

        Assert.Equal("hello", dst.Name);
    }

    [Fact]
    public void TwoTransformers_ComposeLeftToRight()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .AddTransform<string>(s => s == null ? null! : s.Trim())
                .AddTransform<string>(s => s == null ? null! : s.ToUpperInvariant()));
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<D>(new S { Name = "  hello  " });

        // Step 1: trim → "hello". Step 2: upper → "HELLO".
        Assert.Equal("HELLO", dst.Name);
    }

    [Fact]
    public void CtorArg_AlsoTransformed()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, CtorD>(MemberList.None)
                .AddTransform<string>(s => s == null ? null! : s.Trim()));
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<CtorD>(new S { Name = "  hello  " });

        Assert.Equal("hello", dst.Name);
    }

    [Fact]
    public void NestedPath_AlsoTransformed()
    {
        // ForPath into a nested destination — transformer must wrap before the nested-assign emit.
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, Outer>(MemberList.None)
                .ForPath(d => d.Child!.Name, opt => opt.MapFrom(s => s.Name))
                .AddTransform<string>(s => s == null ? null! : s.Trim()));
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<Outer>(new S { Name = "  hello  " });

        Assert.NotNull(dst.Child);
        Assert.Equal("hello", dst.Child!.Name);
    }
}
```

### Step 2: Run tests to verify failure

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~ExecutionPlanBuilderTransformerTests" --nologo`

Expected: 4 failures (the 4 transformer tests). `NoTransformer_PropertyAssign_SourceUntouched` already passes.

### Step 3: Add `WrapWithTransformers` helper

Edit `src/Atlas/Internal/ExecutionPlanBuilder.cs`. Add the following private static helper at the bottom of the class (alongside `BuildNestedAssign` from feature #4 and `BuildHookCall` from feature #5):

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
            // CRITICAL: inline the transformer's body via parameter substitution.
            // Do NOT use Expression.Invoke(transformer, current) — EF Core (and most LINQ
            // providers) cannot translate Invoke nodes to SQL; they CAN translate inlined
            // bodies. The same pattern is used today by ProjectionPlanBuilder for MapFrom
            // lambdas.
            var paramSubst = new ParameterReplacer(transformer.Parameters[0], current);
            current = paramSubst.Visit(transformer.Body)!;
        }
        return current;
    }
```

(`ParameterReplacer` is the existing private nested class in `ExecutionPlanBuilder` — already used today for `MapFrom` lambda inlining. No need to add it.)

### Step 4: Wire `WrapWithTransformers` into `BuildPocoLambda` — ctor-args

In `BuildPocoLambda`, locate the ctor-args block (which currently builds an array of expressions for `Expression.New(ctor, args)`). It looks approximately like:

```csharp
            var args = ctor.GetParameters().Select(p =>
            {
                var pm = ctorParamMaps.FirstOrDefault(m =>
                    string.Equals(m.Name, p.Name, StringComparison.OrdinalIgnoreCase));
                if (pm is null)
                {
                    if (p.HasDefaultValue) return Expression.Constant(p.DefaultValue, p.ParameterType);
                    return (Expression)Expression.Default(p.ParameterType);
                }
                return BuildSourceExpression(pm, srcParam, registry, p.ParameterType)
                    ?? Expression.Default(p.ParameterType);
            }).ToArray();
```

Replace with (wrap the result of each branch with `WrapWithTransformers`):

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
                return WrapWithTransformers(sourceExpr, p.ParameterType, typeMap);
            }).ToArray();
```

### Step 5: Wire `WrapWithTransformers` into `BuildPocoLambda` — property loop

In the same `BuildPocoLambda` method, locate the property-binding loop. After feature #4 + #5 it should look approximately like:

```csharp
        foreach (var pm in propertyMaps)
        {
            if (pm.Ignored) continue;
            if (pm.DestinationProperty is null) continue;

            var sourceExpr = BuildSourceExpression(pm, srcParam, registry, pm.DestinationProperty.PropertyType);
            if (sourceExpr is null) continue;

            if (pm.DestinationPath is { } path && path.Count > 1)
            {
                statements.Add(BuildNestedAssign(destVar, path, sourceExpr));
            }
            else
            {
                statements.Add(Expression.Assign(
                    Expression.Property(destVar, pm.DestinationProperty),
                    sourceExpr));
            }
        }
```

Replace with (wrap the `sourceExpr` once into a new local `transformed`, use `transformed` in both branches):

```csharp
        foreach (var pm in propertyMaps)
        {
            if (pm.Ignored) continue;
            if (pm.DestinationProperty is null) continue;

            var sourceExpr = BuildSourceExpression(pm, srcParam, registry, pm.DestinationProperty.PropertyType);
            if (sourceExpr is null) continue;

            var transformed = WrapWithTransformers(sourceExpr, pm.DestinationProperty.PropertyType, typeMap);

            if (pm.DestinationPath is { } path && path.Count > 1)
            {
                statements.Add(BuildNestedAssign(destVar, path, transformed));
            }
            else
            {
                statements.Add(Expression.Assign(
                    Expression.Property(destVar, pm.DestinationProperty),
                    transformed));
            }
        }
```

### Step 6: Wire `WrapWithTransformers` into `BuildUpdate` — property loop

`BuildUpdate` has the same property-binding loop shape. Locate it:

```csharp
        foreach (var pm in typeMap.PropertyMaps)
        {
            if (pm.Ignored) continue;
            if (pm.DestinationProperty is null) continue;

            var sourceExpr = BuildSourceExpression(pm, srcParam, registry, pm.DestinationProperty.PropertyType);
            if (sourceExpr is null) continue;

            if (pm.DestinationPath is { } path && path.Count > 1)
            {
                statements.Add(BuildNestedAssign(destParam, path, sourceExpr));
            }
            else
            {
                statements.Add(Expression.Assign(
                    Expression.Property(destParam, pm.DestinationProperty),
                    sourceExpr));
            }
        }
```

Replace with:

```csharp
        foreach (var pm in typeMap.PropertyMaps)
        {
            if (pm.Ignored) continue;
            if (pm.DestinationProperty is null) continue;

            var sourceExpr = BuildSourceExpression(pm, srcParam, registry, pm.DestinationProperty.PropertyType);
            if (sourceExpr is null) continue;

            var transformed = WrapWithTransformers(sourceExpr, pm.DestinationProperty.PropertyType, typeMap);

            if (pm.DestinationPath is { } path && path.Count > 1)
            {
                statements.Add(BuildNestedAssign(destParam, path, transformed));
            }
            else
            {
                statements.Add(Expression.Assign(
                    Expression.Property(destParam, pm.DestinationProperty),
                    transformed));
            }
        }
```

### Step 7: Run tests to verify pass

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~ExecutionPlanBuilderTransformerTests" --nologo`

Expected: 5/5 pass.

### Step 8: Run full test suite

Run: `dotnet test --nologo`

Expected: 386 tests pass (381 + 5).

### Step 9: Commit

```powershell
git add src/Atlas/Internal/ExecutionPlanBuilder.cs tests/Atlas.Tests/ExecutionPlanBuilderTransformerTests.cs
git commit -m "Add WrapWithTransformers helper + route property + ctor-args + nested-path through it (5 tests)"
```

---

## Task 8: End-to-end `MapperValueTransformerTests`

**Files:**
- Create: `tests/Atlas.Tests/MapperValueTransformerTests.cs`

Six end-to-end tests covering global + profile + type-map composition, collection per-element, update-in-place, and nested map property transformation. Spec references: §7.7.

### Step 1: Write the tests

Create `tests/Atlas.Tests/MapperValueTransformerTests.cs`:

```csharp
namespace Atlas.Tests;

public class MapperValueTransformerTests
{
    public class S { public string? Name { get; set; } public decimal Total { get; set; } }
    public class D { public string? Name { get; set; } public decimal Total { get; set; } }

    public sealed class TrimProfile : MapperProfile
    {
        public TrimProfile()
        {
            ValueTransformers.Add<string>(s => s == null ? null! : s.Trim());
            CreateMap<S, D>(MemberList.None)
                .AddTransform<decimal>(d => Math.Round(d, 2));
        }
    }

    [Fact]
    public void Global_StringTrim_AppliesEverywhere()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.ValueTransformers.Add<string>(s => s == null ? null! : s.Trim());
            c.CreateMap<S, D>(MemberList.None);
        });
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<D>(new S { Name = "  hello  " });

        Assert.Equal("hello", dst.Name);
    }

    [Fact]
    public void Profile_StringLower_ComposesAfterGlobal()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.ValueTransformers.Add<string>(s => s == null ? null! : s.ToLowerInvariant());
            c.AddProfile<TrimProfile>();
        });
        var mapper = cfg.CreateMapper();

        // Global runs first (lower), then profile (trim). Final: trimmed lowercase.
        var dst = mapper.Map<D>(new S { Name = "  HELLO  ", Total = 100.456m });

        Assert.Equal("hello", dst.Name);
        Assert.Equal(100.46m, dst.Total);   // type-map round
    }

    [Fact]
    public void TypeMap_DecimalRound_AppliesOnlyToConfiguredMap()
    {
        // Two maps in the same profile; only one has the decimal transformer (defined on the
        // map directly via .AddTransform<decimal>). The other map's decimal stays unrounded.
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap<S, D>(MemberList.None)
                .AddTransform<decimal>(d => Math.Round(d, 2));
            c.CreateMap<D, S>(MemberList.None);   // No transformer.
        });
        var mapper = cfg.CreateMapper();

        var d = mapper.Map<D>(new S { Total = 1.234m });
        var s = mapper.Map<S>(new D { Total = 1.234m });

        Assert.Equal(1.23m, d.Total);     // S → D rounds
        Assert.Equal(1.234m, s.Total);    // D → S does not
    }

    [Fact]
    public void Collection_PerElementTransformerFires()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap<S, D>(MemberList.None)
                .AddTransform<string>(s => s == null ? null! : s.Trim());
        });
        var mapper = cfg.CreateMapper();

        var srcs = new List<S>
        {
            new() { Name = "  a  " },
            new() { Name = "  b  " },
            new() { Name = "  c  " },
        };
        var dsts = mapper.Map<List<S>, List<D>>(srcs);

        Assert.Equal(3, dsts.Count);
        Assert.Equal("a", dsts[0].Name);
        Assert.Equal("b", dsts[1].Name);
        Assert.Equal("c", dsts[2].Name);
    }

    [Fact]
    public void UpdateInPlace_TransformersApply()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap<S, D>(MemberList.None)
                .AddTransform<string>(s => s == null ? null! : s.Trim());
        });
        var mapper = cfg.CreateMapper();

        var existing = new D { Name = "old" };
        mapper.Map<S, D>(new S { Name = "  new  " }, existing);

        Assert.Equal("new", existing.Name);
    }

    public sealed class Outer { public Inner? Child { get; set; } public string? Top { get; set; } }
    public sealed class OuterDto { public InnerDto? Child { get; set; } public string? Top { get; set; } }
    public sealed class Inner { public string? Name { get; set; } }
    public sealed class InnerDto { public string? Name { get; set; } }

    [Fact]
    public void NestedMap_DestinationPropertyTransformed()
    {
        // The OUTER (Outer → OuterDto) map has a string transformer. It applies to the outer's
        // own string properties (e.g., Top). The Child property is mapped via a nested
        // (Inner → InnerDto) map — that nested map does NOT inherit transformers, so Inner.Name
        // is NOT trimmed by the outer's transformer. (To trim Inner.Name, the user would need
        // a global/profile-scoped string transformer or a type-map transformer on Inner→InnerDto.)
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap<Outer, OuterDto>(MemberList.None)
                .AddTransform<string>(s => s == null ? null! : s.Trim());
            c.CreateMap<Inner, InnerDto>(MemberList.None);
        });
        var mapper = cfg.CreateMapper();

        var src = new Outer { Top = "  outer  ", Child = new Inner { Name = "  inner  " } };
        var dst = mapper.Map<OuterDto>(src);

        Assert.Equal("outer", dst.Top);                 // Outer's transformer trims Top
        Assert.NotNull(dst.Child);
        Assert.Equal("  inner  ", dst.Child!.Name);     // Inner→InnerDto map has no transformer
    }
}
```

### Step 2: Run tests to verify pass

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~MapperValueTransformerTests" --nologo`

Expected: 6/6 pass. (If any fail, STOP and report which task introduced the gap. Do NOT modify production code; this task is test-only.)

### Step 3: Run full test suite

Run: `dotnet test --nologo`

Expected: 392 tests pass (386 + 6).

### Step 4: Commit

```powershell
git add tests/Atlas.Tests/MapperValueTransformerTests.cs
git commit -m "Add end-to-end MapperValueTransformerTests (6 tests)"
```

---

## Task 9: `Atlas.Projections` integration

**Files:**
- Modify: `src/Atlas.Projections/Internal/ProjectionPlanBuilder.cs`
- Create: `tests/Atlas.Projections.Tests/ProjectionTransformerTests.cs`

Extend the projection builder to wrap projection bindings with the same compose logic (using parameter substitution, NOT `Expression.Invoke` — required for EF Core translation). Spec references: §6.6.

### Step 1: Write failing tests

Create `tests/Atlas.Projections.Tests/ProjectionTransformerTests.cs`:

```csharp
namespace Atlas.Projections.Tests;

public class ProjectionTransformerTests
{
    public class S { public string? Name { get; set; } public int Count { get; set; } }
    public class D { public string? Name { get; set; } public int Count { get; set; } }

    [Fact]
    public void ProjectTo_TranslatableTransformer_AppliesInProjection()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap<S, D>(MemberList.None)
                .AddTransform<string>(s => s == null ? null! : s.Trim());
        });
        var srcs = new List<S>
        {
            new() { Name = "  hello  ", Count = 1 },
            new() { Name = "  world  ", Count = 2 },
        }.AsQueryable();

        var dsts = srcs.ProjectTo<D>(cfg).ToList();

        Assert.Equal(2, dsts.Count);
        Assert.Equal("hello", dsts[0].Name);
        Assert.Equal("world", dsts[1].Name);
    }

    [Fact]
    public void ProjectTo_TwoComposedTransformers_BothInlined()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap<S, D>(MemberList.None)
                .AddTransform<string>(s => s == null ? null! : s.Trim())
                .AddTransform<string>(s => s == null ? null! : s.ToUpperInvariant());
        });
        var srcs = new List<S> { new() { Name = "  hello  " } }.AsQueryable();

        var dsts = srcs.ProjectTo<D>(cfg).ToList();

        Assert.Single(dsts);
        Assert.Equal("HELLO", dsts[0].Name);
    }

    [Fact]
    public void ProjectTo_GlobalTransformer_AppliesInProjection()
    {
        // Verify the entire scope chain (global) flows into projection bindings.
        var cfg = new MapperConfiguration(c =>
        {
            c.ValueTransformers.Add<string>(s => s == null ? null! : s.Trim());
            c.CreateMap<S, D>(MemberList.None);
        });
        var srcs = new List<S> { new() { Name = "  hello  " } }.AsQueryable();

        var dsts = srcs.ProjectTo<D>(cfg).ToList();

        Assert.Single(dsts);
        Assert.Equal("hello", dsts[0].Name);
    }
}
```

### Step 2: Run tests to verify failure

Run: `dotnet test tests/Atlas.Projections.Tests --filter "FullyQualifiedName~ProjectionTransformerTests" --nologo`

Expected: 3 failures — projection currently does not apply transformers, so the strings are returned untrimmed (or not transformed in the expected way).

### Step 3: Add `WrapProjectionWithTransformers` helper to `ProjectionPlanBuilder`

Edit `src/Atlas.Projections/Internal/ProjectionPlanBuilder.cs`. Add the following private static helper near the bottom of the class (alongside `RejectHooksOrThrow` from feature #5):

```csharp
    private static Expression WrapProjectionWithTransformers(
        Expression sourceExpr,
        Type destType,
        TypeMap typeMap)
    {
        if (!typeMap.EffectiveTransformers.TryGetValue(destType, out var transformers))
            return sourceExpr;

        Expression current = sourceExpr;
        foreach (var transformer in transformers)
        {
            // CRITICAL: inline via parameter substitution (NOT Expression.Invoke).
            // The existing ParameterReplacer.Replace helper does this already.
            current = ParameterReplacer.Replace(transformer.Body, transformer.Parameters[0], current);
        }
        return current;
    }
```

(`ParameterReplacer.Replace(body, param, replacement)` is the existing static helper used elsewhere in `ProjectionPlanBuilder` for `MapFrom` lambda inlining. No new code needed for the substitution itself.)

### Step 4: Wire `WrapProjectionWithTransformers` into the property-binding loop

Locate the `BuildBody` method's property-binding loop. After feature #5 it should look approximately like:

```csharp
        var bindings = new List<MemberBinding>();
        foreach (var pm in propertyMaps)
        {
            if (pm.Ignored) continue;
            if (pm.DestinationProperty is null) continue;
            if (!ProjectionCompatibility.IsBindingProjectable(pm, out _)) continue;
            var binding = BuildBinding(srcExpr, pm, depth, pm.DestinationProperty.PropertyType, registry, maxDepth);
            if (binding is null) continue;
            bindings.Add(Expression.Bind(pm.DestinationProperty, binding));
        }
```

Replace with (wrap `binding` after `BuildBinding`):

```csharp
        var bindings = new List<MemberBinding>();
        foreach (var pm in propertyMaps)
        {
            if (pm.Ignored) continue;
            if (pm.DestinationProperty is null) continue;
            if (!ProjectionCompatibility.IsBindingProjectable(pm, out _)) continue;
            var binding = BuildBinding(srcExpr, pm, depth, pm.DestinationProperty.PropertyType, registry, maxDepth);
            if (binding is null) continue;

            binding = WrapProjectionWithTransformers(binding, pm.DestinationProperty.PropertyType, tm);

            bindings.Add(Expression.Bind(pm.DestinationProperty, binding));
        }
```

### Step 5: Wire into the ctor-args block as well

In the same `BuildBody` method, locate the ctor-args block (top of the method, building `args` for `Expression.New(ctor, args)`):

```csharp
            var args = ctor.GetParameters().Select(p =>
            {
                var pm = ctorParamMaps.FirstOrDefault(m =>
                    string.Equals(m.Name, p.Name, StringComparison.OrdinalIgnoreCase));
                if (pm is null)
                {
                    return p.HasDefaultValue
                        ? (Expression)Expression.Constant(p.DefaultValue, p.ParameterType)
                        : Expression.Default(p.ParameterType);
                }
                return BuildBinding(srcExpr, pm, depth, p.ParameterType, registry, maxDepth)
                    ?? Expression.Default(p.ParameterType);
            }).ToArray();
```

Replace with:

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
                return WrapProjectionWithTransformers(sourceExpr, p.ParameterType, tm);
            }).ToArray();
```

### Step 6: Run tests to verify pass

Run: `dotnet test tests/Atlas.Projections.Tests --filter "FullyQualifiedName~ProjectionTransformerTests" --nologo`

Expected: 3/3 pass.

### Step 7: Run full test suite

Run: `dotnet test --nologo`

Expected: 395 tests pass (392 + 3). Existing projection tests remain green — the wrap is a no-op for any TypeMap with empty `EffectiveTransformers`.

### Step 8: Commit

```powershell
git add src/Atlas.Projections/Internal/ProjectionPlanBuilder.cs tests/Atlas.Projections.Tests/ProjectionTransformerTests.cs
git commit -m "Atlas.Projections inlines value transformers into projection bindings via parameter substitution (3 tests)"
```

---

## Task 10: README + coverage check

**Files:**
- Modify: `README.md`

Add a `## Value transformers` section to the README with a worked example. Remove "Value transformers" from the deferred-features list. Verify coverage targets met.

### Step 1: Read the current README

Read `README.md`. Identify:
- Where the existing "Before/after hooks" section sits (the new Value transformers section will go after it)
- The exact text of the "Deferred to v2" list (confirm "Value transformers" is present as a deferred entry)
- The current coverage table (it should exist after the prior features added it)

### Step 2: Edit the README

Two edits:

(a) After the `## Before/after hooks` section and before the "Deferred to v2" list, add:

```markdown
## Value transformers

Apply post-processing functions to every value of a given destination type, registered at
three scopes — **global** on `MapperConfigurationExpression`, **profile** on `MapperProfile`,
and **type-map** on the fluent surface (`AddTransform<T>`). Composition is broad-first
(`global → profile → type-map`); within each scope, transformers run in registration order.

\`\`\`csharp
public sealed class TrimAndLowerProfile : MapperProfile
{
    public TrimAndLowerProfile()
    {
        // Profile-level: applies to every map in this profile.
        ValueTransformers.Add<string>(s => s == null ? null! : s.Trim());

        CreateMap<Order, OrderDto>()
            .AddTransform<decimal>(d => Math.Round(d, 2));   // Type-map level
    }
}

var cfg = new MapperConfiguration(c =>
{
    // Global: applies to every map in the entire configuration.
    c.ValueTransformers.Add<string>(s => s == null ? null! : s.ToLowerInvariant());
    c.AddProfile<TrimAndLowerProfile>();
});
\`\`\`

The API takes `Expression<Func<T, T>>` so the same registration works for both:

- **In-memory** `mapper.Map<TDestination>(source)` — the expression is compiled to a delegate.
- **`query.ProjectTo<TDestination>(cfg)`** — the expression is inlined into the LINQ
  projection. EF Core (and other LINQ providers) translate translatable lambdas to SQL
  natively (e.g., `s => s.Trim()` → `LTRIM(RTRIM(...))`).

**Type matching is exact.** `Add<string>` matches `string` destinations only — not `object`,
not other assignable types. `Add<int>` matches `int` only — register `Add<int?>` separately
for nullable destinations.

**Hooks vs transformers.** Hooks (`BeforeMap` / `AfterMap`) fire around the WHOLE map; value
transformers fire on each property assignment of the matching type. The two compose
independently.

**ProjectTo limitations.** A transformer using constructs the LINQ provider can't translate
(custom static method calls, captures of mutable state, etc.) will fail at query-execution
time with the provider's standard "expression cannot be translated" error. Atlas does not
pre-inspect lambdas.

**Transformers do NOT auto-propagate** via `.ReverseMap()` or `Include`/`IncludeBase` — each
direction or derived map declares its own type-map-level transformers, or relies on
profile/global scope.
```

(NOTE: the triple-backticks above are escaped with backslash for this prompt to parse correctly. When you paste into the README, use unescaped triple-backticks.)

(b) Remove the entry `"Value transformers (post-processing per type at multiple scopes)"` from the deferred-features list.

### Step 3: Run coverage check

Run from `C:\Repos\Atlas`:

```powershell
dotnet test --nologo --collect:"XPlat Code Coverage" --results-directory ./TestResults
```

Use `reportgenerator` to extract per-project numbers:

```powershell
dotnet tool restore
reportgenerator -reports:./TestResults/**/coverage.cobertura.xml -targetdir:./TestResults/CoverageReport -reporttypes:TextSummary
```

Read `./TestResults/CoverageReport/Summary.txt`. Verify:
- Atlas: line ≥ 90%, branch ≥ 80%
- Atlas.Extensions.DependencyInjection: line ≥ 85%, branch ≥ 75%
- Atlas.Projections: line ≥ 90%, branch ≥ 80%

If a gate fails, identify the gap and add 1-2 targeted tests. Likely gaps:
- `TransformerResolver` rare branches (e.g., the `OriginatingProfile is { } prof` false branch when there's no profile but there IS a profile-scope entry — though such an entry can't reach the resolver without a profile, so this branch may be unreachable in practice).
- `WrapWithTransformers` early-return on missing entry — already covered by `NoTransformer_PropertyAssign_SourceUntouched`.

### Step 4: Update the README's coverage table

Use the actual measured percentages from Step 3.

### Step 5: Run final full test suite

Run: `dotnet test --nologo`

Expected: 395 tests pass (or whatever the actual final count was after any coverage-gap tests added in Step 3).

### Step 6: Commit

```powershell
git add README.md
git commit -m "docs: README — add value transformers section, refresh coverage numbers"
```

(If you needed to add coverage-gap tests in Step 3, include those test files and add a note to the commit message.)

---

## Final review

After all 10 tasks land on the `feat/value-transformers` branch:

- [ ] **Step 1: Final-review by `superpowers:code-reviewer`**

The implementing controller (the agent driving subagent-driven-development) dispatches `superpowers:code-reviewer` over the full branch diff. Per memory, the holistic review has caught real bugs in EVERY prior feature (including 1 Critical + 2 Important in ReverseMap, and 1 Important in Hooks). Don't skip.

Particular things to surface in this review:

- **Cross-package consumer audit (Bug 4 lesson):** `TypeMap.EffectiveTransformers` is consumed by `ExecutionPlanBuilder` (Task 7) AND `Atlas.Projections.ProjectionPlanBuilder` (Task 9). Verify no other consumers of `TypeMap` need updating.
- **`ParameterReplacer` correctness:** Verify both `WrapWithTransformers` (in `ExecutionPlanBuilder`) and `WrapProjectionWithTransformers` (in `ProjectionPlanBuilder`) use parameter substitution (NOT `Expression.Invoke`). The latter is critical for EF Core translatability.
- **`OriginatingProfile` setting:** Verify `MapperProfile.CreateMap` sets it but `MapperConfigurationExpression.CreateMap` does NOT (directly-registered TypeMaps have no profile). `TransformerResolver` correctly handles the null case.
- **Hooks + transformers ordering:** the property loop in `BuildPocoLambda` now does `BeforeHooks → property assigns (each wrapped) → AfterHooks`. Verify the wrap is INSIDE the property assign (transforming each value individually), not OUTSIDE the per-property iteration (which would be wrong).
- **Inheritance non-propagation:** verify a base map's type-map transformer does NOT fire on derived maps (transformers don't merge via `InheritanceMerger`).
- **Reverse-map non-propagation:** verify `.ReverseMap()` produces a reverse TypeMap with empty `TypeMapTransformers` and `EffectiveTransformers`.

- [ ] **Step 2: Address any Critical / Important findings**

Per the review-catch frequency norm (~1-3 issues per holistic review), expect 0-2 issues. Fix in-branch with one or more `review fix:` commits (do NOT amend prior commits).

- [ ] **Step 3: Push and open PR**

Use `superpowers:finishing-a-development-branch` Option 2: push the branch, open a PR titled "Add value transformers (ValueTransformers + AddTransform)" with the design doc summary in the body and the actual final test/coverage numbers.

- [ ] **Step 4: After merge — memory updates**

After the user confirms the PR is merged:
- Update `atlas_v2_design_docs_deferred.md` to mark feature #6 as shipped (linking to `docs/Atlas-Design-ValueTransformers.md`) and to identify feature #7 (Conditional Mapping) as next.
- Update `feedback_atlas_v2_workflow.md` baseline test count: 363 → ~395 (or actual measured).
- If the holistic review surfaced a NEW class of bug not covered by `feedback_pseudocode_concrete_trace.md` (currently 4 documented bugs), append it as Bug 5.

---

## Summary

- **10 tasks**, ~32 new tests (4 + 4 + 4 + 6 + 0 + 5 + 6 + 3 = 32).
- **Test baseline:** 363 → ~395.
- **Coverage targets:** line ≥ 90%, branch ≥ 80% on `Atlas` core.
- **New public types:** `ValueTransformerCollection` (one new public class). No new exceptions, no new interfaces.
- **No new package references.** Atlas core's existing dependencies suffice (Microsoft.Extensions.DependencyInjection.Abstractions was already added in feature #5).
- **Branch:** `feat/value-transformers` cut from `main` HEAD `8c1bffe` (after design + plan land).
- **Model selection** (per memory's per-task guidance): haiku for Tasks 1, 2, 3, 4, 6, 10; sonnet for Tasks 5, 7, 8, 9.
