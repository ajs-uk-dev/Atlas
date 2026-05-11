# OGToolz.Atlas.Projections

LINQ-translatable projection (`ProjectTo`) for the [Atlas](https://github.com/ajs-uk-dev/Atlas) object-to-object mapper. Designed for EF Core and other `IQueryable` providers — the configured map is translated to a `Select` expression once and reused.

## Install

```bash
dotnet add package OGToolz.Atlas
dotnet add package OGToolz.Atlas.Projections
```

## Quick start

```csharp
using Atlas;
using Atlas.Projections;

var dtos = db.Blogs
    .Where(b => b.Year >= 2025)
    .ProjectTo<BlogDto>(configuration)
    .ToList();
```

The configuration is validated eagerly at the call site; non-projectable constructs (delegate-form `ConvertUsing`, missing maps, unmapped destination members) throw `AtlasProjectionException` listing every problem. Default recursion depth is 3 (per-call override available).

## Expression translation (`UseAsDataSource`)

The same package ships the inverse direction — write predicates and ordering in destination-DTO terms, and have them translated back to source-typed expressions before they hit the provider:

```csharp
var orders = db.Orders
    .UseAsDataSource(configuration)
    .For<OrderDto>()
    .Where(d => d.CustomerName.StartsWith("A"))
    .OrderBy(d => d.Total)
    .Take(10)
    .ToList();
```

## Documentation

See the main repository for the full developer guide, design documents, and the v2 feature compatibility matrix:

- Repository: https://github.com/ajs-uk-dev/Atlas
- Developer guide: [`docs/DeveloperGuide.md`](https://github.com/ajs-uk-dev/Atlas/blob/main/docs/DeveloperGuide.md)

## License

MIT
