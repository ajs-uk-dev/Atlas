# Atlas v2 — Null Substitution (`NullSubstitute`)

> **Status:** Design approved 2026-05-05. Implementation plan: `Atlas-Plan-NullSubstitution.md` (to be written next).
> **Spec inputs:** `Object-Mapping-Functional-Reference.md` §5.7 (Null substitution), `AutoMapper-Analysis.md` §5.9 (`NullSubstitute`).
> **Position in v2 roadmap:** Feature #8 of 13 deferred groups. Builds on v1 + ProjectTo (#1) + Inheritance (#2) + Enum (#3) + Reverse Mapping (#4) + Hooks (#5) + Value Transformers (#6) + Conditional Mapping (#7).

---

## 1. Goals & Non-Goals

### 1.1 Goal

Add per-member `NullSubstitute` to Atlas: a source-typed fallback value used when the resolved source member is null. The substitute participates in the existing conversion pipeline exactly like a real source value would (numeric / enum auto-conversion, registered TypeMaps), and translates to SQL `COALESCE` in `ProjectTo` via `Expression.Coalesce`.

The headline example from `Object-Mapping-Functional-Reference.md` §5.7 — *"Provide a fallback value when the source value (or anywhere along the source path) is null. The substitute is treated as a source-typed value and runs through the same conversion pipeline as a real source value would"* — works under this design and is testable end-to-end via both in-memory `Map<>()` calls and EF Core `ProjectTo<>()` queries.

### 1.2 In scope (v2 MVP)

1. New methods on `IMemberConfigurationExpression<TSource, TDestination, TMember>`:
   - `void NullSubstitute<TSourceMember>(TSourceMember constant)` — typical case; compiler infers `TSourceMember` from the literal.
   - `void NullSubstitute<TSourceMember>(Expression<Func<TSourceMember>> factory)` — no-arg lambda for computed defaults.
2. One new nullable field on `PropertyMap`: `NullSubstitute: LambdaExpression?`. Stored as `Expression<Func<TSourceMember>>` (constant overload wraps as `() => constant`).
3. `MemberConfigurationExpression` plumbs both overloads into `PropertyMap` via `ApplyTo`. Last-call-wins for repeated calls inside the same callback (matches `MapFrom`).
4. `InheritanceMerger.CopyConfig` extension: copy the new field from base PropertyMap to derived PropertyMap (the existing `IsExplicit` precedence machinery already handles derived-wins).
5. `ExecutionPlanBuilder.BuildSourceExpression` extension: insert `Expression.Coalesce(resolvedExpr, substituteBody)` between the resolve step (CustomExpression / SourcePath) and `ConvertOrMap`. New helper `ApplyNullSubstitute`. No-op when the field is null.
6. `Atlas.Projections.ProjectionPlanBuilder.BuildBinding` extension: same `Coalesce` insert in the projection path. New helper `ApplyProjectionNullSubstitute`. Translates to SQL `COALESCE`.
7. Two new `ConfigurationValidator` rules (always-on, no opt-in):
   - **Unreachable substitute** — error when `NullSubstitute` is set on a member whose resolved source-member type is a non-nullable value type (the substitute can never fire).
   - **Type-mismatch substitute** — error when the substitute's type is not assignable to the resolved source-member type (and not implicitly convertible via numeric coercion).

### 1.3 Out of scope (deferred to a future v3 design doc)

- **Per-typemap or per-call substitutes.** Same scope decision as #7: per-member only. AutoMapper-style `cfg.CreateMap<S,D>().NullSubstitute(...)` (typemap-level) and per-call overrides are deferred.
- **Substitutes that read other source state.** `Expression<Func<TSource, TSourceMember>>` (substitute uses other source data) is already expressible via `MapFrom(s => s.X ?? FallbackFor(s))`. The `NullSubstitute` API focuses on the simple "non-null fallback that doesn't depend on the source" case.
- **Auto-propagation across `.ReverseMap()`.** Per the established scope-A discipline (#4 ReverseMap, #5 Hooks, #6 ValueTransformers, #7 ConditionalMapping all do not auto-flip per-member options), the user reconfigures on the reverse expression. Inheritance propagation (base → derived) IS in scope.
- **Validator pre-inspection of factory-Expression translatability for `ProjectTo`.** Untranslatable factories fail at query-execution time with the LINQ provider's standard error. Same model as #6, #7.
- **Delegate (`Func<TSourceMember>`) overloads.** `Expression<>` form is canonical for projection compatibility; a delegate-only escape hatch would split the API.

### 1.4 Non-goals (out of scope permanently for this feature)

- Discovering substitutes by attribute or convention without an explicit `NullSubstitute` call. Substitutes are opt-in.
- Substitutes for the destination-side null-coalesce ("if the destination property is null after assignment, use a default"). That's a different feature (post-assignment guard); not part of this design.
- Substitutes triggered by the destination's existing value during update-in-place. Update-in-place uses the resolved-source semantic uniformly: substitute fires when source is null, regardless of destination state.

---

## 2. Architecture Overview

### 2.1 What changes

- **`IMemberConfigurationExpression<,,>`** gains two methods (`NullSubstitute` constant + Expression overloads).
- **`MemberConfigurationExpression<,,>`** captures the substitute, applies it to the `PropertyMap` in `ApplyTo`.
- **`PropertyMap`** gains one `LambdaExpression?` field.
- **`InheritanceMerger.CopyConfig`** extended one line to copy the new field.
- **`ExecutionPlanBuilder`** gains one helper (`ApplyNullSubstitute`). One call-site updated: `BuildSourceExpression` inserts the helper call between resolve and `ConvertOrMap`.
- **`Atlas.Projections.ProjectionPlanBuilder`** gains one helper (`ApplyProjectionNullSubstitute`). One call-site updated: `BuildBinding` inserts the helper call between resolve and `ConvertOrInline`.
- **`ConfigurationValidator`** gains two rules (`ValidateNullSubstitutes`).

### 2.2 What does NOT change

- **`TypeMap`** — no new fields. Substitute is per-member, lives on `PropertyMap`.
- **`MapperRegistry`** — unchanged.
- **`ConventionEngine`** — unchanged. Conventions resolve source paths; substitutes are an opt-in user-explicit configuration that tracks alongside the resolved path.
- **`ReverseMapMirror`** — unchanged. Substitutes do not auto-flip (per scope-A discipline).
- **`TransformerResolver`** (#6) — unchanged. Transformers and substitutes are independent concerns; transformers wrap the post-substitute value.
- **`ProjectionCompatibility`** — unchanged. Substitutes do NOT add a projection rejection (they translate via `COALESCE`).
- **Build-time sequence** — unchanged. The current order (`InheritanceMerger.Resolve → ConventionEngine.ResolveMissingMembers → ReverseMapMirror.Mirror → TransformerResolver.Resolve → tm.Seal()`) does not need a new step. Substitute propagation happens inside `InheritanceMerger.MergeBaseConfig` via the extended `CopyConfig` — no separate pass.
- **`BuildPocoLambda`** ctor-arg / property-assign loops, **`BuildUpdate`** property loop — unchanged. They all call `BuildSourceExpression`, which now applies the substitute internally. The `WrapWithConditions` / `BuildUpdateAssignWithConditions` / `WrapWithTransformers` helpers from #6 and #7 don't need any modification — they wrap whatever `BuildSourceExpression` returns, and that may now be a `Coalesce` node, but the wrap pipeline doesn't care.

### 2.3 Pipeline order at codegen time

The fully-extended per-member codegen pipeline (post-#8):

```
PreCondition gate (#7)
  → resolve (MapFrom / SourcePath / constant)
  → null-substitute (#8)                              ← NEW
  → ConvertOrMap (existing — type coercion)
  → transform (#6)
  → Condition gate (#7)
  → assign
```

Rationale:
- **Substitute must come AFTER resolve** — it acts on the resolved source value.
- **Substitute must come BEFORE transform** — otherwise a transformer like `s => s.Trim()` would NRE on a null source. The substitute keeps the rest of the pipeline null-free.
- **Substitute must come BEFORE Condition** — Condition's second argument (`v`) is the resolved-and-transformed value; if NullSubstitute fired after, Condition would see null even when a substitute is configured.
- **Substitute must come BEFORE ConvertOrMap** — the substitute is source-typed and must be coerced/lifted alongside the real source value, exactly per the reference-doc spec.

### 2.4 Runtime path

Unchanged at the dispatch level. `IMapper.Map<TDest>(source)` is still a dictionary lookup → cached delegate invoke. The compiled delegate body for a `TypeMap` whose `PropertyMap`s have substitutes differs only in that the resolved source expressions are wrapped with `Coalesce` nodes. When the field is null, the helper returns the resolved expression unchanged — zero perf cost on maps that don't use substitutes.

### 2.5 Why per-member only

Three reasons (matches #7's rationale):

1. The reference doc (`Object-Mapping-Functional-Reference.md` §5.7) and the established AutoMapper surface (§5.9) both describe `NullSubstitute` as a per-member option inside the `ForMember` callback. A per-typemap or per-call surface would be a new design choice not grounded in the spec inputs.
2. Per-typemap-level "if any source member is null, use this fallback" doesn't make semantic sense — fallbacks are inherently per-member-typed.
3. Per-call substitutes require a context bag plumbed through `IMapper.Map<>()` — explicitly deferred (see deferred feature #11 — reference handling for cycles, which is the natural home for context-bag plumbing).

### 2.6 Why `Expression<>` not just `Func<>`

`Expression<>` is required for projection translatability (`ProjectTo<>()` inlines the factory's body into the SQL-translated lambda). The same expression compiles to a fast delegate for in-memory use. Shipping a `Func<>` overload would bifurcate the API (users must remember which form works in projections) without adding capability — anything expressible as `Func<>` is also expressible as `Expression<>`.

### 2.7 Why `Coalesce` for both reference types and `Nullable<T>`

`Expression.Coalesce(left, right)` produces a node whose runtime type is `left.Type` (unwrapped if `left` is `Nullable<T>`). C# compiler handles this natively, so do all major LINQ providers:

- For **reference types**: behavior is `left ?? right`.
- For **`Nullable<T>`**: behavior is `left.HasValue ? left.Value : right`. The result type is `T` (unwrapped).

Both cases produce a non-null result. The downstream `ConvertOrMap` step then handles any further coercion (e.g., `int → long?` numeric widening), exactly the same path a non-null source value would take.

Non-nullable value types (`int`, `DateTime`, enums) cannot be null and don't need a substitute. The validator catches this configuration error at config-build time.

---

## 3. Public API Surface

### 3.1 `IMemberConfigurationExpression<,,>` — two new methods

```csharp
namespace Atlas.Configuration;

public interface IMemberConfigurationExpression<TSource, TDestination, TMember>
{
    // ---- Existing methods (unchanged) ----
    void MapFrom<TSourceMember>(Expression<Func<TSource, TSourceMember>> sourceMember);
    void MapFrom(TMember constantValue);
    void Ignore();
    void PreCondition(Expression<Func<TSource, bool>> predicate);
    void Condition(Expression<Func<TSource, TMember, bool>> predicate);

    // ---- NEW (Null Substitution) ----

    /// <summary>
    /// Supplies a fallback value used when the resolved source value is <c>null</c>.
    /// The substitute is typed as the source member and runs through the same conversion
    /// pipeline as a real source value would (numeric / enum auto-conversion, registered
    /// TypeMaps).
    /// </summary>
    /// <typeparam name="TSourceMember">
    /// The source-member type. Compiler-inferred from the literal in the constant overload
    /// or the lambda body in the Expression overload.
    /// </typeparam>
    /// <param name="constant">The fallback value used when the resolved source is null.</param>
    /// <remarks>
    /// Only meaningful when the resolved source-member type is a reference type or
    /// <see cref="Nullable{T}"/>. <see cref="MapperConfiguration.AssertConfigurationIsValid"/>
    /// reports an error if <c>NullSubstitute</c> is configured on a non-nullable
    /// value-typed source member (the substitute would be unreachable). It also reports
    /// an error if the substitute's type is not assignable to the resolved source-member type.
    /// <para>
    /// Pipeline placement: <b>PreCondition → resolve → null-substitute → convert → transform →
    /// Condition → assign</b>. Value transformers and <c>Condition</c> see the substituted
    /// (non-null) value, never the original null.
    /// </para>
    /// <para>
    /// Multiple <c>NullSubstitute</c> calls on the same member: last-call-wins (matches
    /// <c>MapFrom</c>). Repeating clears the prior substitute.
    /// </para>
    /// <para>
    /// On a map configured with <see cref="IMappingExpression{TSource, TDestination}.ConvertUsing(Func{TSource, TDestination})"/>,
    /// per-member substitutes are silently inactive (the converter replaces all per-member assigns).
    /// Substitutes flow base→derived through inheritance via the existing explicit-config
    /// precedence rule. Substitutes do NOT auto-flip across <c>.ReverseMap()</c> —
    /// reconfigure on the reverse expression.
    /// </para>
    /// <para>
    /// Translates to SQL <c>COALESCE</c> in <c>ProjectTo&lt;&gt;()</c>.
    /// </para>
    /// </remarks>
    void NullSubstitute<TSourceMember>(TSourceMember constant);

    /// <summary>
    /// Expression form of <see cref="NullSubstitute{TSourceMember}(TSourceMember)"/>.
    /// Use for computed defaults (e.g., <c>() =&gt; DateTime.UtcNow</c>) that cannot be
    /// expressed as a literal constant.
    /// </summary>
    /// <typeparam name="TSourceMember">
    /// The source-member type. Compiler-inferred from the lambda body's return type.
    /// </typeparam>
    /// <param name="factory">A no-arg lambda that produces the fallback value.</param>
    /// <remarks>
    /// See <see cref="NullSubstitute{TSourceMember}(TSourceMember)"/> for storage,
    /// projection, multi-call, ConvertUsing, and inheritance semantics — they apply identically.
    /// <para>
    /// The factory is stored as <see cref="Expression{TDelegate}"/>. For projection
    /// translation, the body must be translatable by the underlying LINQ provider —
    /// untranslatable factories (custom static method calls, captures of mutable state,
    /// etc.) fail at query-execution time with the provider's standard error. Atlas does
    /// not pre-inspect lambdas for translatability.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="factory"/> is null.</exception>
    void NullSubstitute<TSourceMember>(Expression<Func<TSourceMember>> factory);
}
```

### 3.2 Usage examples

**Constant case (95% of real-world use):**

```csharp
public sealed class CustomerProfile : MapperProfile
{
    public CustomerProfile()
    {
        CreateMap<CustomerEntity, CustomerDto>()
            .ForMember(d => d.Name, opt => opt.NullSubstitute("Unknown"))
            .ForMember(d => d.Score, opt => opt.NullSubstitute(0))
            .ForMember(d => d.LastLogin, opt => opt.NullSubstitute(DateTime.MinValue));
    }
}
```

**Expression case (computed default):**

```csharp
CreateMap<OrderEntity, OrderDto>()
    .ForMember(d => d.GeneratedId, opt => opt.NullSubstitute(() => Guid.NewGuid()))
    .ForMember(d => d.CreatedAt, opt => opt.NullSubstitute(() => DateTime.UtcNow));
```

**Combined with conditional mapping:**

```csharp
CreateMap<OrderEntity, OrderDto>()
    .ForMember(d => d.Description, opt =>
    {
        opt.NullSubstitute("(no description)");                   // resolved value coalesced first
        opt.Condition((s, desc) => desc.Length < 100);            // sees substituted value, never null
    });
```

**ProjectTo example (translates to SQL `COALESCE`):**

```csharp
var dtos = dbContext.Customers
    .ProjectTo<CustomerDto>(mapperConfiguration)
    .ToList();

// Generated SQL (illustrative SQLite):
//
//   SELECT
//     COALESCE([c].[Name], 'Unknown') AS [Name],
//     COALESCE([c].[Score], 0) AS [Score],
//     COALESCE([c].[LastLogin], '0001-01-01 00:00:00') AS [LastLogin],
//     ...
//   FROM [Customers] [c];
```

### 3.3 Interaction matrix (documented in the API XML)

| Other feature | Interaction |
|---|---|
| `ConvertUsing` | Per-member substitutes silently inactive (converter replaces all per-member assigns). Documented; no validator error. |
| `MapFrom(expression)` | Substitute applies to the expression's result if it's null. |
| `MapFrom(constant)` | Substitute is generally unreachable when the constant is non-null. Validator catches type-mismatch but not "constant is always non-null" (silent). |
| `Ignore()` | Substitute irrelevant — member is skipped. Validator skips substitute checks for ignored members. |
| `ForCtorParam` | Substitute applies; resolved value is null-coalesced before being passed as a constructor argument. |
| `ForPath` (multi-level dest chain) | Per-binding; the substitute applies to the value being written into the chain leaf. |
| `Include` / `IncludeBase` (inheritance) | Substitute flows base→derived via the existing `MergeBaseConfig` precedence rule. Derived-explicit overrides base-explicit. |
| `.ReverseMap()` | Substitutes do NOT auto-flip. Reconfigure on the reverse expression. (Scope-A discipline.) |
| `AddTransform<T>` (#6 transformers) | Transformer wraps the post-substitute value. Substitute fires upstream of `WrapWithTransformers`. |
| `BeforeMap` / `AfterMap` (#5 hooks) | Independent. Hooks fire on the whole TypeMap; substitutes are per-member. |
| Enum surface (#3) | Substitute applies to the resolved source value before enum coercion. Enum-only typemaps (both source and dest are enums) don't engage per-member codegen, so substitutes don't apply there. |
| `PreCondition` / `Condition` (#7) | PreCondition gates the whole pipeline including the substitute. Condition's `v` arg is the post-substitute, post-transform value. |

---

## 4. Internal Data Shape

### 4.1 `PropertyMap` — one new field

```csharp
// src/Atlas/Internal/PropertyMap.cs
internal sealed class PropertyMap
{
    // ... existing fields (Name, DestinationType, DestinationProperty, DestinationCtorParameter,
    //     SourcePath, CustomExpression, ConstantValue, HasConstant, Ignored, IsExplicit,
    //     DestinationPath, PreCondition, Condition) ...

    /// <summary>
    /// Source-typed fallback used when the resolved source member is null. Stored as
    /// <c>Expression&lt;Func&lt;TSourceMember&gt;&gt;</c>: the constant overload wraps as
    /// <c>() =&gt; constant</c>; the Expression overload stores the user's lambda directly.
    /// Codegen inlines the lambda body and wraps the resolved source expression in
    /// <see cref="Expression.Coalesce(Expression, Expression)"/> upstream of
    /// <c>ConvertOrMap</c> / <c>ConvertOrInline</c>.
    /// </summary>
    public LambdaExpression? NullSubstitute { get; set; }
}
```

### 4.2 `MemberConfigurationExpression<,,>` — two new methods + ApplyTo extension

```csharp
// src/Atlas/Configuration/MemberConfigurationExpression.cs
internal sealed class MemberConfigurationExpression<TSource, TDestination, TMember>
    : IMemberConfigurationExpression<TSource, TDestination, TMember>
{
    // ... existing private fields (_customExpression, _constantValue, _hasConstant, _ignored,
    //     _preCondition, _condition) ...
    private LambdaExpression? _nullSubstitute;   // NEW

    // ... existing methods ...

    public void NullSubstitute<TSourceMember>(TSourceMember constant)
    {
        // Wrap the constant as a parameterless lambda so storage is uniform with the Expression overload.
        Expression<Func<TSourceMember>> wrapped = () => constant;
        _nullSubstitute = wrapped;
    }

    public void NullSubstitute<TSourceMember>(Expression<Func<TSourceMember>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _nullSubstitute = factory;
    }

    public void ApplyTo(PropertyMap propertyMap)
    {
        // ... existing assignments ...
        propertyMap.NullSubstitute = _nullSubstitute;   // NEW
    }
}
```

**Last-call-wins inside the same callback** matches the existing semantics for `MapFrom` and the #7 predicates. Either overload (constant or Expression) clears any prior substitute.

**Wrapping the constant overload as a lambda** keeps the storage uniform: codegen always reads `pm.NullSubstitute.Body` and inlines it. The constant overload's wrapped lambda has no parameters; the Expression overload's lambda also has no parameters (per §1.3, source-aware substitutes are deferred). So `pm.NullSubstitute.Body` directly produces the substitute value at codegen time.

> **Note on closure capture in the constant overload.** `() => constant` captures the parameter `constant` in a closure. At runtime this is fine (the Expression compiler handles the captured local), but the generated Expression tree contains a `MemberAccess` on a closure-class field rather than an `Expression.Constant`. For projection translation, EF Core handles closure-captured constants natively (it's the same shape produced by `where p.Id == myId` queries with a captured variable). For projection inspection in tests, this means the binding's substitute body is a `MemberExpression` on a display class field, not a `ConstantExpression` — the Task 7 lesson about checking `is ConstantExpression` applies similarly. Tests must use `Assert.True(... is ConstantExpression || ... is MemberExpression)` or check `body.Type` rather than relying on exact-type matching.

### 4.3 `InheritanceMerger.CopyConfig` — one-line extension

```csharp
// src/Atlas/Internal/InheritanceMerger.cs
private static void CopyConfig(PropertyMap source, PropertyMap target)
{
    target.SourcePath = source.SourcePath;
    target.HasConstant = source.HasConstant;
    target.ConstantValue = source.ConstantValue;
    target.CustomExpression = source.CustomExpression;
    target.Ignored = source.Ignored;
    target.PreCondition = source.PreCondition;
    target.Condition = source.Condition;
    target.NullSubstitute = source.NullSubstitute;   // NEW
    // Note: do NOT copy DestinationProperty / DestinationCtorParameter — those are
    // already correctly bound to the target's PropertyMap.
}
```

The existing `MergeBaseConfig` precedence rule handles the base-vs-derived decision: derived-explicit wins, then base-explicit (which now carries the substitute), then derived-convention.

### 4.4 What does NOT change

- `TypeMap` — no new fields. Substitute is per-member.
- `MapperConfigurationExpression` / `MapperProfile` — no new endpoints. There is no global or profile scope for substitutes.
- `MapperRegistry` — unchanged.
- The build-time sequence in `MapperConfiguration` constructor — unchanged. No new resolver call.

---

## 5. In-Memory Codegen (`ExecutionPlanBuilder`)

One new helper, one wire-in. The helper inserts a `Coalesce` between the resolve step and the existing `ConvertOrMap` call, but only when `pm.NullSubstitute` is set. No-op otherwise.

### 5.1 Helper

```csharp
// src/Atlas/Internal/ExecutionPlanBuilder.cs

private static Expression ApplyNullSubstitute(Expression resolvedExpr, PropertyMap pm)
{
    if (pm.NullSubstitute is null) return resolvedExpr;

    // The substitute is a parameterless lambda; inline its body directly.
    var substituteBody = pm.NullSubstitute.Body;

    // The substitute body's type is TSourceMember per the public API. resolvedExpr.Type
    // may be either the same TSourceMember (CustomExpression / SourcePath leaf) or a
    // wrapped form (e.g., Nullable<int>). Coalesce handles Nullable<T> natively (returns
    // the unwrapped T). For non-matching types where the validator hasn't already
    // rejected the config, an explicit Convert keeps the expression tree well-typed.
    if (substituteBody.Type != resolvedExpr.Type)
    {
        // Common case: resolvedExpr is Nullable<T>, substituteBody is T. Expression.Coalesce
        // handles this — pass substituteBody as-is and Coalesce produces a T-typed result.
        // For mismatched non-nullable types we Convert; the validator caught the genuine
        // type-mismatch cases at config-build time.
        if (Nullable.GetUnderlyingType(resolvedExpr.Type) == substituteBody.Type)
        {
            return Expression.Coalesce(resolvedExpr, substituteBody);
        }
        substituteBody = Expression.Convert(substituteBody, resolvedExpr.Type);
    }

    return Expression.Coalesce(resolvedExpr, substituteBody);
}
```

### 5.2 Wire-in: `BuildSourceExpression`

```csharp
// src/Atlas/Internal/ExecutionPlanBuilder.cs

private static Expression? BuildSourceExpression(
    PropertyMap pm,
    ParameterExpression srcParam,
    MapperRegistry registry,
    Type targetType)
{
    if (pm.HasConstant)
        return Expression.Constant(pm.ConstantValue, targetType);

    Expression? resolved;
    if (pm.CustomExpression is not null)
    {
        var rebound = new ParameterReplacer(pm.CustomExpression.Parameters[0], srcParam)
            .Visit(pm.CustomExpression.Body);
        resolved = rebound;
    }
    else if (pm.SourcePath is not null)
    {
        resolved = BuildPathAccess(srcParam, pm.SourcePath.Members);
    }
    else
    {
        return null;
    }

    // NEW: apply NullSubstitute BEFORE ConvertOrMap so the substitute participates
    // in the conversion pipeline exactly like a real value (numeric / enum auto-conversion,
    // registered TypeMaps).
    resolved = ApplyNullSubstitute(resolved!, pm);

    return ConvertOrMap(resolved, targetType, registry);
}
```

### 5.3 No changes to existing helpers

`WrapWithTransformers`, `WrapWithConditions`, `BuildUpdateAssignWithConditions`, `BuildPocoLambda`'s ctor-arg loop, `BuildPocoLambda`'s property-assign loop, and `BuildUpdate`'s property loop are all unchanged. They consume whatever `BuildSourceExpression` returns. Now that may be a `Coalesce` node, but the wrap pipeline doesn't care — it operates on the result type, which is the resolved-source-member type (unwrapped from `Nullable<T>` if applicable), exactly as before.

### 5.4 Concrete trace — reference-type source with substitute

User's config:
```csharp
.ForMember(d => d.Name, opt =>
{
    opt.MapFrom(s => s.Customer.Name);   // s.Customer is reference type, s.Customer.Name is string
    opt.NullSubstitute("Unknown");
});
```

`BuildSourceExpression` produces (whitespace-prettified Expression-tree pseudocode):

```
// Step 1 — resolve via CustomExpression (rebound):
resolved = src.Customer == null ? default(string) : src.Customer.Name
// (the existing path-walker null-safes intermediates to default at the leaf)

// Step 2 — ApplyNullSubstitute wraps in Coalesce:
resolved = Coalesce(
    src.Customer == null ? default(string) : src.Customer.Name,
    () => "Unknown"  // the lambda body is the constant "Unknown"
)

// Step 3 — ConvertOrMap (no change needed; types align: string → string)
final = resolved
```

Compiled runtime behavior:
- `src.Customer` is null → inner expression returns `default(string)` = null → `Coalesce` returns `"Unknown"`.
- `src.Customer.Name` is null (Customer non-null but Name null) → inner expression returns null → `Coalesce` returns `"Unknown"`.
- `src.Customer.Name` is `"Acme"` → inner expression returns `"Acme"` → `Coalesce` returns `"Acme"`.

### 5.5 Concrete trace — Nullable<T> source with substitute

User's config:
```csharp
.ForMember(d => d.Score, opt =>
{
    opt.MapFrom(s => s.Score);   // s.Score is int?
    opt.NullSubstitute(0);
});
```

`BuildSourceExpression` produces:

```
// Step 1 — resolve:
resolved = src.Score   // type: int?

// Step 2 — ApplyNullSubstitute:
//   substituteBody.Type is int (from constant 0 wrapped as () => 0)
//   resolvedExpr.Type is int?
//   Nullable.GetUnderlyingType(int?) == int → match
//   Result: Coalesce(int?, int) → int
resolved = Coalesce(src.Score, () => 0)   // type: int

// Step 3 — ConvertOrMap (int → destination type, e.g., long for numeric widening)
final = Convert(resolved, typeof(long))
```

The `Coalesce` between `int?` and `int` is C#'s standard lifted-coalesce semantic: returns `int.Value` if not null, else the right operand. The result is a non-null `int`, which then participates in the existing numeric widening (e.g., `int → long`).

### 5.6 Concrete trace — combined with Condition (#7) and transformer (#6)

User's config:
```csharp
.ForMember(d => d.Description, opt =>
{
    opt.MapFrom(s => s.Description);
    opt.NullSubstitute("(none)");
    // (transformer registered globally: ValueTransformers.Add<string>(s => s.Trim()))
    opt.Condition((s, desc) => desc.Length < 100);
});
```

The compiled lambda's body for the `Description` assign:

```
// Inside BuildPocoLambda's property-assign loop:
sourceExpr = BuildSourceExpression(pm, srcParam, registry, typeof(string))
// = ConvertOrMap(Coalesce(src.Description, () => "(none)"), typeof(string), registry)
// = Coalesce(src.Description, () => "(none)")   (no further conversion needed; both are string)

transformed = WrapWithTransformers(sourceExpr, typeof(string), typeMap)
// = sourceExpr.Trim()   (parameter-substitution: s in transformer → sourceExpr)
// = Coalesce(src.Description, () => "(none)").Trim()

assignValue = WrapWithConditions(transformed, pm, srcParam, typeof(string))
// Condition is set; hoist the transformed value into a local r:
//   var r = Coalesce(src.Description, () => "(none)").Trim()
//   r.Length < 100 ? r : default(string)
// (hoisting prevents double-evaluation of the transform/coalesce chain)

// Final emit:
dst.Description = assignValue
```

Runtime behavior:
- `src.Description` is null → Coalesce → "(none)" → Trim → "(none)" → length 6 → assigns "(none)".
- `src.Description` is "  hi  " → Coalesce → "  hi  " → Trim → "hi" → length 2 → assigns "hi".
- `src.Description` is a 200-char string → Coalesce → unchanged → Trim → unchanged → length > 100 → Condition fails → assigns default(string) = null.

The substitute and the transformer cooperate cleanly: substitute replaces null upstream, transformer normalizes, Condition gates. No null-NRE risk anywhere.

---

## 6. Projection Codegen (`ProjectionPlanBuilder`)

The projection-side wrap is structurally identical to the in-memory side. LINQ providers translate `Coalesce` to SQL `COALESCE` natively — no Block/Variable concerns (unlike Conditions in #7).

### 6.1 Helper

```csharp
// src/Atlas.Projections/Internal/ProjectionPlanBuilder.cs

private static Expression ApplyProjectionNullSubstitute(Expression resolvedExpr, PropertyMap pm)
{
    if (pm.NullSubstitute is null) return resolvedExpr;

    var substituteBody = pm.NullSubstitute.Body;

    if (substituteBody.Type != resolvedExpr.Type)
    {
        if (Nullable.GetUnderlyingType(resolvedExpr.Type) == substituteBody.Type)
            return Expression.Coalesce(resolvedExpr, substituteBody);
        substituteBody = Expression.Convert(substituteBody, resolvedExpr.Type);
    }

    return Expression.Coalesce(resolvedExpr, substituteBody);
}
```

(Identical body to the in-memory helper; the two helpers are kept separate by file/package even though they share shape, matching the precedent set by `WrapProjectionWithTransformers` / `WrapWithTransformers` and `WrapProjectionWithConditions` / `WrapWithConditions`.)

### 6.2 Wire-in: `BuildBinding`

```csharp
// src/Atlas.Projections/Internal/ProjectionPlanBuilder.cs

private static Expression? BuildBinding(
    Expression srcExpr,
    PropertyMap pm,
    int depth,
    Type targetType,
    MapperRegistry registry,
    int maxDepth)
{
    if (pm.HasConstant)
        return Expression.Constant(pm.ConstantValue, targetType);

    Expression resolved;
    if (pm.CustomExpression is not null)
    {
        resolved = ParameterReplacer.Replace(
            pm.CustomExpression.Body,
            pm.CustomExpression.Parameters[0],
            srcExpr);
    }
    else if (pm.SourcePath is not null)
    {
        resolved = BuildNullSafePath(srcExpr, pm.SourcePath.Members);
    }
    else
    {
        return null;
    }

    // NEW: apply NullSubstitute BEFORE ConvertOrInline.
    resolved = ApplyProjectionNullSubstitute(resolved, pm);

    return ConvertOrInline(resolved, targetType, depth, registry, maxDepth);
}
```

### 6.3 Concrete SQL trace

User's config:
```csharp
CreateMap<Order, OrderDto>()
    .ForMember(d => d.Description, opt =>
    {
        opt.MapFrom(s => s.Description);
        opt.NullSubstitute("(none)");
    });

dbContext.Orders.ProjectTo<OrderDto>(cfg).ToList();
```

`Expression.Bind(Description, ...)` body:
```
Coalesce(srcExpr.Description, () => "(none)")
```

EF Core typically translates this to (illustrative SQLite):
```sql
SELECT
    COALESCE([o].[Description], '(none)') AS [Description],
    ...
FROM [Orders] [o];
```

### 6.4 Why no projection rejection rule

Unlike Hooks (#5, where `RejectHooksOrThrow` throws because hook `Action<TS,TD>` can't be translated) and `ForPath` (rejected by `ProjectionCompatibility.IsBindingProjectable`), substitutes translate. They become a `Coalesce` node which every mature LINQ provider supports.

`ProjectionCompatibility.IsTypeMapProjectable` and `IsBindingProjectable` are unchanged for this feature.

### 6.5 Untranslatable factory bodies

If the user writes:
```csharp
opt.NullSubstitute(() => MyHelpers.ComputeDefault());
```
and `MyHelpers.ComputeDefault` isn't a method EF Core knows how to translate, the `ToList()` call on the projected query throws EF Core's standard:
```
System.InvalidOperationException: The LINQ expression '...ComputeDefault()' could not be translated. ...
```

Atlas does not pre-inspect factories — same precedent as #6 (Value Transformers) and #7 (Conditional Mapping).

---

## 7. Build-Time Pipeline

**Unchanged.** No new step is needed.

Current order (post-#7) inside `MapperConfiguration` constructor:
```
1. Profile.Configure()                                    — TypeMaps registered
2. ConfigExpression conflict-guard (#4)
3. AddProfile harvest (#4)
4. InheritanceMerger.Resolve(typeMaps)                    — propagates ForMember + hooks (#5)
                                                            + predicates (#7)
                                                            + NOW also NullSubstitute (this feature, via CopyConfig)
5. ConventionEngine.ResolveMissingMembers(tm)
6. ReverseMapMirror.Mirror(typeMaps)                      — does NOT propagate substitutes (scope-A)
7. TransformerResolver.Resolve(typeMaps, expression.ValueTransformers)
8. tm.Seal() for each TypeMap
9. (On AssertConfigurationIsValid) ConfigurationValidator.Validate
                                                            + NOW includes ValidateNullSubstitutes
10. CompileMappings — codegen reads PropertyMap.NullSubstitute and wraps source-side expressions
```

Substitutes "propagate" via inheritance because step 4's `MergeBaseConfig` calls the extended `CopyConfig` (§4.3). Substitutes do NOT propagate via reverse-map (step 6) — `ReverseMapMirror` constructs reverse-direction `PropertyMap`s from scratch; per scope-A discipline, per-member options like `MapFrom`/`Ignore`/predicates/substitutes do not auto-flip.

---

## 8. Validation

Two new rules in `ConfigurationValidator`. Both run during `AssertConfigurationIsValid()`. Both are always-on (no opt-in).

### 8.1 `ValidateNullSubstitutes`

```csharp
// src/Atlas/Internal/ConfigurationValidator.cs

private static void ValidateNullSubstitutes(TypeMap tm, List<ConfigurationError> errors)
{
    foreach (var pm in tm.PropertyMaps)
    {
        if (pm.NullSubstitute is null) continue;
        if (pm.Ignored) continue;          // ignored members don't reach codegen
        if (pm.HasConstant) continue;      // literal MapFrom can never be null

        // Determine the resolved source-member type:
        //   - CustomExpression: lambda's body return type.
        //   - SourcePath: leaf property type.
        //   - Otherwise (unresolved): skip — covered by other validator rules.
        var sourceType = ResolveSourceMemberType(pm);
        if (sourceType is null) continue;

        // Rule 1 — Unreachable: non-nullable value type can never be null.
        if (sourceType.IsValueType && Nullable.GetUnderlyingType(sourceType) is null)
        {
            errors.Add(new ConfigurationError(
                tm.SourceType, tm.DestinationType, pm.Name,
                $"NullSubstitute on member '{pm.Name}' is unreachable: source member type " +
                $"{sourceType.Name} is a non-nullable value type and cannot be null."));
            continue;
        }

        // Rule 2 — Type mismatch: substitute must be assignable to source type.
        var substituteType = pm.NullSubstitute.Body.Type;
        var underlyingSourceType = Nullable.GetUnderlyingType(sourceType) ?? sourceType;

        if (!underlyingSourceType.IsAssignableFrom(substituteType)
            && !sourceType.IsAssignableFrom(substituteType)
            && !NumericConversions.HasImplicitConversion(substituteType, underlyingSourceType))
        {
            errors.Add(new ConfigurationError(
                tm.SourceType, tm.DestinationType, pm.Name,
                $"NullSubstitute on member '{pm.Name}' has type {substituteType.Name} " +
                $"which is not assignable to source-member type {sourceType.Name}."));
        }
    }
}

private static Type? ResolveSourceMemberType(PropertyMap pm)
{
    if (pm.CustomExpression is not null) return pm.CustomExpression.Body.Type;
    if (pm.SourcePath is { Members.Count: > 0 } sp) return sp.Members[^1].PropertyType;
    return null;
}
```

`ConfigurationValidator.Validate` gets one new call: `ValidateNullSubstitutes(tm, errors);` alongside the existing `ValidateEnum`, `ValidatePaths`, `ValidateHooks`, etc.

### 8.2 What we explicitly DO NOT validate

- **Factory-Expression translatability for `ProjectTo`.** Untranslatable factories fail at query time. Same precedent as #6 and #7.
- **Substitute "always non-null at runtime".** A substitute factory like `() => null` is technically legal but defeats the feature. Atlas doesn't try to detect this — same way `MapFrom(s => null)` is legal.
- **Substitute side effects.** A substitute that mutates state is a user bug; Atlas doesn't try to detect it. Per the API XML, substitutes are documented as pure.
- **Substitute-on-map-with-`ConvertUsing`.** Documented as silently inactive (the converter replaces all per-member assigns). No new warning — `ConvertUsing` already silently bypasses `MapFrom`, `Ignore`, predicates, etc.

### 8.3 Reachable / unreachable / mismatch examples

| Source-member type | Substitute literal | Verdict | Reason |
|---|---|---|---|
| `string` | `"Unknown"` | ✅ reachable, type matches | Reference type can be null. |
| `Customer` | `new Customer()` | ✅ reachable, type matches | Reference type can be null. |
| `int?` | `0` | ✅ reachable, lifted | `int → int?` is implicit. |
| `DateTime?` | `DateTime.MinValue` | ✅ reachable, lifted | `DateTime → DateTime?` is implicit. |
| `int` | `0` | ❌ unreachable | Non-nullable value type. |
| `DateTime` | `DateTime.MinValue` | ❌ unreachable | Non-nullable value type. |
| `OrderStatus` (enum) | `OrderStatus.Pending` | ❌ unreachable | Non-nullable value type. |
| `string` | `42` | ❌ type mismatch | `int` not assignable to `string`. |
| `int?` | `"zero"` | ❌ type mismatch | `string` not assignable to `int`/`int?`. |

---

## 9. Test Plan

Total: **~30 tests**. Test baseline goes from **432 → ~462** after this feature.

### 9.1 `PropertyMapNullSubstituteTests` (Atlas.Tests/Internal)

Add 2 tests:

1. `NewPropertyMap_NullSubstitute_DefaultsToNull` — fresh PM has null field.
2. `PropertyMap_AcceptsNullSubstituteLambda` — round-trip identity check on the stored `LambdaExpression`.

### 9.2 `MappingExpressionNullSubstituteTests` (Atlas.Tests)

Add 6 tests:

1. `NullSubstitute_ConstantOverload_StoredOnPropertyMap` — wraps as parameterless lambda.
2. `NullSubstitute_ExpressionOverload_StoredOnPropertyMap` — stored as-is.
3. `NullSubstitute_ExpressionOverload_NullArg_Throws` — `ArgumentNullException`.
4. `NullSubstitute_LastCallWins` — second call clears first.
5. `NullSubstitute_ConstantThenExpression_LastWins` — second-call-wins across overloads.
6. `NullSubstitute_BodyTypeMatchesGenericArg` — round-trip type-check for both overloads.

### 9.3 `InheritanceMergerNullSubstituteTests` (Atlas.Tests/Internal)

Add 2 tests:

1. `BaseNullSubstitute_PropagatesToDerived_WhenDerivedHasNoExplicit` — propagation via `CopyConfig`.
2. `DerivedExplicit_OverridesBaseExplicit_NullSubstitute` — derived-wins precedence.

### 9.4 `ExecutionPlanBuilderNullSubstituteTests` (Atlas.Tests)

Add 8 tests covering codegen via real `IMapper.Map<>()`:

1. `ReferenceTypeSourceNull_UsesSubstitute` — string source returns substitute.
2. `ReferenceTypeSourceNonNull_BypassesSubstitute` — string source uses real value.
3. `NullableValueTypeSourceNull_UsesSubstitute` — `int?` source returns substituted `int`.
4. `NullableValueTypeSourceNonNull_UsesValue` — `int?` source uses real value.
5. `SubstituteParticipatesInNumericConversion` — substitute `int → long` widening works after Coalesce.
6. `CtorParam_WithNullSubstitute_Works` — ctor-arg path applies the substitute.
7. `ForPath_LeafWithNullSubstitute_Works` — multi-level dest path with substitute.
8. `Substitute_Combined_With_TransformerAndCondition` — pipeline order: substitute → transform → condition.

### 9.5 `ConfigurationValidatorNullSubstituteTests` (Atlas.Tests)

Add 5 tests:

1. `Validator_NullSubstitute_OnNonNullableValueType_Errors` — `int` source.
2. `Validator_NullSubstitute_OnEnum_Errors` — non-nullable enum source.
3. `Validator_NullSubstitute_OnNullableValueType_Passes` — `int?` source.
4. `Validator_NullSubstitute_OnReferenceType_Passes` — `string` source.
5. `Validator_NullSubstitute_TypeMismatch_Errors` — `string` substitute on `int?` source.

### 9.6 `MapperNullSubstituteTests` (Atlas.Tests)

Add 3 end-to-end tests via real `IMapper`:

1. `HeadlineExample_FromReferenceDoc` — the example from §3.2; assert behavior under null and non-null sources.
2. `Update_NullSubstitute_AppliesUniformly` — update-in-place inherits the substitute behavior automatically.
3. `Inheritance_BaseSubstitute_FlowsToDerived` — full flow E2E.

### 9.7 `ProjectionPlanBuilderNullSubstituteTests` (Atlas.Projections.Tests)

Add 2 tests over the inspected expression tree:

1. `Projection_BindingContainsCoalesce_WhenSubstituteSet` — assert presence.
2. `Projection_BindingHasNoCoalesce_WhenSubstituteUnset` — no spurious wrap.

### 9.8 `ProjectTo_NullSubstituteTests` (Atlas.Projections.Tests.EFCore)

Add 2 end-to-end tests against in-memory EF Core SQLite:

1. `ProjectTo_NullSubstitute_GeneratesCoalesceSql` — translate query, capture SQL, assert it contains `COALESCE`.
2. `ProjectTo_NullSubstitute_RowReturnsSubstitutedValue` — seed test data with null column; assert materialized DTO has the substituted value.

### 9.9 What we do NOT add tests for

- **Update-in-place specific tests** beyond the smoke check in §9.6 #2 — `BuildUpdate` calls `BuildSourceExpression` so update-in-place uses the substitute uniformly. No special codegen path to test.
- **Untranslatable factory detection** — by design, we don't pre-inspect; the LINQ provider's natural error path is the documented behavior.

### 9.10 Coverage targets

Same as prior features: line ≥ 90%, branch ≥ 80% on the changed assemblies. The Atlas core change-set is small (one helper + one wire-in + two validator rules + one field copy). Coverage should land comfortably in the high 90s on Atlas core. Projections likewise.

---

## 10. README Updates

Three changes to `README.md`:

1. **New "Null substitution" subsection** under the existing per-member configuration area (between "Conditional mapping" and "What's in v1"):

   ```markdown
   ### Null substitution

   `NullSubstitute` supplies a fallback value when the resolved source member is null.
   The substitute is source-typed and runs through the same conversion pipeline as a
   real source value.

   ```csharp
   CreateMap<CustomerEntity, CustomerDto>()
       .ForMember(d => d.Name, opt => opt.NullSubstitute("Unknown"))
       .ForMember(d => d.Score, opt => opt.NullSubstitute(0))
       .ForMember(d => d.GeneratedId, opt => opt.NullSubstitute(() => Guid.NewGuid()));
   ```

   Pipeline placement: **PreCondition → resolve → null-substitute → convert → transform →
   Condition → assign**. Value transformers and `Condition` see the substituted (non-null)
   value, never the original null.

   Validator rules:
   - **Unreachable substitute** on a non-nullable value-typed source member errors at
     `AssertConfigurationIsValid()`.
   - **Type-mismatch** when the substitute's type isn't assignable to the source-member type errors.

   Translates to SQL `COALESCE` in `ProjectTo<>()`. Substitutes flow base→derived through
   inheritance via the existing explicit-config precedence rule. Substitutes do NOT
   auto-flip across `.ReverseMap()`.
   ```

2. **Coverage / test-count refresh** — bump `432` to `~462` if the README quotes a number.

3. **`ProjectTo` capability section** — confirm null substitution IS translatable, contrast with hooks and `ForPath` (which are rejected).

---

## 11. Risks & Implementer Notes

These are repeated in the implementation plan for in-task visibility, but listed here for the design-doc reader.

### 11.1 Cross-package consumer audit (Bug-4 lesson applied)

The new `PropertyMap.NullSubstitute` field is added to a **shared data shape** consumed by both `Atlas` and `Atlas.Projections`. Per the lesson from feature #4 (ReverseMap), both consumers must be updated in adjacent plan tasks so the spec reviewer can verify cross-package coverage in one pass. The plan should put the Atlas core wire-in and the Atlas.Projections wire-in in adjacent tasks (or a single combined task if the changes are small enough).

### 11.2 NOT scope-identifying TypeMap metadata (Bug-5 lesson applied)

The new field lives on `PropertyMap`, NOT `TypeMap`. It is NOT scope-identifying metadata that needs propagation across related-typemap creators (`ReverseMap`, future inheritance-derived maps). Inheritance propagation is already handled by `MergeBaseConfig`/`CopyConfig`. Reverse-map propagation is intentionally NOT done (per scope-A discipline — same as `MapFrom`, `Ignore`, hooks, transformers, predicates).

### 11.3 Validator must run AFTER `ConventionEngine.ResolveMissingMembers`

`ResolveSourceMemberType` reads either `CustomExpression.Body.Type` (always available at config-build time) or `SourcePath.Members[^1].PropertyType` (populated by `ConventionEngine` for auto-flattened bindings). The validator already runs after `ConventionEngine` per the existing build-time sequence — don't move the new validator call earlier.

### 11.4 `Coalesce` type-coercion subtleties

When the substitute body's type doesn't exactly match the resolved expression's type, `Expression.Coalesce` handles the lifted case (`Nullable<T>` ↔ `T`) natively but requires `Expression.Convert` for non-lifted mismatches. The helper's `Nullable.GetUnderlyingType` check handles the common `int? + int` case; for genuinely-mismatched types the validator catches the configuration error before codegen runs. Test cases must include both the matching-type case (no Convert needed) and the lifted case to exercise both branches of the helper.

### 11.5 Don't try to "optimize" the constant-overload by skipping the lambda wrap

The constant overload wraps as `() => constant`. A naive optimization would skip the wrap and store an `Expression.Constant` directly. Resist this — uniform `LambdaExpression` storage means codegen has one shape (`pm.NullSubstitute.Body`) regardless of overload. Diverging shapes would force codegen to branch.

### 11.6 Holistic review is non-negotiable

Per the established workflow rhythm (`feedback_atlas_v2_workflow.md`), the final holistic review (`superpowers:code-reviewer`) catches cross-task or whole-feature concerns even when per-task reviews are spotless. The Value Transformers branch (#6) was the empirical proof — holistic caught a Critical reverse-map-profile-propagation bug despite ALL 10 per-task reviews passing cleanly. Conditional Mapping (#7) achieved a clean holistic pass by correctly applying the prior bug lessons. Don't skip the holistic review for this feature.

---

## 12. Worked End-to-End Example

This section traces a full `Map<>()` and a full `ProjectTo<>()` through the codegen for a realistic configuration combining null substitution, transformers, and conditions.

### 12.1 Setup

```csharp
public class Customer
{
    public string? Name { get; set; }
    public int? Score { get; set; }
    public DateTime? LastLogin { get; set; }
}
public class CustomerDto
{
    public string Name { get; set; } = "";
    public long Score { get; set; }                      // numeric widening: int → long
    public DateTime LastLogin { get; set; }
}

public class CustomerProfile : MapperProfile
{
    public CustomerProfile()
    {
        ValueTransformers.Add<string>(s => s.Trim());    // global string transformer

        CreateMap<Customer, CustomerDto>()
            .ForMember(d => d.Name, opt =>
            {
                opt.NullSubstitute("Anonymous");          // null source → "Anonymous"
            })
            .ForMember(d => d.Score, opt =>
            {
                opt.NullSubstitute(0);                    // null source → 0 (int → long via existing widening)
            })
            .ForMember(d => d.LastLogin, opt =>
            {
                opt.NullSubstitute(() => DateTime.UnixEpoch);   // computed default
            });
    }
}
```

### 12.2 In-memory codegen for the `Name` assign

After `BuildSourceExpression`:
```
sourceExpr = ConvertOrMap(
    Coalesce(srcParam.Name, () => "Anonymous"),    // Coalesce inserted by ApplyNullSubstitute
    typeof(string),
    registry)
// = Coalesce(srcParam.Name, () => "Anonymous")     (no further conversion needed — both are string)
```

After `WrapWithTransformers` (the global `s => s.Trim()`):
```
transformed = Coalesce(srcParam.Name, () => "Anonymous").Trim()
```

After `WrapWithConditions` (no Condition on this member):
```
assignValue = transformed
```

Final emit:
```
Assign(Property(destVar, "Name"), assignValue)
```

Runtime:
- `srcParam.Name` is null → Coalesce → "Anonymous" → Trim → "Anonymous" → assigned.
- `srcParam.Name` is "  Alice  " → Coalesce → "  Alice  " → Trim → "Alice" → assigned.

### 12.3 In-memory codegen for the `Score` assign

After `BuildSourceExpression`:
```
sourceExpr = ConvertOrMap(
    Coalesce(srcParam.Score, () => 0),     // type: int (Nullable<int> coalesces to int)
    typeof(long),
    registry)
// ConvertOrMap detects int → long is implicit numeric widening:
// = Convert(Coalesce(srcParam.Score, () => 0), typeof(long))
```

No transformer registered for `int`/`long`; no Condition; no wrap. Final assign emits:
```
Assign(Property(destVar, "Score"), Convert(Coalesce(srcParam.Score, () => 0), typeof(long)))
```

Runtime:
- `srcParam.Score` is null → Coalesce → 0 → Convert → 0L → assigned.
- `srcParam.Score` is 42 → Coalesce → 42 → Convert → 42L → assigned.

### 12.4 Projection codegen for the same TypeMap

Bindings emitted to `Expression.MemberInit`:

```
Bind(Name,
    Coalesce(srcExpr.Name, () => "Anonymous").Trim())

Bind(Score,
    Convert(Coalesce(srcExpr.Score, () => 0), typeof(long)))

Bind(LastLogin,
    Coalesce(srcExpr.LastLogin, () => DateTime.UnixEpoch))
```

EF Core SQL (illustrative SQLite):
```sql
SELECT
    rtrim(ltrim(coalesce("c"."Name", 'Anonymous'))) AS "Name",
    CAST(coalesce("c"."Score", 0) AS INTEGER) AS "Score",
    coalesce("c"."LastLogin", '1970-01-01 00:00:00') AS "LastLogin"
FROM "Customers" "c";
```

### 12.5 Behavior verification

| Customer row | `Name` | `Score` | `LastLogin` | Map<> result | ProjectTo result |
|---|---|---|---|---|---|
| `{"Alice", 42, 2024-06-01}` | "Alice" | 42 | 2024-06-01 | identical row | identical row |
| `{"  Alice  ", 42, 2024-06-01}` | "Alice" (trimmed) | 42 | 2024-06-01 | same | same |
| `{null, null, null}` | "Anonymous" | 0 | 1970-01-01 | same | same |
| `{"  ", null, null}` | "" (post-trim) | 0 | 1970-01-01 | same | same |

Both pipelines (in-memory `Map<>` and `ProjectTo`) produce the same observable values for every row.

---

*End of design.*
