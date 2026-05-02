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

- Inheritance / runtime polymorphism (`Include`, `IncludeBase`)
- Enum mapping surface
- Reverse mapping / unflattening
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
| `Atlas` | 91% | 85% | Line met. Branch coverage gap concentrated in the `HasImplicitNumericConversion` switch — duplicated between `ConventionEngine` and `ConfigurationValidator`; a v2 cleanup task is to consolidate into one helper and exercise via `[Theory]`. |
| `Atlas.Extensions.DependencyInjection` | 92.8% | 80% | Met. |
| `Atlas.Projections` | 94.8% | 59.29% | Line met. Branch coverage gap is the same `HasImplicitNumericConversion` switch-arm pattern documented for the v1 Atlas line. |

## License

See `LICENSE`.
