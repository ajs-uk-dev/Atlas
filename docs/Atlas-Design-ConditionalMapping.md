# Atlas v2 — Conditional Mapping (`Condition` / `PreCondition`)

> **Status:** Design approved 2026-05-05. Implementation plan: `Atlas-Plan-ConditionalMapping.md` (to be written next).
> **Spec inputs:** `Object-Mapping-Functional-Reference.md` §5.6 (Conditional mapping), `AutoMapper-Analysis.md` §5.8 (`Condition` / `PreCondition`).
> **Position in v2 roadmap:** Feature #7 of 13 deferred groups. Builds on v1 + ProjectTo (#1) + Inheritance (#2) + Enum (#3) + Reverse Mapping (#4) + Hooks (#5) + Value Transformers (#6).

---

## 1. Goals & Non-Goals

### 1.1 Goal

Add per-member predicates to Atlas: `PreCondition` (evaluated before source-side resolution; gates the entire member mapping including expensive resolution) and `Condition` (evaluated after resolution but before assignment; gates the assignment based on the resolved value). Both expressed as `Expression<Func<...>>` so they participate uniformly in `Map<>()` (compiled to a delegate) and `ProjectTo<>()` (inlined into the LINQ projection for SQL translation by the underlying provider).

The headline use case from `Object-Mapping-Functional-Reference.md` §5.6 — *"Pre-condition runs before any value resolution; use it when the resolution is expensive and would be wasted work if the condition fails. Condition runs after resolution but before assignment; use it when the cost is low and the predicate depends on the resolved value. Pipeline order: pre-condition → resolve value → condition → assign"* — works under this design and is testable end-to-end via a counter lambda.

### 1.2 In scope (v2 MVP)

1. New methods on `IMemberConfigurationExpression<TSource, TDestination, TMember>`:
   - `void PreCondition(Expression<Func<TSource, bool>> predicate)`
   - `void Condition(Expression<Func<TSource, TMember, bool>> predicate)` — second arg is the resolved value
2. Two new nullable fields on `PropertyMap`: `PreCondition: LambdaExpression?` and `Condition: LambdaExpression?`.
3. `MemberConfigurationExpression` plumbs the two predicates into the `PropertyMap` (last-call-wins for repeated calls inside the same callback, matching `MapFrom`).
4. `InheritanceMerger.CopyConfig` extension: copy both predicates from base PropertyMap to derived PropertyMap (the existing `IsExplicit` precedence machinery already handles the rest).
5. `ExecutionPlanBuilder` extension: a new `WrapWithConditions` helper used in `BuildPocoLambda` (property assigns + ctor args) and a new `BuildUpdateAssignWithConditions` helper used in `BuildUpdate`. Both produce no-op codegen when neither predicate is set.
6. `Atlas.Projections.ProjectionPlanBuilder` extension: a new `WrapProjectionWithConditions` helper applied to property bindings and ctor-arg sub-expressions, emitting a single `Expression.Condition` per binding so EF Core / other LINQ providers translate to SQL `CASE WHEN`.

### 1.3 Out of scope (deferred to a future v3 design doc)

- **Per-typemap predicates.** AutoMapper-style `cfg.CreateMap<S,D>().Condition(s => ...)` that gates the entire mapping. Per-member is the documented pattern in the reference doc; per-typemap is additive.
- **Per-call predicates.** Overriding the predicate at `Map<>()` invocation time. Pushes us into the context-bag territory we've explicitly deferred (see deferred feature #11 — reference handling).
- **Predicates that read the existing destination.** AutoMapper's `Condition((src, dest, srcMember, destMember, currentDest) => ...)` overload. Atlas would need to plumb the existing destination into both the in-memory codegen (already accessible in `BuildUpdate` as `destParam`, but not in `BuildPocoLambda` where the destination is a fresh local being constructed) and the projection codegen (where there is no existing destination at all). Adds API surface for a niche use case; defer until a real user need surfaces.
- **Delegate (non-Expression) overloads.** `Action<>` / `Func<>` variants would be in-memory-only and would split the projection translatability story. The `Expression<>` form covers all use cases for which a delegate would also work.
- **Auto-propagation across `.ReverseMap()`.** Per the established scope-A discipline (#4 ReverseMap, #5 Hooks, #6 ValueTransformers all do not auto-flip per-member options), the user reconfigures predicates on the reverse expression. Inheritance propagation (base → derived) IS in scope and uses the existing `IsExplicit` precedence machinery.
- **Validator pre-inspection of predicate translatability.** Untranslatable predicates fail at query-execution time with the LINQ provider's standard "expression cannot be translated" error. Same model as #6 (Value Transformers); same reason (would essentially reimplement EF Core's expression visitor).

### 1.4 Non-goals (out of scope permanently for this feature)

- Discovering predicates by attribute or convention without an explicit `PreCondition` / `Condition` call.
- A single combined `When(...)` API that subsumes both — the two predicates have fundamentally different roles (gate-resolution vs. gate-assignment) and the reference doc treats them as distinct.
- Predicates on `BeforeMap` / `AfterMap` hooks. Hooks are typed `IMappingAction<TS,TD>` (or `Action<TS,TD>` lambdas); the user can `if (predicate) ...` inside the hook body if needed.

---

## 2. Architecture Overview

### 2.1 What changes

- **`IMemberConfigurationExpression<,,>`** gains two methods (`PreCondition`, `Condition`).
- **`MemberConfigurationExpression<,,>`** captures both, applies them to the `PropertyMap` in `ApplyTo`.
- **`PropertyMap`** gains two `LambdaExpression?` fields.
- **`InheritanceMerger.CopyConfig`** extended to copy both fields (one line each).
- **`ExecutionPlanBuilder`** gains two helpers (`WrapWithConditions` for fresh-map / `BuildUpdateAssignWithConditions` for update-in-place). Three call-sites updated: ctor-arg loop in `BuildPocoLambda`, property-assign loop in `BuildPocoLambda`, property-assign loop in `BuildUpdate`.
- **`Atlas.Projections.ProjectionPlanBuilder`** gains one helper (`WrapProjectionWithConditions`). Two call-sites updated: ctor-arg loop and property-binding loop in `BuildBody`.

### 2.2 What does NOT change

- **`ConfigurationValidator`** — no new rules. The C# type system enforces predicate shape at the call site; null checks live in the fluent surface.
- **`TypeMap`** — no new fields. Predicates are per-member and live on `PropertyMap`.
- **`MapperRegistry`** — unchanged.
- **`ConventionEngine`** — unchanged. Conventions don't generate predicates; conventions generate source paths, and predicates are an opt-in user-explicit configuration.
- **`ReverseMapMirror`** — unchanged. Predicates do not auto-flip (per scope-A discipline).
- **`TransformerResolver`** (#6) — unchanged. Transformers and predicates are independent concerns; the codegen wraps both, with transformers innermost (operate on raw resolved value) and predicates outermost (gate the post-transform value).
- **`ProjectionCompatibility`** — unchanged. Predicates do NOT add a projection rejection (they translate). In contrast: hooks (#5) and `ForPath` (#4) do add rejections.
- **Build-time sequence** — unchanged. The current order (`InheritanceMerger.Resolve → ConventionEngine.ResolveMissingMembers → ReverseMapMirror.Mirror → TransformerResolver.Resolve → tm.Seal()`) does not need a new step. Predicate propagation happens inside `InheritanceMerger.MergeBaseConfig` via the extended `CopyConfig` — no separate pass.

### 2.3 Runtime path

Unchanged at the dispatch level. `IMapper.Map<TDest>(source)` is still a dictionary lookup → cached delegate invoke. The compiled delegate body for a `TypeMap` whose `PropertyMap`s have predicates differs only in that the property-assign expressions are wrapped with `Conditional` / `Block` / `IfThen` nodes. When neither predicate is set on a `PropertyMap`, the helpers fall through and emit the exact pre-feature codegen — no perf cost on maps that don't use predicates.

### 2.4 Why per-member only (no per-typemap or per-call)

Three reasons:

1. The reference doc (`Object-Mapping-Functional-Reference.md` §5.6) and the established AutoMapper-Analysis surface (§5.8) both describe `PreCondition` and `Condition` as per-member options inside the `ForMember` callback. A per-typemap or per-call surface would be a new design choice not grounded in the spec inputs.
2. Per-typemap-level "skip the entire map" can already be expressed at the call site (`if (!predicate(src)) return existingDest;` before `mapper.Map(...)`). The marginal value of a fluent surface for it is small.
3. Per-call predicates require a context bag plumbed through `IMapper.Map<>()` — explicitly deferred (see deferred feature #11 — reference handling for cycles, which is the natural home for context-bag plumbing).

### 2.5 Why `Expression<>` not `Func<>`

`Expression<>` is required for projection translatability (`ProjectTo<>()` inlines the predicate into the SQL-translated lambda). The same expression compiles to a fast delegate for in-memory use via `Expression.Compile()` (or in our case, by being inlined into the larger compiled mapping lambda). Shipping a `Func<>` overload would bifurcate the API (users must remember which form works in projections) without adding capability — anything expressible as `Func<>` is also expressible as `Expression<>`.

The one cost: users can't write predicates that capture mutable closures or call non-translatable methods. For in-memory use this works (the expression compiles fine); for projections, EF Core throws its standard untranslatable-expression error at query-execution time. Same precedent as #6 (Value Transformers).

### 2.6 Why ProjectTo translates instead of rejecting

AutoMapper §8.3 lists `Condition` as not supported in ProjectTo (silently dropped or rejected, depending on version). Atlas takes the more capable path: predicates ARE translatable when expressed as `Expression<>`, and we already use parameter-substitution (not `Expression.Invoke`) for `MapFrom` and `AddTransform` — the same machinery applies. Translation gives users the most value; untranslatable predicates fail loudly at query time.

The one subtlety: ProjectTo's "skip" semantics are `default(TMember)` (the projection materializes a fresh row, so there's no existing-destination value to preserve). For fresh-map via `Map<>()`, "skip" is also effectively `default(TMember)` (a fresh dest starts at default and we don't assign). For update-in-place via `Map<>()`, "skip" preserves the existing value. These three behaviors are the same rule ("don't write the property") expressed in three contexts where "don't write" produces different observable values; documented in the API XML on both methods.

---

## 3. Public API Surface

### 3.1 `IMemberConfigurationExpression<,,>` — two new methods

```csharp
namespace Atlas.Configuration;

public interface IMemberConfigurationExpression<TSource, TDestination, TMember>
{
    // ---- Existing v1 methods (unchanged) ----
    void MapFrom<TSourceMember>(Expression<Func<TSource, TSourceMember>> sourceMember);
    void MapFrom(TMember constantValue);
    void Ignore();

    // ---- NEW (Conditional Mapping) ----

    /// <summary>
    /// Predicate evaluated BEFORE source-side resolution. If the predicate returns false,
    /// the destination member is not mapped — for fresh <c>Map&lt;TDest&gt;(src)</c> the
    /// property remains at its default value; for update-in-place
    /// <c>Map&lt;TS,TD&gt;(src, existingDest)</c> the existing destination value is preserved.
    /// Use when source-side resolution is expensive and would be wasted work if the predicate
    /// fails.
    /// </summary>
    /// <remarks>
    /// Stored as <see cref="Expression{TDelegate}"/> so the predicate participates in both
    /// in-memory mapping and IQueryable projection. In <c>ProjectTo&lt;&gt;()</c>, the predicate
    /// becomes part of a LINQ <see cref="ConditionalExpression"/> that the underlying provider
    /// translates to SQL (typically <c>CASE WHEN</c>). Untranslatable predicates fail at
    /// query-execution time with the provider's standard error — Atlas does not pre-inspect
    /// lambdas for translatability.
    /// <para>
    /// Multiple <c>PreCondition</c> calls on the same member: last-call-wins (matches
    /// <c>MapFrom</c>). Repeating clears the prior predicate.
    /// </para>
    /// <para>
    /// On a map configured with <see cref="IMappingExpression{TSource, TDestination}.ConvertUsing"/>,
    /// per-member predicates are silently inactive (the converter replaces all per-member assigns).
    /// On a constructor-parameter binding (<c>ForCtorParam</c>), predicate-fail produces the
    /// parameter's declared default value (<c>p.HasDefaultValue ? p.DefaultValue : default(T)</c>)
    /// rather than skipping the assignment, because a constructor argument cannot be omitted.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="predicate"/> is null.</exception>
    void PreCondition(Expression<Func<TSource, bool>> predicate);

    /// <summary>
    /// Predicate evaluated AFTER source-side resolution but BEFORE assignment. The second
    /// argument is the resolved value (the result of <c>MapFrom</c> / source path / value
    /// transformers). If the predicate returns false, the destination member is not assigned
    /// — same skip semantics as <see cref="PreCondition"/>. Use when the predicate depends
    /// on the resolved value (e.g., "only assign if the resolved value is non-empty").
    /// </summary>
    /// <remarks>
    /// See <see cref="PreCondition"/> for storage, projection, multi-call, ConvertUsing,
    /// and ForCtorParam semantics — they apply identically.
    /// <para>
    /// The resolved sub-expression is hoisted into a local variable in the in-memory codegen
    /// so it is evaluated only once per call, regardless of how many times the predicate
    /// references the resolved value. In projection codegen the resolved expression is
    /// inlined twice (once for the predicate test, once for the assigned value); LINQ
    /// providers handle this fine — typical projections have inlined column references for
    /// the resolved expression, not function calls.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="predicate"/> is null.</exception>
    void Condition(Expression<Func<TSource, TMember, bool>> predicate);
}
```

### 3.2 Usage examples

**Headline example (matches reference doc §5.6):**

```csharp
public sealed class OrderProfile : MapperProfile
{
    public OrderProfile()
    {
        CreateMap<Order, OrderDto>()
            .ForMember(d => d.Total, opt =>
            {
                opt.PreCondition(s => s.Items != null && s.Items.Count > 0);
                opt.MapFrom(s => s.Items.Sum(i => i.Price * i.Quantity));
                opt.Condition((s, total) => total > 0);
            });
    }
}
```

Pipeline at runtime:
1. PreCondition `s.Items != null && s.Items.Count > 0` evaluates first.
2. If false → skip everything; `dto.Total` remains `0m` (default) on a fresh map, or unchanged on an update-in-place.
3. If true → resolution happens: `total = s.Items.Sum(i => i.Price * i.Quantity)`.
4. Condition `(s, total) => total > 0` evaluates next.
5. If false → assignment skipped; `dto.Total` remains `0m` (default) / unchanged.
6. If true → `dto.Total = total`.

**ProjectTo example (translates to `CASE WHEN`):**

```csharp
var dtos = dbContext.Orders
    .ProjectTo<OrderDto>(mapperConfiguration)
    .ToList();

// Generated SQL for the Total column (EF Core SQL Server, illustrative):
//
//   SELECT
//     CASE
//       WHEN [o].[Items_Count] > 0 AND
//            (SELECT SUM([i].[Price] * [i].[Quantity]) FROM [Items] [i] WHERE [i].[OrderId] = [o].[Id]) > 0
//       THEN (SELECT SUM([i].[Price] * [i].[Quantity]) FROM [Items] [i] WHERE [i].[OrderId] = [o].[Id])
//       ELSE 0
//     END AS [Total],
//     ...
//   FROM [Orders] [o]
```

**Update-in-place example (preserve semantics):**

```csharp
var existing = new CustomerDto { Email = "old@example.com", Name = "Old Name" };
mapper.Map(source, existing);   // source.Email == null
// existing.Email is still "old@example.com" (preserved) IF the map declares:
//   .ForMember(d => d.Email, opt => opt.PreCondition(s => s.Email != null))
```

### 3.3 Interaction matrix (documented in the API XML)

| Other feature | Interaction |
|---|---|
| `ConvertUsing` | Per-member predicates silently inactive (converter replaces all per-member assigns). Documented; no validator error. |
| `ForCtorParam` | Predicate-fail produces `p.DefaultValue` if the param has one, else `default(T)`. Distinct skip semantics from property assigns (a ctor arg can't be omitted). |
| `ForPath` (multi-level dest chain) | Predicate gates the entire nested-assign block. `ForPath` is already rejected by `ProjectionCompatibility` for `ProjectTo`, so the projection-side question is moot for `ForPath` bindings. |
| `Include` / `IncludeBase` (inheritance) | Both predicates flow base→derived via the existing `MergeBaseConfig` precedence rule. Derived-explicit overrides base-explicit. |
| `.ReverseMap()` | Predicates do NOT auto-flip. Reconfigure on the reverse expression. (Scope-A discipline.) |
| `AddTransform<T>` (#6 transformers) | Transformers wrap the resolved value first, predicates wrap the post-transform value second. Both can coexist on the same member. |
| `BeforeMap` / `AfterMap` (#5 hooks) | Independent. Hooks fire on the whole TypeMap; predicates fire per-member. Both can coexist. |
| Enum surface (#3) | Independent. Predicates apply to enum-property assigns the same as any other property. The enum codegen path (`BuildEnumLambda` for a typemap whose source AND destination are both enums) does not call into property-assign codegen, so enum-only typemaps don't engage the predicate machinery. |

---

## 4. Internal Data Shape

### 4.1 `PropertyMap` — two new fields

```csharp
// src/Atlas/Internal/PropertyMap.cs
internal sealed class PropertyMap
{
    // ... existing fields (Name, DestinationType, DestinationProperty, DestinationCtorParameter,
    //     SourcePath, CustomExpression, ConstantValue, HasConstant, Ignored, IsExplicit,
    //     DestinationPath) ...

    /// <summary>
    /// Predicate evaluated BEFORE source-side resolution. Null when no PreCondition was set
    /// on this binding. Stored as <see cref="LambdaExpression"/> so codegen can inline the body
    /// (parameter-substitution) for both in-memory mapping and IQueryable projection.
    /// Concrete signature: <c>Expression&lt;Func&lt;TSource, bool&gt;&gt;</c>.
    /// </summary>
    public LambdaExpression? PreCondition { get; set; }

    /// <summary>
    /// Predicate evaluated AFTER source-side resolution. Null when no Condition was set on
    /// this binding. Concrete signature: <c>Expression&lt;Func&lt;TSource, TMember, bool&gt;&gt;</c>
    /// — the second parameter receives the resolved-value sub-expression at codegen time.
    /// </summary>
    public LambdaExpression? Condition { get; set; }
}
```

### 4.2 `MemberConfigurationExpression<,,>` — two new methods + ApplyTo extension

```csharp
// src/Atlas/Configuration/MemberConfigurationExpression.cs
internal sealed class MemberConfigurationExpression<TSource, TDestination, TMember>
    : IMemberConfigurationExpression<TSource, TDestination, TMember>
{
    // ... existing private fields (_customExpression, _constantValue, _hasConstant, _ignored) ...
    private LambdaExpression? _preCondition;   // NEW
    private LambdaExpression? _condition;       // NEW

    // ... existing MapFrom / MapFrom-constant / Ignore methods ...

    public void PreCondition(Expression<Func<TSource, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _preCondition = predicate;     // last-call-wins
    }

    public void Condition(Expression<Func<TSource, TMember, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _condition = predicate;        // last-call-wins
    }

    public void ApplyTo(PropertyMap propertyMap)
    {
        // ... existing assignments ...
        propertyMap.PreCondition = _preCondition;   // NEW
        propertyMap.Condition = _condition;          // NEW
    }
}
```

**Last-call-wins inside the same callback** matches the existing semantics for `MapFrom` (calling `MapFrom` twice in the same `ForMember` callback uses the second one). No "chain multiple predicates" semantics — users compose predicates with `&&` inside a single lambda if they need multiple conditions.

### 4.3 `InheritanceMerger.CopyConfig` — two-line extension

```csharp
// src/Atlas/Internal/InheritanceMerger.cs
private static void CopyConfig(PropertyMap source, PropertyMap target)
{
    target.SourcePath = source.SourcePath;
    target.HasConstant = source.HasConstant;
    target.ConstantValue = source.ConstantValue;
    target.CustomExpression = source.CustomExpression;
    target.Ignored = source.Ignored;
    target.PreCondition = source.PreCondition;   // NEW
    target.Condition = source.Condition;          // NEW
    // Note: do NOT copy DestinationProperty / DestinationCtorParameter — those are
    // already correctly bound to the target's PropertyMap.
}
```

The existing `MergeBaseConfig` precedence rule handles the base-vs-derived decision: derived-explicit wins, then base-explicit (which now carries predicates), then derived-convention.

**Why no separate `MergeConditions` step.** Unlike `TransformerResolver` (#6), which composes across three scopes (global ∪ profile ∪ type-map) and so needed a dedicated build-time pass, predicates live entirely on the `PropertyMap` and only flow base→derived. That flow is already covered by the existing `MergeBaseConfig` machinery — extending `CopyConfig` is the entire build-time change.

### 4.4 What does NOT change

- `TypeMap` — no new fields. Predicates are per-member.
- `MapperConfigurationExpression` / `MapperProfile` — no new endpoints. There is no global or profile scope for predicates.
- `MapperRegistry` — unchanged.
- The build-time sequence in `MapperConfiguration` constructor — unchanged. No new resolver call.

---

## 5. Codegen — In-Memory (`ExecutionPlanBuilder`)

Three call-sites need updating: ctor-arg loop in `BuildPocoLambda`, property-assign loop in `BuildPocoLambda`, property-assign loop in `BuildUpdate`. All three share two new helpers and one shared substitution utility.

### 5.1 Pipeline order at codegen time

For a single `PropertyMap` with predicates:

```
raw resolved expression (MapFrom / SourcePath / constant)
  → wrap with value transformers (existing #6 logic, WrapWithTransformers)
  → wrap with Condition gate            ← NEW (gates assignment based on resolved value)
  → wrap with PreCondition gate         ← NEW (gates entire resolution+condition+assign)
  → emit assign (or IfThen-wrapped assign for update-in-place)
```

Order rationale: transformers operate on the raw resolved value (they normalize / clean it). Conditions decide whether the post-transform value should be assigned. PreCondition gates the whole thing (including the resolution) so it can short-circuit expensive resolution.

### 5.2 Helper for fresh-map (`BuildPocoLambda`)

```csharp
// src/Atlas/Internal/ExecutionPlanBuilder.cs
private static Expression WrapWithConditions(
    Expression resolvedExpr,         // already-transformed source expression
    PropertyMap pm,
    ParameterExpression srcParam,
    Type valueType,                   // destination property/param type
    Expression? fallbackExpr = null)  // ctor-param case: p.DefaultValue if HasDefaultValue
{
    if (pm.PreCondition is null && pm.Condition is null)
        return resolvedExpr;            // no-op — exact pre-feature codegen

    var fallback = fallbackExpr ?? Expression.Default(valueType);

    // Inner: Condition gate (post-resolution).
    Expression inner = resolvedExpr;
    if (pm.Condition is not null)
    {
        // Hoist resolvedExpr into a local so it is evaluated once even if the condition
        // body references it multiple times.
        var resolvedVar = Expression.Variable(valueType, "r");
        var condBody = SubstituteTwoParams(
            pm.Condition,
            param0Replacement: srcParam,
            param1Replacement: resolvedVar);
        inner = Expression.Block(
            variables: new[] { resolvedVar },
            Expression.Assign(resolvedVar, resolvedExpr),
            Expression.Condition(condBody, resolvedVar, fallback));
    }

    // Outer: PreCondition gate (pre-resolution). Wraps the entire Condition block, so
    // resolvedExpr is not evaluated when PreCondition fails.
    if (pm.PreCondition is not null)
    {
        var preBody = SubstituteOneParam(pm.PreCondition, param0Replacement: srcParam);
        inner = Expression.Condition(preBody, inner, fallback);
    }

    return inner;
}

private static Expression SubstituteOneParam(LambdaExpression lambda, Expression param0Replacement)
    => new ParameterReplacer(lambda.Parameters[0], param0Replacement).Visit(lambda.Body)!;

private static Expression SubstituteTwoParams(LambdaExpression lambda,
    Expression param0Replacement, Expression param1Replacement)
{
    var afterFirst = new ParameterReplacer(lambda.Parameters[0], param0Replacement).Visit(lambda.Body)!;
    return new ParameterReplacer(lambda.Parameters[1], param1Replacement).Visit(afterFirst)!;
}
```

`ParameterReplacer` already exists as a private nested class in `ExecutionPlanBuilder` (used by `WrapWithTransformers` and `BuildSourceExpression`); no new visitor needed.

### 5.3 Helper for update-in-place (`BuildUpdate`)

```csharp
private static Expression BuildUpdateAssignWithConditions(
    Expression resolvedExpr,         // already-transformed source expression
    PropertyMap pm,
    ParameterExpression srcParam,
    Expression dstAccess,             // dst.X (or BuildNestedAssign block for ForPath)
    Type valueType)
{
    // Inner: assign (gated by Condition if present).
    Expression assign;
    if (pm.Condition is not null)
    {
        var resolvedVar = Expression.Variable(valueType, "r");
        var condBody = SubstituteTwoParams(pm.Condition, srcParam, resolvedVar);
        assign = Expression.Block(
            variables: new[] { resolvedVar },
            Expression.Assign(resolvedVar, resolvedExpr),
            Expression.IfThen(condBody, Expression.Assign(dstAccess, resolvedVar)));
    }
    else
    {
        assign = Expression.Assign(dstAccess, resolvedExpr);
    }

    // Outer: PreCondition gate.
    if (pm.PreCondition is not null)
    {
        var preBody = SubstituteOneParam(pm.PreCondition, srcParam);
        assign = Expression.IfThen(preBody, assign);
    }

    return assign;
}
```

When neither predicate is set, this falls through to `Expression.Assign(dstAccess, resolvedExpr)` — the exact pre-feature codegen.

### 5.4 Wire-in: `BuildPocoLambda` ctor-arg loop

```csharp
// src/Atlas/Internal/ExecutionPlanBuilder.cs (BuildPocoLambda, ctor-arg branch)
var args = ctor.GetParameters().Select(p =>
{
    Expression sourceExpr;
    var pm = ctorParamMaps.FirstOrDefault(m =>
        string.Equals(m.Name, p.Name, StringComparison.OrdinalIgnoreCase));
    if (pm is null)
    {
        sourceExpr = p.HasDefaultValue
            ? Expression.Constant(p.DefaultValue, p.ParameterType)
            : Expression.Default(p.ParameterType);
    }
    else
    {
        sourceExpr = BuildSourceExpression(pm, srcParam, registry, p.ParameterType)
            ?? Expression.Default(p.ParameterType);
    }

    var transformed = WrapWithTransformers(sourceExpr, p.ParameterType, typeMap);

    // NEW: wrap with predicates. For ctor params, "skip" → p.DefaultValue (or default(T)).
    if (pm is not null)   // only meaningful when there's an explicit user-configured PM
    {
        var fallback = p.HasDefaultValue
            ? (Expression)Expression.Constant(p.DefaultValue, p.ParameterType)
            : Expression.Default(p.ParameterType);
        transformed = WrapWithConditions(transformed, pm, srcParam, p.ParameterType, fallback);
    }
    return transformed;
}).ToArray();
```

### 5.5 Wire-in: `BuildPocoLambda` property-assign loop

```csharp
// src/Atlas/Internal/ExecutionPlanBuilder.cs (BuildPocoLambda, property-assign loop)
foreach (var pm in propertyMaps)
{
    if (pm.Ignored) continue;
    if (pm.DestinationProperty is null) continue;

    var sourceExpr = BuildSourceExpression(pm, srcParam, registry, pm.DestinationProperty.PropertyType);
    if (sourceExpr is null) continue;

    var transformed = WrapWithTransformers(sourceExpr, pm.DestinationProperty.PropertyType, typeMap);

    // NEW: wrap with predicates (no-op if neither set).
    var assignValue = WrapWithConditions(
        transformed, pm, srcParam, pm.DestinationProperty.PropertyType);

    if (pm.DestinationPath is { } path && path.Count > 1)
    {
        statements.Add(BuildNestedAssign(destVar, path, assignValue));
    }
    else
    {
        statements.Add(Expression.Assign(
            Expression.Property(destVar, pm.DestinationProperty),
            assignValue));
    }
}
```

### 5.6 Wire-in: `BuildUpdate` property-assign loop

`BuildUpdate` uses the update-specific helper because update-in-place needs `IfThen` (preserve existing on skip) rather than `Conditional` (assign default on skip).

```csharp
// src/Atlas/Internal/ExecutionPlanBuilder.cs (BuildUpdate)
foreach (var pm in typeMap.PropertyMaps)
{
    if (pm.Ignored) continue;
    if (pm.DestinationProperty is null) continue;     // ctor params skipped on update

    var sourceExpr = BuildSourceExpression(pm, srcParam, registry, pm.DestinationProperty.PropertyType);
    if (sourceExpr is null) continue;

    var transformed = WrapWithTransformers(sourceExpr, pm.DestinationProperty.PropertyType, typeMap);

    Expression dstAccess;
    if (pm.DestinationPath is { } path && path.Count > 1)
        dstAccess = BuildNestedLeafAccess(destParam, path);   // see §5.7
    else
        dstAccess = Expression.Property(destParam, pm.DestinationProperty);

    // NEW: build the gated assign.
    statements.Add(BuildUpdateAssignWithConditions(
        transformed, pm, srcParam, dstAccess, pm.DestinationProperty.PropertyType));
}
```

### 5.7 Nested-path interaction

`BuildNestedAssign` currently emits an entire Block (intermediate-coalesces + leaf-assign) returning void. For update-in-place with predicates, we need to gate the whole block, AND we need the leaf-assign expression separately so it can be wrapped by `BuildUpdateAssignWithConditions`.

Two implementation options for the nested-path case in `BuildUpdate`:

**(Recommended)** Refactor `BuildNestedAssign` slightly so it can also produce just the leaf-access expression (intermediates emitted as a separate block of statements). Then the predicate-gating wraps just the leaf-assign:

```csharp
private static (Expression IntermediatesBlock, Expression LeafAccess) BuildNestedPathAccess(
    Expression destRoot,
    IReadOnlyList<PropertyInfo> destPath)
{
    var statements = new List<Expression>();
    Expression accessSoFar = destRoot;

    for (int i = 0; i < destPath.Count - 1; i++)
    {
        var intermediateProp = destPath[i];
        accessSoFar = Expression.Property(accessSoFar, intermediateProp);
        var ctor = intermediateProp.PropertyType.GetConstructor(Type.EmptyTypes)
            ?? throw new InvalidOperationException(/* unchanged error message */);
        var coalesce = Expression.Coalesce(accessSoFar, Expression.New(ctor));
        statements.Add(Expression.Assign(accessSoFar, coalesce));
    }

    var leafAccess = Expression.Property(accessSoFar, destPath[^1]);
    return (Expression.Block(statements), leafAccess);
}
```

Then in `BuildUpdate`'s nested-path branch:
```csharp
var (intermediates, leafAccess) = BuildNestedPathAccess(destParam, path);
var gatedAssign = BuildUpdateAssignWithConditions(
    transformed, pm, srcParam, leafAccess, pm.DestinationProperty.PropertyType);
statements.Add(Expression.Block(intermediates, gatedAssign));
```

The existing `BuildNestedAssign` (used by `BuildPocoLambda` for fresh-map) is kept as a thin wrapper that calls `BuildNestedPathAccess` and combines the two with `Expression.Assign(leafAccess, valueExpr)`.

(Alternative — gate the entire nested block including intermediate-coalesces — would mean intermediate objects DON'T get auto-instantiated when the predicate fails. Defensible but inconsistent with the documented "skip" semantics ("don't write the leaf"). Recommended option keeps the auto-instantiation orthogonal to predicates.)

### 5.8 Concrete trace — fresh-map property assign with both predicates

User's config (repeated from §3.2 for self-containment):

```csharp
.ForMember(d => d.Total, opt =>
{
    opt.PreCondition(s => s.Items != null && s.Items.Count > 0);
    opt.MapFrom(s => s.Items.Sum(i => i.Price * i.Quantity));
    opt.Condition((s, total) => total > 0);
});
```

Generated Expression tree for the `Total` assign (whitespace-prettified pseudocode of the actual `Expression.*` calls):

```
Assign(
    Property(destVar, "Total"),
    Conditional(                                                  // ← outer PreCondition gate
        AndAlso(
            NotEqual(srcParam.Items, null),                       // s.Items != null
            GreaterThan(Property(srcParam.Items, "Count"), 0)),   // s.Items.Count > 0
        Block(                                                    // ← Condition gate (PreCondition was true)
            variables = [r],
            Assign(r, Call(srcParam.Items, "Sum", ...)),          // r = s.Items.Sum(...)
            Conditional(
                GreaterThan(r, 0),                                // (s, total) => total > 0, total → r
                r,                                                // assigned value if Condition true
                Default(decimal))),                               // assigned value if Condition false (zero on fresh map)
        Default(decimal)))                                        // assigned value if PreCondition false (zero on fresh map)
```

After `Expression.Compile`, the runtime cost when both predicates pass is: predicate test + resolution + condition test + assign. The "wasted resolution" anti-pattern (running the Sum even when Items is null/empty) is correctly avoided because the resolution is inside the PreCondition's true-branch.

### 5.9 Concrete trace — update-in-place with PreCondition only

User's config:
```csharp
.ForMember(d => d.Email, opt => opt.PreCondition(s => s.Email != null));
```

Generated Expression for the `Email` assign in `BuildUpdate`:

```
IfThen(                                                           // ← PreCondition gate
    NotEqual(srcParam.Email, null),
    Assign(Property(destParam, "Email"), srcParam.Email))         // assigned only if PreCondition true;
                                                                  // existing destParam.Email preserved otherwise
```

Note the `IfThen` (not `IfThenElse` or `Conditional` with default) — the existing destination value is preserved by emitting nothing in the false case.

---

## 6. Codegen — Projection (`ProjectionPlanBuilder`)

LINQ providers cannot translate `Expression.Block` or `Expression.Variable` (these are imperative constructs; projection bindings must be a single pure expression per `MemberAssignment`). So the projection-side wrap is structurally simpler but accepts double-evaluation of the resolved sub-expression in SQL.

### 6.1 Helper

```csharp
// src/Atlas.Projections/Internal/ProjectionPlanBuilder.cs
private static Expression WrapProjectionWithConditions(
    Expression resolvedExpr,         // already-transformed expression
    PropertyMap pm,
    Expression srcExpr,              // the source root for this binding
    Type valueType,
    Expression? fallbackExpr = null) // ctor-param case
{
    if (pm.PreCondition is null && pm.Condition is null)
        return resolvedExpr;

    var fallback = fallbackExpr ?? Expression.Default(valueType);
    Expression? testExpr = null;

    if (pm.PreCondition is not null)
    {
        var preBody = ParameterReplacer.Replace(
            pm.PreCondition.Body, pm.PreCondition.Parameters[0], srcExpr);
        testExpr = preBody;
    }

    if (pm.Condition is not null)
    {
        // Substitute BOTH parameters: param 0 = srcExpr, param 1 = resolvedExpr (inlined twice).
        var condBody = ParameterReplacer.Replace(
            pm.Condition.Body, pm.Condition.Parameters[0], srcExpr);
        condBody = ParameterReplacer.Replace(
            condBody, pm.Condition.Parameters[1], resolvedExpr);
        testExpr = testExpr is null ? condBody : Expression.AndAlso(testExpr, condBody);
    }

    return Expression.Condition(testExpr!, resolvedExpr, fallback);
}
```

`ParameterReplacer.Replace` is the existing static method already used by `WrapProjectionWithTransformers` and the `MapFrom`-rebinding code.

### 6.2 Wire-in: ctor-arg loop in `BuildBody`

```csharp
// src/Atlas.Projections/Internal/ProjectionPlanBuilder.cs (BuildBody, ctor-arg branch)
var args = ctor.GetParameters().Select(p =>
{
    Expression sourceExpr;
    var pm = ctorParamMaps.FirstOrDefault(m =>
        string.Equals(m.Name, p.Name, StringComparison.OrdinalIgnoreCase));
    if (pm is null)
    {
        sourceExpr = p.HasDefaultValue
            ? Expression.Constant(p.DefaultValue, p.ParameterType)
            : Expression.Default(p.ParameterType);
    }
    else
    {
        sourceExpr = BuildBinding(srcExpr, pm, depth, p.ParameterType, registry, maxDepth)
            ?? Expression.Default(p.ParameterType);
    }

    var transformed = WrapProjectionWithTransformers(sourceExpr, p.ParameterType, tm);

    // NEW: wrap with predicates.
    if (pm is not null)
    {
        var fallback = p.HasDefaultValue
            ? (Expression)Expression.Constant(p.DefaultValue, p.ParameterType)
            : Expression.Default(p.ParameterType);
        transformed = WrapProjectionWithConditions(transformed, pm, srcExpr, p.ParameterType, fallback);
    }
    return transformed;
}).ToArray();
```

### 6.3 Wire-in: property-binding loop in `BuildBody`

```csharp
// src/Atlas.Projections/Internal/ProjectionPlanBuilder.cs (BuildBody, property-binding loop)
foreach (var pm in propertyMaps)
{
    if (pm.Ignored) continue;
    if (pm.DestinationProperty is null) continue;
    if (!ProjectionCompatibility.IsBindingProjectable(pm, out _)) continue;

    var binding = BuildBinding(srcExpr, pm, depth, pm.DestinationProperty.PropertyType, registry, maxDepth);
    if (binding is null) continue;

    binding = WrapProjectionWithTransformers(binding, pm.DestinationProperty.PropertyType, tm);

    // NEW: wrap with predicates.
    binding = WrapProjectionWithConditions(
        binding, pm, srcExpr, pm.DestinationProperty.PropertyType);

    bindings.Add(Expression.Bind(pm.DestinationProperty, binding));
}
```

### 6.4 Concrete trace — projection of the headline example

User's config (same as §3.2):
```csharp
.ForMember(d => d.Total, opt =>
{
    opt.PreCondition(s => s.Items != null && s.Items.Count > 0);
    opt.MapFrom(s => s.Items.Sum(i => i.Price * i.Quantity));
    opt.Condition((s, total) => total > 0);
});
```

`Expression.Bind(Total, ...)` body:

```
Conditional(
    AndAlso(
        AndAlso(                                                   // ← PreCondition body, srcExpr substituted
            NotEqual(srcExpr.Items, null),
            GreaterThan(Property(srcExpr.Items, "Count"), 0)),
        GreaterThan(                                                // ← Condition body, srcExpr + resolved substituted
            srcExpr.Items.Sum(i => i.Price * i.Quantity), 0)),
    srcExpr.Items.Sum(i => i.Price * i.Quantity),                  // resolved value (inlined twice — fine in SQL)
    Default(decimal))
```

Which EF Core typically translates to (illustrative SQL Server):
```sql
CASE
    WHEN [o].[Items_Count] > 0
     AND (SELECT SUM([i].[Price] * [i].[Quantity]) FROM [Items] [i] WHERE [i].[OrderId] = [o].[Id]) > 0
    THEN (SELECT SUM([i].[Price] * [i].[Quantity]) FROM [Items] [i] WHERE [i].[OrderId] = [o].[Id])
    ELSE 0
END AS [Total]
```

The double-evaluation in SQL is benign — the SQL planner often shares the subquery via CTE/cross-apply, and even without that, the cost is well below network round-trip.

### 6.5 Why no projection rejection rule

Unlike Hooks (#5, where `RejectHooksOrThrow` throws `AtlasProjectionException` because hook `Action<TS,TD>` can't be translated) and `ForPath` (rejected by `ProjectionCompatibility.IsBindingProjectable` because LINQ providers can't write into nested chains), conditions translate. They become a `Conditional` node which every mature LINQ provider supports.

`ProjectionCompatibility.IsTypeMapProjectable` and `IsBindingProjectable` are unchanged for this feature.

The README (per §10) calls this out: *Conditional mapping works in `ProjectTo`. Predicates translate to SQL `CASE WHEN`. Untranslatable predicates fail at query-execution time with the LINQ provider's standard error.*

### 6.6 Untranslatable predicates

If the user writes:
```csharp
opt.Condition((s, t) => MyHelpers.ComplexCheck(s, t));
```
and `MyHelpers.ComplexCheck` isn't a method EF Core knows how to translate, the `ToList()` call on the projected query throws EF Core's standard:
```
System.InvalidOperationException: The LINQ expression '...ComplexCheck(o, total)' could not be translated. ...
```

Atlas does not pre-inspect predicates — same precedent as #6 (Value Transformers). Pre-inspection would essentially reimplement the EF Core expression visitor (and would still be incomplete: different LINQ providers have different translation surfaces).

---

## 7. Build-Time Pipeline

**Unchanged.** No new step is needed.

Current order (post-#6) inside `MapperConfiguration` constructor:
```
1. Profile.Configure()                                    — TypeMaps registered
2. ConfigExpression conflict-guard (#4)
3. AddProfile harvest (#4)
4. InheritanceMerger.Resolve(typeMaps)                    — propagates ForMember + hooks (#5)
                                                            + NOW also predicates (this feature, via CopyConfig)
5. ConventionEngine.ResolveMissingMembers(tm)
6. ReverseMapMirror.Mirror(typeMaps)                      — does NOT propagate predicates (scope-A)
7. TransformerResolver.Resolve(typeMaps, expression.ValueTransformers)
8. tm.Seal() for each TypeMap
9. (On AssertConfigurationIsValid) ConfigurationValidator.Validate
10. CompileMappings — codegen reads PropertyMap.PreCondition / .Condition and wraps source-side expressions
```

Predicates "propagate" via inheritance because step 4's `MergeBaseConfig` calls the extended `CopyConfig` (§4.3). Predicates do NOT propagate via reverse-map (step 6) — `ReverseMapMirror` does not call `CopyConfig` on individual `PropertyMap`s; it constructs reverse-direction `PropertyMap`s from scratch (using the source-side member info), and per scope-A discipline, per-member options like `MapFrom`/`Ignore`/predicates do not auto-flip.

---

## 8. Validation

### 8.1 No new validator rules

Predicates are self-typed at the call site (`Expression<Func<TSource, bool>>` and `Expression<Func<TSource, TMember, bool>>`). The C# type system rejects ill-typed predicates at compile time. Null predicates throw `ArgumentNullException` immediately in `PreCondition` / `Condition` (same precedent as `BeforeMap` / `AfterMap` in #5 and `AddTransform` in #6).

### 8.2 What we explicitly DO NOT validate

- **Predicate translatability for `ProjectTo`.** Untranslatable predicates fail at query time with the LINQ provider's standard error. Same precedent as #6.
- **Predicate "always true" or "always false".** A predicate that's literally `s => true` (or `s => false`) is degenerate but legal — same way `MapFrom(s => null)` is legal.
- **Predicate side effects.** A predicate that mutates source state is a user bug; we don't try to detect it. Per the API XML, predicates are documented as pure.
- **Predicate-on-map-with-`ConvertUsing`.** This is a documented silent-no-op (the converter replaces all per-member assigns). Adding a Minor-severity warning was considered but rejected — `ConvertUsing` already silently bypasses `MapFrom` and `Ignore` too; a one-off warning for predicates would be inconsistent. If a broader "your `ConvertUsing` is silently ignoring per-member config" warning is added later, predicates should be included in it.

---

## 9. Test Plan

Total: **~32 tests**. Test baseline goes from **396 → ~428** after this feature.

### 9.1 `MemberConfigurationExpressionTests` (Atlas.Tests)

Add 6 tests:

1. `PreCondition_StoredOnPropertyMap` — `ApplyTo` writes the predicate to `pm.PreCondition`.
2. `Condition_StoredOnPropertyMap` — `ApplyTo` writes the predicate to `pm.Condition`.
3. `PreCondition_NullPredicate_Throws` — `ArgumentNullException`.
4. `Condition_NullPredicate_Throws` — `ArgumentNullException`.
5. `PreCondition_LastCallWins` — calling `PreCondition` twice in the same callback uses the second.
6. `Condition_LastCallWins` — same for Condition.

### 9.2 `ExecutionPlanBuilderConditionTests` (Atlas.Tests)

Add 8 tests covering fresh-map codegen via `MapperConfiguration` + `IMapper.Map<>()`:

1. `PreConditionTrue_PerformsResolution` — predicate true → resolved value assigned.
2. `PreConditionFalse_SkipsResolution` — using a counter lambda in `MapFrom`, assert the counter is NOT incremented when PreCondition fails. Asserts the short-circuit is real (Bug-3-style guard against "I just gated the assign but still ran the resolver").
3. `PreConditionFalse_FreshMap_PropertyIsDefault` — destination property is `default(TMember)`.
4. `ConditionTrueOnResolvedValue_AssignsValue` — `(s, m) => m > 0` evaluates the resolved value correctly.
5. `ConditionFalseOnResolvedValue_DefaultAssigned_FreshMap` — predicate sees the resolved value, returns false, destination is default.
6. `BothPredicates_BothTrue_AssignsValue` — pipeline order respected.
7. `CtorParam_PreConditionFalse_UsesParamDefault` — ctor param with `int = 42` default; predicate fails; constructed object has 42 not 0.
8. `ForPath_WithCondition_GatesNestedAssign` — multi-level dest path with Condition; intermediate auto-instantiation happens, leaf-assign is gated.

### 9.3 `ExecutionPlanBuilderUpdateConditionTests` (Atlas.Tests)

Add 4 tests covering update-in-place via `IMapper.Map<TS,TD>(src, existingDest)`:

1. `Update_PreConditionFalse_PreservesExistingDestValue` — existing destination value not overwritten.
2. `Update_ConditionFalse_PreservesExistingDestValue` — same for Condition.
3. `Update_BothPredicatesPass_OverwritesValue` — assignment happens.
4. `Update_PreConditionFalse_ResolutionNotEvaluated` — counter-lambda check (matches §9.2 #2 but on the update path).

### 9.4 `InheritanceMergerConditionTests` (Atlas.Tests)

Add 3 tests:

1. `BasePreCondition_PropagatesToDerived` — base map sets PreCondition; derived map (via `Include`) inherits it on the same property.
2. `DerivedExplicit_OverridesBaseExplicit` — derived's own PreCondition wins.
3. `BothPredicates_PropagateTogether` — base map sets both; both flow.

### 9.5 `MapperEndToEndConditionTests` (Atlas.Tests)

Add 5 end-to-end tests via real `IMapper`:

1. `HeadlineExample_FromReferenceDoc` — the full example from §3.2; assert behavior under empty `Items`, items-with-zero-total, items-with-positive-total.
2. `PreCondition_SkipsExpensiveResolution_OnRealMap` — wraps a counter check at the API boundary.
3. `Condition_ReadsResolvedValue_AfterTransformer` — declares both `AddTransform<string>(s => s.Trim())` and `Condition((s, t) => t.Length > 0)`; assert empty-after-trim values are skipped.
4. `Collection_ElementMapHasConditions` — mapping `List<Order>` to `List<OrderDto>`; predicates fire per-element.
5. `NestedMap_HasConditions_OnInnerMember` — `OrderDto` nested in `OrderEnvelopeDto`; inner map's predicate is honored.

### 9.6 `ProjectionPlanBuilderConditionTests` (Atlas.Projections.Tests)

Add 4 tests over the inspected expression tree:

1. `ProjectionEmitsConditionalForPredicate` — assert `binding` is an `Expression.Condition` for a member with a predicate.
2. `BothPredicates_AndAlsoComposed` — assert the test expression is `AndAlso(pre, cond)`.
3. `NoPredicates_NoConditional` — assert the binding is the unwrapped resolved expression (Atlas does not emit unnecessary Conditionals).
4. `PredicateFallback_IsDefault` — assert the false-branch is `Expression.Default(valueType)`.

### 9.7 `ProjectTo_E2E_ConditionTests` (Atlas.Projections.EFCore.Tests)

Add 2 end-to-end tests against in-memory EF Core SQLite:

1. `ProjectTo_PredicateGeneratesCaseWhen` — translate query, capture generated SQL, assert it contains `CASE WHEN`.
2. `ProjectTo_PredicateFalse_RowReturnsDefaultForGatedColumn` — seed test data; assert rows where the predicate would evaluate false return the column's default value.

### 9.8 What we do NOT add tests for

- **`AssertConfigurationIsValid` does NOT throw on predicates** — there are no validator rules to test.
- **Untranslatable predicate detection** — by design, we don't pre-inspect; the EF Core test in §9.7 does not need a "throws on untranslatable" companion because Atlas itself doesn't throw (the LINQ provider does at query time, which is the documented behavior).

### 9.9 Coverage targets

Same as prior features: line ≥ 90%, branch ≥ 80% on the changed assemblies.

The Atlas core change-set is small (one helper-pair + two field copies + three call-site wires). Coverage should land comfortably in the high 90s on Atlas core. Projections likewise (one helper + two call-site wires).

---

## 10. README Updates

Three changes to `README.md`:

1. **New "Conditional mapping" subsection** under the existing per-member configuration area (between "Value transformers" and "Inheritance" or wherever fits the existing flow):

   ```markdown
   ### Conditional mapping (`PreCondition` / `Condition`)

   Two per-member predicates that gate property mapping at runtime.
   `PreCondition(s => predicate)` runs **before** source-side resolution — use it
   when the resolution is expensive and would be wasted work if the predicate
   fails. `Condition((s, value) => predicate)` runs **after** resolution — use
   it when the predicate depends on the resolved value.

   Pipeline order: **PreCondition → resolve → Condition → assign**.

       CreateMap<Order, OrderDto>()
           .ForMember(d => d.Total, opt =>
           {
               opt.PreCondition(s => s.Items != null && s.Items.Count > 0);
               opt.MapFrom(s => s.Items.Sum(i => i.Price * i.Quantity));
               opt.Condition((s, total) => total > 0);
           });

   Skip semantics:
   - **Fresh `Map<TDest>(src)`**: skipped property is `default(TMember)`.
   - **Update-in-place `Map<TS,TD>(src, existingDest)`**: skipped property
     preserves the existing destination value.
   - **`ProjectTo<TDest>(query)`**: skipped property is `default(TMember)`
     (a projection materializes a fresh row).

   Both predicates are `Expression<Func<...>>` and translate to SQL `CASE WHEN`
   in `ProjectTo`. Untranslatable predicates fail at query-execution time with
   the LINQ provider's standard error.
   ```

2. **Coverage-line update** at the top of the README (`396 tests passing` → `~428 tests passing`).

3. **`ProjectTo` capability table** (or equivalent prose): explicitly note that conditional mapping IS translatable (in contrast with hooks and `ForPath`, which are rejected).

---

## 11. Risks & Implementer Notes

These are repeated in the implementation plan for in-task visibility, but listed here for the design-doc reader.

### 11.1 Don't try to "optimize" projection codegen with Block / Variable

The double-evaluation of `resolvedExpr` in projection codegen (§6.4) is intentional. LINQ providers reject `Expression.Block` and `Expression.Variable` in projection bindings because they're imperative constructs. Any attempt to "share the subexpression" via a local variable will break SQL translation. Per-task review note for the implementer subagents.

### 11.2 Wrap order matters

Both helpers (`WrapWithConditions` / `WrapProjectionWithConditions`) must wrap **after** `WrapWithTransformers` / `WrapProjectionWithTransformers`, not before. Conditions see the post-transform value (matching the user's mental model: "transformers normalize, conditions decide"). Reversing this order would mean predicates see the pre-transform raw source value, which is not what the API XML promises.

### 11.3 Cross-package consumer audit (Bug-4 lesson applied)

The two new fields on `PropertyMap` (`PreCondition`, `Condition`) are additions to a **shared data shape** consumed by both `Atlas` and `Atlas.Projections`. Per the lesson from feature #4 (ReverseMap), both consumers must be updated in the same plan task — the spec already requires this in §5 and §6. The implementation plan (next deliverable) should put `Atlas` and `Atlas.Projections` codegen wires in **adjacent tasks** so the spec reviewer can verify cross-package coverage in one pass.

### 11.4 NOT scope-identifying TypeMap metadata (Bug-5 lesson applied)

The new fields live on `PropertyMap`, NOT `TypeMap`. They are NOT scope-identifying metadata that needs propagation across related-typemap creators (`ReverseMap`, future inheritance-derived maps). Inheritance propagation is already handled by `MergeBaseConfig`/`CopyConfig`. Reverse-map propagation is intentionally NOT done (per scope-A discipline — same as `MapFrom`, `Ignore`, hooks, transformers).

### 11.5 Don't over-extend `BuildNestedAssign` refactor

§5.7 proposes splitting `BuildNestedAssign` into `BuildNestedPathAccess` (intermediates + leaf-access) plus a wrapper. Keep this refactor surgical — only what's needed to expose the leaf-access for the predicate gate in `BuildUpdate`. Don't take the opportunity to redesign nested-path codegen in general; that's a larger refactor better done as its own task if warranted.

### 11.6 Watch for tests that quietly diverge from the plan (Hooks Task 10 lesson)

The implementer-subagent should report DONE_WITH_CONCERNS if any test assertion in the plan turns out to be wrong (e.g., the plan asserts a specific exception type that isn't actually thrown, or asserts a specific Expression node shape that the implementation produces differently). Silent test changes are a smell — surface them so the controller can decide whether the test or the plan is wrong.

### 11.7 Holistic review is non-negotiable

Per the established workflow rhythm (`feedback_atlas_v2_workflow.md`), the final holistic review (`superpowers:code-reviewer`) catches cross-task or whole-feature concerns even when per-task reviews are spotless. The Value Transformers branch (#6) was the empirical proof — holistic caught a Critical reverse-map-profile-propagation bug despite ALL 10 per-task reviews passing cleanly. Don't skip the holistic review for this feature.

---

## 12. Worked End-to-End Example

This section traces a full `Map<>()` and a full `ProjectTo<>()` through the codegen for a realistic two-feature interaction (predicates + transformers).

### 12.1 Setup

```csharp
public class Order { public string? Description { get; set; } public decimal RawAmount { get; set; } }
public class OrderDto { public string Description { get; set; } = ""; public decimal Amount { get; set; } }

public class OrderProfile : MapperProfile
{
    public OrderProfile()
    {
        ValueTransformers.Add<string>(s => s.Trim());           // global-ish: trims any string property

        CreateMap<Order, OrderDto>()
            .ForMember(d => d.Description, opt =>
            {
                opt.MapFrom(s => s.Description ?? "");
                opt.Condition((s, desc) => desc.Length > 0);     // skip empty/whitespace descriptions
            })
            .ForMember(d => d.Amount, opt =>
            {
                opt.PreCondition(s => s.RawAmount > 0);          // skip zero/negative amounts before resolution
                opt.MapFrom(s => s.RawAmount * 1.2m);            // 20% surcharge
            });
    }
}
```

### 12.2 In-memory codegen for the `Description` assign

After `BuildSourceExpression`:
```
sourceExpr = Coalesce(srcParam.Description, Constant(""))
```

After `WrapWithTransformers` (the global `s => s.Trim()`):
```
transformed = Call(Coalesce(srcParam.Description, Constant("")), "Trim", null)
```

After `WrapWithConditions` (Condition only — no PreCondition on this member):
```
Block(
    variables = [r : string],
    Assign(r, Call(Coalesce(srcParam.Description, Constant("")), "Trim", null)),
    Conditional(
        GreaterThan(Property(r, "Length"), 0),
        r,
        Default(string)))                               // = null
```

Final emitted statement:
```
Assign(Property(destVar, "Description"), <above>)
```

### 12.3 In-memory codegen for the `Amount` assign

After `BuildSourceExpression`:
```
sourceExpr = Multiply(srcParam.RawAmount, Constant(1.2m))
```

After `WrapWithTransformers` (no decimal transformer registered, so unchanged):
```
transformed = sourceExpr
```

After `WrapWithConditions` (PreCondition only — no Condition on this member):
```
Conditional(
    GreaterThan(srcParam.RawAmount, Constant(0m)),     // PreCondition body
    Multiply(srcParam.RawAmount, Constant(1.2m)),       // resolution happens only if PreCondition true
    Default(decimal))                                   // = 0m
```

Final emitted statement:
```
Assign(Property(destVar, "Amount"), <above>)
```

### 12.4 Projection codegen for the same TypeMap

Bindings emitted to `Expression.MemberInit`:

```
Bind(Description,
    Conditional(
        GreaterThan(
            Property(
                Call(Coalesce(srcExpr.Description, ""), "Trim", null),
                "Length"),
            0),
        Call(Coalesce(srcExpr.Description, ""), "Trim", null),     // double-evaluated in SQL — fine
        Default(string)))

Bind(Amount,
    Conditional(
        GreaterThan(srcExpr.RawAmount, Constant(0m)),
        Multiply(srcExpr.RawAmount, Constant(1.2m)),
        Default(decimal)))
```

EF Core SQL (illustrative SQLite):

```sql
SELECT
    CASE WHEN length(rtrim(ltrim(coalesce("o"."Description", '')))) > 0
         THEN rtrim(ltrim(coalesce("o"."Description", '')))
         ELSE NULL
    END AS "Description",
    CASE WHEN "o"."RawAmount" > 0
         THEN "o"."RawAmount" * 1.2
         ELSE 0
    END AS "Amount"
FROM "Orders" "o";
```

### 12.5 Behavior verification

| Order row | `Description` | `Amount` | Map<> result | ProjectTo result |
|---|---|---|---|---|
| `{"hello", 10m}` | `"hello"` | 12m | same | same |
| `{"  ", 10m}` | `null` (Condition fails post-trim) | 12m | same | same |
| `{null, 10m}` | `null` (Condition fails on `""`) | 12m | same | same |
| `{"x", 0m}` | `"x"` | 0m (PreCondition false) | same | same |
| `{"x", -5m}` | `"x"` | 0m | same | same |

Both pipelines (in-memory `Map<>` and `ProjectTo`) produce the same observable values for every row. This is the integration-test we want from §9.5 and §9.7 combined.

---

*End of design.*
