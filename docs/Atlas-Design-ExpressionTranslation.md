# Atlas v2 — Expression Translation (UseAsDataSource)

**Status:** Approved design (2026-05-07).
**Implementation target:** v2 feature group #13 (post-MVP, post-AttributeConfig). The thirteenth and final v2 deferred feature.
**Predecessor designs:** `docs/Atlas-Design-ProjectTo.md` (forward direction `entity → DTO at SQL level`; this feature is the inverse), `docs/Atlas-Design-AttributeConfig.md` (translate-to-fluent architecture pattern; this feature uses translate-to-source-expression), `docs/Atlas-Design-ReferenceHandling.md` (per-`MapperConfiguration` cache via `ConditionalWeakTable` + `RefEqComparer` pattern), `docs/Atlas-Design.md` (v1 baseline — `MapperConfiguration`, `MapperRegistry`, `PropertyMap.SourcePath`, `PropertyMap.CustomExpression`).

This document specifies Atlas's thirteenth post-MVP feature: **expression translation**. Wrap an `IQueryable<TSource>` and write filtering, sorting, and paging in destination-DTO terms — Atlas translates the destination-typed lambdas back to source-typed expressions before they hit the LINQ provider. Mirrors AutoMapper's `UseAsDataSource(cfg).For<TDto>()` UX. Implementation lives in the existing `Atlas.Projections` package alongside `ProjectTo`.

---

## Architecture summary (the bird's-eye view)

Atlas v2 #13 adds **expression translation** as a parallel front-end to the existing `Atlas.Projections.ProjectTo`. Two layers: a translation engine (`Atlas.Projections.Internal.ExpressionTranslator`) that walks `Expression<Func<TDest, TResult>>` and produces `Expression<Func<TSrc, TResult>>` by substituting destination-typed member accesses with the source expressions Atlas's typemaps already record (`PropertyMap.SourcePath`, `PropertyMap.CustomExpression`), and a thin wrapper (`Atlas.Projections.UseAsDataSourceExtensions` + an internal `UseAsDataSourceQueryable<TSrc, TDst>`) that intercepts predicate/ordering/paging operators and applies translated lambdas to the underlying `IQueryable<TSrc>`. Enumeration delegates to the existing `ProjectTo<TDest>` for the final source→destination shape. Cached per `(TypePair, lambda-reference-identity)` using PR #11's `RefEqComparer` pattern.

**Operator surface (v1):** `Where`, `OrderBy`, `OrderByDescending`, `ThenBy`, `ThenByDescending`, `Skip`, `Take`, plus terminal predicate operators (`Any`, `All`, `Count(predicate)`, `First[OrDefault](predicate)`, `Single[OrDefault](predicate)`, `Last[OrDefault](predicate)`).

**Out of scope for v1** (deferred): `Select`, `SelectMany`, `GroupBy`, `Include`/`MapExpressionAsInclude`, `Join`, raw `IQueryable.Provider.Execute` interception, async LINQ operators, inner-lambda translation on collection-typed destination members.

---

## 1. Goals & Non-Goals

### 1.1 Goals

1. **Destination-side LINQ ergonomics.** A user with an `IQueryable<Order>` can write filters, sorts, and paging in `OrderDto` terms — the wrapper translates back to entity terms before hitting EF Core / NHibernate / any other LINQ provider. Matches AutoMapper's `UseAsDataSource(cfg).For<TDto>()` UX.

2. **Reuse Atlas's existing typemap data.** The translator reads `PropertyMap.SourcePath` (convention or `[SourceMember]` resolved) and `PropertyMap.CustomExpression` (`MapFrom(s => Expression body)`). No new metadata on `PropertyMap` or `TypeMap`. The same data that makes `ProjectTo` work makes UseAsDataSource work — in the inverse direction.

3. **Translate-and-cache architecture.** Each `Where(d => predicate)` call eagerly translates and applies to the underlying source query. Translation results cache per `(TypePair, lambda-reference-identity)` so re-passing a `static readonly Expression<...>` doesn't re-translate. Mirrors the existing `ProjectionPlanCache` pattern.

4. **Strict policy on untranslatable members.** Predicates against `[Ignore]`'d, unmapped, or constant-mapped destination members throw `AtlasProjectionException` at the operator call site with a clear "destination member 'X' on TDto cannot be translated" message. Matches Atlas's established fail-fast posture (PR #5/#10/#11/#12).

5. **Single insertion point.** All new code lives in `Atlas.Projections`. Zero changes to `Atlas` core, zero changes to `Atlas.Extensions.DependencyInjection`, zero changes to existing `ProjectTo` machinery. The wrapper's enumeration path calls `ProjectTo<TDest>` for the final select.

6. **Loud failure on misconfiguration.** Cycle-safe TypeMaps (`PreserveReferences = true`), hook-bearing TypeMaps, and dynamic-shape TypeMaps are all rejected at translate time with the same dual-gate `ProjectionCompatibility` checks that already gate `ProjectTo`. No special handling needed in the translator — the engine reuses the existing rejection layer.

7. **Direct-use helper for power users.** `cfg.Translate<TSource, TDestination, TResult>(Expression<Func<TDest, TResult>>) → Expression<Func<TSrc, TResult>>` is exposed as a standalone extension method on `MapperConfiguration`. The wrapper uses this internally; users can use it directly when they need translated lambdas as values (e.g., for unit tests or composing with custom LINQ providers).

### 1.2 Non-Goals (deferred to v2 of this feature or to v3)

- **Lambda-body translation auditing.** When a `MapFrom(s => SomeFn(s))` `CustomExpression` is inlined, Atlas does NOT pre-inspect whether `SomeFn` is LINQ-provider-translatable. The provider's standard "expression cannot be translated" error fires at query-execution time. Same posture as ProjectTo (per `IMappingExpression.AddTransform<T>` xmldoc).

- **`Select`, `SelectMany`, `GroupBy` operators.** Excluded per Q2; deferred to a future doc. Users wanting projection on top of UseAsDataSource use `query.UseAsDataSource(cfg).For<TDto>().Where(...).OrderBy(...).ToList()` (which materializes via `ProjectTo`) and then LINQ-to-objects on the materialized list, OR `AsQueryable()` to drop down to `IQueryable<TDest>` and use full LINQ.

- **Includes / `MapExpressionAsInclude`.** EF-specific concept with no Atlas equivalent. Out of scope.

- **Translation of expression nodes other than member access.** `dto.Customer.Name.StartsWith("A")` translates the member-access spine; `StartsWith("A")` passes through unchanged. The user's responsibility (and the LINQ provider's) to ensure the non-translated parts are SQL-translatable.

- **Bidirectional translation.** v1 translates DTO-typed expressions to entity-typed only. The reverse direction (entity-typed → DTO-typed) is not a documented use case. If user demand surfaces, defer to a future doc.

- **Plan-precompiled translations.** No `cfg.PrecompileTranslations()` or "warm the translation cache at startup" feature. Translations are lazy on first call (cached on subsequent calls). Mirrors `ProjectionPlanCache` behavior.

- **Custom `IQueryProvider`.** The wrapper does NOT implement `IQueryProvider`. It exposes a finite operator surface on top of the underlying `IQueryable<TSource>.Provider`. Users who need full custom-LINQ-provider semantics drop down to the explicit `cfg.Translate<...>` helper.

- **Async LINQ operators (`ToListAsync`, `FirstAsync`, etc.).** Not part of the wrapper surface. Users who need them call `AsQueryable()` to materialize as `IQueryable<TDest>` and use the LINQ-Async provider extensions on the result.

- **Diagnostic translation introspection.** No "show me the translated expression" public API in v1. The translation result is internal; users who want to debug can use a debug build's reflection or the in-memory LINQ provider for inspection.

- **Inner-lambda translation on collection-typed destination members.** Predicates with `.Any(...)`, `.All(...)`, `.Where(...)` calls on collection-typed destination members (`d => d.Lines.Any(l => l.Total > 100)`) are detected and rejected with a clear error in v1; full translation is v2 work.

- **Derived-type dispatch via `is`-checks.** A wrapper bound to a base typemap cannot translate predicates against derived-only properties. Users explicitly downcast: `query.OfType<DerivedSrc>().UseAsDataSource(cfg).For<DerivedDto>()`.

---

## 2. Architecture Overview

### 2.1 Translation engine + thin wrapper, all in Atlas.Projections

```
mapper.Map<>()                       (existing, in-memory mapping)
                                            │
                                            │ unaffected
                                            ▼
                                     Atlas core unchanged
                                            ▲
                                            │
                                            │ public API
                                            ▼
query.ProjectTo<TDest>(cfg)         (existing, source → dest at SQL level)
                                            │
                                            ▼
query.UseAsDataSource(cfg)           ── NEW
   .For<TDest>()                     ── NEW (returns wrapper)
   .Where(d => ...)                  ── NEW (translates + applies)
   .OrderBy(d => ...)                ── NEW
   .ToList()                         ── enumeration delegates to ProjectTo
        │
        ▼
   ExpressionTranslator              ── NEW (the engine: walks expression tree,
                                              substitutes member access)
        │
        ▼
   PropertyMap.SourcePath / CustomExpression   (existing, unchanged)
```

The new code lives entirely in `Atlas.Projections`. The wrapper's enumeration path calls the existing `ProjectionExtensions.ProjectTo<TDestination>` — so all the projection-side codegen (member-init, null-safe paths, nested typemap recursion, value transformer composition) is reused without modification.

### 2.2 Translation routing diagram

```
user code:  query.UseAsDataSource(cfg).For<OrderDto>().Where(d => d.CustomerName == "A")
                  │                          │             │
                  ▼                          ▼             ▼
       UseAsDataSourceExtensions  IUseAsDataSource    UseAsDataSourceQueryable<Order, OrderDto>
       creates intermediate       returns wrapper     stores _underlying = IQueryable<Order>
                                  (binds TDest)
                                                            │
                                                            ▼
                                                       Where call:
                                                            │
                                                            ▼
                                                       TranslationPlanCache.GetOrTranslate
                                                            │
                                                            ▼ (cache miss)
                                                       ExpressionTranslator.Translate
                                                            │
                                                            ▼
                                                       walk: d.CustomerName
                                                       lookup PropertyMap("CustomerName")
                                                       SourcePath = [Customer, Name]
                                                       substitute: src.Customer.Name
                                                            │
                                                            ▼
                                                       Expression<Func<Order, bool>>:
                                                       src => src.Customer.Name == "A"
                                                            │
                                                            ▼
                                                       _underlying.Where(translated)
                                                            │
                                                            ▼
                                                       new UseAsDataSourceQueryable<,>
                                                            │
   (eventually)  .ToList() / foreach                        │
                                                            ▼
                                                       AsQueryable() → ProjectTo<OrderDto>
                                                            │
                                                            ▼
                                                       SQL: SELECT projection FROM Orders WHERE Customer.Name = 'A'
```

### 2.3 What's gained, what's paid

**Gained** — every existing v2 feature works on the destination side of UseAsDataSource without modification:

| Feature | How it composes |
|---|---|
| Convention/flattening | `PropertyMap.SourcePath` walked; multi-segment paths work for free. |
| `MapFrom(Expression)` | `PropertyMap.CustomExpression` inlined via existing `ParameterReplacer`. |
| `[SourceMember]` (#12) | Resolves to `SourcePath`; identical to convention case. |
| `NullSubstitute` (#8) | `Expression.Coalesce` in the binding inlines correctly; SQL `COALESCE`. |
| `Condition`/`PreCondition` (#7) | Inlined into binding expression. |
| Value transformers (#6) | Already inlined in `ProjectionPlanBuilder`; same data path. |
| Open generics (#9) | Lazy materialization makes the closed pair available; translator looks it up. |
| Attribute config (#12) | `[AutoMap]` typemap is a normal `TypeMap`; no special handling. |
| `ProjectTo` (#1) | Wrapper enumeration delegates to it. |
| Atlas DI extension | No DI changes; explicit `MapperConfiguration` parameter. |

**Paid** — features that explicitly REJECT translation (via the existing `ProjectionCompatibility` dual-gate):

| Feature | Reject mechanism |
|---|---|
| Hooks (#5) | `IsTypeMapProjectable` returns false when `BeforeMap`/`AfterMap` are set. |
| `PreserveReferences` (#11) | Same gate; inheritance from existing dual-gate. |
| Dynamic mapping (#10) | Same gate. |
| `ForPath` (#4) | `IsBindingProjectable` returns false for `DestinationPath.Count > 1`. |

### 2.4 Why translate-to-source-expression rather than a custom `IQueryProvider`

The wrapper could implement `IQueryProvider` and lazily build a translated expression tree at enumeration. **The design rejects this approach.** Reasoning, drawn from ProjectTo's own architecture and the brainstorming:

- **Eager translation gives clean error sites.** Per Q5 → C: if a user writes `.Where(d => d.IgnoredMember > 0)`, the throw fires at the `.Where(...)` call line, not buried inside an enumeration triggered by `.ToList()`. The stack trace points to the offending operator. Lazy translation hides this.
- **No need for a full `IQueryable<TDest>` shim.** The wrapper exposes a finite operator surface (per Q2 → A); implementing `IQueryProvider` would force support for the full `IQueryable` LINQ API, including operators we explicitly don't translate (`Select`, `GroupBy`, `Join`, etc.). Better to fail at compile time (operator not exposed) than at runtime (operator throws or produces incorrect SQL).
- **Caching is straightforward.** Eager translation per call paired with `ConcurrentDictionary` cache covers the common case (`static readonly Expression<>`) without the complexity of analyzing the lazy-built tree to find cacheable subtrees.
- **Composition with the underlying `IQueryable<TSource>` is direct.** Each operator translates the destination-typed lambda, applies the underlying source-typed operator (`_underlying.Where(translated)`), wraps the result. No expression-tree post-processing.

The architectural consequence: the implementer must NOT introduce `IQueryProvider`, `IQueryable<TDest>` shim, or lazy translation. Every operator call eagerly translates and produces a fresh wrapper instance.

---

## 3. Public API Surface

Three new public types in `Atlas.Projections`. No changes to `Atlas` core or `Atlas.Extensions.DependencyInjection`.

```csharp
namespace Atlas.Projections;

/// <summary>
/// Entry point for destination-typed-lambda LINQ operators against a source-typed
/// <see cref="IQueryable"/>. Translates each operator's destination-typed expression
/// back to source-typed via the configured Atlas typemaps, then applies the underlying
/// LINQ operator to the source query.
/// </summary>
public static class UseAsDataSourceExtensions
{
    /// <summary>
    /// Wraps <paramref name="source"/> in an Atlas-aware data-source injection point.
    /// Call <see cref="IUseAsDataSource{TSource}.For{TDestination}"/> next to bind the
    /// destination type and start chaining destination-typed LINQ operators.
    /// </summary>
    public static IUseAsDataSource<TSource> UseAsDataSource<TSource>(
        this IQueryable<TSource> source,
        MapperConfiguration configuration);
}

/// <summary>
/// Intermediate handle returned by <c>UseAsDataSource</c>. Single method <c>For&lt;TDest&gt;</c>
/// binds the destination type and returns a wrapper presenting destination-typed LINQ operators.
/// </summary>
public interface IUseAsDataSource<TSource>
{
    /// <summary>
    /// Binds the destination type and returns the destination-typed wrapper.
    /// The (TSource, TDestination) pair must be registered with the
    /// <see cref="MapperConfiguration"/> passed to <c>UseAsDataSource</c>; otherwise this
    /// throws <see cref="AtlasProjectionException"/> at call time.
    /// </summary>
    IUseAsDataSourceQueryable<TSource, TDestination> For<TDestination>();
}

/// <summary>
/// Destination-typed LINQ-operator surface. Each operator accepts a
/// <c>Func&lt;TDestination, ...&gt;</c>-shaped lambda; the wrapper translates the
/// destination-typed expression to a source-typed expression and applies the underlying
/// LINQ operator to the wrapped <c>IQueryable&lt;TSource&gt;</c>.
/// </summary>
/// <remarks>
/// Operator scope per design §1 / §2 (v1):
/// <list type="bullet">
///   <item>Filtering: <c>Where</c></item>
///   <item>Ordering: <c>OrderBy</c>, <c>OrderByDescending</c>, <c>ThenBy</c>, <c>ThenByDescending</c></item>
///   <item>Paging: <c>Skip</c>, <c>Take</c></item>
///   <item>Terminal predicate: <c>Any</c>, <c>All</c>, <c>Count(predicate)</c>,
///         <c>First[OrDefault](predicate)</c>, <c>Single[OrDefault](predicate)</c>,
///         <c>Last[OrDefault](predicate)</c></item>
/// </list>
/// <c>Select</c>, <c>SelectMany</c>, <c>GroupBy</c>, <c>Include</c>, <c>Join</c>, async
/// operators (<c>ToListAsync</c> etc.) are deferred. Use <c>AsQueryable()</c> to drop down
/// to a translated <c>IQueryable&lt;TDestination&gt;</c> for unsupported operators.
///
/// Enumeration via <c>foreach</c>/<c>ToList</c>/<c>ToArray</c>/<c>AsEnumerable</c> applies
/// the implicit projection via <see cref="ProjectionExtensions.ProjectTo{TDestination}"/>
/// and yields destination instances.
/// </remarks>
public interface IUseAsDataSourceQueryable<TSource, TDestination> : IEnumerable<TDestination>
{
    // ---- Filtering ----
    IUseAsDataSourceQueryable<TSource, TDestination> Where(
        Expression<Func<TDestination, bool>> predicate);

    // ---- Ordering ----
    IUseAsDataSourceOrdered<TSource, TDestination> OrderBy<TKey>(
        Expression<Func<TDestination, TKey>> keySelector);
    IUseAsDataSourceOrdered<TSource, TDestination> OrderByDescending<TKey>(
        Expression<Func<TDestination, TKey>> keySelector);

    // ---- Paging ----
    IUseAsDataSourceQueryable<TSource, TDestination> Skip(int count);
    IUseAsDataSourceQueryable<TSource, TDestination> Take(int count);

    // ---- Terminal predicate operators ----
    bool Any();
    bool Any(Expression<Func<TDestination, bool>> predicate);
    bool All(Expression<Func<TDestination, bool>> predicate);
    int Count();
    int Count(Expression<Func<TDestination, bool>> predicate);
    long LongCount();
    long LongCount(Expression<Func<TDestination, bool>> predicate);
    TDestination First();
    TDestination First(Expression<Func<TDestination, bool>> predicate);
    TDestination? FirstOrDefault();
    TDestination? FirstOrDefault(Expression<Func<TDestination, bool>> predicate);
    TDestination Single();
    TDestination Single(Expression<Func<TDestination, bool>> predicate);
    TDestination? SingleOrDefault();
    TDestination? SingleOrDefault(Expression<Func<TDestination, bool>> predicate);
    TDestination Last();
    TDestination Last(Expression<Func<TDestination, bool>> predicate);
    TDestination? LastOrDefault();
    TDestination? LastOrDefault(Expression<Func<TDestination, bool>> predicate);

    // ---- Escape hatch ----
    /// <summary>
    /// Materializes the wrapper into a translated <c>IQueryable&lt;TDestination&gt;</c>
    /// (the underlying source query with all chained operators applied, plus the implicit
    /// <c>ProjectTo&lt;TDestination&gt;</c> select). Use for operators not exposed on the
    /// wrapper (e.g., <c>Select</c>, <c>GroupBy</c>, <c>ToListAsync</c>) and for explicit
    /// IQueryable composition.
    /// </summary>
    IQueryable<TDestination> AsQueryable();
}

/// <summary>
/// Ordered wrapper produced by <c>OrderBy</c>/<c>OrderByDescending</c>; adds <c>ThenBy</c>
/// chaining. Inherits the full destination-typed surface so non-ordered operators continue
/// to work after an ordering is applied.
/// </summary>
public interface IUseAsDataSourceOrdered<TSource, TDestination>
    : IUseAsDataSourceQueryable<TSource, TDestination>
{
    IUseAsDataSourceOrdered<TSource, TDestination> ThenBy<TKey>(
        Expression<Func<TDestination, TKey>> keySelector);
    IUseAsDataSourceOrdered<TSource, TDestination> ThenByDescending<TKey>(
        Expression<Func<TDestination, TKey>> keySelector);
}
```

**Direct-use helper** on `MapperConfiguration` for users who want the engine without the wrapper:

```csharp
namespace Atlas.Projections;

public static class MapperConfigurationExpressionTranslationExtensions
{
    /// <summary>
    /// Translates a destination-typed expression into a source-typed expression by
    /// substituting destination-member accesses with the source expressions Atlas's
    /// typemaps record (<c>PropertyMap.SourcePath</c> or <c>PropertyMap.CustomExpression</c>).
    /// </summary>
    /// <typeparam name="TSource">The source type. Must be paired with TDestination via a registered TypeMap.</typeparam>
    /// <typeparam name="TDestination">The destination type whose lambda the user authored.</typeparam>
    /// <typeparam name="TResult">The lambda's return type — typically <c>bool</c> for predicates.</typeparam>
    /// <exception cref="AtlasProjectionException">
    /// Thrown when the lambda references an unmapped, ignored, or constant-mapped destination
    /// member, OR when the (TSource, TDestination) pair is not registered, OR when the typemap
    /// has hooks/PreserveReferences/dynamic-shape attributes that reject projection.
    /// </exception>
    public static Expression<Func<TSource, TResult>> Translate<TSource, TDestination, TResult>(
        this MapperConfiguration configuration,
        Expression<Func<TDestination, TResult>> destinationExpression);
}
```

**No new method on `IMapper`.** Translation is a configuration-level operation, not a per-call mapper operation.

**No new exception type.** `AtlasProjectionException` (already public, used by `ProjectionExtensions.ProjectTo`) is reused. Translation-specific error messages are formatted with `"UseAsDataSource translation: ..."` prefix for clarity.

---

## 4. Internal Architecture

Two new internal types, one new internal cache, no changes to existing code.

```
┌─ Atlas.Projections package ──────────────────────────────────────────────┐
│                                                                          │
│  Public:                                                                 │
│  ├── ProjectionExtensions          (existing)                            │
│  ├── UseAsDataSourceExtensions     (NEW — single method UseAsDataSource) │
│  ├── IUseAsDataSource<TSource>     (NEW — intermediate handle)           │
│  ├── IUseAsDataSourceQueryable<,>  (NEW — destination-typed surface)     │
│  ├── IUseAsDataSourceOrdered<,>    (NEW — ordered chaining surface)      │
│  ├── MapperConfigurationExpressionTranslationExtensions                  │
│  │                                  (NEW — direct-use Translate helper)  │
│  └── AtlasProjectionException      (existing, reused)                    │
│                                                                          │
│  Internal:                                                               │
│  ├── ParameterReplacer             (existing — reused for member-substitution)│
│  ├── ProjectionPlanBuilder         (existing — called by enumeration path)   │
│  ├── ProjectionPlanCache           (existing)                            │
│  ├── ProjectionCompatibility       (existing — gates translation too)    │
│  ├── ProjectionValidator           (existing — gates translation too)    │
│  │                                                                       │
│  ├── ExpressionTranslator          (NEW — engine: walks destination       │
│  │                                  expression, substitutes member access)│
│  │                                                                       │
│  ├── UseAsDataSourceQueryable<,>   (NEW — sealed class implementing      │
│  │                                  IUseAsDataSourceQueryable + Ordered)  │
│  │                                                                       │
│  └── TranslationPlanCache          (NEW — per-MapperConfiguration cache  │
│                                     keyed by (TypePair, lambda-ref-id))   │
└──────────────────────────────────────────────────────────────────────────┘
```

### 4.1 New: `Atlas.Projections.Internal.ExpressionTranslator`

The engine. `internal static class ExpressionTranslator` exposing:

```csharp
internal static class ExpressionTranslator
{
    /// <summary>
    /// Walks <paramref name="destinationLambda"/>, substituting destination-member
    /// accesses with source-typed expressions per the typemap chain rooted at
    /// <c>(srcType, dstType)</c>. Returns the rewritten lambda typed
    /// <c>Expression&lt;Func&lt;TSource, TResult&gt;&gt;</c>.
    /// </summary>
    public static LambdaExpression Translate(
        MapperRegistry registry,
        TypePair root,
        LambdaExpression destinationLambda);
}
```

Internal behavior (algorithm details in §5):
- Validates that `(root.Source, root.Destination)` is a registered TypeMap.
- Calls existing `ProjectionCompatibility.IsTypeMapProjectable` to reject hooks / PreserveReferences / dynamic.
- Constructs a `Visitor` instance (private nested `MemberAccessRewriter : ExpressionVisitor`) that:
  - Tracks the "current source-typed expression" as it descends (initially: source parameter).
  - On `MemberExpression` nodes whose root parameter is the destination lambda's parameter, walks down member-by-member, looking up the appropriate `PropertyMap` per type level and substituting via `SourcePath` / `CustomExpression` / rejection.
  - Other node kinds use base `ExpressionVisitor` behavior (visits children).
- Replaces the lambda's destination-typed parameter with a fresh source-typed parameter; the rewritten body becomes the new lambda's body.
- Returns the new `LambdaExpression` typed `Func<TSource, TResult>`.

### 4.2 New: `Atlas.Projections.Internal.UseAsDataSourceQueryable<TSource, TDestination>`

The wrapper. `internal sealed class UseAsDataSourceQueryable<TSource, TDestination> : IUseAsDataSourceOrdered<TSource, TDestination>` (single class implements both `IUseAsDataSourceQueryable` and `IUseAsDataSourceOrdered`).

Internal state:
- `IQueryable<TSource> _underlying` — the current source-typed query (mutated through operator chaining).
- `MapperConfiguration _configuration` — passed to the engine and to `ProjectTo` at enumeration.
- `IOrderedQueryable<TSource>?` _ordered — holds the underlying as `IOrderedQueryable<TSource>` after `OrderBy*` so `ThenBy*` can call the ordered overload.

Each operator method follows the same pattern:

```csharp
public IUseAsDataSourceQueryable<TSource, TDestination> Where(
    Expression<Func<TDestination, bool>> predicate)
{
    var translated = (Expression<Func<TSource, bool>>)
        TranslationPlanCache
            .For(_configuration)
            .GetOrTranslate(
                new TypePair(typeof(TSource), typeof(TDestination)),
                predicate,
                () => ExpressionTranslator.Translate(
                    _configuration.Internal_Registry,
                    new TypePair(typeof(TSource), typeof(TDestination)),
                    predicate));

    return new UseAsDataSourceQueryable<TSource, TDestination>(
        _underlying.Where(translated),
        _configuration);
}
```

Each immutable-style method returns a fresh wrapper instance carrying the new `_underlying`. No state mutation; safe to share an instance across threads.

`OrderBy*` returns the wrapper cast as `IUseAsDataSourceOrdered<,>` and stores the `IOrderedQueryable<TSource>` in `_ordered` for `ThenBy*`. `ThenBy*` uses `_ordered.ThenBy(translated)`.

`AsQueryable()` returns `_underlying.ProjectTo<TDestination>(_configuration)` — chains the underlying source query through the existing ProjectTo machinery to materialize as `IQueryable<TDestination>`.

`IEnumerable<TDestination>.GetEnumerator()` calls `AsQueryable().GetEnumerator()` — enumeration triggers ProjectTo automatically.

Terminal predicate operators (`Any`, `All`, `Count(predicate)`, etc.) translate the predicate via the cache then delegate to the underlying source-typed `IQueryable<TSource>` operator. `First()` / `FirstOrDefault()` (the no-predicate overloads) materialize via `AsQueryable().First[OrDefault]()`.

### 4.3 New: `Atlas.Projections.Internal.TranslationPlanCache`

The cache. Mirrors the shape of the existing `ProjectionPlanCache` but with a different key.

```csharp
internal static class TranslationPlanCacheRegistry
{
    private static readonly ConditionalWeakTable<MapperConfiguration, TranslationPlanCache> _caches = new();

    public static TranslationPlanCache For(MapperConfiguration cfg) =>
        _caches.GetValue(cfg, _ => new TranslationPlanCache());
}

internal sealed class TranslationPlanCache
{
    private readonly ConcurrentDictionary<CacheKey, LambdaExpression> _cache = new();

    public LambdaExpression GetOrTranslate(
        TypePair pair,
        LambdaExpression destLambda,
        Func<LambdaExpression> factory)
    {
        var key = new CacheKey(pair, destLambda);
        return _cache.GetOrAdd(key, _ => factory());
    }

    private readonly record struct CacheKey(TypePair Pair, LambdaExpression Lambda)
    {
        public bool Equals(CacheKey other) =>
            Pair.Equals(other.Pair) && ReferenceEquals(Lambda, other.Lambda);

        public override int GetHashCode() =>
            HashCode.Combine(Pair, RuntimeHelpers.GetHashCode(Lambda));
    }
}
```

The `ReferenceEquals` + `RuntimeHelpers.GetHashCode` cache key captures **lambda-instance identity**, not structural equality. Catches the realistic hot path of `static readonly Expression<Func<OrderDto, bool>> ActiveFilter = d => d.Active;` referenced from many call sites; misses freshly-constructed lambdas (which is fine — they translate once and don't need a cache).

`ConditionalWeakTable<MapperConfiguration, TranslationPlanCache>` ensures the cache lives as long as its owning configuration and is GC'd alongside it, matching `ProjectionPlanCacheRegistry`'s lifecycle.

### 4.4 New: `Atlas.Projections.MapperConfigurationExpressionTranslationExtensions`

Trivial. `public static Expression<Func<TSource, TResult>> Translate<TSource, TDestination, TResult>(this MapperConfiguration cfg, Expression<Func<TDestination, TResult>> destExpr)` — a one-line dispatch through the cache to the engine, returning the strongly-typed result. Used by power users; also used internally by the wrapper.

```csharp
public static Expression<Func<TSource, TResult>> Translate<TSource, TDestination, TResult>(
    this MapperConfiguration configuration,
    Expression<Func<TDestination, TResult>> destinationExpression)
{
    ArgumentNullException.ThrowIfNull(configuration);
    ArgumentNullException.ThrowIfNull(destinationExpression);

    var pair = new TypePair(typeof(TSource), typeof(TDestination));
    var translated = TranslationPlanCacheRegistry.For(configuration).GetOrTranslate(
        pair, destinationExpression,
        () => ExpressionTranslator.Translate(configuration.Internal_Registry, pair, destinationExpression));

    return (Expression<Func<TSource, TResult>>)translated;
}
```

### 4.5 No changes to existing types

`ProjectionExtensions`, `ProjectionPlanBuilder`, `ProjectionPlanCache`, `ProjectionCompatibility`, `ProjectionValidator`, `ParameterReplacer`, `AtlasProjectionException`, all `Atlas` core types, all `Atlas.Extensions.DependencyInjection` types — unchanged.

The wrapper's `AsQueryable()` calls `ProjectionExtensions.ProjectTo<TDestination>(_underlying, _configuration)` — uses the existing public method as a black box.

---

## 5. Translation Algorithm

The heart of the engine. Algorithm runs as a single-pass `ExpressionVisitor` over the destination lambda's body. The visitor's job: identify every member-access spine rooted at the destination parameter, replace it with the corresponding source-typed expression, leave everything else untouched.

### 5.1 The visitor's state

```csharp
private sealed class MemberAccessRewriter : ExpressionVisitor
{
    private readonly MapperRegistry _registry;
    private readonly ParameterExpression _destParam;     // d (in d => d.X.Y)
    private readonly ParameterExpression _srcParam;      // src (replacement)
    private readonly TypePair _rootPair;                 // (TSrc, TDst)
    private readonly Type _destType;                     // typeof(TDst)
    
    // implementation methods below
}
```

The visitor's `_destParam` identifies "any `MemberExpression` chain rooted here is a destination-member access we must rewrite." `_srcParam` is the new lambda parameter we'll emit on rewrite. `_rootPair` tells us which TypeMap to consult for top-level member accesses on `_destParam`.

### 5.2 `VisitParameter` — the destination parameter substitution

```csharp
protected override Expression VisitParameter(ParameterExpression node) =>
    node == _destParam ? _srcParam : base.VisitParameter(node);
```

Two cases:
- The lambda's own parameter — substitute with `_srcParam`. This case fires when the user writes `d => d` (rare but valid) or passes the parameter as an argument (`SomeFn(d)`); the destination instance becomes the source instance.
- Any other `ParameterExpression` (closures, sub-lambdas) — pass through.

Note: bare `d` substitution to `src` is a type-changing substitution. Naked-parameter usage is rare in real predicates; documented limitation in §10.

### 5.3 `VisitMember` — the core rewrite logic

This is the only complex method. Algorithm:

```
input: MemberExpression node (e.g., d.Customer.Name)

1. Find the spine: walk node.Expression repeatedly to extract the chain of member accesses
   rooted at _destParam.

   Examples:
     d.X                    → spine = [X], root = _destParam
     d.Customer.Name        → spine = [Customer, Name], root = _destParam
     d.Foo.Bar.Baz.Qux      → spine = [Foo, Bar, Baz, Qux], root = _destParam
     someClosure.Field      → root != _destParam → fall through to base.VisitMember

2. If the spine's root is NOT _destParam:
   Return base.VisitMember(node) — visits children normally; non-destination access untouched.

3. If the spine's root IS _destParam:
   Walk the spine left-to-right, threading a (currentSrcExpr, currentTypePair) state.
   Initial state: (currentSrcExpr = _srcParam, currentTypePair = _rootPair).
   
   For each member m in spine:
     a. Look up TypeMap for currentTypePair via _registry.GetTypeMap.
        If null: throw AtlasProjectionException (mid-chain pair has no registered map).
     b. Find PropertyMap on that TypeMap matching m.Name.
        If not found: throw AtlasProjectionException ("destination member 'X' on TDst has no
        registered mapping; cannot be referenced in a UseAsDataSource expression").
     c. If pm.Ignored: throw AtlasProjectionException ("destination member 'X' on TDst is
        configured with Ignore(); cannot be referenced in a UseAsDataSource expression").
     d. If pm.HasConstant: throw AtlasProjectionException ("destination member 'X' on TDst is
        a constant; predicates against it are trivially true/false. Write the constant
        comparison directly instead of going through the wrapper.").
     e. If pm.SourcePath is not null:
          newSrcExpr = walk pm.SourcePath.Members on currentSrcExpr, building
                       chained MemberAccess nodes.
          newSrcType = pm.SourcePath.LeafType
        elif pm.CustomExpression is not null:
          newSrcExpr = ParameterReplacer.Replace(
                         pm.CustomExpression.Body,
                         pm.CustomExpression.Parameters[0],
                         currentSrcExpr)
          newSrcType = pm.CustomExpression.ReturnType
        else: throw AtlasProjectionException ("destination member 'X' on TDst has neither
              SourcePath nor CustomExpression; cannot translate").
     
     f. Determine next-step typePair:
        - If this is the LAST member in the spine: we're done; result is newSrcExpr.
        - If there's a next member (m_next): the next member access is on a destination type
          (pm.DestinationProperty.PropertyType), and its translation requires the (newSrcType,
          pm.DestinationProperty.PropertyType) TypeMap. Set:
            currentSrcExpr = newSrcExpr
            currentTypePair = new TypePair(newSrcType, pm.DestinationProperty.PropertyType)
   
   Return newSrcExpr (after the final iteration).
```

### 5.4 `VisitMethodCall` — defensive detection of inner-lambda gap

The visitor includes a defensive override of `VisitMethodCall` to catch the v1-out-of-scope inner-lambda case (per §11 R1):

```csharp
protected override Expression VisitMethodCall(MethodCallExpression node)
{
    // Detect: collection-style methods (Any, All, Where, Select on Enumerable/Queryable)
    // whose first argument is a translated member-access on _destParam AND whose second
    // argument is a LambdaExpression typed against a destination element-type.
    if (IsCollectionPredicateMethod(node) &&
        node.Arguments.Count >= 2 &&
        node.Arguments[1] is LambdaExpression innerLambda &&
        IsDestinationElementType(innerLambda.Parameters[0].Type))
    {
        throw new AtlasProjectionException(
            "UseAsDataSource translation: inner lambdas on collection-typed destination " +
            "members are not translated in v1. Use AsQueryable() then LINQ-to-Objects, " +
            "or rewrite the predicate against the source.");
    }
    return base.VisitMethodCall(node);
}
```

The `IsCollectionPredicateMethod` predicate matches on `(Enumerable | Queryable).(Any | All | Where | Select | First | FirstOrDefault | Count)` — a finite set. `IsDestinationElementType` checks whether the inner lambda's parameter type appears as the element type of any destination member's collection in the current TypeMap (heuristic; doesn't have to be perfect — false negatives just produce LINQ-provider errors, which is the v1 fallback).

Conservative default: if detection is uncertain, let the base visitor descend; the LINQ provider's standard error fires at query execution.

### 5.5 Worked example: `d.Customer.Name`

Trace for `predicate = d => d.Customer.Name == "Alice"` against `(Order, OrderDto)`:

```
VisitBinary(d.Customer.Name == "Alice"):
  Visit(left): VisitMember(d.Customer.Name)
    spine = [Customer, Name], root = _destParam ✓
    state: (currentSrcExpr = _srcParam:Order, currentTypePair = (Order, OrderDto))
    
    iteration m="Customer":
      tm = _registry.GetTypeMap((Order, OrderDto)) → tm_order
      pm = tm_order.PropertyMaps.First(p => p.Name == "Customer")
      pm.Ignored? no. pm.HasConstant? no.
      pm.SourcePath = [Customer]  (convention: dst.Customer ↔ src.Customer)
      newSrcExpr = MemberAccess(_srcParam, Order.Customer)  // src.Customer
      newSrcType = typeof(Customer)
      
      next member exists (Name); compute next typePair:
      currentTypePair = (Customer, CustomerDto)
        // OrderDto.Customer is CustomerDto; Order.Customer is Customer; pair is the 
        // nested-typemap.
      currentSrcExpr = src.Customer
    
    iteration m="Name":
      tm = _registry.GetTypeMap((Customer, CustomerDto)) → tm_customer
      pm = tm_customer.PropertyMaps.First(p => p.Name == "Name")
      pm.SourcePath = [Name]
      newSrcExpr = MemberAccess(src.Customer, Customer.Name)  // src.Customer.Name
      newSrcType = typeof(string)
      
      no next member; return src.Customer.Name
    
    result: MemberAccess(MemberAccess(_srcParam, Customer), Name)
  
  Visit(right): "Alice" → unchanged Constant
  
  return BinaryExpression(src.Customer.Name == "Alice")

Final lambda: src => src.Customer.Name == "Alice"
```

### 5.6 Flattened-name worked example: `d.CustomerName`

Trace for `predicate = d => d.CustomerName.StartsWith("A")` against `(Order, OrderDto)` where `OrderDto.CustomerName` is convention-flattened from `Order.Customer.Name`:

```
VisitMethodCall(d.CustomerName.StartsWith("A")):
  Visit(object): VisitMember(d.CustomerName)
    spine = [CustomerName], root = _destParam ✓
    state: (_srcParam:Order, (Order, OrderDto))
    
    iteration m="CustomerName":
      tm = (Order, OrderDto)
      pm = tm.PropertyMaps.First(p => p.Name == "CustomerName")
      pm.SourcePath = [Customer, Name]  // convention-flattened
      newSrcExpr = walk [Customer, Name] on _srcParam:
                   MemberAccess(MemberAccess(_srcParam, Customer), Name)
      newSrcType = typeof(string)
      no next member; return.
    
    result: MemberAccess(MemberAccess(_srcParam, Customer), Name)
  
  Visit(arguments): ["A"] unchanged
  
  return MethodCallExpression(src.Customer.Name.StartsWith, "A")

Final lambda: src => src.Customer.Name.StartsWith("A")
```

The flattening case is structurally simpler than the nested-DTO case — `pm.SourcePath` already encodes the multi-segment path; the visitor walks it without needing to chain through nested TypeMaps.

### 5.7 `CustomExpression` worked example

Trace for `predicate = d => d.DisplayName == "Alice"` against `(Order, OrderDto)` where `OrderDto.DisplayName` is configured via `MapFrom(s => s.Customer.FirstName + " " + s.Customer.LastName)`:

```
iteration m="DisplayName":
  tm = (Order, OrderDto)
  pm = ...
  pm.CustomExpression = (s) => s.Customer.FirstName + " " + s.Customer.LastName  // bound to s:Order
  
  newSrcExpr = ParameterReplacer.Replace(
                 pm.CustomExpression.Body,        // s.Customer.FirstName + " " + s.Customer.LastName
                 pm.CustomExpression.Parameters[0],  // s
                 _srcParam)                        // src:Order
             = src.Customer.FirstName + " " + src.Customer.LastName
  newSrcType = typeof(string)
  
  no next member; return.

result: src.Customer.FirstName + " " + src.Customer.LastName

Final lambda: src => (src.Customer.FirstName + " " + src.Customer.LastName) == "Alice"
```

The same `ParameterReplacer` that `ProjectionPlanBuilder.BuildBinding` uses (line 105-110 of `ProjectionPlanBuilder.cs`) does the work. The user-supplied `MapFrom` expression is inlined; whether the LINQ provider can translate it is the provider's concern.

---

## 6. Reflection / Generic-Type Mechanics

The wrapper's operator methods are generic; the engine's `Translate` is non-generic (returns `LambdaExpression`). The bridge — converting `LambdaExpression` back to `Expression<Func<TSource, TResult>>` for the strongly-typed wrapper — is the only finicky bit. This section pins it.

### 6.1 The `Translate` engine returns `LambdaExpression`, not `Expression<Func<,>>`

Internal API (`ExpressionTranslator.Translate`) signature:

```csharp
public static LambdaExpression Translate(
    MapperRegistry registry,
    TypePair root,
    LambdaExpression destinationLambda);
```

Returns the abstract base — caller knows the concrete `Expression<TDelegate>` type at the call site (because they typed the input lambda) and casts.

**Why `LambdaExpression` and not generic:** the engine doesn't need the type parameters to do its work; it operates on `LambdaExpression.Body` and the visitor's runtime types. Making the engine generic would force `MakeGenericMethod` calls from the wrapper for every operator (the wrapper's `Where`'s `TResult` is `bool`; `OrderBy`'s is `TKey` — different per operator). Non-generic + cast-at-the-callsite is cleaner.

### 6.2 The wrapper's strongly-typed cast

In each operator method, the wrapper casts the engine's result to the operator-specific concrete type:

```csharp
public IUseAsDataSourceQueryable<TSource, TDestination> Where(
    Expression<Func<TDestination, bool>> predicate)
{
    var translated = (Expression<Func<TSource, bool>>)
        TranslationPlanCache.For(_configuration).GetOrTranslate(
            _pair, predicate,
            () => ExpressionTranslator.Translate(_registry, _pair, predicate));

    return new UseAsDataSourceQueryable<TSource, TDestination>(
        _underlying.Where(translated),
        _configuration);
}

public IUseAsDataSourceOrdered<TSource, TDestination> OrderBy<TKey>(
    Expression<Func<TDestination, TKey>> keySelector)
{
    var translated = (Expression<Func<TSource, TKey>>)
        TranslationPlanCache.For(_configuration).GetOrTranslate(
            _pair, keySelector,
            () => ExpressionTranslator.Translate(_registry, _pair, keySelector));

    return new UseAsDataSourceQueryable<TSource, TDestination>(
        _underlying.OrderBy(translated),
        _configuration);
}
```

The cast `(Expression<Func<TSource, TKey>>)result` succeeds because the engine constructs the lambda with the correct delegate type. Specifically, in `Translate`:

```csharp
var funcType = typeof(Func<,>).MakeGenericType(root.Source, destinationLambda.ReturnType);
return Expression.Lambda(funcType, rewrittenBody, srcParam);
// Concrete runtime type: Expression<Func<TSource, TResult>>
```

`Expression.Lambda(Type delegateType, Expression body, params ParameterExpression[] parameters)` produces an instance whose `GetType()` is `Expression<delegateType>` — so the cast in the wrapper succeeds at runtime even though the engine couldn't statically type it.

### 6.3 The wrapper does NOT use `MakeGenericMethod`

Compare to PR #12's `AttributeScanner`, which had to do the reflection dance because the attribute carried `Type` and the fluent surface was generic. Here, the wrapper is generic; the engine is non-generic; the bridge is a runtime cast. Cleaner — fewer reflection failure modes.

### 6.4 The cache key uses lambda reference identity

The translation cache stores `LambdaExpression` (untyped), keyed on `(TypePair, RuntimeHelpers.GetHashCode(lambda))`. Per Q5 → C: the cache catches `static readonly` lambdas reused across call sites; freshly-constructed lambdas miss the cache (and translate once each).

```csharp
private readonly record struct CacheKey(TypePair Pair, LambdaExpression Lambda)
{
    public bool Equals(CacheKey other) =>
        Pair.Equals(other.Pair) && ReferenceEquals(Lambda, other.Lambda);

    public override int GetHashCode() =>
        HashCode.Combine(Pair, RuntimeHelpers.GetHashCode(Lambda));
}
```

`record struct` for value semantics on the dictionary lookup; `ReferenceEquals` for the lambda comparison defeats user-defined `Expression<>.Equals` overrides (none exist today — `Expression<TDelegate>` inherits `object.Equals` reference-equality — but the explicit guard is consistent with PR #11's `MappingContext.RefEqComparer` defense-in-depth).

### 6.5 Performance posture

- **Translation runs at operator call site** — not on the hot path (LINQ enumeration). Each call: one cache lookup, optional one `Translate` invocation. Translation cost is `O(expression-tree-size)` × small constant for the visitor walk + `PropertyMap` lookups. For a typical predicate (~10-20 expression nodes), translation completes in microseconds.

- **Cache hit cost** is one `ConcurrentDictionary` lookup + `RuntimeHelpers.GetHashCode` call (~10-20 ns). Negligible.

- **Cache miss cost** is one translation (microseconds) + dictionary insert. Amortizes to free if the same lambda is reused.

- **`AsQueryable()` / enumeration cost** delegates to the existing `ProjectTo<TDestination>` machinery, which has its own `ProjectionPlanCache`. Same performance as a direct `ProjectTo` call.

No new hot-path overhead. The wrapper is "thin" by design — every operator method is one cache+cast+pass-through, not a query-tree-rewrite-on-enumerate.

### 6.6 Why no shared interface between `ExpressionTranslator` and `ProjectionPlanBuilder`

Both classes walk expression trees, both consult `PropertyMap`, both use `ParameterReplacer`. Could they share a base class or visitor framework? **Decision: no.** Concrete reasons:

1. **Inverse directions.** `ProjectionPlanBuilder` SYNTHESIZES a destination-typed expression FROM a source. `ExpressionTranslator` REWRITES a destination-typed expression TO a source-typed one. The visitor patterns are different: builder runs over PropertyMap collection (output structure); translator runs over user-provided expression tree (input structure).

2. **Different terminal cases.** Builder emits `Expression.MemberInit` with `MemberBinding`s; translator emits whatever expression the user wrote with member-access spines substituted. No shared output shape.

3. **Code duplication is small.** The actual shared logic is two helpers: walking a `SourceMemberPath` to build a chained `MemberExpression` (used by both); and `ParameterReplacer.Replace` for inlining `CustomExpression` (already shared via the existing internal class). The path-walking helper can be extracted to a small static utility (`SourceMemberPathExpressions.Build(srcExpr, path)`) shared by both — but it's a 5-line method; a copy-paste isn't worth refactoring for. **v1 ships with a small private helper inside `ExpressionTranslator`; the path-walking utility extraction is a post-merge cleanup if the duplication becomes a maintenance smell.**

---

## 7. Validation

The translator runs validation in three phases, all eager (per Q5 → C):

### 7.1 Phase 1 — Pair registration check (engine entry guard)

`ExpressionTranslator.Translate` immediately checks:

```csharp
var rootTm = registry.GetTypeMap(root);
if (rootTm is null)
    throw new AtlasProjectionException(
        $"UseAsDataSource translation: no map registered for {root.Source.Name} → {root.Destination.Name}. " +
        $"UseAsDataSource requires a registered map for the (source, destination) pair.");
```

Fires the moment the user calls `query.UseAsDataSource(cfg).For<UnregisteredDto>()` — call site name is in the stack trace.

### 7.2 Phase 2 — Projection compatibility check (existing dual-gate)

The translator reuses the existing rejection layer:

```csharp
if (!ProjectionCompatibility.IsTypeMapProjectable(rootTm, out var reason))
    throw new AtlasProjectionException($"UseAsDataSource translation: {reason}");
```

This catches:
- TypeMaps with hooks (`BeforeMap` / `AfterMap`) — PR #5 rejection rule
- TypeMaps with `PreserveReferences = true` — PR #11 rejection rule
- TypeMaps with `IsDynamic = true` — PR #10 rejection rule
- TypeMaps with `DestinationPath` (nested-destination chain bindings) — PR #4 rejection rule

The `reason` string from `IsTypeMapProjectable` is the same one `ProjectTo` produces. Wrapping with `"UseAsDataSource translation: "` prefix tells users which surface they hit.

**Net effect:** if a TypeMap rejects `ProjectTo`, it also rejects `UseAsDataSource`. Symmetric coverage; no new validator code.

### 7.3 Phase 3 — Per-member rejection (visitor)

The visitor throws `AtlasProjectionException` on the following cases. All errors thrown are `AtlasProjectionException` with a `"UseAsDataSource translation: "` prefix.

| Case | Trigger | Message |
|---|---|---|
| Member not found | `pm` is null for `m.Name` on `currentDst` | `"destination member '{TDst.Name}.{m.Name}' has no PropertyMap..."` |
| Ignored member | `pm.Ignored == true` | `"destination member '{TDst.Name}.{m.Name}' is configured with Ignore()..."` |
| Constant member | `pm.HasConstant == true` | `"destination member '{TDst.Name}.{m.Name}' is a constant ({pm.ConstantValue})..."` |
| Unmapped member | `pm.SourcePath is null && pm.CustomExpression is null && !pm.HasConstant && !pm.Ignored` | `"destination member '{TDst.Name}.{m.Name}' has neither a configured source path nor a custom expression..."` |
| Mid-chain pair not registered | `_registry.GetTypeMap((newSrcType, nextDstType))` is null when there's a next member | `"destination chain references nested map ({InnerSrc.Name} → {InnerDst.Name}) which is not registered."` |
| Inner lambda on collection-typed destination | `VisitMethodCall` defensive detection per §5.4 | `"inner lambdas on collection-typed destination members are not translated in v1..."` |

### 7.4 What the translator does NOT validate

- **Lambda-body LINQ-translatability.** Whether the LINQ provider can translate the user's expression (after parameter substitution) is the provider's concern. Atlas does not pre-inspect for `StartsWith`/`Contains`/`Substring`/etc. Documented in §1 non-goal.

- **Type-correctness after parameter substitution.** Bare-parameter usage (`d => SomeFn(d)`) produces an expression whose `_destParam → _srcParam` substitution may be type-incompatible. The CLR / LINQ provider catches this at compile-or-execution time. Documented in §10.1.

- **Untranslatable `CustomExpression` bodies.** A `MapFrom(s => ComplexNonTranslatableFn(s))` inlines fine but fails at LINQ-execution time. Atlas does not pre-inspect.

- **Closure-captured variables.** A predicate `d => d.Name == localVar` captures `localVar` as a closure. The visitor passes the closure access through unchanged; the LINQ provider handles parameterization. No new validation needed.

### 7.5 No new `ConfigurationValidator` rules

The existing `ConfigurationValidator` runs at `MapperConfiguration.AssertConfigurationIsValid()` time and catches misconfigurations. UseAsDataSource introduces no new "this configuration is invalid" rules — the rejection happens at translate time (when a user actually tries to translate against a problematic typemap or member). Symmetric with `ProjectTo`'s posture.

### 7.6 Validation phase ordering

1. **Engine entry** (`Translate` called): pair-registration check + projection-compatibility dual-gate. Phase 1 + Phase 2.
2. **Visitor descent** (per member access in the lambda): per-member rejections. Phase 3.

The visitor short-circuits on the FIRST rejection — no aggregation. Rationale: a single rejected member tells the user enough to fix; aggregating would require continuing the visitor walk past the failure point, which has no defined semantics (what's the "rewrite" of a rejected member access?). Compare to attribute-scan which aggregates across MULTIPLE attribute-decorated TYPES (independent units); a single-lambda translation has no parallel structure to aggregate.

---

## 8. Interaction with Existing v2 Features

The translate-to-source architecture means most existing v2 features compose transparently. Per-feature behavior:

### 8.1 #1 ProjectTo

**Status: composes via the wrapper's enumeration path.**

`UseAsDataSourceQueryable<,>.AsQueryable()` calls `IQueryable<TSource>.ProjectTo<TDestination>(_configuration)` on the underlying source query. Foreach / ToList / ToArray / AsEnumerable all funnel through `AsQueryable()` first, so the implicit final SELECT runs through ProjectTo. Same SQL output as a hand-written `query.Where(translatedPredicate).ProjectTo<TDest>(cfg).ToList()`.

### 8.2 #2 Inheritance (`Include` / `IncludeBase`)

**Status: works for the root TypeMap; nested-derived dispatch not supported.**

If `(Order, OrderDto)` has `Include<OnlineOrder, OnlineOrderDto>()` and the user calls `db.Orders.UseAsDataSource(cfg).For<OrderDto>().Where(d => d.Reference == "X")`, the translator looks up `(Order, OrderDto)` and translates against THAT typemap's PropertyMaps. Members declared only on `OnlineOrderDto` (the derived type) are not translatable through this code path — Atlas's projection-side has the same limitation.

A user who needs derived-type predicates can use `Where(d => d is OnlineOrderDto && ...)` if the LINQ provider supports `is`-checks, or downcast: `query.OfType<OnlineOrder>().UseAsDataSource(cfg).For<OnlineOrderDto>()`. Documented in §12 (Risks).

### 8.3 #3 Enum surface

**Status: works.** Enum → enum mappings produce a `PropertyMap.CustomExpression` (or `SourcePath` for ByValue identity) that the translator inlines. Predicates against enum-typed destination members translate to predicates against the equivalently-typed source members. Per-value overrides (e.g., `MapValue(Source.X, Dest.Y)`) generate a `Switch` expression in the CustomExpression that translates correctly via `ParameterReplacer`.

### 8.4 #4 Reverse mapping (`ReverseMap`)

**Status: works.** A reverse pair is just another `(TSrc, TDst)` registration; the translator doesn't care about origin. `db.Orders.UseAsDataSource(cfg).For<OrderDto>()` works whether the `(Order, OrderDto)` typemap was created via `cfg.CreateMap<Order, OrderDto>()` or via `cfg.CreateMap<OrderDto, Order>().ReverseMap()`.

`ForPath` (#4): the destination-side path is on the SOURCE direction's typemap. Reverse-direction translations don't see ForPath — `ProjectionCompatibility` rejects DestinationPath-bearing TypeMaps from projection (and now from translation via the dual-gate). User can't UseAsDataSource a typemap with `ForPath`. Documented limitation.

### 8.5 #5 Before/after hooks

**Status: rejected.** Hooks are projection-incompatible (PR #5 dual-gate); UseAsDataSource inherits that rejection. A user calling `db.Orders.UseAsDataSource(cfg).For<OrderDto>()` against a typemap with a `BeforeMap` throws `AtlasProjectionException` at the `For<>()` call (engine entry's Phase 2 dual-gate fires).

**Why this is correct:** hooks fire per-call on the in-memory mapper; SQL-translated queries can't run delegate code. User who wants UseAsDataSource semantics with hooks must either (a) remove the hooks (use a separate hook-free typemap pair for the SQL path), or (b) materialize without the wrapper, then run hooks in-memory.

### 8.6 #6 Value transformers

**Status: works for global and type-map scope; profile scope inherits OpenGenerics/Dynamic limitation.**

Value transformers are stored as `Expression<Func<T, T>>` and inlined into bindings. When the translator translates a destination-typed lambda, the destination member's resolved expression goes through the transformer pipeline (same as ProjectTo). For example: `dto.Email` maps to `src.Email` with a global `string` transformer that `s => s.ToLower()` — the translated predicate becomes `src.Email.ToLower() == "alice@example.com"`.

Profile-scope transformers don't fire on TypeMaps with `OriginatingProfile = null` (matches DynamicMapping #10, OpenGenerics #9, AttributeConfig #12). Same limitation; same workarounds.

### 8.7 #7 Conditional mapping (`Condition` / `PreCondition`)

**Status: pre-conditions inlined into the binding expression; conditions same.** Both predicates are stored as `Expression<Func<TSource, ...>>` lambdas; they wrap the resolved binding. The translator inlines them via `ParameterReplacer` the same way `ProjectionPlanBuilder` does. Predicates against destinations whose source binding has a `Condition` translate to predicates against the conditional expression.

### 8.8 #8 Null substitution

**Status: works.** `NullSubstitute` produces an `Expression.Coalesce(srcExpr, substituteExpr)` that translates to SQL `COALESCE`. The translator inlines the same way it inlines other binding shapes.

### 8.9 #9 Open generics

**Status: works for materialized closed pairs.** `cfg.CreateMap(typeof(Source<>), typeof(Dest<>))` registers an open template. When the user calls `db.SourceOfInts.UseAsDataSource(cfg).For<DestOfInt>()`, the registry's lazy materialization fires (per PR #9's `MapperRegistry.MaterializeClosed`), producing a closed `(Source<int>, Dest<int>)` TypeMap. The translator looks up the now-closed pair and translates normally.

`OriginatingProfile = null` on materialized closed pairs means profile-scope transformers don't fire — same limitation as #6.

### 8.10 #10 Dynamic mapping

**Status: rejected.** Dynamic-shape TypeMaps (`IsDynamic = true`) are rejected by `ProjectionCompatibility.IsTypeMapProjectable` (PR #10 dual-gate). UseAsDataSource inherits the rejection at Phase 2.

### 8.11 #11 Reference handling (`PreserveReferences`)

**Status: rejected.** `PreserveReferences = true` typemaps are rejected by `ProjectionCompatibility.IsTypeMapProjectable` (PR #11 dual-gate). UseAsDataSource inherits the rejection at Phase 2.

LINQ providers can't model identity tracking; the rejection is correct.

### 8.12 #12 Attribute-based configuration

**Status: works.** `[AutoMap]`-decorated TypeMaps are normal `TypeMap` instances; the translator doesn't know or care about attribute origin. `db.Orders.UseAsDataSource(cfg).For<OrderDto>()` works against attribute-declared maps the same way it works against profile-declared ones.

`[Ignore]` member-attribute → `pm.Ignored = true` → translation rejects with the "configured with Ignore()" message (Phase 3). `[SourceMember("Customer.Name")]` → `pm.SourcePath = [Customer, Name]` → translation works exactly like the convention case. `[NullSubstitute("default")]` → `pm.NullSubstitute` → translates as Coalesce per #8.

### 8.13 Summary table

| v2 feature | UseAsDataSource v1 status | Rejection mechanism |
|---|---|---|
| #1 ProjectTo | ✓ Composes via enumeration | — |
| #2 Inheritance | ✓ Root only; derived-dispatch limited | — (best-effort; document) |
| #3 Enum surface | ✓ Works | — |
| #4 ReverseMap | ✓ Works | — |
| #4 ForPath | ✗ Rejected | Existing DestinationPath dual-gate |
| #5 Hooks | ✗ Rejected | Existing hooks dual-gate |
| #6 Value transformers (global/typemap) | ✓ Works | — |
| #6 Value transformers (profile-scope) | ✗ Doesn't fire | OriginatingProfile == null |
| #7 Conditions / PreCondition | ✓ Inlined | — |
| #8 NullSubstitute | ✓ Translates to COALESCE | — |
| #9 Open generics (closed pairs) | ✓ Works via lazy materialization | — |
| #10 Dynamic mapping | ✗ Rejected | Existing dynamic dual-gate |
| #11 PreserveReferences | ✗ Rejected | Existing PR dual-gate |
| #12 Attribute config | ✓ Works | — |

**Rejection coverage:** every ProjectionCompatibility rejection rule transparently extends to UseAsDataSource via the Phase 2 dual-gate. Zero new rejection logic; zero coverage gaps.

---

## 9. DI Integration

UseAsDataSource is a configuration-level operation, not a service-level one. The wrapper takes an explicit `MapperConfiguration` parameter; no DI changes anticipated.

### 9.1 Existing DI surface (unchanged)

```csharp
services.AddAtlas(typeof(MyAssemblyMarker).Assembly);

// In a controller:
public class OrderController(IMapper mapper, MapperConfiguration mapperConfig) : ControllerBase
{
    public IActionResult Search(string nameFilter) =>
        Ok(_db.Orders
              .UseAsDataSource(mapperConfig)
              .For<OrderDto>()
              .Where(d => d.CustomerName.StartsWith(nameFilter))
              .ToList());
}
```

`MapperConfiguration` is registered as singleton by the existing `AddAtlas` extension. Users inject both `IMapper` and `MapperConfiguration` — same as `ProjectTo`'s usage pattern.

### 9.2 Why no `IMapper` extension method

Two reasons:

1. **`IMapper` doesn't expose `MapperConfiguration`.** `IMapper` is the runtime mapping facade; `MapperConfiguration` is the typemap registry. The existing `ProjectTo` extension takes `MapperConfiguration` directly because that's what holds the typemap data. UseAsDataSource follows the same convention.

2. **Symmetry with `ProjectTo`.** The reference doc and AutoMapper's own surface both treat queryable extensions as configuration-scoped, not mapper-scoped. Atlas's existing `ProjectTo<TDestination>(this IQueryable, MapperConfiguration)` follows this; UseAsDataSource matches.

### 9.3 No new lifetime decisions

| Type | Lifetime |
|---|---|
| `MapperConfiguration` | Singleton (existing) |
| `IMapper` | Transient (existing) |
| `TranslationPlanCache` | Per-`MapperConfiguration` via `ConditionalWeakTable` (effectively singleton; matches `ProjectionPlanCache`) |
| `UseAsDataSourceQueryable<TSrc, TDst>` | Per-call instance (new on every operator chain step) |
| `IUseAsDataSource<TSrc>` | Per-call instance |

The wrapper instances are short-lived and immutable (each operator returns a new instance). No threading concerns; the underlying `_underlying` IQueryable is captured by reference but treated immutably.

### 9.4 Atlas.Projections package — DI registration unchanged

The `Atlas.Projections` package has no DI registration today (it's pure extension methods + internal helpers; no services to register). UseAsDataSource adds no DI registration either. The package's only public surface is extension methods on `IQueryable<>` and `MapperConfiguration` — discovered via standard .NET extension-method lookup.

---

## 10. Edge Cases and Corner Behaviors

The full inventory of weird things users will inevitably try, with the explicit answer for each.

### 10.1 Bare destination parameter usage: `d => d`

User predicate: `d => d == anotherDto` or method call `d => SomeFn(d)`.

**Behavior:** the visitor's `VisitParameter` substitutes `d` → `_srcParam`. The resulting expression has `_srcParam` (typed `TSrc`) in a position previously occupied by `d` (typed `TDst`). Type incompatibility surfaces at one of three points:
- LINQ provider's expression-tree walker fails ("argument type mismatch").
- `Expression.Lambda(funcType, body, srcParam)` rejects the body type at construction.
- For `==` on reference types, the substitution may compile to invalid IL.

Atlas does NOT pre-detect this in v1. Documented limitation: bare-parameter usage is rare in practice; the LINQ provider's error is informative enough to debug. v1 behavior; revisit if user reports demand a clearer error.

### 10.2 Member access on nested destination type without intermediate mapping

User predicate: `d => d.Address.City == "London"` where `OrderDto.Address` is mapped (PropertyMap exists, `SourcePath = [Address]`) but the `(Address, AddressDto)` typemap is not registered.

**Behavior:** Phase 3 visitor encounters `d.Address`, resolves to `src.Address` via `pm.SourcePath`. Then needs to translate `.City` against `(Address, AddressDto)` — `_registry.GetTypeMap` returns null. Throws `AtlasProjectionException: "destination chain references nested map (Address → AddressDto) which is not registered."`.

User fix: register the nested pair (`cfg.CreateMap<Address, AddressDto>()`).

### 10.3 Method calls on translated members: `d.Name.StartsWith("A")`

The visitor translates `d.Name` to `src.Customer.Name` (or whatever the spine resolves to). The `.StartsWith("A")` is a `MethodCallExpression` with the translated member as its target — passes through unchanged. The LINQ provider translates `StartsWith` to SQL `LIKE 'A%'` (EF Core) or equivalent.

Method calls on member chains rooted at the destination parameter ARE supported transparently — the visitor only rewrites member access; method calls are opaque to it.

### 10.4 Collection-typed destination members: `d => d.Lines.Any(...)`

User predicate involves a collection: `d => d.Lines.Any(line => line.Total > 100)`.

**Behavior:** the visitor walks `d.Lines` — finds PropertyMap for `Lines`, substitutes per `SourcePath`/`CustomExpression` → `src.Lines`. The `.Any(line => line.Total > 100)` is a method call with an inner lambda. **The inner lambda's parameter (`line`) is typed `OrderLineDto`, but the source-side now expects `OrderLine`.**

**Per §5.4 defensive detection:** the visitor's `VisitMethodCall` override detects collection-predicate methods on translated destination members and throws `AtlasProjectionException: "inner lambdas on collection-typed destination members are not translated in v1..."`.

**Workaround:** the user rewrites the predicate against the source: `db.Orders.Where(o => o.Lines.Any(l => l.Total > 100)).UseAsDataSource(cfg).For<OrderDto>().Where(d => d.OtherFlatField == ...)`. Mix source-side and destination-side LINQ.

**Future v2:** inner-lambda translation requires propagating "this lambda's parameter is on the destination side; translate using `(SourceElementType, DestElementType)` typemap context" through the visitor. Defer.

### 10.5 Closure captures: `d => d.Name == externalVar`

User predicate captures a local variable: `var threshold = 100; query.Where(d => d.Total > threshold)`.

**Behavior:** the closure access is a `MemberExpression` with root `ConstantExpression` (the closure-capture object), NOT root `_destParam`. The visitor passes it through unchanged. The LINQ provider sees a parameter reference that can be SQL-parameterized. **Works correctly.**

### 10.6 Conditional expressions: `d => d.IsActive ? d.PrimaryName : d.SecondaryName`

User uses a ternary on destination members. The visitor descends into both branches — each member-access spine on `_destParam` translates independently. Result:
```
src => src.IsActiveSourceField ? src.Customer.PrimaryName : src.Customer.SecondaryName
```
LINQ provider handles `Conditional` (SQL `CASE WHEN`). **Works correctly.**

### 10.7 Predicates using arithmetic or string concatenation

`d => d.Quantity * d.UnitPrice > 100` — both members translate via the visitor's `VisitMember` independently; the surrounding `BinaryExpression` (multiply, then >) passes through. SQL: `WHERE (qty * unit_price) > 100`. **Works correctly.**

### 10.8 Translation idempotence: same predicate twice

User chains two `.Where(samePredicate)` calls. Both translate; cache catches the second one (lambda reference identity). Resulting underlying query: `Where(translatedPredicate).Where(translatedPredicate)`. Equivalent to `.Where(translatedPredicate && translatedPredicate)` after LINQ-provider simplification.

No correctness issue; minor efficiency cost (translated lambda evaluated twice in the SQL). Per Q5 rationale, the cache only catches reference-identical lambdas, not structural-equal ones — so freshly-constructed identical predicates miss the cache.

### 10.9 Async LINQ operators not on the wrapper

User calls `.ToListAsync()` on the wrapper. **Compile error** — `IUseAsDataSourceQueryable<,>` doesn't expose `ToListAsync`. User must call `.AsQueryable().ToListAsync()` to drop down to a translated `IQueryable<TDestination>` first. Documented in `AsQueryable()` xmldoc and in the README.

### 10.10 Wrapper after operator that drops the wrapper: chained `Select`

User attempts `query.UseAsDataSource(cfg).For<OrderDto>().Select(d => d.Total)`. **Compile error** — `Select` not on the wrapper. User must call `.AsQueryable().Select(d => d.Total)` to materialize as `IQueryable<OrderDto>` (with translated `Where`/`OrderBy`/etc. applied + ProjectTo for the materialization step), then standard LINQ on the `IQueryable<OrderDto>`. Documented limitation.

### 10.11 Mixed wrapper-and-source operations

User mixes wrapper and direct source: `db.Orders.Where(o => o.Total > 0).UseAsDataSource(cfg).For<OrderDto>().Where(d => d.CustomerName == "Alice").OrderBy(d => d.Date)`.

**Behavior:** the source-side `.Where(o => o.Total > 0)` runs first against the entity. The result `IQueryable<Order>` is then wrapped via `UseAsDataSource`. Subsequent destination-typed operators apply on top of the already-filtered source. The underlying query becomes:
```
SELECT proj FROM Orders WHERE Total > 0 AND Customer.Name = 'Alice' ORDER BY OrderDate
```
**Works correctly.** Standard pre-wrapping pattern from AutoMapper.

### 10.12 `For<TDest>()` on a non-class destination

User calls `.For<int>()` or `.For<string>()`. The `(TSrc, int)` typemap is unlikely to be registered → Phase 1 rejection at `For<>()` call site.

If the user has somehow registered `(Order, int)` (unusual but legal — `cfg.CreateMap<Order, int>().ConvertUsing(o => o.Id)`), translation runs. PropertyMap iteration over `int`'s properties (none in the typical sense) means there's nothing to translate; predicates against `int` make limited sense (`d => d > 100` translates to the source-side via the `ConvertUsing` body — works, but unusual).

**v1 stance: support whatever typemap is registered.** No special-case for value-type destinations. Documented in xmldoc.

### 10.13 Threading

The wrapper is immutable (each operator returns a new instance). The underlying `IQueryable<TSource>` is captured by reference; the user is responsible for the underlying provider's thread-safety semantics (EF Core's `DbContext` is NOT thread-safe; the user's responsibility to keep wrapper usage within one DbContext-scoped lifetime).

The translation cache is `ConcurrentDictionary` — thread-safe. Multiple threads translating the same lambda concurrently MAY both execute the factory (standard `GetOrAdd` race); the second result is discarded. Wasted work is bounded; no correctness issue.

### 10.14 Multiple `UseAsDataSource` calls on the same source

```csharp
var wrapper1 = db.Orders.UseAsDataSource(cfg).For<OrderDto>();
var wrapper2 = db.Orders.UseAsDataSource(cfg).For<OrderDto>();
```

Each call creates a new wrapper instance. Underlying `db.Orders` shared by reference. The two wrappers compose independently (no shared state). Translation cache (per-`MapperConfiguration`) shared, so identical lambdas across the two wrappers get cached translations. **Works correctly.**

### 10.15 Cross-`MapperConfiguration` lambda reuse

User creates two `MapperConfiguration` instances `cfgA` and `cfgB` (rare in production; common in tests). Calls the SAME `static readonly Expression<Func<OrderDto, bool>>` against both wrappers. Each `MapperConfiguration` has its own `TranslationPlanCache` (via `ConditionalWeakTable`); the lambda translates once per cache. Translation results may DIFFER if the typemaps differ between configurations — correct behavior.

---

## 11. Worked Examples End-to-End

Five runnable code samples showing the full UseAsDataSource UX, what the engine does internally, and what SQL the LINQ provider emits.

### 11.1 Example A — minimal flat predicate

**User code:**

```csharp
public class Order
{
    public int Id { get; set; }
    public decimal Total { get; set; }
}

public class OrderDto
{
    public int Id { get; init; }
    public decimal Total { get; init; }
}

public class OrderProfile : MapperProfile
{
    public OrderProfile() { CreateMap<Order, OrderDto>(); }
}

// Application code:
var orders = db.Orders
    .UseAsDataSource(mapperConfig)
    .For<OrderDto>()
    .Where(d => d.Total > 100)
    .ToList();
```

**Translation trace:**

```
.Where(d => d.Total > 100):
  ExpressionTranslator.Translate(_registry, (Order, OrderDto), predicate):
    Phase 1: GetTypeMap((Order, OrderDto)) → tm_order ✓
    Phase 2: ProjectionCompatibility.IsTypeMapProjectable(tm_order, _) → true ✓
    Phase 3: visitor walks d => d.Total > 100
      VisitBinary:
        Visit(left): d.Total
          spine = [Total], root = _destParam ✓
          state: (_srcParam:Order, (Order, OrderDto))
          m="Total":
            pm = tm_order.PropertyMaps["Total"]
            pm.SourcePath = [Total] (convention)
            newSrcExpr = MemberAccess(_srcParam, Order.Total) // src.Total
            return src.Total
        Visit(right): 100 (Constant) — unchanged
        return Binary(src.Total > 100)
    
    rewrite parameter: src => (src.Total > 100)
    funcType = Func<Order, bool>
    return Expression<Func<Order, bool>>: src => src.Total > 100
```

**Underlying query after wrapper apply:**

```csharp
db.Orders.Where(o => o.Total > 100)
```

**SQL emitted (EF Core):**

```sql
SELECT [proj].[Id], [proj].[Total] FROM [Orders] AS [proj] WHERE [proj].[Total] > 100.0
```

(Plus the implicit `ProjectTo<OrderDto>` at `.ToList()` time; for this trivial 1:1 mapping the SELECT shape is unchanged.)

### 11.2 Example B — flattened destination property

**User code:**

```csharp
public class Customer
{
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
}

public class Order
{
    public int Id { get; set; }
    public Customer Customer { get; set; } = new();
    public decimal Total { get; set; }
}

public class OrderDto
{
    public int Id { get; init; }
    public string CustomerFirstName { get; init; } = "";  // convention-flattened from Customer.FirstName
    public decimal Total { get; init; }
}

public class OrderProfile : MapperProfile
{
    public OrderProfile() { CreateMap<Order, OrderDto>(); }
}

// Application code:
var orders = db.Orders
    .UseAsDataSource(mapperConfig)
    .For<OrderDto>()
    .Where(d => d.CustomerFirstName.StartsWith("A"))
    .OrderBy(d => d.Total)
    .Take(10)
    .ToList();
```

**Translation traces:**

```
.Where(d => d.CustomerFirstName.StartsWith("A")):
  visitor: VisitMethodCall:
    Visit(object): VisitMember(d.CustomerFirstName)
      spine = [CustomerFirstName], root = _destParam ✓
      m="CustomerFirstName":
        pm.SourcePath = [Customer, FirstName]  // convention-flattened
        newSrcExpr = MemberAccess(MemberAccess(_srcParam, Customer), FirstName)
        return src.Customer.FirstName
    Visit(arguments[0]): "A" — unchanged
    return MethodCall(src.Customer.FirstName.StartsWith("A"))
  rewrite: src => src.Customer.FirstName.StartsWith("A")

.OrderBy(d => d.Total):
  visitor: VisitMember(d.Total) → src.Total
  rewrite: src => src.Total

.Take(10): no lambda, passthrough
```

**Underlying query after all wrapper ops:**

```csharp
db.Orders
    .Where(o => o.Customer.FirstName.StartsWith("A"))
    .OrderBy(o => o.Total)
    .Take(10)
```

**SQL emitted (EF Core):**

```sql
SELECT TOP(10) [proj].[Id], [proj].[Total],
       [proj].[Customer.FirstName] AS [CustomerFirstName]
FROM [Orders] AS [proj]
INNER JOIN [Customers] AS [c] ON [proj].[CustomerId] = [c].[Id]
WHERE [c].[FirstName] LIKE 'A%'
ORDER BY [proj].[Total]
```

### 11.3 Example C — `MapFrom` with custom expression

**User code:**

```csharp
public class OrderProfile : MapperProfile
{
    public OrderProfile()
    {
        CreateMap<Order, OrderDto>()
            .ForMember(d => d.DisplayName,
                       opt => opt.MapFrom(s => s.Customer.FirstName + " " + s.Customer.LastName));
    }
}

public class OrderDto
{
    public int Id { get; init; }
    public string DisplayName { get; init; } = "";
    // ...
}

// Application code:
var ordersForAlice = db.Orders
    .UseAsDataSource(mapperConfig)
    .For<OrderDto>()
    .Where(d => d.DisplayName.Contains("Alice"))
    .ToList();
```

**Translation trace:**

```
.Where(d => d.DisplayName.Contains("Alice")):
  visitor: VisitMethodCall:
    Visit(object): VisitMember(d.DisplayName)
      spine = [DisplayName], root = _destParam ✓
      m="DisplayName":
        pm.CustomExpression = (s) => s.Customer.FirstName + " " + s.Customer.LastName
                                       (s typed as Order)
        newSrcExpr = ParameterReplacer.Replace(
                       pm.CustomExpression.Body,    // s.Customer.FirstName + " " + s.Customer.LastName
                       pm.CustomExpression.Parameters[0],  // s
                       _srcParam)                    // src:Order
                   = src.Customer.FirstName + " " + src.Customer.LastName
        return that expression
    Visit(arguments[0]): "Alice" — unchanged
    return MethodCall((src.Customer.FirstName + " " + src.Customer.LastName).Contains("Alice"))
  rewrite: src => (src.Customer.FirstName + " " + src.Customer.LastName).Contains("Alice")
```

**SQL emitted (EF Core):**

```sql
SELECT [proj].* FROM [Orders] AS [proj]
INNER JOIN [Customers] AS [c] ON [proj].[CustomerId] = [c].[Id]
WHERE ([c].[FirstName] + N' ' + [c].[LastName]) LIKE N'%Alice%'
```

### 11.4 Example D — direct-use helper (no wrapper)

**User code:**

```csharp
// Power-user path: skip the wrapper, use the engine directly.
var translatedPredicate = mapperConfig
    .Translate<Order, OrderDto, bool>(d => d.CustomerFirstName.StartsWith("A"));
// translatedPredicate now is Expression<Func<Order, bool>>: src => src.Customer.FirstName.StartsWith("A")

var orders = db.Orders
    .Where(translatedPredicate)
    .ProjectTo<OrderDto>(mapperConfig)
    .ToList();
```

Equivalent to Example B's wrapper version, but the user explicitly threads the translated lambda. Useful when:
- The user wants a translated predicate as a value to pass elsewhere.
- The user is composing with custom LINQ providers that don't fit the wrapper's chain shape.
- The user is in a unit test verifying the translation directly (no live IQueryable needed).

### 11.5 Example E — rejection on ignored member

**User code:**

```csharp
public class OrderDto
{
    public int Id { get; init; }
    [Ignore]   // (or fluent .ForMember(d => d.Computed, opt => opt.Ignore()))
    public decimal Computed { get; init; }
}

// Application code:
var orders = db.Orders
    .UseAsDataSource(mapperConfig)
    .For<OrderDto>()
    .Where(d => d.Computed > 100)   // <-- destination member is Ignored
    .ToList();
```

**Translation throws:**

```
AtlasProjectionException: UseAsDataSource translation: destination member 'OrderDto.Computed'
is configured with Ignore() and cannot be referenced in a UseAsDataSource expression.
```

**Stack trace** points at the `.Where(d => d.Computed > 100)` line — user immediately sees which destination member is the problem.

**Fix paths:**
- Remove the `[Ignore]` (configure a real source mapping).
- Translate against a different destination type that DOES map `Computed`.
- Use the source side directly: `db.Orders.Where(o => o.Total > 100).UseAsDataSource(...).For<OrderDto>().ToList()`.

---

## 12. Risks & Open Questions

Honest accounting of what could bite, what's deferred, and what the implementer should push back on if needed.

### 12.1 Known risks (with mitigations)

**R1 — Inner-lambda translation gap.** §10.4: predicates with `.Any(...)`, `.All(...)`, `.Where(...)` calls on collection-typed destination members produce expressions the LINQ provider rejects. The visitor doesn't translate the inner lambda's parameter. Real users WILL hit this — collection-typed predicates are common.

**Mitigation:**
1. Document prominently in the README and the `Where`/`OrderBy` xmldoc — "Inner lambdas on collection-typed destination members are not translated; use the source side or call `AsQueryable()` and use LINQ-to-Objects after materialization."
2. Defensive `VisitMethodCall` override (per §5.4) detects the pattern and throws `AtlasProjectionException` at translate time with a clear message.
3. Track for v2 design: extend the visitor to thread destination-element-type context into inner lambdas. Non-trivial but well-defined.

**R2 — Bare-parameter usage produces invalid expressions.** §10.1: `d => SomeFn(d)` substitutes the parameter to source-typed; method on TDest no longer applicable. v1 doesn't pre-detect.

**Mitigation:**
1. Document in the wrapper's xmldoc.
2. Add a defensive check in `VisitMethodCall`: if the call's instance argument IS the bare `_destParam` (not a member-access on it), throw an explicit `AtlasProjectionException("UseAsDataSource cannot translate method calls on the bare destination parameter ('d.Method()'); the method may not exist on TSource. Use a member access ('d.X.Method()') or rewrite against the source.")`. Defensive, narrow detection — won't catch every malformed case but handles the common ones.

**R3 — Inheritance `Include`/`IncludeBase` derived-type predicates.** §8.2: a base typemap's wrapper can't translate predicates against derived-only properties.

**Mitigation:**
1. Document in §13 of the README.
2. Suggest the workaround: `query.OfType<DerivedSrc>().UseAsDataSource(cfg).For<DerivedDto>()` — the user explicitly downcasts and wraps the derived source.
3. v3 work: extend the visitor to detect `is`/`as`-checks against derived destination types and route to the corresponding derived TypeMap. Significant work; deferred.

**R4 — Cache memory growth on freshly-constructed lambdas.** Q5 → C: cache key uses `RuntimeHelpers.GetHashCode(lambda)` (reference identity). A user who builds a fresh `Expression<...>` per request via `Expression.Lambda(...)` will miss the cache every time AND fill the cache with one entry per request.

**Mitigation:**
1. The cache uses `ConditionalWeakTable<MapperConfiguration, TranslationPlanCache>` — when the configuration is collected, the cache goes with it. In typical DI usage, the configuration is singleton; the cache lives as long as the application.
2. The cache itself is a `ConcurrentDictionary` — unbounded growth IS possible if the user builds millions of unique lambda instances. Document the gotcha.
3. v3 work: add an opt-in size-bounded cache (LRU eviction); or expose `TranslationPlanCacheOptions { MaxSize = N }`. Not in v1 — the realistic memory pressure is bounded by the application's distinct-lambda count (typically tens, not millions).
4. Document in §13 of the README: "The translation cache is keyed on lambda reference identity. Reuse `static readonly Expression<>` lambdas where possible to maximize cache hits."

**R5 — Translation-time error overhead on hot paths.** Phase 1 + Phase 2 checks run on EVERY operator call. If the user calls `.Where(...)` 10,000 times in a loop with the same predicate, the cache catches the translation but Phase 1 + Phase 2 still run.

**Mitigation:**
1. Phase 1 + Phase 2 checks are: one dictionary lookup (`GetTypeMap`) and one boolean check (`IsTypeMapProjectable`). Both are O(1) and < 50 ns. No realistic performance impact even at 10K calls/sec.
2. If profiling shows Phase 1/2 overhead is meaningful, lift the checks to one-per-`For<>()`-call (cache the validated TypeMap on the wrapper). v3 optimization; not in v1.

**R6 — Operator surface gap drives users to the escape hatch.** Q2 → A: no `Select`, no `GroupBy`, no `SelectMany`. Users wanting these will call `.AsQueryable()` and continue with LINQ-to-Entities (`Select` translates over the destination type) or LINQ-to-Objects (after `.ToList()`).

**Mitigation:**
1. The `AsQueryable()` escape hatch is documented prominently. Users get the wrapper for what it's good at (filtering/sorting in DTO terms) and bypass it cleanly for shape-changing operations.
2. v2 work: add `Select<TResult>(Expression<Func<TDest, TResult>>)`. Requires the visitor to handle anonymous-typed projections AND the wrapper's chain to track per-step destination type. Real complexity; defer.

**R7 — Translation cache key collisions.** Two distinct lambdas with identical structure but different runtime hash codes will be cached separately. Two distinct lambdas with the SAME `RuntimeHelpers.GetHashCode` (random hash collision) AND the same `(TypePair)` will share a cache slot — but `ReferenceEquals` in `Equals` rejects the false-positive lookup, so the second lambda just translates again (correct behavior). No correctness risk.

### 12.2 Open questions to flag for the implementer

**O1 — Should the no-predicate `Any()` / `Count()` overloads delegate to the underlying or materialize first?** The wrapper's `Any()` (no predicate) just asks "are there any matches?" The simplest implementation: delegate to `_underlying.Any()`. Materializing through ProjectTo first wastes work. **Decision: delegate to `_underlying.Any()`.** Same for `Count()`, `LongCount()`. Documented in xmldoc.

**O2 — Should `First()` / `FirstOrDefault()` (no predicate) use ProjectTo or materialize then map in-memory?** Two paths:
- (a) `AsQueryable().First()` — ProjectTo runs in SQL, returns one row, materialized as TDest. Single round-trip; SQL `TOP 1`.
- (b) `_underlying.First()` then `mapper.Map<TDest>(srcInstance)` — fetches one source instance via SQL, maps in-memory. Two-step but allows full mapper semantics (hooks, transformers, etc.).

**Decision: (a).** The wrapper's contract is "destination-typed query"; users who want full in-memory mapper semantics use the underlying source query directly. Same posture as `ProjectTo`. Documented.

**O3 — Should `AsQueryable()` cache its result?** Each call rebuilds the projection. For a wrapper used multiple times (e.g., `var q = wrapper.AsQueryable(); q.ToList(); q.Count();`), the projection rebuilds twice. Real cost: small; the projection is a single `IQueryable.Select` call which the LINQ provider handles cheaply.

**Decision: no caching.** Wrapper instances are short-lived; caching adds complexity for negligible gain. If profiling shows it, add a one-shot cache field in v2. Documented as "no caching" in xmldoc.

**O4 — Should `IUseAsDataSourceOrdered<,>` strictly inherit `IUseAsDataSourceQueryable<,>`?** The current API does — meaning after `OrderBy`, the user can call `Where` (which doesn't preserve ordering on most LINQ providers). Two options:
- (a) Strict inheritance: simple API, minor "should ordering carry forward" subtlety.
- (b) Separate interface without `Where`/`Take`/`Skip`: forces users to interleave ordering ops first.

**Decision: (a).** Matches `IOrderedQueryable<T> : IQueryable<T>` in BCL; users expect this shape. The "Where after OrderBy" subtlety is a LINQ-general concern, not Atlas-specific.

**O5 — Does the wrapper expose `TSource` to user code?** The interface `IUseAsDataSourceQueryable<TSource, TDestination>` carries `TSource` as a type parameter. Users see it; they CAN read it via reflection. **Decision: yes — TSource is part of the public contract.** Users may want to compose another wrapper or pass the underlying type around. Documented in xmldoc as a public API guarantee.

### 12.3 Things explicitly NOT design questions for v1

- Implementer must NOT add `Select` / `SelectMany` / `GroupBy` / `Include` / `Join` operators "while they're in there." Q2 → A.
- Implementer must NOT translate inner lambdas on collection-typed destination members. R1; defer to v2.
- Implementer must NOT support derived-type dispatch via `is`-checks. R3; defer to v3.
- Implementer must NOT add async LINQ operators to the wrapper. Q2 → A; users use `AsQueryable()`.
- Implementer must NOT introduce structural equality on the cache key. Q5 → C; lambda reference identity only.
- Implementer must NOT introduce a separate `Atlas.Expressions` package. Q3 → A; ship in `Atlas.Projections`.
- Implementer must NOT make the engine generic. §6; non-generic with cast-at-call-site.
- Implementer must NOT implement `IQueryProvider` on the wrapper. §2.4.

---

## 13. README Delta

The user-facing changes that ship alongside the code in PR #13. Slots into the existing structure following the established pattern from PRs #11 and #12.

### 13.1 New README section: "Expression translation (UseAsDataSource)"

Inserted after "Attribute-based configuration" (PR #12's section). Approximate length: 80-100 lines. Contents:

- One-line intro: "Wrap an `IQueryable<TSource>` and write filtering, sorting, and paging in destination-DTO terms. Atlas translates the destination-typed lambdas back to source-typed expressions before they hit your LINQ provider."
- Minimal example (Example A from §11).
- Flattening example (Example B from §11, condensed).
- Custom-expression example (Example C from §11).
- Direct-use helper example (Example D from §11).
- Operator scope table (the 17 supported operators, grouped: filtering / ordering / paging / terminal predicate).
- Escape hatch: `AsQueryable()` returns a translated `IQueryable<TDestination>` for `Select`, `GroupBy`, async LINQ, etc.
- Rejection rule: "Predicates against destination members configured with `Ignore()`, constants, or with no source mapping throw `AtlasProjectionException` at the operator call site naming the offending member."
- Caching note: "Translation results cache per `(TypePair, lambda-reference-identity)`. Reuse `static readonly Expression<>` lambdas to maximize cache hits."
- Limitations subsection:
  - Inner lambdas on collection-typed destination members not translated (`d => d.Lines.Any(l => l.Total > 100)`). Workaround documented.
  - Derived-type dispatch via inheritance not supported. Workaround: explicit `OfType<>()`.
  - Bare-parameter usage (`d => d == other`) not pre-detected; the LINQ provider's standard error fires.
- Compatibility table: which v2 features compose (✓) vs reject (✗) — mirrors §8.13.

### 13.2 Deferred-list update post-merge

`C:\Users\ajsde\.claude\projects\C--Repos-Atlas\memory\atlas_v2_design_docs_deferred.md`:

```
13. ~~Expression translation (`UseAsDataSource` equivalent).~~ — **shipped** (PR #13 merged at HEAD `<sha>` on <date>; see `docs/Atlas-Design-ExpressionTranslation.md`). [Full recap to be written post-merge per the established pattern.]
```

After this entry: **"All 13 v2 features shipped. Atlas v2 complete."**

### 13.3 MEMORY.md update post-merge

```
- [Atlas v2 deferred features](atlas_v2_design_docs_deferred.md) — 13 feature groups; all 13 shipped (#1-13: ProjectTo, Inheritance, Enum, ReverseMap, Hooks, ValueTransformers, ConditionalMapping, NullSubstitution, OpenGenerics, DynamicMapping, ReferenceHandling, AttributeConfig, ExpressionTranslation). Atlas v2 complete.
```

The "next up" suffix is removed. The deferred-features file becomes a historical record rather than a queue.

### 13.4 `feedback_atlas_v2_workflow.md` test baseline update

`Test baseline: 710 → ~776 after ExpressionTranslation.`

### 13.5 `feedback_pseudocode_concrete_trace.md` — no anticipated change

The architecture intentionally avoids the bug categories that have bitten before:
- **Bug 4 (cross-package consumer audit):** the engine is in `Atlas.Projections` — no new shared-shape field added; reuses existing `PropertyMap` properties.
- **Bug 5 (scope-identifying metadata propagation):** no new TypeMap fields; no propagation concern.
- **Bug 8 (bidirectional propagation):** no flag-mutation interaction with paired siblings.
- **Bug 9 (asymmetric reflection-invoke unwrap):** the engine uses one centralized expression-tree visitor (no reflection invokes); the wrapper uses no reflection at all (cast-at-call-site instead). Pattern symmetry preserved.

If holistic review surfaces a new bug category, append it post-merge per the established pattern.

### 13.6 Documentation file list (final inventory)

**New files (in PR #13):**
- `docs/Atlas-Design-ExpressionTranslation.md` — this design doc.
- `docs/Atlas-Plan-ExpressionTranslation.md` — implementation plan (next phase).
- `src/Atlas.Projections/UseAsDataSourceExtensions.cs`
- `src/Atlas.Projections/IUseAsDataSource.cs`
- `src/Atlas.Projections/IUseAsDataSourceQueryable.cs`
- `src/Atlas.Projections/IUseAsDataSourceOrdered.cs`
- `src/Atlas.Projections/MapperConfigurationExpressionTranslationExtensions.cs`
- `src/Atlas.Projections/Internal/ExpressionTranslator.cs`
- `src/Atlas.Projections/Internal/UseAsDataSourceQueryable.cs`
- `src/Atlas.Projections/Internal/TranslationPlanCache.cs`
- 6-7 test files in `tests/Atlas.Projections.Tests/` (per §14)

**Modified files (in PR #13):**
- `README.md` — add "Expression translation (UseAsDataSource)" section.

---

## 14. Testing Strategy

The test layout mirrors v2 feature precedent. xUnit v3 with plain `Assert.X()` only (per `feedback_no_fluentassertions.md`).

### 14.1 Test files (count: 7 new files, ~55-65 net new tests)

**1. `tests/Atlas.Projections.Tests/ExpressionTranslatorTests.cs`** — engine unit tests (~14 tests):
- Flat member access translates (`d.X` → `src.X`)
- Convention-flattened member translates (`d.CustomerName` → `src.Customer.Name`)
- Multi-level nested-DTO chain translates (`d.Customer.Address.City` → `src.Customer.Address.City` via two TypeMap hops)
- `MapFrom(s => Expression)` `CustomExpression` inlines via `ParameterReplacer`
- `[SourceMember("Path")]` resolves like convention
- Method calls on translated members pass through (`d.Name.StartsWith("A")`)
- Binary operators on translated members pass through (`d.Total > 100`)
- Conditional expressions descend into both branches (`d.IsActive ? d.A : d.B`)
- Closure-captured variables pass through unchanged (`d => d.Total > localVar`)
- Multiple member chains in same lambda translate independently (`d => d.A == 1 && d.B.C == 2`)
- Pair not registered → `AtlasProjectionException`
- Mid-chain pair not registered → `AtlasProjectionException`
- Member not found → `AtlasProjectionException`
- Nested `MapFrom` inside `MapFrom` (rare but legal) inlines correctly

**2. `tests/Atlas.Projections.Tests/ExpressionTranslatorRejectionTests.cs`** — Phase 2 + Phase 3 rejection tests (~10 tests):
- TypeMap with hooks → rejected at translate time
- TypeMap with `PreserveReferences = true` → rejected
- TypeMap with `IsDynamic = true` → rejected
- TypeMap with `DestinationPath` (`ForPath`) → rejected
- `[Ignore]` member → rejected with attribute-named message
- `MapFrom(constant)` member → rejected with constant-named message
- Convention-unmapped member → rejected with "no PropertyMap" message
- Member exists with no source AND no constant AND not ignored → rejected with "neither path nor expression" message
- Phase ordering: pair-not-registered fires before any visitor work
- Phase ordering: hooks-rejection fires before visitor descent

**3. `tests/Atlas.Projections.Tests/UseAsDataSourceWrapperTests.cs`** — wrapper-class behavior tests (~12 tests):
- `UseAsDataSource(cfg).For<TDest>()` returns a wrapper
- `For<TDest>()` against unregistered pair throws at call site
- `Where` translates and applies; chained `Where` chains
- `OrderBy`/`OrderByDescending` translate; cast to `IUseAsDataSourceOrdered<,>`
- `ThenBy`/`ThenByDescending` chain after ordering
- `Skip`/`Take` pass through without translation
- `AsQueryable()` returns translated `IQueryable<TDestination>` with ProjectTo applied
- Enumeration triggers ProjectTo via `AsQueryable().GetEnumerator()`
- `Any()` / `Count()` / `Any(predicate)` / `Count(predicate)` work
- `First()` / `FirstOrDefault()` materialize via ProjectTo
- `First(predicate)` / `FirstOrDefault(predicate)` translate predicate then materialize
- Wrapper instances are immutable (each operator returns new instance)

**4. `tests/Atlas.Projections.Tests/UseAsDataSourceCacheTests.cs`** — translation cache tests (~6 tests):
- Same lambda reference reused: cache hit (translation called once)
- Different lambda instances same structure: cache miss (translates twice)
- Cache scoped per `MapperConfiguration`: same lambda + two configs → two translations
- Cache survives `MapperConfiguration` lifetime via `ConditionalWeakTable`
- Concurrent translation: safe (no exception, possibly redundant work)
- Cache key uses `RuntimeHelpers.GetHashCode` (verify by mocking equal-but-distinct lambdas)

**5. `tests/Atlas.Projections.Tests/UseAsDataSourceCompatibilityTests.cs`** — interaction with other v2 features (~10 tests):
- Attribute-declared TypeMap works through wrapper (`[AutoMap]`)
- Reverse-map TypeMap works in both directions
- Open-generic materialized closed pair works
- Global value transformer fires on translated members
- Profile-scope transformer does NOT fire on TypeMap with `OriginatingProfile = null`
- `NullSubstitute` translates to Coalesce
- `Condition`/`PreCondition` inlined into binding expression
- Inheritance base-typemap works; derived-only members rejected
- Enum mapping translates via `CustomExpression`
- Inner lambda on collection-typed destination member → defensive rejection per R1

**6. `tests/Atlas.Projections.Tests/UseAsDataSourceIntegrationTests.cs`** — end-to-end DI + multi-op scenarios (~6 tests):
- DI-resolved `MapperConfiguration` works through wrapper
- Mixed source-side + destination-side ops (`db.Orders.Where(o => ...).UseAsDataSource(...)`)
- Multi-op chain with all 4 categories (filter+order+paging+terminal)
- Direct-use helper produces equivalent SQL to wrapper version
- Multiple `UseAsDataSource` calls on same source compose independently
- AsyncLINQ workaround: wrapper → `AsQueryable()` → `ToListAsync` works (verifies the escape hatch)

**7. `tests/Atlas.Projections.Tests.EFCore/UseAsDataSourceEFCoreTests.cs`** — SQL-emission tests using EF Core in-memory provider (~8 tests):
- Convention member → expected SQL clause
- Flattened member → expected JOIN + WHERE clause
- `CustomExpression` member → expected SQL expression
- `OrderBy` translates to `ORDER BY` clause
- `Skip`/`Take` translate to `OFFSET`/`FETCH` (EF Core 7+) or LIMIT
- `Any(predicate)` translates to `SELECT EXISTS(SELECT ...)`
- `Count(predicate)` translates to `SELECT COUNT(*)`
- Combined predicate + ordering + paging produces well-formed SQL

**Test baseline projection:** 710 → ~776 (≈66 net new tests).

### 14.2 Test fixture conventions

- Test types in dedicated namespace (`Atlas.Projections.Tests.UseAsDataSource.Fixtures` or per-file private nested classes) to avoid pollution of unrelated tests' assembly scans.
- Each test file uses its own fixture types; unique class-name prefix avoids collisions.
- Fixtures use auto-properties (Atlas convention scans properties).
- Cache tests use `static readonly Expression<>` fields for the reference-identity case AND `() => Expression.Lambda<...>(...)` factory for the structural-distinct case.

### 14.3 Coverage targets

- ≥ 90% line + branch on `Atlas.Projections.Internal.ExpressionTranslator`.
- ≥ 90% line + branch on `Atlas.Projections.Internal.UseAsDataSourceQueryable<,>`.
- ≥ 85% on the public attribute classes / extension methods (mostly delegation; coverage thresholds reflect the trivial nature).
- Existing v1 + v2 coverage thresholds unchanged.

---

## 15. Appendix A — End-to-End Trace of Example C

The single most complex codepath: a `MapFrom(s => Expression)` `CustomExpression` inlined inside a method-call on a destination member. Trace is for `OrderDto.DisplayName.Contains("Alice")` from Example C (§11.3).

### 15.1 Inputs to the engine

```csharp
public class OrderProfile : MapperProfile
{
    public OrderProfile()
    {
        CreateMap<Order, OrderDto>()
            .ForMember(d => d.DisplayName,
                       opt => opt.MapFrom(s => s.Customer.FirstName + " " + s.Customer.LastName));
    }
}

// User's predicate to translate:
var predicate = (Expression<Func<OrderDto, bool>>) (d => d.DisplayName.Contains("Alice"));
```

### 15.2 Wrapper trace (operator call site)

```
db.Orders.UseAsDataSource(mapperConfig).For<OrderDto>().Where(predicate)

UseAsDataSourceQueryable<Order, OrderDto>.Where(predicate):
  pair = (Order, OrderDto)
  
  cache = TranslationPlanCacheRegistry.For(mapperConfig)
  cacheKey = (pair, RuntimeHelpers.GetHashCode(predicate))
  
  cache.GetOrTranslate(pair, predicate, factory):
    miss (first call)
    factory():
      ExpressionTranslator.Translate(_registry, pair, predicate)  // see 15.3
      return rewritten lambda
    cache.TryAdd(cacheKey, rewritten)
    return rewritten
  
  translated = (Expression<Func<Order, bool>>)rewritten
  // src => (src.Customer.FirstName + " " + src.Customer.LastName).Contains("Alice")
  
  newUnderlying = _underlying.Where(translated)
  // IQueryable<Order> with the .Where applied
  
  return new UseAsDataSourceQueryable<Order, OrderDto>(newUnderlying, _configuration)
```

### 15.3 Engine trace

```
ExpressionTranslator.Translate(_registry, (Order, OrderDto), predicate):
  
  Phase 1: GetTypeMap((Order, OrderDto)) → tm_order ✓
  Phase 2: ProjectionCompatibility.IsTypeMapProjectable(tm_order, _) → true ✓
  
  destParam = predicate.Parameters[0] // d (typed OrderDto)
  srcParam = Expression.Parameter(typeof(Order), "src")
  
  visitor = new MemberAccessRewriter(_registry, destParam, srcParam, (Order, OrderDto), typeof(OrderDto))
  
  Phase 3: visitor.Visit(predicate.Body)
  
  predicate.Body = MethodCallExpression:
    Method:    string.Contains(string)
    Object:    MemberAccess(d, OrderDto.DisplayName)  // d.DisplayName
    Arguments: [Constant("Alice")]
  
  visitor.VisitMethodCall:
    // Defensive check (§5.4): is this a collection-predicate method on a translated member?
    // string.Contains is NOT in the (Enumerable | Queryable).(Any | All | ...) set; pass through.
    
    Visit(node.Object): VisitMember(d.DisplayName)
      spine extraction:
        node = d.DisplayName
        node.Expression = d (ParameterExpression == _destParam) ✓
        spine = [DisplayName], root = _destParam
      
      walk spine:
        state: (currentSrcExpr = _srcParam:Order, currentTypePair = (Order, OrderDto))
        
        m = "DisplayName":
          tm = (Order, OrderDto)
          pm = tm.PropertyMaps["DisplayName"]
          pm.Ignored ? false
          pm.HasConstant ? false
          pm.SourcePath ? null
          pm.CustomExpression ? = (s) => s.Customer.FirstName + " " + s.Customer.LastName
                                   bound to s:Order (from the original ForMember.MapFrom)
          
          newSrcExpr = ParameterReplacer.Replace(
                         pm.CustomExpression.Body,           // s.Customer.FirstName + " " + s.Customer.LastName
                         pm.CustomExpression.Parameters[0],  // s
                         _srcParam)                          // src:Order
                     = src.Customer.FirstName + " " + src.Customer.LastName
                     // (BinaryExpression: BinaryExpression(MemberAccess(MemberAccess(src,Customer),FirstName), 
                     //                                      Constant(" ")) + MemberAccess(MemberAccess(src,Customer),LastName))
          newSrcType = typeof(string)
          
          no next member (last in spine); return newSrcExpr
      
      result: src.Customer.FirstName + " " + src.Customer.LastName  // BinaryExpression
    
    Visit(node.Arguments[0]): VisitConstant("Alice")
      "Alice" is not a destination access; pass through unchanged.
      result: Constant("Alice")
    
    return MethodCallExpression(
      Method: string.Contains(string),
      Object: src.Customer.FirstName + " " + src.Customer.LastName,
      Arguments: [Constant("Alice")])
  
  body = (src.Customer.FirstName + " " + src.Customer.LastName).Contains("Alice")
  
  funcType = typeof(Func<,>).MakeGenericType(typeof(Order), typeof(bool)) = Func<Order, bool>
  result = Expression.Lambda(funcType, body, srcParam)
  
  // Concrete runtime type: Expression<Func<Order, bool>>
  // Body: (src.Customer.FirstName + " " + src.Customer.LastName).Contains("Alice")
  // Parameter: src:Order
  
  return result
```

### 15.4 Underlying query state

After the wrapper applies the translated `.Where(...)`:

```csharp
_underlying = db.Orders.Where(src =>
    (src.Customer.FirstName + " " + src.Customer.LastName).Contains("Alice"));
// IQueryable<Order>
```

### 15.5 Enumeration trace

```
.ToList():
  IEnumerable<OrderDto>.GetEnumerator() (called by .ToList())
    → AsQueryable().GetEnumerator()
    
    AsQueryable():
      return _underlying.ProjectTo<OrderDto>(_configuration)
      // existing PR #1 machinery
      // produces IQueryable<OrderDto> via Queryable.Select
      // SELECT shape includes:
      //   d.Id = src.Id
      //   d.DisplayName = src.Customer.FirstName + " " + src.Customer.LastName  (per the same MapFrom CustomExpression, applied symmetrically)
      //   ... other members
    
    .GetEnumerator():
      EF Core builds the SQL command:
        SELECT [proj].[Id],
               (([c].[FirstName] + N' ') + [c].[LastName]) AS [DisplayName]
        FROM [Orders] AS [proj]
        INNER JOIN [Customers] AS [c] ON [proj].[CustomerId] = [c].[Id]
        WHERE (([c].[FirstName] + N' ') + [c].[LastName]) LIKE N'%Alice%'
      
      Execute the SQL, materialize each row as an OrderDto instance.
      Return IEnumerator<OrderDto>.
```

### 15.6 What the implementer takes away

1. **The visitor's cleanest path** is the `CustomExpression` inlining via `ParameterReplacer` — the same code that `ProjectionPlanBuilder.BuildBinding` uses (line 105-110). Reuse, don't rebuild.

2. **The defensive `VisitMethodCall` check is critical** — without it, `d.Lines.Any(l => l.Total > 100)` would silently produce a malformed expression that the LINQ provider rejects at query execution time. The check fires at translate time with a clear actionable error.

3. **The cast `(Expression<Func<Order, bool>>)result` succeeds** because `Expression.Lambda(funcType, body, srcParam)` produces an instance whose runtime type is exactly `Expression<funcType>`. No reflection, no `MakeGenericMethod`, no fragility.

4. **Cache hit on the second `.Where(predicate)` call** with the SAME `predicate` reference reuses the translated lambda — verified by the `static readonly Expression<>` test case in §14.

---

**End of design.**
