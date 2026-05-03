# Atlas Enum Surface Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an enum-aware mapping surface to the core `Atlas` package per `docs/Atlas-Design-EnumSurface.md`: enum-typed properties auto-convert without explicit declaration (enum↔enum, enum↔string, enum↔underlying numeric), and `CreateMap<TEnumSrc, TEnumDst>()` opens a fluent surface for customization (`MapByValue` / `MapByName` strategies, `MapValue` per-value overrides, source-side `Ignore`, `WithFallback`, opt-in strict source-side validation).

**Architecture:** Purely additive to `Atlas` core. One new public exception type (`AtlasMappingException`). Five new methods on `IMappingExpression<,>` (enum-only; throw at config time if types aren't enums). One new method on `MapperConfigurationExpression` (`EnableEnumMappingValidation`). One new field on `TypeMap` (`EnumConfig`). Four new internal classes (`EnumMapConfig`, `EnumResolver`, `EnumConversions`, `StringToEnumCache`). Enum dispatch prologue in `ExecutionPlanBuilder.Build` (zero overhead when both sides aren't enum). Always-on + opt-in-strict enum validation in `ConfigurationValidator`. No new packages, no public type signatures change shape, no `Atlas.Projections` changes.

**Tech Stack:** .NET 10, xUnit v3 (built-in `Assert.X()`, no FluentAssertions), coverlet.

**Spec reference:** `docs/Atlas-Design-EnumSurface.md`. Section numbers in this plan (e.g. "§6.1") refer to the spec.

**v1 conventions to mirror (do not deviate):**
- File-scoped namespaces.
- Internal types under `Internal/` subfolder.
- `internal sealed class` / `internal static class` unless otherwise noted.
- Test naming: `MethodOrFeature_Condition_ExpectedResult`.
- xUnit v3, `[Fact]` / `[Theory]` + `[InlineData]`.
- `TreatWarningsAsErrors=true` is on globally; `GenerateDocumentationFile=true` is on; `CS1591` is suppressed.

**Branching:** Implement on a new branch `feat/enum-surface` cut from current `main` (HEAD `c14013e` after the design + this plan land). Each task ends in a commit. After all tasks land, the implementer runs the `superpowers:finishing-a-development-branch` flow (Option 2: push + PR) per the same pattern used for `feat/inheritance`.

**Key files in v1 + Inheritance to read first** (for context, not to modify outside the plan):
- `src/Atlas/Internal/TypeMap.cs` — field added in Task 4
- `src/Atlas/Internal/PropertyMap.cs` — no changes (read-only context for understanding)
- `src/Atlas/Internal/MappingInvoker.cs` — no changes (registry lookup pattern)
- `src/Atlas/Internal/ExecutionPlanBuilder.cs` — enum prologue added in Task 6 (line 12 dispatch is the insertion point)
- `src/Atlas/Internal/ConventionEngine.cs` — `IsCompatible` extended in Task 7 (line 148-156)
- `src/Atlas/Internal/ConfigurationValidator.cs` — enum rules added in Tasks 8-9
- `src/Atlas/MapperConfiguration.cs` — flag plumbed through ctor in Task 4 (line 28-44)
- `src/Atlas/MapperConfigurationExpression.cs` — flag method added in Task 4
- `src/Atlas/Configuration/IMappingExpression.cs` + `MappingExpression.cs` — 5 methods added in Task 5
- `src/Atlas/AtlasConfigurationException.cs` — sibling pattern for `AtlasMappingException` in Task 2

**Test count baseline:** 216 tests pre-feature (156 Atlas + 52 Projections + 8 Projections.EFCore). Expected after this plan: ~263-268 (≈48-53 new enum tests).

---

## Task 1: Set up branch

**Files:** none modified; branch creation only.

- [ ] **Step 1: Create the feature branch**

```powershell
git checkout main
git pull
git checkout -b feat/enum-surface
```

- [ ] **Step 2: Verify clean baseline**

Run: `dotnet test --nologo`

Expected: all 216 tests pass (record the actual number for the final-task count check). If the count differs from 216, note the actual number — the final task verifies (baseline + ~50) tests pass post-feature.

If any test fails, stop and report — the baseline must be green before changes start.

- [ ] **Step 3: No commit** — branching only.

---

## Task 2: Add `AtlasMappingException`

**Files:**
- Create: `src/Atlas/AtlasMappingException.cs`
- Create: `tests/Atlas.Tests/AtlasMappingExceptionTests.cs`

The design doc references `AtlasMappingException` for enum runtime errors. The codebase doesn't have it — runtime errors today use `InvalidOperationException`. This task adds the new public exception type as a sibling of `AtlasConfigurationException`.

- [ ] **Step 1: Write failing tests**

Create `tests/Atlas.Tests/AtlasMappingExceptionTests.cs`:

```csharp
namespace Atlas.Tests;

public class AtlasMappingExceptionTests
{
    [Fact]
    public void Constructor_WithMessage_PreservesMessage()
    {
        var ex = new AtlasMappingException("source value is undefined");
        Assert.Equal("source value is undefined", ex.Message);
    }

    [Fact]
    public void IsAssignableTo_Exception_ForCatchHandling()
    {
        var ex = new AtlasMappingException("any message");
        Assert.IsAssignableFrom<Exception>(ex);
    }
}
```

- [ ] **Step 2: Run tests; verify they fail**

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~AtlasMappingException" --nologo`

Expected: 2 tests fail with compilation error "type or namespace name 'AtlasMappingException' could not be found".

- [ ] **Step 3: Create the exception class**

Create `src/Atlas/AtlasMappingException.cs`:

```csharp
namespace Atlas;

/// <summary>
/// Thrown by compiled mappings at runtime when an input value has no mapping —
/// e.g., a source enum value that's not defined on the destination enum and no
/// fallback was configured. Distinct from <see cref="AtlasConfigurationException"/>,
/// which surfaces config-time errors.
/// </summary>
public sealed class AtlasMappingException : Exception
{
    public AtlasMappingException(string message) : base(message) { }
}
```

- [ ] **Step 4: Run tests; verify they pass**

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~AtlasMappingException" --nologo`

Expected: 2 passed.

- [ ] **Step 5: Commit**

```powershell
git add src/Atlas/AtlasMappingException.cs tests/Atlas.Tests/AtlasMappingExceptionTests.cs
git commit -m "Add AtlasMappingException for runtime mapping errors (2 tests)"
```

---

## Task 3: `EnumMapConfig` + 10 unit tests

**Files:**
- Create: `src/Atlas/Internal/EnumMapConfig.cs`
- Create: `tests/Atlas.Tests/Internal/EnumMapConfigTests.cs`

This is the per-typemap configuration object that holds the strategy, per-value overrides, ignored source values, and fallback. Hung off `TypeMap.EnumConfig` in Task 4.

- [ ] **Step 1: Write failing tests**

Create `tests/Atlas.Tests/Internal/EnumMapConfigTests.cs`:

```csharp
using Atlas.Internal;

namespace Atlas.Tests.Internal;

public class EnumMapConfigTests
{
    public enum SrcEnum { A = 1, B = 2, C = 3 }
    public enum DstEnum { X = 10, Y = 20, Z = 30 }

    [Fact]
    public void SetStrategy_ByValue_FirstCall_Succeeds()
    {
        var cfg = new EnumMapConfig();
        cfg.SetStrategy(EnumMappingStrategy.ByValue, ignoreCase: false);
        Assert.Equal(EnumMappingStrategy.ByValue, cfg.Strategy);
        Assert.False(cfg.IgnoreCase);
        Assert.True(cfg.StrategyExplicitlySet);
    }

    [Fact]
    public void SetStrategy_ByName_AfterByValue_Throws_AtlasConfigurationException()
    {
        var cfg = new EnumMapConfig();
        cfg.SetStrategy(EnumMappingStrategy.ByValue, false);
        var ex = Assert.Throws<AtlasConfigurationException>(() =>
            cfg.SetStrategy(EnumMappingStrategy.ByName, true));
        Assert.Contains("already set", ex.Message);
    }

    [Fact]
    public void SetStrategy_ByValue_AfterByName_Throws()
    {
        var cfg = new EnumMapConfig();
        cfg.SetStrategy(EnumMappingStrategy.ByName, true);
        Assert.Throws<AtlasConfigurationException>(() =>
            cfg.SetStrategy(EnumMappingStrategy.ByValue, false));
    }

    [Fact]
    public void AddOverride_NewKey_Succeeds()
    {
        var cfg = new EnumMapConfig();
        cfg.AddOverride(SrcEnum.A, DstEnum.X);
        Assert.True(cfg.PerValueOverrides.ContainsKey(SrcEnum.A));
        Assert.Equal(DstEnum.X, cfg.PerValueOverrides[SrcEnum.A]);
    }

    [Fact]
    public void AddOverride_DuplicateKey_Throws()
    {
        var cfg = new EnumMapConfig();
        cfg.AddOverride(SrcEnum.A, DstEnum.X);
        var ex = Assert.Throws<AtlasConfigurationException>(() =>
            cfg.AddOverride(SrcEnum.A, DstEnum.Y));
        Assert.Contains("already configured", ex.Message);
    }

    [Fact]
    public void AddOverride_KeyAlreadyIgnored_Throws()
    {
        var cfg = new EnumMapConfig();
        cfg.AddIgnore(SrcEnum.B);
        var ex = Assert.Throws<AtlasConfigurationException>(() =>
            cfg.AddOverride(SrcEnum.B, DstEnum.X));
        Assert.Contains("Ignore", ex.Message);
    }

    [Fact]
    public void AddIgnore_NewValue_Succeeds()
    {
        var cfg = new EnumMapConfig();
        cfg.AddIgnore(SrcEnum.C);
        Assert.Contains((object)SrcEnum.C, cfg.IgnoredSourceValues);
    }

    [Fact]
    public void AddIgnore_ValueAlreadyOverridden_Throws()
    {
        var cfg = new EnumMapConfig();
        cfg.AddOverride(SrcEnum.A, DstEnum.X);
        var ex = Assert.Throws<AtlasConfigurationException>(() =>
            cfg.AddIgnore(SrcEnum.A));
        Assert.Contains("MapValue", ex.Message);
    }

    [Fact]
    public void SetFallback_FirstCall_Succeeds()
    {
        var cfg = new EnumMapConfig();
        cfg.SetFallback(DstEnum.Z);
        Assert.True(cfg.HasFallback);
        Assert.Equal(DstEnum.Z, cfg.FallbackValue);
    }

    [Fact]
    public void SetFallback_SecondCall_Throws()
    {
        var cfg = new EnumMapConfig();
        cfg.SetFallback(DstEnum.X);
        var ex = Assert.Throws<AtlasConfigurationException>(() =>
            cfg.SetFallback(DstEnum.Y));
        Assert.Contains("already set", ex.Message);
    }
}
```

- [ ] **Step 2: Run tests; verify they fail**

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~EnumMapConfigTests" --nologo`

Expected: 10 tests fail with compilation errors ("EnumMapConfig", "EnumMappingStrategy" not found).

Note: `AtlasConfigurationException` requires an `IReadOnlyList<ConfigurationError>` in its current ctor (see `AtlasConfigurationException.cs` line 21). The tests above pass a string and expect the exception to "contain" that text. The implementation will need to construct a 1-element error list with the message. To keep `AtlasConfigurationException` API stable, the implementation will use a single `ConfigurationError(typeof(void), typeof(void), "(enum-config)", message)` and the tests' `Assert.Contains` walks the formatted message via `ex.Message`.

- [ ] **Step 3: Implement `EnumMapConfig`**

Create `src/Atlas/Internal/EnumMapConfig.cs`:

```csharp
namespace Atlas.Internal;

internal enum EnumMappingStrategy { ByValue, ByName }

internal sealed class EnumMapConfig
{
    public EnumMappingStrategy Strategy { get; private set; } = EnumMappingStrategy.ByValue;
    public bool IgnoreCase { get; private set; }
    public bool StrategyExplicitlySet { get; private set; }

    public Dictionary<object, object> PerValueOverrides { get; } = new();
    public HashSet<object> IgnoredSourceValues { get; } = new();

    public bool HasFallback { get; private set; }
    public object? FallbackValue { get; private set; }

    public void SetStrategy(EnumMappingStrategy strategy, bool ignoreCase)
    {
        if (StrategyExplicitlySet)
            ThrowConfig("Enum strategy already set; only one of MapByValue() / MapByName() allowed per map.");
        Strategy = strategy;
        IgnoreCase = ignoreCase;
        StrategyExplicitlySet = true;
    }

    public void AddOverride(object src, object dst)
    {
        if (IgnoredSourceValues.Contains(src))
            ThrowConfig($"Source value '{src}' is already marked Ignore(); cannot also MapValue().");
        if (!PerValueOverrides.TryAdd(src, dst))
            ThrowConfig($"MapValue for source value '{src}' is already configured.");
    }

    public void AddIgnore(object src)
    {
        if (PerValueOverrides.ContainsKey(src))
            ThrowConfig($"Source value '{src}' already has MapValue(); cannot also Ignore().");
        IgnoredSourceValues.Add(src);
    }

    public void SetFallback(object dst)
    {
        if (HasFallback)
            ThrowConfig("WithFallback() already set; only one fallback allowed per map.");
        HasFallback = true;
        FallbackValue = dst;
    }

    private static void ThrowConfig(string message) =>
        throw new AtlasConfigurationException(
            new[] { new ConfigurationError(typeof(void), typeof(void), "(enum-config)", message) });
}
```

- [ ] **Step 4: Run tests; verify they pass**

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~EnumMapConfigTests" --nologo`

Expected: 10 passed.

- [ ] **Step 5: Commit**

```powershell
git add src/Atlas/Internal/EnumMapConfig.cs tests/Atlas.Tests/Internal/EnumMapConfigTests.cs
git commit -m "Add EnumMapConfig + EnumMappingStrategy (10 unit tests)"
```

---

## Task 4: Plumbing — `TypeMap.EnumConfig` field + `MapperConfigurationExpression.EnableEnumMappingValidation`

**Files:**
- Modify: `src/Atlas/Internal/TypeMap.cs` (add `EnumConfig` field)
- Modify: `src/Atlas/MapperConfigurationExpression.cs` (add `EnableEnumMappingValidation()` + internal flag)
- Modify: `src/Atlas/MapperConfiguration.cs` (read flag and store on instance for the validator to consume)

No new tests — pure data plumbing. The flag is exercised in Task 9's strict-mode validation tests; the field is exercised in Task 6's compilation tests.

- [ ] **Step 1: Add `EnumConfig` to `TypeMap`**

Open `src/Atlas/Internal/TypeMap.cs`. After the existing `IncludedBases` property block (around line 28), add:

```csharp
    /// <summary>
    /// Per-typemap enum customization (strategy, per-value overrides, ignored source values,
    /// fallback). Null unless an enum-method has been called on the fluent surface; null also
    /// for non-enum typemaps. Compilation honours null as "use default ByValue strategy with
    /// no overrides" for typemaps where source/dest are both enums.
    /// </summary>
    public EnumMapConfig? EnumConfig { get; set; }
```

- [ ] **Step 2: Add `EnableEnumMappingValidation` to `MapperConfigurationExpression`**

Open `src/Atlas/MapperConfigurationExpression.cs`. After the existing `CaseSensitive` property (line 18), add:

```csharp
    internal bool EnumValidationEnabled { get; private set; }

    /// <summary>
    /// Enables strict source-side enum mapping validation. When enabled,
    /// <see cref="MapperConfiguration.AssertConfigurationIsValid"/> asserts that every defined
    /// source enum value in every registered enum→enum map is covered by MapValue, Ignore,
    /// the strategy, or WithFallback. Disabled by default.
    /// </summary>
    public void EnableEnumMappingValidation() => EnumValidationEnabled = true;
```

- [ ] **Step 3: Plumb the flag through `MapperConfiguration`**

Open `src/Atlas/MapperConfiguration.cs`. Modify the constructor:

Find this line (around line 14):
```csharp
public MapperConfiguration(MapperConfigurationExpression expression)
```

Inside that constructor, after the existing field assignments and before `_registry = new MapperRegistry(typeMaps);`, add:

```csharp
        _enumValidationEnabled = expression.EnumValidationEnabled;
```

At the top of the class (with the other private fields, around line 11), add:

```csharp
    private readonly bool _enumValidationEnabled;
```

Modify `AssertConfigurationIsValid()` (line 72) to pass the flag through:

```csharp
    public void AssertConfigurationIsValid() =>
        ConfigurationValidator.Validate(_registry, _enumValidationEnabled);
```

Note: `ConfigurationValidator.Validate` signature changes in Task 8. For now (Task 4), keep the existing single-arg `Validate(_registry)` call temporarily so the build stays green; Tasks 8-9 will change `ConfigurationValidator.Validate` to accept the flag and the call site here will be updated.

To stay green during Task 4, use this body in `AssertConfigurationIsValid` instead:

```csharp
    public void AssertConfigurationIsValid() => ConfigurationValidator.Validate(_registry);
```

(No change from existing v1.) The `_enumValidationEnabled` field is stored but unread until Task 8.

- [ ] **Step 4: Build to confirm everything compiles**

Run: `dotnet build --nologo`

Expected: build succeeds; no warnings about unused field (the field is private but assigned, which the compiler accepts).

- [ ] **Step 5: Run all tests; confirm no regressions**

Run: `dotnet test --nologo`

Expected: 218 tests pass (216 baseline + 2 from Task 2 + 10 from Task 3 = 228; if you started with a different baseline, adjust). The added field on `TypeMap` and the new method on `MapperConfigurationExpression` should not affect any existing behavior.

- [ ] **Step 6: Commit**

```powershell
git add src/Atlas/Internal/TypeMap.cs src/Atlas/MapperConfigurationExpression.cs src/Atlas/MapperConfiguration.cs
git commit -m "Plumb TypeMap.EnumConfig + EnableEnumMappingValidation flag (no new tests)"
```

---

## Task 5: Add 5 enum methods to `IMappingExpression` + `MappingExpression`

**Files:**
- Modify: `src/Atlas/Configuration/IMappingExpression.cs` (5 new methods)
- Modify: `src/Atlas/Configuration/MappingExpression.cs` (5 new implementations)
- Create: `tests/Atlas.Tests/EnumExplicitMapTypeGuardTests.cs` (5 tests)

The methods enforce enum-ness at config time. Calling them on non-enum types throws `InvalidOperationException` immediately (NOT `AtlasConfigurationException` — this is an API misuse, not a configuration validity issue).

- [ ] **Step 1: Write failing tests**

Create `tests/Atlas.Tests/EnumExplicitMapTypeGuardTests.cs`:

```csharp
namespace Atlas.Tests;

public class EnumExplicitMapTypeGuardTests
{
    public enum MyEnum { A, B }
    public class NotAnEnum { public int Value { get; set; } }

    [Fact]
    public void MapByValue_OnNonEnumSource_Throws_InvalidOperationException()
    {
        var cfg = new MapperConfigurationExpression();
        var map = cfg.CreateMap<NotAnEnum, MyEnum>();
        var ex = Assert.Throws<InvalidOperationException>(() => map.MapByValue());
        Assert.Contains("enum", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MapByName_OnNonEnumDest_Throws_InvalidOperationException()
    {
        var cfg = new MapperConfigurationExpression();
        var map = cfg.CreateMap<MyEnum, NotAnEnum>();
        var ex = Assert.Throws<InvalidOperationException>(() => map.MapByName());
        Assert.Contains("enum", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MapValue_OnNonEnumSource_Throws_InvalidOperationException()
    {
        var cfg = new MapperConfigurationExpression();
        var map = cfg.CreateMap<NotAnEnum, MyEnum>();
        var ex = Assert.Throws<InvalidOperationException>(() => map.MapValue(new NotAnEnum(), MyEnum.A));
        Assert.Contains("enum", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ignore_TSourceOverload_OnNonEnumSource_Throws_InvalidOperationException()
    {
        var cfg = new MapperConfigurationExpression();
        var map = cfg.CreateMap<NotAnEnum, MyEnum>();
        var ex = Assert.Throws<InvalidOperationException>(() => map.Ignore(new NotAnEnum()));
        Assert.Contains("enum", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WithFallback_OnNonEnumDest_Throws_InvalidOperationException()
    {
        var cfg = new MapperConfigurationExpression();
        var map = cfg.CreateMap<MyEnum, NotAnEnum>();
        var ex = Assert.Throws<InvalidOperationException>(() => map.WithFallback(new NotAnEnum()));
        Assert.Contains("enum", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 2: Run tests; verify they fail**

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~EnumExplicitMapTypeGuardTests" --nologo`

Expected: 5 tests fail with compilation errors ("MapByValue", "MapByName", "MapValue", "WithFallback" methods not found; the new `Ignore(TSource)` overload not found).

- [ ] **Step 3: Add the methods to the interface**

Open `src/Atlas/Configuration/IMappingExpression.cs`. After the `IncludeBase<,>` method (line 53), add:

```csharp

    // ---- Enum surface (callable only when both TSource and TDestination are enums; otherwise throws at config time) ----

    /// <summary>
    /// Forces by-value matching for this enum→enum map (matches by underlying integer).
    /// Default if neither MapByValue nor MapByName is called.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown at configuration time if <typeparamref name="TSource"/> or <typeparamref name="TDestination"/> is not an enum.
    /// </exception>
    IMappingExpression<TSource, TDestination> MapByValue();

    /// <summary>
    /// Forces by-name matching for this enum→enum map (matches by member name).
    /// </summary>
    /// <param name="ignoreCase">If true, name matching is case-insensitive. Defaults to false.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown at configuration time if <typeparamref name="TSource"/> or <typeparamref name="TDestination"/> is not an enum.
    /// </exception>
    IMappingExpression<TSource, TDestination> MapByName(bool ignoreCase = false);

    /// <summary>
    /// Maps a specific source enum value to a specific destination enum value.
    /// Takes precedence over the strategy default. Repeating the same source value throws AtlasConfigurationException.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown at configuration time if <typeparamref name="TSource"/> or <typeparamref name="TDestination"/> is not an enum.
    /// </exception>
    IMappingExpression<TSource, TDestination> MapValue(TSource sourceValue, TDestination destinationValue);

    /// <summary>
    /// Marks a source enum value as ignored. Mapping that value at runtime produces
    /// <c>default(TDestination)</c> rather than searching the strategy or fallback.
    /// </summary>
    /// <remarks>
    /// If <c>default(TDestination)</c> is not a defined value of <typeparamref name="TDestination"/>,
    /// <see cref="MapperConfiguration.AssertConfigurationIsValid"/> throws — Ignore would otherwise silently produce an undefined enum value.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown at configuration time if <typeparamref name="TSource"/> is not an enum.
    /// </exception>
    IMappingExpression<TSource, TDestination> Ignore(TSource sourceValue);

    /// <summary>
    /// Sets a fallback destination value used when no explicit MapValue, Ignore, or strategy match applies.
    /// Without a fallback, unmatched values throw <see cref="AtlasMappingException"/> at runtime.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown at configuration time if <typeparamref name="TDestination"/> is not an enum.
    /// </exception>
    IMappingExpression<TSource, TDestination> WithFallback(TDestination fallbackValue);
```

- [ ] **Step 4: Implement the methods on `MappingExpression`**

Open `src/Atlas/Configuration/MappingExpression.cs`. After the `IncludeBase<,>` method (around line 89), add:

```csharp

    // ---- Enum surface ----

    public IMappingExpression<TSource, TDestination> MapByValue()
    {
        TypeMap.EnsureMutable();
        EnsureBothEnums(nameof(MapByValue));
        EnsureEnumConfig().SetStrategy(EnumMappingStrategy.ByValue, ignoreCase: false);
        return this;
    }

    public IMappingExpression<TSource, TDestination> MapByName(bool ignoreCase = false)
    {
        TypeMap.EnsureMutable();
        EnsureBothEnums(nameof(MapByName));
        EnsureEnumConfig().SetStrategy(EnumMappingStrategy.ByName, ignoreCase);
        return this;
    }

    public IMappingExpression<TSource, TDestination> MapValue(TSource sourceValue, TDestination destinationValue)
    {
        TypeMap.EnsureMutable();
        EnsureBothEnums(nameof(MapValue));
        EnsureEnumConfig().AddOverride(sourceValue!, destinationValue!);
        return this;
    }

    public IMappingExpression<TSource, TDestination> Ignore(TSource sourceValue)
    {
        TypeMap.EnsureMutable();
        EnsureSourceEnum(nameof(Ignore));
        EnsureEnumConfig().AddIgnore(sourceValue!);
        return this;
    }

    public IMappingExpression<TSource, TDestination> WithFallback(TDestination fallbackValue)
    {
        TypeMap.EnsureMutable();
        EnsureDestEnum(nameof(WithFallback));
        EnsureEnumConfig().SetFallback(fallbackValue!);
        return this;
    }

    private EnumMapConfig EnsureEnumConfig() => TypeMap.EnumConfig ??= new EnumMapConfig();

    private static void EnsureBothEnums(string methodName)
    {
        if (!typeof(TSource).IsEnum || !typeof(TDestination).IsEnum)
            throw new InvalidOperationException(
                $"{methodName}() requires both TSource ({typeof(TSource).Name}) and TDestination ({typeof(TDestination).Name}) to be enum types.");
    }

    private static void EnsureSourceEnum(string methodName)
    {
        if (!typeof(TSource).IsEnum)
            throw new InvalidOperationException(
                $"{methodName}(TSource) requires TSource ({typeof(TSource).Name}) to be an enum type.");
    }

    private static void EnsureDestEnum(string methodName)
    {
        if (!typeof(TDestination).IsEnum)
            throw new InvalidOperationException(
                $"{methodName}(TDestination) requires TDestination ({typeof(TDestination).Name}) to be an enum type.");
    }
```

**Note on `Ignore(TSource)` overload:** The existing `Ignore(Expression<Func<TDestination, object>>)` lives on `IMemberConfigurationExpression`, NOT on `IMappingExpression`. Verify this in the existing files — if there's no name collision on `IMappingExpression`, no overload disambiguation is needed. (Confirmed by reading `IMappingExpression.cs` — no existing `Ignore` method on the interface.)

- [ ] **Step 5: Run tests; verify they pass**

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~EnumExplicitMapTypeGuardTests" --nologo`

Expected: 5 passed.

- [ ] **Step 6: Run all tests; confirm no regressions**

Run: `dotnet test --nologo`

Expected: baseline + 17 (2 from T2 + 10 from T3 + 5 from T5) = 233 if baseline was 216.

- [ ] **Step 7: Commit**

```powershell
git add src/Atlas/Configuration/IMappingExpression.cs src/Atlas/Configuration/MappingExpression.cs tests/Atlas.Tests/EnumExplicitMapTypeGuardTests.cs
git commit -m "Add 5 enum methods to IMappingExpression with type guards (5 tests)"
```

---

## Task 6: `EnumResolver` + `BuildEnumLambda` + ExecutionPlanBuilder dispatch + 12 ExplicitMap tests

**Files:**
- Create: `src/Atlas/Internal/EnumResolver.cs` (the §6 algorithm; shared by builder + validator)
- Modify: `src/Atlas/Internal/ExecutionPlanBuilder.cs` (add enum prologue + `BuildEnumLambda`)
- Create: `tests/Atlas.Tests/EnumExplicitMapTests.cs` (12 tests, §8.3)

This is the heart of the feature — runtime enum-map dispatch via compiled switch expressions. Per memory `feedback_pseudocode_concrete_trace`: trace through a concrete example before signing off (the §6.2 worked trace in the spec).

- [ ] **Step 1: Write failing tests**

Create `tests/Atlas.Tests/EnumExplicitMapTests.cs`:

```csharp
namespace Atlas.Tests;

public class EnumExplicitMapTests
{
    public enum SrcByValue { A = 1, B = 2, C = 3 }
    public enum DstByValueAll { Alpha = 1, Beta = 2, Gamma = 3 }
    public enum DstByValuePartial { Alpha = 1, Beta = 2 }   // missing 3 → C has no match

    public enum SrcByName { Pending, Active, Inactive }
    public enum DstByName { Pending, Active, Inactive }
    public enum DstByNameNoInactive { Pending, Active }

    public enum SrcSnake { lower_pending, lower_active }
    public enum DstPascal { Pending, Active }   // ByName ci needs case-insensitive even after lowercased compare

    // ---- ByValue ----

    [Fact]
    public void CreateMap_NoEnumMethods_DefaultsToByValue_AllValuesDefinedOnDest_Maps()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<SrcByValue, DstByValueAll>());
        var mapper = cfg.CreateMapper();
        Assert.Equal(DstByValueAll.Alpha, mapper.Map<SrcByValue, DstByValueAll>(SrcByValue.A));
        Assert.Equal(DstByValueAll.Beta, mapper.Map<SrcByValue, DstByValueAll>(SrcByValue.B));
        Assert.Equal(DstByValueAll.Gamma, mapper.Map<SrcByValue, DstByValueAll>(SrcByValue.C));
    }

    [Fact]
    public void ByValue_WithMapValue_OverrideWinsOverStrategy()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<SrcByValue, DstByValueAll>()
                .MapByValue()
                .MapValue(SrcByValue.A, DstByValueAll.Gamma));   // override: A → Gamma not Alpha
        var mapper = cfg.CreateMapper();
        Assert.Equal(DstByValueAll.Gamma, mapper.Map<SrcByValue, DstByValueAll>(SrcByValue.A));
        Assert.Equal(DstByValueAll.Beta, mapper.Map<SrcByValue, DstByValueAll>(SrcByValue.B)); // strategy still applies
    }

    [Fact]
    public void ByValue_WithIgnore_ProducesDefaultDestValue()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<SrcByValue, DstByValueAll>()
                .Ignore(SrcByValue.B));
        var mapper = cfg.CreateMapper();
        Assert.Equal(default(DstByValueAll), mapper.Map<SrcByValue, DstByValueAll>(SrcByValue.B));
    }

    [Fact]
    public void ByValue_WithFallback_UnmatchedSourceUsesFallback()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<SrcByValue, DstByValuePartial>()
                .WithFallback(DstByValuePartial.Alpha));
        var mapper = cfg.CreateMapper();
        Assert.Equal(DstByValuePartial.Alpha, mapper.Map<SrcByValue, DstByValuePartial>(SrcByValue.C));
    }

    [Fact]
    public void ByValue_NoFallback_SourceValueNotDefinedOnDest_ThrowsAtlasMappingException()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<SrcByValue, DstByValuePartial>());
        var mapper = cfg.CreateMapper();
        var ex = Assert.Throws<AtlasMappingException>(() =>
            mapper.Map<SrcByValue, DstByValuePartial>(SrcByValue.C));
        Assert.Contains("C", ex.Message);
    }

    // ---- ByName ----

    [Fact]
    public void ByName_DefaultCaseSensitive_SameNameSameCase_Maps()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<SrcByName, DstByName>().MapByName());
        var mapper = cfg.CreateMapper();
        Assert.Equal(DstByName.Pending, mapper.Map<SrcByName, DstByName>(SrcByName.Pending));
        Assert.Equal(DstByName.Active, mapper.Map<SrcByName, DstByName>(SrcByName.Active));
    }

    [Fact]
    public void ByName_DefaultCaseSensitive_DifferentCase_ThrowsAtlasMappingException()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<SrcSnake, DstPascal>().MapByName());   // case-sensitive default; "lower_pending" != "Pending"
        var mapper = cfg.CreateMapper();
        Assert.Throws<AtlasMappingException>(() => mapper.Map<SrcSnake, DstPascal>(SrcSnake.lower_pending));
    }

    [Fact]
    public void ByName_IgnoreCaseTrue_DifferentCase_Maps()
    {
        // Need an enum pair where names match modulo case.
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<SrcSnake, DstPascal>().MapByName(ignoreCase: true)
                // Manual mapping needed since lower_pending != Pending even ci.
                // Use MapValue to demonstrate the override path; demonstrate ci matching via the next test pair.
                .MapValue(SrcSnake.lower_pending, DstPascal.Pending)
                .MapValue(SrcSnake.lower_active, DstPascal.Active));
        var mapper = cfg.CreateMapper();
        Assert.Equal(DstPascal.Pending, mapper.Map<SrcSnake, DstPascal>(SrcSnake.lower_pending));

        // Pure ci case: same words, different case, no MapValue.
        var cfg2 = new MapperConfiguration(c =>
            c.CreateMap<DstPascal, SrcByName>().MapByName(ignoreCase: true));
        var mapper2 = cfg2.CreateMapper();
        Assert.Equal(SrcByName.Pending, mapper2.Map<DstPascal, SrcByName>(DstPascal.Pending));
    }

    [Fact]
    public void ByName_WithMapValue_OverrideWinsOverNameMatch()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<SrcByName, DstByName>().MapByName()
                .MapValue(SrcByName.Active, DstByName.Inactive));
        var mapper = cfg.CreateMapper();
        Assert.Equal(DstByName.Inactive, mapper.Map<SrcByName, DstByName>(SrcByName.Active));
    }

    [Fact]
    public void ByName_NoFallback_NoNameMatch_ThrowsAtlasMappingException()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<SrcByName, DstByNameNoInactive>().MapByName());
        var mapper = cfg.CreateMapper();
        Assert.Throws<AtlasMappingException>(() => mapper.Map<SrcByName, DstByNameNoInactive>(SrcByName.Inactive));
    }

    // ---- Precedence ----

    [Fact]
    public void Precedence_MapValue_Beats_Ignore_Beats_Strategy_Beats_Fallback()
    {
        // Per §6.1: PerValueOverride (1) > Ignore (2) > Strategy (3) > Fallback (4) > Throw (5)
        // SrcByValue.B is overridden, SrcByValue.C is ignored, SrcByValue.A uses strategy ByValue=Alpha.
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<SrcByValue, DstByValueAll>()
                .MapByValue()
                .MapValue(SrcByValue.B, DstByValueAll.Gamma)
                .Ignore(SrcByValue.C)
                .WithFallback(DstByValueAll.Alpha));   // unused — every source value resolves earlier
        var mapper = cfg.CreateMapper();
        Assert.Equal(DstByValueAll.Alpha, mapper.Map<SrcByValue, DstByValueAll>(SrcByValue.A)); // strategy
        Assert.Equal(DstByValueAll.Gamma, mapper.Map<SrcByValue, DstByValueAll>(SrcByValue.B)); // override beats strategy
        Assert.Equal(default(DstByValueAll), mapper.Map<SrcByValue, DstByValueAll>(SrcByValue.C)); // ignore beats strategy
    }

    [Fact]
    public void UndefinedSourceValueCastFromInt_ThrowsAtlasMappingException_RegardlessOfFallback()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<SrcByValue, DstByValueAll>()
                .WithFallback(DstByValueAll.Alpha));
        var mapper = cfg.CreateMapper();
        var corruptValue = (SrcByValue)99;   // not in defined values {1, 2, 3}
        var ex = Assert.Throws<AtlasMappingException>(() =>
            mapper.Map<SrcByValue, DstByValueAll>(corruptValue));
        Assert.Contains("not defined", ex.Message);
    }
}
```

- [ ] **Step 2: Run tests; verify they fail**

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~EnumExplicitMapTests" --nologo`

Expected: 12 tests fail. Most fail because `mapper.Map<SrcByValue, DstByValueAll>(...)` throws `InvalidCastException` or returns wrong values — current ExecutionPlanBuilder treats both enum types as "complex objects" and attempts POCO mapping.

- [ ] **Step 3: Create `EnumResolver`**

Create `src/Atlas/Internal/EnumResolver.cs`:

```csharp
namespace Atlas.Internal;

/// <summary>
/// Per-value resolution for enum→enum mapping (§6.1). Pure: takes a source enum value plus
/// the typemap's <see cref="EnumMapConfig"/> and returns a (Hit, dstValue) or (Throw, reason).
/// Single source of truth — used by both <c>ExecutionPlanBuilder.BuildEnumLambda</c> and
/// <c>ConfigurationValidator</c>.
/// </summary>
internal static class EnumResolver
{
    public enum ActionKind { Hit, Throw }

    public readonly record struct ResolveAction(ActionKind Kind, object? DestValue, string? Reason);

    public static ResolveAction Resolve(object src, EnumMapConfig cfg, Type srcType, Type dstType)
    {
        // 1. Explicit override
        if (cfg.PerValueOverrides.TryGetValue(src, out var overridden))
            return new ResolveAction(ActionKind.Hit, overridden, null);

        // 2. Explicit ignore → default(dstType)
        if (cfg.IgnoredSourceValues.Contains(src))
            return new ResolveAction(ActionKind.Hit, GetDefault(dstType), null);

        // 3. Strategy
        var strategyHit = cfg.Strategy switch
        {
            EnumMappingStrategy.ByValue => ResolveByValue(src, srcType, dstType),
            EnumMappingStrategy.ByName  => ResolveByName(src, srcType, dstType, cfg.IgnoreCase),
            _ => null
        };
        if (strategyHit is not null)
            return new ResolveAction(ActionKind.Hit, strategyHit, null);

        // 4. Fallback
        if (cfg.HasFallback)
            return new ResolveAction(ActionKind.Hit, cfg.FallbackValue, null);

        // 5. No match
        return new ResolveAction(
            ActionKind.Throw,
            null,
            $"No mapping defined for {srcType.Name}.{src} -> {dstType.Name}.");
    }

    private static object? ResolveByValue(object src, Type srcType, Type dstType)
    {
        var srcUnderlying = Convert.ChangeType(src, Enum.GetUnderlyingType(srcType));
        foreach (var dstVal in Enum.GetValues(dstType))
        {
            var dstUnderlying = Convert.ChangeType(dstVal, Enum.GetUnderlyingType(dstType));
            // Compare as long for portability across underlying types (with a ulong edge case
            // we accept for v1 — see spec R2). Conversion through long handles byte/short/int.
            if (Convert.ToInt64(srcUnderlying) == Convert.ToInt64(dstUnderlying))
                return dstVal;
        }
        return null;
    }

    private static object? ResolveByName(object src, Type srcType, Type dstType, bool ignoreCase)
    {
        var srcName = Enum.GetName(srcType, src);
        if (srcName is null) return null;
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var dstVal in Enum.GetValues(dstType))
        {
            var dstName = Enum.GetName(dstType, dstVal);
            if (dstName is not null && string.Equals(srcName, dstName, comparison))
                return dstVal;
        }
        return null;
    }

    private static object GetDefault(Type t) => Activator.CreateInstance(t)!;
}
```

- [ ] **Step 4: Add enum dispatch + `BuildEnumLambda` to `ExecutionPlanBuilder`**

Open `src/Atlas/Internal/ExecutionPlanBuilder.cs`. Modify the `Build` method (line 12):

```csharp
    public static LambdaExpression Build(TypeMap typeMap, MapperRegistry registry)
    {
        // Enum dispatch — both source AND destination must be enums (sealed value types,
        // so this branch is mutually exclusive with inheritance dispatch below).
        if (typeMap.SourceType.IsEnum && typeMap.DestinationType.IsEnum)
            return BuildEnumLambda(typeMap);

        // Per-base body (existing v1 codegen).
        var baseLambda = BuildBaseBody(typeMap, registry);

        if (typeMap.IncludedDerived.Count == 0)
            return baseLambda;

        return BuildWithInheritanceDispatch(baseLambda, typeMap, registry);
    }
```

After the `Build` method (or anywhere convenient — placing it near the dispatch is clearest), add the new private method:

```csharp
    private static LambdaExpression BuildEnumLambda(TypeMap typeMap)
    {
        var cfg = typeMap.EnumConfig ?? new EnumMapConfig();
        var srcType = typeMap.SourceType;
        var dstType = typeMap.DestinationType;
        var srcParam = Expression.Parameter(srcType, "src");

        var cases = new List<SwitchCase>();
        foreach (var definedSrc in Enum.GetValues(srcType))
        {
            var action = EnumResolver.Resolve(definedSrc, cfg, srcType, dstType);
            Expression caseBody = action.Kind switch
            {
                EnumResolver.ActionKind.Hit =>
                    Expression.Constant(action.DestValue, dstType),
                EnumResolver.ActionKind.Throw =>
                    Expression.Throw(
                        Expression.New(
                            typeof(AtlasMappingException).GetConstructor(new[] { typeof(string) })!,
                            Expression.Constant(action.Reason)),
                        dstType),
                _ => throw new InvalidOperationException("Unreachable"),
            };
            cases.Add(Expression.SwitchCase(caseBody, Expression.Constant(definedSrc, srcType)));
        }

        // Default case: source value not in defined values of srcType (e.g., (SrcEnum)99).
        var defaultBody = Expression.Throw(
            Expression.New(
                typeof(AtlasMappingException).GetConstructor(new[] { typeof(string) })!,
                Expression.Constant($"Source value is not defined on {srcType.Name}.")),
            dstType);

        var switchExpr = Expression.Switch(srcParam, defaultBody, cases.ToArray());
        var funcType = typeof(Func<,>).MakeGenericType(srcType, dstType);
        return Expression.Lambda(funcType, switchExpr, srcParam);
    }
```

- [ ] **Step 5: Run tests; verify they pass**

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~EnumExplicitMapTests" --nologo`

Expected: 12 passed.

If `ByName_IgnoreCaseTrue_DifferentCase_Maps` fails on the second assertion, double-check `ResolveByName` uses `StringComparison.OrdinalIgnoreCase` when `ignoreCase` is true.

If `UndefinedSourceValueCastFromInt_ThrowsAtlasMappingException_RegardlessOfFallback` fails (no throw), verify the switch's default case is what runs for undefined values — `Expression.Switch` falls through to default when no case matches, and the cases iterate `Enum.GetValues` which excludes the int-cast 99.

- [ ] **Step 6: Run all tests; confirm no regressions**

Run: `dotnet test --nologo`

Expected: baseline + 29 (2 + 10 + 5 + 12) = 245 if baseline was 216. Existing inheritance tests should still pass (the dispatch order is enum-first, then inheritance, then base — disjoint).

- [ ] **Step 7: Commit**

```powershell
git add src/Atlas/Internal/EnumResolver.cs src/Atlas/Internal/ExecutionPlanBuilder.cs tests/Atlas.Tests/EnumExplicitMapTests.cs
git commit -m "Add EnumResolver + BuildEnumLambda + ExecutionPlanBuilder dispatch (12 tests)"
```

---

## Task 7: Auto-conversion via `EnumConversions` + `StringToEnumCache` (10 tests)

**Files:**
- Create: `src/Atlas/Internal/EnumConversions.cs`
- Create: `src/Atlas/Internal/StringToEnumCache.cs`
- Modify: `src/Atlas/Internal/ConventionEngine.cs` (extend `IsCompatible`)
- Modify: `src/Atlas/Internal/ExecutionPlanBuilder.cs` (extend `ConvertOrMap` to recognize enum conversions)
- Modify: `src/Atlas/MapperConfiguration.cs` (instantiate `StringToEnumCache`, plumb through)
- Modify: `src/Atlas/Internal/MapperRegistry.cs` (carry the cache; OR the registry already exposes registry-scoped state — adapt)
- Create: `tests/Atlas.Tests/EnumAutoConversionTests.cs` (10 tests, §8.2)

This is the no-CreateMap path: enum-typed properties on object DTOs should auto-convert through Atlas's existing convention engine without requiring an explicit `CreateMap<E1, E2>()` registration.

- [ ] **Step 1: Write failing tests**

Create `tests/Atlas.Tests/EnumAutoConversionTests.cs`:

```csharp
namespace Atlas.Tests;

public class EnumAutoConversionTests
{
    public enum E1 { A = 1, B = 2, C = 3 }
    public enum E2 { A = 1, B = 2 }   // missing C
    public enum EByte : byte { A = 1, B = 2 }
    public enum EInt : int { A = 1, B = 2 }

    public class SrcWithE1 { public E1 Value { get; set; } }
    public class DstWithE2 { public E2 Value { get; set; } }
    public class DstWithString { public string? Value { get; set; } }
    public class SrcWithString { public string? Value { get; set; } }
    public class DstWithE1 { public E1 Value { get; set; } }
    public class DstWithInt { public int Value { get; set; } }
    public class SrcWithEByte { public EByte Value { get; set; } }
    public class DstWithEInt { public EInt Value { get; set; } }
    public class SrcWithNullableE1 { public E1? Value { get; set; } }
    public class DstWithNullableE1 { public E1? Value { get; set; } }

    [Fact]
    public void EnumToEnum_SameUnderlyingType_AllValuesDefinedOnDest_Maps()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<SrcWithE1, DstWithE2>());
        var mapper = cfg.CreateMapper();
        var dst = mapper.Map<SrcWithE1, DstWithE2>(new SrcWithE1 { Value = E1.A });
        Assert.Equal(E2.A, dst.Value);
    }

    [Fact]
    public void EnumToEnum_SourceValueNotDefinedOnDest_ThrowsAtlasMappingException_AtRuntime()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<SrcWithE1, DstWithE2>());
        var mapper = cfg.CreateMapper();
        Assert.Throws<AtlasMappingException>(() =>
            mapper.Map<SrcWithE1, DstWithE2>(new SrcWithE1 { Value = E1.C }));
    }

    [Fact]
    public void EnumToEnum_DifferentUnderlyingTypes_ByteToInt_Maps()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<SrcWithEByte, DstWithEInt>());
        var mapper = cfg.CreateMapper();
        var dst = mapper.Map<SrcWithEByte, DstWithEInt>(new SrcWithEByte { Value = EByte.A });
        Assert.Equal(EInt.A, dst.Value);
    }

    [Fact]
    public void EnumToString_DefinedValue_ReturnsVerbatimMemberName()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<SrcWithE1, DstWithString>());
        var mapper = cfg.CreateMapper();
        var dst = mapper.Map<SrcWithE1, DstWithString>(new SrcWithE1 { Value = E1.A });
        Assert.Equal("A", dst.Value);
    }

    [Fact]
    public void EnumToString_UndefinedValueCastFromInt_ReturnsNull()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<SrcWithE1, DstWithString>());
        var mapper = cfg.CreateMapper();
        var dst = mapper.Map<SrcWithE1, DstWithString>(new SrcWithE1 { Value = (E1)99 });
        Assert.Null(dst.Value);
    }

    [Fact]
    public void StringToEnum_ExactCaseMatch_Maps()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<SrcWithString, DstWithE1>());
        var mapper = cfg.CreateMapper();
        var dst = mapper.Map<SrcWithString, DstWithE1>(new SrcWithString { Value = "A" });
        Assert.Equal(E1.A, dst.Value);
    }

    [Fact]
    public void StringToEnum_CaseMismatch_ThrowsAtlasMappingException()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<SrcWithString, DstWithE1>());
        var mapper = cfg.CreateMapper();
        Assert.Throws<AtlasMappingException>(() =>
            mapper.Map<SrcWithString, DstWithE1>(new SrcWithString { Value = "a" }));
    }

    [Fact]
    public void StringToEnum_UnrecognizedString_ThrowsAtlasMappingException()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<SrcWithString, DstWithE1>());
        var mapper = cfg.CreateMapper();
        Assert.Throws<AtlasMappingException>(() =>
            mapper.Map<SrcWithString, DstWithE1>(new SrcWithString { Value = "Z" }));
    }

    [Fact]
    public void EnumToUnderlyingNumeric_ReturnsUnderlyingInt()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<SrcWithE1, DstWithInt>());
        var mapper = cfg.CreateMapper();
        var dst = mapper.Map<SrcWithE1, DstWithInt>(new SrcWithE1 { Value = E1.B });
        Assert.Equal(2, dst.Value);
    }

    [Fact]
    public void NullableEnum_NullSource_NullableDest_PreservesNull()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<SrcWithNullableE1, DstWithNullableE1>());
        var mapper = cfg.CreateMapper();
        var dst = mapper.Map<SrcWithNullableE1, DstWithNullableE1>(new SrcWithNullableE1 { Value = null });
        Assert.Null(dst.Value);
    }
}
```

- [ ] **Step 2: Run tests; verify they fail**

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~EnumAutoConversionTests" --nologo`

Expected: most tests fail at config validation or runtime — `ConventionEngine.IsCompatible` rejects enum↔string and enum↔enum (different underlying types) currently, OR the property mapping path does an `Expression.Convert` that fails at runtime.

- [ ] **Step 3: Create `StringToEnumCache`**

Create `src/Atlas/Internal/StringToEnumCache.cs`:

```csharp
namespace Atlas.Internal;

/// <summary>
/// Per-MapperConfiguration cache of <c>(dstEnumType) → Dictionary&lt;string, dstEnumValue&gt;</c>
/// for the auto-conversion <c>string → enum</c> path. Built on demand.
/// </summary>
internal sealed class StringToEnumCache
{
    private readonly Dictionary<Type, Dictionary<string, object>> _maps = new();
    private readonly System.Threading.Lock _lock = new();

    public Dictionary<string, object> GetOrCreateForType(Type dstEnumType)
    {
        lock (_lock)
        {
            if (_maps.TryGetValue(dstEnumType, out var existing)) return existing;
            var built = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var v in Enum.GetValues(dstEnumType))
            {
                var name = Enum.GetName(dstEnumType, v);
                if (name is not null) built[name] = v;
            }
            _maps[dstEnumType] = built;
            return built;
        }
    }
}
```

- [ ] **Step 4: Create `EnumConversions`**

Create `src/Atlas/Internal/EnumConversions.cs`:

```csharp
using System.Linq.Expressions;

namespace Atlas.Internal;

/// <summary>
/// Property-level enum conversion layer (§7.3 spec). Used by <c>ConventionEngine</c> for
/// compatibility checks and by <c>ExecutionPlanBuilder.ConvertOrMap</c> to emit the
/// expression for a single-property conversion. Does NOT handle registered
/// <c>CreateMap&lt;E1, E2&gt;()</c> typemaps — those go through <c>ExecutionPlanBuilder.Build</c>.
/// </summary>
internal static class EnumConversions
{
    public static bool HasImplicitConversion(Type srcType, Type dstType)
    {
        var srcCore = Nullable.GetUnderlyingType(srcType) ?? srcType;
        var dstCore = Nullable.GetUnderlyingType(dstType) ?? dstType;

        if (srcCore.IsEnum && dstCore.IsEnum) return true;
        if (srcCore.IsEnum && dstCore == typeof(string)) return true;
        if (srcCore == typeof(string) && dstCore.IsEnum) return true;
        if (srcCore.IsEnum && dstCore == Enum.GetUnderlyingType(srcCore)) return true;
        if (dstCore.IsEnum && srcCore == Enum.GetUnderlyingType(dstCore)) return true;
        return false;
    }

    public static Expression BuildConversion(
        Expression srcExpr,
        Type dstType,
        StringToEnumCache cache)
    {
        var srcType = srcExpr.Type;
        var srcCore = Nullable.GetUnderlyingType(srcType) ?? srcType;
        var dstCore = Nullable.GetUnderlyingType(dstType) ?? dstType;

        // Both enums (possibly with nullable wrapping) — build a switch like BuildEnumLambda's body.
        if (srcCore.IsEnum && dstCore.IsEnum)
            return BuildEnumToEnum(srcExpr, srcCore, dstType, dstCore);

        if (srcCore.IsEnum && dstCore == typeof(string))
            return BuildEnumToString(srcExpr, srcCore);

        if (srcCore == typeof(string) && dstCore.IsEnum)
            return BuildStringToEnum(srcExpr, dstCore, cache);

        // Underlying-numeric conversions — straight cast.
        return Expression.Convert(srcExpr, dstType);
    }

    private static Expression BuildEnumToEnum(Expression srcExpr, Type srcEnum, Type dstFullType, Type dstEnum)
    {
        // Build a switch expression for all defined source values, default ByValue, no overrides.
        var cfg = new EnumMapConfig();   // defaults: ByValue, no overrides

        // Source might be Nullable<srcEnum> — peel it via .GetValueOrDefault() if needed.
        // For the common case (non-nullable), just use srcExpr.
        // Returns a value of type dstEnum (NOT Nullable<dstEnum>); caller handles wrapping if dstFullType is nullable.

        var srcParam = Expression.Parameter(srcEnum, "_src");
        var cases = new List<SwitchCase>();
        foreach (var definedSrc in Enum.GetValues(srcEnum))
        {
            var action = EnumResolver.Resolve(definedSrc, cfg, srcEnum, dstEnum);
            Expression caseBody = action.Kind switch
            {
                EnumResolver.ActionKind.Hit =>
                    Expression.Constant(action.DestValue, dstEnum),
                EnumResolver.ActionKind.Throw =>
                    Expression.Throw(
                        Expression.New(
                            typeof(AtlasMappingException).GetConstructor(new[] { typeof(string) })!,
                            Expression.Constant(action.Reason)),
                        dstEnum),
                _ => throw new InvalidOperationException("Unreachable"),
            };
            cases.Add(Expression.SwitchCase(caseBody, Expression.Constant(definedSrc, srcEnum)));
        }
        var defaultBody = Expression.Throw(
            Expression.New(
                typeof(AtlasMappingException).GetConstructor(new[] { typeof(string) })!,
                Expression.Constant($"Source value is not defined on {srcEnum.Name}.")),
            dstEnum);

        var switchExpr = Expression.Switch(srcParam, defaultBody, cases.ToArray());
        var lambda = Expression.Lambda(switchExpr, srcParam);

        // Inline the lambda by invoking it with srcExpr (after peeling nullable).
        var srcCore = Nullable.GetUnderlyingType(srcExpr.Type) is not null
            ? Expression.Property(srcExpr, "Value")   // crash on null — callers should handle nullable upstream
            : srcExpr;
        var invoked = Expression.Invoke(lambda, srcCore);

        // Wrap result in Nullable<dstEnum> if needed.
        if (Nullable.GetUnderlyingType(dstFullType) is not null)
            return Expression.Convert(invoked, dstFullType);
        return invoked;
    }

    private static Expression BuildEnumToString(Expression srcExpr, Type srcEnum)
    {
        // Enum.GetName(srcEnumType, srcValue) returns null for undefined casts.
        var srcCore = Nullable.GetUnderlyingType(srcExpr.Type) is not null
            ? Expression.Property(srcExpr, "Value")
            : srcExpr;
        var getName = typeof(Enum).GetMethod(nameof(Enum.GetName), new[] { typeof(Type), typeof(object) })!;
        return Expression.Call(getName, Expression.Constant(srcEnum), Expression.Convert(srcCore, typeof(object)));
    }

    private static Expression BuildStringToEnum(Expression srcExpr, Type dstEnum, StringToEnumCache cache)
    {
        var dict = cache.GetOrCreateForType(dstEnum);
        var dictConst = Expression.Constant(dict, typeof(Dictionary<string, object>));

        // dict.TryGetValue(srcExpr, out var v) ? (dstEnum)v : throw new AtlasMappingException(...)
        var tryGet = typeof(Dictionary<string, object>).GetMethod(
            nameof(Dictionary<string, object>.TryGetValue),
            new[] { typeof(string), typeof(object).MakeByRefType() })!;
        var outVar = Expression.Variable(typeof(object), "v");
        var block = Expression.Block(
            new[] { outVar },
            Expression.Condition(
                Expression.Call(dictConst, tryGet, srcExpr, outVar),
                Expression.Convert(outVar, dstEnum),
                Expression.Throw(
                    Expression.New(
                        typeof(AtlasMappingException).GetConstructor(new[] { typeof(string) })!,
                        Expression.Call(
                            typeof(string).GetMethod(nameof(string.Concat), new[] { typeof(string), typeof(string), typeof(string) })!,
                            Expression.Constant("String value '"),
                            srcExpr,
                            Expression.Constant($"' does not match any defined name of {dstEnum.Name}."))),
                    dstEnum)));
        return block;
    }
}
```

- [ ] **Step 5: Plumb `StringToEnumCache` through `MapperConfiguration`**

Open `src/Atlas/MapperConfiguration.cs`. After the existing `_enumValidationEnabled` field (added in Task 4), add:

```csharp
    private readonly StringToEnumCache _stringToEnumCache = new();
    internal StringToEnumCache Internal_StringToEnumCache => _stringToEnumCache;
```

Then make the cache available to `ExecutionPlanBuilder.Build`. The simplest plumbing: `MapperRegistry` already gets passed to `Build`; have the registry hold a reference to the cache.

Open `src/Atlas/Internal/MapperRegistry.cs` and add a property + ctor parameter:

```csharp
    public StringToEnumCache StringToEnumCache { get; }
```

Locate the existing `MapperRegistry` constructor. Add the cache parameter (with a default for backward compat in tests that construct the registry directly):

```csharp
    public MapperRegistry(IReadOnlyList<TypeMap> typeMaps, StringToEnumCache? stringToEnumCache = null)
    {
        // ... existing assignments ...
        StringToEnumCache = stringToEnumCache ?? new StringToEnumCache();
    }
```

Update the call in `MapperConfiguration` (line 43):

```csharp
        _registry = new MapperRegistry(typeMaps, _stringToEnumCache);
```

- [ ] **Step 6: Wire `EnumConversions` into `ConventionEngine` and `ExecutionPlanBuilder.ConvertOrMap`**

Open `src/Atlas/Internal/ConventionEngine.cs`. Modify `IsCompatible` (line 148):

```csharp
    private static bool IsCompatible(Type src, Type dst, Func<Type, Type, bool>? hasRegisteredMap)
    {
        if (dst.IsAssignableFrom(src)) return true;
        if (NumericConversions.HasImplicitConversion(src, dst)) return true;
        if (EnumConversions.HasImplicitConversion(src, dst)) return true;   // NEW
        if (IsEnumerable(src) && IsEnumerable(dst)) return true;
        if (IsComplex(src) && IsComplex(dst)) return true;
        if (hasRegisteredMap is not null && hasRegisteredMap(src, dst)) return true;
        return false;
    }
```

Open `src/Atlas/Internal/ExecutionPlanBuilder.cs`. Modify `ConvertOrMap` (line 274):

```csharp
    private static Expression ConvertOrMap(Expression source, Type targetType, MapperRegistry registry)
    {
        if (source.Type == targetType) return source;
        if (targetType.IsAssignableFrom(source.Type)) return Expression.Convert(source, targetType);

        if (NumericConversions.HasImplicitConversion(source.Type, targetType))
            return Expression.Convert(source, targetType);

        // Enum auto-conversion (NEW): only if no registered typemap covers the pair.
        if (EnumConversions.HasImplicitConversion(source.Type, targetType)
            && registry.GetTypeMap(new TypePair(source.Type, targetType)) is null)
        {
            return EnumConversions.BuildConversion(source, targetType, registry.StringToEnumCache);
        }

        if (IsCollection(source.Type) && IsCollection(targetType))
            return BuildCollectionInvoke(source, targetType, registry);

        // Fallback: nested map invocation
        return BuildNestedInvoke(source, targetType, registry);
    }
```

The registered-map check is critical — without it, a registered `CreateMap<E1, E2>()` would be silently bypassed by auto-conversion (spec R1 — "Registered enum map vs auto-conversion precedence").

- [ ] **Step 7: Run tests; verify they pass**

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~EnumAutoConversionTests" --nologo`

Expected: 10 passed.

If `NullableEnum_NullSource_NullableDest_PreservesNull` fails with NullReferenceException, the existing v1 nullable wrapper isn't firing for enum properties. Per spec R6: confirm by examining the generated expression. The minimal fix: in `EnumConversions.BuildConversion` when `srcExpr.Type` is `Nullable<E>`, wrap the conversion in a null-check (return `default(dstFullType)` if source is null).

- [ ] **Step 8: Run all tests; confirm no regressions**

Run: `dotnet test --nologo`

Expected: baseline + 39 (2 + 10 + 5 + 12 + 10) = 255 if baseline was 216.

- [ ] **Step 9: Commit**

```powershell
git add src/Atlas/Internal/EnumConversions.cs src/Atlas/Internal/StringToEnumCache.cs src/Atlas/Internal/ConventionEngine.cs src/Atlas/Internal/ExecutionPlanBuilder.cs src/Atlas/Internal/MapperRegistry.cs src/Atlas/MapperConfiguration.cs tests/Atlas.Tests/EnumAutoConversionTests.cs
git commit -m "Add EnumConversions auto-conversion + StringToEnumCache (10 tests)"
```

---

## Task 8: Always-on enum invariants in `ConfigurationValidator` (5 tests)

**Files:**
- Modify: `src/Atlas/Internal/ConfigurationValidator.cs` (add `ValidateEnum` always-on rules)
- Create: `tests/Atlas.Tests/EnumValidationTests.cs` (5 tests, §8.4 #1-5)

These rules run for every typemap with a non-null `EnumConfig`, regardless of the strict-mode flag. They catch garbage configurations: per-value overrides referencing undefined values, fallbacks that aren't defined dest values, and the foot-gun guard for `Ignore` when `default(dstType)` isn't defined.

- [ ] **Step 1: Write failing tests**

Create `tests/Atlas.Tests/EnumValidationTests.cs`:

```csharp
namespace Atlas.Tests;

public class EnumValidationTests
{
    public enum Src { A = 1, B = 2 }
    public enum Dst { X = 1, Y = 2 }
    public enum DstNoZero { X = 1, Y = 2 }   // no defined value for 0
    public enum DstWithZero { X = 0, Y = 1 }

    [Fact]
    public void MapValue_SourceValueNotDefinedOnSourceEnum_AssertConfig_Throws()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<Src, Dst>().MapValue((Src)99, Dst.X));
        var ex = Assert.Throws<AtlasConfigurationException>(() => cfg.AssertConfigurationIsValid());
        Assert.Contains("99", ex.Message);
    }

    [Fact]
    public void MapValue_DestValueNotDefinedOnDestEnum_AssertConfig_Throws()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<Src, Dst>().MapValue(Src.A, (Dst)99));
        Assert.Throws<AtlasConfigurationException>(() => cfg.AssertConfigurationIsValid());
    }

    [Fact]
    public void Ignore_SourceValueNotDefinedOnSourceEnum_AssertConfig_Throws()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<Src, Dst>().Ignore((Src)99));
        Assert.Throws<AtlasConfigurationException>(() => cfg.AssertConfigurationIsValid());
    }

    [Fact]
    public void WithFallback_DestValueNotDefinedOnDestEnum_AssertConfig_Throws()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<Src, Dst>().WithFallback((Dst)99));
        Assert.Throws<AtlasConfigurationException>(() => cfg.AssertConfigurationIsValid());
    }

    [Fact]
    public void Ignore_WhenDefaultDstIsNotDefined_AssertConfig_Throws_TheFootGunGuard()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<Src, DstNoZero>().Ignore(Src.A));
        var ex = Assert.Throws<AtlasConfigurationException>(() => cfg.AssertConfigurationIsValid());
        Assert.Contains("default", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 2: Run tests; verify they fail**

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~EnumValidationTests" --nologo`

Expected: 5 fail. Some may fail with no exception thrown (validation lets the bad config through); others may throw the wrong exception (e.g., `InvalidOperationException` from `EnumMapConfig.AddOverride` if the test checks happen to trip a different invariant).

- [ ] **Step 3: Modify `ConfigurationValidator.Validate` signature to accept the strict-mode flag**

Open `src/Atlas/Internal/ConfigurationValidator.cs`. Change the `Validate` method signature from:

```csharp
    public static void Validate(MapperRegistry registry)
```

to:

```csharp
    public static void Validate(MapperRegistry registry, bool enumValidationEnabled = false)
```

Inside the per-typemap loop, hoist enum validation BEFORE the `MemberList.None` and `CustomConverter` skips (matches the inheritance-validator placement pattern):

```csharp
    public static void Validate(MapperRegistry registry, bool enumValidationEnabled = false)
    {
        var errors = new List<ConfigurationError>();
        foreach (var tm in registry.AllTypeMaps)
        {
            // Enum rules (always-on; covers per-value overrides, fallback, foot-gun guard).
            ValidateEnum(tm, errors);

            // Strict-mode enum source-side coverage (Task 9).
            if (enumValidationEnabled)
                ValidateEnumStrict(tm, errors);

            // Inheritance rules (existing).
            ValidateInheritance(tm, registry, errors);

            if (tm.MemberList == MemberList.None) continue;

            if (tm.CustomConverter is not null) continue;

            if (tm.MemberList == MemberList.Destination)
                ValidateDestination(tm, registry, errors);
            else
                ValidateSource(tm, registry, errors);
        }

        if (errors.Count > 0)
            throw new AtlasConfigurationException(errors);
    }
```

Add the two new helper methods:

```csharp
    private static void ValidateEnum(TypeMap tm, List<ConfigurationError> errors)
    {
        if (tm.EnumConfig is null) return;
        var cfg = tm.EnumConfig;
        var srcType = tm.SourceType;
        var dstType = tm.DestinationType;

        // Rule: PerValueOverrides keys must be defined on srcType.
        foreach (var (src, dst) in cfg.PerValueOverrides)
        {
            if (!Enum.IsDefined(srcType, src))
                errors.Add(new ConfigurationError(
                    srcType, dstType, "(MapValue)",
                    $"MapValue source value '{src}' is not defined on {srcType.Name}."));
            if (!Enum.IsDefined(dstType, dst))
                errors.Add(new ConfigurationError(
                    srcType, dstType, "(MapValue)",
                    $"MapValue destination value '{dst}' is not defined on {dstType.Name}."));
        }

        // Rule: IgnoredSourceValues entries must be defined on srcType.
        foreach (var src in cfg.IgnoredSourceValues)
        {
            if (!Enum.IsDefined(srcType, src))
                errors.Add(new ConfigurationError(
                    srcType, dstType, "(Ignore)",
                    $"Ignore source value '{src}' is not defined on {srcType.Name}."));
        }

        // Rule: Fallback must be defined on dstType.
        if (cfg.HasFallback)
        {
            if (!Enum.IsDefined(dstType, cfg.FallbackValue!))
                errors.Add(new ConfigurationError(
                    srcType, dstType, "(WithFallback)",
                    $"WithFallback value '{cfg.FallbackValue}' is not defined on {dstType.Name}."));
        }

        // Rule: foot-gun guard — Ignore + undefined default(dstType).
        if (cfg.IgnoredSourceValues.Count > 0)
        {
            var defaultDst = Activator.CreateInstance(dstType)!;
            if (!Enum.IsDefined(dstType, defaultDst))
                errors.Add(new ConfigurationError(
                    srcType, dstType, "(Ignore)",
                    $"Ignore() would produce default({dstType.Name}) which is not a defined enum value (zero value undefined). Use MapValue with an explicit destination instead."));
        }
    }

    private static void ValidateEnumStrict(TypeMap tm, List<ConfigurationError> errors)
    {
        // Filled in Task 9.
    }
```

- [ ] **Step 4: Update the call site in `MapperConfiguration.AssertConfigurationIsValid`**

Open `src/Atlas/MapperConfiguration.cs`. Change the body of `AssertConfigurationIsValid`:

```csharp
    public void AssertConfigurationIsValid() =>
        ConfigurationValidator.Validate(_registry, _enumValidationEnabled);
```

- [ ] **Step 5: Run tests; verify they pass**

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~EnumValidationTests" --nologo`

Expected: 5 passed.

- [ ] **Step 6: Run all tests; confirm no regressions**

Run: `dotnet test --nologo`

Expected: baseline + 44 (2 + 10 + 5 + 12 + 10 + 5) = 260 if baseline was 216. Existing inheritance validation tests must still pass (the enum rule runs additionally, doesn't replace inheritance rules).

- [ ] **Step 7: Commit**

```powershell
git add src/Atlas/Internal/ConfigurationValidator.cs src/Atlas/MapperConfiguration.cs tests/Atlas.Tests/EnumValidationTests.cs
git commit -m "Add always-on enum validation rules to ConfigurationValidator (5 tests)"
```

---

## Task 9: Strict-mode enum validation (5 tests)

**Files:**
- Modify: `src/Atlas/Internal/ConfigurationValidator.cs` (fill in `ValidateEnumStrict`)
- Modify: `tests/Atlas.Tests/EnumValidationTests.cs` (add 5 tests, §8.4 #6-10)

Strict-mode iterates every typemap where source AND dest are both enums (regardless of whether `EnumConfig` is null) and asserts every defined source enum value is covered by override/ignore/strategy/fallback.

- [ ] **Step 1: Add 5 more failing tests to `EnumValidationTests`**

Append to `tests/Atlas.Tests/EnumValidationTests.cs`:

```csharp
    public enum SrcGap { A = 1, B = 2, C = 3 }
    public enum DstGap { A = 1, B = 2 }   // missing 3 → SrcGap.C uncovered

    [Fact]
    public void EnableEnumMappingValidation_NotCalled_GapsInCoverage_AssertConfig_Passes()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<SrcGap, DstGap>());
        // No EnableEnumMappingValidation, so strict validation is OFF.
        cfg.AssertConfigurationIsValid();   // no throw — gap is allowed without strict mode
    }

    [Fact]
    public void EnableEnumMappingValidation_GapInCoverage_AssertConfig_ThrowsListsAllUncoveredValues()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.EnableEnumMappingValidation();
            c.CreateMap<SrcGap, DstGap>();
        });
        var ex = Assert.Throws<AtlasConfigurationException>(() => cfg.AssertConfigurationIsValid());
        Assert.Contains("C", ex.Message);
    }

    [Fact]
    public void EnableEnumMappingValidation_WithFallback_AllValuesCovered_Passes()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.EnableEnumMappingValidation();
            c.CreateMap<SrcGap, DstGap>().WithFallback(DstGap.A);
        });
        cfg.AssertConfigurationIsValid();   // no throw — fallback covers C
    }

    [Fact]
    public void EnableEnumMappingValidation_RegisteredMapWithNoEnumMethods_DefaultByValueAppliesToValidation()
    {
        // CreateMap with no enum methods → EnumConfig is null → strict validation uses default ByValue.
        // SrcGap.C has no underlying value match in DstGap → uncovered.
        var cfg = new MapperConfiguration(c =>
        {
            c.EnableEnumMappingValidation();
            c.CreateMap<SrcGap, DstGap>();   // no enum methods; null EnumConfig
        });
        var ex = Assert.Throws<AtlasConfigurationException>(() => cfg.AssertConfigurationIsValid());
        Assert.Contains("C", ex.Message);
    }

    [Fact]
    public void EnableEnumMappingValidation_DoesNotValidate_StringToEnumOrEnumToString_AutoConversions()
    {
        // Property-level auto-conversions don't go through registered typemaps → strict validation
        // doesn't apply to them. A DTO with enum→string property mapping shouldn't be validated.
        var cfg = new MapperConfiguration(c =>
        {
            c.EnableEnumMappingValidation();
            c.CreateMap<SrcWithEnumProp, DstWithStringProp>();   // not enum→enum at the typemap level
        });
        cfg.AssertConfigurationIsValid();   // no throw
    }

    public class SrcWithEnumProp { public SrcGap Value { get; set; } }
    public class DstWithStringProp { public string? Value { get; set; } }
}
```

- [ ] **Step 2: Run tests; verify they fail**

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~EnumValidationTests" --nologo`

Expected: 5 of the 10 fail (the new ones — strict-mode tests). The 5 from Task 8 still pass.

- [ ] **Step 3: Implement `ValidateEnumStrict`**

Open `src/Atlas/Internal/ConfigurationValidator.cs`. Replace the `ValidateEnumStrict` stub with:

```csharp
    private static void ValidateEnumStrict(TypeMap tm, List<ConfigurationError> errors)
    {
        // Strict mode applies only to typemaps where BOTH sides are enum types.
        // (Auto-conversions at the property level — enum→string, string→enum, etc. — are not
        // validated here; they're not registered typemaps.)
        if (!tm.SourceType.IsEnum || !tm.DestinationType.IsEnum) return;

        var cfg = tm.EnumConfig ?? new EnumMapConfig();   // null → use defaults (ByValue)
        var uncovered = new List<object>();

        foreach (var definedSrc in Enum.GetValues(tm.SourceType))
        {
            var action = EnumResolver.Resolve(definedSrc, cfg, tm.SourceType, tm.DestinationType);
            if (action.Kind == EnumResolver.ActionKind.Throw)
                uncovered.Add(definedSrc);
        }

        if (uncovered.Count > 0)
        {
            var list = string.Join(", ", uncovered);
            errors.Add(new ConfigurationError(
                tm.SourceType, tm.DestinationType, "(strict)",
                $"Strict enum validation: source values [{list}] have no mapping. Declare MapValue / Ignore for each, or WithFallback for a catch-all."));
        }
    }
```

- [ ] **Step 4: Run tests; verify they pass**

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~EnumValidationTests" --nologo`

Expected: 10 passed.

- [ ] **Step 5: Run all tests; confirm no regressions**

Run: `dotnet test --nologo`

Expected: baseline + 49 (44 + 5) = 265.

- [ ] **Step 6: Commit**

```powershell
git add src/Atlas/Internal/ConfigurationValidator.cs tests/Atlas.Tests/EnumValidationTests.cs
git commit -m "Add strict-mode enum validation via EnumResolver (5 more tests)"
```

---

## Task 10: End-to-end `MapperEnumTests` (6 tests)

**Files:**
- Create: `tests/Atlas.Tests/MapperEnumTests.cs` (6 tests, §8.5)

End-to-end smoke tests via `IMapper.Map<TDest>(source)` exercising enum properties on object DTOs. These tests aren't whitebox — they exercise the same paths covered by EnumExplicitMapTests + EnumAutoConversionTests but at the full DTO level.

- [ ] **Step 1: Write failing tests**

Create `tests/Atlas.Tests/MapperEnumTests.cs`:

```csharp
namespace Atlas.Tests;

public class MapperEnumTests
{
    public enum Status { Pending, Active, Cancelled }
    public enum StatusV2 { Pending, Active, Cancelled }
    public enum LegacyStatus { Pending, Active, Internal }

    public class Src { public Status Status { get; set; } public string Name { get; set; } = ""; }
    public class Dst { public Status Status { get; set; } public string Name { get; set; } = ""; }

    public class SrcWithNullable { public Status? Status { get; set; } }
    public class DstWithNullable { public Status? Status { get; set; } }

    public class SrcWithStringStatus { public string Status { get; set; } = ""; }
    public class DstWithEnumStatus { public Status Status { get; set; } }

    public class SrcWithLegacy { public LegacyStatus Status { get; set; } }
    public class DstWithStatusV2 { public StatusV2 Status { get; set; } }

    public class Outer { public Inner? Inner { get; set; } }
    public class Inner { public Status Status { get; set; } }
    public class OuterDst { public InnerDst? Inner { get; set; } }
    public class InnerDst { public Status Status { get; set; } }

    [Fact]
    public void Map_ObjectWithEnumProperty_AutoConverts_SameUnderlyingType()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<Src, Dst>());
        var mapper = cfg.CreateMapper();
        var dst = mapper.Map<Src, Dst>(new Src { Status = Status.Active, Name = "hello" });
        Assert.Equal(Status.Active, dst.Status);
        Assert.Equal("hello", dst.Name);
    }

    [Fact]
    public void Map_ObjectWithNullableEnumProperty_NullSource_PreservesNull()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<SrcWithNullable, DstWithNullable>());
        var mapper = cfg.CreateMapper();
        var dst = mapper.Map<SrcWithNullable, DstWithNullable>(new SrcWithNullable { Status = null });
        Assert.Null(dst.Status);
    }

    [Fact]
    public void Map_ObjectWithStringPropertyToEnumProperty_AutoConvertsViaName()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<SrcWithStringStatus, DstWithEnumStatus>());
        var mapper = cfg.CreateMapper();
        var dst = mapper.Map<SrcWithStringStatus, DstWithEnumStatus>(new SrcWithStringStatus { Status = "Active" });
        Assert.Equal(Status.Active, dst.Status);
    }

    [Fact]
    public void Map_ObjectWithEnumProperty_RegisteredMapWithMapByName_UsesNameStrategy()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap<LegacyStatus, StatusV2>().MapByName();
            c.CreateMap<SrcWithLegacy, DstWithStatusV2>();
        });
        var mapper = cfg.CreateMapper();
        var dst = mapper.Map<SrcWithLegacy, DstWithStatusV2>(new SrcWithLegacy { Status = LegacyStatus.Active });
        Assert.Equal(StatusV2.Active, dst.Status);
    }

    [Fact]
    public void Map_ObjectWithEnumProperty_RegisteredMapWithFallback_UnmatchedUsesFallback()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap<LegacyStatus, StatusV2>().WithFallback(StatusV2.Cancelled);
            c.CreateMap<SrcWithLegacy, DstWithStatusV2>();
        });
        var mapper = cfg.CreateMapper();
        // LegacyStatus.Internal has no matching value in StatusV2 by ByValue → fallback Cancelled.
        var dst = mapper.Map<SrcWithLegacy, DstWithStatusV2>(new SrcWithLegacy { Status = LegacyStatus.Internal });
        Assert.Equal(StatusV2.Cancelled, dst.Status);
    }

    [Fact]
    public void Map_NestedDtoWithEnumProperty_RoutesThroughRegisteredEnumMap()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap<Inner, InnerDst>();
            c.CreateMap<Outer, OuterDst>();
        });
        var mapper = cfg.CreateMapper();
        var dst = mapper.Map<Outer, OuterDst>(new Outer { Inner = new Inner { Status = Status.Cancelled } });
        Assert.NotNull(dst.Inner);
        Assert.Equal(Status.Cancelled, dst.Inner!.Status);
    }
}
```

- [ ] **Step 2: Run tests; verify they fail or pass**

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~MapperEnumTests" --nologo`

Expected: most should pass given Tasks 6-7 already implemented the underlying paths. Any failures are integration gaps — debug by examining the failing test's compiled lambda.

If `Map_ObjectWithEnumProperty_RegisteredMapWithMapByName_UsesNameStrategy` fails because the property mapping doesn't route to the registered enum map, verify spec R1 — the registered-map check in `ConvertOrMap` (Task 7 step 6).

- [ ] **Step 3: Run all tests; confirm no regressions**

Run: `dotnet test --nologo`

Expected: baseline + 55 (49 + 6) = 271.

- [ ] **Step 4: Commit**

```powershell
git add tests/Atlas.Tests/MapperEnumTests.cs
git commit -m "Add MapperEnumTests for end-to-end enum mapping (6 tests)"
```

---

## Task 11: Coverage check + README + memory updates + PR readiness

**Files:**
- Modify: `README.md` (add `## Enum surface` section, update coverage table)
- No code changes other than the README.

- [ ] **Step 1: Run full test suite + coverage**

Run:
```powershell
dotnet test /p:CollectCoverage=true /p:CoverletOutputFormat=cobertura /p:CoverletOutput=./TestResults/coverage.cobertura.xml --nologo
```

Expected: ~265-271 tests pass. Coverage on `Atlas` core line ≥ 90%, branch ≥ 80%.

If branch coverage falls short, the most likely uncovered branches are in `EnumConversions.BuildConversion` (the four conversion variants) or `EnumResolver.Resolve` (the strategy match arms). Add tests as needed; the existing test files have natural homes.

- [ ] **Step 2: Update README**

Open `README.md`. After the existing `## Inheritance & polymorphism` section, add a new section:

```markdown
## Enum surface

Enum-typed properties auto-convert without an explicit `CreateMap`:

```csharp
public enum OrderStatusV1 { Pending = 1, Active = 2, Cancelled = 3 }
public enum OrderStatusV2 { Pending = 1, Active = 2, Cancelled = 3, Refunded = 4 }

public class Order { public OrderStatusV1 Status { get; set; } }
public class OrderDto { public OrderStatusV2 Status { get; set; } }

cfg.CreateMap<Order, OrderDto>();
// Status automatically maps from V1 to V2 by underlying value.
```

For customization, declare a `CreateMap<TEnumSrc, TEnumDst>()` with one or more enum methods:

```csharp
cfg.CreateMap<LegacyStatus, OrderStatusV2>()
   .MapByName(ignoreCase: true)
   .MapValue(LegacyStatus.Pending, OrderStatusV2.Active)
   .Ignore(LegacyStatus.Internal)
   .WithFallback(OrderStatusV2.Cancelled);
```

`mapper.Map<OrderStatusV2>(LegacyStatus.X)` consults: per-value override → ignore → strategy match → fallback → throws `AtlasMappingException`.

**String ↔ enum** is also auto-handled (verbatim member name, case-sensitive parse). Cross-type enum mapping with different underlying types (e.g., `byte` → `int`) auto-converts.

**Strict validation:** `cfg.EnableEnumMappingValidation()` makes `AssertConfigurationIsValid()` enforce that every defined source enum value in every registered enum→enum map is covered by override / ignore / strategy / fallback.

**Foot-gun guards:**
- `Ignore(srcValue)` produces `default(TDst)`. If `default(TDst)` isn't a defined value of TDst, validation throws — use `MapValue` with an explicit dest instead.
- `[Flags]` enums: only single-bit defined values are recognized by the auto-strategy. Combinations require explicit `MapValue` declarations.
- `Atlas.Projections` does NOT translate the enum-mapping switch into LINQ. ProjectTo of enum-typed properties relies on the underlying provider's enum support.
```

Also update the coverage table at the top of the README — replace the line/branch numbers for `Atlas` with the post-feature values from Step 1's coverage report.

Remove any "Enum surface" or "Enums" entry from the "Deferred to v2" list (search for "Enum" in the README to confirm).

- [ ] **Step 3: Verify build and tests are still green**

Run:
```powershell
dotnet build --nologo
dotnet test --nologo
```

Expected: clean build, all tests pass.

- [ ] **Step 4: Final commit + handoff**

```powershell
git add README.md
git commit -m "docs: README — add enum surface section, refresh coverage numbers"
```

- [ ] **Step 5: Push and open PR**

Per the workflow established in `feat/inheritance`:
```powershell
git push -u origin feat/enum-surface
gh pr create --title "Add enum surface (auto-conversion + customization) to core Atlas" --body "$(cat <<'EOF'
## Summary
- Auto-conversion for enum↔enum, enum↔string, string↔enum, enum↔underlying numeric — no CreateMap required.
- Five new methods on `IMappingExpression<,>`: `MapByValue`, `MapByName`, `MapValue`, `Ignore(TSource)`, `WithFallback`.
- `cfg.EnableEnumMappingValidation()` for strict source-side validation.
- New `AtlasMappingException` for runtime mapping errors (sibling of `AtlasConfigurationException`).
- ~50 new tests; full suite green.

See `docs/Atlas-Design-EnumSurface.md` and `docs/Atlas-Plan-EnumSurface.md`.

## Test plan
- [ ] CI green
- [ ] Coverage on Atlas core line ≥ 90%, branch ≥ 80%
- [ ] No regression in 216 baseline tests

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

After PR is open, run holistic review:
- `superpowers:requesting-code-review` (code-quality reviewer)
- Spec review (verify implementation matches `docs/Atlas-Design-EnumSurface.md`)

Address any Critical / Important findings before merge. Minor findings can be folded into the same PR or deferred per reviewer judgement.

---

## Final notes

- **Test count target:** ~265-271 (216 baseline + ~50). If actual count differs significantly, investigate before reporting "done."
- **Coverage gates:** line ≥ 90%, branch ≥ 80% on `Atlas` core (matches v1 + Inheritance gates).
- **No `Atlas.Projections` changes** — enum behavior in ProjectTo is what the provider natively does. If a future v2 design adds discriminator-aware enum projection, it builds on top of this work.
- **Spec self-correction note:** Per memory `feedback_pseudocode_concrete_trace`, two implementer-caught bugs in the inheritance plan stemmed from untraced pseudocode. The §6.2 worked trace in the spec is the canonical example for this plan; verify your `EnumResolver.Resolve` behavior matches it as a sanity check before declaring Task 6 done.
