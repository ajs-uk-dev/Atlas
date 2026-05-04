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

- Before/after hooks, value transformers
- Conditional mapping (`Condition`, `PreCondition`)
- Null substitution
- Open generics
- Dynamic / dictionary-shaped sources
- Reference handling (cycle detection)
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
| `Atlas` | 94.3% | 82.7% | Met. Reverse mapping adds `ReverseMapMirror`, unflatten `ForPath` paths, and conflict-guard coverage. |
| `Atlas.Extensions.DependencyInjection` | 92.9% | 100% | Met. |
| `Atlas.Projections` | 93.9% | 83.6% | Met. Branch coverage benefits from the consolidated numeric-conversion helper. |

## License

See `LICENSE`.
