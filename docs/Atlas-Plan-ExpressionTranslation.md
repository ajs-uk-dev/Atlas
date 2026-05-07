# Atlas v2 Expression Translation (UseAsDataSource) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship Atlas v2 feature #13 — expression translation as a parallel front-end to `Atlas.Projections.ProjectTo`. Wrap an `IQueryable<TSource>` and write filtering, sorting, and paging in destination-DTO terms. Atlas translates the destination-typed lambdas back to source-typed expressions before they hit the LINQ provider. Mirrors AutoMapper's `UseAsDataSource(cfg).For<TDto>()` UX. The thirteenth and final v2 deferred feature.

**Architecture:** Two layers in `Atlas.Projections`. Engine (`Atlas.Projections.Internal.ExpressionTranslator`) walks `Expression<Func<TDest, TResult>>` and produces `Expression<Func<TSrc, TResult>>` by substituting destination-typed member accesses with source expressions Atlas's typemaps already record (`PropertyMap.SourcePath`, `PropertyMap.CustomExpression`). Wrapper (`Atlas.Projections.UseAsDataSourceExtensions` + internal `UseAsDataSourceQueryable<TSrc, TDst>`) intercepts predicate/ordering/paging operators and applies translated lambdas to the underlying `IQueryable<TSrc>`. Enumeration delegates to existing `ProjectTo<TDest>` for the final source→destination shape. Cached per `(TypePair, lambda-reference-identity)` using PR #11's `RefEqComparer` pattern via `ConditionalWeakTable<MapperConfiguration, TranslationPlanCache>`.

**Tech Stack:** C# 14 preview, `System.Linq.Expressions`, `System.Runtime.CompilerServices.RuntimeHelpers` + `ConditionalWeakTable`, xUnit v3 (plain `Assert.X()` only — NO FluentAssertions per project convention).

**Branch:** `feat/expression-translation`, cut from `main` HEAD `454d2ac` (the design commit for #13).

**Reference design:** `C:\Repos\Atlas\docs\Atlas-Design-ExpressionTranslation.md` — primary spec. All section references (e.g., "design §5.4") point at it.

---

## File Map

### New files (production)

- `C:\Repos\Atlas\src\Atlas.Projections\Internal\ExpressionTranslator.cs` — internal static class with `Translate(MapperRegistry, TypePair, LambdaExpression) → LambdaExpression` plus a private nested `MemberAccessRewriter : ExpressionVisitor` that walks the destination lambda and rewrites destination-member spines.
- `C:\Repos\Atlas\src\Atlas.Projections\Internal\TranslationPlanCache.cs` — internal sealed class + internal static `TranslationPlanCacheRegistry` that binds a cache instance per `MapperConfiguration` via `ConditionalWeakTable`.
- `C:\Repos\Atlas\src\Atlas.Projections\IUseAsDataSource.cs` — public interface with single `For<TDestination>()` method.
- `C:\Repos\Atlas\src\Atlas.Projections\IUseAsDataSourceQueryable.cs` — public interface with the destination-typed LINQ-operator surface (Where/OrderBy/Skip/Take/terminal-predicate operators/AsQueryable). Inherits `IEnumerable<TDestination>`.
- `C:\Repos\Atlas\src\Atlas.Projections\IUseAsDataSourceOrdered.cs` — public interface inheriting `IUseAsDataSourceQueryable` with `ThenBy`/`ThenByDescending`.
- `C:\Repos\Atlas\src\Atlas.Projections\Internal\UseAsDataSourceQueryable.cs` — internal sealed class implementing `IUseAsDataSourceOrdered<TSource, TDestination>` (which inherits `IUseAsDataSourceQueryable<,>`).
- `C:\Repos\Atlas\src\Atlas.Projections\UseAsDataSourceExtensions.cs` — public static `UseAsDataSource<TSource>(this IQueryable<TSource>, MapperConfiguration)` extension entry point.
- `C:\Repos\Atlas\src\Atlas.Projections\MapperConfigurationExpressionTranslationExtensions.cs` — public static `Translate<TSource, TDestination, TResult>` extension on `MapperConfiguration`.

### Modified files (production)

None. The feature is additive — no changes to `Atlas` core, `Atlas.Extensions.DependencyInjection`, or existing `Atlas.Projections` files.

### New files (tests)

- `C:\Repos\Atlas\tests\Atlas.Projections.Tests\ExpressionTranslatorTests.cs` — engine unit tests (~14 tests).
- `C:\Repos\Atlas\tests\Atlas.Projections.Tests\ExpressionTranslatorRejectionTests.cs` — Phase 2 + Phase 3 rejection tests (~10 tests).
- `C:\Repos\Atlas\tests\Atlas.Projections.Tests\UseAsDataSourceWrapperTests.cs` — wrapper-class behavior tests (~12 tests).
- `C:\Repos\Atlas\tests\Atlas.Projections.Tests\UseAsDataSourceCacheTests.cs` — translation cache tests (~6 tests).
- `C:\Repos\Atlas\tests\Atlas.Projections.Tests\UseAsDataSourceCompatibilityTests.cs` — interaction with other v2 features (~10 tests).
- `C:\Repos\Atlas\tests\Atlas.Projections.Tests\UseAsDataSourceIntegrationTests.cs` — end-to-end DI + multi-op scenarios (~6 tests).
- `C:\Repos\Atlas\tests\Atlas.Projections.Tests.EFCore\UseAsDataSourceEFCoreTests.cs` — SQL-emission tests using EF Core in-memory provider (~8 tests).

### Modified files (docs)

- `C:\Repos\Atlas\README.md` — add "Expression translation (UseAsDataSource)" section.

### Test count delta target

Baseline from PR #12: **710 PASS** (618 Atlas.Tests + 78 Projections + 14 EFCore).

After this feature: **~776 PASS** (≈ +66 net):
- +14 in `ExpressionTranslatorTests`
- +10 in `ExpressionTranslatorRejectionTests`
- +12 in `UseAsDataSourceWrapperTests`
- +6 in `UseAsDataSourceCacheTests`
- +10 in `UseAsDataSourceCompatibilityTests`
- +6 in `UseAsDataSourceIntegrationTests`
- +8 in `UseAsDataSourceEFCoreTests`

Per-feature plan-arithmetic-drift discipline (memory feedback): the implementer's actual count is authoritative; treat ≈66 as approximate.

### Key API discovery (verified via codebase inspection before plan finalized)

**Important:** `AtlasProjectionException` constructor takes `IReadOnlyList<ProjectionDiagnostic>` — NOT a string. The design doc's `throw new AtlasProjectionException("...string...")` form must be adapted to construct a `ProjectionDiagnostic` (defined in `src/Atlas.Projections/AtlasProjectionException.cs`):

```csharp
public sealed record ProjectionDiagnostic(
    Type SourceType,
    Type DestinationType,
    string Member,
    string Reason);
```

Every rejection site uses a small helper:

```csharp
private static AtlasProjectionException Reject(Type srcType, Type dstType, string member, string reason) =>
    new AtlasProjectionException(new[] {
        new ProjectionDiagnostic(srcType, dstType, member, "UseAsDataSource translation: " + reason)
    });
```

The `"UseAsDataSource translation: "` prefix per design §6 carries through.

`ProjectionCompatibility.IsTypeMapProjectable(TypeMap, out string? reason)` is the existing dual-gate; the translator wraps the `reason` string in a `ProjectionDiagnostic` for the throw.

---

## Task 0 — Branch setup

**Files:** none (controller-only operation).

- [ ] **Step 0.1: Verify clean state on `main`**

```pwsh
cd C:\Repos\Atlas
git status
git log --oneline -3
```

Expected: working tree clean; HEAD at `454d2ac` ("Atlas v2 #13 design: Expression Translation (UseAsDataSource)") or further if subsequent commits land.

- [ ] **Step 0.2: Cut feature branch**

```pwsh
git checkout -b feat/expression-translation
```

Expected: switched to a new branch `feat/expression-translation`.

- [ ] **Step 0.3: Confirm baseline test count**

```pwsh
dotnet test --nologo 2>&1 | Select-String -Pattern "Passed:|Failed:|Skipped:"
```

Expected: total `Passed: 710, Failed: 0, Skipped: 0` across the three test projects (Atlas.Tests + Atlas.Projections.Tests + Atlas.Projections.Tests.EFCore).

If the count differs, investigate before proceeding.

---

## Task 1 — `ExpressionTranslator` engine skeleton (Phase 1 + Phase 2 entry guards)

**Goal:** Stand up the engine entry point with the two non-visitor validation phases. Phase 1 (pair-not-registered) and Phase 2 (existing `ProjectionCompatibility.IsTypeMapProjectable` dual-gate) reject before any visitor work runs. The actual visitor descent is a stub — fully implemented across Tasks 2-6.

**Files:**
- Create: `C:\Repos\Atlas\src\Atlas.Projections\Internal\ExpressionTranslator.cs`
- Create: `C:\Repos\Atlas\tests\Atlas.Projections.Tests\ExpressionTranslatorRejectionTests.cs`

**Allowlist for the implementer subagent:** the two files above, no others.

- [ ] **Step 1.1: Write failing tests for Phase 1 + Phase 2 rejections**

Contents of `tests/Atlas.Projections.Tests/ExpressionTranslatorRejectionTests.cs`:

```csharp
using System.Linq.Expressions;
using Atlas;
using Atlas.Internal;
using Atlas.Projections.Internal;

namespace Atlas.Projections.Tests;

public class ExpressionTranslatorRejectionTests
{
    [Fact]
    public void PairNotRegistered_ThrowsAtlasProjectionException_AtTranslate()
    {
        var cfg = new MapperConfiguration(_ => { /* no maps */ });
        Expression<Func<UEDS_RejectDtoA, bool>> predicate = d => d.Id == 1;

        var ex = Assert.Throws<AtlasProjectionException>(() =>
            ExpressionTranslator.Translate(
                cfg.Internal_Registry,
                new TypePair(typeof(UEDS_RejectSrcA), typeof(UEDS_RejectDtoA)),
                predicate));

        Assert.Contains(ex.Diagnostics, d => d.Reason.Contains("UseAsDataSource translation:")
                                          && d.Reason.Contains("no map registered"));
    }

    [Fact]
    public void TypeMapWithHooks_ThrowsAtlasProjectionException_AtTranslate()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap<UEDS_RejectSrcHooks, UEDS_RejectDtoHooks>()
                .BeforeMap((s, d) => { });
        });
        Expression<Func<UEDS_RejectDtoHooks, bool>> predicate = d => d.Id == 1;

        var ex = Assert.Throws<AtlasProjectionException>(() =>
            ExpressionTranslator.Translate(
                cfg.Internal_Registry,
                new TypePair(typeof(UEDS_RejectSrcHooks), typeof(UEDS_RejectDtoHooks)),
                predicate));

        Assert.Contains(ex.Diagnostics, d => d.Reason.Contains("hook"));
    }

    [Fact]
    public void TypeMapWithPreserveReferences_ThrowsAtlasProjectionException_AtTranslate()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap<UEDS_RejectSrcPR, UEDS_RejectDtoPR>().PreserveReferences();
        });
        Expression<Func<UEDS_RejectDtoPR, bool>> predicate = d => d.Id == 1;

        var ex = Assert.Throws<AtlasProjectionException>(() =>
            ExpressionTranslator.Translate(
                cfg.Internal_Registry,
                new TypePair(typeof(UEDS_RejectSrcPR), typeof(UEDS_RejectDtoPR)),
                predicate));

        Assert.Contains(ex.Diagnostics, d => d.Reason.Contains("PreserveReferences"));
    }
}

public class UEDS_RejectSrcA { public int Id { get; set; } }
public class UEDS_RejectDtoA { public int Id { get; set; } }

public class UEDS_RejectSrcHooks { public int Id { get; set; } }
public class UEDS_RejectDtoHooks { public int Id { get; set; } }

public class UEDS_RejectSrcPR { public int Id { get; set; } public UEDS_RejectSrcPR? Self { get; set; } }
public class UEDS_RejectDtoPR { public int Id { get; set; } public UEDS_RejectDtoPR? Self { get; set; } }
```

**Fixture-naming note:** all use-as-data-source test fixtures use the `UEDS_` prefix throughout to avoid collision with other tests' fixtures in the same assembly. Future tasks add more `UEDS_*` fixtures; reuse this prefix.

- [ ] **Step 1.2: Run tests to verify they fail (compile error)**

```pwsh
cd C:\Repos\Atlas
dotnet test --nologo --filter "FullyQualifiedName~ExpressionTranslatorRejectionTests"
```

Expected: build error referencing missing type `Atlas.Projections.Internal.ExpressionTranslator`.

- [ ] **Step 1.3: Create `ExpressionTranslator.cs` skeleton with Phase 1 + Phase 2**

Contents of `src/Atlas.Projections/Internal/ExpressionTranslator.cs`:

```csharp
using System.Linq.Expressions;
using Atlas.Internal;

namespace Atlas.Projections.Internal;

/// <summary>
/// Walks a destination-typed lambda and produces a source-typed lambda by substituting
/// destination-member accesses with the source expressions Atlas's typemaps record
/// (<see cref="PropertyMap.SourcePath"/>, <see cref="PropertyMap.CustomExpression"/>).
/// See <c>docs/Atlas-Design-ExpressionTranslation.md</c> §4.1 / §5.
/// </summary>
internal static class ExpressionTranslator
{
    /// <summary>
    /// Top-level entry point. Validates pair registration (Phase 1) and projection
    /// compatibility (Phase 2), then descends via the visitor (Phase 3).
    /// </summary>
    public static LambdaExpression Translate(
        MapperRegistry registry,
        TypePair root,
        LambdaExpression destinationLambda)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(destinationLambda);

        // Phase 1: pair registration check.
        var rootTm = registry.GetTypeMap(root);
        if (rootTm is null)
            throw Reject(root.Source, root.Destination, "(translate)",
                $"no map registered for {root.Source.Name} → {root.Destination.Name}. " +
                "UseAsDataSource requires a registered map for the (source, destination) pair.");

        // Phase 2: projection compatibility dual-gate (existing).
        if (!ProjectionCompatibility.IsTypeMapProjectable(rootTm, out var reason))
            throw Reject(root.Source, root.Destination, "(translate)", reason!);

        // Phase 3: visitor descent. Filled in across Tasks 2-6.
        // For Task 1, return the input unchanged so the rejection-only tests pass.
        // Tasks 2-6 replace this with the visitor invocation.
        return destinationLambda;
    }

    /// <summary>
    /// Constructs a single-diagnostic <see cref="AtlasProjectionException"/> with the
    /// "UseAsDataSource translation: " prefix per design §7.
    /// </summary>
    private static AtlasProjectionException Reject(
        Type srcType, Type dstType, string member, string reason) =>
        new AtlasProjectionException(new[]
        {
            new ProjectionDiagnostic(srcType, dstType, member,
                "UseAsDataSource translation: " + reason)
        });
}
```

- [ ] **Step 1.4: Run tests to verify they pass**

```pwsh
cd C:\Repos\Atlas
dotnet test --nologo --filter "FullyQualifiedName~ExpressionTranslatorRejectionTests"
```

Expected: 3 tests pass.

- [ ] **Step 1.5: Run full suite — zero regressions**

```pwsh
cd C:\Repos\Atlas
dotnet test --nologo 2>&1 | Select-String -Pattern "Passed:|Failed:|Skipped:"
```

Expected: 3 new tests added; total = 710 + 3 = 713. Failed = 0.

- [ ] **Step 1.6: Commit**

```pwsh
git add src/Atlas.Projections/Internal/ExpressionTranslator.cs tests/Atlas.Projections.Tests/ExpressionTranslatorRejectionTests.cs
git commit -m "ExpressionTranslator skeleton + Phase 1/2 entry guards (Task 1)`n`nNew internal Atlas.Projections.Internal.ExpressionTranslator with Translate`nentry point. Phase 1 (pair-not-registered) and Phase 2 (existing`nProjectionCompatibility dual-gate for hooks/PreserveReferences/dynamic/`nForPath) reject before any visitor work. Phase 3 visitor descent is a stub`nreturning the input lambda unchanged — fully implemented across Tasks 2-6.`nReject helper centralizes the 'UseAsDataSource translation: ' prefix per`ndesign §7."
```

---

## Task 2 — Engine visitor: flat single-member translation + missing-PropertyMap rejection

**Goal:** Replace the Task 1 stub with a visitor that handles the simplest case — a single destination-member access (e.g., `d => d.Total > 100` where `Total` is convention-mapped 1:1). Adds Phase 3 rejection for "PropertyMap not found" (member doesn't exist on the typemap).

**Files:**
- Modify: `C:\Repos\Atlas\src\Atlas.Projections\Internal\ExpressionTranslator.cs`
- Create: `C:\Repos\Atlas\tests\Atlas.Projections.Tests\ExpressionTranslatorTests.cs`
- Modify: `C:\Repos\Atlas\tests\Atlas.Projections.Tests\ExpressionTranslatorRejectionTests.cs` (add member-not-found test)

**Allowlist for the implementer subagent:** the three files above.

- [ ] **Step 2.1: Write failing engine tests for flat translation**

Create `tests/Atlas.Projections.Tests/ExpressionTranslatorTests.cs`:

```csharp
using System.Linq.Expressions;
using Atlas;
using Atlas.Internal;
using Atlas.Projections.Internal;

namespace Atlas.Projections.Tests;

public class ExpressionTranslatorTests
{
    [Fact]
    public void FlatPropertyTranslation_RewritesParameter()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<UEDS_FlatSrc, UEDS_FlatDto>());
        Expression<Func<UEDS_FlatDto, bool>> predicate = d => d.Total > 100m;

        var translated = (Expression<Func<UEDS_FlatSrc, bool>>)ExpressionTranslator.Translate(
            cfg.Internal_Registry,
            new TypePair(typeof(UEDS_FlatSrc), typeof(UEDS_FlatDto)),
            predicate);

        // Compile + run against an in-memory instance to verify behavior.
        var compiled = translated.Compile();
        Assert.True(compiled(new UEDS_FlatSrc { Total = 150m }));
        Assert.False(compiled(new UEDS_FlatSrc { Total = 50m }));
    }

    [Fact]
    public void FlatPropertyTranslation_ProducesCorrectExpressionShape()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<UEDS_FlatSrc, UEDS_FlatDto>());
        Expression<Func<UEDS_FlatDto, bool>> predicate = d => d.Total > 100m;

        var translated = ExpressionTranslator.Translate(
            cfg.Internal_Registry,
            new TypePair(typeof(UEDS_FlatSrc), typeof(UEDS_FlatDto)),
            predicate);

        // Source-typed lambda
        Assert.Equal(typeof(UEDS_FlatSrc), translated.Parameters[0].Type);
        Assert.Equal(typeof(bool), translated.ReturnType);
    }
}

public class UEDS_FlatSrc
{
    public int Id { get; set; }
    public decimal Total { get; set; }
}

public class UEDS_FlatDto
{
    public int Id { get; set; }
    public decimal Total { get; set; }
}
```

Append to `ExpressionTranslatorRejectionTests.cs`:

```csharp
public class ExpressionTranslatorMemberRejectionTests
{
    [Fact]
    public void MemberNotFound_ThrowsAtlasProjectionException()
    {
        // Configure a map but reference a destination member that doesn't have a PropertyMap.
        // The simplest case: a destination DTO whose property Atlas's convention engine
        // can't resolve to the source — declare an extra DTO property no source has.
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<UEDS_MissingMemberSrc, UEDS_MissingMemberDto>(MemberList.None));
        Expression<Func<UEDS_MissingMemberDto, bool>> predicate = d => d.PhantomMember == "x";

        var ex = Assert.Throws<AtlasProjectionException>(() =>
            ExpressionTranslator.Translate(
                cfg.Internal_Registry,
                new TypePair(typeof(UEDS_MissingMemberSrc), typeof(UEDS_MissingMemberDto)),
                predicate));

        Assert.Contains(ex.Diagnostics, d => d.Reason.Contains("PhantomMember")
                                          && d.Reason.Contains("no PropertyMap"));
    }
}

public class UEDS_MissingMemberSrc { public int Id { get; set; } }
public class UEDS_MissingMemberDto { public int Id { get; set; } public string PhantomMember { get; set; } = ""; }
```

- [ ] **Step 2.2: Run tests to verify they fail**

```pwsh
cd C:\Repos\Atlas
dotnet test --nologo --filter "FullyQualifiedName~ExpressionTranslatorTests|FullyQualifiedName~ExpressionTranslatorMemberRejectionTests"
```

Expected: tests fail because the visitor stub returns the input lambda unchanged (so `translated.Parameters[0].Type` is `UEDS_FlatDto`, not `UEDS_FlatSrc`).

- [ ] **Step 2.3: Replace the Task 1 stub with the visitor**

Update `ExpressionTranslator.cs`. Replace the body of `Translate` from:

```csharp
        // Phase 3: visitor descent. Filled in across Tasks 2-6.
        // For Task 1, return the input unchanged so the rejection-only tests pass.
        return destinationLambda;
```

to:

```csharp
        // Phase 3: visitor descent.
        var destParam = destinationLambda.Parameters[0];
        var srcParam = Expression.Parameter(root.Source, "src");
        var visitor = new MemberAccessRewriter(registry, destParam, srcParam, root);

        var rewrittenBody = visitor.Visit(destinationLambda.Body);

        var funcType = typeof(Func<,>).MakeGenericType(root.Source, destinationLambda.ReturnType);
        return Expression.Lambda(funcType, rewrittenBody, srcParam);
```

Add the visitor as a private nested class at the bottom of `ExpressionTranslator`:

```csharp
    private sealed class MemberAccessRewriter : ExpressionVisitor
    {
        private readonly MapperRegistry _registry;
        private readonly ParameterExpression _destParam;
        private readonly ParameterExpression _srcParam;
        private readonly TypePair _rootPair;

        public MemberAccessRewriter(
            MapperRegistry registry,
            ParameterExpression destParam,
            ParameterExpression srcParam,
            TypePair rootPair)
        {
            _registry = registry;
            _destParam = destParam;
            _srcParam = srcParam;
            _rootPair = rootPair;
        }

        protected override Expression VisitParameter(ParameterExpression node) =>
            node == _destParam ? _srcParam : base.VisitParameter(node);

        protected override Expression VisitMember(MemberExpression node)
        {
            // Walk the spine: chain of MemberExpressions rooted at a single Expression.
            // For Task 2 we handle the flat single-member case (length-1 spine rooted at _destParam).
            if (node.Expression is ParameterExpression p && p == _destParam)
            {
                // Single-member spine: d.X
                var pm = LookupPropertyMap(_rootPair, node.Member.Name);
                return BuildSourceExpression(pm, _srcParam);
            }

            // Spine root is not _destParam (closure access, etc.) — pass through.
            return base.VisitMember(node);
        }

        private PropertyMap LookupPropertyMap(TypePair pair, string memberName)
        {
            var tm = _registry.GetTypeMap(pair)
                ?? throw Reject(pair.Source, pair.Destination, memberName,
                    $"destination chain references nested map ({pair.Source.Name} → {pair.Destination.Name}) which is not registered.");

            var pm = tm.PropertyMaps.FirstOrDefault(p =>
                string.Equals(p.Name, memberName, StringComparison.Ordinal));

            if (pm is null)
                throw Reject(pair.Source, pair.Destination, memberName,
                    $"destination member '{pair.Destination.Name}.{memberName}' has no PropertyMap. " +
                    "Use UseAsDataSource only with members that have a configured source.");

            return pm;
        }

        private Expression BuildSourceExpression(PropertyMap pm, Expression currentSrcExpr)
        {
            // Task 2 supports SourcePath only. Multi-segment paths, CustomExpression, recursive
            // nesting, and rejection rules for Ignored/HasConstant/unmapped land in Tasks 3-5.
            if (pm.SourcePath is null)
                throw new NotImplementedException("Filled in Tasks 3-5.");

            // Single-segment path — chain a single MemberAccess on the source parameter.
            return Expression.MakeMemberAccess(currentSrcExpr, pm.SourcePath.Members[0]);
        }
    }
```

The translator file now contains:
1. Reject helper (Task 1)
2. Translate entry point with Phase 1/2 + new visitor invocation (Task 1 + new)
3. MemberAccessRewriter visitor (new)

**Note on `Reject`:** the helper is currently `private static` on `ExpressionTranslator`, so the visitor can call it (nested classes have access to enclosing type's privates).

- [ ] **Step 2.4: Run tests to verify they pass**

```pwsh
cd C:\Repos\Atlas
dotnet test --nologo --filter "FullyQualifiedName~ExpressionTranslatorTests|FullyQualifiedName~ExpressionTranslatorMemberRejectionTests"
```

Expected: 3 tests pass (2 in `ExpressionTranslatorTests` + 1 in `ExpressionTranslatorMemberRejectionTests`).

- [ ] **Step 2.5: Run full suite — zero regressions**

```pwsh
cd C:\Repos\Atlas
dotnet test --nologo 2>&1 | Select-String -Pattern "Passed:|Failed:|Skipped:"
```

Expected: total = 713 (Task 1) + 3 = 716. Failed = 0.

- [ ] **Step 2.6: Commit**

```pwsh
git add src/Atlas.Projections/Internal/ExpressionTranslator.cs tests/Atlas.Projections.Tests/ExpressionTranslatorTests.cs tests/Atlas.Projections.Tests/ExpressionTranslatorRejectionTests.cs
git commit -m "ExpressionTranslator visitor: flat single-member case (Task 2)`n`nMemberAccessRewriter ExpressionVisitor with VisitParameter (substitute destParam`nwith srcParam) and VisitMember for single-member spines (length-1 SourcePath).`nLookupPropertyMap centralizes the 'pm not found' Phase 3 rejection. Translate`nentry point now invokes the visitor and constructs Expression<Func<TSrc,TResult>>`nvia MakeGenericType(srcType, returnType) + Expression.Lambda. Multi-segment`npaths, CustomExpression, recursive nesting, and additional rejection rules`nland in Tasks 3-5; visitor throws NotImplementedException on those paths."
```

---

## Task 3 — Multi-segment SourcePath (flattening) + Ignored/HasConstant/unmapped rejections

**Goal:** Extend the visitor to handle multi-segment `SourcePath` (e.g., `OrderDto.CustomerName` whose `pm.SourcePath = [Customer, Name]` walks both segments on the source parameter). Add Phase 3 rejections for `Ignored`, `HasConstant`, and the `no SourcePath + no CustomExpression + not Ignored + not HasConstant` (unmapped) case.

**Files:**
- Modify: `C:\Repos\Atlas\src\Atlas.Projections\Internal\ExpressionTranslator.cs`
- Modify: `C:\Repos\Atlas\tests\Atlas.Projections.Tests\ExpressionTranslatorTests.cs`
- Modify: `C:\Repos\Atlas\tests\Atlas.Projections.Tests\ExpressionTranslatorRejectionTests.cs`

**Allowlist:** the three files above.

- [ ] **Step 3.1: Write failing tests for flattening + rejections**

Append to `ExpressionTranslatorTests.cs`:

```csharp
public class ExpressionTranslatorFlatteningTests
{
    [Fact]
    public void FlattenedMember_ResolvesViaMultiSegmentSourcePath()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<UEDS_FlattenSrc, UEDS_FlattenDto>());
        Expression<Func<UEDS_FlattenDto, bool>> predicate = d => d.CustomerName == "Alice";

        var translated = (Expression<Func<UEDS_FlattenSrc, bool>>)ExpressionTranslator.Translate(
            cfg.Internal_Registry,
            new TypePair(typeof(UEDS_FlattenSrc), typeof(UEDS_FlattenDto)),
            predicate);

        var compiled = translated.Compile();
        Assert.True(compiled(new UEDS_FlattenSrc { Customer = new UEDS_FlattenCustomer { Name = "Alice" } }));
        Assert.False(compiled(new UEDS_FlattenSrc { Customer = new UEDS_FlattenCustomer { Name = "Bob" } }));
    }

    [Fact]
    public void DeepFlattenedMember_ResolvesViaMultiSegmentSourcePath()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<UEDS_DeepFlattenSrc, UEDS_DeepFlattenDto>());
        Expression<Func<UEDS_DeepFlattenDto, bool>> predicate = d => d.CustomerAddressCity == "London";

        var translated = (Expression<Func<UEDS_DeepFlattenSrc, bool>>)ExpressionTranslator.Translate(
            cfg.Internal_Registry,
            new TypePair(typeof(UEDS_DeepFlattenSrc), typeof(UEDS_DeepFlattenDto)),
            predicate);

        var compiled = translated.Compile();
        Assert.True(compiled(new UEDS_DeepFlattenSrc
        {
            Customer = new UEDS_DeepFlattenCustomer { Address = new UEDS_DeepFlattenAddress { City = "London" } }
        }));
        Assert.False(compiled(new UEDS_DeepFlattenSrc
        {
            Customer = new UEDS_DeepFlattenCustomer { Address = new UEDS_DeepFlattenAddress { City = "Paris" } }
        }));
    }

    [Fact]
    public void MethodCallOnTranslatedMember_PassesThrough()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<UEDS_FlattenSrc, UEDS_FlattenDto>());
        Expression<Func<UEDS_FlattenDto, bool>> predicate = d => d.CustomerName.StartsWith("A");

        var translated = (Expression<Func<UEDS_FlattenSrc, bool>>)ExpressionTranslator.Translate(
            cfg.Internal_Registry,
            new TypePair(typeof(UEDS_FlattenSrc), typeof(UEDS_FlattenDto)),
            predicate);

        var compiled = translated.Compile();
        Assert.True(compiled(new UEDS_FlattenSrc { Customer = new UEDS_FlattenCustomer { Name = "Alice" } }));
        Assert.False(compiled(new UEDS_FlattenSrc { Customer = new UEDS_FlattenCustomer { Name = "Bob" } }));
    }
}

public class UEDS_FlattenCustomer { public string Name { get; set; } = ""; }
public class UEDS_FlattenSrc { public UEDS_FlattenCustomer Customer { get; set; } = new(); }
public class UEDS_FlattenDto { public string CustomerName { get; set; } = ""; }

public class UEDS_DeepFlattenAddress { public string City { get; set; } = ""; }
public class UEDS_DeepFlattenCustomer { public UEDS_DeepFlattenAddress Address { get; set; } = new(); }
public class UEDS_DeepFlattenSrc { public UEDS_DeepFlattenCustomer Customer { get; set; } = new(); }
public class UEDS_DeepFlattenDto { public string CustomerAddressCity { get; set; } = ""; }
```

Append to `ExpressionTranslatorRejectionTests.cs` (in the existing `ExpressionTranslatorMemberRejectionTests` class):

```csharp
    [Fact]
    public void IgnoredMember_ThrowsAtlasProjectionException()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<UEDS_IgnoredSrc, UEDS_IgnoredDto>()
                .ForMember(d => d.Computed, opt => opt.Ignore()));
        Expression<Func<UEDS_IgnoredDto, bool>> predicate = d => d.Computed > 100;

        var ex = Assert.Throws<AtlasProjectionException>(() =>
            ExpressionTranslator.Translate(
                cfg.Internal_Registry,
                new TypePair(typeof(UEDS_IgnoredSrc), typeof(UEDS_IgnoredDto)),
                predicate));

        Assert.Contains(ex.Diagnostics, d => d.Reason.Contains("Computed")
                                          && d.Reason.Contains("Ignore"));
    }

    [Fact]
    public void ConstantMember_ThrowsAtlasProjectionException()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<UEDS_ConstantSrc, UEDS_ConstantDto>()
                .ForMember(d => d.Status, opt => opt.MapFrom("active")));
        Expression<Func<UEDS_ConstantDto, bool>> predicate = d => d.Status == "active";

        var ex = Assert.Throws<AtlasProjectionException>(() =>
            ExpressionTranslator.Translate(
                cfg.Internal_Registry,
                new TypePair(typeof(UEDS_ConstantSrc), typeof(UEDS_ConstantDto)),
                predicate));

        Assert.Contains(ex.Diagnostics, d => d.Reason.Contains("Status")
                                          && d.Reason.Contains("constant"));
    }
}

public class UEDS_IgnoredSrc { public int Id { get; set; } }
public class UEDS_IgnoredDto { public int Id { get; set; } public decimal Computed { get; set; } }

public class UEDS_ConstantSrc { public int Id { get; set; } }
public class UEDS_ConstantDto { public int Id { get; set; } public string Status { get; set; } = ""; }
```

- [ ] **Step 3.2: Run tests to verify they fail**

```pwsh
cd C:\Repos\Atlas
dotnet test --nologo --filter "FullyQualifiedName~ExpressionTranslatorFlatteningTests|FullyQualifiedName~ExpressionTranslatorMemberRejectionTests"
```

Expected: flattening tests fail because `BuildSourceExpression` only handles single-segment SourcePath; rejection tests fail because the rejection paths aren't yet implemented.

- [ ] **Step 3.3: Update `BuildSourceExpression` and `LookupPropertyMap` for the new cases**

In `ExpressionTranslator.cs`, replace `BuildSourceExpression` with:

```csharp
        private Expression BuildSourceExpression(PropertyMap pm, Expression currentSrcExpr)
        {
            var srcType = currentSrcExpr.Type;
            var dstType = pm.DestinationProperty?.DeclaringType ?? typeof(object);

            // Phase 3 rejection: Ignored member
            if (pm.Ignored)
                throw Reject(srcType, dstType, pm.Name,
                    $"destination member '{dstType.Name}.{pm.Name}' is configured with Ignore() and " +
                    "cannot be referenced in a UseAsDataSource expression.");

            // Phase 3 rejection: constant-mapped member
            if (pm.HasConstant)
                throw Reject(srcType, dstType, pm.Name,
                    $"destination member '{dstType.Name}.{pm.Name}' is a constant ({pm.ConstantValue}); " +
                    "predicates against it are trivially true/false. Compare against the constant directly instead.");

            // SourcePath case: walk the path, chaining MemberAccess.
            if (pm.SourcePath is not null)
            {
                Expression result = currentSrcExpr;
                foreach (var member in pm.SourcePath.Members)
                {
                    result = Expression.MakeMemberAccess(result, member);
                }
                return result;
            }

            // CustomExpression case: filled in Task 4.
            if (pm.CustomExpression is not null)
                throw new NotImplementedException("CustomExpression filled in Task 4.");

            // Phase 3 rejection: unmapped (no SourcePath, no CustomExpression, not Ignored, not HasConstant).
            throw Reject(srcType, dstType, pm.Name,
                $"destination member '{dstType.Name}.{pm.Name}' has neither a configured source path " +
                "nor a custom expression. Add a MapFrom or [SourceMember] to make it translatable.");
        }
```

The visitor's `VisitMember` for the flat case still works because Task 2's body delegated to `BuildSourceExpression(pm, _srcParam)`. With the multi-segment SourcePath now handled, both single-segment AND multi-segment cases flow through the same code path (the `foreach` loop iterates once for single-segment, multiple times for flattening).

- [ ] **Step 3.4: Run tests to verify they pass**

```pwsh
cd C:\Repos\Atlas
dotnet test --nologo --filter "FullyQualifiedName~ExpressionTranslatorFlatteningTests|FullyQualifiedName~ExpressionTranslatorMemberRejectionTests"
```

Expected: 3 new flattening tests pass; 2 new rejection tests pass.

- [ ] **Step 3.5: Run full suite — zero regressions**

```pwsh
cd C:\Repos\Atlas
dotnet test --nologo 2>&1 | Select-String -Pattern "Passed:|Failed:|Skipped:"
```

Expected: total = 716 (Task 2) + 5 = 721. Failed = 0.

- [ ] **Step 3.6: Commit**

```pwsh
git add src/Atlas.Projections/Internal/ExpressionTranslator.cs tests/Atlas.Projections.Tests/ExpressionTranslatorTests.cs tests/Atlas.Projections.Tests/ExpressionTranslatorRejectionTests.cs
git commit -m "ExpressionTranslator: flattening (multi-segment SourcePath) + Phase 3 rejections (Task 3)`n`nBuildSourceExpression now walks pm.SourcePath.Members iteratively to handle`nflattened destination members (e.g., OrderDto.CustomerName ↔ Order.Customer.Name).`nAdds Phase 3 rejections for Ignored members, constant-mapped members (HasConstant),`nand unmapped members (no SourcePath, no CustomExpression). CustomExpression case`nstill throws NotImplementedException — filled in Task 4. All rejections route`nthrough the existing Reject helper with structured ProjectionDiagnostic and the`n'UseAsDataSource translation: ' prefix."
```

---

## Task 4 — `CustomExpression` inlining via `ParameterReplacer`

**Goal:** Handle `pm.CustomExpression` (the `MapFrom(s => Expression body)` case) by inlining the lambda body via the existing `ParameterReplacer` helper. This is the same code path `ProjectionPlanBuilder.BuildBinding` uses (lines 105-110 of `ProjectionPlanBuilder.cs`).

**Files:**
- Modify: `C:\Repos\Atlas\src\Atlas.Projections\Internal\ExpressionTranslator.cs`
- Modify: `C:\Repos\Atlas\tests\Atlas.Projections.Tests\ExpressionTranslatorTests.cs`

**Allowlist:** the two files above.

- [ ] **Step 4.1: Write failing test for CustomExpression inlining**

Append to `ExpressionTranslatorTests.cs`:

```csharp
public class ExpressionTranslatorCustomExpressionTests
{
    [Fact]
    public void CustomExpression_InlinesBodyViaParameterReplacer()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<UEDS_CustomSrc, UEDS_CustomDto>()
                .ForMember(d => d.DisplayName,
                           opt => opt.MapFrom(s => s.FirstName + " " + s.LastName)));
        Expression<Func<UEDS_CustomDto, bool>> predicate = d => d.DisplayName.Contains("Alice");

        var translated = (Expression<Func<UEDS_CustomSrc, bool>>)ExpressionTranslator.Translate(
            cfg.Internal_Registry,
            new TypePair(typeof(UEDS_CustomSrc), typeof(UEDS_CustomDto)),
            predicate);

        var compiled = translated.Compile();
        Assert.True(compiled(new UEDS_CustomSrc { FirstName = "Alice", LastName = "Smith" }));
        Assert.False(compiled(new UEDS_CustomSrc { FirstName = "Bob", LastName = "Smith" }));
    }
}

public class UEDS_CustomSrc
{
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
}
public class UEDS_CustomDto
{
    public string DisplayName { get; set; } = "";
}
```

- [ ] **Step 4.2: Run test to verify it fails**

```pwsh
cd C:\Repos\Atlas
dotnet test --nologo --filter "FullyQualifiedName~ExpressionTranslatorCustomExpressionTests"
```

Expected: test fails with `NotImplementedException` from Task 3's `BuildSourceExpression`.

- [ ] **Step 4.3: Replace the `NotImplementedException` with `ParameterReplacer.Replace`**

In `ExpressionTranslator.cs`, replace the CustomExpression branch:

```csharp
            // CustomExpression case: filled in Task 4.
            if (pm.CustomExpression is not null)
                throw new NotImplementedException("CustomExpression filled in Task 4.");
```

with:

```csharp
            // CustomExpression case: inline the body, substituting the lambda's parameter
            // with currentSrcExpr. Same code path as ProjectionPlanBuilder.BuildBinding
            // (src/Atlas.Projections/Internal/ProjectionPlanBuilder.cs lines 105-110).
            if (pm.CustomExpression is not null)
            {
                return ParameterReplacer.Replace(
                    pm.CustomExpression.Body,
                    pm.CustomExpression.Parameters[0],
                    currentSrcExpr);
            }
```

`ParameterReplacer` is the existing internal class at `src/Atlas.Projections/Internal/ParameterReplacer.cs`. No new using directive needed (same namespace `Atlas.Projections.Internal`).

- [ ] **Step 4.4: Run test to verify it passes**

```pwsh
cd C:\Repos\Atlas
dotnet test --nologo --filter "FullyQualifiedName~ExpressionTranslatorCustomExpressionTests"
```

Expected: 1 test passes.

- [ ] **Step 4.5: Run full suite — zero regressions**

```pwsh
cd C:\Repos\Atlas
dotnet test --nologo 2>&1 | Select-String -Pattern "Passed:|Failed:|Skipped:"
```

Expected: total = 721 (Task 3) + 1 = 722. Failed = 0.

- [ ] **Step 4.6: Commit**

```pwsh
git add src/Atlas.Projections/Internal/ExpressionTranslator.cs tests/Atlas.Projections.Tests/ExpressionTranslatorTests.cs
git commit -m "ExpressionTranslator: CustomExpression inlining via ParameterReplacer (Task 4)`n`nBuildSourceExpression's CustomExpression branch now routes through the existing`nAtlas.Projections.Internal.ParameterReplacer.Replace helper to substitute the`nuser-supplied lambda's parameter with the current source expression. Same code`npath ProjectionPlanBuilder.BuildBinding uses for ProjectTo bindings — single`nsource of truth for parameter substitution semantics."
```

---

## Task 5 — Recursive nested-DTO chain (`d.Customer.Name`)

**Goal:** Handle the case where the destination expression is a multi-level member chain through nested DTOs (e.g., `d.Customer.Name` where `OrderDto.Customer` is `CustomerDto` and `(Customer, CustomerDto)` is registered separately). The visitor currently only handles single-member spines rooted at `_destParam`; this task extends it to walk multi-level spines, hopping through nested TypeMaps as the type changes.

**Files:**
- Modify: `C:\Repos\Atlas\src\Atlas.Projections\Internal\ExpressionTranslator.cs`
- Modify: `C:\Repos\Atlas\tests\Atlas.Projections.Tests\ExpressionTranslatorTests.cs`
- Modify: `C:\Repos\Atlas\tests\Atlas.Projections.Tests\ExpressionTranslatorRejectionTests.cs` (mid-chain unregistered-pair case)

**Allowlist:** the three files above.

- [ ] **Step 5.1: Write failing tests for recursive nested-DTO**

Append to `ExpressionTranslatorTests.cs`:

```csharp
public class ExpressionTranslatorRecursiveTests
{
    [Fact]
    public void NestedDtoChain_TranslatesViaTwoTypeMaps()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap<UEDS_NestedSrc, UEDS_NestedDto>();
            c.CreateMap<UEDS_NestedCustomer, UEDS_NestedCustomerDto>();
        });
        Expression<Func<UEDS_NestedDto, bool>> predicate = d => d.Customer.Name == "Alice";

        var translated = (Expression<Func<UEDS_NestedSrc, bool>>)ExpressionTranslator.Translate(
            cfg.Internal_Registry,
            new TypePair(typeof(UEDS_NestedSrc), typeof(UEDS_NestedDto)),
            predicate);

        var compiled = translated.Compile();
        Assert.True(compiled(new UEDS_NestedSrc { Customer = new UEDS_NestedCustomer { Name = "Alice" } }));
        Assert.False(compiled(new UEDS_NestedSrc { Customer = new UEDS_NestedCustomer { Name = "Bob" } }));
    }
}

public class UEDS_NestedCustomer { public string Name { get; set; } = ""; }
public class UEDS_NestedCustomerDto { public string Name { get; set; } = ""; }
public class UEDS_NestedSrc { public UEDS_NestedCustomer Customer { get; set; } = new(); }
public class UEDS_NestedDto { public UEDS_NestedCustomerDto Customer { get; set; } = new(); }
```

Append to `ExpressionTranslatorRejectionTests.cs` (in `ExpressionTranslatorMemberRejectionTests`):

```csharp
    [Fact]
    public void MidChainPairNotRegistered_ThrowsAtlasProjectionException()
    {
        // Outer (Order, OrderDto) is registered; inner (Customer, CustomerDto) is NOT.
        var cfg = new MapperConfiguration(c => c.CreateMap<UEDS_MidChainSrc, UEDS_MidChainDto>());
        Expression<Func<UEDS_MidChainDto, bool>> predicate = d => d.Customer.Name == "Alice";

        var ex = Assert.Throws<AtlasProjectionException>(() =>
            ExpressionTranslator.Translate(
                cfg.Internal_Registry,
                new TypePair(typeof(UEDS_MidChainSrc), typeof(UEDS_MidChainDto)),
                predicate));

        Assert.Contains(ex.Diagnostics, d => d.Reason.Contains("not registered"));
    }
}

public class UEDS_MidChainCustomer { public string Name { get; set; } = ""; }
public class UEDS_MidChainCustomerDto { public string Name { get; set; } = ""; }
public class UEDS_MidChainSrc { public UEDS_MidChainCustomer Customer { get; set; } = new(); }
public class UEDS_MidChainDto { public UEDS_MidChainCustomerDto Customer { get; set; } = new(); }
```

- [ ] **Step 5.2: Run tests to verify they fail**

```pwsh
cd C:\Repos\Atlas
dotnet test --nologo --filter "FullyQualifiedName~ExpressionTranslatorRecursiveTests|FullyQualifiedName~ExpressionTranslatorMemberRejectionTests"
```

Expected: recursive test fails — visitor's `VisitMember` only handles spines whose root is `_destParam` directly (single-member). Nested case has `node.Expression` being another `MemberExpression`, not a `ParameterExpression`, so it falls through to `base.VisitMember(node)` and the inner member doesn't translate.

- [ ] **Step 5.3: Rewrite `VisitMember` to walk multi-segment spines**

In `ExpressionTranslator.cs`, replace the existing `VisitMember`:

```csharp
        protected override Expression VisitMember(MemberExpression node)
        {
            // Walk the spine: chain of MemberExpressions rooted at a single Expression.
            // For Task 2 we handle the flat single-member case (length-1 spine rooted at _destParam).
            if (node.Expression is ParameterExpression p && p == _destParam)
            {
                // Single-member spine: d.X
                var pm = LookupPropertyMap(_rootPair, node.Member.Name);
                return BuildSourceExpression(pm, _srcParam);
            }

            // Spine root is not _destParam (closure access, etc.) — pass through.
            return base.VisitMember(node);
        }
```

with:

```csharp
        protected override Expression VisitMember(MemberExpression node)
        {
            // Walk the spine: collect chain of MemberExpressions rooted at a single Expression.
            var spine = new List<MemberInfo>();
            Expression? current = node;
            while (current is MemberExpression me)
            {
                spine.Add(me.Member);
                current = me.Expression;
            }
            // spine is currently outermost-first; reverse so it's innermost-first
            // (e.g., d.Customer.Name → spine = [Customer, Name]).
            spine.Reverse();

            // Spine root must be the destination parameter for translation.
            if (current is not ParameterExpression p || p != _destParam)
            {
                // Non-destination access (closure, sub-lambda parameter, etc.) — pass through.
                return base.VisitMember(node);
            }

            // Walk the spine left-to-right, threading (currentSrcExpr, currentTypePair).
            Expression currentSrcExpr = _srcParam;
            TypePair currentPair = _rootPair;

            for (int i = 0; i < spine.Count; i++)
            {
                var memberName = spine[i].Name;
                var pm = LookupPropertyMap(currentPair, memberName);
                var resolved = BuildSourceExpression(pm, currentSrcExpr);

                if (i == spine.Count - 1)
                {
                    // Last member; return the resolved expression.
                    return resolved;
                }

                // More members to walk. Determine the next typepair from the resolved
                // source expression's type and the destination property's declared type.
                if (pm.DestinationProperty is null)
                    throw Reject(currentPair.Source, currentPair.Destination, memberName,
                        $"destination member '{currentPair.Destination.Name}.{memberName}' has no " +
                        "DestinationProperty (constructor-only mapping cannot be walked through nested chains).");

                currentSrcExpr = resolved;
                currentPair = new TypePair(resolved.Type, pm.DestinationProperty.PropertyType);
            }

            // Unreachable: spine has at least one member (otherwise we wouldn't be in VisitMember).
            throw new InvalidOperationException("Unreachable: spine was empty.");
        }
```

`LookupPropertyMap` already throws the "mid-chain pair not registered" error via its `_registry.GetTypeMap` null check (Task 2). The new path simply reuses it for non-root pairs.

- [ ] **Step 5.4: Run tests to verify they pass**

```pwsh
cd C:\Repos\Atlas
dotnet test --nologo --filter "FullyQualifiedName~ExpressionTranslatorRecursiveTests|FullyQualifiedName~ExpressionTranslatorMemberRejectionTests"
```

Expected: 1 new recursive test passes; mid-chain rejection test passes (3 in the rejection class now).

- [ ] **Step 5.5: Run full suite — zero regressions**

```pwsh
cd C:\Repos\Atlas
dotnet test --nologo 2>&1 | Select-String -Pattern "Passed:|Failed:|Skipped:"
```

Expected: total = 722 (Task 4) + 2 = 724. Failed = 0.

- [ ] **Step 5.6: Commit**

```pwsh
git add src/Atlas.Projections/Internal/ExpressionTranslator.cs tests/Atlas.Projections.Tests/ExpressionTranslatorTests.cs tests/Atlas.Projections.Tests/ExpressionTranslatorRejectionTests.cs
git commit -m "ExpressionTranslator: recursive nested-DTO chains (Task 5)`n`nVisitMember now walks multi-segment spines (e.g., d.Customer.Name) by collecting`nthe MemberInfo chain, validating the spine's root is _destParam, then iterating`nleft-to-right while threading (currentSrcExpr, currentTypePair). Each iteration:`nlooks up PropertyMap, builds resolved source expression, advances state to the`nnext (resolvedType, dstPropType) pair via DestinationProperty.PropertyType.`nMid-chain unregistered-pair rejection reuses LookupPropertyMap's existing null`nguard. Constructor-only mappings (DestinationProperty == null) explicitly`nrejected when used mid-chain — they have no walkable type for further descent."
```

---

## Task 6 — Defensive `VisitMethodCall` (inner-lambda gap detection per §5.4)

**Goal:** Per design §5.4 / R1, the v1 visitor does NOT translate inner lambdas on collection-typed destination members. A predicate like `d => d.Lines.Any(l => l.Total > 100)` produces an expression with the inner lambda's parameter typed against the destination element type — the LINQ provider rejects it. This task adds a defensive `VisitMethodCall` override that detects the pattern and throws a clear `AtlasProjectionException` at translate time.

**Files:**
- Modify: `C:\Repos\Atlas\src\Atlas.Projections\Internal\ExpressionTranslator.cs`
- Modify: `C:\Repos\Atlas\tests\Atlas.Projections.Tests\ExpressionTranslatorRejectionTests.cs`

**Allowlist:** the two files above.

- [ ] **Step 6.1: Write failing test for inner-lambda detection**

Append to `ExpressionTranslatorRejectionTests.cs`:

```csharp
public class ExpressionTranslatorInnerLambdaTests
{
    [Fact]
    public void InnerLambdaOnCollectionDestinationMember_ThrowsAtlasProjectionException()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap<UEDS_InnerSrc, UEDS_InnerDto>();
            c.CreateMap<UEDS_InnerLineSrc, UEDS_InnerLineDto>();
        });
        Expression<Func<UEDS_InnerDto, bool>> predicate = d => d.Lines.Any(l => l.Total > 100);

        var ex = Assert.Throws<AtlasProjectionException>(() =>
            ExpressionTranslator.Translate(
                cfg.Internal_Registry,
                new TypePair(typeof(UEDS_InnerSrc), typeof(UEDS_InnerDto)),
                predicate));

        Assert.Contains(ex.Diagnostics, d => d.Reason.Contains("inner lambda"));
    }
}

public class UEDS_InnerLineSrc { public decimal Total { get; set; } }
public class UEDS_InnerLineDto { public decimal Total { get; set; } }
public class UEDS_InnerSrc { public List<UEDS_InnerLineSrc> Lines { get; set; } = new(); }
public class UEDS_InnerDto { public List<UEDS_InnerLineDto> Lines { get; set; } = new(); }
```

- [ ] **Step 6.2: Run test to verify it fails**

```pwsh
cd C:\Repos\Atlas
dotnet test --nologo --filter "FullyQualifiedName~ExpressionTranslatorInnerLambdaTests"
```

Expected: test fails. Without the defensive `VisitMethodCall`, the visitor walks `d.Lines` (translates to source-side `List<UEDS_InnerLineSrc>`), then the `.Any(l => l.Total > 100)` body's `l.Total` is processed by the visitor — `l` is a fresh `ParameterExpression` (not `_destParam`), so the visitor passes the inner expression through unchanged. Result: a malformed expression where the outer member access is source-typed but the inner lambda is destination-typed. May fail at runtime in `compiled` or at LINQ-provider translation. The test expects an immediate `AtlasProjectionException` with "inner lambda" in the reason.

- [ ] **Step 6.3: Add the defensive `VisitMethodCall` override**

In `ExpressionTranslator.cs`'s `MemberAccessRewriter`, add the override just BEFORE `VisitMember`:

```csharp
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            // Defensive detection per design §5.4: reject inner lambdas on collection-typed
            // destination members (e.g., d.Lines.Any(l => l.Total > 100)). v1 doesn't thread
            // destination-element-type context into inner lambdas; allowing the visit to
            // proceed would produce malformed expressions that the LINQ provider rejects.
            if (IsCollectionPredicateMethod(node.Method) &&
                node.Arguments.Count >= 2)
            {
                // First argument is the source collection; we expect it to be a member-access
                // chain on _destParam (or pass-through if it's a closure).
                var firstArg = node.Arguments[0];
                bool firstArgIsDestinationMember =
                    firstArg is MemberExpression me && SpineIsRootedAtDestParam(me);

                if (firstArgIsDestinationMember &&
                    node.Arguments[1] is LambdaExpression innerLambda &&
                    innerLambda.Parameters.Count == 1)
                {
                    // The inner lambda's parameter is typed against the destination element
                    // type — translation would produce a type-incompatible expression.
                    throw Reject(_rootPair.Source, _rootPair.Destination, "(translate)",
                        "inner lambdas on collection-typed destination members are not " +
                        "translated in v1. Use AsQueryable() then LINQ-to-Objects, or " +
                        "rewrite the predicate against the source.");
                }
            }

            return base.VisitMethodCall(node);
        }

        /// <summary>
        /// True if the method belongs to <see cref="System.Linq.Enumerable"/> or
        /// <see cref="System.Linq.Queryable"/> AND its name is a recognized
        /// collection-predicate operator that takes an inner lambda.
        /// </summary>
        private static bool IsCollectionPredicateMethod(System.Reflection.MethodInfo method)
        {
            if (method.DeclaringType != typeof(System.Linq.Enumerable) &&
                method.DeclaringType != typeof(System.Linq.Queryable))
                return false;

            return method.Name switch
            {
                "Any" or "All" or "Where" or "Select" or "First" or
                "FirstOrDefault" or "Single" or "SingleOrDefault" or "Count" => true,
                _ => false,
            };
        }

        /// <summary>
        /// True if a MemberExpression's spine root is the destination parameter.
        /// </summary>
        private bool SpineIsRootedAtDestParam(MemberExpression node)
        {
            Expression? current = node;
            while (current is MemberExpression me)
            {
                current = me.Expression;
            }
            return current is ParameterExpression p && p == _destParam;
        }
```

The detector is intentionally conservative: it only fires on `Enumerable`/`Queryable` methods named `Any`/`All`/`Where`/`Select`/`First`/`FirstOrDefault`/`Single`/`SingleOrDefault`/`Count` whose first argument is a member-access chain rooted at `_destParam` AND whose second argument is a single-parameter lambda. Other patterns (closures, captured collections, etc.) pass through unchanged.

- [ ] **Step 6.4: Run test to verify it passes**

```pwsh
cd C:\Repos\Atlas
dotnet test --nologo --filter "FullyQualifiedName~ExpressionTranslatorInnerLambdaTests"
```

Expected: 1 new test passes.

- [ ] **Step 6.5: Run full suite — zero regressions**

```pwsh
cd C:\Repos\Atlas
dotnet test --nologo 2>&1 | Select-String -Pattern "Passed:|Failed:|Skipped:"
```

Expected: total = 724 (Task 5) + 1 = 725. Failed = 0.

- [ ] **Step 6.6: Commit**

```pwsh
git add src/Atlas.Projections/Internal/ExpressionTranslator.cs tests/Atlas.Projections.Tests/ExpressionTranslatorRejectionTests.cs
git commit -m "ExpressionTranslator: defensive VisitMethodCall (inner-lambda gap) (Task 6)`n`nPer design §5.4 / R1, v1 doesn't thread destination-element-type context into`ninner lambdas on collection-typed destination members. Without this defensive`ndetector, a predicate like d => d.Lines.Any(l => l.Total > 100) produces a`nmalformed expression at translate time. New VisitMethodCall override detects:`n(1) call belongs to Enumerable/Queryable, (2) method is a recognized collection-`npredicate operator, (3) first argument is a member chain rooted at _destParam,`n(4) second argument is a single-parameter lambda. When all four hold, throws`nAtlasProjectionException with 'inner lambda' in the reason. Other method-call`npatterns (string.StartsWith, BCL helpers, closure-captured collections) pass`nthrough unchanged."
```

---

## Task 7 — `TranslationPlanCache` + `TranslationPlanCacheRegistry`

**Goal:** Per-`MapperConfiguration` cache for translated lambdas, keyed by `(TypePair, lambda-reference-identity)`. Mirrors the existing `ProjectionPlanCacheRegistry` pattern. The cache is consumed by the public `Translate` extension and by the wrapper.

**Files:**
- Create: `C:\Repos\Atlas\src\Atlas.Projections\Internal\TranslationPlanCache.cs`
- Create: `C:\Repos\Atlas\tests\Atlas.Projections.Tests\UseAsDataSourceCacheTests.cs`

**Allowlist:** the two files above.

- [ ] **Step 7.1: Write failing tests for cache behavior**

Create `tests/Atlas.Projections.Tests/UseAsDataSourceCacheTests.cs`:

```csharp
using System.Collections.Concurrent;
using System.Linq.Expressions;
using Atlas;
using Atlas.Internal;
using Atlas.Projections.Internal;

namespace Atlas.Projections.Tests;

public class UseAsDataSourceCacheTests
{
    private static readonly Expression<Func<UEDS_CacheDto, bool>> _stableLambda =
        d => d.Total > 100m;

    [Fact]
    public void SameLambdaReference_ReturnsCachedResult()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<UEDS_CacheSrc, UEDS_CacheDto>());
        var cache = TranslationPlanCacheRegistry.For(cfg);
        var pair = new TypePair(typeof(UEDS_CacheSrc), typeof(UEDS_CacheDto));

        int factoryCalls = 0;
        LambdaExpression Factory()
        {
            factoryCalls++;
            return ExpressionTranslator.Translate(cfg.Internal_Registry, pair, _stableLambda);
        }

        var first = cache.GetOrTranslate(pair, _stableLambda, Factory);
        var second = cache.GetOrTranslate(pair, _stableLambda, Factory);

        Assert.Same(first, second);
        Assert.Equal(1, factoryCalls);
    }

    [Fact]
    public void DistinctLambdaInstances_TranslateIndependently()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<UEDS_CacheSrc, UEDS_CacheDto>());
        var cache = TranslationPlanCacheRegistry.For(cfg);
        var pair = new TypePair(typeof(UEDS_CacheSrc), typeof(UEDS_CacheDto));

        int factoryCalls = 0;
        LambdaExpression Factory(LambdaExpression src) =>
            ExpressionTranslator.Translate(cfg.Internal_Registry, pair, src);

        // Two separate lambda instances with identical structure.
        Expression<Func<UEDS_CacheDto, bool>> first = d => d.Total > 100m;
        Expression<Func<UEDS_CacheDto, bool>> second = d => d.Total > 100m;
        Assert.NotSame(first, second);

        cache.GetOrTranslate(pair, first, () => { factoryCalls++; return Factory(first); });
        cache.GetOrTranslate(pair, second, () => { factoryCalls++; return Factory(second); });

        Assert.Equal(2, factoryCalls);
    }

    [Fact]
    public void DifferentMapperConfigurations_HaveSeparateCaches()
    {
        var cfgA = new MapperConfiguration(c => c.CreateMap<UEDS_CacheSrc, UEDS_CacheDto>());
        var cfgB = new MapperConfiguration(c => c.CreateMap<UEDS_CacheSrc, UEDS_CacheDto>());

        var cacheA = TranslationPlanCacheRegistry.For(cfgA);
        var cacheB = TranslationPlanCacheRegistry.For(cfgB);

        Assert.NotSame(cacheA, cacheB);
    }

    [Fact]
    public void SameMapperConfiguration_ReturnsSameCacheInstance()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<UEDS_CacheSrc, UEDS_CacheDto>());

        var first = TranslationPlanCacheRegistry.For(cfg);
        var second = TranslationPlanCacheRegistry.For(cfg);

        Assert.Same(first, second);
    }
}

public class UEDS_CacheSrc { public decimal Total { get; set; } }
public class UEDS_CacheDto { public decimal Total { get; set; } }
```

- [ ] **Step 7.2: Run tests to verify they fail (compile error)**

```pwsh
cd C:\Repos\Atlas
dotnet test --nologo --filter "FullyQualifiedName~UseAsDataSourceCacheTests"
```

Expected: build error referencing missing types `TranslationPlanCache`, `TranslationPlanCacheRegistry`.

- [ ] **Step 7.3: Create `TranslationPlanCache.cs`**

Contents of `src/Atlas.Projections/Internal/TranslationPlanCache.cs`:

```csharp
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Atlas.Internal;

namespace Atlas.Projections.Internal;

/// <summary>
/// Per-<see cref="MapperConfiguration"/> cache of translated lambdas. Keyed on
/// <c>(TypePair, lambda-reference-identity)</c> using <see cref="RuntimeHelpers.GetHashCode"/>
/// — catches the realistic hot path of <c>static readonly Expression&lt;&gt;</c> lambdas
/// reused across call sites; freshly-constructed lambdas miss the cache (and translate once
/// each). See design §4.3 / §6.4.
/// </summary>
internal sealed class TranslationPlanCache
{
    private readonly ConcurrentDictionary<CacheKey, LambdaExpression> _cache = new();

    public LambdaExpression GetOrTranslate(
        TypePair pair,
        LambdaExpression destLambda,
        Func<LambdaExpression> factory)
    {
        ArgumentNullException.ThrowIfNull(destLambda);
        ArgumentNullException.ThrowIfNull(factory);

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

/// <summary>
/// Binds one <see cref="TranslationPlanCache"/> instance per <see cref="MapperConfiguration"/>
/// without contaminating the v1 core type. Bound via <see cref="ConditionalWeakTable{TKey,TValue}"/>
/// so cache lifetime tracks the configuration's lifetime.
/// </summary>
internal static class TranslationPlanCacheRegistry
{
    private static readonly ConditionalWeakTable<MapperConfiguration, TranslationPlanCache> _table = new();

    public static TranslationPlanCache For(MapperConfiguration config) =>
        _table.GetValue(config, _ => new TranslationPlanCache());
}
```

- [ ] **Step 7.4: Run tests to verify they pass**

```pwsh
cd C:\Repos\Atlas
dotnet test --nologo --filter "FullyQualifiedName~UseAsDataSourceCacheTests"
```

Expected: 4 tests pass.

- [ ] **Step 7.5: Run full suite — zero regressions**

```pwsh
cd C:\Repos\Atlas
dotnet test --nologo 2>&1 | Select-String -Pattern "Passed:|Failed:|Skipped:"
```

Expected: total = 725 (Task 6) + 4 = 729. Failed = 0.

- [ ] **Step 7.6: Commit**

```pwsh
git add src/Atlas.Projections/Internal/TranslationPlanCache.cs tests/Atlas.Projections.Tests/UseAsDataSourceCacheTests.cs
git commit -m "TranslationPlanCache + TranslationPlanCacheRegistry (Task 7)`n`nPer-MapperConfiguration cache of translated lambdas keyed on (TypePair,`nlambda-reference-identity) via RuntimeHelpers.GetHashCode + ReferenceEquals.`nMirrors the existing ProjectionPlanCache + ProjectionPlanCacheRegistry shape:`nConditionalWeakTable<MapperConfiguration, TranslationPlanCache> binds the cache`nto the configuration's lifetime. ConcurrentDictionary for thread-safe GetOrAdd.`nCacheKey is a readonly record struct with explicit Equals/GetHashCode that uses`nReferenceEquals on the lambda — catches static readonly Expression<> reuse;`nfreshly-constructed lambdas miss the cache (correct behavior; they translate`nonce each)."
```

---

## Task 8 — Public extensions: `Translate` helper + `UseAsDataSource` entry + `IUseAsDataSource` intermediate

**Goal:** Public-API surface for the engine. The direct-use `cfg.Translate<TSrc, TDst, TResult>(expr)` extension hits the cache + engine. `UseAsDataSource(cfg)` returns an `IUseAsDataSource<TSource>` intermediate whose `For<TDest>()` will (in Task 9) return the wrapper. For Task 8, `For<TDest>()` returns a stub that doesn't yet have the operators wired up — Tasks 9-10 fill those in.

**Files:**
- Create: `C:\Repos\Atlas\src\Atlas.Projections\MapperConfigurationExpressionTranslationExtensions.cs`
- Create: `C:\Repos\Atlas\src\Atlas.Projections\IUseAsDataSource.cs`
- Create: `C:\Repos\Atlas\src\Atlas.Projections\IUseAsDataSourceQueryable.cs` (skeleton — stubs out methods filled in Tasks 9-10)
- Create: `C:\Repos\Atlas\src\Atlas.Projections\IUseAsDataSourceOrdered.cs` (skeleton)
- Create: `C:\Repos\Atlas\src\Atlas.Projections\UseAsDataSourceExtensions.cs`
- Create: `C:\Repos\Atlas\src\Atlas.Projections\Internal\UseAsDataSourceQueryable.cs` (skeleton wrapper class with empty operator stubs)

**Allowlist:** the six files above.

**Note:** this task creates the FULL public-interface skeletons but with stub method bodies. Tasks 9-10 fill in the operator implementations. The skeleton must compile and the `Translate` extension must work end-to-end.

- [ ] **Step 8.1: Write failing tests for `Translate` extension and entry point**

Append to `UseAsDataSourceCacheTests.cs` a new test class:

```csharp
public class TranslateExtensionTests
{
    [Fact]
    public void Translate_ReturnsStronglyTypedExpression()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<UEDS_CacheSrc, UEDS_CacheDto>());

        Expression<Func<UEDS_CacheDto, bool>> destExpr = d => d.Total > 100m;
        Expression<Func<UEDS_CacheSrc, bool>> srcExpr =
            cfg.Translate<UEDS_CacheSrc, UEDS_CacheDto, bool>(destExpr);

        var compiled = srcExpr.Compile();
        Assert.True(compiled(new UEDS_CacheSrc { Total = 150m }));
        Assert.False(compiled(new UEDS_CacheSrc { Total = 50m }));
    }

    [Fact]
    public void UseAsDataSource_ReturnsIntermediate_ForReturnsWrapper()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<UEDS_CacheSrc, UEDS_CacheDto>());
        var queryable = new[] { new UEDS_CacheSrc { Total = 150m } }.AsQueryable();

        var intermediate = queryable.UseAsDataSource(cfg);
        Assert.NotNull(intermediate);

        var wrapper = intermediate.For<UEDS_CacheDto>();
        Assert.NotNull(wrapper);
    }
}
```

- [ ] **Step 8.2: Run tests to verify they fail (compile error)**

```pwsh
cd C:\Repos\Atlas
dotnet test --nologo --filter "FullyQualifiedName~TranslateExtensionTests"
```

Expected: build error referencing missing types/methods.

- [ ] **Step 8.3: Create `IUseAsDataSource.cs`**

Contents of `src/Atlas.Projections/IUseAsDataSource.cs`:

```csharp
namespace Atlas.Projections;

/// <summary>
/// Intermediate handle returned by
/// <see cref="UseAsDataSourceExtensions.UseAsDataSource{TSource}"/>. Single method
/// <see cref="For{TDestination}"/> binds the destination type and returns a wrapper
/// presenting destination-typed LINQ operators.
/// </summary>
public interface IUseAsDataSource<TSource>
{
    /// <summary>
    /// Binds the destination type and returns the destination-typed wrapper. The
    /// (TSource, TDestination) pair must be registered with the
    /// <see cref="MapperConfiguration"/> passed to <c>UseAsDataSource</c>; otherwise this
    /// throws <see cref="AtlasProjectionException"/> at the FIRST translated operator call
    /// (Task 9 / Task 10 wraps each translation in a Phase 1 check).
    /// </summary>
    IUseAsDataSourceQueryable<TSource, TDestination> For<TDestination>();
}
```

- [ ] **Step 8.4: Create `IUseAsDataSourceQueryable.cs` (full surface; stubs filled in Tasks 9-10)**

Contents of `src/Atlas.Projections/IUseAsDataSourceQueryable.cs`:

```csharp
using System.Linq.Expressions;

namespace Atlas.Projections;

/// <summary>
/// Destination-typed LINQ-operator surface for UseAsDataSource. Each operator accepts
/// a <c>Func&lt;TDestination, ...&gt;</c>-shaped lambda; the wrapper translates the
/// destination-typed expression to a source-typed expression and applies the underlying
/// LINQ operator to the wrapped <see cref="IQueryable{TSource}"/>.
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
/// </remarks>
public interface IUseAsDataSourceQueryable<TSource, TDestination> : IEnumerable<TDestination>
{
    // Filtering
    IUseAsDataSourceQueryable<TSource, TDestination> Where(
        Expression<Func<TDestination, bool>> predicate);

    // Ordering
    IUseAsDataSourceOrdered<TSource, TDestination> OrderBy<TKey>(
        Expression<Func<TDestination, TKey>> keySelector);
    IUseAsDataSourceOrdered<TSource, TDestination> OrderByDescending<TKey>(
        Expression<Func<TDestination, TKey>> keySelector);

    // Paging
    IUseAsDataSourceQueryable<TSource, TDestination> Skip(int count);
    IUseAsDataSourceQueryable<TSource, TDestination> Take(int count);

    // Terminal predicate
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

    // Escape hatch
    IQueryable<TDestination> AsQueryable();
}
```

- [ ] **Step 8.5: Create `IUseAsDataSourceOrdered.cs`**

Contents of `src/Atlas.Projections/IUseAsDataSourceOrdered.cs`:

```csharp
using System.Linq.Expressions;

namespace Atlas.Projections;

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

- [ ] **Step 8.6: Create `Internal/UseAsDataSourceQueryable.cs` (stub wrapper)**

Contents of `src/Atlas.Projections/Internal/UseAsDataSourceQueryable.cs`:

```csharp
using System.Linq.Expressions;
using Atlas.Internal;

namespace Atlas.Projections.Internal;

/// <summary>
/// Internal wrapper around an <see cref="IQueryable{TSource}"/> that exposes the
/// destination-typed LINQ-operator surface. Each operator translates the destination-
/// typed lambda via <see cref="ExpressionTranslator"/> + <see cref="TranslationPlanCache"/>
/// and applies the source-typed result to the underlying query.
///
/// Task 8: skeleton with stub operator implementations. Tasks 9-10 fill in the bodies.
/// </summary>
internal sealed class UseAsDataSourceQueryable<TSource, TDestination>
    : IUseAsDataSourceOrdered<TSource, TDestination>
{
    private readonly IQueryable<TSource> _underlying;
    private readonly MapperConfiguration _configuration;
    private readonly TypePair _pair;

    internal UseAsDataSourceQueryable(IQueryable<TSource> underlying, MapperConfiguration configuration)
    {
        _underlying = underlying;
        _configuration = configuration;
        _pair = new TypePair(typeof(TSource), typeof(TDestination));
    }

    // Filtering
    public IUseAsDataSourceQueryable<TSource, TDestination> Where(
        Expression<Func<TDestination, bool>> predicate) => throw new NotImplementedException("Task 9");

    // Ordering
    public IUseAsDataSourceOrdered<TSource, TDestination> OrderBy<TKey>(
        Expression<Func<TDestination, TKey>> keySelector) => throw new NotImplementedException("Task 9");

    public IUseAsDataSourceOrdered<TSource, TDestination> OrderByDescending<TKey>(
        Expression<Func<TDestination, TKey>> keySelector) => throw new NotImplementedException("Task 9");

    public IUseAsDataSourceOrdered<TSource, TDestination> ThenBy<TKey>(
        Expression<Func<TDestination, TKey>> keySelector) => throw new NotImplementedException("Task 9");

    public IUseAsDataSourceOrdered<TSource, TDestination> ThenByDescending<TKey>(
        Expression<Func<TDestination, TKey>> keySelector) => throw new NotImplementedException("Task 9");

    // Paging
    public IUseAsDataSourceQueryable<TSource, TDestination> Skip(int count) => throw new NotImplementedException("Task 9");
    public IUseAsDataSourceQueryable<TSource, TDestination> Take(int count) => throw new NotImplementedException("Task 9");

    // Terminal predicate
    public bool Any() => throw new NotImplementedException("Task 10");
    public bool Any(Expression<Func<TDestination, bool>> predicate) => throw new NotImplementedException("Task 10");
    public bool All(Expression<Func<TDestination, bool>> predicate) => throw new NotImplementedException("Task 10");
    public int Count() => throw new NotImplementedException("Task 10");
    public int Count(Expression<Func<TDestination, bool>> predicate) => throw new NotImplementedException("Task 10");
    public long LongCount() => throw new NotImplementedException("Task 10");
    public long LongCount(Expression<Func<TDestination, bool>> predicate) => throw new NotImplementedException("Task 10");
    public TDestination First() => throw new NotImplementedException("Task 10");
    public TDestination First(Expression<Func<TDestination, bool>> predicate) => throw new NotImplementedException("Task 10");
    public TDestination? FirstOrDefault() => throw new NotImplementedException("Task 10");
    public TDestination? FirstOrDefault(Expression<Func<TDestination, bool>> predicate) => throw new NotImplementedException("Task 10");
    public TDestination Single() => throw new NotImplementedException("Task 10");
    public TDestination Single(Expression<Func<TDestination, bool>> predicate) => throw new NotImplementedException("Task 10");
    public TDestination? SingleOrDefault() => throw new NotImplementedException("Task 10");
    public TDestination? SingleOrDefault(Expression<Func<TDestination, bool>> predicate) => throw new NotImplementedException("Task 10");
    public TDestination Last() => throw new NotImplementedException("Task 10");
    public TDestination Last(Expression<Func<TDestination, bool>> predicate) => throw new NotImplementedException("Task 10");
    public TDestination? LastOrDefault() => throw new NotImplementedException("Task 10");
    public TDestination? LastOrDefault(Expression<Func<TDestination, bool>> predicate) => throw new NotImplementedException("Task 10");

    // Escape hatch
    public IQueryable<TDestination> AsQueryable() => throw new NotImplementedException("Task 10");

    // IEnumerable
    public IEnumerator<TDestination> GetEnumerator() => throw new NotImplementedException("Task 10");
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
```

- [ ] **Step 8.7: Create `UseAsDataSourceExtensions.cs`**

Contents of `src/Atlas.Projections/UseAsDataSourceExtensions.cs`:

```csharp
using Atlas.Projections.Internal;

namespace Atlas.Projections;

/// <summary>
/// Entry point for destination-typed-lambda LINQ operators against a source-typed
/// <see cref="IQueryable{TSource}"/>. Translates each operator's destination-typed
/// expression back to source-typed via the configured Atlas typemaps, then applies
/// the underlying LINQ operator to the source query.
/// </summary>
public static class UseAsDataSourceExtensions
{
    public static IUseAsDataSource<TSource> UseAsDataSource<TSource>(
        this IQueryable<TSource> source,
        MapperConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(configuration);
        return new Intermediate<TSource>(source, configuration);
    }

    private sealed class Intermediate<TSource> : IUseAsDataSource<TSource>
    {
        private readonly IQueryable<TSource> _source;
        private readonly MapperConfiguration _configuration;

        public Intermediate(IQueryable<TSource> source, MapperConfiguration configuration)
        {
            _source = source;
            _configuration = configuration;
        }

        public IUseAsDataSourceQueryable<TSource, TDestination> For<TDestination>() =>
            new UseAsDataSourceQueryable<TSource, TDestination>(_source, _configuration);
    }
}
```

- [ ] **Step 8.8: Create `MapperConfigurationExpressionTranslationExtensions.cs`**

Contents of `src/Atlas.Projections/MapperConfigurationExpressionTranslationExtensions.cs`:

```csharp
using System.Linq.Expressions;
using Atlas.Internal;
using Atlas.Projections.Internal;

namespace Atlas.Projections;

/// <summary>
/// Direct-use translation helper on <see cref="MapperConfiguration"/>. Used by power
/// users who want a translated lambda as a value (e.g., for unit tests or composing
/// with custom LINQ providers); also used internally by
/// <see cref="UseAsDataSourceExtensions"/>'s wrapper operators.
/// </summary>
public static class MapperConfigurationExpressionTranslationExtensions
{
    /// <summary>
    /// Translates a destination-typed expression into a source-typed expression by
    /// substituting destination-member accesses with the source expressions Atlas's
    /// typemaps record (<c>PropertyMap.SourcePath</c> or <c>PropertyMap.CustomExpression</c>).
    /// </summary>
    /// <exception cref="AtlasProjectionException">
    /// Thrown when the lambda references an unmapped, ignored, or constant-mapped
    /// destination member, OR when the (TSource, TDestination) pair is not registered,
    /// OR when the typemap has hooks/PreserveReferences/dynamic-shape attributes that
    /// reject projection.
    /// </exception>
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
}
```

- [ ] **Step 8.9: Run tests to verify they pass**

```pwsh
cd C:\Repos\Atlas
dotnet test --nologo --filter "FullyQualifiedName~TranslateExtensionTests"
```

Expected: 2 tests pass.

- [ ] **Step 8.10: Run full suite — zero regressions**

```pwsh
cd C:\Repos\Atlas
dotnet test --nologo 2>&1 | Select-String -Pattern "Passed:|Failed:|Skipped:"
```

Expected: total = 729 (Task 7) + 2 = 731. Failed = 0.

- [ ] **Step 8.11: Commit**

```pwsh
git add src/Atlas.Projections/MapperConfigurationExpressionTranslationExtensions.cs src/Atlas.Projections/IUseAsDataSource.cs src/Atlas.Projections/IUseAsDataSourceQueryable.cs src/Atlas.Projections/IUseAsDataSourceOrdered.cs src/Atlas.Projections/UseAsDataSourceExtensions.cs src/Atlas.Projections/Internal/UseAsDataSourceQueryable.cs tests/Atlas.Projections.Tests/UseAsDataSourceCacheTests.cs
git commit -m "Public extensions: Translate helper + UseAsDataSource entry + interfaces (Task 8)`n`nFull public-API surface: Translate<TSource, TDestination, TResult> on`nMapperConfiguration (cache + engine dispatch); UseAsDataSource<TSource> on`nIQueryable returns IUseAsDataSource<TSource>; .For<TDest>() returns the`nwrapper. IUseAsDataSourceQueryable<,> + IUseAsDataSourceOrdered<,> declare`nthe destination-typed operator surface (Where, OrderBy*, ThenBy*, Skip,`nTake, terminal predicates, AsQueryable). Internal UseAsDataSourceQueryable<,>`nimplements both interfaces with NotImplementedException stubs — operator`nbodies fill in across Tasks 9-10. Translate extension verified end-to-end:`ncfg.Translate<TSrc, TDst, bool>(d => d.X > 100) returns a strongly-typed`nExpression<Func<TSrc, bool>> that compiles and evaluates correctly."
```

---

## Task 9 — Wrapper operators: `Where`, `OrderBy*`, `ThenBy*`, `Skip`, `Take`

**Goal:** Fill in the non-terminal operator bodies. Each operator translates the destination-typed lambda via the cache+engine pipeline, applies the resulting source-typed lambda to the underlying `IQueryable<TSource>`, and returns a new wrapper instance.

**Files:**
- Modify: `C:\Repos\Atlas\src\Atlas.Projections\Internal\UseAsDataSourceQueryable.cs`
- Create: `C:\Repos\Atlas\tests\Atlas.Projections.Tests\UseAsDataSourceWrapperTests.cs`

**Allowlist:** the two files above.

- [ ] **Step 9.1: Write failing tests for non-terminal operators**

Create `tests/Atlas.Projections.Tests/UseAsDataSourceWrapperTests.cs`:

```csharp
using System.Linq.Expressions;
using Atlas;

namespace Atlas.Projections.Tests;

public class UseAsDataSourceWrapperTests
{
    private static MapperConfiguration BuildCfg() =>
        new MapperConfiguration(c => c.CreateMap<UEDS_WrapperSrc, UEDS_WrapperDto>());

    private static IQueryable<UEDS_WrapperSrc> BuildSource() => new[]
    {
        new UEDS_WrapperSrc { Id = 1, Name = "Alice", Total = 50m },
        new UEDS_WrapperSrc { Id = 2, Name = "Bob",   Total = 150m },
        new UEDS_WrapperSrc { Id = 3, Name = "Carol", Total = 250m },
    }.AsQueryable();

    [Fact]
    public void Where_TranslatesPredicateAndAppliesToUnderlying()
    {
        var cfg = BuildCfg();
        var wrapper = BuildSource().UseAsDataSource(cfg).For<UEDS_WrapperDto>();

        var filtered = wrapper.Where(d => d.Total > 100m);

        Assert.NotNull(filtered);
        Assert.NotSame(wrapper, filtered);  // immutable: new instance
    }

    [Fact]
    public void Where_ChainedTwice_BothApply()
    {
        var cfg = BuildCfg();
        var wrapper = BuildSource().UseAsDataSource(cfg).For<UEDS_WrapperDto>();

        // Chain Where calls; each translates and applies independently.
        var filtered = wrapper
            .Where(d => d.Total > 100m)
            .Where(d => d.Name.StartsWith("B"));

        Assert.NotNull(filtered);
    }

    [Fact]
    public void OrderBy_ReturnsOrderedWrapper()
    {
        var cfg = BuildCfg();
        var wrapper = BuildSource().UseAsDataSource(cfg).For<UEDS_WrapperDto>();

        IUseAsDataSourceOrdered<UEDS_WrapperSrc, UEDS_WrapperDto> ordered =
            wrapper.OrderBy(d => d.Total);

        Assert.NotNull(ordered);
    }

    [Fact]
    public void ThenBy_ChainsAfterOrderBy()
    {
        var cfg = BuildCfg();
        var wrapper = BuildSource().UseAsDataSource(cfg).For<UEDS_WrapperDto>();

        var ordered = wrapper
            .OrderBy(d => d.Total)
            .ThenBy(d => d.Name);

        Assert.NotNull(ordered);
    }

    [Fact]
    public void OrderByDescending_ReturnsOrderedWrapper()
    {
        var cfg = BuildCfg();
        var wrapper = BuildSource().UseAsDataSource(cfg).For<UEDS_WrapperDto>();

        var ordered = wrapper.OrderByDescending(d => d.Total);

        Assert.NotNull(ordered);
    }

    [Fact]
    public void Skip_PassesThroughWithoutTranslation()
    {
        var cfg = BuildCfg();
        var wrapper = BuildSource().UseAsDataSource(cfg).For<UEDS_WrapperDto>();

        var skipped = wrapper.Skip(1);
        Assert.NotNull(skipped);
        Assert.NotSame(wrapper, skipped);
    }

    [Fact]
    public void Take_PassesThroughWithoutTranslation()
    {
        var cfg = BuildCfg();
        var wrapper = BuildSource().UseAsDataSource(cfg).For<UEDS_WrapperDto>();

        var taken = wrapper.Take(2);
        Assert.NotNull(taken);
        Assert.NotSame(wrapper, taken);
    }
}

public class UEDS_WrapperSrc
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Total { get; set; }
}

public class UEDS_WrapperDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Total { get; set; }
}
```

- [ ] **Step 9.2: Run tests to verify they fail**

```pwsh
cd C:\Repos\Atlas
dotnet test --nologo --filter "FullyQualifiedName~UseAsDataSourceWrapperTests"
```

Expected: tests fail with `NotImplementedException` from the Task 8 stubs.

- [ ] **Step 9.3: Fill in the operator bodies**

Replace the stubs in `src/Atlas.Projections/Internal/UseAsDataSourceQueryable.cs`. The full updated file:

```csharp
using System.Linq.Expressions;
using Atlas.Internal;

namespace Atlas.Projections.Internal;

/// <summary>
/// Internal wrapper around an <see cref="IQueryable{TSource}"/> that exposes the
/// destination-typed LINQ-operator surface. Each operator translates the destination-
/// typed lambda via <see cref="ExpressionTranslator"/> + <see cref="TranslationPlanCache"/>
/// and applies the source-typed result to the underlying query.
/// </summary>
internal sealed class UseAsDataSourceQueryable<TSource, TDestination>
    : IUseAsDataSourceOrdered<TSource, TDestination>
{
    private readonly IQueryable<TSource> _underlying;
    private readonly MapperConfiguration _configuration;
    private readonly TypePair _pair;

    internal UseAsDataSourceQueryable(IQueryable<TSource> underlying, MapperConfiguration configuration)
    {
        _underlying = underlying;
        _configuration = configuration;
        _pair = new TypePair(typeof(TSource), typeof(TDestination));
    }

    private Expression<Func<TSource, TResult>> Translate<TResult>(
        Expression<Func<TDestination, TResult>> destLambda)
    {
        var cached = TranslationPlanCacheRegistry.For(_configuration).GetOrTranslate(
            _pair, destLambda,
            () => ExpressionTranslator.Translate(_configuration.Internal_Registry, _pair, destLambda));
        return (Expression<Func<TSource, TResult>>)cached;
    }

    private static IOrderedQueryable<TSource> AsOrderedQueryable(IQueryable<TSource> q) =>
        q as IOrderedQueryable<TSource>
        ?? throw new InvalidOperationException(
            "ThenBy/ThenByDescending called on a non-ordered query. " +
            "Call OrderBy or OrderByDescending first.");

    // ---- Filtering ----
    public IUseAsDataSourceQueryable<TSource, TDestination> Where(
        Expression<Func<TDestination, bool>> predicate) =>
        new UseAsDataSourceQueryable<TSource, TDestination>(
            _underlying.Where(Translate(predicate)),
            _configuration);

    // ---- Ordering ----
    public IUseAsDataSourceOrdered<TSource, TDestination> OrderBy<TKey>(
        Expression<Func<TDestination, TKey>> keySelector) =>
        new UseAsDataSourceQueryable<TSource, TDestination>(
            _underlying.OrderBy(Translate(keySelector)),
            _configuration);

    public IUseAsDataSourceOrdered<TSource, TDestination> OrderByDescending<TKey>(
        Expression<Func<TDestination, TKey>> keySelector) =>
        new UseAsDataSourceQueryable<TSource, TDestination>(
            _underlying.OrderByDescending(Translate(keySelector)),
            _configuration);

    public IUseAsDataSourceOrdered<TSource, TDestination> ThenBy<TKey>(
        Expression<Func<TDestination, TKey>> keySelector) =>
        new UseAsDataSourceQueryable<TSource, TDestination>(
            AsOrderedQueryable(_underlying).ThenBy(Translate(keySelector)),
            _configuration);

    public IUseAsDataSourceOrdered<TSource, TDestination> ThenByDescending<TKey>(
        Expression<Func<TDestination, TKey>> keySelector) =>
        new UseAsDataSourceQueryable<TSource, TDestination>(
            AsOrderedQueryable(_underlying).ThenByDescending(Translate(keySelector)),
            _configuration);

    // ---- Paging ----
    public IUseAsDataSourceQueryable<TSource, TDestination> Skip(int count) =>
        new UseAsDataSourceQueryable<TSource, TDestination>(_underlying.Skip(count), _configuration);

    public IUseAsDataSourceQueryable<TSource, TDestination> Take(int count) =>
        new UseAsDataSourceQueryable<TSource, TDestination>(_underlying.Take(count), _configuration);

    // ---- Terminal predicate (Task 10 fills in) ----
    public bool Any() => throw new NotImplementedException("Task 10");
    public bool Any(Expression<Func<TDestination, bool>> predicate) => throw new NotImplementedException("Task 10");
    public bool All(Expression<Func<TDestination, bool>> predicate) => throw new NotImplementedException("Task 10");
    public int Count() => throw new NotImplementedException("Task 10");
    public int Count(Expression<Func<TDestination, bool>> predicate) => throw new NotImplementedException("Task 10");
    public long LongCount() => throw new NotImplementedException("Task 10");
    public long LongCount(Expression<Func<TDestination, bool>> predicate) => throw new NotImplementedException("Task 10");
    public TDestination First() => throw new NotImplementedException("Task 10");
    public TDestination First(Expression<Func<TDestination, bool>> predicate) => throw new NotImplementedException("Task 10");
    public TDestination? FirstOrDefault() => throw new NotImplementedException("Task 10");
    public TDestination? FirstOrDefault(Expression<Func<TDestination, bool>> predicate) => throw new NotImplementedException("Task 10");
    public TDestination Single() => throw new NotImplementedException("Task 10");
    public TDestination Single(Expression<Func<TDestination, bool>> predicate) => throw new NotImplementedException("Task 10");
    public TDestination? SingleOrDefault() => throw new NotImplementedException("Task 10");
    public TDestination? SingleOrDefault(Expression<Func<TDestination, bool>> predicate) => throw new NotImplementedException("Task 10");
    public TDestination Last() => throw new NotImplementedException("Task 10");
    public TDestination Last(Expression<Func<TDestination, bool>> predicate) => throw new NotImplementedException("Task 10");
    public TDestination? LastOrDefault() => throw new NotImplementedException("Task 10");
    public TDestination? LastOrDefault(Expression<Func<TDestination, bool>> predicate) => throw new NotImplementedException("Task 10");

    // ---- Escape hatch (Task 10 fills in) ----
    public IQueryable<TDestination> AsQueryable() => throw new NotImplementedException("Task 10");

    // ---- IEnumerable (Task 10 fills in) ----
    public IEnumerator<TDestination> GetEnumerator() => throw new NotImplementedException("Task 10");
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
```

- [ ] **Step 9.4: Run tests to verify they pass**

```pwsh
cd C:\Repos\Atlas
dotnet test --nologo --filter "FullyQualifiedName~UseAsDataSourceWrapperTests"
```

Expected: 7 tests pass.

- [ ] **Step 9.5: Run full suite — zero regressions**

```pwsh
cd C:\Repos\Atlas
dotnet test --nologo 2>&1 | Select-String -Pattern "Passed:|Failed:|Skipped:"
```

Expected: total = 731 (Task 8) + 7 = 738. Failed = 0.

- [ ] **Step 9.6: Commit**

```pwsh
git add src/Atlas.Projections/Internal/UseAsDataSourceQueryable.cs tests/Atlas.Projections.Tests/UseAsDataSourceWrapperTests.cs
git commit -m "Wrapper operators: Where, OrderBy*, ThenBy*, Skip, Take (Task 9)`n`nFill in the seven non-terminal operator bodies on UseAsDataSourceQueryable<,>.`nEach operator: translates the destination-typed lambda via the cache+engine`npipeline (private Translate<TResult> helper centralizes the dispatch), applies`nthe source-typed result to _underlying via the corresponding LINQ operator,`nwraps in a new instance (immutable). Skip and Take pass through without`ntranslation (no lambda). ThenBy/ThenByDescending downcasts _underlying to`nIOrderedQueryable<TSource> via AsOrderedQueryable helper that throws a clear`nerror if called without a prior OrderBy. Terminal predicates and AsQueryable`nstill throw NotImplementedException — Task 10 fills those in."
```

---

## Task 10 — Wrapper terminal operators + `AsQueryable` + enumeration

**Goal:** Fill in the remaining wrapper bodies. Terminal predicate operators (`Any(predicate)`, `Count(predicate)`, etc.) translate the predicate then delegate to the underlying source-typed LINQ operator. No-predicate operators (`Any()`, `Count()`, `LongCount()`) delegate directly to the underlying. Materializing operators (`First`, `FirstOrDefault`, `Single`, `Last`, etc.) materialize via `AsQueryable()` (which routes through `ProjectTo<TDestination>`) and then call the corresponding LINQ method on the destination-typed result. Enumeration (`GetEnumerator`) delegates to `AsQueryable().GetEnumerator()`.

**Files:**
- Modify: `C:\Repos\Atlas\src\Atlas.Projections\Internal\UseAsDataSourceQueryable.cs`
- Modify: `C:\Repos\Atlas\tests\Atlas.Projections.Tests\UseAsDataSourceWrapperTests.cs`

**Allowlist:** the two files above.

- [ ] **Step 10.1: Write failing tests for terminal operators + enumeration**

Append to `UseAsDataSourceWrapperTests.cs`:

```csharp
public class UseAsDataSourceTerminalOperatorTests
{
    private static MapperConfiguration BuildCfg() =>
        new MapperConfiguration(c => c.CreateMap<UEDS_WrapperSrc, UEDS_WrapperDto>());

    private static IQueryable<UEDS_WrapperSrc> BuildSource() => new[]
    {
        new UEDS_WrapperSrc { Id = 1, Name = "Alice", Total = 50m },
        new UEDS_WrapperSrc { Id = 2, Name = "Bob",   Total = 150m },
        new UEDS_WrapperSrc { Id = 3, Name = "Carol", Total = 250m },
    }.AsQueryable();

    [Fact]
    public void Any_NoPredicate_DelegatesToUnderlying()
    {
        var cfg = BuildCfg();
        var wrapper = BuildSource().UseAsDataSource(cfg).For<UEDS_WrapperDto>();

        Assert.True(wrapper.Any());
    }

    [Fact]
    public void Any_WithPredicate_TranslatesAndDelegates()
    {
        var cfg = BuildCfg();
        var wrapper = BuildSource().UseAsDataSource(cfg).For<UEDS_WrapperDto>();

        Assert.True(wrapper.Any(d => d.Total > 100m));
        Assert.False(wrapper.Any(d => d.Total > 1000m));
    }

    [Fact]
    public void All_TranslatesAndDelegates()
    {
        var cfg = BuildCfg();
        var wrapper = BuildSource().UseAsDataSource(cfg).For<UEDS_WrapperDto>();

        Assert.True(wrapper.All(d => d.Total > 0m));
        Assert.False(wrapper.All(d => d.Total > 100m));
    }

    [Fact]
    public void Count_NoPredicate_DelegatesToUnderlying()
    {
        var cfg = BuildCfg();
        var wrapper = BuildSource().UseAsDataSource(cfg).For<UEDS_WrapperDto>();

        Assert.Equal(3, wrapper.Count());
    }

    [Fact]
    public void Count_WithPredicate_TranslatesAndDelegates()
    {
        var cfg = BuildCfg();
        var wrapper = BuildSource().UseAsDataSource(cfg).For<UEDS_WrapperDto>();

        Assert.Equal(2, wrapper.Count(d => d.Total > 100m));
    }

    [Fact]
    public void First_WithPredicate_MaterializesViaProjectTo()
    {
        var cfg = BuildCfg();
        var wrapper = BuildSource().UseAsDataSource(cfg).For<UEDS_WrapperDto>();

        var dto = wrapper.First(d => d.Total > 100m);
        Assert.True(dto.Total > 100m);
    }

    [Fact]
    public void FirstOrDefault_WithPredicate_NoMatch_ReturnsNull()
    {
        var cfg = BuildCfg();
        var wrapper = BuildSource().UseAsDataSource(cfg).For<UEDS_WrapperDto>();

        var dto = wrapper.FirstOrDefault(d => d.Total > 1000m);
        Assert.Null(dto);
    }

    [Fact]
    public void AsQueryable_ReturnsTranslatedDestinationQuery()
    {
        var cfg = BuildCfg();
        var wrapper = BuildSource().UseAsDataSource(cfg).For<UEDS_WrapperDto>();

        var q = wrapper.AsQueryable();
        var list = q.ToList();
        Assert.Equal(3, list.Count);
        Assert.Contains(list, d => d.Name == "Alice");
    }

    [Fact]
    public void Enumeration_TriggersProjectTo()
    {
        var cfg = BuildCfg();
        var wrapper = BuildSource().UseAsDataSource(cfg).For<UEDS_WrapperDto>();

        var list = wrapper.ToList();
        Assert.Equal(3, list.Count);
    }

    [Fact]
    public void WhereThenEnumerate_TranslatesAndProjects()
    {
        var cfg = BuildCfg();
        var wrapper = BuildSource().UseAsDataSource(cfg).For<UEDS_WrapperDto>();

        var filtered = wrapper.Where(d => d.Total > 100m).ToList();
        Assert.Equal(2, filtered.Count);
        Assert.All(filtered, d => Assert.True(d.Total > 100m));
    }

    [Fact]
    public void OrderByThenEnumerate_TranslatesAndProjectsOrdered()
    {
        var cfg = BuildCfg();
        var wrapper = BuildSource().UseAsDataSource(cfg).For<UEDS_WrapperDto>();

        var sorted = wrapper.OrderByDescending(d => d.Total).ToList();
        Assert.Equal(3, sorted.Count);
        Assert.Equal(250m, sorted[0].Total);
        Assert.Equal(150m, sorted[1].Total);
        Assert.Equal(50m,  sorted[2].Total);
    }
}
```

- [ ] **Step 10.2: Run tests to verify they fail**

```pwsh
cd C:\Repos\Atlas
dotnet test --nologo --filter "FullyQualifiedName~UseAsDataSourceTerminalOperatorTests"
```

Expected: tests fail with `NotImplementedException`.

- [ ] **Step 10.3: Replace the terminal-operator + AsQueryable + GetEnumerator stubs**

In `UseAsDataSourceQueryable.cs`, replace the stub region (everything from `// ---- Terminal predicate (Task 10 fills in) ----` to the end of the class) with:

```csharp
    // ---- Terminal predicate ----
    public bool Any() => _underlying.Any();
    public bool Any(Expression<Func<TDestination, bool>> predicate) => _underlying.Any(Translate(predicate));
    public bool All(Expression<Func<TDestination, bool>> predicate) => _underlying.All(Translate(predicate));

    public int Count() => _underlying.Count();
    public int Count(Expression<Func<TDestination, bool>> predicate) => _underlying.Count(Translate(predicate));
    public long LongCount() => _underlying.LongCount();
    public long LongCount(Expression<Func<TDestination, bool>> predicate) => _underlying.LongCount(Translate(predicate));

    public TDestination First() => AsQueryable().First();
    public TDestination First(Expression<Func<TDestination, bool>> predicate) =>
        new UseAsDataSourceQueryable<TSource, TDestination>(
            _underlying.Where(Translate(predicate)), _configuration).AsQueryable().First();
    public TDestination? FirstOrDefault() => AsQueryable().FirstOrDefault();
    public TDestination? FirstOrDefault(Expression<Func<TDestination, bool>> predicate) =>
        new UseAsDataSourceQueryable<TSource, TDestination>(
            _underlying.Where(Translate(predicate)), _configuration).AsQueryable().FirstOrDefault();

    public TDestination Single() => AsQueryable().Single();
    public TDestination Single(Expression<Func<TDestination, bool>> predicate) =>
        new UseAsDataSourceQueryable<TSource, TDestination>(
            _underlying.Where(Translate(predicate)), _configuration).AsQueryable().Single();
    public TDestination? SingleOrDefault() => AsQueryable().SingleOrDefault();
    public TDestination? SingleOrDefault(Expression<Func<TDestination, bool>> predicate) =>
        new UseAsDataSourceQueryable<TSource, TDestination>(
            _underlying.Where(Translate(predicate)), _configuration).AsQueryable().SingleOrDefault();

    public TDestination Last() => AsQueryable().Last();
    public TDestination Last(Expression<Func<TDestination, bool>> predicate) =>
        new UseAsDataSourceQueryable<TSource, TDestination>(
            _underlying.Where(Translate(predicate)), _configuration).AsQueryable().Last();
    public TDestination? LastOrDefault() => AsQueryable().LastOrDefault();
    public TDestination? LastOrDefault(Expression<Func<TDestination, bool>> predicate) =>
        new UseAsDataSourceQueryable<TSource, TDestination>(
            _underlying.Where(Translate(predicate)), _configuration).AsQueryable().LastOrDefault();

    // ---- Escape hatch ----
    public IQueryable<TDestination> AsQueryable() => _underlying.ProjectTo<TDestination>(_configuration);

    // ---- IEnumerable ----
    public IEnumerator<TDestination> GetEnumerator() => AsQueryable().GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
```

The `First[OrDefault](predicate)` / `Single[OrDefault](predicate)` / `Last[OrDefault](predicate)` overloads each:
1. Apply `Where(translatedPredicate)` to the underlying source query.
2. Wrap in a new `UseAsDataSourceQueryable<,>` (so caching for the `Where` predicate is preserved).
3. Materialize via `AsQueryable()` (which calls `ProjectTo<TDestination>`).
4. Apply the corresponding LINQ-to-Objects terminal operator on the materialized destination query.

Note the design choice (per §11 O2): `First()` materializes via ProjectTo's SQL `TOP 1`, not by fetching the source then mapping in-memory. Single SQL round-trip.

- [ ] **Step 10.4: Run tests to verify they pass**

```pwsh
cd C:\Repos\Atlas
dotnet test --nologo --filter "FullyQualifiedName~UseAsDataSourceTerminalOperatorTests"
```

Expected: 11 tests pass.

- [ ] **Step 10.5: Run full suite — zero regressions**

```pwsh
cd C:\Repos\Atlas
dotnet test --nologo 2>&1 | Select-String -Pattern "Passed:|Failed:|Skipped:"
```

Expected: total = 738 (Task 9) + 11 = 749. Failed = 0.

- [ ] **Step 10.6: Commit**

```pwsh
git add src/Atlas.Projections/Internal/UseAsDataSourceQueryable.cs tests/Atlas.Projections.Tests/UseAsDataSourceWrapperTests.cs
git commit -m "Wrapper terminal operators + AsQueryable + enumeration (Task 10)`n`nFill in the remaining wrapper bodies. Any(), Count(), LongCount() (no predicate)`ndelegate directly to _underlying — fastest path; no projection needed. Any(p),`nAll(p), Count(p), LongCount(p) translate the predicate via Translate<bool> and`ndelegate to the underlying source-typed LINQ operator. First[OrDefault](p),`nSingle[OrDefault](p), Last[OrDefault](p) apply Where(translated), wrap in a new`ninstance (preserves cache invariants), materialize via AsQueryable() — single`nSQL round-trip with TOP 1 (per design §11 O2). AsQueryable() returns`n_underlying.ProjectTo<TDestination>(_configuration) — the existing PR #1`nmachinery handles the source→dest projection. GetEnumerator() delegates to`nAsQueryable().GetEnumerator() so foreach/ToList/ToArray naturally trigger the`nProjectTo materialization."
```

---

## Task 11 — Compatibility tests (composition with v2 features)

**Goal:** Verify the wrapper composes correctly with the existing v2 features (per design §8). Tests run against in-memory `IQueryable<>` (LINQ-to-Objects) — sufficient for behavioral coverage. EF Core SQL emission moves to Task 12.

**Files:**
- Create: `C:\Repos\Atlas\tests\Atlas.Projections.Tests\UseAsDataSourceCompatibilityTests.cs`

**Allowlist:** the file above.

- [ ] **Step 11.1: Write tests for v2-feature compositions**

Create `tests/Atlas.Projections.Tests/UseAsDataSourceCompatibilityTests.cs`:

```csharp
using System.Linq.Expressions;
using Atlas;

namespace Atlas.Projections.Tests;

public class UseAsDataSourceCompatibilityTests
{
    [Fact]
    public void AttributeDeclaredTypeMap_WorksThroughWrapper()
    {
        // [AutoMap] from PR #12 produces a normal TypeMap; wrapper doesn't care about origin.
        var cfg = new MapperConfiguration(c => c.AddMaps(typeof(UEDS_AttrSrc).Assembly));
        // Note: the assembly contains many bad fixtures from Task 3; tolerate that.
        try { } catch (AtlasConfigurationException) { /* tolerate — we only care this DTO maps */ }
        // Build cfg with explicit registration to avoid relying on assembly scan (cleaner).
        var cleanCfg = new MapperConfiguration(c => c.CreateMap<UEDS_AttrSrc, UEDS_AttrDto>());

        var src = new[] { new UEDS_AttrSrc { Id = 1, Name = "Alice" } }.AsQueryable();
        var list = src.UseAsDataSource(cleanCfg).For<UEDS_AttrDto>().ToList();
        Assert.Single(list);
        Assert.Equal("Alice", list[0].Name);
    }

    [Fact]
    public void NullSubstitute_TranslatesViaCoalesce()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<UEDS_NullSubSrc, UEDS_NullSubDto>()
                .ForMember(d => d.Email, opt => opt.NullSubstitute("(none)")));

        var src = new[]
        {
            new UEDS_NullSubSrc { Id = 1, Email = null },
            new UEDS_NullSubSrc { Id = 2, Email = "alice@x" },
        }.AsQueryable();

        var withNone = src.UseAsDataSource(cfg).For<UEDS_NullSubDto>()
            .Where(d => d.Email == "(none)")
            .ToList();
        Assert.Single(withNone);
        Assert.Equal(1, withNone[0].Id);
    }

    [Fact]
    public void OpenGenericMaterializedClosedPair_WorksThroughWrapper()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap(typeof(UEDS_OpenSrc<>), typeof(UEDS_OpenDto<>)));

        var src = new[] { new UEDS_OpenSrc<int> { Value = 42 } }.AsQueryable();
        var list = src.UseAsDataSource(cfg).For<UEDS_OpenDto<int>>().ToList();
        Assert.Single(list);
        Assert.Equal(42, list[0].Value);
    }

    [Fact]
    public void HooksTypeMap_RejectedAtFor()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<UEDS_HookSrc, UEDS_HookDto>().BeforeMap((s, d) => { }));

        var src = Array.Empty<UEDS_HookSrc>().AsQueryable();
        var ex = Assert.Throws<AtlasProjectionException>(() =>
            src.UseAsDataSource(cfg).For<UEDS_HookDto>().Where(d => d.Id > 0).ToList());

        Assert.Contains(ex.Diagnostics, d => d.Reason.Contains("hook"));
    }

    [Fact]
    public void DynamicTypeMap_RejectedAtFor()
    {
        // Dictionary<string, object> source triggers IsDynamic auto-materialization.
        var cfg = new MapperConfiguration(c => c.CreateMap<UEDS_DynSrc, UEDS_DynDto>());
        var src = new[]
        {
            new Dictionary<string, object> { ["Id"] = 1, ["Name"] = "x" }
        }.AsQueryable();

        // Direct UseAsDataSource against a Dictionary<string,object> source triggers
        // dynamic-shape detection at GetTypeMap time — translator's Phase 2 dual-gate
        // rejects.
        var ex = Assert.Throws<AtlasProjectionException>(() =>
            src.UseAsDataSource(cfg).For<UEDS_DynDto>().Where(d => true).ToList());

        Assert.Contains(ex.Diagnostics, d => d.Reason.Contains("dynamic"));
    }

    [Fact]
    public void ReverseMapTypeMap_WorksInBothDirections()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<UEDS_RevSrc, UEDS_RevDto>().ReverseMap());

        var srcQuery = new[] { new UEDS_RevSrc { Id = 1, Name = "A" } }.AsQueryable();
        var dtoList = srcQuery.UseAsDataSource(cfg).For<UEDS_RevDto>().ToList();
        Assert.Single(dtoList);

        var dtoQuery = new[] { new UEDS_RevDto { Id = 2, Name = "B" } }.AsQueryable();
        var srcList = dtoQuery.UseAsDataSource(cfg).For<UEDS_RevSrc>().ToList();
        Assert.Single(srcList);
        Assert.Equal(2, srcList[0].Id);
    }

    [Fact]
    public void GlobalValueTransformer_FiresOnTranslatedMembers()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.ValueTransformers.Add<string>(s => s + "!");
            c.CreateMap<UEDS_TransSrc, UEDS_TransDto>();
        });

        var src = new[] { new UEDS_TransSrc { Id = 1, Name = "Alice" } }.AsQueryable();
        var list = src.UseAsDataSource(cfg).For<UEDS_TransDto>().ToList();
        Assert.Equal("Alice!", list[0].Name);
    }
}

[AutoMap(typeof(UEDS_AttrSrc))]
public class UEDS_AttrDto { public int Id { get; set; } public string Name { get; set; } = ""; }
public class UEDS_AttrSrc { public int Id { get; set; } public string Name { get; set; } = ""; }

public class UEDS_NullSubSrc { public int Id { get; set; } public string? Email { get; set; } }
public class UEDS_NullSubDto { public int Id { get; set; } public string Email { get; set; } = ""; }

public class UEDS_OpenSrc<T> { public T Value { get; set; } = default!; }
public class UEDS_OpenDto<T> { public T Value { get; set; } = default!; }

public class UEDS_HookSrc { public int Id { get; set; } }
public class UEDS_HookDto { public int Id { get; set; } }

public class UEDS_DynSrc { public int Id { get; set; } public string Name { get; set; } = ""; }
public class UEDS_DynDto { public int Id { get; set; } public string Name { get; set; } = ""; }

public class UEDS_RevSrc { public int Id { get; set; } public string Name { get; set; } = ""; }
public class UEDS_RevDto { public int Id { get; set; } public string Name { get; set; } = ""; }

public class UEDS_TransSrc { public int Id { get; set; } public string Name { get; set; } = ""; }
public class UEDS_TransDto { public int Id { get; set; } public string Name { get; set; } = ""; }
```

**Note:** the dynamic-typemap rejection test uses `Dictionary<string, object>` source — this triggers Atlas's existing dynamic-shape detection. The test verifies that the translator's Phase 2 dual-gate correctly rejects.

- [ ] **Step 11.2: Run tests**

```pwsh
cd C:\Repos\Atlas
dotnet test --nologo --filter "FullyQualifiedName~UseAsDataSourceCompatibilityTests"
```

Expected: 7 tests pass. If any fail, investigate — most likely cause is a v2-feature interaction the design didn't anticipate.

- [ ] **Step 11.3: Run full suite — zero regressions**

```pwsh
cd C:\Repos\Atlas
dotnet test --nologo 2>&1 | Select-String -Pattern "Passed:|Failed:|Skipped:"
```

Expected: total = 749 (Task 10) + 7 = 756. Failed = 0.

- [ ] **Step 11.4: Commit**

```pwsh
git add tests/Atlas.Projections.Tests/UseAsDataSourceCompatibilityTests.cs
git commit -m "UseAsDataSource compatibility tests (Task 11)`n`nVerify the wrapper composes correctly with existing v2 features per design §8.`n7 tests covering: attribute-declared TypeMap (#12), NullSubstitute as Coalesce`n(#8), open-generic materialized closed pair (#9), hooks rejection (#5),`ndynamic-shape rejection (#10), ReverseMap in both directions (#4), global`nvalue transformer firing on translated members (#6 global scope). All tests`nrun against in-memory IQueryable (LINQ-to-Objects); EF Core SQL emission moves`nto Task 12."
```

---

## Task 12 — EF Core SQL emission tests

**Goal:** Verify the wrapper produces the expected SQL when the underlying `IQueryable` is an EF Core query. Uses the existing `Atlas.Projections.Tests.EFCore` test project (which has the EF Core in-memory provider configured).

**Files:**
- Create: `C:\Repos\Atlas\tests\Atlas.Projections.Tests.EFCore\UseAsDataSourceEFCoreTests.cs`

**Allowlist:** the file above.

- [ ] **Step 12.1: Inspect existing EF Core test patterns**

```pwsh
cd C:\Repos\Atlas
dir tests\Atlas.Projections.Tests.EFCore
Get-Content tests\Atlas.Projections.Tests.EFCore\*.cs -TotalCount 80 | Select-Object -First 80
```

Use the existing test pattern as a template — same `DbContext`, same fixture style, same SQL-capture pattern.

- [ ] **Step 12.2: Write tests verifying SQL emission**

Create `tests/Atlas.Projections.Tests.EFCore/UseAsDataSourceEFCoreTests.cs`. The exact contents depend on the existing test infrastructure in this project; follow the patterns there. Skeleton:

```csharp
using Atlas;
using Atlas.Projections;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Projections.Tests.EFCore;

public class UseAsDataSourceEFCoreTests
{
    private static (DbContext db, MapperConfiguration cfg) Setup()
    {
        // Use the same DbContext + cfg pattern as existing EFCore tests in this project.
        // (Implementer: copy from sibling tests' Setup/fixture pattern.)
        throw new NotImplementedException("Mirror existing EFCore test setup.");
    }

    // The following tests should each:
    //   1. Build the wrapper chain.
    //   2. Capture the SQL emitted by EF Core (via DbContext.Database.Log or similar).
    //   3. Assert the expected SQL fragments appear.
    //
    // EF Core 7+ in-memory provider produces relational SQL; the SqlServer provider would
    // produce dialect-specific SQL. Implementer: use whatever provider the existing
    // EFCore test fixtures use.

    [Fact]
    public void Where_FlatPredicate_EmitsExpectedSql() { /* implementer */ }

    [Fact]
    public void Where_FlattenedMember_EmitsJoinAndWhere() { /* implementer */ }

    [Fact]
    public void Where_CustomExpression_EmitsConcatenation() { /* implementer */ }

    [Fact]
    public void OrderBy_EmitsOrderByClause() { /* implementer */ }

    [Fact]
    public void SkipTake_EmitsOffsetFetchOrLimit() { /* implementer */ }

    [Fact]
    public void AnyPredicate_EmitsExists() { /* implementer */ }

    [Fact]
    public void CountPredicate_EmitsCountFiltered() { /* implementer */ }

    [Fact]
    public void Combined_PredicateOrderingPaging_EmitsWellFormedSql() { /* implementer */ }
}
```

**Implementer guidance:** the `tests/Atlas.Projections.Tests.EFCore/` project already has working tests for `ProjectTo` against an EF Core in-memory provider. Copy the setup pattern exactly:
- Same DbContext class (or build a new local one with `UEDS_*` entities).
- Same SQL-capture mechanism (`DbContext.Database.Log` via a logger factory, or whatever the existing tests use).
- Same `Setup()` helper structure.
- Use the existing fixture entities if they fit; otherwise add `UEDS_EFCore*` fixtures alongside.

Per design §14.1, the EF Core test count is ~8 tests. Each test asserts a specific SQL pattern (e.g., `WHERE`, `INNER JOIN`, `ORDER BY`, `OFFSET`/`FETCH`). If the existing EF Core test infrastructure makes capturing SQL hard, fall back to behavioral tests (assert `.ToList()` returns the expected rows after the SQL roundtrips).

- [ ] **Step 12.3: Run tests**

```pwsh
cd C:\Repos\Atlas
dotnet test --nologo --filter "FullyQualifiedName~UseAsDataSourceEFCoreTests"
```

Expected: ~8 tests pass.

- [ ] **Step 12.4: Run full suite — zero regressions**

```pwsh
cd C:\Repos\Atlas
dotnet test --nologo 2>&1 | Select-String -Pattern "Passed:|Failed:|Skipped:"
```

Expected: total = 756 (Task 11) + 8 = 764. Failed = 0.

- [ ] **Step 12.5: Commit**

```pwsh
git add tests/Atlas.Projections.Tests.EFCore/UseAsDataSourceEFCoreTests.cs
git commit -m "EF Core SQL emission tests (Task 12)`n`nVerify the wrapper produces the expected SQL when the underlying IQueryable is`nan EF Core query. 8 tests covering: flat predicate (WHERE), flattened member`n(INNER JOIN + WHERE), CustomExpression (string concatenation in SQL), OrderBy`n(ORDER BY), Skip/Take (OFFSET/FETCH), Any(predicate) (EXISTS), Count(predicate)`n(COUNT with WHERE), combined predicate+ordering+paging (well-formed compound`nSQL). Test setup mirrors existing Atlas.Projections.Tests.EFCore patterns."
```

---

## Task 13 — Integration tests + README delta

**Goal:** End-to-end DI integration tests (multiple ops chained, mixed source-side + destination-side, AsQueryable escape hatch with async LINQ workaround) plus the README section. Final acceptance check.

**Files:**
- Create: `C:\Repos\Atlas\tests\Atlas.Projections.Tests\UseAsDataSourceIntegrationTests.cs`
- Modify: `C:\Repos\Atlas\README.md`

**Allowlist:** the two files above.

- [ ] **Step 13.1: Write integration tests**

Create `tests/Atlas.Projections.Tests/UseAsDataSourceIntegrationTests.cs`:

```csharp
using System.Linq.Expressions;
using Atlas;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Projections.Tests;

public class UseAsDataSourceIntegrationTests
{
    private static (IQueryable<UEDS_IntSrc> source, MapperConfiguration cfg) Setup()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<UEDS_IntSrc, UEDS_IntDto>());
        var source = new[]
        {
            new UEDS_IntSrc { Id = 1, Name = "Alice", Total = 50m },
            new UEDS_IntSrc { Id = 2, Name = "Bob", Total = 150m },
            new UEDS_IntSrc { Id = 3, Name = "Carol", Total = 250m },
        }.AsQueryable();
        return (source, cfg);
    }

    [Fact]
    public void DI_ResolvedMapperConfiguration_WorksThroughWrapper()
    {
        var services = new ServiceCollection();
        services.AddAtlas(c => c.CreateMap<UEDS_IntSrc, UEDS_IntDto>());
        using var sp = services.BuildServiceProvider();
        var cfg = sp.GetRequiredService<MapperConfiguration>();

        var (source, _) = Setup();
        var list = source.UseAsDataSource(cfg).For<UEDS_IntDto>()
            .Where(d => d.Total > 100m)
            .OrderBy(d => d.Total)
            .ToList();

        Assert.Equal(2, list.Count);
        Assert.Equal("Bob", list[0].Name);
        Assert.Equal("Carol", list[1].Name);
    }

    [Fact]
    public void MixedSourceAndDestinationOps_ApplyInOrder()
    {
        var (source, cfg) = Setup();

        // Pre-filter on source side (Total > 0), then DTO-typed ops.
        var list = source
            .Where(s => s.Total > 0m)
            .UseAsDataSource(cfg).For<UEDS_IntDto>()
            .Where(d => d.Total < 200m)
            .OrderBy(d => d.Id)
            .ToList();

        Assert.Equal(2, list.Count);
        Assert.Equal(1, list[0].Id);
        Assert.Equal(2, list[1].Id);
    }

    [Fact]
    public void MultiOpChain_AllFourCategories_ProducesCorrectResult()
    {
        var (source, cfg) = Setup();

        // Filter + order + paging + terminal-predicate.
        var any = source.UseAsDataSource(cfg).For<UEDS_IntDto>()
            .Where(d => d.Total > 0m)
            .OrderBy(d => d.Id)
            .Skip(1)
            .Take(1)
            .Any(d => d.Name == "Bob");

        Assert.True(any);
    }

    [Fact]
    public void DirectUseHelper_ProducesEquivalentResult()
    {
        var (source, cfg) = Setup();

        Expression<Func<UEDS_IntDto, bool>> destPredicate = d => d.Total > 100m;
        var srcPredicate = cfg.Translate<UEDS_IntSrc, UEDS_IntDto, bool>(destPredicate);

        var directList = source.Where(srcPredicate).ProjectTo<UEDS_IntDto>(cfg).ToList();
        var wrapperList = source.UseAsDataSource(cfg).For<UEDS_IntDto>().Where(destPredicate).ToList();

        Assert.Equal(directList.Count, wrapperList.Count);
        Assert.Equal(directList.Select(d => d.Id), wrapperList.Select(d => d.Id));
    }

    [Fact]
    public void MultipleUseAsDataSourceCalls_OnSameSource_ComposeIndependently()
    {
        var (source, cfg) = Setup();

        var list1 = source.UseAsDataSource(cfg).For<UEDS_IntDto>().Where(d => d.Total > 100m).ToList();
        var list2 = source.UseAsDataSource(cfg).For<UEDS_IntDto>().Where(d => d.Total < 100m).ToList();

        Assert.Equal(2, list1.Count);
        Assert.Single(list2);
        Assert.Equal(1, list2[0].Id);
    }

    [Fact]
    public void AsQueryable_EscapeHatch_WorksWithStandardLinq()
    {
        var (source, cfg) = Setup();

        // Drop down to IQueryable<TDest>, use Select (not on the wrapper surface).
        var totals = source.UseAsDataSource(cfg).For<UEDS_IntDto>()
            .Where(d => d.Total > 0m)
            .AsQueryable()
            .Select(d => d.Total)
            .ToList();

        Assert.Equal(3, totals.Count);
    }
}

public class UEDS_IntSrc
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Total { get; set; }
}

public class UEDS_IntDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Total { get; set; }
}
```

- [ ] **Step 13.2: Run tests to verify they pass**

```pwsh
cd C:\Repos\Atlas
dotnet test --nologo --filter "FullyQualifiedName~UseAsDataSourceIntegrationTests"
```

Expected: 6 tests pass.

- [ ] **Step 13.3: Add the README section**

Open `C:\Repos\Atlas\README.md` and locate the existing section "Attribute-based configuration" (added in PR #12). Insert the new section AFTER it (and before "Migration notes" if that subsection exists at this level, or at the end of the configuration content).

Add the following content:

```markdown
## Expression translation (UseAsDataSource)

Wrap an `IQueryable<TSource>` and write filtering, sorting, and paging in destination-DTO terms. Atlas translates the destination-typed lambdas back to source-typed expressions before they hit your LINQ provider.

```csharp
public class OrderProfile : MapperProfile
{
    public OrderProfile() { CreateMap<Order, OrderDto>(); }
}

// In a controller:
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
| --- | --- |
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
var srcPredicate = mapperConfig.Translate<Order, OrderDto, bool>(d => d.CustomerName == "Alice");
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
    public static readonly Expression<Func<OrderDto, bool>> Active = d => d.Status == "Active";
}

// Both calls hit the cache after the first one:
db.Orders.UseAsDataSource(cfg).For<OrderDto>().Where(OrderFilters.Active).ToList();
db.Orders.UseAsDataSource(cfg).For<OrderDto>().Where(OrderFilters.Active).ToList();
```

Freshly-constructed lambdas (`d => d.Total > 100`) miss the cache (different reference each call). They translate once each; correctness unchanged.

### Limitations

- **Inner lambdas on collection-typed destination members are not translated** in v1. `d => d.Lines.Any(l => l.Total > 100)` throws at translate time; rewrite the predicate against the source (`db.Orders.Where(o => o.Lines.Any(l => l.Total > 100)).UseAsDataSource(cfg).For<OrderDto>()`) or use `AsQueryable()` and operate on the materialized destination collection.
- **Derived-type dispatch via inheritance is not supported.** A wrapper bound to a base typemap can't translate predicates against derived-only properties. Workaround: `query.OfType<OnlineOrder>().UseAsDataSource(cfg).For<OnlineOrderDto>()`.
- **Bare-parameter usage** (`d => d == other` or `d => SomeFn(d)`) is not pre-detected. The LINQ provider's standard error fires at query execution.

### Compatibility with v2 features

| Feature | UseAsDataSource v1 |
| --- | --- |
| ProjectTo (#1) | ✓ Composes via enumeration |
| Inheritance (#2) | ✓ Root only; derived-dispatch limited |
| Enum surface (#3) | ✓ Works |
| ReverseMap (#4) | ✓ Works |
| `ForPath` (#4) | ✗ Rejected by existing dual-gate |
| Hooks (#5) | ✗ Rejected by existing dual-gate |
| Value transformers (#6 global/typemap) | ✓ Works |
| Profile-scope transformers (#6) | ✗ Don't fire (`OriginatingProfile == null`) |
| Conditional mapping (#7) | ✓ Inlined |
| Null substitution (#8) | ✓ Translates to `COALESCE` |
| Open generics (#9) | ✓ Closed pair via lazy materialization |
| Dynamic mapping (#10) | ✗ Rejected by existing dual-gate |
| `PreserveReferences` (#11) | ✗ Rejected by existing dual-gate |
| Attribute config (#12) | ✓ Works |
```

If the README has a deferred-features list mentioning #13, also remove the line that lists it (since the feature is shipping).

- [ ] **Step 13.4: Verify the README renders cleanly**

```pwsh
cd C:\Repos\Atlas
git diff README.md | Select-Object -First 200
```

Spot-check that the new section's markdown renders correctly (table headers, code fences, list bullets).

- [ ] **Step 13.5: Run the full test suite as final acceptance check**

```pwsh
cd C:\Repos\Atlas
dotnet test --nologo 2>&1 | Select-String -Pattern "Passed:|Failed:|Skipped:"
```

Expected: total = 764 (Task 12) + 6 = ~770. Failed = 0. (Approximate; the exact count varies.)

- [ ] **Step 13.6: Commit**

```pwsh
git add tests/Atlas.Projections.Tests/UseAsDataSourceIntegrationTests.cs README.md
git commit -m "Integration tests + README — add Expression translation section (Task 13)`n`n6 integration tests covering DI-resolved MapperConfiguration through wrapper,`nmixed source-side+destination-side ops, multi-op chain (all 4 categories),`ndirect-use Translate<> helper equivalence to wrapper, multiple UseAsDataSource`ncalls on same source composing independently, AsQueryable escape hatch with`nstandard LINQ Select. README adds 'Expression translation (UseAsDataSource)'`nsection between the Attribute-config and prior sections — covers minimal`nexample, operator scope table, AsQueryable escape hatch, direct-use helper,`nrejection rule, caching guidance, limitations (inner lambdas on collections,`nderived-type dispatch, bare-parameter usage), and v2-feature compatibility`ntable. Final acceptance: 770 PASS / 0 FAIL / 0 SKIP."
```

- [ ] **Step 13.7: Push branch and open PR**

```pwsh
git push -u origin feat/expression-translation
gh pr create --base main --head feat/expression-translation `
  --title "Atlas v2 #13: Expression Translation (UseAsDataSource)" `
  --body @"
Implements Atlas v2 feature #13 per ``docs/Atlas-Design-ExpressionTranslation.md`` (1879 lines, 15 sections; design merged at ``454d2ac``). The thirteenth and final v2 deferred feature.

## Summary

Adds expression translation as a parallel front-end to the existing ``Atlas.Projections.ProjectTo``. Two layers in ``Atlas.Projections``: an engine (``ExpressionTranslator``) that walks ``Expression<Func<TDest, TResult>>`` and produces ``Expression<Func<TSrc, TResult>>`` by substituting destination-typed member accesses with source expressions Atlas's typemaps already record (``PropertyMap.SourcePath``, ``PropertyMap.CustomExpression``); a wrapper (``UseAsDataSourceQueryable<,>``) that intercepts predicate/ordering/paging operators and applies translated lambdas to the underlying ``IQueryable<TSource>``. Enumeration delegates to ``ProjectTo<TDest>`` for the final source→destination shape.

## Operator surface (v1)

Filtering: ``Where``. Ordering: ``OrderBy*``, ``ThenBy*``. Paging: ``Skip``, ``Take``. Terminal predicate: ``Any``, ``All``, ``Count(predicate)``, ``First[OrDefault](predicate)``, ``Single[OrDefault](predicate)``, ``Last[OrDefault](predicate)``. Plus ``AsQueryable()`` escape hatch for ``Select``/``GroupBy``/async LINQ.

## Direct-use helper

``cfg.Translate<TSource, TDestination, TResult>(destExpr)`` returns a translated ``Expression<Func<TSource, TResult>>`` for power-user composition.

## Rejection coverage

Reuses existing ``ProjectionCompatibility.IsTypeMapProjectable`` dual-gate — same TypeMaps that reject ``ProjectTo`` (Hooks #5, PreserveReferences #11, Dynamic #10, ForPath #4) reject ``UseAsDataSource``. Per-member rejections at translate time: ``[Ignore]``'d, constant-mapped, unmapped destination members.

## Cache

Per-``MapperConfiguration`` cache keyed on ``(TypePair, lambda-reference-identity)`` via ``RuntimeHelpers.GetHashCode``. Catches ``static readonly Expression<>`` reuse; freshly-constructed lambdas translate once.

## Test count

Baseline 710 (post PR #12) → 770 (+60 net new), 0 failed.

## Atlas v2

This is the thirteenth and final v2 deferred feature. Atlas v2 complete.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
"@
```

---

## Final review

After Task 13, the PR is open and the test suite is green. Before merge:

1. **Holistic review the entire diff for cross-task issues.** Specific holistic checks for #13:
   - Engine + wrapper share state correctly (the wrapper uses the engine via the cache; the cache survives across operator chain steps).
   - Engine's `Reject` helper is consistent across all rejection sites — same prefix, same `ProjectionDiagnostic` shape.
   - The wrapper's terminal-predicate operators correctly route to `_underlying` for no-predicate cases (don't materialize unnecessarily) and to `AsQueryable` for materializing cases.
   - `AsQueryable()` consistently uses the existing `ProjectionExtensions.ProjectTo<TDestination>` — no parallel projection codepath.
   - The defensive `VisitMethodCall` (Task 6) doesn't false-positive on legitimate non-collection method calls (e.g., `d.Name.StartsWith("A")`).
2. **Memory updates post-merge.** Update `MEMORY.md` (mark all 13 v2 features shipped; remove "next up" suffix), `atlas_v2_design_docs_deferred.md` (#13 marked shipped + final wrap-up note), `feedback_atlas_v2_workflow.md` (test baseline 770; final v2 wrap-up note).
3. **Branch cleanup.** Delete `feat/expression-translation` locally and on remote.

---

**End of plan.**
