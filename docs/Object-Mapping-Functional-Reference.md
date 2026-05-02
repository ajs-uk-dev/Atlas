# Object-to-Object Mapping in .NET — Functional Reference

> A vendor-neutral catalogue of the capabilities, configuration surface, and runtime behaviors that an object-to-object mapping library for .NET is expected to provide. This document describes *what* such a library does and *how* the features are typically exposed, without reference to any particular implementation.

---

## 1. Purpose

An object-to-object mapper transforms an instance of one type (the **source**) into an instance of another type (the **destination**), copying or computing each destination member from the source. It exists to eliminate hand-written boilerplate in the boundary code between layers — domain ↔ DTO, entity ↔ view model, request ↔ command — and to keep that mapping logic out of the model types themselves.

The functional value comes from three properties:

1. **Convention-driven defaults** — most members map automatically by name.
2. **Targeted overrides** — declarative hooks let you correct the convention when it is wrong, without writing the whole mapping by hand.
3. **Validation** — the mapping surface can be checked as a whole so that renames, additions, and removals are caught instead of silently producing wrong output.

---

## 2. Implementation Strategies

Two implementation strategies dominate, and the choice shapes everything else about how a library behaves.

### 2.1 Runtime expression compilation
The library reads its configuration, builds an `Expression` tree describing the copy from source to destination, and compiles it (`Expression.Compile()`) into a delegate at first use (or eagerly at startup). Subsequent calls invoke the cached delegate.

Properties:
- Configuration is dynamic — mappings can be added, removed, or rebuilt at runtime.
- A warm-up cost is paid the first time each mapping runs, unless explicitly pre-compiled.
- Reflection is used during expression construction; the runtime call path itself is reflection-free.
- Less compatible with aggressive trimming and Native AOT, because the runtime needs metadata that those tools strip.

### 2.2 Compile-time source generation
The library is a Roslyn source generator. The user declares partial classes with method signatures and annotations, and the generator emits the implementation as ordinary C# at build time. The generated code is part of the assembly.

Properties:
- Zero runtime reflection, zero startup cost.
- Trim-safe and Native AOT-compatible.
- Configuration is fixed at compile time; nothing can be reconfigured at runtime.
- Mismatches and unmapped members surface as build-time analyzer diagnostics rather than runtime exceptions.

### 2.3 Inspecting the generated work
Both strategies expose what they actually produce:

- **Expression-based**: an API to retrieve the compiled `Expression` tree for any source/destination pair, typically combined with an expression-prettifier to render it as readable C#.
- **Generator-based**: an MSBuild flag (e.g. `EmitCompilerGeneratedFiles`) that persists the generated `.g.cs` files into a configured output directory.

Either way, when the behavior of a mapping is surprising, reading the generated code is the canonical first debugging step.

---

## 3. Configuration Model

### 3.1 The configuration container
There is a single configuration object that holds every type-pair mapping the library knows about. It is **expensive to build, cheap to use**: it must be constructed once per process and stored as a singleton.

The mapper instance derived from it is **thread-safe and stateless** with respect to that configuration; it can be injected as a singleton or transient with no functional difference.

### 3.2 Grouping mappings into modules
Maps are grouped into modules (variously called profiles, mapper classes, configuration units). A module:

- bundles related mappings,
- carries module-scoped settings (naming conventions, prefixes, transformers) that apply only to maps inside it,
- can be discovered automatically by scanning assemblies, type-marker lists, or namespace lists.

### 3.3 Configuration scopes
Settings cascade from broad to narrow. Higher (more specific) scopes win:

1. Global / root configuration
2. Assembly-level defaults
3. Module / class-level configuration
4. Per-mapping configuration
5. Per-member configuration

There is no implicit reconciliation; an explicit per-member override always wins over a global rule.

### 3.4 Compilation control
For runtime-compiled implementations:
- An eager-compile entry point lets you pay the JIT cost at startup rather than first request.
- A per-member opt-out lets you defer compilation for specific paths if startup cost is dominated by an unused branch.
- A maximum-depth setting bounds compilation work for deeply nested type graphs.

For source-generator implementations, all of this collapses into the build itself.

---

## 4. Conventions

The conventions are what make convention-based mapping worth using; everything else is overrides.

### 4.1 Name matching
A destination member is filled from a source member with the same name. Case-sensitivity is configurable — case-sensitive by default, with opt-in case-insensitive matching.

### 4.2 Naming-style translation
Cross-style matching is first-class. Common built-in styles include:

- PascalCase, camelCase
- snake_case, UPPER_SNAKE_CASE
- kebab-case, UPPER-KEBAB-CASE
- "exact match only" (disable convention matching entirely)

Source and destination styles are independently configurable, so a snake_case JSON DTO can map to a PascalCase domain object without per-member work.

### 4.3 Flattening
A destination property whose name resolves to a path on the source is filled by walking that path. For example, a destination `CustomerName` is filled from `source.Customer.Name` with no configuration.

Flattening is recursive: any depth of nested object can be flattened so long as the concatenated names match.

### 4.4 Method matching with prefix recognition
If a destination property cannot be matched to a source property, the library can match it to a source **method**. By default, common prefixes (typically `Get`) are stripped: a destination `Total` will be filled by `source.GetTotal()`.

The recognized prefixes — and postfixes — are configurable, including clearing all defaults.

### 4.5 Substring replacement
A small dictionary of name substitutions runs before matching, useful for handling typos, transliteration, and migrations:

```
Replace("Ä", "A")
Replace("Airlina", "Airline")
```

### 4.6 Member visibility & filtering
Predicates control which members are even considered. Common controls:

- Map only public properties / fields
- Include or exclude internal, private, or static members
- Skip members marked `[Obsolete]` (configurable per side: source, target, both)

### 4.7 Controlled flattening
When automatic flattening cannot disambiguate a path, an explicit lift-the-child-into-the-parent declaration tells the engine which child object's mapped members participate. Order matters — first match wins.

---

## 5. Per-Member Override Hooks

When the convention is wrong, the library provides a layered set of hooks. Listed roughly from most-common to most-specialized.

### 5.1 Map a member from a custom expression
The everyday workhorse — fill one destination member from a lambda or expression on the source:

- An **expression** form, suitable for both in-memory mapping and queryable projection.
- A **delegate** form, in-memory only, but capable of arbitrary code.

### 5.2 Ignore a destination member
Skip the member entirely and remove it from validation reporting.

### 5.3 Map a constant value
Set a destination member to a fixed value or the result of a parameterless function — no source involvement.

### 5.4 Map the whole source object to a destination member
For nested DTOs that wrap or summarize the entire source, route the source itself into a single destination member, optionally through a user-supplied conversion function.

### 5.5 Map a constructor parameter
For destinations with non-default constructors (records, immutable types), a per-parameter override binds source members to constructor arguments. Constructor selection is itself configurable:

- Prefer parameterless constructors (default), or
- Prefer the most-parameters constructor, or
- Use the constructor explicitly marked as the mapping constructor, or
- Disable constructor mapping entirely.

### 5.6 Conditional mapping
Two distinct hooks:

- **Pre-condition** — evaluated before any value resolution. Use it when the resolution is expensive and would be wasted work if the condition fails.
- **Condition** — evaluated after resolution, before assignment. Use it when the cost is low and the predicate depends on the resolved value.

Pipeline order: **pre-condition → resolve value → condition → assign**.

### 5.7 Null substitution
Provide a fallback value when the source value (or anywhere along the source path) is null. The substitute is treated as a source-typed value and runs through the same conversion pipeline as a real source value would.

### 5.8 Format string and culture
For values that implement `IFormattable`, a format string and a format provider (typically a culture) can be applied at the member level — useful for currency, date, and number formatting without writing a custom converter.

### 5.9 Custom value resolvers (per-member, full context)
A class implementing a per-member resolver interface receives the source, destination, current destination value, and a context bag. It returns the destination value. Resolvers can be:

- referenced by type (and instantiated by the DI container), or
- supplied as a pre-built instance.

The returned value is itself routed through the rest of the mapping pipeline — resolvers feed into mapping, they don't bypass it.

A reusable variant accepts a redirected source member, so the same resolver class can serve many maps.

### 5.10 Per-member value converters
The middle ground between global type converters and full value resolvers: scoped to a single map, signature is just `member → member`. Useful for formatting, normalization, and unit conversion at one specific point.

In implementations that support queryable projection, value converters are typically **in-memory only** — they don't translate into LINQ expressions.

### 5.11 Suppressing diagnostics for a single member
A per-member flag silences specific analyzer/validation messages (e.g. "nullable source mapped to non-nullable target") for the cases where the developer has already verified the path is safe.

---

## 6. Cross-Cutting Hooks

### 6.1 Type converters (global)
Map between two unrelated types, applied **everywhere** the type pair appears. Once registered, a `string → DateTime` converter is used for every property of those types in every mapping.

Three configuration shapes are typical:
1. A lambda for trivial cases.
2. A pre-built converter instance.
3. A converter type, instantiated by the DI container.

### 6.2 Value transformers
A post-processing function applied to every value of a given type as it is assigned to a destination. Configurable at multiple scopes — global, module, type-map, member — so a global `string → string.Trim()` transformer can be added once and apply everywhere a string is mapped.

### 6.3 Before / after hooks
Run code at well-defined points around each mapping:

- **Before-map** — once, before any member is mapped.
- **After-map** — once, after every member is mapped.

These can be inline lambdas configured on the map, or implementations of a reusable mapping-action interface, in which case dependencies can be injected by the DI container. The reusable interface is the canonical pattern for hooks that need access to ambient services (HTTP context, current user, telemetry).

### 6.4 User-implemented partial methods (generator-based libraries)
In source-generator implementations, the universal escape hatch is "write the method body yourself in the same partial class." If the generator finds a method whose signature matches a needed type pair, it calls that method instead of generating one. This is how DI services, async work, and arbitrary logic enter the mapping pipeline.

### 6.5 Context bag
A per-call dictionary (variously called items, state, context) carries values from the caller into resolvers, converters, and hooks. Typical contents:

- A correlation/trace ID
- The current user
- Tenant or culture overrides
- Anything else the resolver needs but the source object doesn't carry

---

## 7. Type & Value Conversions

The library walks a fixed priority list of conversion strategies for each source/destination type pair. The first that matches is used. A representative ordering:

1. Direct assignment (identity / implicit reference)
2. Same-type collection mapping (delegated to element mapping)
3. Dictionary mapping
4. Span / Memory / Enumerable mapping
5. Implicit operators
6. Static `Parse` methods on the destination type
7. Constructors taking the source type
8. String ↔ enum
9. Enum ↔ enum (by value or by name)
10. Explicit operators
11. `ToString` (with `IFormattable` and format support)
12. Instance factory methods on the destination (`To{T}()`, `Create()`, `From()`, `CreateFrom()`)
13. Static factory methods (`Create`, `CreateFrom`, `From`)
14. `DateTime` ↔ `DateOnly` / `TimeOnly`
15. `IConvertible` fallback
16. User-supplied converter methods on the mapper class

Whole strategy families can be disabled via a flag enum, useful when you want to forbid (say) implicit `ToString` to make formatting choices explicit.

---

## 8. Object Construction

### 8.1 New instance vs update-in-place
Two method shapes are typically supported:

- **Construct-and-return**: `Map(source) → new Destination(...)`
- **Update-in-place**: `Map(source, existingDestination)` — scalar members are overwritten; nested objects are recursively updated where a matching update mapping exists, otherwise replaced.

Update-in-place is essential for ORM patterns where you load an entity, mutate it from a DTO, and save changes.

### 8.2 Constructor selection
Configurable via:

- An attribute marking one constructor as the mapping constructor.
- A "prefer parameterless" toggle (default for most libraries).
- A predicate ("only public", "only constructors with these parameter names").
- An explicit "disable constructor mapping" switch.

Parameter binding to source members is typically **case-insensitive**; an explicit override can rename a binding.

### 8.3 Init-only and required members
Init-only and `required` properties are populated through the object initializer the library emits or compiles — no runtime trick is involved. Records, primary constructors, and immutable types are first-class destinations.

---

## 9. Collections

### 9.1 Supported destination shapes
Configure a single element-pair mapping; the library handles every supported collection wrapping:

- `IEnumerable<T>`, `IEnumerable`
- `ICollection<T>`, `ICollection`
- `IList<T>`, `IList`
- `IReadOnlyCollection<T>`, `IReadOnlyList<T>`
- `List<T>`
- Arrays (`T[]`)
- `ImmutableArray<T>`, `ImmutableList<T>`, `ImmutableHashSet<T>`, immutable dictionaries, etc.
- `HashSet<T>`, other set types
- `Stack<T>`, `Queue<T>`
- Dictionaries (`Dictionary<K,V>`, `IDictionary<K,V>`, read-only variants)
- `Span<T>` and `Memory<T>` (where applicable)

### 9.2 Null-source handling
Two policies, each defensible:

- **Null becomes empty** (default in many libraries) — aligns with the Framework Design Guidelines stance that collection references should never be null.
- **Null stays null** — opt-in globally, per module, or per member (`AllowNull` / `DoNotAllowNull`).

### 9.3 Deep cloning
For same-type or compatible collections, the default is to reuse references. Opt-in deep cloning forces a fresh recursive copy of every element. Performance-relevant; off by default in most libraries for that reason.

When the destination is a `Stack<T>`, an additional setting controls whether the original push order is preserved or reversed by the copy.

### 9.4 Polymorphic elements
If source elements are of derived types, they are mapped to the corresponding derived destinations — but only if the child mappings are explicitly registered. The library cannot infer them.

### 9.5 Queryable projection of collections
Nested collections in a queryable projection are translated into the appropriate joins by the underlying provider. Filtered relationships are expressed in the projection lambda itself, not as a separate include step.

---

## 10. Inheritance & Polymorphism

### 10.1 Sharing configuration up the hierarchy
Two equivalent mechanisms:

- Declare on the **base** map which derived maps inherit from it.
- Declare on the **derived** map that it inherits from a named base.

A convenience option auto-includes all derived maps from a given base type, at the cost of an exhaustive scan.

### 10.2 Pointing one map at another
A "use this other map for this base type" shortcut redirects mapping without re-declaring members.

### 10.3 Runtime polymorphism on call
Calling `Map<DestinationBase>(actuallyDerivedSource)` selects the most-specific map registered for the runtime type. The same applies to collection elements.

### 10.4 Annotation-driven derived dispatch
A per-method annotation listing the recognized source-type → destination-type pairs generates a `switch` on runtime type:

```
[MapDerivedType<Dog,  DogDto>]
[MapDerivedType<Cat,  CatDto>]
public partial AnimalDto Map(Animal animal);
```

Rules: each source type appears at most once; multiple source types may map to one destination; an unrecognized runtime type at call time throws.

### 10.5 Mapping-priority order
When multiple rules could fill a destination member, the resolution order is:

1. Explicit per-member mapping
2. Inherited explicit mapping (from a base map)
3. Explicit ignore
4. Convention-based match

An ignore on the base overrides a convention-based match on the derived.

---

## 11. Reverse Mapping & Unflattening

### 11.1 Generating the inverse map
A single declaration produces the inverse: an entity-to-DTO map can be flipped to a DTO-to-entity map automatically. Where the original used member-access expressions, the reverse direction reconstructs the path:

```
CustomerName  →  Customer.Name      // reversed automatically
```

### 11.2 Path-level overrides on the reverse direction
Where the get-path and set-path differ, an explicit per-path override on the reverse map specifies the unflattening route. The same hook can be used to disable unflattening on a particular path.

### 11.3 Default validation policy
Reverse maps are typically created with validation disabled by default, because the inverse direction's expectations rarely match the forward direction's exactly.

---

## 12. Open Generics

A single map definition can apply to every closed generic instantiation at runtime:

```
Map(Source<>, Destination<>)
```

The same applies to converters and resolvers — single-arg and multi-arg generic forms are both supported, with closed source/destination types substituted as the converter's type parameters.

Open generic maps are typically **excluded from configuration validation**, since not every closed combination will be valid.

---

## 13. Dynamic, Dictionary, and ExpandoObject Mapping

Bidirectional mapping between strongly-typed classes and dynamic / dictionary-shaped sources works without explicit per-property configuration:

- `dynamic` and `ExpandoObject` round-trip to and from typed objects.
- `Dictionary<string, object>` keys align with property names.
- Dot notation in keys (`InnerFoo.Bar`) populates nested members.

This is typically used for JSON, MongoDB, and configuration-shaped inputs.

---

## 14. Enum Mapping

Enums get a dedicated configuration surface because there are several legitimate strategies and naming policies.

### 14.1 Strategy
- **By value** (default) — match by underlying integer value.
- **By value with defined check** — match by value but throw if the integer doesn't correspond to a defined member.
- **By name** — match by member name; case-sensitivity is independently configurable.

### 14.2 Name strategy for string ↔ enum conversions
A naming-style policy controls how an enum member becomes (or is parsed from) a string:

- Member name verbatim
- camelCase, PascalCase
- snake_case, UPPER_SNAKE_CASE
- kebab-case, UPPER-KEBAB-CASE
- Use the value of `[Description]` (`System.ComponentModel`)
- Use the value of `[EnumMember]` (`System.Runtime.Serialization`)

### 14.3 Per-value overrides
- Map one specific source value to one specific destination value.
- Ignore a specific source value (no destination assigned).
- Ignore a specific destination value (never produced).
- Define a fallback destination value for unmapped sources.

### 14.4 Strict enum validation
A "required enum mapping" mode enforces that every defined source/destination value participates in some mapping, either by convention or by explicit override.

---

## 15. Attribute-Based vs Fluent Configuration

Two declarative styles are commonly supported, often interchangeably.

### 15.1 Fluent / programmatic
Configuration is built through a chain of method calls inside a configuration callback or module class. Strengths: full expression support, conditional logic, dependency injection of configuration values.

### 15.2 Attribute-based
Configuration is declared as attributes on the destination type and its members:

- A class-level attribute declaring "this type is a mapping target of `T`" — equivalent to a fluent map declaration.
- Member-level attributes for ignore, source-member redirection, value substitution, format, custom converters, and per-property mapping options.

Limitation: attributes cannot accept lambda expressions, so true flattening, computed mappings, and member-access paths typically still require fluent code or user-implemented partial methods.

The two styles can usually be mixed in the same project — attributes for the simple cases, fluent for the rest.

---

## 16. Queryable Projection

### 16.1 What it does
Translate the configured mapping into a LINQ expression and let the underlying query provider (EF Core, NHibernate, etc.) turn it into SQL. The result: only the columns the destination actually needs land in the `SELECT` clause — no full entity load, no N+1 follow-up queries.

Two API shapes are common:
- An **extension method** on `IQueryable<TSource>` that takes a destination type parameter and returns `IQueryable<TDestination>`.
- A **declared method** on the mapper class with the signature `IQueryable<TDestination> Project(IQueryable<TSource>)`, with the body generated by the library.

### 16.2 Operator placement rule
Filter and sort on the source `IQueryable` first, then project as the **last** step. Operators applied after projection run on the destination shape and may not survive translation.

### 16.3 Supported projection features
Typically:
- Expression-form member overrides
- Expression-form type conversions
- Ignore
- Null substitution
- Value transformers (where they translate)
- Lifted/included child-member configurations
- Runtime polymorphism via inheritance includes

### 16.4 Common projection limitations
Anything that cannot be translated to provider expressions:

- Delegate-form member overrides (must be expression-form)
- Custom value resolvers and value converters
- Custom type converters (in some implementations)
- Conditional and pre-conditional hooks
- Before/after hooks
- Calculated properties on the source that the provider can't see
- Reference handling
- Deep cloning
- Some null-mismatch validation flags
- Path-level overrides for unflattening

The exact "supported / not supported" list is the most common source of "works in memory, fails on the database" bugs.

### 16.5 N+1 prevention
Nested collection projections cause the provider to emit appropriate joins automatically. Explicit eager-load directives are not needed and not used.

### 16.6 Type coercion in projection
Strings are typically auto-coerced via `ToString`. Other coercions require explicit expression-form converters.

### 16.7 Parameterization
A per-call parameter dictionary lets you bind values into the projection at runtime (e.g. the current user name, the current tenant id) without rebuilding the configuration:

```
query.ProjectTo<Dto>(config, new { currentUserName = user.Name })
```

### 16.8 Recursive models
Disabled by default to prevent infinite expansion. A recursion-depth setting opts in:

```
config.RecursiveQueriesMaxDepth = 3;
```

### 16.9 Explicit expansion
For wide DTOs, callers can opt in to which members are expanded; only the requested members are included in the projection, keeping the SQL narrow.

---

## 17. Expression Translation

A separable feature, sometimes packaged independently: rewriting a lambda expression written against the **destination** so it runs against the **source**.

```
db.OrderLines
  .UseAsDataSource()
  .For<OrderLineDto>()
  .Where(dto => dto.Name.StartsWith("A"));
// Translated to filter on the source entity equivalent of dto.Name.
```

Useful when an outer layer (UI, API) wants to express filtering, sorting, and includes in destination terms while the persistence layer must run them against domain types.

---

## 18. Reference Handling & Cycles

Cyclic graphs are off by default — they would otherwise stack-overflow. An opt-in mode tracks already-mapped instances and reuses the previously produced destination when the same source instance is encountered again, both breaking cycles and preserving shared references.

The handler is pluggable:
- A built-in "preserve references" handler is the default.
- A custom handler implementing the reference-handler interface can be passed as a method parameter.

In most implementations, reference handling is **incompatible with queryable projection** — the provider can't model identity tracking.

---

## 19. Dependency Injection

### 19.1 Registration
A standard DI extension method registers:
- The configuration as a singleton.
- The mapper interface (cheap; wraps the singleton config) as a transient or singleton, depending on implementation.
- Discovered modules / mapper classes from the supplied assemblies or marker types.

### 19.2 Resolving the mapper
The mapper is injected like any other service:

```csharp
public class EmployeesController(IMapper mapper) { ... }
```

For queryable projection, the mapper exposes its underlying configuration so it can be passed to the projection extension method without ambiguity.

### 19.3 DI inside the customization surface
Resolvers, converters, value converters, and mapping actions are typically **resolved from the DI container** when the library instantiates them, so they can take constructor dependencies.

In many implementations, **module/profile classes themselves cannot have constructor dependencies** — by design, since they are constructed during configuration. The standard workaround is to put the dependency-needing logic in a mapping-action or resolver class and reference its type from the module.

In source-generator implementations, the natural pattern is to make the mapper class itself an injected service with constructor dependencies, and to call those dependencies from user-implemented partial methods on the same class.

### 19.4 Static composition
For dependency-free helper mappers, a class-level annotation lets one mapper compose the methods of another static mapper without instance plumbing.

### 19.5 Alternative containers
Service-location hooks let the library construct resolvers, converters, and actions through any container (not just `Microsoft.Extensions.DependencyInjection`).

---

## 20. Validation

### 20.1 What validation checks
Across every configured map, every destination member that *could* be filled either:
- has a configured source (by convention, by override, by inheritance), or
- is explicitly ignored.

If neither holds, validation fails with a per-violation list.

### 20.2 When it runs
Two strategies, each rooted in the implementation choice from §2:

- **Runtime validation** — invoked from a unit test in CI. The test bootstraps the configuration and asserts validity. Catches drift before deployment so long as the test exists and runs.
- **Compile-time validation** — emitted as Roslyn analyzer diagnostics during build. Each unmapped member, each unmappable property, each null-mismatch is a build warning that can be promoted to an error. No test needed.

The compile-time variant is structurally stronger because it cannot be skipped or forgotten; the runtime variant is more flexible because the unit test can bootstrap arbitrary configuration shapes.

### 20.3 Coverage selection
Per-map controls choose which side of the mapping is enforced:

- Validate against destination members (default — every destination member must be explained).
- Validate against source members (every source member must be consumed).
- Skip validation entirely (useful for the auto-generated reverse direction).

### 20.4 Required-mapping policy
A coverage strictness setting (`Both` / `Source` / `Target` / `None`) declares the expected fullness of a mapping at the configuration layer, separate from the validation pass.

### 20.5 Promoting analyzer warnings to errors
For build-time diagnostics, severity is tunable through the standard Roslyn mechanisms:

- `.editorconfig` per-rule severity
- `[SuppressMessage]` in code
- `#pragma warning disable` for one site
- `<WarningsAsErrors>` in the project file to fail CI on drift

### 20.6 Suppressing diagnostics
Per-member flags silence specific warnings (e.g. nullable source → non-nullable target) for cases the developer has audited and accepts.

---

## 21. Compile-Time Constraints (Source-Generator Implementations)

Some constraints are inherent to generating static code at build time and are worth flagging because the workarounds are different from runtime libraries.

### 21.1 Constraints
- The generator cannot emit `await`. Generated mappings are synchronous.
- The generator cannot look up services from `IServiceProvider` inside generated bodies.
- Configuration cannot branch on values that are unknown at compile time.
- IDE diagnostic feedback is build-bound; it does not always update on every keystroke.

### 21.2 Workarounds
- **User-implemented partial methods** for any logic that needs DI, async, or runtime decisions.
- **Materialize-then-map** — perform async work outside the mapping (`await` to a list, then map synchronously).
- **Instance mappers with constructor-injected dependencies** — keep the generated mapping pure, route side effects through user methods that consume the injected services.

### 21.3 Build environment requirements
Source-generator-based implementations typically require a recent SDK (current LTS or later) and a recent Roslyn version (4.0+). Older build agents may silently produce no output; pinning the SDK in `global.json` is a practical safeguard.

---

## 22. Lifecycle and Performance Characteristics

### 22.1 Configuration lifetime
Build the configuration **once per process**. Inject the resulting mapper everywhere. Never construct configuration per-request — it is the most expensive thing the library does, and the result is fully shareable and thread-safe.

### 22.2 Compilation timing
For runtime-compiled implementations, an explicit "compile now" call at startup is the recommended pattern. It pays the cost in a known place rather than against the first user request.

For source-generator implementations, the equivalent cost is paid at build time and is amortized across every run of the assembly.

### 22.3 Per-call cost
After warm-up (or build), per-call cost approaches that of hand-written assignment code. Both implementation strategies generate property-by-property C# (compiled or emitted), with no reflection on the hot path.

### 22.4 Memory characteristics
Allocations during a mapping are dominated by the destination object and its nested objects/collections. Custom resolvers and value converters add no per-call allocation if they are stateless. Context bags, when used, allocate a dictionary per call.

---

## 23. Production Patterns & Pitfalls

### 23.1 Validate the configuration in CI
Whichever variant your library offers — a runtime test that calls "assert configuration is valid", or a build-time analyzer set to error severity — wire it in. It is the single highest-leverage safety net the library offers and the difference between "renamed a property and shipped a wrong DTO" and "renamed a property and the build broke".

### 23.2 Prefer queryable projection for read paths
A pattern of `Where … ToList … Map<List<Dto>>` loads full entities into memory, then maps them down. The same code with queryable projection emits a SQL `SELECT` of just the destination columns. The performance gap is often an order of magnitude.

### 23.3 Don't hide business logic in the mapper
If a "mapping" applies discounts, computes prices, or runs domain rules, that's domain code that doesn't belong in a mapper module. Resolvers, converters, and partial methods should be thin format/coercion adapters. Move the logic out and let the mapper consume the result.

### 23.4 Keep modules dependency-free where required
Where the library forbids constructor dependencies on configuration modules, push the dependency-needing logic into a mapping-action / resolver / partial method that can be DI-resolved. Don't fight the constraint with service-locator anti-patterns.

### 23.5 Inspect generated code when behavior is surprising
Both implementation strategies expose what they actually produce. Reading the generated code (via the expression-tree dump or the persisted `.g.cs` files) is faster than reasoning backwards from a wrong field assignment.

### 23.6 Watch the in-memory / queryable feature gap
Anything in the customization surface that can't be translated to a LINQ expression — delegate-form overrides, value converters, custom resolvers, custom type converters, conditions, before/after hooks — works in memory but fails or is silently dropped in a queryable projection. Cross-check against §16.4 before adopting an override on a path that is also projected.

### 23.7 Pre-compile or pre-build
Pay the configuration / generation cost in a known place: at startup for runtime-compiled libraries, in the build for generator-based libraries.

### 23.8 Be deliberate about flattening conventions
Convention-driven flattening is excellent when it works and confusing when it doesn't. For non-trivial graphs, an explicit lift-the-child or explicit member-path declaration is more predictable than waiting for the convention to discover the right path.

### 23.9 Treat unmapped-member diagnostics as errors
Whether they come from a runtime test or an analyzer, a missing destination member is almost always a bug. Default-on enforcement is cheap; default-off enforcement defers the cost to production debugging.

### 23.10 Don't over-customize
Every per-member override is a hint that the convention failed for that member. If you find yourself adding overrides by the dozen, restructure the destination type's names to match — the convention should be doing the heavy lifting, with overrides reserved for the cases where source and destination genuinely diverge.

---

## 24. Summary by Layer

| Layer | What it provides |
|---|---|
| **Mental model** | A description of how to copy values from one type to another, expressed declaratively, materialized into fast code by the library. |
| **Convention engine** | Recursive name matching with prefix/postfix recognition, naming-style translation, and substring substitution — producing flattening, method matching, and cross-style mapping for free. |
| **Customization surface** | Layered hooks: per-member (custom expression / resolver / converter / condition / format), per-type (type converter / value transformer), per-call (before/after, context items / state). |
| **Read-side optimization** | Queryable projection that rewrites the mapping as a LINQ expression so the database returns destination-shaped rows directly. |
| **Validation** | Either a runtime "assert configuration is valid" entry point or a compile-time analyzer rule set — provably exhaustive coverage of the mapping surface. |
| **Construction** | Constructor mapping for records / immutables / required / init-only members; new-instance and update-in-place method shapes. |
| **Polymorphism** | Inheritance-aware configuration plus runtime type dispatch (and an annotation-driven switch form for source-generator libraries). |
| **Enum surface** | Dedicated by-value / by-name strategies, naming-style policies for string conversions, per-value overrides and ignores, fallback destinations. |
| **DI integration** | DI-resolved resolvers, converters, and actions; the mapper itself injectable; a documented pattern for the (common) "modules can't take dependencies" constraint. |
| **Reference safety** | Optional cycle-safe mapping with pluggable handlers; off by default for cost reasons. |
| **Inspection** | A way to see exactly what code the library produced — either by walking the runtime expression tree or by reading the persisted generated source files. |
