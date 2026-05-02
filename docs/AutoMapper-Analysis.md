# AutoMapper: Comprehensive Technical Analysis

> A complete reference on AutoMapper — what it does, how it works internally, every major feature, and the current state of the project (as of v16.x, 2026).

---

## 1. Project Overview

**AutoMapper** is a convention-based, object-to-object mapper for .NET. It eliminates the hand-written boilerplate that maps one type to another (typically domain → DTO and back) by inferring the mapping from naming conventions, with hooks to override or extend whenever the convention is wrong.

| Attribute | Value |
|---|---|
| Repository | `LuckyPennySoftware/AutoMapper` (formerly `AutoMapper/AutoMapper`) |
| Current version | **v16.1.1** (March 2026) |
| Target frameworks | .NET 8.0+, .NET 9.0, .NET Standard 2.0 (legacy: .NET Framework 4.6.2+) |
| Language | C# (99.8%) |
| Adoption | 10.2k★, 2.4k forks, hundreds of millions of NuGet downloads |
| License | Dual: **RPL-1.5** (reciprocal/copyleft) + **commercial** via Lucky Penny Software |
| Owner | Lucky Penny Software (founded by Jimmy Bogard) |

### Tagline / value proposition
> *"Mapping code is boring. Testing mapping code is even more boring."*

AutoMapper exists to (a) remove that boilerplate, (b) keep mapping logic out of domain models, and (c) let you validate your entire mapping surface in a single test.

### When NOT to use it
The maintainers themselves have written publicly that AutoMapper is misused when:
- Source and destination shapes are radically different (you're writing more `ForMember` than the convention saves you).
- The mapping is the business logic (you're hiding domain transforms inside a `IValueResolver`).
- You're inside a hot path where the cost of going through the execution-plan engine outweighs hand-written assignment code.

For straight DTO ↔ entity flattening with EF Core, it remains a strong fit.

---

## 2. Core Architecture & Technical Approach

AutoMapper is not a runtime reflection mapper; it is an **expression-tree compiler**. Understanding this is the key to understanding everything else.

### 2.1 The Execution Plan
For every `(TSource, TDestination)` pair, AutoMapper builds an `Expression` tree describing how to copy values from source to destination. That expression is compiled (`Expression.Compile()`) into a delegate the first time the mapping is used (or eagerly via `CompileMappings()`). Every subsequent call invokes the compiled delegate, which is close to hand-written code in performance.

Two consequences:
- **`MapperConfiguration` is expensive to build, cheap to use.** It must be built once per process and stored as a singleton.
- **`IMapper` is thread-safe and stateless** with respect to the configuration; you inject the same instance everywhere.

### 2.2 Inspecting the generated code
```csharp
var plan = configuration.BuildExecutionPlan(typeof(Foo), typeof(Bar));
```
Combined with the **ReadableExpressions** NuGet package or VS extension, this prints the actual C# the compiler generated. Invaluable for diagnosing surprising behavior. (Strip these calls before shipping.)

### 2.3 The two execution paths
AutoMapper has two distinct code-generation backends:

| Path | Used by | Output | Notes |
|---|---|---|---|
| **In-memory mapping** | `mapper.Map<>()` | Compiled `Func<TSource, TDestination>` | Full feature set: resolvers, before/after, value converters, custom funcs. |
| **Queryable projection** | `IQueryable.ProjectTo<>()` | LINQ `Expression` tree handed to the underlying provider (EF Core, NHibernate, …) | Only what the provider can translate to SQL is supported. |

Many features work in one path but not the other. The most common gotcha across the project's lifetime is "it works in `Map` but throws on `ProjectTo`." See § 8.

### 2.4 Configuration validation
Because the entire mapping surface is described by configuration, AutoMapper can walk the type maps at startup and verify every destination member has a source. `AssertConfigurationIsValid()` is the recommended unit test — it catches refactoring drift before it ships.

---

## 3. Configuration Model

### 3.1 `MapperConfiguration`
The root container. Built once per process:

```csharp
var config = new MapperConfiguration(cfg => {
    cfg.CreateMap<Foo, Bar>();
    cfg.AddProfile<FooProfile>();
}, loggerFactory);
```

Since v15, the constructor **requires an `ILoggerFactory`** (used for license-status logs and diagnostics). The static `Mapper.Initialize` API was deprecated in v9 and removed long since.

### 3.2 Profiles
Profiles group related maps and let you scope conventions to a subset of the application:

```csharp
public class OrganizationProfile : Profile
{
    public OrganizationProfile()
    {
        CreateMap<Foo, FooDto>();
        CreateMap<Bar, BarDto>();
    }
}
```

Profile-level settings (naming conventions, prefixes, value transformers, etc.) only apply inside the profile; root-level settings apply everywhere. This is the recommended way to organize mappings in any non-trivial app.

### 3.3 Assembly scanning
Auto-discover both fluent profiles and attribute-based maps:

```csharp
cfg.AddMaps(myAssembly);
cfg.AddMaps(new[] { "Foo.UI", "Foo.Core" });          // by name
cfg.AddMaps(new[] { typeof(HomeController), typeof(Entity) }); // by marker
```

### 3.4 Compilation control
```csharp
configuration.CompileMappings();   // eager compile (recommended at startup)
```
Per-member or global control of compilation depth keeps startup time in check on huge maps:
```csharp
opt.MapAtRuntime();                 // skip compilation, evaluate at call time
cfg.Internal().MaxExecutionPlanDepth = 0;
```

---

## 4. Conventions: How Automatic Mapping Actually Works

### 4.1 Name-based matching
A destination member is mapped from a source member with the same name. This is AutoMapper's whole reason for existing.

### 4.2 Flattening
The convention is recursive: a destination property `CustomerName` will match `source.Customer.Name` (any depth). Likewise, `OrderTotal` will match `source.Order.Total`. This is what "AutoMapper flattens object graphs" means in practice.

### 4.3 Method matching with prefix recognition
A destination property can be filled from a source **method** if the method name matches. By default the prefix `Get` is stripped: `OrderDto.Total` will be filled by `Order.GetTotal()`.

```csharp
cfg.RecognizePrefixes("frm");     // also strip "frm"
cfg.ClearPrefixes();              // disable the default "Get"
```

You can also recognize **postfixes** and **substring** replacements:
```csharp
cfg.ReplaceMemberName("Ä", "A");
cfg.ReplaceMemberName("Airlina", "Airline");
```

### 4.4 Naming conventions (e.g. snake_case → PascalCase)
```csharp
cfg.SourceMemberNamingConvention      = LowerUnderscoreNamingConvention.Instance;
cfg.DestinationMemberNamingConvention = PascalCaseNamingConvention.Instance;
// Maps property_name -> PropertyName
```

Built-in conventions: `PascalCaseNamingConvention`, `LowerUnderscoreNamingConvention`, `ExactMatchNamingConvention` (disable convention matching entirely).

### 4.5 Member visibility & filtering
```csharp
cfg.ShouldMapField    = fi => false;
cfg.ShouldMapProperty = pi =>
    pi.GetMethod != null && (pi.GetMethod.IsPublic || pi.GetMethod.IsAssembly);
```

### 4.6 `IncludeMembers` — controlled flattening
When automatic flattening can't disambiguate, lift a child object's mapped members into the parent's destination map:
```csharp
cfg.CreateMap<Source, Destination>()
   .IncludeMembers(s => s.InnerSource, s => s.OtherInnerSource);
cfg.CreateMap<InnerSource, Destination>(MemberList.None);
cfg.CreateMap<OtherInnerSource, Destination>();
```
Match resolution happens at **configuration time** (static analysis on the expression). The order of the parameters matters — first hit wins, with the source object itself checked first.

---

## 5. Customization Surface (the override hooks)

When the convention is wrong, you have an extensive set of opt-out / opt-in hooks. Listed roughly from most-common to most-specialized.

### 5.1 `ForMember` + `MapFrom`
The everyday workhorse. Tells AutoMapper how to fill one destination property:
```csharp
cfg.CreateMap<CalendarEvent, CalendarEventForm>()
   .ForMember(d => d.EventDate,   o => o.MapFrom(s => s.Date.Date))
   .ForMember(d => d.EventHour,   o => o.MapFrom(s => s.Date.Hour))
   .ForMember(d => d.EventMinute, o => o.MapFrom(s => s.Date.Minute));
```
Two flavors: an **expression** form (works in `ProjectTo`) and a **`Func`** form (in-memory only).

### 5.2 `Ignore`
Skip the destination member entirely (also removes it from validation):
```csharp
.ForMember(d => d.SomeProperty, o => o.Ignore());
```

### 5.3 `ForCtorParam`
For destinations with constructor parameters (records, immutable types):
```csharp
.ForCtorParam("paramName", o => o.MapFrom(s => s.Value));
cfg.DisableConstructorMapping();      // turn it off entirely
cfg.ShouldUseConstructor = c => c.IsPublic;
```

### 5.4 Custom Type Converters — `ITypeConverter<TSource, TDestination>`
**Global** transform between unrelated types. Once registered, every `Source → Destination` mapping uses it:
```csharp
cfg.CreateMap<string, int>().ConvertUsing(s => Convert.ToInt32(s));
cfg.CreateMap<string, DateTime>().ConvertUsing(new DateTimeTypeConverter());
cfg.CreateMap<string, Type>().ConvertUsing<TypeTypeConverter>();

public class DateTimeTypeConverter : ITypeConverter<string, DateTime>
{
    public DateTime Convert(string src, DateTime dest, ResolutionContext ctx)
        => System.Convert.ToDateTime(src);
}
```

### 5.5 Custom Value Resolvers — `IValueResolver<TSource, TDestination, TDestMember>`
**Per-member** custom logic, with full access to source, destination, and resolution context:
```csharp
public interface IValueResolver<in TSource, in TDestination, TDestMember>
{
    TDestMember Resolve(TSource src, TDestination dest, TDestMember destMember, ResolutionContext ctx);
}

cfg.CreateMap<Source, Destination>()
   .ForMember(d => d.Total, o => o.MapFrom<CustomResolver>());
```

`IMemberValueResolver<TSource, TDestination, TSourceMember, TDestMember>` lets you reuse a resolver across mappings by redirecting which source member feeds it.

The returned value is itself put through any applicable type maps — resolvers don't bypass mapping, they feed into it. Resolvers run **before** `Condition` is evaluated.

### 5.6 Value Converters — `IValueConverter<TSourceMember, TDestMember>`
The middle ground between type converters (global) and value resolvers (per-member, full context). Scoped to a single map, signature is just `member → member`:

```csharp
public class CurrencyFormatter : IValueConverter<decimal, string>
{
    public string Convert(decimal src, ResolutionContext ctx) => src.ToString("c");
}

cfg.CreateMap<Order, OrderDto>()
   .ForMember(d => d.Amount, o => o.ConvertUsing(new CurrencyFormatter()));
```

**Limitation:** in-memory only — value converters are **not** supported by `ProjectTo`.

### 5.7 Value Transformers — `AddTransform<T>`
Post-processing for a given type, applied wherever it appears in destinations. Can be set at four scopes: **global / profile / type-map / member**.
```csharp
cfg.ValueTransformers.Add<string>(val => val + "!!!");
// Now every mapped string gets "!!!" appended.
```

### 5.8 `Condition` and `PreCondition`
Decide whether to map a member at all:
- **`PreCondition`** — runs *before* value resolution. Use it when resolution is expensive.
- **`Condition`** — runs *after* resolution but before assignment.

```csharp
.ForMember(d => d.baz, o => {
    o.PreCondition(s => s.baz >= 0);
    o.MapFrom(s => ExpensiveLookup(s));   // bypassed if PreCondition fails
});
```
Pipeline order: **PreCondition → resolution (MapFrom / resolver) → Condition → assignment**.

### 5.9 `NullSubstitute`
Provide a fallback when a source value (anywhere in the chain) is null. The substitute is treated as a source-typed value and goes through the same type conversion pipeline:
```csharp
.ForMember(d => d.Value, o => o.NullSubstitute("Other Value"));
```

### 5.10 Before/After Map actions
Run code at well-defined points around the mapping:
```csharp
cfg.CreateMap<Source, Dest>()
   .BeforeMap((src, dest) => src.Value += 10)
   .AfterMap ((src, dest) => dest.Name  = "John");
```

For DI-friendly logic, implement `IMappingAction<TSource, TDestination>`:
```csharp
public class SetTraceIdentifierAction : IMappingAction<SomeModel, SomeOtherModel>
{
    private readonly IHttpContextAccessor _http;
    public SetTraceIdentifierAction(IHttpContextAccessor http) => _http = http;

    public void Process(SomeModel src, SomeOtherModel dest, ResolutionContext ctx)
        => dest.TraceIdentifier = _http.HttpContext.TraceIdentifier;
}

cfg.CreateMap<SomeModel, SomeOtherModel>()
   .AfterMap<SetTraceIdentifierAction>();
```
This is the canonical workaround for the fact that **profiles themselves cannot have constructor dependencies**.

---

## 6. Inheritance & Polymorphism

### 6.1 Sharing configuration up the hierarchy
- `Include<TDerivedSource, TDerivedDestination>()` — declared on the **base** map.
- `IncludeBase<TBaseSource, TBaseDestination>()` — declared on the **derived** map.
- `IncludeAllDerived()` — convenience; slower because it scans all maps.
- `As<T>()` — point a base mapping at an existing derived mapping without writing a new one.

### 6.2 Runtime polymorphism
If you call `mapper.Map<OrderDto>(order)` where `order` is actually an `OnlineOrder`, AutoMapper picks the most-specific configured map (`OnlineOrder → OnlineOrderDto`) automatically. The same applies to **collection elements**: derived elements are mapped to their derived destination types, but you must register the child mappings explicitly (`Include<ChildSource, ChildDestination>()`).

### 6.3 Mapping-priority order
1. Explicit `MapFrom`
2. Inherited explicit mapping (from base class)
3. Explicit `Ignore`
4. Convention-based match

Ignored properties on the base override conventions on the derived. This catches people out — keep it in mind when refactoring inheritance.

---

## 7. Collections

### 7.1 Supported destinations
`IEnumerable`, `IEnumerable<T>`, `ICollection`, `ICollection<T>`, `IList`, `IList<T>`, `List<T>`, arrays. You only configure the **element** mapping; collection wrapping is automatic.

### 7.2 Null collections
By default, **null source collections become empty destination collections**, in line with the Framework Design Guidelines stance that collection references should never be null. Override globally or per scope:
```csharp
cfg.AllowNullCollections = true;       // global
opt.AllowNull();                       // per member
opt.DoNotAllowNull();
```

### 7.3 Polymorphic elements
See §6.2 — same rules apply. Register the child mappings if you want derived element types to map to derived destinations.

---

## 8. Queryable Extensions & `ProjectTo`

This is the feature that drives most production usage of AutoMapper inside an EF Core / NHibernate stack.

### 8.1 What it does
`ProjectTo<TDestination>()` translates the configured mapping into a LINQ `Expression` and hands it to the underlying provider, which turns it into SQL. The result: only the columns you actually need land in the `SELECT` clause — no full entity graph load, no N+1.

```csharp
var config = new MapperConfiguration(cfg =>
    cfg.CreateProjection<OrderLine, OrderLineDTO>()
       .ForMember(dto => dto.Item, c => c.MapFrom(ol => ol.Item.Name))
);

return context.OrderLines
              .Where(ol => ol.OrderId == orderId)
              .ProjectTo<OrderLineDTO>(configuration)
              .ToList();
```

**Rule of thumb:** filter and sort first on the entity, *then* `ProjectTo` last. Operations after `ProjectTo` filter on the DTO shape and sometimes break translation.

### 8.2 What's supported in `ProjectTo`
- `MapFrom` (expression form only)
- `ConvertUsing` (expression form)
- `Ignore`
- `NullSubstitute`
- Value transformers
- `IncludeMembers`
- Runtime polymorphism via `Include` / `IncludeBase`

### 8.3 What's **not** supported
- `Condition`, `SetMappingOrder`
- `UseDestinationValue`
- `MapFrom` (Func-based)
- Before/AfterMap, custom value resolvers
- Custom type converters, `ForPath`
- Calculated properties on the domain object (anything the provider can't translate)

This list is the source of most "works in `Map`, blows up in `ProjectTo`" bugs.

### 8.4 N+1 prevention
Nested collections in the projection cause the provider to emit appropriate joins automatically — you do **not** call `Include()`. If you need to filter a related collection, do so in the `MapFrom` expression itself.

### 8.5 Type coercion
Strings are auto-`ToString()`ed. For other coercions, use the expression form of `ConvertUsing`.

### 8.6 Parameterization
Pass runtime values into the projection without post-mapping code:
```csharp
string currentUserName = null; // captured but not yet bound
cfg.CreateProjection<Course, CourseModel>()
   .ForMember(m => m.CurrentUserName, o => o.MapFrom(_ => currentUserName));

dbContext.Courses.ProjectTo<CourseModel>(
    Config,
    new { currentUserName = Request.User.Name }   // bound here
);
```

### 8.7 Recursive models
Disabled by default to prevent infinite expansion:
```csharp
configuration.Internal().RecursiveQueriesMaxDepth = 3;
```

### 8.8 Explicit expansion
For very wide DTOs, you can mark members as opt-in: callers pass which members to expand and only those are projected.

---

## 9. Expression Translation — `UseAsDataSource`

Provided by the `AutoMapper.Extensions.ExpressionMapping` package. It rewrites lambda expressions written against the **DTO** so they run against the **entity**:

```csharp
dataContext.OrderLines
    .UseAsDataSource()
    .For<OrderLineDTO>()
    .Where(dto => dto.Name.StartsWith("A"));   // translated to filter on entity
```

Useful when a UI/API layer wants to express filtering, sorting, and `Include`s in DTO terms but the persistence layer must run them against domain types. `MapExpressionAsInclude<>()` extends this to `Include` expressions for navigation properties.

---

## 10. Specialized Mapping Modes

### 10.1 Reverse mapping & unflattening
`ReverseMap()` produces the inverse map automatically, including reversing flattening:
```csharp
cfg.CreateMap<Order, OrderDto>().ReverseMap();
// CustomerName -> Customer.Name on the way back
```
Where get/set paths diverge, use `ForPath`:
```csharp
.ReverseMap()
.ForPath(s => s.Customer.Name, o => o.MapFrom(d => d.CustomerName));
```
By default `ReverseMap()` is created with `MemberList.None` (validation off on the reverse direction).

### 10.2 Open generics
Configure once, apply to every closed combination:
```csharp
cfg.CreateMap(typeof(Source<>), typeof(Destination<>));
.ConvertUsing(typeof(Converter<>));      // single-arg
.ConvertUsing(typeof(Converter<,>));     // src + dest
```
Open generic maps are skipped by validation since not every closed pair will be valid.

### 10.3 Dynamic / `ExpandoObject` / dictionaries
Works without explicit configuration — to and from `dynamic`, `ExpandoObject`, and `Dictionary<string, object>`. Dot-notation keys (`InnerFoo.Bar`) populate nested members.

### 10.4 Enum mapping (`AutoMapper.Extensions.EnumMapping`)
Built-in enum mapping is by-value. The extension package adds convention-based enum mapping with overrides:
```csharp
CreateMap<Source, Destination>()
    .ConvertUsingEnumMapping(o => o.MapValue(Source.First, Destination.Default))
    .ReverseMap();
```
Modes: `MapByValue` (default) or `MapByName()`. Validation extends `AssertConfigurationIsValid`:
```csharp
configuration.EnableEnumMappingValidation();
```
Reverse mapping is rebuilt deterministically so overrides flip correctly.

### 10.5 Attribute-based mapping
Declare maps with attributes instead of fluent code:
```csharp
[AutoMap(typeof(Order))]
public class OrderDto
{
    [Ignore] public decimal Total { get; set; }

    [SourceMember(nameof(Order.OrderTotal))]
    public decimal Amount { get; set; }
}
```
Supporting attributes mirror most fluent options: `MapAtRuntimeAttribute`, `MappingOrderAttribute`, `NullSubstituteAttribute`, `UseExistingValueAttribute`, `ValueConverterAttribute`. **Limitation:** attributes don't accept expressions, so true flattening or computed mappings still need the fluent API.

---

## 11. Dependency Injection & Lifecycle

### 11.1 Registration
Since v13, `AddAutoMapper` is in the core package:
```csharp
services.AddAutoMapper(cfg => { cfg.LicenseKey = "..."; },
                       typeof(ProfileType1).Assembly);

services.AddAutoMapper(cfg => { /* ... */ },
                       typeof(ProfileType1), typeof(ProfileType2));
```

### 11.2 What gets registered
- `MapperConfiguration` as singleton.
- `IMapper` as transient (cheap; wraps the singleton config).
- All discovered profiles instantiated and added to the configuration.

### 11.3 Injecting `IMapper`
```csharp
public class EmployeesController(IMapper mapper) {
    public IActionResult Get(int id) =>
        Ok(_db.Employees.Where(e => e.Id == id)
                        .ProjectTo<EmployeeDto>(mapper.ConfigurationProvider)
                        .First());
}
```

### 11.4 DI inside resolvers / converters / actions
- `IValueResolver`, `ITypeConverter`, `IValueConverter`, `IMappingAction` — **resolved from the DI container** when AutoMapper instantiates them.
- `Profile` classes themselves — **cannot** receive constructor dependencies.

The way around the profile constraint is to put the dependency-needing logic in an `IMappingAction` and reference the action type from the profile.

### 11.5 v15 `AddAutoMapper` signature change (breaking)
```csharp
// pre-v15
services.AddAutoMapper(typeof(Program));

// v15+
services.AddAutoMapper(cfg => cfg.LicenseKey = "...", typeof(Program));
```
The `Action<IMapperConfigurationExpression>` parameter is now required.

### 11.6 Alternative containers
Service-location hooks for non-MS containers:
```csharp
cfg.ConstructServicesUsing(ObjectFactory.GetInstance);
var mapper = new Mapper(configuration, childContainer.GetInstance);
```
Third-party packages exist for Autofac and others.

---

## 12. Configuration Validation

The single most useful production safety net.

```csharp
[Fact]
public void AutoMapper_Configuration_Is_Valid()
{
    var config = new MapperConfiguration(cfg => cfg.AddMaps(typeof(SomeProfile)),
                                         NullLoggerFactory.Instance);
    config.AssertConfigurationIsValid();
}
```

What it checks: every destination member that *could* be filled has a configured source (or is explicitly ignored). Throws `AutoMapperConfigurationException` listing every violation.

### 12.1 Member-list selection
```csharp
cfg.CreateMap<Source, Destination>(MemberList.Source);       // validate sources are used
cfg.CreateMap<Source, Destination>(MemberList.Destination);  // default
cfg.CreateMap<Source, Destination>(MemberList.None);         // skip
```
`ReverseMap()` defaults to `MemberList.None` because reverse expectations rarely match the original direction.

### 12.2 Enum mapping validation
Opt-in via `configuration.EnableEnumMappingValidation()`.

---

## 13. Licensing & Current State (2026)

In **July 2025**, AutoMapper (and MediatR) moved to **Lucky Penny Software**, a new company founded by Jimmy Bogard to house both projects, and adopted a **dual license**.

### 13.1 The dual license
- **Open-source side:** Reciprocal Public License 1.5 (RPL-1.5). Strong copyleft — if you build software on AutoMapper's source or binaries, you must release your software under RPL-1.5.
- **Commercial side:** A Lucky Penny Software commercial license, exempting you from the reciprocity requirement.

### 13.2 Commercial pricing tiers (team-based, not per-seat)
| Tier | Team size | Price |
|---|---|---|
| **Community** | Free, see qualifications below | £0 |
| **Standard** | 1–10 developers | £40–65/month |
| **Professional** | 11–50 developers | £120–195/month |
| **Enterprise** | Unlimited | £320–520/month |

Annual and bundle (AutoMapper + MediatR) discounts are available.

### 13.3 Free Community qualifications
- Companies with **under $5M annual revenue**
- Non-profits with **under $5M budget**
- Educational institutions
- Non-production environments

### 13.4 License key configuration
```csharp
services.AddAutoMapper(cfg => cfg.LicenseKey = "...");
// or
new MapperConfiguration(cfg => cfg.LicenseKey = "...", loggerFactory);
```

### 13.5 Enforcement model
**Logging only.** No license server, no outbound HTTP, no degraded features:
- `INFO` — valid license
- `WARNING` — missing license
- `ERROR` — invalid/expired license

Logs are written under category `LuckyPennySoftware.AutoMapper.License` via `Microsoft.Extensions.Logging`. Filter that category to `LogLevel.None` for client-side redistributables (Blazor WASM, MAUI, WPF) where the key shouldn't be embedded.

### 13.6 Backward compatibility
Pre-v15 versions remain on NuGet under their original MIT-style open-source licenses, unchanged. If you cannot or will not adopt the dual license, you can pin to those versions — at the cost of no further fixes.

### 13.7 v15 breaking changes (in addition to licensing)
- `MapperConfiguration` ctor now requires `ILoggerFactory`.
- `AddAutoMapper` overloads restructured (config callback first).
- Targets restricted to .NET 8/9 + .NET Standard 2.0.

---

## 14. Companion Libraries

| Package | Purpose |
|---|---|
| `AutoMapper.Extensions.Microsoft.DependencyInjection` | (Now folded into core for v13+) DI integration |
| `AutoMapper.Extensions.ExpressionMapping` | `UseAsDataSource`, expression rewrites between DTO and entity |
| `AutoMapper.Extensions.EnumMapping` | Convention-based enum mapping with overrides + validation |
| `AutoMapper.Collection` / `AutoMapper.Collection.EntityFrameworkCore` | Equivalency-based collection updates (insert/update/delete diff) |
| `AutoMapper.EF6` | Specific helpers for EF6 |
| `AutoMapper.Data` | `IDataReader` / `IDataRecord` projection |

---

## 15. Production Patterns & Pitfalls

### 15.1 Lifetime
Build `MapperConfiguration` **once**. Inject `IMapper` (or use `mapper.ConfigurationProvider` with `ProjectTo`) everywhere. Don't `new MapperConfiguration` per request.

### 15.2 Validate in CI
Wire `AssertConfigurationIsValid()` into a unit test. This is the highest-leverage piece of safety net the library offers — it catches missing maps the moment a property is renamed.

### 15.3 Prefer `ProjectTo` over `Map` for read paths
A `Where … ToList … Map<List<Dto>>` pattern loads full entities. The same code with `ProjectTo` issues a SQL `SELECT` of just the DTO columns. The performance gap is often an order of magnitude.

### 15.4 Don't hide business logic in resolvers
If a "mapping" computes prices, applies discounts, or runs domain rules, that's domain code that doesn't belong in a profile. Resolvers should be thin format/coercion adapters.

### 15.5 Keep profiles dependency-free
Profile constructors don't get DI. If you need services (HTTP context, config, repos), put the work in an `IMappingAction` / `IValueResolver` / `IValueConverter` and reference the type from the profile.

### 15.6 Watch for `ProjectTo`-incompatible features
When a feature works locally with `Map` but throws against an `IQueryable`, the most likely cause is `Func`-based `MapFrom`, a value converter, a `Condition`, or a custom resolver. Cross-check § 8.3.

### 15.7 Compile up front
```csharp
configuration.CompileMappings();
```
Pay the JIT cost at startup, not on the first request.

### 15.8 Be deliberate about flattening conventions
Convention-driven flattening is magic when it works and bewildering when it doesn't. For non-trivial graphs, `IncludeMembers` (with explicit child maps) is more predictable than waiting for the convention to discover the right path.

---

## 16. Documentation Map

The official docs live at <https://docs.automapper.io>. The page hierarchy as of 2026:

- **Overview**: Getting Started, Understanding Your Mappings, License Enforcement, Client Redistribution
- **Features**: Configuration, Configuration Validation, Dependency Injection, Projection, Nested Mappings, Lists and Arrays, Construction, Flattening (with `IncludeMembers`), Reverse Mapping & Unflattening, Mapping Inheritance, Attribute Mapping, Dynamic & ExpandoObject Mapping, Open Generics, Queryable Extensions, Expression Translation (`UseAsDataSource`), `AutoMapper.Extensions.EnumMapping`
- **Extensibility**: Custom Type Converters, Custom Value Resolvers, Conditional Mapping, Null Substitution, Value Converters, Value Transformers, Before/After Map Actions
- **Upgrading**: Per-version guides (15.0, 13.0, 12.0, 11.0, 10.0, 9.0, 8.1.1, 8.0, 5.0)

---

## 17. One-Line Summary by Layer

| Layer | What it is |
|---|---|
| **Mental model** | A code generator that compiles your "this maps to that" rules into a fast delegate, validates them statically, and gives you sharp escape hatches. |
| **Convention engine** | Recursive name-matching with prefix/postfix stripping and configurable naming styles, producing flattening for free. |
| **Customization surface** | Layered hooks: per-member (`MapFrom`/resolver/converter/condition), per-type (type converter), per-scope (transformers), per-call (before/after, items/state). |
| **Read-side optimization** | `ProjectTo` rewrites the mapping as a LINQ expression so the DB returns DTO-shaped rows directly. |
| **Productivity safety net** | `AssertConfigurationIsValid` proves at startup/test time that no destination property is silently un-mapped. |
| **Business model (2026)** | Dual-licensed (RPL-1.5 + commercial), free under $5M revenue / non-prod / non-profits, log-only enforcement. |

---

*Sources: docs.automapper.io, github.com/LuckyPennySoftware/AutoMapper, jimmybogard.com (commercial-launch and licensing-update posts), automapper.io (homepage and pricing).*
