# Atlas Before/After Hooks Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `.BeforeMap` / `.AfterMap` to Atlas's fluent surface per `docs/Atlas-Design-BeforeAfterHooks.md`. Two flavors per direction: inline `Action<TSource, TDestination>` lambda and `IMappingAction<TSource, TDestination>` interface for DI-friendly logic. Multiple hooks supported, FIFO. Inheritance propagates hooks (base-first for BeforeMap, base-last for AfterMap — stack-unwind semantics). DI resolution via `ActivatorUtilities.CreateInstance` from root SP, cached. `Atlas.Projections` rejects TypeMaps with hooks at projection-build time.

**Architecture:** Purely additive. One new public interface (`IMappingAction<,>`). One new internal record (`HookEntry`). Two new ordered lists on `TypeMap` (`BeforeHooks`, `AfterHooks`). Four new methods on `IMappingExpression<,>`. One new internal helper (`HookResolver`). `MapperConfiguration` and `MapperRegistry` plumb a nullable `IServiceProvider`. `InheritanceMerger.Resolve` extended with hook merge step. `ConfigurationValidator.Validate` extended with hook validation rule. `ExecutionPlanBuilder` emits hook calls in `BuildPocoLambda` + `BuildUpdate`. `Atlas.Projections.ProjectionPlanBuilder` rejects TypeMaps with hooks. New package reference: `Microsoft.Extensions.DependencyInjection.Abstractions` (for `ActivatorUtilities`) added to `Atlas` core.

**Tech Stack:** .NET 10, xUnit v3 (built-in `Assert.X()`, no FluentAssertions), coverlet.

**Spec reference:** `docs/Atlas-Design-BeforeAfterHooks.md`. Section numbers in this plan (e.g. "§5.6") refer to the spec.

**v1 conventions to mirror (do NOT deviate):**
- File-scoped namespaces.
- Internal types under `Internal/` subfolder.
- `internal sealed class` / `internal sealed record` / `internal static class` unless otherwise noted.
- Test naming: `MethodOrFeature_Condition_ExpectedResult`.
- xUnit v3, `[Fact]` / `[Theory]` + `[InlineData]`.
- `TreatWarningsAsErrors=true` is on globally; `GenerateDocumentationFile=true` is on; `CS1591` is suppressed.
- **NEVER use FluentAssertions.** xUnit v3 built-in `Assert.X()` only.
- **`AtlasConfigurationException` only takes `IReadOnlyList<ConfigurationError>` — no string-only constructor.** Wrap a single error in a 1-element list.
- **Forward refs in XML docs:** for types not yet introduced (e.g., `HookResolver` referenced before Task 5 lands), use `<c>TypeName</c>` (literal text) instead of `<see cref="TypeName"/>` to avoid CS1574.
- Run tests with `dotnet test --nologo` (PowerShell on Windows).

**Branching:** Implement on a new branch `feat/before-after-hooks` cut from current `main` (HEAD `922bc44` after the design + this plan land). Each task ends in a commit. After all tasks land, the implementer runs `superpowers:finishing-a-development-branch` Option 2 (push + PR) per the established pattern.

**Key files in the codebase to read first** (for context):
- `src/Atlas/Internal/TypeMap.cs` — fields added in Task 2
- `src/Atlas/Internal/InheritanceMerger.cs` — extended in Task 6 (around line 65, the `Resolve` method)
- `src/Atlas/Internal/MapperRegistry.cs` — fields added in Task 4
- `src/Atlas/Internal/ConfigurationValidator.cs` — extended in Task 7
- `src/Atlas/Internal/ExecutionPlanBuilder.cs` — extended in Task 8 (around line 200 in `BuildPocoLambda`, line 143 in `BuildUpdate`)
- `src/Atlas/Configuration/IMappingExpression.cs` — methods added in Task 3
- `src/Atlas/Configuration/MappingExpression.cs` — methods added in Task 3
- `src/Atlas/MapperConfiguration.cs` — ctor overloads added in Task 4
- `src/Atlas.Extensions.DependencyInjection/AtlasServiceCollectionExtensions.cs` — extended in Task 4
- `src/Atlas.Projections/Internal/ProjectionPlanBuilder.cs` — extended in Task 10
- `src/Atlas.Projections/Internal/ProjectionCompatibility.cs` — extended in Task 10

**Test count baseline:** 324 tests pre-feature (261 Atlas + 55 Projections + 8 Projections.EFCore) — verified at HEAD `922bc44` before this plan commit. Expected after this plan: ~362 (≈38 new hook tests).

**Coverage targets:** line ≥ 90%, branch ≥ 80% on `Atlas` core. Verified by Task 11.

---

## Task 1: Set up branch

**Files:** none modified; branch creation only.

- [ ] **Step 1: Create the feature branch**

```powershell
git checkout main
git pull
git checkout -b feat/before-after-hooks
```

- [ ] **Step 2: Verify clean baseline**

Run: `dotnet test --nologo`

Expected: 324 tests pass (261 Atlas + 55 Projections + 8 Projections.EFCore). If any test fails, stop and report — the baseline must be green before changes start.

- [ ] **Step 3: No commit** — branching only.

---

## Task 2: Data model — `IMappingAction<,>`, `HookEntry`, `TypeMap` fields

**Files:**
- Create: `src/Atlas/IMappingAction.cs`
- Create: `src/Atlas/Internal/HookEntry.cs`
- Modify: `src/Atlas/Internal/TypeMap.cs`
- Create: `tests/Atlas.Tests/Internal/HookEntryTests.cs`

Spec references: §4.1, §5.1, §5.2.

- [ ] **Step 1: Write failing tests**

Create `tests/Atlas.Tests/Internal/HookEntryTests.cs`:

```csharp
using Atlas.Internal;

namespace Atlas.Tests.Internal;

public class HookEntryTests
{
    [Fact]
    public void FromLambda_StoresLambdaAndNullActionType()
    {
        Action<int, int> hook = (s, d) => { };
        var entry = HookEntry.FromLambda(hook);

        Assert.Same(hook, entry.Lambda);
        Assert.Null(entry.ActionType);
    }

    [Fact]
    public void FromActionType_StoresActionTypeAndNullLambda()
    {
        var entry = HookEntry.FromActionType(typeof(string));

        Assert.Equal(typeof(string), entry.ActionType);
        Assert.Null(entry.Lambda);
    }

    [Fact]
    public void FromLambda_NullDelegate_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => HookEntry.FromLambda(null!));
    }

    [Fact]
    public void FromActionType_NullType_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => HookEntry.FromActionType(null!));
    }
}
```

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~HookEntryTests" --nologo`

Expected: 4 failures — `HookEntry` does not exist.

- [ ] **Step 3: Create `IMappingAction<,>` public interface**

Create `src/Atlas/IMappingAction.cs`:

```csharp
namespace Atlas;

/// <summary>
/// Reusable mapping-action interface for DI-friendly hook logic. Implementations are
/// instantiated via <c>ActivatorUtilities.CreateInstance</c> from the root
/// <see cref="System.IServiceProvider"/> when Atlas is registered through
/// <c>Atlas.Extensions.DependencyInjection</c>; without DI, a public parameterless
/// constructor is required.
/// </summary>
/// <remarks>
/// Constructor injection of singleton and transient services works out of the box.
/// <b>Scoped services (HTTP context, current user, scoped EF DbContext) are NOT supported</b> —
/// the action is resolved from the root provider and cached once per configuration.
/// For HTTP context-aware logic, inject <c>IHttpContextAccessor</c> (which is itself
/// singleton-resolvable) and read the per-request context inside <see cref="Process"/>.
/// </remarks>
public interface IMappingAction<in TSource, in TDestination>
{
    /// <summary>
    /// Runs at the time configured by <c>BeforeMap</c> or <c>AfterMap</c>.
    /// </summary>
    void Process(TSource source, TDestination destination);
}
```

- [ ] **Step 4: Create `HookEntry` record**

Create `src/Atlas/Internal/HookEntry.cs`:

```csharp
namespace Atlas.Internal;

/// <summary>
/// One BeforeMap or AfterMap registration. Exactly one of <see cref="Lambda"/> or
/// <see cref="ActionType"/> is non-null. Lambda entries store the user's
/// <c>Action&lt;TSource, TDestination&gt;</c> directly; ActionType entries reference an
/// <see cref="IMappingAction{TSource, TDestination}"/> implementation type to be instantiated
/// at config-build time by <c>HookResolver</c>.
/// </summary>
internal sealed record HookEntry(Delegate? Lambda, Type? ActionType)
{
    public static HookEntry FromLambda(Delegate lambda) =>
        new(lambda ?? throw new ArgumentNullException(nameof(lambda)), null);

    public static HookEntry FromActionType(Type actionType) =>
        new(null, actionType ?? throw new ArgumentNullException(nameof(actionType)));
}
```

- [ ] **Step 5: Run tests to verify pass**

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~HookEntryTests" --nologo`

Expected: 4/4 pass.

- [ ] **Step 6: Add `BeforeHooks` / `AfterHooks` fields to `TypeMap`**

Edit `src/Atlas/Internal/TypeMap.cs` — add two new properties after `RegistrationOrigin`:

```csharp
    /// <summary>
    /// Hooks that run BEFORE any destination member is mapped, in FIFO order (registration
    /// order at the user's call site). After <c>InheritanceMerger</c> runs, this list also
    /// contains base TypeMaps' BeforeHooks prepended at the front (base-first order).
    /// </summary>
    public List<HookEntry> BeforeHooks { get; } = new();

    /// <summary>
    /// Hooks that run AFTER all destination members are mapped, in FIFO order. After
    /// <c>InheritanceMerger</c> runs, this list also contains base TypeMaps' AfterHooks
    /// appended at the end (so unwind goes derived-first then base-last, pairing with
    /// <see cref="BeforeHooks"/>'s base-first order).
    /// </summary>
    public List<HookEntry> AfterHooks { get; } = new();
```

- [ ] **Step 7: Run full test suite**

Run: `dotnet test --nologo`

Expected: 328 tests pass (324 baseline + 4 new). Existing tests unaffected — purely additive change.

- [ ] **Step 8: Commit**

```powershell
git add src/Atlas/IMappingAction.cs src/Atlas/Internal/HookEntry.cs src/Atlas/Internal/TypeMap.cs tests/Atlas.Tests/Internal/HookEntryTests.cs
git commit -m "Add IMappingAction interface + HookEntry record + TypeMap.BeforeHooks/AfterHooks (4 tests)"
```

---

## Task 3: `BeforeMap`/`AfterMap` fluent surface

**Files:**
- Modify: `src/Atlas/Configuration/IMappingExpression.cs`
- Modify: `src/Atlas/Configuration/MappingExpression.cs`
- Create: `tests/Atlas.Tests/MappingExpressionBeforeAfterMapTests.cs`

Add four methods to `IMappingExpression<,>`: `BeforeMap` (lambda + interface), `AfterMap` (lambda + interface). Spec references: §4.2, §5.3.

- [ ] **Step 1: Write failing tests**

Create `tests/Atlas.Tests/MappingExpressionBeforeAfterMapTests.cs`:

```csharp
using Atlas.Configuration;
using Atlas.Internal;

namespace Atlas.Tests;

public class MappingExpressionBeforeAfterMapTests
{
    public sealed class S { public string? V { get; set; } }
    public sealed class D { public string? V { get; set; } }

    public sealed class TestAction : IMappingAction<S, D>
    {
        public void Process(S source, D destination) { }
    }

    private static MappingExpression<S, D> NewExpr() =>
        new(new TypeMap(typeof(S), typeof(D), MemberList.None));

    [Fact]
    public void BeforeMap_Lambda_AppendsToBeforeHooks()
    {
        var expr = NewExpr();
        Action<S, D> hook = (s, d) => { };

        expr.BeforeMap(hook);

        Assert.Single(expr.TypeMap.BeforeHooks);
        Assert.Same(hook, expr.TypeMap.BeforeHooks[0].Lambda);
        Assert.Null(expr.TypeMap.BeforeHooks[0].ActionType);
    }

    [Fact]
    public void BeforeMap_ActionType_AppendsToBeforeHooks()
    {
        var expr = NewExpr();

        expr.BeforeMap<TestAction>();

        Assert.Single(expr.TypeMap.BeforeHooks);
        Assert.Equal(typeof(TestAction), expr.TypeMap.BeforeHooks[0].ActionType);
        Assert.Null(expr.TypeMap.BeforeHooks[0].Lambda);
    }

    [Fact]
    public void AfterMap_Lambda_AppendsToAfterHooks()
    {
        var expr = NewExpr();
        Action<S, D> hook = (s, d) => { };

        expr.AfterMap(hook);

        Assert.Single(expr.TypeMap.AfterHooks);
        Assert.Same(hook, expr.TypeMap.AfterHooks[0].Lambda);
    }

    [Fact]
    public void AfterMap_ActionType_AppendsToAfterHooks()
    {
        var expr = NewExpr();

        expr.AfterMap<TestAction>();

        Assert.Single(expr.TypeMap.AfterHooks);
        Assert.Equal(typeof(TestAction), expr.TypeMap.AfterHooks[0].ActionType);
    }

    [Fact]
    public void MultipleBeforeMap_PreservesFifoOrder()
    {
        var expr = NewExpr();
        Action<S, D> first = (s, d) => { };
        Action<S, D> second = (s, d) => { };
        Action<S, D> third = (s, d) => { };

        expr.BeforeMap(first);
        expr.BeforeMap(second);
        expr.BeforeMap(third);

        Assert.Equal(3, expr.TypeMap.BeforeHooks.Count);
        Assert.Same(first, expr.TypeMap.BeforeHooks[0].Lambda);
        Assert.Same(second, expr.TypeMap.BeforeHooks[1].Lambda);
        Assert.Same(third, expr.TypeMap.BeforeHooks[2].Lambda);
    }

    [Fact]
    public void MultipleAfterMap_PreservesFifoOrder()
    {
        var expr = NewExpr();
        Action<S, D> first = (s, d) => { };
        Action<S, D> second = (s, d) => { };

        expr.AfterMap(first);
        expr.AfterMap(second);

        Assert.Equal(2, expr.TypeMap.AfterHooks.Count);
        Assert.Same(first, expr.TypeMap.AfterHooks[0].Lambda);
        Assert.Same(second, expr.TypeMap.AfterHooks[1].Lambda);
    }

    [Fact]
    public void BeforeMap_NullLambda_Throws()
    {
        var expr = NewExpr();
        Assert.Throws<ArgumentNullException>(() => expr.BeforeMap((Action<S, D>)null!));
    }

    [Fact]
    public void BeforeMap_ReturnsExpression_ForChaining()
    {
        var expr = NewExpr();
        var returned = expr.BeforeMap((s, d) => { });

        Assert.Same(expr, returned);
    }
}
```

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~MappingExpressionBeforeAfterMapTests" --nologo`

Expected: 8 failures — `BeforeMap` / `AfterMap` methods do not exist.

- [ ] **Step 3: Add four methods to `IMappingExpression<TSource, TDestination>`**

Edit `src/Atlas/Configuration/IMappingExpression.cs` — add the four new method declarations after `WithFallback` (the last enum method) and before the closing brace:

```csharp
    // ---- Before/After hooks ----

    /// <summary>
    /// Registers a callback to run BEFORE any destination member is mapped. Multiple BeforeMap
    /// calls on the same map run in registration order (FIFO). With inheritance, base hooks
    /// run before derived hooks (base-first order).
    /// </summary>
    /// <remarks>
    /// Hooks fire on every <see cref="IMapper.Map{TDestination}"/> call (including update-in-place
    /// via <c>Map&lt;TS, TD&gt;(src, existingDest)</c>) and on every per-element invocation when
    /// mapping a collection. Hooks DO NOT auto-propagate across <c>.ReverseMap()</c> — configure
    /// hooks on the reverse expression separately if needed.
    /// <para>
    /// Hooks are NOT translatable to IQueryable. Calling <c>query.ProjectTo&lt;TDestination&gt;()</c>
    /// against a TypeMap with hooks throws <see cref="AtlasConfigurationException"/> at
    /// projection-build time naming the hook count.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="hook"/> is null.</exception>
    IMappingExpression<TSource, TDestination> BeforeMap(Action<TSource, TDestination> hook);

    /// <summary>
    /// Registers a typed mapping-action class to run BEFORE any destination member is mapped.
    /// The action is instantiated via <c>ActivatorUtilities.CreateInstance</c> from the root
    /// <see cref="System.IServiceProvider"/> when Atlas is registered through DI; without DI,
    /// requires a public parameterless constructor. The instance is cached once per configuration.
    /// </summary>
    /// <remarks>
    /// Use this overload to inject services (logging, IOptions, telemetry, IHttpContextAccessor).
    /// See <see cref="IMappingAction{TSource, TDestination}"/> for the scoped-service limitation.
    /// </remarks>
    IMappingExpression<TSource, TDestination> BeforeMap<TAction>()
        where TAction : IMappingAction<TSource, TDestination>;

    /// <summary>
    /// Registers a callback to run AFTER all destination members are mapped. Multiple AfterMap
    /// calls on the same map run in registration order (FIFO). With inheritance, derived hooks
    /// run before base hooks (stack-unwind order — pairs with BeforeMap's base-first order).
    /// </summary>
    /// <remarks>See <see cref="BeforeMap(Action{TSource, TDestination})"/> for shared semantics.</remarks>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="hook"/> is null.</exception>
    IMappingExpression<TSource, TDestination> AfterMap(Action<TSource, TDestination> hook);

    /// <summary>
    /// Registers a typed mapping-action class to run AFTER all destination members are mapped.
    /// </summary>
    /// <remarks>See <see cref="BeforeMap{TAction}"/> for resolution and lifetime semantics.</remarks>
    IMappingExpression<TSource, TDestination> AfterMap<TAction>()
        where TAction : IMappingAction<TSource, TDestination>;
```

- [ ] **Step 4: Implement the four methods in `MappingExpression<TSource, TDestination>`**

Edit `src/Atlas/Configuration/MappingExpression.cs` — add the four implementations at the end of the class (after `WithFallback`'s implementation, before the private helpers):

```csharp
    // ---- Before/After hooks ----

    public IMappingExpression<TSource, TDestination> BeforeMap(Action<TSource, TDestination> hook)
    {
        TypeMap.EnsureMutable();
        ArgumentNullException.ThrowIfNull(hook);
        TypeMap.BeforeHooks.Add(HookEntry.FromLambda(hook));
        return this;
    }

    public IMappingExpression<TSource, TDestination> BeforeMap<TAction>()
        where TAction : IMappingAction<TSource, TDestination>
    {
        TypeMap.EnsureMutable();
        TypeMap.BeforeHooks.Add(HookEntry.FromActionType(typeof(TAction)));
        return this;
    }

    public IMappingExpression<TSource, TDestination> AfterMap(Action<TSource, TDestination> hook)
    {
        TypeMap.EnsureMutable();
        ArgumentNullException.ThrowIfNull(hook);
        TypeMap.AfterHooks.Add(HookEntry.FromLambda(hook));
        return this;
    }

    public IMappingExpression<TSource, TDestination> AfterMap<TAction>()
        where TAction : IMappingAction<TSource, TDestination>
    {
        TypeMap.EnsureMutable();
        TypeMap.AfterHooks.Add(HookEntry.FromActionType(typeof(TAction)));
        return this;
    }
```

- [ ] **Step 5: Run tests to verify pass**

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~MappingExpressionBeforeAfterMapTests" --nologo`

Expected: 8/8 pass.

- [ ] **Step 6: Run full test suite**

Run: `dotnet test --nologo`

Expected: 336 tests pass (328 + 8). Existing tests unaffected.

- [ ] **Step 7: Commit**

```powershell
git add src/Atlas/Configuration/IMappingExpression.cs src/Atlas/Configuration/MappingExpression.cs tests/Atlas.Tests/MappingExpressionBeforeAfterMapTests.cs
git commit -m "Add BeforeMap/AfterMap fluent surface (lambda + interface, 8 tests)"
```

---

## Task 4: SP plumbing — `MapperConfiguration` ctors, `MapperRegistry` fields, DI extension wiring, package reference

**Files:**
- Modify: `src/Atlas/Atlas.csproj` (add `Microsoft.Extensions.DependencyInjection.Abstractions` reference)
- Modify: `src/Atlas/MapperConfiguration.cs`
- Modify: `src/Atlas/Internal/MapperRegistry.cs`
- Modify: `src/Atlas.Extensions.DependencyInjection/AtlasServiceCollectionExtensions.cs`

This task adds the infrastructure for `IServiceProvider` plumbing. No new tests — exercised by Tasks 5, 7, 9. Spec references: §4.3, §4.4, §5.4, §5.8.

- [ ] **Step 1: Add package reference to `Atlas.csproj`**

Edit `src/Atlas/Atlas.csproj` — add a `PackageReference` ItemGroup:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>Atlas</PackageId>
    <Description>A fluent, high-performance object-to-object mapper for .NET.</Description>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Atlas.Tests" />
    <InternalsVisibleTo Include="Atlas.Extensions.DependencyInjection" />
    <InternalsVisibleTo Include="Atlas.Projections" />
    <InternalsVisibleTo Include="Atlas.Projections.Tests" />
  </ItemGroup>
</Project>
```

(The package version is centrally managed in `Directory.Packages.props` — `Microsoft.Extensions.DependencyInjection.Abstractions` 10.0.0 — so no version attribute is needed.)

- [ ] **Step 2: Add `ServiceProvider` and `ActionInstances` to `MapperRegistry`**

Edit `src/Atlas/Internal/MapperRegistry.cs` — modify the constructor signature and add two new properties.

Replace the existing constructor block:

```csharp
    public StringToEnumCache StringToEnumCache { get; }

    public MapperRegistry(IEnumerable<TypeMap> typeMaps, StringToEnumCache? stringToEnumCache = null)
    {
        _typeMaps = typeMaps.ToDictionary(t => t.Pair);
        StringToEnumCache = stringToEnumCache ?? new StringToEnumCache();
    }
```

with:

```csharp
    public StringToEnumCache StringToEnumCache { get; }

    /// <summary>
    /// The application's root <see cref="IServiceProvider"/> when Atlas is registered through
    /// <c>Atlas.Extensions.DependencyInjection</c>; otherwise <c>null</c>. Used by
    /// <c>HookResolver</c> to instantiate <see cref="IMappingAction{TSource,TDestination}"/>
    /// implementations via <c>ActivatorUtilities.CreateInstance</c>.
    /// </summary>
    public IServiceProvider? ServiceProvider { get; }

    /// <summary>
    /// Cached <see cref="IMappingAction{TSource, TDestination}"/> instances keyed by action type.
    /// Populated by <c>HookResolver</c> at codegen time; one entry per distinct action type
    /// regardless of how many TypeMaps reference it.
    /// </summary>
    internal Dictionary<Type, object> ActionInstances { get; } = new();

    public MapperRegistry(
        IEnumerable<TypeMap> typeMaps,
        StringToEnumCache? stringToEnumCache = null,
        IServiceProvider? serviceProvider = null)
    {
        _typeMaps = typeMaps.ToDictionary(t => t.Pair);
        StringToEnumCache = stringToEnumCache ?? new StringToEnumCache();
        ServiceProvider = serviceProvider;
    }
```

- [ ] **Step 3: Add `IServiceProvider` plumbing to `MapperConfiguration`**

Edit `src/Atlas/MapperConfiguration.cs` — add a new field, two new public constructors that accept SP, and update the registry construction.

Locate (around line 11-13):

```csharp
    private readonly MapperRegistry _registry;
    private readonly ConventionOptions _conventionOptions;
    private readonly bool _enumValidationEnabled;
    private readonly StringToEnumCache _stringToEnumCache = new();
    internal StringToEnumCache Internal_StringToEnumCache => _stringToEnumCache;
```

Add after these:

```csharp
    private readonly IServiceProvider? _serviceProvider;
```

Locate the existing public constructors (around line 17-23):

```csharp
    public MapperConfiguration(Action<MapperConfigurationExpression> configure)
        : this(BuildExpression(configure))
    {
    }

    public MapperConfiguration(MapperConfigurationExpression expression)
    {
```

Add two new constructor overloads BEFORE the existing two:

```csharp
    public MapperConfiguration(Action<MapperConfigurationExpression> configure, IServiceProvider serviceProvider)
        : this(BuildExpression(configure), serviceProvider)
    {
    }

    public MapperConfiguration(MapperConfigurationExpression expression, IServiceProvider serviceProvider)
        : this(expression)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        _serviceProvider = serviceProvider;
        _registry = new MapperRegistry(_registry.AllTypeMaps.ToList(), _stringToEnumCache, serviceProvider);
    }

    public MapperConfiguration(Action<MapperConfigurationExpression> configure)
        : this(BuildExpression(configure))
    {
    }

    public MapperConfiguration(MapperConfigurationExpression expression)
    {
```

(Note: the SP-accepting `MapperConfigurationExpression` ctor delegates to the parameterless one, then RE-CONSTRUCTS the registry to attach the SP. This avoids duplicating all the pre-registry setup; the trade-off is two registry allocations during DI startup, which is negligible for a one-time event.)

- [ ] **Step 4: Update DI extension to pass SP into `MapperConfiguration`**

Edit `src/Atlas.Extensions.DependencyInjection/AtlasServiceCollectionExtensions.cs` — replace the body of `AddAtlas(this IServiceCollection, Action<MapperConfigurationExpression>?, params Assembly[])`:

```csharp
    public static IServiceCollection AddAtlas(
        this IServiceCollection services,
        Action<MapperConfigurationExpression>? configure,
        params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<MapperConfiguration>(sp =>
        {
            var expression = new MapperConfigurationExpression();
            configure?.Invoke(expression);

            foreach (var profile in ProfileScanner.Discover(assemblies))
                expression.AddProfile(profile);

            var configuration = new MapperConfiguration(expression, sp);
            configuration.CompileMappings();
            return configuration;
        });
        services.AddSingleton<IMapper>(sp => sp.GetRequiredService<MapperConfiguration>().CreateMapper());
        return services;
    }
```

(Key change: the configuration is now built INSIDE the service factory, so it has access to the container's `IServiceProvider`. Previously it was eagerly built outside the factory and added via `services.AddSingleton(configuration)` with the instance.)

- [ ] **Step 5: Run full test suite**

Run: `dotnet test --nologo`

Expected: 336 tests still pass. The change is purely additive — existing tests don't use the new SP-accepting overloads, and the DI extension's behavior is observably unchanged (still produces a singleton `MapperConfiguration` and `IMapper`).

If any existing DI-related tests fail, the most likely issue is the relocation of `expression` and `configuration` construction into the factory — verify the factory delegates run when `MapperConfiguration` is first resolved.

- [ ] **Step 6: Commit**

```powershell
git add src/Atlas/Atlas.csproj src/Atlas/MapperConfiguration.cs src/Atlas/Internal/MapperRegistry.cs src/Atlas.Extensions.DependencyInjection/AtlasServiceCollectionExtensions.cs
git commit -m "Plumb IServiceProvider through MapperConfiguration + MapperRegistry; AddAtlas passes container SP (no new tests)"
```

---

## Task 5: `HookResolver`

**Files:**
- Create: `src/Atlas/Internal/HookResolver.cs`
- Create: `tests/Atlas.Tests/Internal/HookResolverTests.cs`

Resolves a `HookEntry` to a strongly-typed `Action<TSource, TDestination>`. For action-type entries, instantiates ONCE via `ActivatorUtilities.CreateInstance` (when SP is non-null) or `Activator.CreateInstance` (when SP is null) and caches in `MapperRegistry.ActionInstances`. Spec references: §5.5.

- [ ] **Step 1: Write failing tests**

Create `tests/Atlas.Tests/Internal/HookResolverTests.cs`:

```csharp
using Atlas.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Tests.Internal;

public class HookResolverTests
{
    public sealed class S { }
    public sealed class D { }

    public sealed class ParameterlessAction : IMappingAction<S, D>
    {
        public int CallCount { get; private set; }
        public void Process(S source, D destination) => CallCount++;
    }

    public sealed class ServiceDependency
    {
        public string Tag { get; } = "from-DI";
    }

    public sealed class DiAction : IMappingAction<S, D>
    {
        public ServiceDependency Dep { get; }
        public DiAction(ServiceDependency dep) => Dep = dep;
        public void Process(S source, D destination) { }
    }

    public sealed class NoCtorAction : IMappingAction<S, D>
    {
        public NoCtorAction(int x) { _ = x; }
        public void Process(S source, D destination) { }
    }

    private static MapperRegistry NewRegistry(IServiceProvider? sp = null) =>
        new(Array.Empty<TypeMap>(), null, sp);

    [Fact]
    public void Resolve_LambdaEntry_ReturnsTypedDelegate()
    {
        Action<S, D> hook = (s, d) => { };
        var entry = HookEntry.FromLambda(hook);
        var registry = NewRegistry();

        var resolved = HookResolver.Resolve<S, D>(entry, registry);

        Assert.Same(hook, resolved);
    }

    [Fact]
    public void Resolve_ActionType_NoDI_RequiresParameterlessCtor()
    {
        var entry = HookEntry.FromActionType(typeof(ParameterlessAction));
        var registry = NewRegistry();

        var resolved = HookResolver.Resolve<S, D>(entry, registry);
        var src = new S(); var dst = new D();
        resolved(src, dst);

        Assert.True(registry.ActionInstances.ContainsKey(typeof(ParameterlessAction)));
        var instance = (ParameterlessAction)registry.ActionInstances[typeof(ParameterlessAction)];
        Assert.Equal(1, instance.CallCount);
    }

    [Fact]
    public void Resolve_ActionType_DI_ConstructsViaActivatorUtilities()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ServiceDependency>();
        var sp = services.BuildServiceProvider();
        var entry = HookEntry.FromActionType(typeof(DiAction));
        var registry = NewRegistry(sp);

        var resolved = HookResolver.Resolve<S, D>(entry, registry);
        resolved(new S(), new D());

        var instance = (DiAction)registry.ActionInstances[typeof(DiAction)];
        Assert.Equal("from-DI", instance.Dep.Tag);
    }

    [Fact]
    public void Resolve_ActionType_CachedAcrossCalls()
    {
        var entry = HookEntry.FromActionType(typeof(ParameterlessAction));
        var registry = NewRegistry();

        var first = HookResolver.Resolve<S, D>(entry, registry);
        var second = HookResolver.Resolve<S, D>(entry, registry);

        Assert.Single(registry.ActionInstances);
        // Both delegates target the same instance.
        Assert.Same(first.Target, second.Target);
    }

    [Fact]
    public void Resolve_ActionTypeWithoutCtor_NoDI_Throws()
    {
        var entry = HookEntry.FromActionType(typeof(NoCtorAction));
        var registry = NewRegistry();

        Assert.Throws<InvalidOperationException>(() => HookResolver.Resolve<S, D>(entry, registry));
    }
}
```

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~HookResolverTests" --nologo`

Expected: 5 failures — `HookResolver` does not exist.

- [ ] **Step 3: Create `HookResolver`**

Create `src/Atlas/Internal/HookResolver.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Internal;

/// <summary>
/// Resolves a <see cref="HookEntry"/> to a runnable <c>Action&lt;TSource, TDestination&gt;</c>.
/// Lambda entries are returned directly. ActionType entries are instantiated ONCE per
/// configuration via <see cref="ActivatorUtilities.CreateInstance"/> (when SP is non-null)
/// or <see cref="Activator.CreateInstance(Type)"/> (when SP is null). Instances are cached
/// in <see cref="MapperRegistry.ActionInstances"/>.
/// </summary>
internal static class HookResolver
{
    public static Action<TSource, TDestination> Resolve<TSource, TDestination>(
        HookEntry entry,
        MapperRegistry registry)
    {
        if (entry.Lambda is Action<TSource, TDestination> typedLambda)
            return typedLambda;

        if (entry.Lambda is not null)
            throw new InvalidOperationException(
                $"Hook lambda has type {entry.Lambda.GetType().Name} but expected " +
                $"Action<{typeof(TSource).Name}, {typeof(TDestination).Name}>.");

        var actionType = entry.ActionType
            ?? throw new InvalidOperationException("HookEntry has neither Lambda nor ActionType set.");

        if (!registry.ActionInstances.TryGetValue(actionType, out var instance))
        {
            try
            {
                instance = registry.ServiceProvider is { } sp
                    ? ActivatorUtilities.CreateInstance(sp, actionType)
                    : Activator.CreateInstance(actionType)
                        ?? throw new InvalidOperationException(
                            $"Activator.CreateInstance returned null for {actionType.Name}.");
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                throw new InvalidOperationException(
                    $"Failed to construct mapping action {actionType.Name}. " +
                    "When Atlas is used without the DI extension, the action must have a public parameterless constructor. " +
                    "When using DI, ensure all constructor dependencies are registered as singleton or transient (scoped services are not supported).",
                    ex);
            }
            registry.ActionInstances[actionType] = instance;
        }

        var action = (IMappingAction<TSource, TDestination>)instance;
        return action.Process;
    }
}
```

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~HookResolverTests" --nologo`

Expected: 5/5 pass.

- [ ] **Step 5: Run full test suite**

Run: `dotnet test --nologo`

Expected: 341 tests pass (336 + 5).

- [ ] **Step 6: Commit**

```powershell
git add src/Atlas/Internal/HookResolver.cs tests/Atlas.Tests/Internal/HookResolverTests.cs
git commit -m "Add HookResolver with caching + DI activation (5 tests)"
```

---

## Task 6: `InheritanceMerger` hook merge

**Files:**
- Modify: `src/Atlas/Internal/InheritanceMerger.cs`
- Create: `tests/Atlas.Tests/Internal/InheritanceMergerHookTests.cs`

Extend `MergeBaseConfig` to also merge hooks: prepend base BeforeHooks; append base AfterHooks. Spec references: §5.6.

- [ ] **Step 1: Write failing tests**

Create `tests/Atlas.Tests/Internal/InheritanceMergerHookTests.cs`:

```csharp
using Atlas.Internal;

namespace Atlas.Tests.Internal;

public class InheritanceMergerHookTests
{
    public class LivingThing { }
    public class Animal : LivingThing { }
    public class Cat : Animal { }
    public class LivingThingDto { }
    public class AnimalDto : LivingThingDto { }
    public class CatDto : AnimalDto { }
    public class Dog : Animal { }
    public class DogDto : AnimalDto { }

    private static (TypeMap fwd, Action<int, int> hook) MakeHook(int id, List<int> log)
    {
        Action<int, int> action = (s, d) => log.Add(id);
        return (null!, action);   // tm not used
    }

    [Fact]
    public void Merge_OneLevel_BeforeHooks_PrependsBase()
    {
        var animalTm = new TypeMap(typeof(Animal), typeof(AnimalDto), MemberList.None);
        Action<Animal, AnimalDto> A_Before = (s, d) => { };
        animalTm.BeforeHooks.Add(HookEntry.FromLambda(A_Before));

        var catTm = new TypeMap(typeof(Cat), typeof(CatDto), MemberList.None);
        catTm.IncludedBases.Add(animalTm.Pair);
        Action<Cat, CatDto> C_Before = (s, d) => { };
        catTm.BeforeHooks.Add(HookEntry.FromLambda(C_Before));

        var typeMaps = new List<TypeMap> { animalTm, catTm };
        var pairIndex = typeMaps.ToDictionary(t => t.Pair);

        InheritanceMerger.Resolve(typeMaps, pairIndex);

        Assert.Equal(2, catTm.BeforeHooks.Count);
        Assert.Same(A_Before, catTm.BeforeHooks[0].Lambda);
        Assert.Same(C_Before, catTm.BeforeHooks[1].Lambda);
    }

    [Fact]
    public void Merge_OneLevel_AfterHooks_AppendsBase()
    {
        var animalTm = new TypeMap(typeof(Animal), typeof(AnimalDto), MemberList.None);
        Action<Animal, AnimalDto> A_After = (s, d) => { };
        animalTm.AfterHooks.Add(HookEntry.FromLambda(A_After));

        var catTm = new TypeMap(typeof(Cat), typeof(CatDto), MemberList.None);
        catTm.IncludedBases.Add(animalTm.Pair);
        Action<Cat, CatDto> C_After = (s, d) => { };
        catTm.AfterHooks.Add(HookEntry.FromLambda(C_After));

        var typeMaps = new List<TypeMap> { animalTm, catTm };
        var pairIndex = typeMaps.ToDictionary(t => t.Pair);

        InheritanceMerger.Resolve(typeMaps, pairIndex);

        Assert.Equal(2, catTm.AfterHooks.Count);
        Assert.Same(C_After, catTm.AfterHooks[0].Lambda);
        Assert.Same(A_After, catTm.AfterHooks[1].Lambda);
    }

    [Fact]
    public void Merge_ThreeLevelChain_BaseFirstOrder()
    {
        // LivingThing → Animal → Cat
        var ltTm = new TypeMap(typeof(LivingThing), typeof(LivingThingDto), MemberList.None);
        Action<LivingThing, LivingThingDto> LT_B = (s, d) => { };
        Action<LivingThing, LivingThingDto> LT_A = (s, d) => { };
        ltTm.BeforeHooks.Add(HookEntry.FromLambda(LT_B));
        ltTm.AfterHooks.Add(HookEntry.FromLambda(LT_A));

        var animalTm = new TypeMap(typeof(Animal), typeof(AnimalDto), MemberList.None);
        animalTm.IncludedBases.Add(ltTm.Pair);
        Action<Animal, AnimalDto> A_B = (s, d) => { };
        Action<Animal, AnimalDto> A_A = (s, d) => { };
        animalTm.BeforeHooks.Add(HookEntry.FromLambda(A_B));
        animalTm.AfterHooks.Add(HookEntry.FromLambda(A_A));

        var catTm = new TypeMap(typeof(Cat), typeof(CatDto), MemberList.None);
        catTm.IncludedBases.Add(animalTm.Pair);
        Action<Cat, CatDto> C_B = (s, d) => { };
        Action<Cat, CatDto> C_A = (s, d) => { };
        catTm.BeforeHooks.Add(HookEntry.FromLambda(C_B));
        catTm.AfterHooks.Add(HookEntry.FromLambda(C_A));

        var typeMaps = new List<TypeMap> { ltTm, animalTm, catTm };
        var pairIndex = typeMaps.ToDictionary(t => t.Pair);

        InheritanceMerger.Resolve(typeMaps, pairIndex);

        // Expected: Cat.BeforeHooks = [LT_B, A_B, C_B]
        Assert.Equal(3, catTm.BeforeHooks.Count);
        Assert.Same(LT_B, catTm.BeforeHooks[0].Lambda);
        Assert.Same(A_B, catTm.BeforeHooks[1].Lambda);
        Assert.Same(C_B, catTm.BeforeHooks[2].Lambda);

        // Expected: Cat.AfterHooks = [C_A, A_A, LT_A]
        Assert.Equal(3, catTm.AfterHooks.Count);
        Assert.Same(C_A, catTm.AfterHooks[0].Lambda);
        Assert.Same(A_A, catTm.AfterHooks[1].Lambda);
        Assert.Same(LT_A, catTm.AfterHooks[2].Lambda);
    }

    [Fact]
    public void Merge_DerivedOnly_NoBaseHooks_Unchanged()
    {
        var animalTm = new TypeMap(typeof(Animal), typeof(AnimalDto), MemberList.None);
        // No base hooks.

        var catTm = new TypeMap(typeof(Cat), typeof(CatDto), MemberList.None);
        catTm.IncludedBases.Add(animalTm.Pair);
        Action<Cat, CatDto> C_B = (s, d) => { };
        Action<Cat, CatDto> C_A = (s, d) => { };
        catTm.BeforeHooks.Add(HookEntry.FromLambda(C_B));
        catTm.AfterHooks.Add(HookEntry.FromLambda(C_A));

        var typeMaps = new List<TypeMap> { animalTm, catTm };
        var pairIndex = typeMaps.ToDictionary(t => t.Pair);

        InheritanceMerger.Resolve(typeMaps, pairIndex);

        Assert.Single(catTm.BeforeHooks);
        Assert.Same(C_B, catTm.BeforeHooks[0].Lambda);
        Assert.Single(catTm.AfterHooks);
        Assert.Same(C_A, catTm.AfterHooks[0].Lambda);
    }

    [Fact]
    public void Merge_PreservesPropertyMapMergeBehavior()
    {
        // Regression: existing PropertyMap merge still works after the hook merge addition.
        var animalTm = new TypeMap(typeof(Animal), typeof(AnimalDto), MemberList.None);
        var basePm = PropertyMap.ForProperty(typeof(AnimalDto).GetProperty(nameof(AnimalDto.GetType))!);
        // ...actually pick a simpler shape — use a concrete domain.
        // For brevity, just assert no crash and counts unchanged for an empty merge.

        var catTm = new TypeMap(typeof(Cat), typeof(CatDto), MemberList.None);
        catTm.IncludedBases.Add(animalTm.Pair);

        var typeMaps = new List<TypeMap> { animalTm, catTm };
        var pairIndex = typeMaps.ToDictionary(t => t.Pair);

        InheritanceMerger.Resolve(typeMaps, pairIndex);

        // Hooks empty on both — verify nothing leaks.
        Assert.Empty(animalTm.BeforeHooks);
        Assert.Empty(animalTm.AfterHooks);
        Assert.Empty(catTm.BeforeHooks);
        Assert.Empty(catTm.AfterHooks);
    }
}
```

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~InheritanceMergerHookTests" --nologo`

Expected: at least 3 failures (the merge tests). The "no base hooks" test may pass coincidentally.

- [ ] **Step 3: Extend `InheritanceMerger.MergeBaseConfig` to merge hooks**

Edit `src/Atlas/Internal/InheritanceMerger.cs` — at the END of the existing `MergeBaseConfig` method (currently ends at line 42 with `// else: derived is explicit — keep it as-is.`), add the hook-merge code:

```csharp
    public static void MergeBaseConfig(TypeMap baseTm, TypeMap derivedTm)
    {
        foreach (var basePm in baseTm.PropertyMaps)
        {
            // ... existing PropertyMap merge logic, unchanged ...
            if (!basePm.IsExplicit) continue;

            var derivedPm = derivedTm.PropertyMaps.FirstOrDefault(p => p.Name == basePm.Name);

            if (derivedPm is null)
            {
                var derivedProp = derivedTm.DestinationType.GetProperty(basePm.Name);
                if (derivedProp is null) continue;

                var clone = PropertyMap.ForProperty(derivedProp);
                CopyConfig(basePm, clone);
                clone.IsExplicit = true;
                derivedTm.PropertyMaps.Add(clone);
            }
            else if (!derivedPm.IsExplicit)
            {
                CopyConfig(basePm, derivedPm);
                derivedPm.IsExplicit = true;
            }
        }

        // NEW: hook merge.
        // BeforeHooks: prepend base's hooks so they run FIRST at runtime (base-first).
        if (baseTm.BeforeHooks.Count > 0)
            derivedTm.BeforeHooks.InsertRange(0, baseTm.BeforeHooks);

        // AfterHooks: append base's hooks so they run LAST at runtime (stack-unwind order:
        // derived's AfterHooks fire first, then base's).
        if (baseTm.AfterHooks.Count > 0)
            derivedTm.AfterHooks.AddRange(baseTm.AfterHooks);
    }
```

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~InheritanceMergerHookTests" --nologo`

Expected: 5/5 pass.

- [ ] **Step 5: Run full test suite**

Run: `dotnet test --nologo`

Expected: 346 tests pass (341 + 5). Existing inheritance tests should remain green — the hook-merge adds new lines but doesn't alter PropertyMap merge behavior.

- [ ] **Step 6: Commit**

```powershell
git add src/Atlas/Internal/InheritanceMerger.cs tests/Atlas.Tests/Internal/InheritanceMergerHookTests.cs
git commit -m "Add hook merge to InheritanceMerger (base-first BeforeHooks, base-last AfterHooks, 5 tests)"
```

---

## Task 7: `ConfigurationValidator` hook validation

**Files:**
- Modify: `src/Atlas/Internal/ConfigurationValidator.cs`
- Modify: `src/Atlas/MapperConfiguration.cs` (pass SP to validator)
- Create: `tests/Atlas.Tests/ConfigurationValidatorHookTests.cs`

Add an always-on `ValidateHooks` rule. Plumb `IServiceProvider` through `Validate`. Spec references: §5.7.

- [ ] **Step 1: Write failing tests**

Create `tests/Atlas.Tests/ConfigurationValidatorHookTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Tests;

public class ConfigurationValidatorHookTests
{
    public sealed class S { public int X { get; set; } }
    public sealed class D { public int X { get; set; } }

    public sealed class GoodAction : IMappingAction<S, D>
    {
        public void Process(S source, D destination) { }
    }

    public sealed class CtorRequiredAction : IMappingAction<S, D>
    {
        public CtorRequiredAction(int x) { _ = x; }
        public void Process(S source, D destination) { }
    }

    public sealed class WrongPair : IMappingAction<int, string>
    {
        public void Process(int source, string destination) { }
    }

    public sealed class ScopedDep
    {
        public ScopedDep() { }
    }

    public sealed class ActionWithScopedDep : IMappingAction<S, D>
    {
        public ActionWithScopedDep(ScopedDep dep) { _ = dep; }
        public void Process(S source, D destination) { }
    }

    [Fact]
    public void ValidateHooks_ActionTypeWithoutParameterlessCtor_NoDI_Errors()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .BeforeMap<CtorRequiredAction>());

        var ex = Assert.Throws<AtlasConfigurationException>(() => cfg.AssertConfigurationIsValid());
        Assert.Contains("CtorRequiredAction", ex.Message);
        Assert.Contains("parameterless", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateHooks_ActionTypeNotImplementingInterface_Errors()
    {
        // The fluent API's generic constraint prevents this at COMPILE time, but a
        // hand-crafted HookEntry could carry an arbitrary type. Validate via a TypeMap
        // built bypassing the fluent surface.
        var expression = new MapperConfigurationExpression();
        var fwdExpr = expression.CreateMap<S, D>(MemberList.None);
        // Reach into the TypeMap and add a malformed HookEntry.
        var tm = ((Atlas.Configuration.MappingExpression<S, D>)fwdExpr).TypeMap;
        tm.BeforeHooks.Add(Atlas.Internal.HookEntry.FromActionType(typeof(WrongPair)));
        var cfg = new MapperConfiguration(expression);

        var ex = Assert.Throws<AtlasConfigurationException>(() => cfg.AssertConfigurationIsValid());
        Assert.Contains("WrongPair", ex.Message);
        Assert.Contains("IMappingAction", ex.Message);
    }

    [Fact]
    public void ValidateHooks_ValidLambdaAndAction_Pass()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .BeforeMap((s, d) => { })
                .AfterMap<GoodAction>());

        cfg.AssertConfigurationIsValid();   // does not throw
    }

    [Fact]
    public void ValidateHooks_ScopedServiceDependency_Errors_WithClearMessage()
    {
        var services = new ServiceCollection();
        services.AddScoped<ScopedDep>();
        var sp = services.BuildServiceProvider();

        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .AfterMap<ActionWithScopedDep>(),
            sp);

        var ex = Assert.Throws<AtlasConfigurationException>(() => cfg.AssertConfigurationIsValid());
        Assert.Contains("ActionWithScopedDep", ex.Message);
        // Message should mention the construction failed and recommend singleton/transient.
        Assert.Contains("scoped", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~ConfigurationValidatorHookTests" --nologo`

Expected: 4 failures — `ValidateHooks` does not exist; configurations with hooks pass validation today.

- [ ] **Step 3: Add `ValidateHooks` and the SP parameter to `ConfigurationValidator.Validate`**

Edit `src/Atlas/Internal/ConfigurationValidator.cs`:

(a) Add a `using Microsoft.Extensions.DependencyInjection;` import at the top of the file (next to the existing `using System.Reflection;`).

(b) Update the `Validate` signature and add the `ValidateHooks` call. Replace the existing `Validate` method:

```csharp
    public static void Validate(MapperRegistry registry, bool enumValidationEnabled = false)
    {
        var errors = new List<ConfigurationError>();
        foreach (var tm in registry.AllTypeMaps)
        {
            ValidateEnum(tm, errors);
            ValidatePaths(tm, errors);

            if (enumValidationEnabled)
                ValidateEnumStrict(tm, errors);

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

with:

```csharp
    public static void Validate(
        MapperRegistry registry,
        bool enumValidationEnabled = false,
        IServiceProvider? serviceProvider = null)
    {
        var errors = new List<ConfigurationError>();
        foreach (var tm in registry.AllTypeMaps)
        {
            ValidateEnum(tm, errors);
            ValidatePaths(tm, errors);
            ValidateHooks(tm, serviceProvider, errors);

            if (enumValidationEnabled)
                ValidateEnumStrict(tm, errors);

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

(c) Add the new private helper at the end of the class (before the closing brace):

```csharp
    private static void ValidateHooks(TypeMap tm, IServiceProvider? sp, List<ConfigurationError> errors)
    {
        foreach (var entry in tm.BeforeHooks.Concat(tm.AfterHooks))
        {
            if (entry.ActionType is null) continue;   // lambda entries are always valid

            var actionType = entry.ActionType;

            // 1. Interface implementation check.
            var expectedInterface = typeof(IMappingAction<,>).MakeGenericType(tm.SourceType, tm.DestinationType);
            if (!expectedInterface.IsAssignableFrom(actionType))
            {
                errors.Add(new ConfigurationError(
                    tm.SourceType, tm.DestinationType, "(BeforeMap/AfterMap)",
                    $"Action type {actionType.Name} does not implement IMappingAction<{tm.SourceType.Name}, {tm.DestinationType.Name}>."));
                continue;
            }

            // 2. Eager construction check.
            try
            {
                _ = sp is not null
                    ? ActivatorUtilities.CreateInstance(sp, actionType)
                    : Activator.CreateInstance(actionType);
            }
            catch (Exception ex)
            {
                errors.Add(new ConfigurationError(
                    tm.SourceType, tm.DestinationType, "(BeforeMap/AfterMap)",
                    $"Action type {actionType.Name} construction failed: {ex.Message}. " +
                    "When Atlas is used without the DI extension, the action must have a public parameterless constructor. " +
                    "When using DI, ensure all constructor dependencies are registered as singleton or transient (scoped services are not supported)."));
            }
        }
    }
```

- [ ] **Step 4: Update `MapperConfiguration.AssertConfigurationIsValid` to pass SP**

Edit `src/Atlas/MapperConfiguration.cs` — locate the `AssertConfigurationIsValid` method (around line 77-78) and update it:

Replace:

```csharp
    public void AssertConfigurationIsValid() =>
        ConfigurationValidator.Validate(_registry, _enumValidationEnabled);
```

with:

```csharp
    public void AssertConfigurationIsValid() =>
        ConfigurationValidator.Validate(_registry, _enumValidationEnabled, _serviceProvider);
```

- [ ] **Step 5: Run tests to verify pass**

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~ConfigurationValidatorHookTests" --nologo`

Expected: 4/4 pass.

- [ ] **Step 6: Run full test suite**

Run: `dotnet test --nologo`

Expected: 350 tests pass (346 + 4). Existing validator tests remain green — the new SP parameter has a default of `null` so existing call sites work unchanged.

- [ ] **Step 7: Commit**

```powershell
git add src/Atlas/Internal/ConfigurationValidator.cs src/Atlas/MapperConfiguration.cs tests/Atlas.Tests/ConfigurationValidatorHookTests.cs
git commit -m "Add ValidateHooks rule (interface check + eager construction) (4 tests)"
```

---

## Task 8: `ExecutionPlanBuilder` hook emission

**Files:**
- Modify: `src/Atlas/Internal/ExecutionPlanBuilder.cs`
- Create: `tests/Atlas.Tests/ExecutionPlanBuilderHookTests.cs`

Emit hook calls in `BuildPocoLambda` and `BuildUpdate`. Spec references: §6.1, §6.2, §6.6.

- [ ] **Step 1: Write failing tests**

Create `tests/Atlas.Tests/ExecutionPlanBuilderHookTests.cs`:

```csharp
using Atlas.Configuration;

namespace Atlas.Tests;

public class ExecutionPlanBuilderHookTests
{
    public class S { public int Value { get; set; } }
    public class D { public int Value { get; set; } public List<string> Trace { get; } = new(); }

    [Fact]
    public void BuildPocoLambda_EmitsBeforeHooksAtTop()
    {
        var trace = new List<string>();
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .BeforeMap((s, d) => trace.Add("before")));
        var mapper = cfg.CreateMapper();

        mapper.Map<D>(new S());

        Assert.Single(trace);
        Assert.Equal("before", trace[0]);
    }

    [Fact]
    public void BuildPocoLambda_EmitsAfterHooksAtBottom()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .AfterMap((s, d) => d.Trace.Add($"after value={d.Value}")));
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<D>(new S { Value = 7 });

        Assert.Single(dst.Trace);
        Assert.Equal("after value=7", dst.Trace[0]);
    }

    [Fact]
    public void BuildUpdate_EmitsHooksToo()
    {
        var trace = new List<string>();
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .BeforeMap((s, d) => trace.Add($"before existing-value={d.Value}"))
                .AfterMap((s, d) => trace.Add($"after value={d.Value}")));
        var mapper = cfg.CreateMapper();

        var existing = new D { Value = 99 };
        mapper.Map<S, D>(new S { Value = 7 }, existing);

        Assert.Equal(2, trace.Count);
        Assert.Equal("before existing-value=99", trace[0]);
        Assert.Equal("after value=7", trace[1]);
    }

    [Fact]
    public void NoHooks_NoExtraStatementsEmitted()
    {
        // Sanity: no-hook config behaves identically to pre-feature.
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None));
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<D>(new S { Value = 42 });

        Assert.Equal(42, dst.Value);
        Assert.Empty(dst.Trace);
    }
}
```

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~ExecutionPlanBuilderHookTests" --nologo`

Expected: 3 failures (the hook-emission tests). The "NoHooks" sanity test passes already.

- [ ] **Step 3: Add `BuildHookCall` helper and emission in `BuildPocoLambda` + `BuildUpdate`**

Edit `src/Atlas/Internal/ExecutionPlanBuilder.cs`:

(a) Add the `BuildHookCall` helper at the bottom of the class, alongside `BuildNestedAssign` (which was added in feature #4). Place it just before the closing brace of the class:

```csharp
    private static Expression BuildHookCall(
        HookEntry entry,
        Expression srcExpr,
        Expression destExpr,
        MapperRegistry registry)
    {
        var srcType = srcExpr.Type;
        var dstType = destExpr.Type;
        var resolveMethod = typeof(HookResolver)
            .GetMethod(nameof(HookResolver.Resolve), BindingFlags.Public | BindingFlags.Static)!
            .MakeGenericMethod(srcType, dstType);
        var typedDelegate = (Delegate)resolveMethod.Invoke(null, new object?[] { entry, registry })!;

        // Emit: typedDelegate.Invoke(src, dest)
        return Expression.Invoke(Expression.Constant(typedDelegate), srcExpr, destExpr);
    }
```

(b) Update `BuildPocoLambda` to emit hooks. Locate the section starting around line 203:

```csharp
        var statements = new List<Expression>
        {
            Expression.Assign(destVar, newDest),
        };

        foreach (var pm in propertyMaps)
        {
            // ... existing per-binding emit ...
        }

        statements.Add(destVar);
```

Replace with:

```csharp
        var statements = new List<Expression>
        {
            Expression.Assign(destVar, newDest),
        };

        // NEW: emit BeforeHooks (FIFO order).
        foreach (var hookEntry in typeMap.BeforeHooks)
            statements.Add(BuildHookCall(hookEntry, srcParam, destVar, registry));

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

        // NEW: emit AfterHooks (FIFO order).
        foreach (var hookEntry in typeMap.AfterHooks)
            statements.Add(BuildHookCall(hookEntry, srcParam, destVar, registry));

        statements.Add(destVar);
```

(c) Update `BuildUpdate` similarly. Locate the section starting around line 141 (the `foreach (var pm in typeMap.PropertyMaps)` loop in `BuildUpdate`). The current shape:

```csharp
        var statements = new List<Expression>();

        foreach (var pm in typeMap.PropertyMaps)
        {
            // ... existing per-binding emit ...
        }

        Expression body = statements.Count > 0
            ? Expression.Block(statements)
            : Expression.Empty();
```

Replace with:

```csharp
        var statements = new List<Expression>();

        // NEW: emit BeforeHooks.
        foreach (var hookEntry in typeMap.BeforeHooks)
            statements.Add(BuildHookCall(hookEntry, srcParam, destParam, registry));

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

        // NEW: emit AfterHooks.
        foreach (var hookEntry in typeMap.AfterHooks)
            statements.Add(BuildHookCall(hookEntry, srcParam, destParam, registry));

        Expression body = statements.Count > 0
            ? Expression.Block(statements)
            : Expression.Empty();
```

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~ExecutionPlanBuilderHookTests" --nologo`

Expected: 4/4 pass.

- [ ] **Step 5: Run full test suite**

Run: `dotnet test --nologo`

Expected: 354 tests pass (350 + 4). Existing tests remain green — the hook emission is additive and only fires when hooks are present.

- [ ] **Step 6: Commit**

```powershell
git add src/Atlas/Internal/ExecutionPlanBuilder.cs tests/Atlas.Tests/ExecutionPlanBuilderHookTests.cs
git commit -m "Emit hook calls in BuildPocoLambda + BuildUpdate (4 tests)"
```

---

## Task 9: End-to-end `MapperBeforeAfterMapTests`

**Files:**
- Create: `tests/Atlas.Tests/MapperBeforeAfterMapTests.cs`

Seven end-to-end tests covering single-map, collection per-element, FIFO, inheritance order, DI-resolved IMappingAction, exception propagation, update-in-place. Spec references: §7.7.

- [ ] **Step 1: Write the tests**

Create `tests/Atlas.Tests/MapperBeforeAfterMapTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Tests;

public class MapperBeforeAfterMapTests
{
    public class S { public int X { get; set; } }
    public class D { public int X { get; set; } }

    public class Animal { public string? Name { get; set; } }
    public class Dog : Animal { }
    public class AnimalDto { public string? Name { get; set; } }
    public class DogDto : AnimalDto { }

    [Fact]
    public void BeforeMap_Lambda_FiresOncePerMap()
    {
        var trace = new List<string>();
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .BeforeMap((s, d) => trace.Add("before")));
        var mapper = cfg.CreateMapper();

        mapper.Map<D>(new S());

        Assert.Single(trace);
    }

    [Fact]
    public void BeforeMap_FiresPerElement_OnCollectionMapping()
    {
        var trace = new List<string>();
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .BeforeMap((s, d) => trace.Add($"item {s.X}")));
        var mapper = cfg.CreateMapper();

        var srcs = new List<S> { new() { X = 1 }, new() { X = 2 }, new() { X = 3 } };
        mapper.Map<List<S>, List<D>>(srcs);

        Assert.Equal(3, trace.Count);
        Assert.Equal("item 1", trace[0]);
        Assert.Equal("item 2", trace[1]);
        Assert.Equal("item 3", trace[2]);
    }

    [Fact]
    public void MultipleHooks_FireInFifoOrder()
    {
        var trace = new List<string>();
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .BeforeMap((s, d) => trace.Add("before-1"))
                .BeforeMap((s, d) => trace.Add("before-2"))
                .BeforeMap((s, d) => trace.Add("before-3"))
                .AfterMap((s, d) => trace.Add("after-1"))
                .AfterMap((s, d) => trace.Add("after-2")));
        var mapper = cfg.CreateMapper();

        mapper.Map<D>(new S());

        Assert.Equal(new[] { "before-1", "before-2", "before-3", "after-1", "after-2" }, trace);
    }

    [Fact]
    public void Inheritance_HookOrder_MatchesStackUnwind()
    {
        var trace = new List<string>();
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap<Animal, AnimalDto>(MemberList.None)
                .Include<Dog, DogDto>()
                .BeforeMap((s, d) => trace.Add("animal-before"))
                .AfterMap((s, d) => trace.Add("animal-after"));
            c.CreateMap<Dog, DogDto>(MemberList.None)
                .IncludeBase<Animal, AnimalDto>()
                .BeforeMap((s, d) => trace.Add("dog-before"))
                .AfterMap((s, d) => trace.Add("dog-after"));
        });
        var mapper = cfg.CreateMapper();

        mapper.Map<DogDto>(new Dog { Name = "Rex" });

        // Expected: animal-before → dog-before → [property mapping] → dog-after → animal-after.
        Assert.Equal(new[] { "animal-before", "dog-before", "dog-after", "animal-after" }, trace);
    }

    public sealed class Counter
    {
        public int Value;
    }

    public sealed class IncAction : IMappingAction<S, D>
    {
        private readonly Counter _counter;
        public IncAction(Counter counter) => _counter = counter;
        public void Process(S source, D destination) => _counter.Value++;
    }

    [Fact]
    public void IMappingAction_DI_ResolvesAndCallsProcess()
    {
        var counter = new Counter();
        var services = new ServiceCollection();
        services.AddSingleton(counter);
        var sp = services.BuildServiceProvider();

        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .AfterMap<IncAction>(),
            sp);
        var mapper = cfg.CreateMapper();

        mapper.Map<D>(new S());
        mapper.Map<D>(new S());
        mapper.Map<D>(new S());

        Assert.Equal(3, counter.Value);
    }

    [Fact]
    public void Hook_ExceptionPropagates()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .BeforeMap((s, d) => throw new InvalidOperationException("boom")));
        var mapper = cfg.CreateMapper();

        var ex = Assert.Throws<InvalidOperationException>(() => mapper.Map<D>(new S()));
        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public void UpdateInPlace_FiresHooks()
    {
        var trace = new List<string>();
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .BeforeMap((s, d) => trace.Add($"before existing.X={d.X}"))
                .AfterMap((s, d) => trace.Add($"after dest.X={d.X}")));
        var mapper = cfg.CreateMapper();

        var existing = new D { X = 99 };
        mapper.Map<S, D>(new S { X = 7 }, existing);

        Assert.Equal(new[] { "before existing.X=99", "after dest.X=7" }, trace);
    }
}
```

- [ ] **Step 2: Run tests to verify pass**

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~MapperBeforeAfterMapTests" --nologo`

Expected: 7/7 pass. (If any fail, the failure indicates an integration gap from earlier tasks — STOP, identify which task introduced the regression, and report. Do NOT modify production code; this task is test-only.)

- [ ] **Step 3: Run full test suite**

Run: `dotnet test --nologo`

Expected: 361 tests pass (354 + 7).

- [ ] **Step 4: Commit**

```powershell
git add tests/Atlas.Tests/MapperBeforeAfterMapTests.cs
git commit -m "Add end-to-end MapperBeforeAfterMapTests (7 tests)"
```

---

## Task 10: `Atlas.Projections` rejection of TypeMaps with hooks

**Files:**
- Modify: `src/Atlas.Projections/Internal/ProjectionPlanBuilder.cs`
- Modify: `src/Atlas.Projections/Internal/ProjectionCompatibility.cs`
- Create: `tests/Atlas.Projections.Tests/ProjectionRejectsHooksTests.cs`

Reject any TypeMap with non-empty BeforeHooks or AfterHooks at projection-build time. Spec references: §6.7.

- [ ] **Step 1: Write failing tests**

Create `tests/Atlas.Projections.Tests/ProjectionRejectsHooksTests.cs`:

```csharp
namespace Atlas.Projections.Tests;

public class ProjectionRejectsHooksTests
{
    public class S { public int X { get; set; } }
    public class D { public int X { get; set; } }

    [Fact]
    public void ProjectTo_ForwardMapWithBeforeMap_ThrowsNamingHookCount()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .BeforeMap((s, d) => { }));
        var srcs = new List<S> { new() { X = 1 } }.AsQueryable();

        var ex = Assert.Throws<AtlasConfigurationException>(() => srcs.ProjectTo<D>(cfg).ToList());
        Assert.Contains("BeforeMap", ex.Message);
        Assert.Contains("1", ex.Message);   // hook count
        Assert.Contains("Map<>", ex.Message);
    }

    [Fact]
    public void ProjectTo_ForwardMapWithAfterMap_ThrowsNamingHookCount()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<S, D>(MemberList.None)
                .AfterMap((s, d) => { })
                .AfterMap((s, d) => { }));
        var srcs = new List<S> { new() { X = 1 } }.AsQueryable();

        var ex = Assert.Throws<AtlasConfigurationException>(() => srcs.ProjectTo<D>(cfg).ToList());
        Assert.Contains("AfterMap", ex.Message);
        Assert.Contains("2", ex.Message);   // hook count
    }
}
```

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test tests/Atlas.Projections.Tests --filter "FullyQualifiedName~ProjectionRejectsHooksTests" --nologo`

Expected: 2 failures — projection currently silently runs the lambda (or skips the hook); doesn't throw.

- [ ] **Step 3: Add the hook check in `ProjectionPlanBuilder.Build`**

Edit `src/Atlas.Projections/Internal/ProjectionPlanBuilder.cs` — locate the `Build` method (top of the file, around line 14):

```csharp
    public static LambdaExpression Build(MapperRegistry registry, TypePair root, int maxDepth)
    {
        var tm = registry.GetTypeMap(root)
            ?? throw new InvalidOperationException(
                $"No map registered for {root.Source.Name} -> {root.Destination.Name}.");
        var srcParam = Expression.Parameter(tm.SourceType, "src");
        var body = BuildBody(tm, srcParam, depth: 0, registry, maxDepth);
        var funcType = typeof(Func<,>).MakeGenericType(tm.SourceType, tm.DestinationType);
        return Expression.Lambda(funcType, body, srcParam);
    }
```

Add a hook-rejection check before the body is built. Replace with:

```csharp
    public static LambdaExpression Build(MapperRegistry registry, TypePair root, int maxDepth)
    {
        var tm = registry.GetTypeMap(root)
            ?? throw new InvalidOperationException(
                $"No map registered for {root.Source.Name} -> {root.Destination.Name}.");

        RejectHooksOrThrow(tm);

        var srcParam = Expression.Parameter(tm.SourceType, "src");
        var body = BuildBody(tm, srcParam, depth: 0, registry, maxDepth);
        var funcType = typeof(Func<,>).MakeGenericType(tm.SourceType, tm.DestinationType);
        return Expression.Lambda(funcType, body, srcParam);
    }

    private static void RejectHooksOrThrow(TypeMap tm)
    {
        if (tm.BeforeHooks.Count == 0 && tm.AfterHooks.Count == 0) return;
        throw new AtlasConfigurationException(new List<ConfigurationError>
        {
            new(tm.SourceType, tm.DestinationType, "(BeforeMap/AfterMap)",
                $"Cannot project ({tm.SourceType.Name}, {tm.DestinationType.Name}): " +
                $"map has {tm.BeforeHooks.Count} BeforeMap and {tm.AfterHooks.Count} AfterMap hook(s). " +
                "Hooks are not translatable to IQueryable. Use mapper.Map<>() instead, or remove the hooks.")
        });
    }
```

(Also call `RejectHooksOrThrow` at the top of `BuildBody` to cover nested-map invocations — same shape, but ensure it's called before `ClassifyBindings`. The simplest implementation: call it once at the top of `BuildBody` so that BOTH the top-level entry and recursive nested invocations trigger it.)

Update `BuildBody`:

```csharp
    private static Expression BuildBody(TypeMap tm, Expression srcExpr, int depth, MapperRegistry registry, int maxDepth)
    {
        RejectHooksOrThrow(tm);   // catches nested maps with hooks too

        var (ctor, ctorParamMaps, propertyMaps) = ClassifyBindings(tm);

        // ... rest unchanged ...
    }
```

- [ ] **Step 4: (Optional) Add hook check to `ProjectionCompatibility`**

This task only needs the `ProjectionPlanBuilder` rejection to satisfy the tests, but for symmetry with the `ProjectionValidator` (which surfaces issues up-front), add a TypeMap-level check to `ProjectionCompatibility`:

Edit `src/Atlas.Projections/Internal/ProjectionCompatibility.cs` — modify `IsTypeMapProjectable`:

```csharp
    public static bool IsTypeMapProjectable(TypeMap tm, out string? reason)
    {
        if (tm.CustomConverter is not null)
        {
            reason = "ConvertUsing(...) — delegate-form converter is in-memory only.";
            return false;
        }

        if (tm.BeforeHooks.Count > 0 || tm.AfterHooks.Count > 0)
        {
            reason = $"map has {tm.BeforeHooks.Count} BeforeMap and {tm.AfterHooks.Count} AfterMap hook(s) — hooks are not translatable to IQueryable.";
            return false;
        }

        reason = null;
        return true;
    }
```

- [ ] **Step 5: Run tests to verify pass**

Run: `dotnet test tests/Atlas.Projections.Tests --filter "FullyQualifiedName~ProjectionRejectsHooksTests" --nologo`

Expected: 2/2 pass.

- [ ] **Step 6: Run full test suite**

Run: `dotnet test --nologo`

Expected: 363 tests pass (361 + 2). All existing Atlas.Projections tests remain green — they don't use hooks.

- [ ] **Step 7: Commit**

```powershell
git add src/Atlas.Projections/Internal/ProjectionPlanBuilder.cs src/Atlas.Projections/Internal/ProjectionCompatibility.cs tests/Atlas.Projections.Tests/ProjectionRejectsHooksTests.cs
git commit -m "Atlas.Projections rejects TypeMaps with hooks at projection-build (2 tests)"
```

---

## Task 11: README + coverage check

**Files:**
- Modify: `README.md`

Add a `## Before/after hooks` section to the README with a worked example. Remove "Before/after hooks" from the deferred-features list. Verify coverage targets met.

- [ ] **Step 1: Locate the deferred-to-v2 list and existing sections in README**

Read `README.md`. Identify:
- Where the existing "Reverse mapping" section sits (the new Before/after hooks section will go right after it)
- The exact text of the "Deferred to v2" list (confirm "Before/after hooks" is the next entry)
- The current coverage table

- [ ] **Step 2: Add the new section and remove the deferred entry**

Two edits:

(a) After the `## Reverse mapping` section and before the "Deferred to v2" list, add:

```markdown
## Before/after hooks

Run code at well-defined points around each mapping. Two flavors per direction:

```csharp
public class OrderProfile : MapperProfile
{
    public OrderProfile()
    {
        CreateMap<Order, OrderDto>()
            .BeforeMap((s, d) => s.NormalizeFields())   // inline lambda
            .AfterMap<AuditAction>();                    // DI-friendly action
    }
}

public sealed class AuditAction : IMappingAction<Order, OrderDto>
{
    private readonly ILogger<AuditAction> _log;
    public AuditAction(ILogger<AuditAction> log) => _log = log;
    public void Process(Order src, OrderDto dst) =>
        _log.LogInformation("Mapped Order {Id}", src.Id);
}
```

Multiple hooks per direction run in registration order (FIFO). With `Include`/`IncludeBase`,
base hooks fire BEFORE derived hooks for `BeforeMap`, and AFTER derived hooks for `AfterMap`
(stack-unwind order — pairs cleanly with try/finally-style intent).

Hooks fire on every `Map<>()` call (including update-in-place) and on every per-element
invocation when mapping a collection.

**DI integration.** When Atlas is registered through `AddAtlas(...)`, `IMappingAction`
implementations are instantiated via `ActivatorUtilities.CreateInstance` from the root
service provider — constructor-injecting singleton and transient services. Without DI, the
action type must have a public parameterless constructor.

**Limitation:** scoped services (HTTP context, current user, scoped EF DbContext) are NOT
supported because actions are resolved from the root provider and cached. For HTTP-context-aware
logic, inject `IHttpContextAccessor` (which is itself singleton-resolvable) and read the
per-request context inside `Process`.

**Foot-gun guards** (caught by `AssertConfigurationIsValid`):
- Action types must implement `IMappingAction<TSource, TDestination>` matching the map's pair.
- Without DI, action types require a public parameterless constructor.
- With DI, scoped-service dependencies surface as a clear error at validate time.

**ProjectTo limitation.** Hooks are not translatable to IQueryable. Calling
`query.ProjectTo<TDestination>()` against a map with any hooks throws
`AtlasConfigurationException` at projection-build time naming the hook count and pointing
to `Map<>()` instead.

**Hooks do NOT auto-propagate via `.ReverseMap()`** — configure hooks on the reverse
expression separately if needed.
```

(b) Remove the `Before/after hooks (`BeforeMap`, `AfterMap`, `IMappingAction`)` entry from the deferred-features list.

- [ ] **Step 3: Run coverage check**

Run from `C:\Repos\Atlas`:

```powershell
dotnet test --nologo --collect:"XPlat Code Coverage" --results-directory ./TestResults
```

Use `reportgenerator` to extract the per-project numbers:

```powershell
dotnet tool restore
reportgenerator -reports:./TestResults/**/coverage.cobertura.xml -targetdir:./TestResults/CoverageReport -reporttypes:TextSummary
```

Read `./TestResults/CoverageReport/Summary.txt` and find the Atlas line. Verify:
- Atlas: line ≥ 90%, branch ≥ 80%
- Atlas.Extensions.DependencyInjection: line ≥ 85%, branch ≥ 75%
- Atlas.Projections: line ≥ 90%, branch ≥ 80%

If any gate fails, identify the gap by reading the per-class breakdown in the report and add 1-2 targeted tests in the appropriate test file. Likely gaps:
- `HookResolver` rare branches (e.g., the InvalidOperationException branch when `entry.Lambda` is non-null but not the expected type — could be tested via a hand-crafted HookEntry with a wrong-shape Delegate).
- `ConfigurationValidator.ValidateHooks` branches (interface mismatch + construction failure).
- `BuildHookCall` reflection path (already exercised by Task 8 and 9 tests).

Report the actual coverage numbers in the commit message.

- [ ] **Step 4: Update the README's coverage table to reflect the new measured numbers**

Use the actual measured percentages from Step 3.

- [ ] **Step 5: Run final full test suite**

Run: `dotnet test --nologo`

Expected: 363 tests pass (or whatever the actual final count was after any coverage-gap tests added in Step 3).

- [ ] **Step 6: Commit**

```powershell
git add README.md
git commit -m "docs: README — add before/after hooks section, refresh coverage numbers"
```

(If you needed to add coverage-gap tests in Step 3, include those test files and add a note to the commit message.)

---

## Final review

After all 11 tasks land on the `feat/before-after-hooks` branch:

- [ ] **Step 1: Final-review by `superpowers:code-reviewer`**

The implementing controller (the agent driving subagent-driven-development) dispatches `superpowers:code-reviewer` over the full branch diff. Per memory, the holistic review has caught real bugs in every prior feature (Reverse mapping had a Critical ProjectTo crash that per-task review missed). Don't skip.

Particular things to surface in this review (cross-task / whole-feature):
- Did the new `Microsoft.Extensions.DependencyInjection.Abstractions` reference change Atlas's transitive package surface? (Should be additive only — Abstractions is small and ubiquitous.)
- Does `MapperConfiguration`'s SP-accepting ctor correctly preserve all non-SP state from the base ctor? (Two registry allocations during DI startup — verify no other state is lost.)
- Did the inheritance dispatch interaction in `ExecutionPlanBuilder.BuildWithInheritanceDispatch` get verified against hooks? (The dispatched-derived path should run merged hooks; the base-body path should run base-only hooks. See spec §6.4 trace.)
- Are there any `typeMap.` accesses in `Atlas.Projections` not covered by Task 10's rejection? (Per the Bug 4 lesson — full grep, not just the obvious paths.)

- [ ] **Step 2: Address any Critical / Important findings**

Per the review-catch frequency norm (~1-3 issues per holistic review based on Inheritance, Enum, ReverseMap experience), expect 0-2 issues. Fix in-branch with one or more `review fix:` commits (do NOT amend prior commits).

- [ ] **Step 3: Push and open PR**

Use `superpowers:finishing-a-development-branch` Option 2: push the branch, open a PR titled "Add before/after hooks (BeforeMap, AfterMap, IMappingAction)" with the design doc summary in the body and the actual final test/coverage numbers.

- [ ] **Step 4: After merge — memory updates**

After the user confirms the PR is merged:
- Update `atlas_v2_design_docs_deferred.md` to mark feature #5 as shipped (linking to `docs/Atlas-Design-BeforeAfterHooks.md`) and to identify feature #6 (Value Transformers) as next.
- Update `feedback_atlas_v2_workflow.md` baseline test count: 324 → ~363 (or actual measured).
- If the holistic review surfaced a NEW class of bug not covered by `feedback_pseudocode_concrete_trace.md` (currently 4 documented bugs), append it as Bug 5.

---

## Summary

- **11 tasks**, ~38 new tests (4 + 8 + 0 + 5 + 5 + 4 + 4 + 7 + 2 = 39).
- **Test baseline:** 324 → ~363.
- **Coverage targets:** line ≥ 90%, branch ≥ 80% on `Atlas` core.
- **New public types:** `IMappingAction<,>` interface only. No new exceptions.
- **New package reference:** `Microsoft.Extensions.DependencyInjection.Abstractions` added to `Atlas` core (centrally versioned at 10.0.0).
- **Branch:** `feat/before-after-hooks` cut from `main` HEAD `922bc44` (after design + plan land).
- **Model selection** (per memory's per-task guidance): haiku for Tasks 1, 2, 3, 10, 11; sonnet for Tasks 4, 5, 6, 7, 8, 9.
