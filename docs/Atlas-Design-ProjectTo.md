# Plan: Atlas v2 — Queryable Projection (`ProjectTo`)

> **Status:** Approved design, ready to implement.
> **Depends on:** `Atlas` v1 (already shipped per `docs/Atlas-Design.md`, 88 tests green).
> **Output of this doc:** A new `Atlas.Projections` package and matching test suite.

---

## 1. Goals & Non-Goals

### 1.1 Goals
- Translate the configured Atlas mapping for a `(TSource, TDestination)` pair into a LINQ `Expression<Func<TSource, TDestination>>` and apply it as a `Select` over an `IQueryable`.
- Eagerly reject configurations that contain in-memory-only constructs at the call site, listing every incompatibility in one exception. No "works in `Map`, blows up in `ProjectTo`" runtime surprises.
- Cache the built expression per `(TypePair, maxDepth)` per `MapperConfiguration` instance.
- Ship as a separate NuGet package (`Atlas.Projections`) so consumers who don't use projection don't pay for the IL.
- No churn to the `Atlas` v1 core. The new package consumes v1 internals via the existing `InternalsVisibleTo` grant.

### 1.2 Non-Goals (explicit out-of-scope; future design docs)
- Runtime parameter dictionary (`ProjectTo<Dto>(config, new { currentUserName = ... })`).
- Explicit member expansion (`ProjectTo<Dto>(config, dest => dest.WideCollection)`).
- Cross-provider compatibility validation (Postgres / SQL Server / MySQL parity matrix). v1 of ProjectTo is tested against EF Core SQLite.
- Per-map fluent setter (`CreateMap<S,D>().MaxDepth(3)`). Depth is per-call only in v1; per-map can be added without breaking change.
- `IMapper.ProjectTo(...)` instance method. The extension on `IQueryable` is the only call shape.
- Provider-specific helpers (Include hints, query splitting strategy, raw SQL fallbacks).

---

## 2. Architecture Overview

```
┌──────────────────────────┐
│   Atlas (v1, unchanged)  │
│  MapperConfiguration     │
│  TypeMap, PropertyMap    │
│  MapperRegistry          │
└────────────┬─────────────┘
             │ InternalsVisibleTo
             ▼
┌──────────────────────────────────────────────────┐
│              Atlas.Projections                   │
│                                                  │
│  public ProjectionExtensions                     │
│      .ProjectTo<TDest>(this IQueryable, …)       │
│           │                                      │
│           ▼                                      │
│  internal ProjectionPlanCache                    │
│      ConditionalWeakTable<MC, Cache>             │
│           │ miss                                 │
│           ▼                                      │
│  internal ProjectionValidator                    │
│      walks TypeMap graph; throws on bad binding  │
│           │ silent                               │
│           ▼                                      │
│  internal ProjectionPlanBuilder                  │
│      emits Expression<Func<TSource, TDest>>      │
│           │                                      │
│           ▼                                      │
│  IQueryable<TDest> = source.Select(expression)   │
└──────────────────────────────────────────────────┘
```

Runtime flow on `query.ProjectTo<Dto>(config)`:
1. Resolve `(TSource, TDestination, maxDepth)` from `source.ElementType` and the type parameter.
2. Get-or-create the `ProjectionPlanCache` attached to `config` via `ConditionalWeakTable`.
3. On cache miss: run `ProjectionValidator` (throws `AtlasProjectionException` on incompatibility); then `ProjectionPlanBuilder` produces the expression; cache it.
4. Return `query.Provider.CreateQuery<TDest>(Expression.Call(Queryable.Select, query.Expression, expression))`. The provider does the rest.

---

## 3. Solution & Project Layout

```
src/
  Atlas.Projections/
    Atlas.Projections.csproj
    ProjectionExtensions.cs
    AtlasProjectionException.cs
    Internal/
      ProjectionPlanBuilder.cs
      ProjectionPlanCache.cs
      ProjectionValidator.cs
      ProjectionCompatibility.cs        ← shared "is binding projectable" predicate
      ProjectionDiagnostic.cs           ← internal record (1 row per incompatibility)

tests/
  Atlas.Projections.Tests/
    Atlas.Projections.Tests.csproj
    GlobalUsings.cs
    ProjectionValidatorTests.cs
    ProjectionPlanBuilderTests.cs
    ProjectionPlanCacheTests.cs
    ProjectionExtensionsTests.cs

  Atlas.Projections.Tests.EFCore/
    Atlas.Projections.Tests.EFCore.csproj
    GlobalUsings.cs
    ProjectionEFCoreTests.cs
    Fixtures/
      BlogContext.cs
      BlogModels.cs
```

### 3.1 `src/Atlas.Projections/Atlas.Projections.csproj`
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>Atlas.Projections</PackageId>
    <Description>LINQ-translatable projection (ProjectTo) for the Atlas mapper.</Description>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Atlas\Atlas.csproj" />
  </ItemGroup>
</Project>
```

### 3.2 Update `src/Atlas/Atlas.csproj`
Append `<InternalsVisibleTo Include="Atlas.Projections" />` and `<InternalsVisibleTo Include="Atlas.Projections.Tests" />`.

### 3.3 Update `Directory.Packages.props`
Add:
```xml
<PackageVersion Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.0" />
```
(Pin to `10.0.x` minor; `Atlas.Projections` itself takes no EF Core dependency. Only `Atlas.Projections.Tests.EFCore` references it.)

### 3.4 Update `Atlas.slnx`
Add the two new csproj entries alongside the existing four projects.

---

## 4. Public API Surface

```csharp
namespace Atlas.Projections;

/// <summary>
/// Translates a configured Atlas map into a LINQ expression and applies it as a Select.
/// Designed to be the last operator in an IQueryable chain — apply Where/OrderBy first.
/// </summary>
public static class ProjectionExtensions
{
    public static IQueryable<TDestination> ProjectTo<TDestination>(
        this IQueryable source,
        MapperConfiguration configuration,
        int maxDepth = 3);
}

/// <summary>
/// One entry in a projection-incompatibility report. <see cref="Member"/> is the destination
/// member name, or "(whole map)" when the entire pair is non-projectable.
/// </summary>
public sealed record ProjectionDiagnostic(
    Type SourceType,
    Type DestinationType,
    string Member,
    string Reason);

/// <summary>
/// Thrown when ProjectTo is asked to translate a configuration that contains constructs
/// the LINQ provider cannot handle. Aggregates every incompatibility for the requested
/// (TSource, TDestination) pair (including reachable nested pairs within maxDepth).
/// </summary>
public sealed class AtlasProjectionException : Exception
{
    public IReadOnlyList<ProjectionDiagnostic> Diagnostics { get; }

    public AtlasProjectionException(IReadOnlyList<ProjectionDiagnostic> diagnostics);
}
```

### 4.1 Behavior contracts on `ProjectTo`
- `source.ElementType` resolves `TSource`. Reflection looks up the cached `Func<IQueryable, MapperConfiguration, int, IQueryable>` open-generic by `(TSource, TDest)` once per pair.
- `maxDepth` must be `> 0`. Out-of-range throws `ArgumentOutOfRangeException`.
- `configuration` must be non-null. Null throws `ArgumentNullException`.
- If no map is registered for `(TSource, TDest)`: `AtlasProjectionException` with one diagnostic, `Member = "(no map registered)"`.
- If the requested pair (root or any reachable nested pair within `maxDepth`) uses a delegate-form converter or has unresolved bindings: `AtlasProjectionException` listing every problem.
- `ProjectTo` does **not** enumerate the source. It returns a wrapped `IQueryable<TDest>`; iteration semantics are entirely the provider's.

---

## 5. Internal Architecture

### 5.1 `ProjectionCompatibility`

A small static class consumed by **both** `ProjectionValidator` and `ProjectionPlanBuilder` so they can never disagree on what's projectable.

```csharp
internal static class ProjectionCompatibility
{
    /// <summary>
    /// True if the binding can be emitted as a projectable expression (no method call into
    /// the registry, no delegate invocation). False values produce a diagnostic.
    /// </summary>
    public static bool IsBindingProjectable(PropertyMap pm, out string? reason);

    /// <summary>
    /// True if a TypeMap as a whole can be projected. False when CustomConverter is set.
    /// </summary>
    public static bool IsTypeMapProjectable(TypeMap tm, out string? reason);
}
```

Both functions return false with a human-readable `reason` filled in (e.g. `"ConvertUsing(...) — delegate-form converter is in-memory only"`, `"unmapped — projection requires every destination binding resolved"`).

### 5.2 `ProjectionValidator` algorithm

The validator takes the `MapperRegistry` (accessed from the `MapperConfiguration` via the existing internal `Internal_Registry` slot) — not the configuration directly — because that's where `GetTypeMap` lives.

```
input: MapperRegistry registry, TypePair root, int maxDepth
output: throws AtlasProjectionException with all diagnostics, or returns silently

diagnostics = []
visited = HashSet<TypePair>()
Walk(root, depth=0)
if diagnostics.Count > 0: throw new AtlasProjectionException(diagnostics)

Walk(pair, depth):
  if depth >= maxDepth: return       # depth-limited; recursive member becomes default(T)
  if not visited.Add(pair): return   # already validated this pair on another branch

  tm = registry.GetTypeMap(pair)
  if tm is null:
    diagnostics.Add(pair, "(no map registered)", $"No map registered for {pair.Source.Name} -> {pair.Destination.Name}.")
    return

  if not IsTypeMapProjectable(tm, out var typeMapReason):
    diagnostics.Add(pair, "(whole map)", typeMapReason)
    return            # don't recurse into a black-box converter

  for each pm in tm.PropertyMaps where !pm.Ignored:
    if not IsBindingProjectable(pm, out var bindingReason):
      diagnostics.Add(pair, pm.Name, bindingReason)
      continue

    if pm.HasConstant or pm.CustomExpression is not null:
      continue                           # both are pure expression nodes

    if pm.SourcePath is null:
      diagnostics.Add(pair, pm.Name, "Unmapped — projection requires every destination binding resolved.")
      continue

    leaf = pm.SourcePath.Members[^1].PropertyType
    target = pm.DestinationType
    if leaf == target or target.IsAssignableFrom(leaf): continue
    if HasImplicitNumericConversion(leaf, target): continue

    if collection-pair:
      Walk(new TypePair(elementSrc, elementDst), depth + 1); continue
    if dictionary-pair:
      Walk(new TypePair(srcKey, dstKey), depth + 1)
      Walk(new TypePair(srcVal, dstVal), depth + 1); continue

    Walk(new TypePair(leaf, target), depth + 1)
```

The validator is **read-only** over the type-map graph. If it returns silently, the builder is guaranteed to succeed (modulo bugs the unit tests will catch).

### 5.3 `ProjectionPlanBuilder` algorithm

```
Build(typeMap, depth) -> Expression<Func<TSource, TDestination>>:
  srcParam = Expression.Parameter(typeMap.SourceType, "src")
  body = BuildBody(typeMap, srcParam, depth)
  return Expression.Lambda<Func<TSource, TDestination>>(body, srcParam)

BuildBody(typeMap, srcExpr, depth) -> Expression:
  ctor = ChooseCtor(typeMap)              # parameterless if available, else widest public
  ctorArgs = ctor.GetParameters().Select(p => BuildBinding(srcExpr, ctorParamMap[p.Name], depth, p.ParameterType))
  newExpr = Expression.New(ctor, ctorArgs)

  propBindings = typeMap.PropertyMaps
      .Where(pm => pm.DestinationProperty is not null && !pm.Ignored)
      .Select(pm => Expression.Bind(pm.DestinationProperty,
                                    BuildBinding(srcExpr, pm, depth, pm.DestinationProperty.PropertyType)))
      .ToList()

  return propBindings.Count > 0
    ? Expression.MemberInit(newExpr, propBindings)
    : (Expression)newExpr

BuildBinding(srcExpr, pm, depth, targetType) -> Expression:
  if pm.HasConstant:
    return Expression.Constant(pm.ConstantValue, targetType)

  if pm.CustomExpression is not null:
    return ParameterReplacer.Replace(
        pm.CustomExpression.Body,
        pm.CustomExpression.Parameters[0],
        srcExpr)

  pathExpr = BuildNullSafePath(srcExpr, pm.SourcePath.Members)

  if HasImplicitNumericConversion(pathExpr.Type, targetType):
    return Expression.Convert(pathExpr, targetType)

  if collection(pathExpr.Type) and collection(targetType):
    return BuildSelect(pathExpr, elementSrc, elementDst, depth + 1, targetType)

  if dictionary(...): return BuildDictionaryProjection(...)

  # Nested object: a registered TypeMap exists for (pathExpr.Type, targetType).
  # The validator already proved this; the builder looks up and inlines.
  nestedTypeMap = registry.GetTypeMap(new TypePair(pathExpr.Type, targetType))
  if nestedTypeMap is not null:
    return BuildNestedProjection(pathExpr, nestedTypeMap, depth + 1)

  return pathExpr      # identity/assignable

BuildNestedProjection(pathExpr, nestedTypeMap, depth):
  if depth >= maxDepth: return Expression.Default(nestedTypeMap.DestinationType)
  nestedParam = Expression.Parameter(nestedTypeMap.SourceType, "n")
  nestedBody = BuildBody(nestedTypeMap, nestedParam, depth)
  inlined = ParameterReplacer.Replace(nestedBody, nestedParam, pathExpr)
  if pathExpr.Type.IsClass:
    return Expression.Condition(
      Expression.ReferenceEqual(pathExpr, Expression.Constant(null, pathExpr.Type)),
      Expression.Default(nestedTypeMap.DestinationType),
      inlined)
  return inlined

BuildSelect(sourceExpr, srcElem, dstElem, depth, targetType):
  # Build the per-element projection lambda for the nested element TypeMap (recursive)
  elementMap = registry.GetTypeMap(new TypePair(srcElem, dstElem))   # validator guarantees non-null
  itemParam  = Expression.Parameter(srcElem, "i")
  itemBody   = BuildBody(elementMap, itemParam, depth)
  selector   = Expression.Lambda(itemBody, itemParam)

  # Use Queryable.Select for IQueryable sources; Enumerable.Select otherwise. Provider-rewriting
  # over IQueryable<T>.AsQueryable() gives the right node for either.
  selectCall = Expression.Call(
      typeof(Enumerable), "Select", [srcElem, dstElem],
      sourceExpr, selector)

  if targetType.IsArray:
    return Expression.Call(typeof(Enumerable), "ToArray", [dstElem], selectCall)
  if isListType(targetType):
    return Expression.Call(typeof(Enumerable), "ToList", [dstElem], selectCall)
  return selectCall    # IEnumerable<T> destination
```

#### 5.3.1 Critical invariants
- **No `MappingInvoker.Invoke` call appears anywhere in a built projection lambda.** Nested mapping is fully inlined. The unit test `Build_FlatPair_DoesNotContainMappingInvokerCall` is the load-bearing safety check.
- **Null-safety wraps each nested object access in `Conditional(ReferenceEqual ?: Default : inlined)`.** EF Core translates this to a `LEFT JOIN` with appropriate null-handling. Identical idiom to v1 `ExecutionPlanBuilder.BuildPathAccess`.
- **Collections produce `Enumerable.Select(...)` calls.** EF Core's expression visitor rewrites `Enumerable.*` to `Queryable.*` when the source is an `IQueryable`. This is the documented EF idiom; do not hand-build `Queryable.Select` calls (different generic-arity expectations across providers).

#### 5.3.2 Helper: `ParameterReplacer`
`ParameterReplacer` is a small `ExpressionVisitor` that swaps a `ParameterExpression` for an arbitrary replacement expression. v1 `ExecutionPlanBuilder` defines a private nested copy; the projections package gets its own (it's ~15 lines and lives in `Internal/`). Do not expose it publicly.

### 5.4 `ProjectionPlanCache`

```csharp
internal sealed class ProjectionPlanCache
{
    private readonly Dictionary<(TypePair, int), LambdaExpression> _cache = new();
    private readonly Lock _lock = new();

    public LambdaExpression GetOrBuild(
        TypePair pair, int maxDepth, Func<LambdaExpression> build)
    {
        lock (_lock)
        {
            var key = (pair, maxDepth);
            if (_cache.TryGetValue(key, out var existing)) return existing;
            var fresh = build();
            _cache[key] = fresh;
            return fresh;
        }
    }
}
```

Cache instances are bound to `MapperConfiguration` lifetime via:

```csharp
internal static class ProjectionPlanCacheRegistry
{
    private static readonly ConditionalWeakTable<MapperConfiguration, ProjectionPlanCache> _table = new();

    public static ProjectionPlanCache For(MapperConfiguration config) =>
        _table.GetValue(config, _ => new ProjectionPlanCache());
}
```

`ConditionalWeakTable` was chosen over a static `Dictionary` so that disposing an unused short-lived `MapperConfiguration` (anti-pattern, but possible) doesn't leak its cache. Per `MapperConfiguration` is the only useful key — projection lambdas are bound to a specific configuration's `TypeMap`s.

---

## 6. Validation timing

- `AtlasProjectionException` is thrown from `ProjectTo` **before** the wrapped `IQueryable<TDest>` is returned. The consumer never receives a query that would later blow up at translation.
- The validator + builder + cache run on the calling thread under the configuration's per-instance cache lock. Subsequent calls on the same `(TypePair, maxDepth)` skip validation entirely (cached `Expression` reuse).
- The validator does **not** modify any v1 state. It is purely read-side over the existing `MapperConfiguration` graph.

---

## 7. TDD Plan

The implementer writes each test failing first, then the minimum production code to make it pass, in file order. Test counts are floors; add edge cases as you encounter them.

### 7.1 `ProjectionCompatibilityTests.cs` (~6 tests)
1. `IsTypeMapProjectable_NoCustomConverter_ReturnsTrue`
2. `IsTypeMapProjectable_CustomConverter_ReturnsFalseWithReason`
3. `IsBindingProjectable_Constant_ReturnsTrue`
4. `IsBindingProjectable_CustomExpression_ReturnsTrue`
5. `IsBindingProjectable_SourcePath_ReturnsTrue`
6. `IsBindingProjectable_Ignored_ReturnsTrue` (Ignore is fine; the binding is just omitted)

### 7.2 `ProjectionValidatorTests.cs` (~10 tests)
1. `Validate_FullyMappedSimplePair_ReturnsSilently`
2. `Validate_NoMapRegistered_ReportsRootMissing`
3. `Validate_CustomConverter_ReportsWholeMapIncompatible`
4. `Validate_NestedCustomConverter_ReportsNestedMap_NotRoot`
5. `Validate_UnresolvedDestinationMember_ReportsMember`
6. `Validate_IgnoredMember_DoesNotReport`
7. `Validate_NumericWidening_PassesValidation`
8. `Validate_NestedObjectWithMissingMap_ReportsNestedTypePair`
9. `Validate_RecursiveCycle_StopsAtMaxDepth_DoesNotInfiniteLoop`
10. `Validate_AggregatesAllErrors_NotJustFirst`

### 7.3 `ProjectionPlanBuilderTests.cs` (~12 tests)
Whitebox tests asserting the **shape** of the emitted expression, not just its result.

1. `Build_FlatPair_EmitsMemberInitWithBindings`
2. `Build_FlatPair_DoesNotContainMappingInvokerCall` *(load-bearing)*
3. `Build_NestedObject_InlinesNestedMemberInit`
4. `Build_NestedClassMember_WrapsInNullSafeConditional`
5. `Build_NumericWidening_EmitsConvert`
6. `Build_ConstantBinding_EmitsConstantNode`
7. `Build_CustomExpression_RebindsParameter`
8. `Build_CollectionMember_EmitsSelectOverElementProjection`
9. `Build_DepthLimit_RecursiveMember_EmitsDefault`
10. `Build_IgnoredMember_OmitsBinding`
11. `Build_RecordCtor_UsesNewExpressionWithCtorArgs`
12. `Build_LambdaParameterCount_IsExactlyOne`

Use a small `ExpressionVisitor` helper (`AssertExpression.Contains<TNode>(...)`, `AssertExpression.DoesNotCallMethod(...)`) to make these assertions readable.

### 7.4 `ProjectionPlanCacheTests.cs` (~4 tests)
1. `GetOrBuild_FirstCall_InvokesBuilder`
2. `GetOrBuild_SecondCallSameKey_ReturnsCachedAndDoesNotInvokeBuilder`
3. `GetOrBuild_DifferentMaxDepth_BuildsSeparately`
4. `GetOrBuild_ConcurrentCalls_BuildsOnce` (Parallel.For 200, builder called once)

### 7.5 `ProjectionExtensionsTests.cs` (~10 tests, in-memory `IQueryable`)
End-to-end behavior over `IEnumerable<T>.AsQueryable()`. Public-API surface tests.

1. `ProjectTo_FlatPair_ReturnsMappedItems`
2. `ProjectTo_NestedObject_PopulatesNestedMembersCorrectly`
3. `ProjectTo_NullNestedSource_ReturnsDefaultDestination_NoNRE`
4. `ProjectTo_Collection_MappedItemsInOrder`
5. `ProjectTo_FilteredQueryThenProjectTo_ReturnsFilteredResults`
6. `ProjectTo_TypeConverterPair_Throws_WithDiagnostic`
7. `ProjectTo_MissingMap_Throws_WithDiagnosticListing`
8. `ProjectTo_DepthLimit_TruncatesRecursiveMember_AtMaxDepth`
9. `ProjectTo_DefaultMaxDepth_IsThree`
10. `ProjectTo_MaxDepthZero_ThrowsArgumentOutOfRange`

### 7.6 `ProjectionEFCoreTests.cs` (~8 tests, in `Atlas.Projections.Tests.EFCore`)
EF Core SQLite in-memory. Wire up a minimal `BlogContext` with `Blog { Id, Title, Posts }` and `Post { Id, Body, BlogId }`. Tests assert generated SQL via `query.ToQueryString()`.

1. `EFCore_FlatProjection_EmitsSingleSelect_NoFullEntityHydration`
2. `EFCore_NestedProjection_EmitsLeftJoin_NotN1Queries`
3. `EFCore_CollectionProjection_EmitsSingleQuery`
4. `EFCore_FilterBeforeProjectTo_FilterPushesDown` (assert `WHERE` appears in SQL)
5. `EFCore_ProjectionRoundtrip_ReturnsExpectedRows` (data correctness)
6. `EFCore_NumericWidening_TranslatesToProvider` (`int` → `long`)
7. `EFCore_NullableSourceMember_TranslatesToNullCoalesce`
8. `EFCore_RecursiveMap_DepthLimitTerminatesQuery`

SQL assertions are on **column count and column-name presence**, not exact whitespace, to survive minor EF Core version bumps.

**Total: ~50 tests across 6 files** (5 in `Atlas.Projections.Tests`, 1 in `Atlas.Projections.Tests.EFCore`).

---

## 8. Coverage Targets

| Project | Line | Branch |
|---|---|---|
| `Atlas.Projections` | ≥ 90% | ≥ 80% |

Branch coverage is set lower than the v1 line-coverage gate because `BuildBinding` is structurally exhaustive — each arm is hit by a dedicated `ProjectionPlanBuilderTests` case in §7.3. The same caveat applies as v1: `HasImplicitNumericConversion` switch arms are not individually tested.

Run with:
```
dotnet test tests/Atlas.Projections.Tests/Atlas.Projections.Tests.csproj --collect:"XPlat Code Coverage"
dotnet test tests/Atlas.Projections.Tests.EFCore/Atlas.Projections.Tests.EFCore.csproj --collect:"XPlat Code Coverage"
reportgenerator -reports:tests/**/coverage.cobertura.xml -targetdir:coverage -reporttypes:TextSummary
```

---

## 9. Risks & Open Questions

A short list the implementing session should push back on if a better path surfaces.

1. **EF Core SQL assertion brittleness.** Tests in §7.6 risk false-failing across EF minor versions. Assert column count + name presence, not whitespace; pin EF Core to a specific minor in `Directory.Packages.props`.
2. **Null-safety divergence between LINQ-to-Objects and EF Core.** In-memory tests pass with `Conditional(ReferenceEqual…)`; EF Core may rewrite differently for nullable navigation properties. Both should produce semantically equivalent results, but `null`-vs-default for value-type members in nested DTOs is a known sharp edge. Run §7.6 #7 early.
3. **`source.ElementType` resolution sharp edges.** A consumer with a covariant `IQueryable` element type or a non-generic interface base may surprise the type lookup. Validate `source.ElementType` is a concrete type at the call site; throw `ArgumentException` if it's `object`-like.
4. **`ConditionalWeakTable` cache lifetime.** Bound to `MapperConfiguration` instance lifetime. A consumer that builds many short-lived configs will see no caching effect. Already documented as anti-pattern in v1's README; no extra mitigation needed.
5. **`Expression.Default(T)` at `maxDepth` boundary for value types.** The recursive member becomes `default(struct)` / `0`. EF Core translates this to `NULL` / a literal. Confirm via §7.6 #8 that this round-trips correctly for non-nullable struct destinations.
6. **`Enumerable.Select` vs `Queryable.Select`.** Per §5.3, emit `Enumerable.Select` and rely on EF Core's `IQueryable` rewriter. If a non-EF provider doesn't perform that rewrite, swap to `Queryable.Select` — but defer that change until a concrete provider asks for it.

---

## 10. Appendix A — Worked Example

Given:
```csharp
public class BlogProfile : MapperProfile
{
    public BlogProfile()
    {
        CreateMap<Blog, BlogDto>();
        CreateMap<Post, PostDto>();
    }
}

public class Blog { public int Id { get; set; } public string Title { get; set; } = ""; public List<Post> Posts { get; set; } = new(); }
public class Post { public int Id { get; set; } public string Body { get; set; } = ""; }
public class BlogDto { public int Id { get; set; } public string Title { get; set; } = ""; public List<PostDto> Posts { get; set; } = new(); }
public class PostDto { public int Id { get; set; } public string Body { get; set; } = ""; }
```

Calling `db.Blogs.Where(b => b.Id > 5).ProjectTo<BlogDto>(config)` (where `db` is an EF Core `DbContext` and `db.Blogs` is `DbSet<Blog>`) emits the projection lambda:

```csharp
src => new BlogDto
{
    Id    = src.Id,
    Title = src.Title,
    Posts = src.Posts.Select(i => new PostDto { Id = i.Id, Body = i.Body }).ToList()
}
```

EF Core translates this to a single SQL query along the lines of:
```sql
SELECT b.Id, b.Title, p.Id, p.Body
FROM Blogs AS b
LEFT JOIN Posts AS p ON p.BlogId = b.Id
WHERE b.Id > 5
ORDER BY b.Id, p.Id
```
— one query, only the columns in `BlogDto` / `PostDto`, no `Include()` needed.

---

## 11. Implementation Checklist

A future Claude session can execute this top-to-bottom.

- [ ] Create `src/Atlas.Projections/` project. Append `InternalsVisibleTo` entries to `Atlas.csproj`.
- [ ] Create `tests/Atlas.Projections.Tests/` project. Wire xUnit v3 + coverlet, mirror existing test csproj shape.
- [ ] Create `tests/Atlas.Projections.Tests.EFCore/` project. Reference EF Core SQLite.
- [ ] Add both new projects + EF Core SQLite package version to `Directory.Packages.props` and `Atlas.slnx`.
- [ ] Implement §7.1 tests, then `ProjectionCompatibility`. Green.
- [ ] Implement §7.2 tests, then `ProjectionValidator`. Green.
- [ ] Implement §7.3 tests, then `ProjectionPlanBuilder`. Green.
- [ ] Implement §7.4 tests, then `ProjectionPlanCache` + `ProjectionPlanCacheRegistry`. Green.
- [ ] Implement §7.5 tests, then `ProjectionExtensions.ProjectTo<T>`. Green.
- [ ] Implement §7.6 tests, then any minor adjustments to the builder needed for EF Core translation. Green.
- [ ] Run coverage; verify §8 targets.
- [ ] Update root `README.md` with a short "Atlas.Projections" section linking to this doc.
- [ ] Mark `Atlas v2 design docs deferred` memory: ProjectTo is now shipped; the remaining 12 deferred features are still pending.
