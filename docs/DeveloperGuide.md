# Atlas Developer Guide

> A practical guide for developers using Atlas, the fluent runtime expression-tree-compiling object-to-object mapper for .NET 10+.

**Atlas v2 is feature-complete.** This guide covers the full feature surface — from basic `CreateMap<S, D>()` through the thirteen v2 deferred features (projection, attribute config, expression translation, etc.).

---

## Table of Contents

1. [Introduction](#introduction)
2. [Getting Started](#getting-started)
3. [Core Concepts](#core-concepts)
4. [Defining Maps](#defining-maps)
5. [The Convention Engine](#the-convention-engine)
6. [Per-Member Configuration](#per-member-configuration)
7. [Constructor Mapping](#constructor-mapping)
8. [Custom Type Converters](#custom-type-converters)
9. [Collections & Dictionaries](#collections--dictionaries)
10. [Update-in-Place Mapping](#update-in-place-mapping)
11. [Configuration Validation](#configuration-validation)
12. [Eager Compilation](#eager-compilation)
13. [Dependency Injection](#dependency-injection)
14. **v2 Feature Set:**
    - [14.1 IQueryable Projection (`ProjectTo`)](#141-iqueryable-projection-projectto)
    - [14.2 Inheritance & Polymorphism](#142-inheritance--polymorphism)
    - [14.3 Enum Mapping](#143-enum-mapping)
    - [14.4 Reverse Mapping & Unflattening](#144-reverse-mapping--unflattening)
    - [14.5 Before/After Hooks](#145-beforeafter-hooks)
    - [14.6 Value Transformers](#146-value-transformers)
    - [14.7 Conditional Mapping](#147-conditional-mapping)
    - [14.8 Null Substitution](#148-null-substitution)
    - [14.9 Open Generics](#149-open-generics)
    - [14.10 Dynamic / Dictionary Mapping](#1410-dynamic--dictionary-mapping)
    - [14.11 Reference Handling (Cycles)](#1411-reference-handling-cycles)
    - [14.12 Attribute-Based Configuration](#1412-attribute-based-configuration)
    - [14.13 Expression Translation (`UseAsDataSource`)](#1413-expression-translation-useasdatasource)
15. [Performance Notes](#performance-notes)
16. [Limitations](#limitations)
17. [Further Reading](#further-reading)

---

## Introduction

Atlas maps one object to another. You declare the source-to-destination relationship once at startup; Atlas compiles a delegate per `(TSource, TDestination)` pair and invokes that delegate at runtime — close to hand-written-code performance, no per-call reflection.

```csharp
// Define a map
public class OrderProfile : MapperProfile
{
    public OrderProfile()
    {
        CreateMap<Order, OrderDto>();
    }
}

// Use it
var dto = mapper.Map<OrderDto>(order);
```

**Why Atlas?**

- **Fluent runtime API** — familiar shape if you've used AutoMapper.
- **Expression-tree compiled** — no per-call reflection cost.
- **Convention-driven** — `dst.CustomerName ↔ src.Customer.Name` works automatically.
- **Validation up front** — `AssertConfigurationIsValid()` catches missing mappings in your CI before they ship.
- **DI-friendly** — `services.AddAtlas(asm)` does it all.

### Packages

| Package | Purpose | When to use |
|---|---|---|
| `Atlas` | Core mapper | Always |
| `Atlas.Extensions.DependencyInjection` | `IServiceCollection` integration | When using DI (most apps) |
| `Atlas.Projections` | `IQueryable` projection (`ProjectTo`) and expression translation (`UseAsDataSource`) | When working with EF Core / NHibernate / any LINQ provider |

---

## Getting Started

### Hello, World

```csharp
using Atlas;

// 1. Define source and destination types
public class Person
{
    public string FirstName { get; set; } = "";
    public string LastName  { get; set; } = "";
}

public class PersonDto
{
    public string FirstName { get; init; } = "";
    public string LastName  { get; init; } = "";
}

// 2. Define a profile
public class PeopleProfile : MapperProfile
{
    public PeopleProfile()
    {
        CreateMap<Person, PersonDto>();
    }
}

// 3. Build a mapper
var configuration = new MapperConfiguration(cfg => cfg.AddProfile<PeopleProfile>());
var mapper = configuration.CreateMapper();

// 4. Use it
var dto = mapper.Map<PersonDto>(new Person { FirstName = "Alice", LastName = "Smith" });
// dto.FirstName == "Alice", dto.LastName == "Smith"
```

### With Dependency Injection

```csharp
// Program.cs / Startup.cs
services.AddAtlas(typeof(PeopleProfile).Assembly);

// In a controller / service:
public class OrderController(IMapper mapper) : ControllerBase
{
    public IActionResult Get(int id) =>
        Ok(mapper.Map<OrderDto>(_repository.GetOrder(id)));
}
```

`services.AddAtlas(asm)` does five things in order:
1. Scans the assembly (or assemblies) for `MapperProfile` subclasses.
2. Scans the same assemblies for `[Map]`-decorated types.
3. Registers `MapperConfiguration` as singleton.
4. Compiles all maps eagerly (`CompileMappings()`).
5. Registers `IMapper` as transient (cheap; wraps the singleton config).

---

## Core Concepts

### `MapperConfiguration`

The singleton root. Holds every `(TSource, TDestination)` typemap. Constructed once at startup, immutable thereafter.

```csharp
var configuration = new MapperConfiguration(cfg =>
{
    cfg.AddProfile<OrderProfile>();
    cfg.AddProfile<CustomerProfile>();
});
```

You'll typically construct this once via `services.AddAtlas` and never directly otherwise.

### `IMapper`

The runtime facade. Three overloads of `Map`:

```csharp
// 1. Source as object, destination type-only (slowest — uses reflection-dispatch internally)
TDest Map<TDest>(object source);

// 2. Strongly-typed source and destination (fastest)
TDest Map<TSrc, TDest>(TSrc source);

// 3. Update-in-place — populates an existing destination instance
void Map<TSrc, TDest>(TSrc source, TDest existingDestination);
```

### `MapperProfile`

A class that groups related `CreateMap<>` calls. Discovered by assembly scan. Must have a public parameterless constructor.

```csharp
public class OrderProfile : MapperProfile
{
    public OrderProfile()
    {
        CreateMap<Order, OrderDto>();
        CreateMap<OrderLine, OrderLineDto>();
        CreateMap<Customer, CustomerSummaryDto>();
    }
}
```

### `MemberList` validation policy

Each `CreateMap` accepts a `MemberList` argument that controls what `AssertConfigurationIsValid()` checks:

```csharp
CreateMap<Source, Destination>(MemberList.Destination)  // default — every dst member must have a source
CreateMap<Source, Destination>(MemberList.Source)        // every src member must be mapped to a dst
CreateMap<Source, Destination>(MemberList.None)          // skip validation entirely
```

---

## Defining Maps

Three ways to declare a map. They produce identical `TypeMap` instances downstream.

### 1. Fluent (most common)

```csharp
public class OrderProfile : MapperProfile
{
    public OrderProfile()
    {
        CreateMap<Order, OrderDto>()
            .ForMember(d => d.DisplayName, opt => opt.MapFrom(s => $"#{s.Id} {s.Customer.Name}"))
            .ForMember(d => d.Total,       opt => opt.Ignore());
    }
}
```

### 2. Attributes

```csharp
[Map(typeof(Order))]
public class OrderDto
{
    public int Id { get; init; }

    [SourceMember("Customer.Name")]
    public string CustomerName { get; init; } = "";

    [Ignore]
    public decimal Total { get; init; }
}
```

`AddMaps(asm)` / `AddAtlas(asm)` discover attribute-decorated types alongside profiles. See §14.12 for full coverage.

### 3. Direct on `MapperConfigurationExpression`

```csharp
var configuration = new MapperConfiguration(cfg =>
{
    cfg.CreateMap<Order, OrderDto>();
    cfg.CreateMap<Customer, CustomerDto>();
});
```

Useful for one-off maps without a profile class.

### Discovery via `AddMaps` / `AddAtlas`

```csharp
// Inside MapperConfiguration callback
cfg.AddMaps(typeof(OrderProfile).Assembly);

// Or with DI
services.AddAtlas(typeof(OrderProfile).Assembly);
```

Both scan for `MapperProfile` subclasses AND `[Map]`-decorated types in the named assemblies.

### Conflict rule

A `(TSource, TDestination)` pair must be declared **exactly once**. Two `CreateMap<S, D>()` calls for the same pair throw `AtlasConfigurationException`. Same for fluent + attribute on the same pair.

---

## The Convention Engine

### Default behavior

Atlas looks for a destination property `X` on `TDestination` and tries to fill it from a source named `X` on `TSource`. The default match is **case-sensitive PascalCase**.

```csharp
public class Order { public int Id { get; set; } public decimal Total { get; set; } }
public class OrderDto { public int Id { get; set; } public decimal Total { get; set; } }

// CreateMap<Order, OrderDto>() — both members map by convention; no ForMember needed.
```

### Flattening

The convention engine walks recursive paths automatically. `dst.CustomerName` resolves to `src.Customer.Name`:

```csharp
public class Order
{
    public Customer Customer { get; set; } = new();
}
public class Customer
{
    public string Name { get; set; } = "";
}

public class OrderDto
{
    public string CustomerName { get; set; } = "";   // ← resolves to src.Customer.Name
}
```

Flattening is recursive — `dst.CustomerAddressCity` would walk `src.Customer.Address.City`.

### Case sensitivity

Default is case-sensitive. Toggle via:

```csharp
new MapperConfiguration(cfg =>
{
    cfg.CaseSensitive = false;        // case-insensitive name match
    cfg.AddProfile<OrderProfile>();
});
```

### Naming-convention translation

PascalCase ↔ camelCase ↔ snake_case is supported via per-side toggle:

```csharp
new MapperConfiguration(cfg =>
{
    cfg.SourceMemberNamingConvention      = NamingConvention.SnakeCase;       // src has first_name
    cfg.DestinationMemberNamingConvention = NamingConvention.PascalCase;     // dst has FirstName
    cfg.AddProfile<MyProfile>();
});
```

Built-in `NamingConvention` values: `PascalCase`, `CamelCase`, `SnakeCase`.

---

## Per-Member Configuration

`ForMember` overrides the convention engine for a specific destination property. Inside its callback, you have access to `MapFrom`, `MapFrom(constant)`, `Ignore`, `Condition`, `PreCondition`, and `NullSubstitute`.

### `MapFrom(expression)`

Map from any expression on the source:

```csharp
CreateMap<Order, OrderDto>()
    .ForMember(d => d.DisplayName,
               opt => opt.MapFrom(s => $"#{s.Id} for {s.Customer.Name}"));
```

The expression can compute, transform, or compose anything. It runs through Atlas's expression-tree compilation and is also translatable to SQL via `ProjectTo` (when the expression is provider-friendly).

### `MapFrom(constant)`

Map to a constant value:

```csharp
CreateMap<Order, OrderDto>()
    .ForMember(d => d.Status, opt => opt.MapFrom("active"));
```

### `Ignore()`

Skip the destination member. Excluded from `MemberList.Destination` validation.

```csharp
CreateMap<Order, OrderDto>()
    .ForMember(d => d.ComputedField, opt => opt.Ignore());
```

When mapping an existing destination via update-in-place (`Map<S, D>(src, dst)`), the existing value is preserved — `Ignore` doesn't overwrite with `default(T)`.

### `ForPath` (nested-destination chains)

Write into a nested destination chain. Atlas auto-instantiates intermediate types if their public parameterless constructors exist:

```csharp
CreateMap<Order, OrderDto>()
    .ForPath(d => d.Customer.DisplayName,
             opt => opt.MapFrom(s => $"{s.Customer.FirstName} {s.Customer.LastName}"));
```

`Atlas.Projections` rejects `ForPath` configurations (LINQ providers can't model nested member-init writes). Use forward-direction `Map<>()` for `ForPath`-bearing maps.

---

## Constructor Mapping

Atlas works with records, init-only properties, and `required` members.

### Records

```csharp
public record OrderDto(int Id, string CustomerName, decimal Total);

CreateMap<Order, OrderDto>();
// Convention engine binds all three constructor parameters by name.
```

### `ForCtorParam`

Override a specific constructor parameter:

```csharp
public record OrderDto(int Id, string DisplayName, decimal Total);

CreateMap<Order, OrderDto>()
    .ForCtorParam("DisplayName",
                  opt => opt.MapFrom(s => $"#{s.Id} for {s.Customer.Name}"));
```

The first argument is the parameter name (case-insensitive match). The callback exposes the same surface as `ForMember` (`MapFrom`, `Ignore`, `Condition`, `NullSubstitute`).

### `required` and `init`-only properties

```csharp
public class OrderDto
{
    public required int Id { get; init; }
    public required string CustomerName { get; init; }
    public decimal Total { get; init; }
}

CreateMap<Order, OrderDto>();   // works; init-only and required are honored
```

---

## Custom Type Converters

Replace Atlas's per-property mapping for a whole `(TSource, TDestination)` pair with your own logic.

### Class-based: `ITypeConverter<TS, TD>`

```csharp
public class OrderToOrderDtoConverter : ITypeConverter<Order, OrderDto>
{
    public OrderDto Convert(Order source) => new OrderDto
    {
        Id = source.Id,
        CustomerName = source.Customer?.Name ?? "(no customer)",
        Total = source.Lines.Sum(l => l.Quantity * l.UnitPrice),
    };
}

CreateMap<Order, OrderDto>().ConvertUsing<OrderToOrderDtoConverter>();
```

The converter must have a public parameterless constructor — Atlas instantiates it once per `MapperConfiguration` build. With DI, it's resolved via `ActivatorUtilities.CreateInstance` from the root `IServiceProvider`, so it can take constructor dependencies.

### Inline: `ConvertUsing(Func<>)`

```csharp
CreateMap<Order, OrderDto>()
    .ConvertUsing(s => new OrderDto
    {
        Id = s.Id,
        CustomerName = s.Customer.Name,
        Total = s.Lines.Sum(l => l.Quantity * l.UnitPrice),
    });
```

Inline converters are in-memory only — `Atlas.Projections.ProjectTo` rejects them because the provider can't translate the delegate.

---

## Collections & Dictionaries

### Source → destination collection types

| Source type | Destination type | Works? |
|---|---|---|
| `IEnumerable<T>` | `List<T>` / `IList<T>` / `ICollection<T>` / `IEnumerable<T>` / `T[]` | ✅ |
| `T[]` | (any of the above) | ✅ |
| `List<T>` | (any of the above) | ✅ |
| `Dictionary<K, V>` | `Dictionary<K, V>` | ✅ |

Atlas maps each element through the `(TSourceElement, TDestinationElement)` typemap. You must register the element pair separately:

```csharp
CreateMap<Order, OrderDto>();
CreateMap<OrderLine, OrderLineDto>();   // ← required for src.Lines → dst.Lines

public class Order { public List<OrderLine> Lines { get; set; } = new(); }
public class OrderDto { public List<OrderLineDto> Lines { get; init; } = new(); }
```

### Dictionaries

```csharp
public class Source { public Dictionary<int, OrderLine> Lines { get; set; } = new(); }
public class Dest { public Dictionary<int, OrderLineDto> Lines { get; init; } = new(); }

CreateMap<Source, Dest>();
CreateMap<OrderLine, OrderLineDto>();   // value-type pair must be registered
```

Dictionary keys aren't mapped (`int` → `int` directly). Values run through the registered typemap.

---

## Update-in-Place Mapping

Populate an existing destination instance instead of allocating a new one:

```csharp
var existingDto = new OrderDto { Id = 0, Notes = "preserved" };
mapper.Map(source, existingDto);
// existingDto.Id is now source.Id
// existingDto.Notes is unchanged ('Notes' was Ignored)
```

`Ignore()`'d members preserve their existing values instead of being reset to `default(T)`. This is the key difference from fresh `Map<TDest>()`.

---

## Configuration Validation

```csharp
var configuration = new MapperConfiguration(cfg => cfg.AddProfile<OrderProfile>());

configuration.AssertConfigurationIsValid();   // throws on misconfigurations
```

Catches:
- Unmapped destination members (under `MemberList.Destination`).
- Unresolvable `MapFrom` paths.
- Type-incompatible converters / null substitutes.
- Duplicate registrations.
- Hooks/PreserveReferences/Dynamic + ConvertUsing combinations.

Run this in a unit test in your CI:

```csharp
[Fact]
public void Mapping_Configuration_Is_Valid()
{
    var configuration = new MapperConfiguration(cfg => cfg.AddProfile<OrderProfile>());
    configuration.AssertConfigurationIsValid();
}
```

Failure throws `AtlasConfigurationException` with one `ConfigurationError` per problem — the message lists every error.

---

## Eager Compilation

Atlas compiles each typemap's body to a delegate the first time it's used. To pay the JIT cost at startup instead of first request:

```csharp
var configuration = new MapperConfiguration(cfg => cfg.AddProfile<OrderProfile>());
configuration.CompileMappings();   // pre-compile every map now
```

`AddAtlas(...)` calls `CompileMappings()` automatically, so DI users get this for free.

---

## Dependency Injection

```csharp
services.AddAtlas(typeof(SomeMarkerType).Assembly);
```

The DI extension is in the `Atlas.Extensions.DependencyInjection` package.

### Overloads

```csharp
services.AddAtlas(params Assembly[] assemblies);

services.AddAtlas(
    Action<MapperConfigurationExpression>? configure,
    params Assembly[] assemblies);
```

Use the second overload to set `cfg.SourceMemberNamingConvention`, register transformers globally, etc:

```csharp
services.AddAtlas(cfg =>
{
    cfg.CaseSensitive = false;
    cfg.ValueTransformers.Add<string>(s => s.Trim());
}, typeof(OrderProfile).Assembly);
```

### Lifetimes

| Type | Lifetime | Notes |
|---|---|---|
| `MapperConfiguration` | Singleton | Built once at startup; immutable |
| `IMapper` | Transient | Cheap; wraps the singleton config |
| `MapperProfile` subclasses | Single instance per build | |
| `IMappingAction<,>` (hooks) | Per-config cache; resolved via `ActivatorUtilities.CreateInstance` | |
| `ITypeConverter<,>` | Per-config cache; same resolution path | |

### Scoped services in actions/converters

If your `IMappingAction<,>` or `ITypeConverter<,>` needs a scoped service (e.g., `DbContext`), inject `IServiceProvider` or `IHttpContextAccessor` and resolve the scoped service from it at call time:

```csharp
public class AuditAction(IHttpContextAccessor http) : IMappingAction<Order, OrderDto>
{
    public void Process(Order source, OrderDto destination)
    {
        var user = http.HttpContext?.User?.Identity?.Name;
        destination.AuditedBy = user ?? "(anonymous)";
    }
}
```

Then register the action via the typed-action overload (see §14.5):

```csharp
CreateMap<Order, OrderDto>().AfterMap<AuditAction>();
```

---

## 14.1 IQueryable Projection (`ProjectTo`)

Translate a typemap into a LINQ `Select` that runs at the database level — only the columns the destination actually needs hit the SELECT clause.

### Setup

```csharp
using Atlas.Projections;
```

### Usage

```csharp
public class OrderDto
{
    public int Id { get; init; }
    public string CustomerName { get; init; } = "";
    public decimal Total { get; init; }
}

CreateMap<Order, OrderDto>();   // convention does the rest

// In a controller:
var dtos = db.Orders
    .Where(o => o.IsActive)
    .ProjectTo<OrderDto>(mapperConfig)
    .ToList();
```

EF Core emits SQL like:

```sql
SELECT [proj].[Id], [c].[Name] AS [CustomerName], [proj].[Total]
FROM [Orders] AS [proj]
INNER JOIN [Customers] AS [c] ON [proj].[CustomerId] = [c].[Id]
WHERE [proj].[IsActive] = 1
```

### Operator placement rule

**Filter and sort on the source `IQueryable` first, then project as the last step.** Operators applied after `ProjectTo` run on the destination shape and may not survive translation:

```csharp
// ✅ Correct
db.Orders.Where(o => o.IsActive).OrderBy(o => o.Total).ProjectTo<OrderDto>(cfg).ToList()

// ❌ Wrong — Where on destination DTO won't translate
db.Orders.ProjectTo<OrderDto>(cfg).Where(d => d.Total > 100).ToList()
```

For destination-typed filters, see §14.13 (UseAsDataSource).

### `maxDepth`

```csharp
db.Orders.ProjectTo<OrderDto>(mapperConfig, maxDepth: 5).ToList()
```

Default is `3`. Each nested DTO costs one depth level. Atlas refuses to descend deeper than `maxDepth` and returns `null` for over-deep references.

### What ProjectTo can't translate

`ProjectTo` rejects (at projection-build time, with `AtlasProjectionException`):
- TypeMaps with `BeforeMap`/`AfterMap` hooks
- TypeMaps with `PreserveReferences = true`
- TypeMaps with `IsDynamic = true` (dynamic-shape mapping)
- TypeMaps with `DestinationPath` (`ForPath`) bindings
- TypeMaps with `ConvertUsing<T>()` class-based converter
- Per-property delegate overrides (only `Expression`-form `MapFrom` translates)

For any of these, use `mapper.Map<>()` after fetching the source.

---

## 14.2 Inheritance & Polymorphism

Map base types and Atlas dispatches to the correct derived map at runtime.

### `Include<TDerivedSrc, TDerivedDst>()`

Declare on the BASE map:

```csharp
CreateMap<Animal, AnimalDto>()
    .Include<Dog, DogDto>()
    .Include<Cat, CatDto>();

CreateMap<Dog, DogDto>();
CreateMap<Cat, CatDto>();
```

When `mapper.Map<AnimalDto>(actualDog)` runs, Atlas dispatches to the `(Dog, DogDto)` map. Most-derived-first dispatch ordering is computed at config-build.

### `IncludeBase<TBaseSrc, TBaseDst>()`

Declare on the DERIVED map — useful when the base map lives in a different profile:

```csharp
// In one profile:
CreateMap<Animal, AnimalDto>();

// In another profile:
CreateMap<Dog, DogDto>().IncludeBase<Animal, AnimalDto>();
```

Member-level configuration on the base flows into the derived map via `InheritanceMerger`. Explicit (derived) config beats explicit (base) config beats convention.

### Collection-element dispatch

`mapper.Map<List<AnimalDto>>(animals)` where `animals` is `List<Animal>` containing a `Dog` instance maps the `Dog` element via the `(Dog, DogDto)` map automatically.

---

## 14.3 Enum Mapping

### Auto-conversion

Atlas converts enum-to-enum, enum-to-string, enum-to-underlying-numeric, and the reverses without configuration:

```csharp
public enum SourceLevel { Low, Medium, High }
public enum DestLevel { Low, Medium, High }

CreateMap<MySource, MyDest>();
// SourceLevel → DestLevel works by-value automatically.
// SourceLevel → string works (returns "Low", "Medium", "High").
// SourceLevel → int works (returns 0, 1, 2).
```

### Per-pair strategy: `MapByValue` / `MapByName`

```csharp
CreateMap<SourceLevel, DestLevel>().MapByValue();   // default
CreateMap<SourceLevel, DestLevel>().MapByName();    // case-sensitive
CreateMap<SourceLevel, DestLevel>().MapByName(ignoreCase: true);
```

### Per-value overrides: `MapValue` / `Ignore`

```csharp
CreateMap<SourceLevel, DestLevel>()
    .MapByValue()
    .MapValue(SourceLevel.Low, DestLevel.Medium)   // explicit
    .Ignore(SourceLevel.High);                      // → default(DestLevel)
```

### Fallback: `WithFallback`

For unmatched values without a `MapValue` or `Ignore` configured:

```csharp
CreateMap<SourceLevel, DestLevel>()
    .MapByName()
    .WithFallback(DestLevel.Low);   // unmatched → Low instead of throwing
```

Without a fallback, unmatched values throw `AtlasMappingException` at runtime.

### Strict validation

```csharp
new MapperConfiguration(cfg =>
{
    cfg.EnableEnumMappingValidation();
    cfg.AddProfile<MyProfile>();
});
```

`AssertConfigurationIsValid()` then asserts every defined source enum value is covered by `MapValue`, `Ignore`, the strategy, or `WithFallback`.

---

## 14.4 Reverse Mapping & Unflattening

`ReverseMap()` registers the inverse pair `(TDestination, TSource)` automatically. Conventions and source-side flattening are auto-inverted; per-member explicit config is NOT (you reconfigure on the reverse expression if needed).

```csharp
CreateMap<Order, OrderDto>()
    .ReverseMap();
// Now both (Order, OrderDto) AND (OrderDto, Order) are registered.

var order = mapper.Map<Order>(dto);   // works
```

### What auto-inverts

- Convention name matching.
- Source-side flattening: forward `dst.CustomerName ← src.Customer.Name` becomes reverse `dst.Customer.Name ← src.CustomerName`.

### What does NOT auto-invert (reconfigure if needed)

- `ForMember(MapFrom(expression))` — the forward expression isn't translated to reverse.
- `Ignore()` — does not propagate.
- `ConvertUsing` — converters are generally not invertible.
- Inheritance includes.
- Enum per-value overrides.
- Constructor parameter mappings.

### `ForPath` for unflattening with divergent set/get

When unflattening needs to write into a nested chain (`dst.Customer.Name` from `src.CustomerName`):

```csharp
CreateMap<Order, OrderDto>()
    .ReverseMap()
    .ForPath(s => s.Customer.Name,
             opt => opt.MapFrom(d => d.CustomerName));
```

`ForPath` auto-instantiates intermediates via parameterless constructors.

---

## 14.5 Before/After Hooks

Run code before or after the destination is populated.

### Lambda form

```csharp
CreateMap<Order, OrderDto>()
    .BeforeMap((src, dst) => Console.WriteLine($"Mapping order {src.Id}"))
    .AfterMap((src, dst) => dst.AuditTimestamp = DateTime.UtcNow);
```

### Typed-action form (for DI dependencies)

```csharp
public class AuditAction(IAuditLog auditLog) : IMappingAction<Order, OrderDto>
{
    public void Process(Order source, OrderDto destination) =>
        auditLog.LogMapping(source.Id);
}

CreateMap<Order, OrderDto>().AfterMap<AuditAction>();
```

The action is instantiated via `ActivatorUtilities.CreateInstance` from the root `IServiceProvider` when Atlas is registered through DI; without DI, requires a public parameterless constructor.

### Ordering semantics

- Multiple `BeforeMap` calls on the same map run in **registration order (FIFO)**.
- Multiple `AfterMap` calls run in registration order too.
- With inheritance: base hooks run BEFORE derived hooks (base-first), and AfterMap runs in stack-unwind order (derived first, then base).

### Limitation

Hook-bearing TypeMaps are **rejected by `ProjectTo`** (LINQ providers can't run delegate code). Use `mapper.Map<>()` for hook-using maps.

---

## 14.6 Value Transformers

Apply a post-processing function to every value of a given type, regardless of which map produces it.

### Three scopes

```csharp
// 1. Global
new MapperConfiguration(cfg =>
{
    cfg.ValueTransformers.Add<string>(s => s.Trim());
    cfg.AddProfile<MyProfile>();
});

// 2. Profile
public class MyProfile : MapperProfile
{
    public MyProfile()
    {
        ValueTransformers.Add<string>(s => s.ToUpperInvariant());
        CreateMap<Order, OrderDto>();
    }
}

// 3. Type-map
CreateMap<Order, OrderDto>()
    .AddTransform<string>(s => s + "!");
```

Composition order: **global → profile → type-map**, broad-first. Within each scope, transformers run in registration order.

### Translates to projection

Transformers are stored as `Expression<Func<T, T>>` so the same declaration works for both `mapper.Map<>()` and `query.ProjectTo<>()`. The latter inlines the transformer expression into the SQL projection.

### Limitation

Profile-scope transformers do NOT fire on TypeMaps with `OriginatingProfile == null` — this includes attribute-declared TypeMaps (#14.12), open-generic materialized closed pairs (#14.9), and dynamic TypeMaps (#14.10). Use global scope for cross-cutting transforms.

---

## 14.7 Conditional Mapping

Two predicates per member:

### `PreCondition` — gates resolution + assign

Runs BEFORE source-side resolution. If false, the destination member is skipped. Useful when source-side resolution is expensive:

```csharp
CreateMap<Order, OrderDto>()
    .ForMember(d => d.ExpensiveDerived,
               opt =>
               {
                   opt.PreCondition(s => s.IsActive);            // skip if inactive
                   opt.MapFrom(s => ExpensiveCalculation(s));    // only runs if PreCondition passed
               });
```

### `Condition` — gates assign only

Runs AFTER source-side resolution. The second argument is the resolved value:

```csharp
CreateMap<Order, OrderDto>()
    .ForMember(d => d.Notes,
               opt =>
               {
                   opt.MapFrom(s => s.Comments);
                   opt.Condition((s, resolved) => !string.IsNullOrEmpty(resolved));
               });
```

### Skip semantics

- Fresh `Map<TDest>()`: skipped → destination member stays at `default(T)`.
- Update-in-place `Map<TS, TD>(src, existing)`: skipped → existing destination value preserved.
- Constructor parameter (`ForCtorParam`): skipped → parameter's declared default value (or `default(T)` if no default).

### Translation

Both predicates are stored as `Expression`, so they participate in `ProjectTo` (translated to SQL `CASE WHEN`).

---

## 14.8 Null Substitution

Provide a fallback value when the resolved source member is null.

### Constant form

```csharp
CreateMap<Order, OrderDto>()
    .ForMember(d => d.CustomerEmail,
               opt =>
               {
                   opt.MapFrom(s => s.Customer.Email);
                   opt.NullSubstitute("(no email)");
               });
```

### Factory form

For computed defaults:

```csharp
opt.NullSubstitute<DateTime?>(() => DateTime.UtcNow);
```

### Pipeline placement

`PreCondition → resolve → null-substitute → convert → transform → Condition → assign`. Value transformers and `Condition` see the substituted (non-null) value.

### Translation

Translates to SQL `COALESCE` in `ProjectTo` (in the projection direction; predicate-side is a v1 limitation — see §14.13 "Limitations").

### Validator rules

- Substitute on a non-nullable source value-type → unreachable; throws.
- Substitute type incompatible with source-member type → throws.

---

## 14.9 Open Generics

Configure once, apply to every closed instantiation:

```csharp
new MapperConfiguration(cfg =>
{
    cfg.CreateMap(typeof(Result<>), typeof(ResultDto<>));
});

public class Result<T> { public T Value { get; set; } = default!; public bool IsSuccess { get; set; } }
public class ResultDto<T> { public T Value { get; set; } = default!; public bool IsSuccess { get; set; } }

// At runtime:
mapper.Map<ResultDto<int>>(new Result<int> { Value = 42, IsSuccess = true });
// ✓ — closed pair (Result<int>, ResultDto<int>) materialized lazily on first use.
```

### Closed-pair takes precedence

If both an open template and a specific closed pair are registered, the closed pair wins:

```csharp
cfg.CreateMap(typeof(Result<>), typeof(ResultDto<>));
cfg.CreateMap<Result<string>, ResultDto<string>>()           // overrides for string
    .ForMember(d => d.Value, opt => opt.MapFrom(s => s.Value.ToUpper()));
```

### Limitations

- Convention-only. Per-member overrides on open generics are deferred to v3.
- `ConvertUsing(typeof(<>))`, `Include`/`IncludeBase`, `ReverseMap` on open generics are deferred to v3.
- Materialized closed pairs have `OriginatingProfile == null` — profile-scope transformers don't fire on them.

---

## 14.10 Dynamic / Dictionary Mapping

Map between strongly-typed POCOs and `IDictionary<string, object>`, `Dictionary<string, object>`, or `ExpandoObject` — no `CreateMap` call required.

### POCO → dictionary

```csharp
public class Order
{
    public int Id { get; set; }
    public string CustomerName { get; set; } = "";
    public Customer Customer { get; set; } = new();
}

var order = new Order { Id = 1, CustomerName = "Alice" };
ExpandoObject expando = mapper.Map<ExpandoObject>(order);
// dynamic d = expando; d.Id == 1, d.CustomerName == "Alice"

Dictionary<string, object> dict = mapper.Map<Dictionary<string, object>>(order);
// dict["Id"] == 1, dict["CustomerName"] == "Alice"
```

### Dictionary → POCO

```csharp
var dict = new Dictionary<string, object>
{
    ["Id"] = 1,
    ["CustomerName"] = "Alice",
};

var order = mapper.Map<Order>(dict);
// order.Id == 1, order.CustomerName == "Alice"
```

### Dot-notation read fallback

`dict["Customer.Email"]` populates `dst.Customer.Email` if no top-level `Customer` key exists:

```csharp
var dict = new Dictionary<string, object>
{
    ["Id"] = 1,
    ["Customer.Email"] = "alice@example.com",
};

var order = mapper.Map<Order>(dict);
// order.Customer.Email == "alice@example.com"
```

Top-level wins over dot-notation siblings.

### Concrete type contract

| Requested destination type | Returned concrete type |
|---|---|
| `ExpandoObject` | `ExpandoObject` |
| `Dictionary<string, object>` | `Dictionary<string, object>` |
| `IDictionary<string, object>` | `ExpandoObject` typed as the abstraction |

### Limitations

- Convention-only. No fluent customization for dynamic TypeMaps in v1.
- Profile-scope transformers don't fire on dynamic TypeMaps (`OriginatingProfile == null`).
- `ProjectTo` rejects dynamic typemaps.
- Only `Dictionary<string, object>` (and the two equivalents) — `Dictionary<string, T>` with `T != object` is not supported.

---

## 14.11 Reference Handling (Cycles)

Map cyclic graphs (e.g., `Person.Boss = self`) and preserve shared references within a single top-level `Map` call.

### Opt-in per-typemap

```csharp
public class Person
{
    public string Name { get; set; } = "";
    public Person? Boss { get; set; }
}
public class PersonDto
{
    public string Name { get; init; } = "";
    public PersonDto? Boss { get; init; }
}

CreateMap<Person, PersonDto>().PreserveReferences();
```

### Behavior

```csharp
var alice = new Person { Name = "Alice" };
alice.Boss = alice;   // cyclic

var dto = mapper.Map<PersonDto>(alice);
// dto.Boss is the SAME instance as dto. No stack overflow.

// Shared references:
var bob = new Person { Name = "Bob", Boss = alice };
var charlie = new Person { Name = "Charlie", Boss = alice };
var team = new List<Person> { bob, charlie };
var teamDto = mapper.Map<List<PersonDto>>(team);
// teamDto[0].Boss IS teamDto[1].Boss — same Alice instance reused.
```

### Propagation

`PreserveReferences()` propagates through:
- `.ReverseMap()` (bidirectional propagation — flag setter checks for existing siblings)
- `Include<>` / `IncludeBase<>` (base→derived OR semantics)
- Open-generic template → closed-pair materialization

### Cache scope

The instance cache is per-`Map<>` call. Each top-level `mapper.Map<>()` invocation allocates its own cache; cycles within that call dedupe; subsequent calls don't see the prior cache.

### Limitations

- Cannot be combined with `ConvertUsing<T>()` — validator throws at config time.
- `Atlas.Projections.ProjectTo` rejects PreserveReferences typemaps (LINQ providers can't model identity tracking).
- Custom `IReferenceHandler` interface is deferred to v3.

---

## 14.12 Attribute-Based Configuration

Decorate destination classes with `[Map(typeof(SourceType))]` to declare mappings without writing a profile.

### Basic example

```csharp
[Map(typeof(Order))]
public class OrderDto
{
    public int Id { get; init; }

    [SourceMember("Customer.Name")]
    public string CustomerName { get; init; } = "";

    [Ignore]
    public decimal Total { get; init; }

    [NullSubstitute("(no email)")]
    public string Email { get; init; } = "";
}

services.AddAtlas(typeof(OrderDto).Assembly);
// Discovers OrderDto via [Map]; mapping is convention + member-attribute driven.
```

### Class-level options

```csharp
[Map(typeof(Order),
         MemberList = MemberList.Source,
         ReverseMap = true,
         PreserveReferences = true)]
public class OrderDto { ... }
```

### Member attributes

| Attribute | Equivalent fluent |
|---|---|
| `[Ignore]` | `ForMember(d => d.X, opt => opt.Ignore())` |
| `[SourceMember("Path")]` | `ForMember(d => d.X, opt => opt.MapFrom(s => s.Path))` (with dotted-path support) |
| `[NullSubstitute(value)]` | `ForMember(d => d.X, opt => opt.NullSubstitute(value))` |

### Combining attributes

Multiple attributes on one property:

```csharp
[SourceMember("Customer.Email")]
[NullSubstitute("(no email)")]
public string CustomerEmail { get; init; } = "";
```

`[Ignore]` short-circuits — when `[Ignore]` is present, other attributes on the same property are unreachable.

### What attributes can express

| Feature | Attribute |
|---|---|
| Class declaration | `[Map(typeof(SourceType))]` |
| Validation policy | `[Map(MemberList = ...)]` |
| Auto-reverse | `[Map(ReverseMap = true)]` |
| Cycle-safe | `[Map(PreserveReferences = true)]` |
| Skip member | `[Ignore]` |
| Source-member redirect | `[SourceMember("name")]` (supports dotted paths) |
| Null fallback | `[NullSubstitute("default")]` |

### What attributes can't express

Attributes can't carry lambdas. Use a fluent profile (or a fluent `cfg.CreateMap<>` call) for: `MapFrom(expr)`, `Condition`/`PreCondition`, `BeforeMap`/`AfterMap`, `ConvertUsing`, `AddTransform`, `Include`/`IncludeBase`, `ForCtorParam`, `ForPath`, factory-form `NullSubstitute`, per-value enum overrides.

### Conflict rule

A `(TSource, TDestination)` pair must be declared exactly once. Declaring the same pair via both an attribute and a fluent `CreateMap` throws at config-build naming both registration sites.

---

## 14.13 Expression Translation (`UseAsDataSource`)

Wrap an `IQueryable<TSource>` and write filtering, sorting, and paging in destination-DTO terms. Atlas translates the destination-typed lambdas back to source-typed expressions before they hit your LINQ provider.

### Setup

```csharp
using Atlas.Projections;
```

### Usage

```csharp
public class OrderProfile : MapperProfile
{
    public OrderProfile() { CreateMap<Order, OrderDto>(); }
}

var orders = db.Orders
    .UseAsDataSource(mapperConfig)
    .For<OrderDto>()
    .Where(d => d.CustomerName.StartsWith("A"))
    .OrderBy(d => d.Total)
    .Take(10)
    .ToList();
```

The wrapper translates `d.CustomerName.StartsWith("A")` to `src.Customer.Name.StartsWith("A")` (per the typemap's `SourcePath`) before applying it to the underlying `IQueryable<Order>`. EF Core sees a normal source-typed expression and emits SQL like:

```sql
SELECT TOP(10) [proj].[Id], [c].[FirstName] AS [CustomerFirstName], ...
FROM [Orders] AS [proj]
INNER JOIN [Customers] AS [c] ON [proj].[CustomerId] = [c].[Id]
WHERE [c].[FirstName] LIKE 'A%'
ORDER BY [proj].[Total]
```

### Operator scope

| Category | Operators |
|---|---|
| Filtering | `Where` |
| Ordering | `OrderBy`, `OrderByDescending`, `ThenBy`, `ThenByDescending` |
| Paging | `Skip`, `Take` |
| Terminal predicate | `Any`, `All`, `Count(predicate)`, `First[OrDefault](predicate)`, `Single[OrDefault](predicate)`, `Last[OrDefault](predicate)` |

`Select`, `SelectMany`, `GroupBy`, `Include`, `Join`, async LINQ (`ToListAsync` etc.) are not on the wrapper. Use `AsQueryable()` to drop down to a translated `IQueryable<TDestination>`:

```csharp
var totals = db.Orders.UseAsDataSource(mapperConfig).For<OrderDto>()
    .Where(d => d.Total > 0)
    .AsQueryable()                  // returns IQueryable<OrderDto> with ProjectTo applied
    .Select(d => d.Total)            // standard LINQ from here
    .ToListAsync();
```

### Direct-use helper

`cfg.Translate<TSource, TDestination, TResult>(destExpr)` returns a translated `Expression<Func<TSource, TResult>>` for power-user composition:

```csharp
var srcPredicate = mapperConfig.Translate<Order, OrderDto, bool>(
    d => d.CustomerName == "Alice");
// srcPredicate is now Expression<Func<Order, bool>>: src => src.Customer.Name == "Alice"

var orders = db.Orders.Where(srcPredicate).ProjectTo<OrderDto>(mapperConfig).ToList();
```

### Rejection rule

Predicates against destination members that have no source mapping throw `AtlasProjectionException` at the operator call site:

- `[Ignore]`'d members → "destination member 'OrderDto.X' is configured with Ignore() and cannot be referenced in a UseAsDataSource expression."
- Constant-mapped members (`MapFrom("active")`) → "destination member 'OrderDto.Status' is a constant; predicates against it are trivially true/false."
- Unmapped members (no convention or fluent source) → "destination member 'OrderDto.X' has no PropertyMap."

The error message names the destination member so you can fix the configuration without reading the stack trace.

### Caching

Translation results cache per `(TypePair, lambda-reference-identity)`. Reuse `static readonly Expression<>` lambdas to maximize cache hits:

```csharp
public static class OrderFilters
{
    public static readonly Expression<Func<OrderDto, bool>> Active =
        d => d.Status == "Active";
}

// Both calls hit the cache after the first one:
db.Orders.UseAsDataSource(cfg).For<OrderDto>().Where(OrderFilters.Active).ToList();
db.Orders.UseAsDataSource(cfg).For<OrderDto>().Where(OrderFilters.Active).ToList();
```

Freshly-constructed lambdas (`d => d.Total > 100`) miss the cache (different reference each call). They translate once each; correctness unchanged.

### Limitations

- **Inner lambdas on collection-typed destination members are not translated** in v1. `d => d.Lines.Any(l => l.Total > 100)` throws at translate time; rewrite the predicate against the source (`db.Orders.Where(o => o.Lines.Any(l => l.Total > 100)).UseAsDataSource(cfg).For<OrderDto>()`) or use `AsQueryable()` and operate on the materialized destination collection.
- **Property access on collection-typed destination members** (e.g., `d => d.Lines.Count`) is rejected with a "nested map not registered" error. Workaround: rewrite against the source.
- **Derived-type dispatch via inheritance is not supported.** A wrapper bound to a base typemap can't translate predicates against derived-only properties. Workaround: `query.OfType<OnlineOrder>().UseAsDataSource(cfg).For<OnlineOrderDto>()`.
- **`NullSubstitute` Coalesce applies in projection only**, not predicate translation. `Where(d => d.Email == "(default)")` against a `NullSubstitute("(default)")` map matches no null-source rows in v1.
- **Bare-parameter usage** (`d => d == other` or `d => SomeFn(d)`) is not pre-detected. The LINQ provider's standard error fires at query execution.

### Compatibility with v2 features

| Feature | UseAsDataSource v1 |
|---|---|
| ProjectTo (#14.1) | ✓ Composes via enumeration |
| Inheritance (#14.2) | ✓ Root only; derived-dispatch limited |
| Enum surface (#14.3) | ✓ Works |
| ReverseMap (#14.4) | ✓ Works |
| `ForPath` (#14.4) | ✗ Rejected by existing dual-gate |
| Hooks (#14.5) | ✗ Rejected by existing dual-gate |
| Value transformers (#14.6 global/typemap) | ✓ Works |
| Profile-scope transformers (#14.6) | ✗ Don't fire (`OriginatingProfile == null`) |
| Conditional mapping (#14.7) | ✓ Inlined |
| Null substitution (#14.8) | ✓ Translates to `COALESCE` (projection only — predicate path is a v1 limitation) |
| Open generics (#14.9) | ✓ Closed pair via lazy materialization |
| Dynamic mapping (#14.10) | ✗ Rejected by existing dual-gate |
| `PreserveReferences` (#14.11) | ✗ Rejected by existing dual-gate |
| Attribute config (#14.12) | ✓ Works |

---

## 15. Performance Notes

### Per-call cost

- `mapper.Map<TSrc, TDest>(src)` — strongly-typed overload — is one delegate invocation plus the destination's allocation. No reflection, no dictionary lookups on the hot path.
- `mapper.Map<TDest>(object src)` — reflection-dispatch overload — adds one `MakeGenericMethod` lookup per call. Use the strongly-typed overload when the source type is known at compile time.
- `mapper.Map<S, D>(src, existingDest)` — update-in-place — same as the strongly-typed overload, no extra allocation.

### Allocation budget

Per-call, Atlas allocates:
- The destination instance itself.
- Collection allocations (one `List<T>` if the destination is a list, etc.).
- Nothing else internal — no per-call dictionaries, no per-call context bags.

The exception: `PreserveReferences()`-flagged maps allocate one `MappingContext` per top-level `Map` call.

### Configuration-build cost

- `MapperConfiguration` construction is O(profiles × maps × members). For a typical 50-profile / 200-map application, expect tens of milliseconds at startup.
- `CompileMappings()` JIT-compiles each typemap's body. Cost is approximately linear in member count.
- `services.AddAtlas(asm)` calls `CompileMappings()` automatically, so this cost is paid at startup, not first request.

### Benchmark project

`tests/Atlas.Benchmarks/` contains BenchmarkDotNet benchmarks for cold-call, warm-call, and config-build scenarios. Run via:

```pwsh
dotnet run -c Release --project tests/Atlas.Benchmarks
```

---

## 16. Limitations

These limitations are intentional in v1 (Atlas v2 final). Each has a documented workaround.

### Native AOT not supported

Atlas uses `Expression.Compile()` which produces dynamic IL. Native AOT (which forbids dynamic IL) requires a source-generator-based alternative implementation. This was always a v3+ item in the original v1 design.

### `UseAsDataSource` v1 limitations (see §14.13)

- Inner lambdas on collection-typed destination members.
- Property access on collection-typed destination members.
- Derived-type dispatch via inheritance.
- Predicate-path NullSubstitute (Coalesce only fires in projection).
- Nullable-widening Convert wrap.

### Profile-scope value transformers don't fire on certain TypeMaps

TypeMaps with `OriginatingProfile == null` — attribute-declared (#14.12), open-generic materialized closed pairs (#14.9), and dynamic (#14.10) — don't see profile-scope transformers. Use global scope for cross-cutting transforms.

### Various per-feature deferrals to v3

Each v2 feature has explicitly-deferred-to-v3 sub-items. Refer to the individual `docs/Atlas-Design-<Feature>.md` files for the full deferred list per feature. Common themes:
- Lambda-shaped attribute config (e.g., `[ValueConverter(typeof(...))]`).
- Async hooks / async LINQ on the wrapper.
- Custom interfaces (`IReferenceHandler`, `IValueTransformer<T>`).
- `ConvertUsing(typeof(<>))` on open generics.
- `ForMember`/`Include`/etc. on open generics.

---

## 17. Further Reading

### Design documents (per feature)

Each v2 feature has a comprehensive design doc under `docs/`:

- `Atlas-Design.md` — v1 baseline.
- `Atlas-Design-ProjectTo.md` — IQueryable projection.
- `Atlas-Design-Inheritance.md` — `Include`/`IncludeBase`.
- `Atlas-Design-EnumSurface.md` — enum mapping.
- `Atlas-Design-ReverseMap.md` — `ReverseMap`/`ForPath`.
- `Atlas-Design-BeforeAfterHooks.md` — hooks.
- `Atlas-Design-ValueTransformers.md` — transformers.
- `Atlas-Design-ConditionalMapping.md` — `Condition`/`PreCondition`.
- `Atlas-Design-NullSubstitution.md` — null substitution.
- `Atlas-Design-OpenGenerics.md` — open generics.
- `Atlas-Design-DynamicMapping.md` — dynamic / dictionary.
- `Atlas-Design-ReferenceHandling.md` — cycles.
- `Atlas-Design-AttributeConfig.md` — attribute config.
- `Atlas-Design-ExpressionTranslation.md` — `UseAsDataSource`.

### Reference inputs

- `docs/Object-Mapping-Functional-Reference.md` — vendor-neutral capability descriptions.
- `docs/AutoMapper-Analysis.md` — AutoMapper feature analysis.
- `docs/Mapperly-Analysis.md` — Mapperly source-generator analysis.

### Quick reference

- README at the repo root has a high-level summary and migration notes.
- xmldoc on every public type and method (Visual Studio / Rider / VS Code IntelliSense).

---

**Atlas v2 final. Happy mapping.**
