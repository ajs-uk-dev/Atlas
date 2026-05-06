# Atlas

A fluent, high-performance object-to-object mapper for .NET 10+.

Atlas compiles your mapping configuration once at startup into delegates and serves
every subsequent `Map<TSource, TDestination>(source)` call as a dictionary lookup
plus a delegate invocation. No reflection on the hot path.

## Requirements

- **.NET 10** or later (uses preview language features and `System.Threading.Lock`).

## Quick start

```csharp
using Atlas;

public class OrderProfile : MapperProfile
{
    public OrderProfile()
    {
        CreateMap<OrderEntity, OrderDto>();
        CreateMap<CustomerEntity, CustomerDto>();
    }
}

var configuration = new MapperConfiguration(cfg =>
{
    cfg.AddProfile<OrderProfile>();
});
configuration.CompileMappings();           // eager — pay JIT cost at startup
configuration.AssertConfigurationIsValid(); // run once from a unit test

IMapper mapper = configuration.CreateMapper();
OrderDto dto = mapper.Map<OrderEntity, OrderDto>(entity);
```

## Dependency injection

The `Atlas.Extensions.DependencyInjection` package adds the standard registration
shape:

```csharp
using Atlas;

services.AddAtlas(typeof(Program).Assembly); // scans for MapperProfile subclasses
```

Both `MapperConfiguration` and `IMapper` are registered as singletons. Profiles
must be top-level public classes with a public parameterless constructor;
violations throw `AtlasConfigurationException` at registration time.

## Queryable projection (`Atlas.Projections`)

Optional package that translates a configured map into a LINQ expression and applies it as a `Select` over an `IQueryable`. Designed for EF Core read paths.

```csharp
using Atlas.Projections;

var dtos = db.Blogs
    .Where(b => b.Year >= 2025)
    .ProjectTo<BlogDto>(configuration)
    .ToList();
```

The configuration is validated eagerly at the call site; non-projectable constructs (delegate-form `ConvertUsing`, missing maps, unmapped destination members) throw `AtlasProjectionException` listing every problem. Default recursion depth is 3 (per-call override available).

See `docs/Atlas-Design-ProjectTo.md` for the full design.

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

**Foot-gun**: an explicit `Ignore` on the base **overrides** convention on the derived. If you ignore `Animal.Name` on the base map, Dog will also ignore `Name` even if Dog has a matching `Name` property by convention. This is the standard semantics (consistent with AutoMapper) but commonly catches people out — keep it in mind when refactoring inheritance.

**ProjectTo limitation (v1)**: today's `Atlas.Projections` package is unaware of `Include` declarations. A `query.ProjectTo<AnimalDto>(cfg)` against a polymorphic `DbSet<Animal>` projects every row as `AnimalDto` — derived rows lose their derived shape silently. A future v3 design will lift this limitation.

See `docs/Atlas-Design-Inheritance.md` for the full design.

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

See `docs/Atlas-Design-EnumSurface.md` for the full design.

## Reverse mapping

Declare both directions with one call. Forward conventions and source-side flattening
auto-invert; the reverse map defaults to `MemberList.None`:

```csharp
public class OrderProfile : MapperProfile
{
    public OrderProfile()
    {
        CreateMap<Order, OrderDto>()
            .ForMember(d => d.OrderTotal, opt => opt.MapFrom(s => s.Subtotal + s.Tax))
            .ReverseMap();   // returns IMappingExpression<OrderDto, Order>
    }
}
```

Forward `Customer.Name → CustomerName` flattening becomes reverse `CustomerName → Customer.Name`
unflattening — intermediates are auto-instantiated via parameterless constructors. Use `ForPath`
on either direction to override or configure nested chains explicitly:

```csharp
.ReverseMap()
.ForPath(d => d.Pricing.Total, opt => opt.MapFrom(s => s.OrderTotal))
```

**What does NOT auto-invert** (reconfigure on the returned reverse expression if needed):
- `ForMember(MapFrom(expression))` — the forward expression is not inverted.
- `Ignore()` — does not propagate to the reverse direction.
- `ConvertUsing` — custom converters generally are not invertible.
- `Include`/`IncludeBase` — inheritance chains are not reversed.
- Enum per-value overrides — the reverse pair gets default ByValue strategy with no overrides.
- Constructor parameter mappings (`ForCtorParam`).

**Foot-gun guards** (caught by `AssertConfigurationIsValid`):
- Each intermediate type in a `ForPath` or mirrored unflatten path must have a public parameterless constructor.
- Each intermediate property must have a public setter.
- Declaring both `CreateMap<D, S>()` and `CreateMap<S, D>().ReverseMap()` for the same pair throws — pick one.

> **Note:** an explicit `.ForMember(d => d.X, ...)` on the reverse map suppresses mirroring of any `X.*` flattened bindings — the user is treated as writing X wholesale. Use `.ForPath(d => d.X.Y, ...)` instead if you want to override one leaf while keeping the other mirrored bindings active.

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
- With DI, scoped-service dependencies surface as a clear error at validate time (when the SP is built with `ValidateScopes = true`).

**ProjectTo limitation.** Hooks are not translatable to IQueryable. Calling
`query.ProjectTo<TDestination>()` against a map with any hooks throws
`AtlasProjectionException` at projection-build time naming the hook count and pointing
to `Map<>()` instead.

**Hooks do NOT auto-propagate via `.ReverseMap()`** — configure hooks on the reverse
expression separately if needed.

## Value transformers

Apply post-processing functions to every value of a given destination type, registered at
three scopes — **global** on `MapperConfigurationExpression`, **profile** on `MapperProfile`,
and **type-map** on the fluent surface (`AddTransform<T>`). Composition is broad-first
(`global → profile → type-map`); within each scope, transformers run in registration order.

```csharp
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
```

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

## Conditional mapping

Two per-member predicates that gate property mapping at runtime. `PreCondition(s => predicate)`
runs **before** source-side resolution — use it when the resolution is expensive and would be
wasted work if the predicate fails. `Condition((s, value) => predicate)` runs **after**
resolution — use it when the predicate depends on the resolved value.

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
- **Update-in-place `Map<TS,TD>(src, existingDest)`**: skipped property preserves the
  existing destination value.
- **`ProjectTo<TDest>(query)`**: skipped property is `default(TMember)` (a projection
  materializes a fresh row).

Both predicates are `Expression<Func<...>>` and translate to SQL `CASE WHEN` in `ProjectTo`.
Untranslatable predicates fail at query-execution time with the LINQ provider's standard error.

Predicates flow base→derived through inheritance via the existing explicit-config precedence
rule. Predicates do NOT auto-flip across `.ReverseMap()` — reconfigure on the reverse
expression.

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

## Open generics

A single `CreateMap(typeof(Source<>), typeof(Destination<>))` registration applies to
every closed instantiation at runtime. Atlas materializes a closed `TypeMap` per closed
pair on first use via `MapperRegistry.GetTypeMap`'s lazy fallback, then caches it.

```csharp
public class Wrapper<T> { public T Value { get; set; } }
public class WrapperDto<T> { public T Value { get; set; } }

var cfg = new MapperConfiguration(c =>
{
    c.CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>));
});
var mapper = cfg.CreateMapper();

var intDto = mapper.Map<WrapperDto<int>>(new Wrapper<int> { Value = 42 });
var stringDto = mapper.Map<WrapperDto<string>>(new Wrapper<string> { Value = "hi" });
```

**Closed-pair-takes-precedence rule:** when a user has registered both the open
template AND a specific closed pair, the closed pair wins. This is the documented
escape hatch for per-member overrides on a specific instantiation.

**Convention-only:** open-generic registrations carry no fluent surface — no
`ForMember`, no `Include`, no `BeforeMap`, no `NullSubstitute`, no `ReverseMap`.
Users who need any of these register the specific closed pair via the generic
`CreateMap<TSrc, TDst>()` overload.

**Validation:** open-generic templates are excluded from `AssertConfigurationIsValid()`
per the "not every closed combination is valid" rule. Materialized closed pairs that
exist by validation time will be validated as a side effect of being in the closed-pair
registry.

**Translates to ProjectTo:** `query.ProjectTo<WrapperDto<int>>(cfg)` triggers
materialization on first call and reuses the cached closed `TypeMap` for subsequent
projections.

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
- Self-pair round-trips (`ExpandoObject → ExpandoObject`, `Dictionary<string, object> → Dictionary<string, object>`, etc.) are not supported by the convention-only path; explicit `CreateMap` registration of dynamic-shape pairs is a known v1 limitation (see `docs/Atlas-Design-DynamicMapping.md` §7.5). For dict-to-dict copy semantics, iterate manually.
- Profile-scoped value transformers do NOT fire on dynamic TypeMaps; only global-scope transformers compose.

See `docs/Atlas-Design-DynamicMapping.md` for the full specification.

## Reference handling for cycles

Atlas can map graphs with cycles or shared references safely, opt-in per typemap.
Without this opt-in, mapping a cyclic graph stack-overflows — by design, since
cycle detection has runtime cost.

```csharp
class Person
{
    public string Name { get; set; }
    public Person Boss { get; set; }            // self-cycle: alice.Boss = alice
}

cfg.CreateMap<Person, PersonDto>().PreserveReferences();

var alice = new Person { Name = "Alice" };
alice.Boss = alice;                              // cycle
var dto = mapper.Map<PersonDto>(alice);          // works — no stack overflow
// dto.Boss == dto (same instance; identity preserved)
```

Behavior summary:

- Convention: ONE flag on the OUTERMOST typemap of a potentially-cyclic graph
  is enough. Inner typemaps inherit cycle-safety at runtime via a per-call
  cache threaded through the call chain.
- Pre-population semantics: a destination is registered into the cache BEFORE
  its members are populated, which is what breaks cycles. Back-references
  resolve to the partially-constructed destination, fully populated by the
  time control returns to the caller.
- Shared references are also preserved: a `Department` referenced by 5
  `Employee` instances produces ONE `DepartmentDto` shared across all 5
  destination back-references.
- Hooks (`BeforeMap`/`AfterMap`), value transformers, conditional predicates,
  and null substitutes fire on the FIRST allocation only — cache hits skip
  the body entirely (no double-invocation of side effects).
- Propagates through `.ReverseMap()`, `Include<>` inheritance, and open-generic
  template materializations.
- `Atlas.Projections` rejects PreserveReferences typemaps — LINQ providers
  cannot model identity tracking. Use `mapper.Map<>()` for cycle-safe in-memory
  mapping; use `ProjectTo` only for non-cyclic projections.

Limitations (v1):

- Cannot be combined with `ConvertUsing<TConverter>()` — the converter replaces
  the body that the cache would wrap. Validator rejects the combination at
  `AssertConfigurationIsValid()` time.
- The cycle-safety flag must be on the OUTERMOST typemap of a potentially-cyclic
  graph. Marking only an INNER typemap (e.g., Employee → EmployeeDto) without
  marking its OUTER caller (e.g., Department → DepartmentDto) means the inner
  cycle protection is unreachable from the outer call. v3 may relax this.
- No custom reference-handler interface in v1 — built-in handler only.
- No per-call opt-in (`mapper.Map(src, opts => ...)`) in v1 — per-typemap only.
- Hooks and transformers cannot inspect the cycle-cache directly; they see
  destinations they create. Cyclically-referenced destinations may appear
  partially-populated to a hook fired during their own allocation phase
  (a known and documented limitation).

See `docs/Atlas-Design-ReferenceHandling.md` for the full specification.

## What's in v1

| Feature | Notes |
|---|---|
| Convention-based name matching | PascalCase canonical form; `CaseSensitive` toggle. |
| Naming-convention translation | PascalCase ↔ camelCase ↔ snake_case. |
| Recursive flattening | `Customer.Name` → `CustomerName`. |
| `ForMember` / `Ignore` | Per-destination-member overrides. |
| `MapFrom(expression)` / `MapFrom(constant)` | Source override or literal value. |
| Constructor / record / `init` / `required` mapping | Constructor parameters always match case-insensitively. |
| `ConvertUsing<T>()` and lambda converters | Whole-type custom conversion. |
| Collections | `List<T>`, `IList<T>`, `ICollection<T>`, `IEnumerable<T>`, `T[]`. |
| `Dictionary<K,V>` | Element-by-element mapping. |
| Update-in-place | `mapper.Map(source, existingDestination)`. |
| Validation | `AssertConfigurationIsValid()` returns every error in one exception. |
| Eager compile | `CompileMappings()` removes first-call latency. |
| Lazy compile | First call to a registered map compiles it under a lock; subsequent calls hit the cache. |
| Assembly scanning | `services.AddAtlas(...)` overloads, marker generic, params Assembly[]. |

## Deferred to v2

Each of these has its own design doc to be written separately:

- Attribute-based configuration
- Expression translation (`UseAsDataSource`)

## Performance

`tests/Atlas.Benchmarks` ships three BenchmarkDotNet classes:

- `WarmCallBenchmarks` — flat POCO, 3-level nested, list-of-100. Allocation column is the
  load-bearing metric; warm-call mapping should allocate exactly the destination plus
  collection structures (no internal context bags).
- `ConfigBuildBenchmarks` — configuration build + compile latency.
- `ColdCallBenchmarks` — combined build + first-call cost.

Run with `dotnet run -c Release --project tests/Atlas.Benchmarks -- --filter '*'`.

## Coverage

Run with coverage:

```
dotnet test tests/Atlas.Tests/Atlas.Tests.csproj --collect:"XPlat Code Coverage"
reportgenerator -reports:tests/Atlas.Tests/TestResults/**/coverage.cobertura.xml -targetdir:coverage -reporttypes:TextSummary
```

| Project | Line | Branch (target) | Status |
|---|---|---|---|
| `Atlas` | 94.6% | 83.5% | Met. |
| `Atlas.Extensions.DependencyInjection` | 94.7% | 100% | Met. |
| `Atlas.Projections` | 93.9% | 84.8% | Met. |

## License

See `LICENSE`.
