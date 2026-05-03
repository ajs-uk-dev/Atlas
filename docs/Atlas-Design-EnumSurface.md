# Design: Atlas v2 — Enum Surface

> **Status:** Approved design, ready to implement.
> **Depends on:** `Atlas` v1 + Inheritance (216 tests green pre-feature: 156 Atlas + 52 Projections + 8 Projections.EFCore).
> **Output of this doc:** Enum-aware mapping surface added to the core `Atlas` package. No new packages.

---

## 1. Goals & Non-Goals

### 1.1 Goals
- Enum-typed properties on DTOs map automatically without an explicit `CreateMap` declaration: `enum → enum` (by underlying value), `enum → string` (verbatim member name), `string → enum` (case-sensitive verbatim parse), `enum ↔ underlying numeric` (direct cast).
- `CreateMap<TEnumSrc, TEnumDst>()` is the opt-in entry for customization: by-name strategy (with optional case-insensitivity), per-value overrides, source-side ignore, fallback destination value.
- Atlas's strict-by-default posture extends to enums: any unmatched source value at runtime throws `AtlasMappingException` unless `WithFallback(...)` is configured.
- Profile/config-wide `cfg.EnableEnumMappingValidation()` makes `AssertConfigurationIsValid()` assert every defined source enum value is covered (override, ignore, strategy match, or fallback).
- Zero performance impact on non-enum maps. Enum maps compile to a single `Expression.Switch` with no per-call dictionary lookups, no `Enum.GetName` / `Enum.TryParse` reflection.
- All changes are **additive** — no public type signatures change shape; no existing test should regress.

### 1.2 Non-Goals (explicit out-of-scope; future design docs)
- **`[Description]` / `[EnumMember]` attribute hooks.** Adds attribute-discovery infrastructure orthogonal to the core algorithm. Defer to a separate v2 doc.
- **Naming policies beyond verbatim** (snake_case, kebab-case, UPPER_SNAKE_CASE, camelCase, PascalCase). Doubles the test surface for the long tail; v1 covers the 90% case.
- **`IgnoreTargetValue`** (destination-side ignore). Source-side covers the common case; destination-side is for "this dest value is reserved" scenarios.
- **`ByValueCheckDefined` strategy.** Made redundant by Atlas's "throw on unmatched everywhere" default — Mapperly ships this strategy because their default is silent cast; we don't have that problem.
- **Destination-side strict validation.** Source-side covers the 90% case; dest-side fires on legitimate "dest enum has new values v1 source can't produce yet" scenarios. Defer.
- **`[Flags]` enum combination mapping.** The §6 algorithm only iterates single-bit defined values via `Enum.GetValues`. Combination-of-flags requires explicit per-combination `MapValue` declarations (which already work).
- **`Atlas.Projections` enum behavior.** Whatever the LINQ provider natively does. ProjectTo's auto-conversion of enum properties is a function of the provider's `Enum.Parse` / cast support, not Atlas. Out of scope.

---

## 2. Architecture Overview

```
┌────────────────────────────────────────────────────────────┐
│                        Atlas (core)                        │
│                                                            │
│  Configuration phase:                                      │
│    MappingExpression.MapByValue() / MapByName()            │
│      → typeMap.EnumConfig.SetStrategy(...)                 │
│    MappingExpression.MapValue(src, dst)                    │
│      → typeMap.EnumConfig.AddOverride(src, dst)            │
│    MappingExpression.Ignore(srcValue)                      │
│      → typeMap.EnumConfig.AddIgnore(srcValue)              │
│    MappingExpression.WithFallback(dst)                     │
│      → typeMap.EnumConfig.SetFallback(dst)                 │
│    MapperConfigurationExpression                           │
│      .EnableEnumMappingValidation()                        │
│        → cfg.EnumValidationEnabled = true                  │
│                                                            │
│  Build phase (in MapperConfiguration ctor):                │
│    1. Existing InheritanceMerger.Resolve                   │
│    2. Existing ConventionEngine.ResolveMissingMembers      │
│    3. Existing TypeMap.Seal                                │
│       (no new build-phase pass — enum config is consumed   │
│        on demand at compile and validate time)             │
│                                                            │
│  Runtime phase:                                            │
│    ExecutionPlanBuilder.Build(typeMap, registry):          │
│      if typeMap.SourceType.IsEnum &&                       │
│         typeMap.DestinationType.IsEnum:                    │
│        return BuildEnumLambda(typeMap)                     │
│      if typeMap.IncludedDerived.Count > 0:                 │
│        return BuildWithInheritanceDispatch(typeMap)        │
│      return BuildBaseBody(typeMap)                         │
│                                                            │
│    EnumConversions (used by ConventionEngine for           │
│    property-level enum auto-conversion when no             │
│    typemap is registered):                                 │
│      enum→enum  → switch with default ByValue              │
│      enum→string → Enum.GetName                            │
│      string→enum → Dictionary<string,T> lookup             │
│      enum↔numeric → Expression.Convert                     │
└────────────────────────────────────────────────────────────┘
```

The change is **purely additive** to v1 + Inheritance. No existing public type changes shape. No new packages. Inheritance dispatch and enum dispatch are disjoint (enum types are sealed) and the Build prologue handles them as separate branches.

---

## 3. Solution & Project Layout

No new projects. Files modified:

```
src/Atlas/
  Configuration/
    IMappingExpression.cs           ← add 5 enum methods (MapByValue/MapByName/MapValue/Ignore/WithFallback)
    MappingExpression.cs            ← implement them
  Internal/
    TypeMap.cs                      ← add EnumConfig field
    EnumMapConfig.cs                ← NEW: per-typemap enum config object
    EnumConversions.cs              ← NEW: auto-conversion layer
    EnumResolver.cs                 ← NEW: shared per-value resolution (used by builder + validator)
    StringToEnumCache.cs            ← NEW: per-config dst-enum lookup cache
    ExecutionPlanBuilder.cs         ← enum prologue + BuildEnumLambda
    ConventionEngine.cs             ← EnumConversions hook in IsAssignmentCompatible + property path
    ConfigurationValidator.cs       ← always-on enum invariants + strict-mode validation
  MapperConfiguration.cs            ← propagate EnumValidationEnabled flag to validator
  MapperConfigurationExpression.cs  ← add EnableEnumMappingValidation()
  AtlasMappingException.cs          ← NEW: runtime mapping errors (replaces ad-hoc InvalidOperationException for enum throws)

tests/Atlas.Tests/
  Internal/
    EnumMapConfigTests.cs           ← NEW: 10 tests (§8.1)
  EnumAutoConversionTests.cs        ← NEW: 10 tests (§8.2)
  EnumExplicitMapTests.cs           ← NEW: 12 tests (§8.3)
  EnumValidationTests.cs            ← NEW: 10 tests (§8.4)
  MapperEnumTests.cs                ← NEW: 6 tests (§8.5)

README.md                           ← add ## Enum surface section + coverage refresh
```

No changes to `Directory.Packages.props`, `Atlas.slnx`, csproj files, `Atlas.Projections`, or `Atlas.Extensions.DependencyInjection`.

---

## 4. Public API Additions

### 4.1 `IMappingExpression<TSource, TDestination>` — five new methods

```csharp
namespace Atlas.Configuration;

public interface IMappingExpression<TSource, TDestination>
{
    // ... existing methods (ForMember, ForCtorParam, ConvertUsing, Include, IncludeBase, etc.) ...

    /// <summary>
    /// Forces by-value matching for this enum→enum map (matches by underlying integer).
    /// This is the default if neither MapByValue nor MapByName is called.
    /// </summary>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown at configuration time if <typeparamref name="TSource"/> or
    /// <typeparamref name="TDestination"/> is not an enum.
    /// </exception>
    IMappingExpression<TSource, TDestination> MapByValue();

    /// <summary>
    /// Forces by-name matching for this enum→enum map (matches by member name).
    /// </summary>
    /// <param name="ignoreCase">If true, name matching is case-insensitive. Defaults to false.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown at configuration time if <typeparamref name="TSource"/> or
    /// <typeparamref name="TDestination"/> is not an enum.
    /// </exception>
    IMappingExpression<TSource, TDestination> MapByName(bool ignoreCase = false);

    /// <summary>
    /// Maps a specific source enum value to a specific destination enum value.
    /// Takes precedence over the strategy default. Repeating the same source value throws.
    /// </summary>
    /// <exception cref="Atlas.AtlasConfigurationException">
    /// Thrown if <paramref name="sourceValue"/> is already configured via MapValue or Ignore.
    /// </exception>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown at configuration time if <typeparamref name="TSource"/> or
    /// <typeparamref name="TDestination"/> is not an enum.
    /// </exception>
    IMappingExpression<TSource, TDestination> MapValue(TSource sourceValue, TDestination destinationValue);

    /// <summary>
    /// Marks a source enum value as ignored. Mapping that value at runtime produces
    /// <c>default(TDestination)</c> rather than searching the strategy or fallback.
    /// </summary>
    /// <remarks>
    /// If <c>default(TDestination)</c> is not a defined value of <typeparamref name="TDestination"/>,
    /// <see cref="Atlas.MapperConfiguration.AssertConfigurationIsValid"/> throws — Ignore would
    /// otherwise silently produce an undefined enum value (a subtle foot-gun).
    /// In that case, use <see cref="MapValue"/> with an explicit destination instead.
    /// </remarks>
    /// <exception cref="Atlas.AtlasConfigurationException">
    /// Thrown if <paramref name="sourceValue"/> is already configured via MapValue.
    /// </exception>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown at configuration time if <typeparamref name="TSource"/> is not an enum.
    /// </exception>
    IMappingExpression<TSource, TDestination> Ignore(TSource sourceValue);

    /// <summary>
    /// Sets a fallback destination value used when no explicit MapValue, Ignore, or strategy
    /// match applies. Without a fallback, unmatched source values throw
    /// <see cref="Atlas.AtlasMappingException"/> at runtime.
    /// </summary>
    /// <remarks>
    /// A configured fallback short-circuits source-side strict validation
    /// (<see cref="Atlas.Configuration.MapperConfigurationExpression.EnableEnumMappingValidation"/>):
    /// every unmatched source value resolves to the fallback at compile time, so no values are
    /// "uncovered" from the validator's perspective.
    /// </remarks>
    /// <exception cref="Atlas.AtlasConfigurationException">
    /// Thrown if WithFallback was already called on this map.
    /// </exception>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown at configuration time if <typeparamref name="TDestination"/> is not an enum.
    /// </exception>
    IMappingExpression<TSource, TDestination> WithFallback(TDestination fallbackValue);
}
```

**Naming notes:**
- The existing `Ignore(Expression<Func<TDestination, object>> destinationMember)` and the new `Ignore(TSource sourceValue)` are overloads. Resolution is unambiguous (lambda vs enum value). The new overload throws at config time if `TSource` is not an enum, since calling `.Ignore(SomeEnum.X)` on a non-enum map is a programming error.
- `MapValue` deliberately *not* overloaded with the existing `MapFrom` family — different semantics (per-value lookup table vs property-level expression).

### 4.2 `MapperConfigurationExpression` — one new method

```csharp
namespace Atlas.Configuration;

public class MapperConfigurationExpression
{
    // ... existing members ...

    /// <summary>
    /// Enables strict source-side enum mapping validation. When enabled,
    /// <see cref="Atlas.MapperConfiguration.AssertConfigurationIsValid"/> asserts that every
    /// defined source enum value in every registered enum→enum map is covered by MapValue,
    /// Ignore, the strategy, or WithFallback. Disabled by default.
    /// </summary>
    public void EnableEnumMappingValidation();
}
```

The `EnumValidationEnabled` flag is internal, set only by this method.

### 4.3 What does NOT change

- `MapperProfile` base class — no new methods.
- `IMapper` facade — no new methods. `mapper.Map<TDest>(src)` already routes enum source/dest correctly through the registry once the typemap is registered.
- `MemberList` — no new variants.
- `Atlas.Extensions.DependencyInjection` — no new DI surface.

### 4.4 Worked example

```csharp
public class StatusProfile : MapperProfile
{
    public StatusProfile()
    {
        // Auto-conversion — no CreateMap needed.
        // OrderStatusV1 (Active=1, Cancelled=2) → OrderStatusV2 (Active=1, Cancelled=2, Refunded=3)
        // works automatically by underlying value.

        // Customized mapping — opt in to by-name with overrides.
        CreateMap<LegacyStatus, OrderStatusV2>()
            .MapByName(ignoreCase: true)
            .MapValue(LegacyStatus.Pending, OrderStatusV2.Active)
            .Ignore(LegacyStatus.Internal)             // produces default(OrderStatusV2)
            .WithFallback(OrderStatusV2.Cancelled);    // catch-all
    }
}

services.AddAtlas(cfg =>
{
    cfg.EnableEnumMappingValidation();   // strict mode for all enum maps
    cfg.AddProfile<StatusProfile>();
}, typeof(StatusProfile).Assembly);
```

---

## 5. Internal Architecture

### 5.1 `TypeMap` data model addition

```csharp
public sealed class TypeMap
{
    // ... existing members ...
    public EnumMapConfig? EnumConfig { get; set; }   // null unless an enum-method has been called
}
```

`EnumConfig == null` does **not** mean "this typemap is non-enum." It means "no customization beyond defaults." A registered enum→enum typemap with no enum methods called still has `EnumConfig == null` and behaves as if `MapByValue()` was the only configuration.

### 5.2 `EnumMapConfig` — the configuration object

```csharp
namespace Atlas.Internal;

internal enum EnumMappingStrategy { ByValue, ByName }

internal sealed class EnumMapConfig
{
    public EnumMappingStrategy Strategy { get; private set; } = EnumMappingStrategy.ByValue;
    public bool IgnoreCase { get; private set; }
    public bool StrategyExplicitlySet { get; private set; }

    // Boxed to handle any underlying type (byte/short/int/long, signed/unsigned).
    // Boxing only at config time; runtime uses compiled switch with Expression.Constant.
    public Dictionary<object, object> PerValueOverrides { get; } = new();
    public HashSet<object> IgnoredSourceValues { get; } = new();

    public bool HasFallback { get; private set; }
    public object? FallbackValue { get; private set; }

    public void SetStrategy(EnumMappingStrategy strategy, bool ignoreCase);
    public void AddOverride(object src, object dst);
    public void AddIgnore(object src);
    public void SetFallback(object dst);
}
```

Each setter enforces single-call semantics where applicable:
- `SetStrategy` throws `AtlasConfigurationException` if called a second time (i.e., `MapByValue()` after `MapByName()` or vice versa).
- `AddOverride` throws if the source value is already in `PerValueOverrides` or `IgnoredSourceValues`.
- `AddIgnore` throws if the source value is already in `PerValueOverrides`.
- `SetFallback` throws if called a second time.

### 5.3 `MapperConfigurationExpression.EnableEnumMappingValidation`

```csharp
public class MapperConfigurationExpression
{
    internal bool EnumValidationEnabled { get; private set; }
    public void EnableEnumMappingValidation() => EnumValidationEnabled = true;
}
```

`MapperConfiguration` constructor reads this flag and passes it to `ConfigurationValidator`'s constructor.

### 5.4 `EnumConversions` — auto-conversion layer

```csharp
namespace Atlas.Internal;

internal static class EnumConversions
{
    public static bool HasImplicitConversion(Type srcType, Type dstType);

    // For enum→enum: builds a switch expression equivalent to BuildEnumLambda's body
    //   with default ByValue strategy and no overrides.
    // For enum→string: Expression.Call(typeof(Enum).GetMethod("GetName", ...), Constant(srcType), Convert(src, object))
    // For string→enum: Dictionary<string, dstEnum> lookup precomputed and embedded as Constant
    // For enum↔underlying numeric: Expression.Convert
    public static Expression BuildConversion(
        Expression srcExpr,
        Type dstType,
        StringToEnumCache cache);
}

// Cache hangs off MapperConfiguration (per-config singleton, not static) to avoid
// cross-instance leaks. Lookup key: dst Type. Value: Dictionary<string, dstEnumValue>.
internal sealed class StringToEnumCache
{
    public Dictionary<string, object> GetOrCreateForType(Type dstEnumType);
}
```

`ConventionEngine.IsAssignmentCompatible` calls `EnumConversions.HasImplicitConversion` alongside the existing `NumericConversions.HasImplicitConversion`. The property-conversion path in `ExecutionPlanBuilder` calls `EnumConversions.BuildConversion` when both sides match the conversion criteria and no registered typemap covers the pair.

---

## 6. Per-Value Resolution Algorithm

This algorithm runs at **compile time** (during `BuildEnumLambda`) and at **strict-validation time** (during `AssertConfigurationIsValid`). Single source of truth — no parallel implementations.

### 6.1 `ResolvePerValue(src, cfg, srcType, dstType)`

```
ResolvePerValue(src, cfg, srcType, dstType):
  // 1. Explicit override wins
  if cfg.PerValueOverrides.TryGetValue(src, out var dst):
    return (Hit, dst)

  // 2. Explicit ignore → default(dstType)
  if cfg.IgnoredSourceValues.Contains(src):
    return (Hit, GetDefault(dstType))

  // 3. Strategy match
  match cfg.Strategy:
    ByValue:
      srcUnderlying = ToBoxedUnderlying(src)   // works across byte/short/int/long
      foreach definedDst in Enum.GetValues(dstType):
        if EqualUnderlying(ToBoxedUnderlying(definedDst), srcUnderlying):
          return (Hit, definedDst)
    ByName:
      srcName = Enum.GetName(srcType, src)
      foreach definedDst in Enum.GetValues(dstType):
        if NameEquals(Enum.GetName(dstType, definedDst), srcName, cfg.IgnoreCase):
          return (Hit, definedDst)

  // 4. Fallback
  if cfg.HasFallback:
    return (Hit, cfg.FallbackValue)

  // 5. No match
  return (Throw, $"No mapping defined for {srcType.Name}.{src} → {dstType.Name}")
```

### 6.2 Worked trace

Source = `LegacyStatus { Pending=1, Active=2, Internal=3 }`. Dest = `OrderStatusV2 { Active=1, Cancelled=2, Refunded=3 }`. Config: `MapByName(ignoreCase: true).MapValue(Pending, Active).Ignore(Internal).WithFallback(Cancelled)`.

| `definedSrc` | Step 1 | Step 2 | Step 3 (ByName, ci) | Step 4 fallback? | Result |
|---|---|---|---|---|---|
| Pending | yes → Active | — | — | — | (Hit, Active) |
| Active | no | no | dest "Active" found | — | (Hit, Active) |
| Internal | no | yes → default | — | — | (Hit, default) |

Default case (any source value cast from int that isn't `Pending`/`Active`/`Internal`) → throw.

### 6.3 Ignore + undefined-zero foot-gun guard

`Ignore(srcValue)` produces `default(TDst)` at runtime (step 2 above). If `default(TDst)` is itself not a defined value of `TDst` (e.g., the dest enum has no zero-valued member), the user has invoked an undocumented foot-gun: an Ignored source produces an undefined enum.

To avoid silently-undefined results, `AssertConfigurationIsValid()` adds a guard:
> If `EnumConfig.IgnoredSourceValues` is non-empty AND `default(dstType)` is not a defined value of `dstType`, throw `AtlasConfigurationException`. The remedy: use `MapValue(srcValue, <explicit dest>)` instead of `Ignore`.

The check runs in `ConfigurationValidator` (which has access to the typemap's `dstType`); `EnumMapConfig.AddIgnore` is type-erased and cannot run the check itself.

---

## 7. Compilation & Auto-Conversion Algorithm

### 7.1 `ExecutionPlanBuilder.Build` dispatch

```
Build(typeMap):
  if typeMap.SourceType.IsEnum && typeMap.DestinationType.IsEnum:
    return BuildEnumLambda(typeMap)                  // §7.2
  if typeMap.IncludedDerived.Count > 0:
    return BuildWithInheritanceDispatch(typeMap)     // existing
  return BuildBaseBody(typeMap)                      // existing
```

Enum dispatch precedes inheritance dispatch — they're mutually exclusive (enums are sealed value types). Order matters because cheaper guards run first.

### 7.2 `BuildEnumLambda(typeMap)`

```
BuildEnumLambda(typeMap):
  cfg = typeMap.EnumConfig ?? new EnumMapConfig()    // null → use defaults
  srcType = typeMap.SourceType
  dstType = typeMap.DestinationType
  srcParam = Expression.Parameter(srcType, "src")
  cases = []

  for each definedSrc in Enum.GetValues(srcType):
    action = ResolvePerValue(definedSrc, cfg, srcType, dstType)
    case_body = action.Kind switch
      Hit   => Expression.Constant(action.DestValue, dstType)
      Throw => BuildMappingExceptionThrow(srcType, dstType, definedSrc, action.Reason)
    cases.Add(SwitchCase(case_body, Expression.Constant(definedSrc, srcType)))

  defaultBody = BuildUndefinedSourceThrow(srcType, dstType, srcParam)
  switchExpr = Expression.Switch(srcParam, defaultBody, cases.ToArray())
  return Expression.Lambda<Func<TSrc, TDst>>(switchExpr, srcParam)
```

The compiled switch contains one case per defined source value plus a default case. No runtime dictionary lookups, no `Enum.GetName` / `Enum.TryParse` calls — everything is pre-resolved into `Expression.Constant` operands. The JIT may dispatch as a hash table or jump table depending on case count.

**Switch produced for the §6.2 example:**
```csharp
src switch
{
    LegacyStatus.Pending  => OrderStatusV2.Active,
    LegacyStatus.Active   => OrderStatusV2.Active,
    LegacyStatus.Internal => default(OrderStatusV2),
    _ => throw new AtlasMappingException("Source value is not defined on LegacyStatus")
}
```

### 7.3 Auto-conversion (no `EnumMapConfig`, no registered typemap)

Routes through `EnumConversions.BuildConversion` from `ConventionEngine`'s property-conversion path:

| Source type | Dest type | Generated expression |
|---|---|---|
| `EnumA` | `EnumB` | Same switch as §7.2 with default ByValue, no overrides |
| `EnumA` | `string` | `Enum.GetName(typeof(EnumA), src)` — returns `null` for undefined casts |
| `string` | `EnumB` | `precomputedDict.TryGetValue(src, out var v) ? v : throw new AtlasMappingException(...)` |
| `EnumA` | underlying numeric | `Expression.Convert(src, underlyingType)` |
| underlying numeric | `EnumA` | `Expression.Convert(src, enumType)` |

The `string → enum` precomputed dictionary lives on `MapperConfiguration` (per-config singleton via `StringToEnumCache`), keyed by destination enum type. One dictionary per dest enum type, shared across all `string → enum` conversions in that configuration.

### 7.4 Nullable enum handling (`E?`)

Property-level only — registered enum maps are non-nullable on both sides. The existing v1 nullable wrapper handles `E?` source/dest:
- `E? → E?`: null preserved; non-null unwraps, runs through the registered map (or auto-conversion), wraps result.
- `E → E?`: trivial lift.
- `E? → E`: throws `InvalidOperationException` (or `AtlasMappingException`, matching v1 nullable→non-nullable behavior) if source is null.

Implementer must verify v1's nullable wrapper applies cleanly to enum value types as it does to primitives — the wrapper is type-agnostic but enum-specific tests need to confirm.

### 7.5 Performance posture

Per the v1 design's allocation budget ("no internal dictionaries / context bags allocated per call"):
- Compiled switch has one case per defined source value, dispatched as a hash table or jump table by the JIT — single comparison/lookup per call, no allocation.
- All destination constants baked into `Expression.Constant` — no field reads, no lookups.
- `Throw` arms allocate the exception object only when taken (error path; allocation cost is acceptable).
- Auto-conversion `string → enum` uses one pre-allocated `Dictionary<string, T>` per dst type — same dictionary lives for the lifetime of the `MapperConfiguration`, lookup is O(1).

---

## 8. TDD Plan

xUnit v3 with `Assert.X()` only (no FluentAssertions, per project convention). ~48 tests across 5 files; expected coverage: line ≥ 90%, branch ≥ 80% on Atlas core.

### 8.1 `tests/Atlas.Tests/Internal/EnumMapConfigTests.cs` (10 tests)

Whitebox tests on `EnumMapConfig`. Pure data-model invariants; no compilation, no registry.

1. `SetStrategy_ByValue_FirstCall_Succeeds`
2. `SetStrategy_ByName_AfterByValue_Throws_AtlasConfigurationException`
3. `SetStrategy_ByValue_AfterByName_Throws`
4. `AddOverride_NewKey_Succeeds`
5. `AddOverride_DuplicateKey_Throws`
6. `AddOverride_KeyAlreadyIgnored_Throws`
7. `AddIgnore_NewValue_Succeeds`
8. `AddIgnore_ValueAlreadyOverridden_Throws`
9. `SetFallback_FirstCall_Succeeds`
10. `SetFallback_SecondCall_Throws`

### 8.2 `tests/Atlas.Tests/EnumAutoConversionTests.cs` (10 tests)

End-to-end through `IMapper.Map<>` for the no-CreateMap path.

1. `EnumToEnum_SameUnderlyingType_AllValuesDefinedOnDest_Maps`
2. `EnumToEnum_SourceValueNotDefinedOnDest_ThrowsAtlasMappingException_AtRuntime`
3. `EnumToEnum_DifferentUnderlyingTypes_ByteToInt_Maps`
4. `EnumToString_DefinedValue_ReturnsVerbatimMemberName`
5. `EnumToString_UndefinedValueCastFromInt_ReturnsNull`
6. `StringToEnum_ExactCaseMatch_Maps`
7. `StringToEnum_CaseMismatch_ThrowsAtlasMappingException`
8. `StringToEnum_UnrecognizedString_ThrowsAtlasMappingException`
9. `EnumToUnderlyingNumeric_ReturnsUnderlyingInt`
10. `NullableEnum_NullSource_NullableDest_PreservesNull`

### 8.3 `tests/Atlas.Tests/EnumExplicitMapTests.cs` (12 tests)

End-to-end through `IMapper.Map<>` after a `CreateMap<E1, E2>()` with enum methods.

ByValue (5):
1. `CreateMap_NoEnumMethods_DefaultsToByValue_AllValuesDefinedOnDest_Maps`
2. `ByValue_WithMapValue_OverrideWinsOverStrategy`
3. `ByValue_WithIgnore_ProducesDefaultDestValue`
4. `ByValue_WithFallback_UnmatchedSourceUsesFallback`
5. `ByValue_NoFallback_SourceValueNotDefinedOnDest_ThrowsAtlasMappingException`

ByName (5):
6. `ByName_DefaultCaseSensitive_SameNameSameCase_Maps`
7. `ByName_DefaultCaseSensitive_DifferentCase_ThrowsAtlasMappingException`
8. `ByName_IgnoreCaseTrue_DifferentCase_Maps`
9. `ByName_WithMapValue_OverrideWinsOverNameMatch`
10. `ByName_NoFallback_NoNameMatch_ThrowsAtlasMappingException`

Precedence (2):
11. `Precedence_MapValue_Beats_Ignore_Beats_Strategy_Beats_Fallback`
12. `UndefinedSourceValueCastFromInt_ThrowsAtlasMappingException_RegardlessOfFallback`

### 8.4 `tests/Atlas.Tests/EnumValidationTests.cs` (10 tests)

Always-on layer (5):
1. `MapValue_SourceValueNotDefinedOnSourceEnum_AssertConfig_Throws`
2. `MapValue_DestValueNotDefinedOnDestEnum_AssertConfig_Throws`
3. `Ignore_SourceValueNotDefinedOnSourceEnum_AssertConfig_Throws`
4. `WithFallback_DestValueNotDefinedOnDestEnum_AssertConfig_Throws`
5. `Ignore_WhenDefaultDstIsNotDefined_AssertConfig_Throws_TheFootGunGuard` (§6.3)

Strict-mode (5):
6. `EnableEnumMappingValidation_NotCalled_GapsInCoverage_AssertConfig_Passes`
7. `EnableEnumMappingValidation_GapInCoverage_AssertConfig_ThrowsListsAllUncoveredValues`
8. `EnableEnumMappingValidation_WithFallback_AllValuesCovered_Passes`
9. `EnableEnumMappingValidation_RegisteredMapWithNoEnumMethods_DefaultByValueAppliesToValidation`
10. `EnableEnumMappingValidation_DoesNotValidate_StringToEnumOrEnumToString_AutoConversions`

### 8.5 `tests/Atlas.Tests/MapperEnumTests.cs` (6 tests)

End-to-end via `IMapper.Map<TDest>(source)` exercising enum properties on object DTOs.

1. `Map_ObjectWithEnumProperty_AutoConverts_SameUnderlyingType`
2. `Map_ObjectWithNullableEnumProperty_NullSource_PreservesNull`
3. `Map_ObjectWithStringPropertyToEnumProperty_AutoConvertsViaName`
4. `Map_ObjectWithEnumProperty_RegisteredMapWithMapByName_UsesNameStrategy`
5. `Map_ObjectWithEnumProperty_RegisteredMapWithFallback_UnmatchedUsesFallback`
6. `Map_NestedDtoWithEnumProperty_RoutesThroughRegisteredEnumMap`

### 8.6 Test scaffolding

The existing `AssertExpression` helper (hoisted into `Atlas.Tests/Internal/` during the inheritance feature) is available if any whitebox tests on `BuildEnumLambda` are needed. None are planned for v1 — end-to-end behavior tests give better signal than tree-shape tests for switch-expression compilation.

---

## 9. Risks & Open Questions

These are flagged for the implementing session to verify or surface alternatives if my assumption is wrong.

**R1 — Registered enum map vs auto-conversion precedence.** §7.3 assumes that when a property's source/dest are both enums AND a `CreateMap<E1, E2>()` is registered, the property routes to the registered map (not the auto-conversion path). This is the existing behavior for object types via `MapperRegistry` lookup. Implementer must verify the property-mapping path in `ExecutionPlanBuilder` checks the registry *before* falling through to `EnumConversions`. If the order is reversed, registered enum maps would be ignored at the property level — a silent correctness bug.

**R2 — `Expression.Constant` for boxed enum overrides.** §5.2 stores `PerValueOverrides` as `Dictionary<object, object>` (boxed). At compile time, §7.2 unboxes back to the typed enum value via `Expression.Constant(action.DestValue, dstType)`. This works because `Expression.Constant(object, Type)` accepts boxed value-types and unboxes on read. Implementer should add at least one test confirming a `byte`-backed enum and a `long`-backed enum both compile correctly.

**R3 — `[Flags]` enum behavior is implicit.** §7.2 iterates `Enum.GetValues(srcType)`, which returns the SINGLE-bit defined values for `[Flags]` enums, not combinations. A source value of `FilePermissions.Read | FilePermissions.Write` (which is itself not a defined name) falls into the default case and throws "source value not defined." This is correct per Section 1's "throw on undefined" posture but is a **deliberate limitation** — combination-of-flags mapping is out of scope for v1. README should call this out so users with `[Flags]` enums declare per-combination `MapValue(Read | Write, ...)` overrides explicitly.

**R4 — `string → enum` precomputed dictionary cache lifetime.** §7.3 says the auto-conversion path precomputes a `Dictionary<string, dstType>` per dst enum type. The cache should hang off `MapperConfiguration` (which is per-config singleton), not be a static field on `EnumConversions` (which would leak across `MapperConfiguration` instances and never get GC'd). Implementer should plumb the cache through the `MapperConfiguration` instance via constructor injection or a setter on `EnumConversions.BuildConversion`.

**R5 — Validator iteration order.** §6 strict-mode iterates "every typemap where source AND dest are both enum." Iteration order should follow `MapperRegistry`'s registration order so error messages are reproducible across runs. Implementer must not introduce dictionary-iteration-order dependencies in error message construction.

**R6 — Nullable enum at the property boundary.** §7.4 assumes the existing v1 nullable wrapper handles enum value types as it does for primitives. The wrapper is type-agnostic in principle, but enum-specific tests (test #10 in §8.2) need to confirm. If the wrapper doesn't fire for enums, a small adjustment may be needed.

**R7 — `Ignore` foot-gun guard placement.** §6.3 specifies the guard runs in `ConfigurationValidator` (because `EnumMapConfig` is type-erased and doesn't know `dstType`). Implementer must not be tempted to add the check inside `EnumMapConfig.AddIgnore` — it's structurally impossible there. If a future refactor passes `dstType` into `EnumMapConfig`, the guard could move; for v1, validator is the right home.

---

## 10. Appendix A — Worked Example

End-to-end trace of one mapping declaration through configuration → compilation → runtime.

### 10.1 Source code

```csharp
public enum LegacyStatus { Pending = 1, Active = 2, Internal = 3 }
public enum OrderStatusV2 { Active = 1, Cancelled = 2, Refunded = 3 }

public class StatusProfile : MapperProfile
{
    public StatusProfile()
    {
        CreateMap<LegacyStatus, OrderStatusV2>()
            .MapByName(ignoreCase: true)
            .MapValue(LegacyStatus.Pending, OrderStatusV2.Active)
            .Ignore(LegacyStatus.Internal)
            .WithFallback(OrderStatusV2.Cancelled);
    }
}
```

### 10.2 Configuration phase

Step-by-step calls to `EnumMapConfig`:

| Call | `EnumMapConfig` mutation |
|---|---|
| `MapByName(ignoreCase: true)` | `Strategy = ByName`, `IgnoreCase = true`, `StrategyExplicitlySet = true` |
| `MapValue(Pending, Active)` | `PerValueOverrides[Pending] = Active` |
| `Ignore(Internal)` | `IgnoredSourceValues.Add(Internal)` |
| `WithFallback(Cancelled)` | `HasFallback = true`, `FallbackValue = Cancelled` |

`TypeMap.EnumConfig` for the `(LegacyStatus, OrderStatusV2)` typemap is now non-null.

### 10.3 Build phase (validation)

`AssertConfigurationIsValid()` runs:
- Always-on: `Pending` defined on `LegacyStatus`? ✓ `Active` defined on `OrderStatusV2`? ✓ `Internal` defined? ✓ `Cancelled` defined? ✓ Foot-gun guard: `default(OrderStatusV2) = (OrderStatusV2)0` defined? ✗ — but `IgnoredSourceValues` is non-empty (`Internal`), so guard runs and throws.

(For the worked example to succeed, change the dest enum to include `Active = 0`. Updating both enums for clarity:)

```csharp
public enum LegacyStatus     { Pending = 1, Active = 2, Internal = 3 }
public enum OrderStatusV2    { Active  = 0, Cancelled = 1, Refunded = 2 }
```

Now `default(OrderStatusV2) == Active`, defined. Guard passes.

Strict-mode validation (assuming `EnableEnumMappingValidation()` was called):
- For each defined `LegacyStatus` value, run `ResolvePerValue`:
  - `Pending` → step 1 hit → (Hit, Active). Covered.
  - `Active` → step 3 ByName ci → dest "Active" found → (Hit, Active). Covered.
  - `Internal` → step 2 → (Hit, default = Active). Covered.
- All values covered. Validation passes.

### 10.4 Compile phase

`ExecutionPlanBuilder.Build(typeMap)`:
- Both source and dest are enums → `BuildEnumLambda(typeMap)`.
- Iterate `Enum.GetValues(LegacyStatus)` = `[Pending, Active, Internal]`.
- Cases produced:
  - `Pending => Constant(Active, OrderStatusV2)`
  - `Active => Constant(Active, OrderStatusV2)`
  - `Internal => Constant(default(OrderStatusV2) = Active, OrderStatusV2)`
- Default case: throw `AtlasMappingException("Source value is not defined on LegacyStatus")`.

Generated lambda equivalent:
```csharp
(LegacyStatus src) => src switch
{
    LegacyStatus.Pending  => OrderStatusV2.Active,
    LegacyStatus.Active   => OrderStatusV2.Active,
    LegacyStatus.Internal => OrderStatusV2.Active,
    _ => throw new AtlasMappingException("Source value is not defined on LegacyStatus")
};
```

### 10.5 Runtime

`mapper.Map<OrderStatusV2>(LegacyStatus.Pending)` → registry lookup `(LegacyStatus, OrderStatusV2)` → cached `Func<LegacyStatus, OrderStatusV2>` → switch dispatch → `Active`.

`mapper.Map<OrderStatusV2>((LegacyStatus)99)` → switch default → throws `AtlasMappingException`.

---

## 11. Implementation Checklist

Tracked sub-deliverables for the implementing session. Each line is a concrete commit unit.

- [ ] **Task 1.** Add `EnumMappingStrategy` enum + `EnumMapConfig` class to `src/Atlas/Internal/EnumMapConfig.cs`. 10 unit tests in `Internal/EnumMapConfigTests.cs` (§8.1). Green.
- [ ] **Task 2.** Add `EnumConfig` field to `TypeMap.cs`. No behavior change yet; existing tests stay green.
- [ ] **Task 3.** Add 5 enum methods to `IMappingExpression<,>` and `MappingExpression<,>`: `MapByValue`, `MapByName`, `MapValue`, `Ignore(TSource)`, `WithFallback`. Each enforces enum-ness at config time. No compilation logic yet.
- [ ] **Task 4.** Add `EnumValidationEnabled` flag + `EnableEnumMappingValidation()` to `MapperConfigurationExpression`; propagate through `MapperConfiguration` constructor to `ConfigurationValidator`.
- [ ] **Task 5.** Implement `ResolvePerValue` per §6.1 in a shared internal helper class (suggested: `src/Atlas/Internal/EnumResolver.cs`). Single source of truth — both `BuildEnumLambda` (Task 6) and `ConfigurationValidator` (Tasks 8-9) call into it.
- [ ] **Task 6.** Add enum dispatch prologue + `BuildEnumLambda` to `ExecutionPlanBuilder`. End-to-end `MapperEnumTests` skeleton (3 tests minimum) green; full §8.3 test list added incrementally.
- [ ] **Task 7.** Add `EnumConversions` class (`src/Atlas/Internal/EnumConversions.cs`) + `StringToEnumCache` (lives on `MapperConfiguration`). Wire into `ConventionEngine.IsAssignmentCompatible` and the property-conversion path. Tests in `EnumAutoConversionTests.cs` (§8.2) green.
- [ ] **Task 8.** Add always-on enum invariants to `ConfigurationValidator` (§8.4 tests 1-5). Includes the §6.3 foot-gun guard for `Ignore` + undefined `default(dstType)`.
- [ ] **Task 9.** Add strict-mode validation to `ConfigurationValidator` (§8.4 tests 6-10). Iterate registered enum→enum typemaps in `MapperRegistry` order.
- [ ] **Task 10.** Complete `EnumExplicitMapTests` (§8.3 — all 12 tests) and `MapperEnumTests` (§8.5 — all 6 tests). Full test count: 216 + 48 = 264 green.
- [ ] **Task 11.** Update README: add `## Enum surface` section (mirrors `## Inheritance & polymorphism`), refresh coverage table if numbers shift, note the `[Flags]` and projection limitations.
- [ ] **Task 12.** Open PR (Option 2 of `superpowers:finishing-a-development-branch`) with the implementation plan landed first via separate commit on main. Holistic review by code-quality and spec-review subagents before merge.

---
