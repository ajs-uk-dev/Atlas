# Atlas v2 — Before/After Hooks (`BeforeMap`, `AfterMap`, `IMappingAction`)

> **Status:** Design approved 2026-05-04. Implementation plan: `Atlas-Plan-BeforeAfterHooks.md` (to be written next).
> **Spec inputs:** `Object-Mapping-Functional-Reference.md` §6.3 (Before/after hooks) and §6.5 (DI integration); `AutoMapper-Analysis.md` §5.10 (`BeforeMap`/`AfterMap`/`IMappingAction`).
> **Position in v2 roadmap:** Feature #5 of 13 deferred groups. Builds on v1 + ProjectTo (#1) + Inheritance (#2) + Enum (#3) + Reverse Mapping (#4).

---

## 1. Goals & Non-Goals

### 1.1 Goal

Add `.BeforeMap` and `.AfterMap` to Atlas's fluent surface, with two flavors per direction:
- (a) inline `Action<TSource, TDestination>` lambda for stateless logic;
- (b) `IMappingAction<TSource, TDestination>` interface for DI-friendly logic that needs services (logging, IOptions, telemetry, `IHttpContextAccessor`).

Multiple hooks per direction supported, FIFO. Inheritance propagates hooks (base-first for `BeforeMap`, base-last for `AfterMap` — stack-unwind semantics). DI resolution via `ActivatorUtilities.CreateInstance` from root `IServiceProvider`, cached. `Atlas.Projections` rejects TypeMaps with hooks at projection-build time with a clear diagnostic.

### 1.2 In scope (v2 MVP)

1. New public interface `IMappingAction<in TSource, in TDestination>` with a single `Process(TSource, TDestination)` method.
2. Four new fluent methods on `IMappingExpression<TSource, TDestination>`: `BeforeMap(Action<...>)`, `BeforeMap<TAction>()`, `AfterMap(Action<...>)`, `AfterMap<TAction>()`.
3. New internal `HookEntry` discriminated record (lambda OR action type) and two ordered lists on `TypeMap` (`BeforeHooks`, `AfterHooks`).
4. `InheritanceMerger` extension: prepend base BeforeHooks; append base AfterHooks (so unwind order is derived-then-base for AfterMap).
5. `MapperConfiguration` and `MapperRegistry` plumb a nullable `IServiceProvider`. `Atlas.Extensions.DependencyInjection`'s `AddAtlas` passes the container's SP through.
6. `HookResolver` (new internal): resolves a `HookEntry` to a typed `Action<TSource, TDestination>`. For action types, calls `ActivatorUtilities.CreateInstance` once at config-build and caches the instance.
7. `ExecutionPlanBuilder` extension: emits hook calls inline at the top of `BuildPocoLambda`/`BuildUpdate` (BeforeHooks) and just before `return` (AfterHooks).
8. `ConfigurationValidator` extension: always-on rule that verifies action types are constructible (DI: `ActivatorUtilities.CreateInstance` against root SP succeeds; no-DI: public parameterless ctor exists).
9. `Atlas.Projections.ProjectionPlanBuilder` rejects any TypeMap with non-empty BeforeHooks or AfterHooks at projection-build time, throwing `AtlasConfigurationException` with the hook count and a "use Map<>() instead" message.

### 1.3 Out of scope (deferred to a future v3 design doc)

- **`ResolutionContext` / context bag for hooks.** A cross-cutting concern likely co-designed with feature #6 (Value Transformers) and #7 (Conditional Mapping).
- **Scoped-service support for `IMappingAction`.** The `ActivatorUtilities.CreateInstance(rootSp, ...)` model resolves singleton/transient ctor params correctly but cannot resolve scoped services (HTTP context, current user, scoped EF DbContext). Documented limitation; the workaround is wrapping scoped state in a singleton-resolvable accessor (e.g., `IHttpContextAccessor`).
- **Async hook support** (`Func<TSource, TDestination, Task>`).
- **Reverse-map auto-propagation of hooks.** Per scope-A discipline established in feature #4: hooks do NOT auto-flip via `.ReverseMap()`. User reconfigures hooks on the reverse expression if needed.
- **Collection-level (vs per-element) hooks.** Atlas's collection codegen forces per-element semantics: a `List<Order> → List<OrderDto>` map iterates per element via `MappingInvoker.Invoke<Order, OrderDto>`, so hooks on the `(Order, OrderDto)` map fire once per element. There is no separate `(List<Order>, List<OrderDto>)` TypeMap for the user to attach collection-level hooks.

### 1.4 Non-goals (out of scope permanently for this feature)

- Discovering hooks by attribute or convention without an explicit `.BeforeMap`/`.AfterMap` call. Hooks are opt-in.
- Modifying the source-side parameter inside a hook (`src` is logically `in`; mutation is a foot-gun but not blocked).
- Conditional hook execution (run hook only when a predicate holds). That's the Conditional Mapping feature (#7) territory.

---

## 2. Architecture Overview

### 2.1 What changes

- **`IMappingAction<,>`** — new public interface in `src/Atlas/`.
- **`IMappingExpression<,>`** gains four methods (two lambda-flavored, two action-type-flavored). No existing methods change.
- **`TypeMap`** gains two ordered lists: `BeforeHooks` and `AfterHooks`.
- **`HookEntry`** — new internal record encoding "lambda OR action type".
- **`MapperRegistry`** gains a nullable `IServiceProvider` and a `Dictionary<Type, object> ActionInstances` cache.
- **`HookResolver`** — new internal static class (resolves entries to typed delegates).
- **`InheritanceMerger`** extended (hook merge step).
- **`ExecutionPlanBuilder`** extended (hook emission in `BuildPocoLambda` + `BuildUpdate`).
- **`ConfigurationValidator`** extended (hook validation rule + SP plumbing).
- **`MapperConfiguration`** new constructor overloads that accept `IServiceProvider`.
- **`Atlas.Extensions.DependencyInjection`** passes container SP through.
- **`Atlas.Projections.ProjectionPlanBuilder`** + `ProjectionCompatibility` reject TypeMaps with hooks.

### 2.2 Build-time sequence (revised, NEW step in **bold**)

The current v1+v2 order in `MapperConfiguration.cs` is `InheritanceMerger.Resolve → ConventionEngine.ResolveMissingMembers → ReverseMapMirror.Mirror → tm.Seal()`. Hook merge runs as part of `InheritanceMerger.Resolve` (the same merge that already propagates `PropertyMap`s). No new top-level phase; the merger gains an additional concern.

```
1. Profile.Configure() — TypeMaps registered;
                         .BeforeMap/.AfterMap append HookEntry to BeforeHooks/AfterHooks.
2. ConfigExpression conflict-guard (unchanged from feature #4).
3. InheritanceMerger.Resolve(typeMaps) — EXTENDED: in addition to PropertyMap merge,
                                         prepend base.BeforeHooks to derived.BeforeHooks
                                         and append base.AfterHooks to derived.AfterHooks.
4. ConventionEngine.ResolveMissingMembers (unchanged).
5. ReverseMapMirror.Mirror (unchanged — Mirror only iterates PropertyMaps; hooks don't propagate).
6. tm.Seal() for each TypeMap.
7. (On AssertConfigurationIsValid) ConfigurationValidator.Validate(registry, enumValidationEnabled, sp)
   — EXTENDED: hook validation (action type constructibility).
8. CompileMappings — codegen reads merged BeforeHooks/AfterHooks and emits inline calls.
```

### 2.3 Runtime path

Unchanged at the dispatch level. `IMapper.Map<TDest>(source)` is still a dictionary lookup → cached delegate invoke. The compiled delegate body for a TypeMap with hooks differs only in that it includes `Expression.Invoke(constantHookDelegate, src, dst)` calls at the top (BeforeHooks) and bottom (AfterHooks) of the body.

### 2.4 Why merge-at-config-time and not resolve-at-runtime

Two architectures were considered:

- **(Recommended)** Merge-at-config-time: hooks propagate during `InheritanceMerger.Resolve`. Codegen reads merged lists and emits inline calls. Same architecture as PropertyMap merging.
- (Rejected) Resolve-at-runtime: codegen emits calls to a runtime helper that walks the inheritance chain at each `Map` call. More flexible (configurations could change after seal — but they can't anyway because of `Seal`) but adds per-call overhead and doesn't match Atlas's "everything resolved at config-build" architecture.

The merge-at-config-time approach also makes deterministic ordering trivially observable in tests (read `tm.BeforeHooks` after merge and assert the contents directly).

### 2.5 Why `ActivatorUtilities` over keyed DI registration

Three resolution strategies for `IMappingAction`:

- **Parameterless ctor only** — simplest, but kills the headline use case (DI services in actions).
- **(Recommended)** `ActivatorUtilities.CreateInstance(rootSp, type)` cached once — enables singleton/transient ctor injection without requiring users to register the action with DI separately. AutoMapper-style.
- **Per-call DI resolution** — most flexible (supports scoped services) but requires either making `IMapper` scoped (breaking change) or threading `IServiceProvider` through every `Map` call (verbose).

`ActivatorUtilities`-with-cache is the pareto cut: it covers the typical singleton/transient use cases (logging, IOptions, telemetry, `IHttpContextAccessor`) without API surface burden, and the scoped-service limitation has a clean workaround (wrap scoped state in a singleton-resolvable accessor).

---

## 3. Solution & Project Layout

No new project. Additions land in `src/Atlas/` (core), `src/Atlas.Extensions.DependencyInjection/` (DI wiring), and `src/Atlas.Projections/` (rejection). Test additions land in `tests/Atlas.Tests/` and `tests/Atlas.Projections.Tests/`.

```
src/Atlas/
├── IMappingAction.cs                    ← NEW: public interface
├── Internal/
│   ├── HookEntry.cs                     ← NEW: discriminated record
│   ├── HookResolver.cs                  ← NEW: resolve entry → typed delegate; cache action instances
│   ├── TypeMap.cs                       ← MODIFIED: add BeforeHooks, AfterHooks
│   ├── InheritanceMerger.cs             ← MODIFIED: hook merge
│   ├── MapperRegistry.cs                ← MODIFIED: ServiceProvider + ActionInstances cache
│   ├── ConfigurationValidator.cs        ← MODIFIED: hook validation rule + SP param
│   ├── ExecutionPlanBuilder.cs          ← MODIFIED: hook emission in BuildPocoLambda + BuildUpdate
│   └── ...
├── Configuration/
│   ├── IMappingExpression.cs            ← MODIFIED: 4 fluent methods
│   ├── MappingExpression.cs             ← MODIFIED: implement 4 methods
│   └── ...
├── MapperConfiguration.cs               ← MODIFIED: new ctor overloads with IServiceProvider
└── ...

src/Atlas.Extensions.DependencyInjection/
└── AtlasServiceCollectionExtensions.cs  ← MODIFIED: AddAtlas plumbs SP into MapperConfiguration

src/Atlas.Projections/
└── Internal/
    ├── ProjectionCompatibility.cs       ← MODIFIED: reject TypeMaps with hooks
    └── ProjectionPlanBuilder.cs         ← MODIFIED: surface rejection at projection-build time

tests/Atlas.Tests/
├── Internal/
│   ├── HookEntryTests.cs                ← NEW
│   ├── HookResolverTests.cs             ← NEW
│   └── InheritanceMergerHookTests.cs    ← NEW
├── MappingExpressionBeforeAfterMapTests.cs   ← NEW
├── ConfigurationValidatorHookTests.cs        ← NEW
├── ExecutionPlanBuilderHookTests.cs          ← NEW
└── MapperBeforeAfterMapTests.cs              ← NEW (end-to-end)

tests/Atlas.Projections.Tests/
└── ProjectionRejectsHooksTests.cs       ← NEW
```

No NuGet additions. xUnit v3 + built-in `Assert.X()` only (no FluentAssertions, per project convention).

---

## 4. Public API Additions

### 4.1 `IMappingAction<,>` (new public interface)

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
    void Process(TSource source, TDestination destination);
}
```

### 4.2 `IMappingExpression<,>` additions

Four new methods, two per direction:

```csharp
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
IMappingExpression<TSource, TDestination> AfterMap(Action<TSource, TDestination> hook);

/// <summary>
/// Registers a typed mapping-action class to run AFTER all destination members are mapped.
/// </summary>
/// <remarks>See <see cref="BeforeMap{TAction}"/> for resolution and lifetime semantics.</remarks>
IMappingExpression<TSource, TDestination> AfterMap<TAction>()
    where TAction : IMappingAction<TSource, TDestination>;
```

### 4.3 `MapperConfiguration` constructor overloads

```csharp
// Existing constructors retained, behavior unchanged. New overloads accept IServiceProvider.

public MapperConfiguration(
    Action<MapperConfigurationExpression> configure,
    IServiceProvider serviceProvider);

public MapperConfiguration(
    MapperConfigurationExpression expression,
    IServiceProvider serviceProvider);
```

### 4.4 Atlas.Extensions.DependencyInjection wiring

`AddAtlas` is updated to thread the container's `IServiceProvider` into the `MapperConfiguration` factory. Sketch:

```csharp
// src/Atlas.Extensions.DependencyInjection/AtlasServiceCollectionExtensions.cs (excerpt)
public static IServiceCollection AddAtlas(this IServiceCollection services, params Assembly[] assemblies)
{
    services.AddSingleton<MapperConfiguration>(sp =>
    {
        var expression = new MapperConfigurationExpression();
        foreach (var profile in ProfileScanner.Discover(assemblies))
            expression.AddProfile(profile);
        return new MapperConfiguration(expression, sp);   // pass SP through
    });
    services.AddSingleton<IMapper>(sp => sp.GetRequiredService<MapperConfiguration>().CreateMapper());
    return services;
}
```

### 4.5 Surface NOT changed

- `IMapper`, `MapperProfile`, `MapperConfigurationExpression` — no new methods.
- `IMemberConfigurationExpression`, `ITypeConverter`, `MemberList`, `AtlasConfigurationException`, `AtlasMappingException` — unchanged.
- `ForMember`, `ForCtorParam`, `ForPath`, `ReverseMap`, `Include`, `IncludeBase`, enum methods (`MapByValue`, `MapByName`, `MapValue`, `Ignore`, `WithFallback`) — unchanged.

### 4.6 Worked-example fluent

```csharp
public sealed class AuditAction : IMappingAction<Order, OrderDto>
{
    private readonly ILogger<AuditAction> _log;
    public AuditAction(ILogger<AuditAction> log) => _log = log;
    public void Process(Order src, OrderDto dst) => _log.LogInformation("Mapped Order {Id}", src.Id);
}

public class OrderProfile : MapperProfile
{
    public OrderProfile()
    {
        CreateMap<Order, OrderDto>()
            .ForMember(d => d.OrderTotal, opt => opt.MapFrom(s => s.Subtotal + s.Tax))
            .BeforeMap((s, d) => s.NormalizeFields())
            .AfterMap<AuditAction>();
    }
}

// Wiring:
services.AddLogging();
services.AddAtlas(typeof(OrderProfile).Assembly);
```

---

## 5. Internal Architecture

### 5.1 `HookEntry` (new internal record)

```csharp
// src/Atlas/Internal/HookEntry.cs
namespace Atlas.Internal;

/// <summary>
/// One BeforeMap or AfterMap registration. Exactly one of <see cref="Lambda"/> or
/// <see cref="ActionType"/> is non-null.
/// </summary>
internal sealed record HookEntry(Delegate? Lambda, Type? ActionType)
{
    public static HookEntry FromLambda(Delegate lambda) =>
        new(lambda ?? throw new ArgumentNullException(nameof(lambda)), null);

    public static HookEntry FromActionType(Type actionType) =>
        new(null, actionType ?? throw new ArgumentNullException(nameof(actionType)));
}
```

### 5.2 `TypeMap` additions

```csharp
// src/Atlas/Internal/TypeMap.cs (additions only)

/// <summary>
/// Hooks that run BEFORE any destination member is mapped, in FIFO order (registration
/// order at the user's call site). After <see cref="InheritanceMerger"/> runs, this list
/// also contains base TypeMaps' BeforeHooks prepended at the front (base-first order).
/// </summary>
public List<HookEntry> BeforeHooks { get; } = new();

/// <summary>
/// Hooks that run AFTER all destination members are mapped, in FIFO order. After
/// <see cref="InheritanceMerger"/> runs, this list also contains base TypeMaps' AfterHooks
/// appended at the end (so unwind goes derived-first then base-last, pairing with
/// <see cref="BeforeHooks"/>'s base-first order).
/// </summary>
public List<HookEntry> AfterHooks { get; } = new();
```

### 5.3 `MappingExpression<,>` implementations

```csharp
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

### 5.4 `MapperRegistry` additions

```csharp
// src/Atlas/Internal/MapperRegistry.cs (additions only)

public IServiceProvider? ServiceProvider { get; }

/// <summary>
/// Cached <see cref="IMappingAction{TSource, TDestination}"/> instances keyed by action type.
/// Populated by <see cref="HookResolver"/> at codegen time; one entry per distinct action type
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

### 5.5 `HookResolver`

```csharp
// src/Atlas/Internal/HookResolver.cs
namespace Atlas.Internal;

internal static class HookResolver
{
    /// <summary>
    /// Resolves a <see cref="HookEntry"/> to a strongly-typed <c>Action&lt;TSource, TDestination&gt;</c>.
    /// For lambda entries, returns the captured delegate cast to the typed shape.
    /// For action-type entries, instantiates ONCE via <c>ActivatorUtilities.CreateInstance</c>
    /// (when SP is non-null) or <c>Activator.CreateInstance</c> (when SP is null), caches the
    /// instance in <see cref="MapperRegistry.ActionInstances"/>, and returns the instance's
    /// <c>Process</c> method as a delegate.
    /// </summary>
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

        // Action-type path.
        var actionType = entry.ActionType!;
        if (!registry.ActionInstances.TryGetValue(actionType, out var instance))
        {
            instance = registry.ServiceProvider is { } sp
                ? ActivatorUtilities.CreateInstance(sp, actionType)
                : Activator.CreateInstance(actionType)
                    ?? throw new InvalidOperationException(
                        $"Action type {actionType.Name} could not be activated. " +
                        "When Atlas is used without the DI extension, the action must have a public parameterless constructor.");
            registry.ActionInstances[actionType] = instance;
        }

        var action = (IMappingAction<TSource, TDestination>)instance;
        return action.Process;
    }
}
```

### 5.6 `InheritanceMerger` extension

The existing merger walks `IncludedBases` topologically (most-base-first) and merges PropertyMaps into the derived TypeMap. The hook merge is added to the same loop:

```
For each derived TypeMap d (in topological order, most-base maps first so by the time we
reach d, all its bases have already been merged with their own ancestors):
    For each baseTypeMap b in d.IncludedBases:
        // Existing PropertyMap merge (unchanged).
        MergePropertyMaps(b, d);

        // NEW: Hook merge.
        // BeforeHooks: prepend base's hooks so they run FIRST at runtime (base-first).
        d.BeforeHooks.InsertRange(0, b.BeforeHooks);

        // AfterHooks: append base's hooks so they run LAST at runtime (stack-unwind order).
        d.AfterHooks.AddRange(b.AfterHooks);
```

**Concrete trace — 3-level chain `LivingThing → Animal → Cat`:**

```
Pre-merge:
  LivingThing.BeforeHooks = [LT_B];  AfterHooks = [LT_After]
  Animal.IncludedBases    = [LivingThing]
  Animal.BeforeHooks      = [A_B];   AfterHooks = [A_After]
  Cat.IncludedBases       = [Animal]
  Cat.BeforeHooks         = [C_B];   AfterHooks = [C_After]

Topological order (most-base first): LivingThing, Animal, Cat.

Merge step 1 — LivingThing into itself (no IncludedBases): no-op.

Merge step 2 — Animal: process IncludedBases = [LivingThing].
  Animal.BeforeHooks.InsertRange(0, LivingThing.BeforeHooks)
                                 → [LT_B, A_B]
  Animal.AfterHooks.AddRange(LivingThing.AfterHooks)
                                 → [A_After, LT_After]

Merge step 3 — Cat: process IncludedBases = [Animal] (now with merged hooks).
  Cat.BeforeHooks.InsertRange(0, Animal.BeforeHooks)
                                 → [LT_B, A_B, C_B]
  Cat.AfterHooks.AddRange(Animal.AfterHooks)
                                 → [C_After, A_After, LT_After]
```

Runtime hook order for mapping a Cat: `LT_B → A_B → C_B → [property mapping] → C_After → A_After → LT_After` — exactly the stack-unwind semantic.

### 5.7 `ConfigurationValidator` extension

Add an always-on rule called between `ValidatePaths` and `ValidateInheritance`:

```csharp
// src/Atlas/Internal/ConfigurationValidator.cs
public static void Validate(
    MapperRegistry registry,
    bool enumValidationEnabled = false,
    IServiceProvider? serviceProvider = null)   // NEW PARAM
{
    var errors = new List<ConfigurationError>();
    foreach (var tm in registry.AllTypeMaps)
    {
        ValidateEnum(tm, errors);
        ValidatePaths(tm, errors);
        ValidateHooks(tm, serviceProvider, errors);   // NEW
        ValidateInheritance(tm, registry, errors);
        // ... rest unchanged ...
    }
    if (errors.Count > 0) throw new AtlasConfigurationException(errors);
}

private static void ValidateHooks(TypeMap tm, IServiceProvider? sp, List<ConfigurationError> errors)
{
    foreach (var entry in tm.BeforeHooks.Concat(tm.AfterHooks))
    {
        if (entry.ActionType is null) continue;   // lambda entries are always valid

        var actionType = entry.ActionType;

        // 1. The action type must implement IMappingAction<TSource, TDestination> for THIS map's pair.
        var expectedInterface = typeof(IMappingAction<,>).MakeGenericType(tm.SourceType, tm.DestinationType);
        if (!expectedInterface.IsAssignableFrom(actionType))
        {
            errors.Add(new ConfigurationError(
                tm.SourceType, tm.DestinationType, "(BeforeMap/AfterMap)",
                $"Action type {actionType.Name} does not implement IMappingAction<{tm.SourceType.Name}, {tm.DestinationType.Name}>."));
            continue;
        }

        // 2. Construction check: eager construction surfaces clearer errors.
        try
        {
            var instance = sp is not null
                ? ActivatorUtilities.CreateInstance(sp, actionType)
                : Activator.CreateInstance(actionType);
            if (instance is null)
                errors.Add(new ConfigurationError(
                    tm.SourceType, tm.DestinationType, "(BeforeMap/AfterMap)",
                    $"Action type {actionType.Name} could not be constructed."));
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

`MapperConfiguration.AssertConfigurationIsValid()` passes the SP through:

```csharp
public void AssertConfigurationIsValid() =>
    ConfigurationValidator.Validate(_registry, _enumValidationEnabled, _serviceProvider);
```

### 5.8 `MapperConfiguration` plumbing

Adds an internal `IServiceProvider? _serviceProvider` field plus the two new public constructors. The existing constructors continue to work (pass `null` SP through, equivalent to "no DI"). The constructed `MapperRegistry` receives the SP.

---

## 6. Compilation Algorithm

### 6.1 Where the change lives

`ExecutionPlanBuilder.BuildPocoLambda` and `BuildUpdate` both gain a hook-emission step. The current shape of `BuildPocoLambda` (after Task 4 of feature #4) is approximately:

```csharp
statements.Add(Expression.Assign(destVar, newDest));   // dst = new T() or ctor-init

foreach (var pm in propertyMaps)
{
    // ... existing per-binding emit (single-level Expression.Assign or BuildNestedAssign) ...
}

statements.Add(destVar);   // return value
```

After the change:

```csharp
statements.Add(Expression.Assign(destVar, newDest));

// NEW: emit BeforeHooks (in order).
foreach (var hook in typeMap.BeforeHooks)
    statements.Add(BuildHookCall(hook, srcParam, destVar, registry));

foreach (var pm in propertyMaps)
{
    // ... existing per-binding emit ...
}

// NEW: emit AfterHooks (in order).
foreach (var hook in typeMap.AfterHooks)
    statements.Add(BuildHookCall(hook, srcParam, destVar, registry));

statements.Add(destVar);
```

`BuildUpdate` follows the same pattern but with `destParam` (the existing destination) instead of `destVar` and no `Assign(destVar, newDest)` step.

### 6.2 `BuildHookCall` helper

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

    return Expression.Invoke(Expression.Constant(typedDelegate), srcExpr, destExpr);
}
```

`Expression.Constant(typedDelegate)` captures the delegate into the compiled body. `typedDelegate.Invoke(src, dst)` runs at execution time — for lambda entries this is the user's lambda; for action-type entries this is the cached instance's `Process` method group.

### 6.3 Concrete trace — single forward map

User code:
```csharp
CreateMap<Order, OrderDto>()
    .BeforeMap((s, d) => s.NormalizeFields())
    .AfterMap<AuditAction>();
```

Compiled lambda body (pseudocode):
```csharp
(Order src) => {
    var dst = new OrderDto();
    beforeHook0.Invoke(src, dst);             // user lambda: NormalizeFields
    dst.OrderTotal = src.Subtotal + src.Tax;
    dst.CustomerName = src.Customer.Name;
    afterHook0.Invoke(src, dst);              // cached AuditAction.Process delegate
    return dst;
}
```

### 6.4 Concrete trace — inheritance dispatch interaction

`CreateMap<Animal, AnimalDto>().BeforeMap(A_B).AfterMap(A_A).Include<Dog, DogDto>()` plus `CreateMap<Dog, DogDto>().IncludeBase<Animal, AnimalDto>().BeforeMap(D_B).AfterMap(D_A)`.

After `InheritanceMerger`:
- `DogDto`'s TypeMap has `BeforeHooks = [A_B, D_B]`, `AfterHooks = [D_A, A_A]`.

The Animal→AnimalDto compiled lambda includes inheritance dispatch (per feature #2):
```csharp
(Animal src) => {
    if (src is Dog d) return (AnimalDto)MappingInvoker.Invoke<Dog, DogDto>(registry, d);
    var dst = new AnimalDto();
    A_B.Invoke(src, dst);                     // base BeforeMap (only fires when runtime type IS Animal, not Dog)
    // ... property assignments ...
    A_A.Invoke(src, dst);                     // base AfterMap
    return dst;
}
```

The Dog→DogDto compiled lambda runs all merged hooks:
```csharp
(Dog src) => {
    var dst = new DogDto();
    A_B.Invoke(src, dst);                     // base BeforeMap (merged in)
    D_B.Invoke(src, dst);                     // derived BeforeMap
    // ... property assignments ...
    D_A.Invoke(src, dst);                     // derived AfterMap
    A_A.Invoke(src, dst);                     // base AfterMap
    return dst;
}
```

**Key observation:** when calling `mapper.Map<AnimalDto>(someDog)`, the Animal lambda dispatches to the Dog lambda — and the Dog lambda's merged hooks fire. The Animal lambda's base hooks (`A_B`, `A_A`) do NOT fire twice because the dispatch returns before reaching them. The merge pre-baked them into the Dog lambda.

### 6.5 Concrete trace — collection mapping

User maps `List<Order> → List<OrderDto>`. Atlas's collection codegen calls `MappingInvoker.Invoke<Order, OrderDto>` per element. Each per-element call invokes the `(Order, OrderDto)` lambda — which fires its hooks once. Result: hooks fire ONCE per element, never at the collection level. This matches user expectations (most hook scenarios — "log each mapped order" — naturally want per-element fires).

### 6.6 Concrete trace — update-in-place

User calls `mapper.Map<Order, OrderDto>(src, existingDto)`. `BuildUpdate` produces:
```csharp
(Order src, OrderDto dest) => {
    if (src is not null) {
        beforeHook0.Invoke(src, dest);        // BeforeMap fires before any property is overwritten
        dest.OrderTotal = src.Subtotal + src.Tax;
        // ... other property assigns ...
        afterHook0.Invoke(src, dest);         // AfterMap fires after all assigns
    }
}
```

Hooks fire correctly for both create and update paths because both code paths share the emission pattern.

### 6.7 `Atlas.Projections` rejection

```csharp
// src/Atlas.Projections/Internal/ProjectionPlanBuilder.cs (new check at top of Build/BuildBody)
if (typeMap.BeforeHooks.Count > 0 || typeMap.AfterHooks.Count > 0)
{
    throw new AtlasConfigurationException(new List<ConfigurationError>
    {
        new(typeMap.SourceType, typeMap.DestinationType, "(BeforeMap/AfterMap)",
            $"Cannot project ({typeMap.SourceType.Name}, {typeMap.DestinationType.Name}): " +
            $"map has {typeMap.BeforeHooks.Count} BeforeMap and {typeMap.AfterHooks.Count} AfterMap hook(s). " +
            "Hooks are not translatable to IQueryable. Use mapper.Map<>() instead, or remove the hooks.")
    });
}
```

This rejection fires at the `ProjectTo<T>` call site, mirroring the DestinationPath rejection from feature #4. The check also applies to NESTED maps invoked during projection — `ProjectionPlanBuilder.BuildBinding` recursively constructs nested maps, and each invocation goes through the same `Build` entry point that has the hook check.

`ProjectionCompatibility.IsBindingProjectable` is also extended to reject any binding whose target nested TypeMap has hooks (so the rejection surfaces at the most specific level when the user has a hook on a nested map).

---

## 7. TDD Plan

11 implementation tasks, ~38 new tests. Same TDD-first cadence as features #2-4.

### 7.1 `Internal/HookEntryTests.cs` — 3 tests

- `FromLambda_StoresLambdaAndNullActionType`
- `FromActionType_StoresActionTypeAndNullLambda`
- `FromLambda_NullDelegate_Throws` and `FromActionType_NullType_Throws`

### 7.2 `MappingExpressionBeforeAfterMapTests.cs` — 8 tests

- `BeforeMap_Lambda_AppendsToBeforeHooks`
- `BeforeMap_ActionType_AppendsToBeforeHooks`
- `AfterMap_Lambda_AppendsToAfterHooks`
- `AfterMap_ActionType_AppendsToAfterHooks`
- `MultipleBeforeMap_PreservesFifoOrder`
- `MultipleAfterMap_PreservesFifoOrder`
- `BeforeMap_NullLambda_Throws`
- `BeforeMap_ReturnsExpression_ForChaining`

### 7.3 `Internal/InheritanceMergerHookTests.cs` — 5 tests

- `Merge_OneLevel_BeforeHooks_PrependsBase`
- `Merge_OneLevel_AfterHooks_AppendsBase`
- `Merge_ThreeLevelChain_BaseFirstOrder` (the §5.6 trace)
- `Merge_DerivedOnly_NoBaseHooks_Unchanged`
- `Merge_PreservesPropertyMapMergeBehavior` (regression — existing PropertyMap merge still works)

### 7.4 `Internal/HookResolverTests.cs` — 5 tests

- `Resolve_LambdaEntry_ReturnsTypedDelegate`
- `Resolve_ActionType_NoDI_RequiresParameterlessCtor`
- `Resolve_ActionType_DI_ConstructsViaActivatorUtilities`
- `Resolve_ActionType_CachedAcrossCalls` (multiple resolves return same instance)
- `Resolve_ActionTypeWithoutCtor_NoDI_Throws`

### 7.5 `ConfigurationValidatorHookTests.cs` — 4 tests

- `ValidateHooks_ActionTypeWithoutParameterlessCtor_NoDI_Errors`
- `ValidateHooks_ActionTypeNotImplementingInterface_Errors`
- `ValidateHooks_ValidLambdaAndAction_Pass`
- `ValidateHooks_ScopedServiceDependency_Errors_WithClearMessage`

### 7.6 `ExecutionPlanBuilderHookTests.cs` — 4 tests

- `BuildPocoLambda_EmitsBeforeHooksAtTop`
- `BuildPocoLambda_EmitsAfterHooksAtBottom`
- `BuildUpdate_EmitsHooksToo`
- `NoHooks_NoExtraStatementsEmitted`

### 7.7 `MapperBeforeAfterMapTests.cs` — 7 end-to-end tests

- `BeforeMap_Lambda_FiresOncePerMap`
- `BeforeMap_FiresPerElement_OnCollectionMapping`
- `MultipleHooks_FireInFifoOrder`
- `Inheritance_HookOrder_MatchesStackUnwind` (Cat→CatDto with Animal base, asserts `[LT_B, A_B, C_B, ..., C_A, A_A, LT_A]`)
- `IMappingAction_DI_ResolvesAndCallsProcess`
- `Hook_ExceptionPropagates` (no swallowing)
- `UpdateInPlace_FiresHooks`

### 7.8 `Atlas.Projections.Tests/ProjectionRejectsHooksTests.cs` — 2 tests

- `ProjectTo_ForwardMapWithBeforeMap_ThrowsNamingHookCount`
- `ProjectTo_ForwardMapWithAfterMap_ThrowsNamingHookCount`

### 7.9 Implementation tasks (commit-by-commit)

| # | Task | Tests | Model |
|---|---|---|---|
| 1 | Branch setup (`feat/before-after-hooks` from main HEAD) | 0 | manual |
| 2 | Data model: `IMappingAction<,>` interface, `HookEntry` record, `TypeMap.BeforeHooks`/`AfterHooks` lists | 3 | haiku |
| 3 | `MappingExpression` public API: 4 fluent methods (`BeforeMap`/`AfterMap` × lambda/interface) | 8 | haiku |
| 4 | `MapperConfiguration` SP plumbing (new ctor overloads; `MapperRegistry.ServiceProvider` + `ActionInstances` cache) + DI extension wiring (`AddAtlas` passes container's SP through) | 0 | sonnet |
| 5 | `HookResolver` — resolve lambda + action-type; cache action instances; ActivatorUtilities call when SP present | 5 | sonnet |
| 6 | `InheritanceMerger` hook merge (prepend base.BeforeHooks; append base.AfterHooks) | 5 | sonnet |
| 7 | `ConfigurationValidator` hook validation (parameterless ctor when no DI; IMappingAction interface check; eager construction surfaces scoped-service errors) | 4 | sonnet |
| 8 | `ExecutionPlanBuilder` hook emission in `BuildPocoLambda` + `BuildUpdate` | 4 | sonnet |
| 9 | End-to-end `MapperBeforeAfterMapTests` | 7 | sonnet |
| 10 | `Atlas.Projections` rejection (extend `ProjectionPlanBuilder` + `ProjectionCompatibility`) | 2 | haiku |
| 11 | README "Before/after hooks" section + remove deferred entry + coverage check | 0 | haiku |

**Total: ~38 new tests.** Baseline 324 → ~362 after this lands. Coverage targets carried forward: line ≥ 90%, branch ≥ 80% on `Atlas` core.

---

## 8. Risks & Open Questions

### 8.1 Things to trace concretely during plan-writing (per pseudocode-trace memory)

1. **InheritanceMerger walk order — 3-level chain.** §5.6 has the worked trace. Plan must include this trace verbatim AND a test (`Merge_ThreeLevelChain_BaseFirstOrder`) that constructs the 3-level chain and asserts the resulting merged hook lists are exactly `[LT_B, A_B, C_B]` and `[C_After, A_After, LT_After]`. Without this trace, the off-by-one risk (re-inserting base hooks per level) is real.

2. **Cross-package consumer audit (Bug 4 lesson).** `BeforeHooks` and `AfterHooks` are new fields on `TypeMap` consumed by `Atlas.Projections.ProjectionPlanBuilder` (rejection at projection-build) AND `Atlas.Projections.Internal.ProjectionCompatibility` (rejection of nested bindings whose target maps have hooks). Plan must:
   - Grep `Atlas.Projections` for every `typeMap.` access and confirm Task 10's rejection covers all entry points (`Build`, `BuildBody`, `BuildBinding`'s nested-map invocation).
   - Add 2 tests in `Atlas.Projections.Tests/ProjectionRejectsHooksTests.cs` covering both top-level and nested-map cases.

3. **Inheritance dispatch + hook emission interaction.** When `(Animal, AnimalDto)` has `Include<Dog, DogDto>()`, the Animal lambda includes runtime dispatch to Dog. Hooks on the Animal map fire ONLY when the runtime type is exactly Animal (not Dog). The Dog map's merged hooks (which include Animal's hooks via merger) fire when the runtime type is Dog. Plan must trace this concretely — see §6.4. Test required: `mapper.Map<AnimalDto>(new Dog())` fires `[LT_B, A_B, D_B, ..., D_A, A_A, LT_A]`, NOT Animal's hooks twice.

4. **DI scoped-service mismatch surfaces at validate time, not first-Map time.** Per §5.7's eager construction, scoped-service deps surface during `AssertConfigurationIsValid()` with a message that includes the underlying .NET DI exception message. Plan must include test: an action with an `[Scoped]` ctor dep registered via `services.AddScoped<TScoped>()` triggers a clear validation error. Without eager construction, the user sees the generic .NET message at first `Map` call, which is harder to debug.

5. **Lambda hook closure mutability.** Lambdas captured via `.BeforeMap((s, d) => ...)` can close over mutable user state. Atlas compiles once and caches the delegate — the closure's captured state lives forever. README note required: "hook lambdas should be pure (or capture only thread-safe state) — `MapperConfiguration` is a singleton; the closure persists for the application lifetime."

6. **Action instance cache scope.** `MapperRegistry.ActionInstances` is keyed by action type. Two TypeMaps using the same action type share ONE instance. Two `MapperConfiguration` instances each have their own cache. Plan must verify: a test that creates two TypeMaps both using `AuditAction` and asserts the same instance is reused (`Assert.Same`). Also verifies `ActivatorUtilities.CreateInstance` is called only once per action type per config.

7. **`AssertConfigurationIsValid` SP plumbing — backward compatibility.** Adding the `IServiceProvider?` param to `Validate` is a backward-compatible change (default null). Plan must verify: existing tests that call `Validate(registry, enumValidationEnabled)` without the SP param still compile and behave identically when SP is null.

8. **Reverse map non-propagation.** Per scope, hooks DO NOT auto-propagate via `.ReverseMap()`. Plan must include test: forward map with `.BeforeMap(...).ReverseMap()` produces a reverse TypeMap with empty `BeforeHooks` and `AfterHooks`. The `ReverseMapMirror.Mirror` algorithm only iterates `forward.PropertyMaps`, never `Hooks` — verify by code-reading after Task 6.

### 8.2 Explicitly deferred to v3

- `ResolutionContext` / context bag for hooks (cross-cutting; design with #6, #7).
- Scoped-service support for `IMappingAction` (per-call resolution from per-call SP).
- Async hook support (`Func<TSource, TDestination, Task>`).
- Reverse-map propagation of hooks.
- Collection-level hooks (vs per-element).

### 8.3 Open questions for the implementing session to push back on

- **Eager vs lazy validation construction.** §5.7 recommends eager construction in `ValidateHooks` for clearer errors. If this turns out to be expensive (some users may have hundreds of actions), implementer can switch to lazy validation that only checks the type implements the interface. The eager-vs-lazy tradeoff is small because actions are typically cheap to construct and validation only runs on `AssertConfigurationIsValid()`.
- **Lambda capture mode for `HookResolver`.** Currently the plan stores `Action<TSource, TDestination>` as `Delegate? Lambda` and uses `MakeGenericMethod` reflection in `HookResolver.Resolve`. If profiling shows this codegen-time reflection cost matters (it's once per hook per config, but configs with many hooks could add up), alternative is to store `Action<object, object>` after a one-time wrapping at the user's call site — small ergonomic loss in typed access but zero codegen reflection.
- **`ActivatorUtilities` package dependency.** `Microsoft.Extensions.DependencyInjection.Abstractions` is the package that provides `ActivatorUtilities`. Atlas core currently doesn't depend on it. Plan must decide: (a) add the dependency to `Atlas` core (low cost — Abstractions is small and ubiquitous in modern .NET), or (b) define the helper interface in Atlas and have the DI extension register an `IServiceProvider` that knows how to invoke `ActivatorUtilities` indirectly. Recommendation: (a) — `Microsoft.Extensions.DependencyInjection.Abstractions` is already a transitive dep through other places and Atlas users are overwhelmingly .NET-DI users.

---

## 9. Appendix A — Worked Example

### 9.1 User code

```csharp
public sealed class Customer
{
    public string? Name { get; set; }
    public string? Email { get; set; }
}

public sealed class Order
{
    public int Id { get; set; }
    public Customer? Customer { get; set; }
    public decimal Subtotal { get; set; }
    public decimal Tax { get; set; }

    // Hook target — normalize the customer email to lowercase BEFORE mapping.
    public void NormalizeFields()
    {
        if (Customer is { Email: { } e }) Customer.Email = e.Trim().ToLowerInvariant();
    }
}

public sealed class OrderDto
{
    public string? CustomerName { get; set; }
    public string? CustomerEmail { get; set; }
    public decimal OrderTotal { get; set; }
}

public sealed class AuditAction : IMappingAction<Order, OrderDto>
{
    private readonly ILogger<AuditAction> _log;
    public AuditAction(ILogger<AuditAction> log) => _log = log;
    public void Process(Order src, OrderDto dst) =>
        _log.LogInformation("Mapped Order {Id} → DTO with Total {Total}", src.Id, dst.OrderTotal);
}

public class OrderProfile : MapperProfile
{
    public OrderProfile()
    {
        CreateMap<Order, OrderDto>()
            .ForMember(d => d.OrderTotal, opt => opt.MapFrom(s => s.Subtotal + s.Tax))
            .BeforeMap((s, d) => s.NormalizeFields())
            .AfterMap<AuditAction>();
    }
}
```

### 9.2 DI wiring

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddLogging();
builder.Services.AddAtlas(typeof(OrderProfile).Assembly);
var app = builder.Build();

// Resolve the singleton mapper.
var mapper = app.Services.GetRequiredService<IMapper>();
```

### 9.3 Build trace

1. **Profile.Configure().** Forward `(Order, OrderDto)` TypeMap registered:
   - `PropertyMaps`: `[OrderTotal IsExplicit + CustomExpression for s.Subtotal+s.Tax]`.
   - `BeforeHooks`: `[FromLambda(NormalizeFields delegate)]`.
   - `AfterHooks`: `[FromActionType(typeof(AuditAction))]`.
2. **InheritanceMerger.Resolve.** No Includes; no-op.
3. **ConventionEngine.ResolveMissingMembers.** Discovers `CustomerName → [Customer, Name]` and `CustomerEmail → [Customer, Email]`. PropertyMaps now: `[OrderTotal explicit, CustomerName conv, CustomerEmail conv]`.
4. **ReverseMapMirror.Mirror.** No `.ReverseMap()` call; no-op.
5. **`tm.Seal()`.**
6. **`AssertConfigurationIsValid()` (called explicitly):**
   - `ValidateEnum`, `ValidatePaths`, `ValidateInheritance` all pass.
   - `ValidateHooks`:
     - `BeforeHooks[0]` is a lambda — skipped (always valid).
     - `AfterHooks[0]` is `AuditAction`. Interface check passes (`IMappingAction<Order, OrderDto>` is implemented). Eager construction via `ActivatorUtilities.CreateInstance(rootSp, typeof(AuditAction))` succeeds (logger is singleton). Cached in `registry.ActionInstances`.
   - No errors.
7. **`CompileMappings()`.** Codegen reads merged hook lists and emits:
   ```csharp
   (Order src) => {
       var dst = new OrderDto();
       beforeHook0.Invoke(src, dst);             // user lambda (NormalizeFields)
       dst.OrderTotal = src.Subtotal + src.Tax;
       dst.CustomerName = src.Customer.Name;
       dst.CustomerEmail = src.Customer.Email;
       afterHook0.Invoke(src, dst);              // cached AuditAction.Process delegate
       return dst;
   }
   ```

### 9.4 Runtime use

```csharp
var entity = new Order
{
    Id = 7,
    Customer = new Customer { Name = "Alice", Email = "  ALICE@X.COM  " },
    Subtotal = 90m,
    Tax = 10m,
};

var dto = mapper.Map<OrderDto>(entity);
// Step-by-step:
//   1. dst = new OrderDto()
//   2. NormalizeFields runs — entity.Customer.Email becomes "alice@x.com" (lowercased + trimmed)
//   3. dst.OrderTotal = 100m
//   4. dst.CustomerName = "Alice"
//   5. dst.CustomerEmail = "alice@x.com"
//   6. AuditAction.Process logs "Mapped Order 7 → DTO with Total 100"

Assert dto.CustomerEmail == "alice@x.com";
```

Note: the BeforeMap mutates the SOURCE (`entity.Customer.Email`) before mapping. This is by-design for normalization patterns. Users who want immutable sources should keep BeforeMap effects scoped to the destination only.

---

## 10. Implementation Checklist

For the implementing Claude session. Each row is a self-contained commit.

- [ ] **Task 1 — Branch setup.** Cut `feat/before-after-hooks` from `main`. Verify clean baseline (324 tests).
- [ ] **Task 2 — Data model.** New `IMappingAction<,>` public interface; new `HookEntry` internal record; `TypeMap.BeforeHooks`/`AfterHooks` fields. Tests: `Internal/HookEntryTests.cs` (3 tests).
- [ ] **Task 3 — Public API.** Add 4 methods (`BeforeMap`/`AfterMap` × lambda/interface) to `IMappingExpression<,>` and implement in `MappingExpression<,>`. Tests: `MappingExpressionBeforeAfterMapTests.cs` (8 tests).
- [ ] **Task 4 — SP plumbing.** New `MapperConfiguration` ctor overloads accepting `IServiceProvider`; `MapperRegistry.ServiceProvider` + `ActionInstances` cache; DI extension wiring (`AddAtlas` passes SP into `MapperConfiguration`). No new tests — exercised by Tasks 5, 7, 9.
- [ ] **Task 5 — `HookResolver`.** New `Internal/HookResolver.cs`. Lambda passthrough; action-type with parameterless ctor (no DI); action-type via `ActivatorUtilities.CreateInstance` (DI); per-action-type instance caching. Tests: `Internal/HookResolverTests.cs` (5 tests).
- [ ] **Task 6 — Inheritance merge.** Extend `InheritanceMerger.Resolve`: prepend base BeforeHooks; append base AfterHooks. Tests: `Internal/InheritanceMergerHookTests.cs` (5 tests).
- [ ] **Task 7 — Validator.** Extend `ConfigurationValidator.Validate` with SP param + `ValidateHooks` rule (interface check + eager construction). Tests: `ConfigurationValidatorHookTests.cs` (4 tests).
- [ ] **Task 8 — Codegen.** Extend `ExecutionPlanBuilder.BuildPocoLambda` and `BuildUpdate` with hook emission. Tests: `ExecutionPlanBuilderHookTests.cs` (4 tests).
- [ ] **Task 9 — End-to-end.** `MapperBeforeAfterMapTests.cs` (7 tests covering single map, collection per-element, FIFO, inheritance order, `IMappingAction` via DI, exception propagation, update-in-place).
- [ ] **Task 10 — Projection rejection.** Extend `Atlas.Projections.Internal.ProjectionPlanBuilder` to reject TypeMaps with hooks at projection-build; extend `ProjectionCompatibility` for nested-map cases. Tests: `Atlas.Projections.Tests/ProjectionRejectsHooksTests.cs` (2 tests).
- [ ] **Task 11 — README + coverage.** Add `## Before/after hooks` section; remove "Before/after hooks" from deferred-features list; verify line ≥ 90% / branch ≥ 80% on `Atlas` core.

**Final holistic review** by `superpowers:code-reviewer` over the whole branch before merge — per the established workflow rhythm. Three of four prior features have surfaced 1+ critical/important issue at this stage that per-task reviews missed. Don't skip.
