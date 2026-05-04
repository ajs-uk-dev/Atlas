# Atlas Reverse Mapping & Unflattening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `.ReverseMap()` and `.ForPath` to Atlas's fluent surface per `docs/Atlas-Design-ReverseMap.md`. `.ReverseMap()` registers the inverse `(TDest, TSource)` map, auto-inverts conventions and source-side flattening (`Customer.Name → CustomerName` flips to unflattening on the way back), and defaults to `MemberList.None` validation. `.ForPath` accepts nested destination chains (e.g., `d => d.Customer.Name`) on either direction with auto-instantiation of intermediate objects.

**Architecture:** Purely additive to `Atlas` core. Three new fields on `TypeMap` (`ReverseMapPair`, `CachedReverseExpression`, `RegistrationOrigin`). One new field on `PropertyMap` (`DestinationPath`) plus a `ForPath` factory. Two new methods on `IMappingExpression<,>` (`ReverseMap`, `ForPath`). One new sink parameter on `MappingExpression<,>` constructor. Conflict guard added inside `MapperConfigurationExpression.RegisterTypeMap` (new private helper). One new internal static class (`ReverseMapMirror`). One new `BuildNestedAssign` helper in `ExecutionPlanBuilder`. Two new always-on validation rules in `ConfigurationValidator`. No new public types, no new packages, no `Atlas.Projections` changes.

**Tech Stack:** .NET 10, xUnit v3 (built-in `Assert.X()`, no FluentAssertions), coverlet.

**Spec reference:** `docs/Atlas-Design-ReverseMap.md`. Section numbers in this plan (e.g. "§6.1") refer to the spec.

**v1 conventions to mirror (do not deviate):**
- File-scoped namespaces.
- Internal types under `Internal/` subfolder.
- `internal sealed class` / `internal static class` unless otherwise noted.
- Test naming: `MethodOrFeature_Condition_ExpectedResult`.
- xUnit v3, `[Fact]` / `[Theory]` + `[InlineData]`.
- `TreatWarningsAsErrors=true` is on globally; `GenerateDocumentationFile=true` is on; `CS1591` is suppressed.

**Branching:** Implement on a new branch `feat/reverse-map` cut from current `main` (HEAD `2db0b22` after the design + this plan land). Each task ends in a commit. After all tasks land, the implementer runs the `superpowers:finishing-a-development-branch` flow (Option 2: push + PR) per the same pattern used for `feat/enum-surface`.

**Key files in the v1 + Inheritance + Enum codebase to read first** (for context):
- `src/Atlas/Internal/TypeMap.cs` — fields added in Task 2
- `src/Atlas/Internal/PropertyMap.cs` — field + factory added in Task 2
- `src/Atlas/Internal/ExecutionPlanBuilder.cs` — `BuildNestedAssign` added in Task 4 (insertion point: `BuildPocoLambda` line 208-219, the `Expression.Property(destVar, pm.DestinationProperty)` line)
- `src/Atlas/Internal/ConfigurationValidator.cs` — path guards added in Task 5 (insertion point: top of `Validate` method, line 16, alongside other always-on rules)
- `src/Atlas/Internal/ConventionEngine.cs` — no modifications (read-only context)
- `src/Atlas/Internal/MapperRegistry.cs` — no modifications (read-only context)
- `src/Atlas/Configuration/IMappingExpression.cs` — methods added in Tasks 3 + 6
- `src/Atlas/Configuration/MappingExpression.cs` — methods + sink added in Tasks 3 + 6
- `src/Atlas/MapperProfile.cs` — sink wiring added in Task 6
- `src/Atlas/MapperConfigurationExpression.cs` — sink wiring + RegisterTypeMap added in Tasks 6-7
- `src/Atlas/MapperConfiguration.cs` — Mirror call added in Task 9 (insertion point: between line 42 and line 44)

**Test count baseline:** 273 tests pre-feature (213 Atlas + 52 Projections + 8 Projections.EFCore) — verified at HEAD `2db0b22` before plan commit. Expected after this plan: ~319 (≈46 new reverse-map tests).

**Coverage targets:** line ≥ 90%, branch ≥ 80% on `Atlas` core. Verified by Task 11.

---

## Task 1: Set up branch

**Files:** none modified; branch creation only.

- [ ] **Step 1: Create the feature branch**

```powershell
git checkout main
git pull
git checkout -b feat/reverse-map
```

- [ ] **Step 2: Verify clean baseline**

Run: `dotnet test --nologo`

Expected: all 273 tests pass (213 Atlas + 52 Projections + 8 Projections.EFCore). If the count differs from 273, note the actual number — the final task verifies (baseline + ~46) tests pass post-feature.

If any test fails, stop and report — the baseline must be green before changes start.

- [ ] **Step 3: No commit** — branching only.

---

## Task 2: Data model — TypeMap fields, PropertyMap.DestinationPath, PropertyMap.ForPath factory

**Files:**
- Modify: `src/Atlas/Internal/TypeMap.cs`
- Modify: `src/Atlas/Internal/PropertyMap.cs`
- Create: `tests/Atlas.Tests/Internal/PropertyMapDestinationPathTests.cs`

This task adds the data-model plumbing that subsequent tasks will use. No fluent surface yet, no compilation changes — just the fields and the factory. Spec references: §5.1, §5.2.

- [ ] **Step 1: Write failing tests**

Create `tests/Atlas.Tests/Internal/PropertyMapDestinationPathTests.cs`:

```csharp
using System.Reflection;
using Atlas.Internal;

namespace Atlas.Tests.Internal;

public class PropertyMapDestinationPathTests
{
    private sealed class Outer { public Inner? Child { get; set; } }
    private sealed class Inner { public string? Name { get; set; } }

    [Fact]
    public void ForPath_StoresPathAndLeafInDestinationProperty()
    {
        var childProp = typeof(Outer).GetProperty(nameof(Outer.Child))!;
        var nameProp = typeof(Inner).GetProperty(nameof(Inner.Name))!;
        var path = new[] { childProp, nameProp };

        var pm = PropertyMap.ForPath(path);

        Assert.Equal(path, pm.DestinationPath);
        Assert.Same(nameProp, pm.DestinationProperty);
        Assert.Equal(typeof(string), pm.DestinationType);
    }

    [Fact]
    public void ForPath_NameIsDottedJoin()
    {
        var childProp = typeof(Outer).GetProperty(nameof(Outer.Child))!;
        var nameProp = typeof(Inner).GetProperty(nameof(Inner.Name))!;

        var pm = PropertyMap.ForPath(new[] { childProp, nameProp });

        Assert.Equal("Child.Name", pm.Name);
    }

    [Fact]
    public void ForPath_EmptyPath_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => PropertyMap.ForPath(Array.Empty<PropertyInfo>()));
        Assert.Contains("at least one property", ex.Message);
    }
}
```

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~PropertyMapDestinationPathTests" --nologo`

Expected: 3 failures — `PropertyMap.ForPath` does not exist; `PropertyMap.DestinationPath` does not exist.

- [ ] **Step 3: Add `DestinationPath` property and `ForPath` factory to `PropertyMap`**

Edit `src/Atlas/Internal/PropertyMap.cs` — add the new property after `IsExplicit` and the factory after `ForCtorParam`:

```csharp
using System.Linq.Expressions;
using System.Reflection;

namespace Atlas.Internal;

/// <summary>
/// One destination-binding mapping rule. Three mutually-exclusive resolution states:
/// source-resolved (carries a member path or expression), constant (carries a value), or ignored.
/// Bindings target either a property (<see cref="DestinationProperty"/> set) or a constructor
/// parameter (<see cref="DestinationCtorParameter"/> set).
/// </summary>
internal sealed class PropertyMap
{
    public string Name { get; }
    public Type DestinationType { get; }
    public PropertyInfo? DestinationProperty { get; }
    public ParameterInfo? DestinationCtorParameter { get; }

    public SourceMemberPath? SourcePath { get; set; }
    public LambdaExpression? CustomExpression { get; set; }
    public object? ConstantValue { get; set; }
    public bool HasConstant { get; set; }
    public bool Ignored { get; set; }
    /// <summary>
    /// True when this binding was configured via <c>ForMember</c> / <c>ForCtorParam</c> /
    /// <c>ForPath</c> (an explicit user choice). False when populated by <c>ConventionEngine</c>
    /// or <c>ReverseMapMirror</c>. Used by <c>InheritanceMerger</c> as the precedence
    /// discriminator: derived explicit beats base explicit beats derived convention.
    /// Also used by <c>ReverseMapMirror</c> skip-rule-2 (the user-explicit top-level guard).
    /// </summary>
    public bool IsExplicit { get; set; }

    /// <summary>
    /// Non-null when this binding writes into a nested destination chain (e.g.,
    /// Customer.Name) rather than a single property. The leaf is the writable target;
    /// intermediates are auto-instantiated at runtime via parameterless constructor.
    /// When null, <see cref="DestinationProperty"/> is used (single-level write — current
    /// behavior).
    /// </summary>
    public IReadOnlyList<PropertyInfo>? DestinationPath { get; set; }

    public bool IsResolved => Ignored || HasConstant || CustomExpression is not null || SourcePath is not null;

    private PropertyMap(string name, Type destinationType, PropertyInfo? prop, ParameterInfo? ctorParam)
    {
        Name = name;
        DestinationType = destinationType;
        DestinationProperty = prop;
        DestinationCtorParameter = ctorParam;
    }

    public static PropertyMap ForProperty(PropertyInfo property) =>
        new(property.Name, property.PropertyType, property, null);

    public static PropertyMap ForCtorParam(ParameterInfo parameter) =>
        new(parameter.Name ?? throw new ArgumentException("Constructor parameter must have a name.", nameof(parameter)),
            parameter.ParameterType, null, parameter);

    /// <summary>
    /// Factory for nested-path bindings. Produces a PropertyMap whose <see cref="Name"/>
    /// is the dotted path ("Customer.Name") for diagnostics, whose
    /// <see cref="DestinationProperty"/> is the leaf (so existing consumers like
    /// <c>ConventionEngine</c> and <c>ConfigurationValidator</c> see a stable
    /// "single property" view), and whose <see cref="DestinationPath"/> carries the full chain.
    /// </summary>
    public static PropertyMap ForPath(IReadOnlyList<PropertyInfo> path)
    {
        if (path is null || path.Count == 0)
            throw new ArgumentException("Path must contain at least one property.", nameof(path));
        var leaf = path[^1];
        var pm = new PropertyMap(string.Join('.', path.Select(p => p.Name)),
                                 leaf.PropertyType, leaf, null);
        pm.DestinationPath = path;
        return pm;
    }
}
```

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~PropertyMapDestinationPathTests" --nologo`

Expected: 3/3 pass.

- [ ] **Step 5: Add fields to `TypeMap`**

Edit `src/Atlas/Internal/TypeMap.cs` — add three new properties after `EnumConfig`:

```csharp
namespace Atlas.Internal;

/// <summary>
/// Configuration for a single (source, destination) type pair. Mutable during configuration build,
/// frozen by <see cref="Seal"/> when the configuration is constructed.
/// </summary>
internal sealed class TypeMap
{
    public Type SourceType { get; }
    public Type DestinationType { get; }
    public MemberList MemberList { get; set; }
    public List<PropertyMap> PropertyMaps { get; } = new();

    public List<TypePair> IncludedDerived { get; } = new();
    public List<TypePair> IncludedBases { get; } = new();

    public EnumMapConfig? EnumConfig { get; set; }

    /// <summary>
    /// When this map was created via <c>.ReverseMap()</c> on another map, points back to
    /// that forward pair. Used by <see cref="ReverseMapMirror"/> to know which forward to
    /// read from, AND by the conflict guard in <c>MapperConfigurationExpression</c> to
    /// detect duplicate registrations. Null for maps registered directly via
    /// <see cref="MapperProfile.CreateMap{TSource,TDestination}"/>.
    /// </summary>
    public TypePair? ReverseMapPair { get; set; }

    /// <summary>
    /// Cached reverse <c>MappingExpression</c> instance for idempotent <c>.ReverseMap()</c>
    /// calls. Boxed as <c>object?</c> because the generic args differ from the forward map's.
    /// Set by the first <c>.ReverseMap()</c> call on the corresponding forward
    /// <c>MappingExpression</c>; null on the reverse TypeMap and on TypeMaps that were
    /// never reversed.
    /// </summary>
    public object? CachedReverseExpression { get; set; }

    /// <summary>
    /// Human-readable origin string for diagnostic messages
    /// (<c>"CreateMap&lt;Order, OrderDto&gt;()"</c> or
    /// <c>"CreateMap&lt;Order, OrderDto&gt;().ReverseMap()"</c>). Set at construction in
    /// <see cref="MapperProfile.CreateMap"/>, <c>MapperConfigurationExpression.CreateMap</c>,
    /// and <c>MappingExpression.ReverseMap</c>. Empty string for TypeMaps constructed in
    /// tests that don't care about the origin.
    /// </summary>
    public string RegistrationOrigin { get; set; } = string.Empty;

    public Delegate? CustomConverter { get; set; }
    public bool IsSealed { get; private set; }

    public TypePair Pair => new(SourceType, DestinationType);

    public TypeMap(Type sourceType, Type destinationType, MemberList memberList)
    {
        SourceType = sourceType;
        DestinationType = destinationType;
        MemberList = memberList;
    }

    public void EnsureMutable()
    {
        if (IsSealed)
            throw new InvalidOperationException(
                $"TypeMap {SourceType.Name} -> {DestinationType.Name} is sealed and cannot be modified.");
    }

    public void Seal() => IsSealed = true;
}
```

- [ ] **Step 6: Run full test suite**

Run: `dotnet test --nologo`

Expected: 273 tests still pass (additive change — no existing behavior modified). The 3 new tests pushed the total to 276.

- [ ] **Step 7: Commit**

```powershell
git add src/Atlas/Internal/TypeMap.cs src/Atlas/Internal/PropertyMap.cs tests/Atlas.Tests/Internal/PropertyMapDestinationPathTests.cs
git commit -m "Add TypeMap.ReverseMapPair/CachedReverseExpression/RegistrationOrigin + PropertyMap.DestinationPath + ForPath factory (3 tests)"
```

---

## Task 3: `ForPath` fluent surface

**Files:**
- Modify: `src/Atlas/Configuration/IMappingExpression.cs`
- Modify: `src/Atlas/Configuration/MappingExpression.cs`
- Create: `tests/Atlas.Tests/MappingExpressionForPathTests.cs`

Add `ForPath<TMember>(Expression<Func<TDestination, TMember>>, Action<...>)` to the fluent surface. Walks `MemberExpression` chains. Spec references: §4.2.

- [ ] **Step 1: Write failing tests**

Create `tests/Atlas.Tests/MappingExpressionForPathTests.cs`:

```csharp
using Atlas.Configuration;
using Atlas.Internal;

namespace Atlas.Tests;

public class MappingExpressionForPathTests
{
    public sealed class Src { public string? Value { get; set; } }
    public sealed class Inner { public string? Name { get; set; } public int Count { get; set; } }
    public sealed class Outer { public Inner? Child { get; set; } public Mid? Middle { get; set; } public string? Top { get; set; } }
    public sealed class Mid { public Inner? Deep { get; set; } }

    private static MappingExpression<Src, Outer> NewExpr() =>
        new(new TypeMap(typeof(Src), typeof(Outer), MemberList.None));

    [Fact]
    public void ForPath_SingleLevel_EquivalentToForMember()
    {
        var expr = NewExpr();
        expr.ForPath(d => d.Top, opt => opt.MapFrom(s => s.Value));

        var pm = expr.TypeMap.PropertyMaps.Single(p => p.Name == "Top");
        Assert.NotNull(pm.SourcePath);
        Assert.Equal("Value", pm.SourcePath!.Members.Single().Name);
        Assert.Null(pm.DestinationPath);   // single-level uses ForProperty path, not ForPath path
        Assert.True(pm.IsExplicit);
    }

    [Fact]
    public void ForPath_TwoLevelChain_StoresFullPath()
    {
        var expr = NewExpr();
        expr.ForPath(d => d.Child!.Name, opt => opt.MapFrom(s => s.Value));

        var pm = expr.TypeMap.PropertyMaps.Single(p => p.Name == "Child.Name");
        Assert.NotNull(pm.DestinationPath);
        Assert.Collection(pm.DestinationPath!,
            p => Assert.Equal("Child", p.Name),
            p => Assert.Equal("Name", p.Name));
        Assert.True(pm.IsExplicit);
    }

    [Fact]
    public void ForPath_ThreeLevelChain_StoresFullPath()
    {
        var expr = NewExpr();
        expr.ForPath(d => d.Middle!.Deep!.Count, opt => opt.MapFrom(s => 5));

        var pm = expr.TypeMap.PropertyMaps.Single(p => p.Name == "Middle.Deep.Count");
        Assert.NotNull(pm.DestinationPath);
        Assert.Collection(pm.DestinationPath!,
            p => Assert.Equal("Middle", p.Name),
            p => Assert.Equal("Deep", p.Name),
            p => Assert.Equal("Count", p.Name));
    }

    [Fact]
    public void ForPath_MethodCallInChain_Throws()
    {
        var expr = NewExpr();
        var ex = Assert.Throws<ArgumentException>(() =>
            expr.ForPath(d => d.Top!.ToUpper(), opt => opt.MapFrom(s => s.Value)));
        Assert.Contains("chain of property accesses", ex.Message);
    }

    [Fact]
    public void ForPath_ArithmeticInChain_Throws()
    {
        var expr = NewExpr();
        var ex = Assert.Throws<ArgumentException>(() =>
            expr.ForPath(d => d.Child!.Count + 1, opt => opt.MapFrom(s => 7)));
        Assert.Contains("chain of property accesses", ex.Message);
    }

    [Fact]
    public void ForPath_LastCallWins_OnSamePath()
    {
        var expr = NewExpr();
        expr.ForPath(d => d.Child!.Name, opt => opt.MapFrom(s => "first"));
        expr.ForPath(d => d.Child!.Name, opt => opt.MapFrom(s => "second"));

        var matches = expr.TypeMap.PropertyMaps.Where(p => p.Name == "Child.Name").ToList();
        Assert.Single(matches);
        // Probe the second binding by inspecting the lambda's body constant (kept simple here).
        Assert.NotNull(matches[0].CustomExpression);
    }
}
```

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~MappingExpressionForPathTests" --nologo`

Expected: 6 failures — `ForPath` method does not exist on `IMappingExpression` / `MappingExpression`.

- [ ] **Step 3: Add `ForPath` to `IMappingExpression`**

Edit `src/Atlas/Configuration/IMappingExpression.cs` — add the new method declaration after `ForCtorParam` (around line 18):

```csharp
/// <summary>
/// Configures a binding for a nested destination path (e.g., <c>d => d.Customer.Name</c>).
/// At runtime, intermediates are auto-instantiated via their public parameterless
/// constructor (<c>dst.Customer ??= new Customer(); dst.Customer.Name = ...;</c>).
/// </summary>
/// <remarks>
/// Available on both forward and reverse maps with identical semantics. A single-level
/// path (<c>d => d.Foo</c>) is equivalent to <see cref="ForMember"/> — both methods
/// remove any prior binding for the same target and replace it (last-call-wins).
///
/// Every intermediate type in the path must have a public parameterless constructor
/// AND every intermediate property must have a public setter; otherwise
/// <see cref="MapperConfiguration.AssertConfigurationIsValid"/> throws naming the
/// offending type and path.
///
/// The leaf (last property in the chain) must be a writable property; this matches the
/// existing <c>ForMember</c> requirement.
/// </remarks>
/// <exception cref="ArgumentException">
/// Thrown at configuration time if <paramref name="destinationPath"/> is not a chain
/// of property accesses — e.g., it contains method calls, indexers, arithmetic, or any
/// non-<see cref="System.Linq.Expressions.MemberExpression"/> node.
/// </exception>
IMappingExpression<TSource, TDestination> ForPath<TMember>(
    Expression<Func<TDestination, TMember>> destinationPath,
    Action<IMemberConfigurationExpression<TSource, TDestination, TMember>> memberOptions);
```

- [ ] **Step 4: Implement `ForPath` in `MappingExpression`**

Edit `src/Atlas/Configuration/MappingExpression.cs` — add the new method after `ForCtorParam` (around line 56), and add a new private static `ExtractPath` helper after `ExtractProperty` (around line 168):

```csharp
public IMappingExpression<TSource, TDestination> ForPath<TMember>(
    Expression<Func<TDestination, TMember>> destinationPath,
    Action<IMemberConfigurationExpression<TSource, TDestination, TMember>> memberOptions)
{
    TypeMap.EnsureMutable();
    var path = ExtractPath(destinationPath);

    // Single-level: behave exactly like ForMember (no DestinationPath set).
    if (path.Count == 1)
    {
        TypeMap.PropertyMaps.RemoveAll(p => p.Name == path[0].Name);

        var pmSingle = PropertyMap.ForProperty(path[0]);
        var memberSingle = new MemberConfigurationExpression<TSource, TDestination, TMember>();
        memberOptions(memberSingle);
        memberSingle.ApplyTo(pmSingle);
        pmSingle.IsExplicit = true;
        TypeMap.PropertyMaps.Add(pmSingle);
        return this;
    }

    // Multi-level: store full path; Name = "A.B.C".
    var dottedName = string.Join('.', path.Select(p => p.Name));
    TypeMap.PropertyMaps.RemoveAll(p => p.Name == dottedName);

    var pm = PropertyMap.ForPath(path);
    var member = new MemberConfigurationExpression<TSource, TDestination, TMember>();
    memberOptions(member);
    member.ApplyTo(pm);
    pm.IsExplicit = true;
    TypeMap.PropertyMaps.Add(pm);
    return this;
}

// Add this helper alongside ExtractProperty:
private static IReadOnlyList<PropertyInfo> ExtractPath<TMember>(Expression<Func<TDestination, TMember>> selector)
{
    var body = selector.Body;
    if (body is UnaryExpression { NodeType: ExpressionType.Convert, Operand: var operand })
        body = operand;

    var stack = new Stack<PropertyInfo>();
    var current = body;
    while (current is MemberExpression me)
    {
        if (me.Member is not PropertyInfo prop)
            throw new ArgumentException(
                "Destination selector must be a chain of property accesses (e.g., d => d.Outer.Inner.Property).",
                nameof(selector));
        stack.Push(prop);
        current = me.Expression!;
    }

    if (current is not ParameterExpression)
        throw new ArgumentException(
            "Destination selector must be a chain of property accesses (e.g., d => d.Outer.Inner.Property).",
            nameof(selector));

    if (stack.Count == 0)
        throw new ArgumentException(
            "Destination selector must reference at least one property.",
            nameof(selector));

    return stack.ToArray();
}
```

- [ ] **Step 5: Run tests to verify pass**

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~MappingExpressionForPathTests" --nologo`

Expected: 6/6 pass.

- [ ] **Step 6: Run full test suite**

Run: `dotnet test --nologo`

Expected: 279 tests pass (273 baseline + 3 from Task 2 + 6 here).

- [ ] **Step 7: Commit**

```powershell
git add src/Atlas/Configuration/IMappingExpression.cs src/Atlas/Configuration/MappingExpression.cs tests/Atlas.Tests/MappingExpressionForPathTests.cs
git commit -m "Add ForPath fluent surface (6 tests)"
```

---

## Task 4: Compile nested writes (`BuildNestedAssign` in `ExecutionPlanBuilder`)

**Files:**
- Modify: `src/Atlas/Internal/ExecutionPlanBuilder.cs`
- Create: `tests/Atlas.Tests/ExecutionPlanBuilderNestedAssignTests.cs`

Route property assignments through a new `BuildNestedAssign` helper when `pm.DestinationPath` is set with more than one element. Single-level paths continue to use the existing direct assign. Spec references: §7.

- [ ] **Step 1: Write failing tests**

Create `tests/Atlas.Tests/ExecutionPlanBuilderNestedAssignTests.cs`:

```csharp
using Atlas.Configuration;
using Atlas.Internal;

namespace Atlas.Tests;

public class ExecutionPlanBuilderNestedAssignTests
{
    public sealed class Src { public string? Value { get; set; } public int Count { get; set; } }
    public sealed class Inner { public string? Name { get; set; } public int Tally { get; set; } }
    public sealed class Mid { public Inner? Deep { get; set; } }
    public sealed class Outer { public Inner? Child { get; set; } public Mid? Middle { get; set; } public string? Top { get; set; } }

    private static MapperConfiguration BuildConfig(Action<IMappingExpression<Src, Outer>> configure) =>
        new(cfg =>
        {
            var expr = cfg.CreateMap<Src, Outer>(MemberList.None);
            configure(expr);
        });

    [Fact]
    public void NestedAssign_SingleLevel_NoCoalesceEmitted()
    {
        // Single-level path uses ForProperty, no DestinationPath, no coalesce.
        var cfg = BuildConfig(expr => expr.ForPath(d => d.Top, opt => opt.MapFrom(s => s.Value)));
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<Outer>(new Src { Value = "hi" });

        Assert.Equal("hi", dst.Top);
        Assert.Null(dst.Child);
    }

    [Fact]
    public void NestedAssign_TwoLevel_EmitsCoalesceThenAssign()
    {
        var cfg = BuildConfig(expr => expr.ForPath(d => d.Child!.Name, opt => opt.MapFrom(s => s.Value)));
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<Outer>(new Src { Value = "alice" });

        Assert.NotNull(dst.Child);
        Assert.Equal("alice", dst.Child!.Name);
    }

    [Fact]
    public void NestedAssign_ThreeLevel_EmitsTwoCoalescesThenAssign()
    {
        var cfg = BuildConfig(expr => expr.ForPath(d => d.Middle!.Deep!.Tally, opt => opt.MapFrom(s => s.Count)));
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<Outer>(new Src { Count = 7 });

        Assert.NotNull(dst.Middle);
        Assert.NotNull(dst.Middle!.Deep);
        Assert.Equal(7, dst.Middle.Deep!.Tally);
    }

    [Fact]
    public void NestedAssign_TwoBindingsSharingPrefix_BothPopulate()
    {
        // Probes the design's "second `??=` is a no-op" claim — both Name and Tally end up set.
        var cfg = BuildConfig(expr =>
        {
            expr.ForPath(d => d.Child!.Name, opt => opt.MapFrom(s => s.Value));
            expr.ForPath(d => d.Child!.Tally, opt => opt.MapFrom(s => s.Count));
        });
        var mapper = cfg.CreateMapper();

        var dst = mapper.Map<Outer>(new Src { Value = "bob", Count = 42 });

        Assert.NotNull(dst.Child);
        Assert.Equal("bob", dst.Child!.Name);
        Assert.Equal(42, dst.Child.Tally);
    }
}
```

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~ExecutionPlanBuilderNestedAssignTests" --nologo`

Expected: 3-4 failures. The single-level test (`NestedAssign_SingleLevel_NoCoalesceEmitted`) may already pass because Task 3's single-level `ForPath` falls through to `ForProperty` semantics. The multi-level tests fail because `ExecutionPlanBuilder` does not yet emit nested writes.

- [ ] **Step 3: Add `BuildNestedAssign` to `ExecutionPlanBuilder`**

Edit `src/Atlas/Internal/ExecutionPlanBuilder.cs`:

(a) Add a new private helper at the end of the class (before the closing brace):

```csharp
private static Expression BuildNestedAssign(
    Expression destRoot,                       // dst (parameter or local var)
    IReadOnlyList<PropertyInfo> destPath,      // [Customer, Address, City]
    Expression valueExpr)                      // src.CustomerCity (already built)
{
    var statements = new List<Expression>();
    Expression accessSoFar = destRoot;

    // Walk all intermediates (destPath[0..^2]) emitting a coalesce-and-assign per step.
    for (int i = 0; i < destPath.Count - 1; i++)
    {
        var intermediateProp = destPath[i];
        accessSoFar = Expression.Property(accessSoFar, intermediateProp);

        // emit: accessSoFar = accessSoFar ?? new IntermediateType();
        // Validator should have verified parameterless ctor exists; emit a clear runtime
        // error here as a safety net for users who skip AssertConfigurationIsValid.
        var ctor = intermediateProp.PropertyType.GetConstructor(Type.EmptyTypes)
            ?? throw new InvalidOperationException(
                $"Cannot unflatten path through {intermediateProp.DeclaringType?.Name}.{intermediateProp.Name}: " +
                $"intermediate type {intermediateProp.PropertyType.FullName} has no public parameterless constructor. " +
                "Call AssertConfigurationIsValid() at startup to catch this at config time.");
        var coalesce = Expression.Coalesce(accessSoFar, Expression.New(ctor));
        statements.Add(Expression.Assign(accessSoFar, coalesce));
    }

    // Final step: leaf assign.
    var leafAccess = Expression.Property(accessSoFar, destPath[^1]);
    statements.Add(Expression.Assign(leafAccess, valueExpr));

    return Expression.Block(statements);
}
```

(b) Modify the `BuildPocoLambda` method's per-PropertyMap loop (around line 208-219). Replace:

```csharp
        foreach (var pm in propertyMaps)
        {
            if (pm.Ignored) continue;
            if (pm.DestinationProperty is null) continue;

            var sourceExpr = BuildSourceExpression(pm, srcParam, registry, pm.DestinationProperty.PropertyType);
            if (sourceExpr is null) continue;

            statements.Add(Expression.Assign(
                Expression.Property(destVar, pm.DestinationProperty),
                sourceExpr));
        }
```

with:

```csharp
        foreach (var pm in propertyMaps)
        {
            if (pm.Ignored) continue;
            if (pm.DestinationProperty is null) continue;

            var sourceExpr = BuildSourceExpression(pm, srcParam, registry, pm.DestinationProperty.PropertyType);
            if (sourceExpr is null) continue;

            if (pm.DestinationPath is { } path && path.Count > 1)
            {
                statements.Add(BuildNestedAssign(destVar, path, sourceExpr));
            }
            else
            {
                statements.Add(Expression.Assign(
                    Expression.Property(destVar, pm.DestinationProperty),
                    sourceExpr));
            }
        }
```

(c) Apply the same change to `BuildUpdate` (around line 143-154). Replace:

```csharp
        foreach (var pm in typeMap.PropertyMaps)
        {
            if (pm.Ignored) continue;
            if (pm.DestinationProperty is null) continue;     // ctor params skipped on update

            var sourceExpr = BuildSourceExpression(pm, srcParam, registry, pm.DestinationProperty.PropertyType);
            if (sourceExpr is null) continue;

            statements.Add(Expression.Assign(
                Expression.Property(destParam, pm.DestinationProperty),
                sourceExpr));
        }
```

with:

```csharp
        foreach (var pm in typeMap.PropertyMaps)
        {
            if (pm.Ignored) continue;
            if (pm.DestinationProperty is null) continue;     // ctor params skipped on update

            var sourceExpr = BuildSourceExpression(pm, srcParam, registry, pm.DestinationProperty.PropertyType);
            if (sourceExpr is null) continue;

            if (pm.DestinationPath is { } path && path.Count > 1)
            {
                statements.Add(BuildNestedAssign(destParam, path, sourceExpr));
            }
            else
            {
                statements.Add(Expression.Assign(
                    Expression.Property(destParam, pm.DestinationProperty),
                    sourceExpr));
            }
        }
```

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~ExecutionPlanBuilderNestedAssignTests" --nologo`

Expected: 4/4 pass.

- [ ] **Step 5: Run full test suite**

Run: `dotnet test --nologo`

Expected: 283 tests pass (273 baseline + 3 + 6 + 4 = 286 actually; recompute: 273 + 3 (task 2) + 6 (task 3) + 4 (task 4) = 286).

- [ ] **Step 6: Commit**

```powershell
git add src/Atlas/Internal/ExecutionPlanBuilder.cs tests/Atlas.Tests/ExecutionPlanBuilderNestedAssignTests.cs
git commit -m "Add ExecutionPlanBuilder.BuildNestedAssign + DestinationPath route (4 tests)"
```

---

## Task 5: Validator path guards (parameterless ctor + setter checks; DestinationPath top-level coverage)

**Files:**
- Modify: `src/Atlas/Internal/ConfigurationValidator.cs`
- Create: `tests/Atlas.Tests/ConfigurationValidatorPathTests.cs`

Add three always-on validation rules and adjust the `MemberList.Destination` coverage walk for nested-path bindings. Spec references: §6 risks 4, 6, 7, 8; §8.4.

- [ ] **Step 1: Write failing tests**

Create `tests/Atlas.Tests/ConfigurationValidatorPathTests.cs`:

```csharp
namespace Atlas.Tests;

public class ConfigurationValidatorPathTests
{
    public sealed class Src { public string? Value { get; set; } }
    public sealed class GoodInner { public string? Name { get; set; } }
    public sealed class GoodOuter { public GoodInner? Child { get; set; } public string? Other { get; set; } }
    public sealed class CtorlessInner { public string? Name { get; set; } public CtorlessInner(string n) { Name = n; } }   // no parameterless ctor
    public sealed class CtorlessOuter { public CtorlessInner? Child { get; set; } }
    public sealed class GetterOnlyOuter { public GoodInner? Child { get; } = new(); }   // intermediate has no setter
    public sealed class LeafReadOnlyInner { public string? Name { get; } }
    public sealed class LeafReadOnlyOuter { public LeafReadOnlyInner? Child { get; set; } = new(); }

    [Fact]
    public void Validate_IntermediateMissingParameterlessCtor_Throws_NamingPath()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<Src, CtorlessOuter>(MemberList.None)
                .ForPath(d => d.Child!.Name, opt => opt.MapFrom(s => s.Value)));

        var ex = Assert.Throws<AtlasConfigurationException>(() => cfg.AssertConfigurationIsValid());
        Assert.Contains("CtorlessInner", ex.Message);
        Assert.Contains("parameterless constructor", ex.Message);
    }

    [Fact]
    public void Validate_IntermediateMissingSetter_Throws_NamingProperty()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<Src, GetterOnlyOuter>(MemberList.None)
                .ForPath(d => d.Child!.Name, opt => opt.MapFrom(s => s.Value)));

        var ex = Assert.Throws<AtlasConfigurationException>(() => cfg.AssertConfigurationIsValid());
        Assert.Contains("Child", ex.Message);
        Assert.Contains("setter", ex.Message);
    }

    [Fact]
    public void Validate_LeafMissingSetter_Throws()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<Src, LeafReadOnlyOuter>(MemberList.None)
                .ForPath(d => d.Child!.Name, opt => opt.MapFrom(s => s.Value)));

        var ex = Assert.Throws<AtlasConfigurationException>(() => cfg.AssertConfigurationIsValid());
        Assert.Contains("Name", ex.Message);
        Assert.Contains("setter", ex.Message);
    }

    [Fact]
    public void Validate_AllValid_ReturnsCleanly()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<Src, GoodOuter>(MemberList.None)
                .ForPath(d => d.Child!.Name, opt => opt.MapFrom(s => s.Value)));

        cfg.AssertConfigurationIsValid();   // does not throw
    }

    [Fact]
    public void Validate_DestinationPathCountsAsCoveringTopIntermediate_ForMemberListDestination()
    {
        // Reverse-style scenario without using ReverseMap (which lands in Task 6).
        // GoodOuter has { Child, Other }. We map Child.Name (covers Child) and leave Other unmapped.
        // With MemberList.Destination, ONLY Other should be reported as unmapped — Child is "covered"
        // because path[0] == Child.
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<Src, GoodOuter>(MemberList.Destination)
                .ForPath(d => d.Child!.Name, opt => opt.MapFrom(s => s.Value)));

        var ex = Assert.Throws<AtlasConfigurationException>(() => cfg.AssertConfigurationIsValid());
        Assert.Contains("Other", ex.Message);
        Assert.DoesNotContain("Child", ex.Message);   // path[0] coverage suppresses the "Child unmapped" complaint
    }
}
```

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~ConfigurationValidatorPathTests" --nologo`

Expected: 4 failures. `Validate_AllValid_ReturnsCleanly` may pass (no path validation yet, so nothing to fail on). The two `_ReturnsCleanly`-adjacent tests fail because the validator doesn't yet check ctor/setter; the coverage test fails because the validator doesn't yet credit `path[0]` as covered.

- [ ] **Step 3: Add `ValidatePaths` and adjust `ValidateDestination`**

Edit `src/Atlas/Internal/ConfigurationValidator.cs`:

(a) Add the new validator method at the bottom of the class (before the closing brace):

```csharp
private static void ValidatePaths(TypeMap tm, List<ConfigurationError> errors)
{
    foreach (var pm in tm.PropertyMaps)
    {
        if (pm.DestinationPath is null || pm.DestinationPath.Count < 2) continue;
        var path = pm.DestinationPath;

        // Walk all intermediates (path[0..^2]):
        //  - Each intermediate property must have a setter (we will assign path[i] = path[i] ?? new T()).
        //  - Each intermediate's PropertyType must have a public parameterless ctor (Expression.New(ctor)).
        for (int i = 0; i < path.Count - 1; i++)
        {
            var intermediate = path[i];
            if (intermediate.SetMethod is not { IsPublic: true })
            {
                errors.Add(new ConfigurationError(
                    tm.SourceType, tm.DestinationType, pm.Name,
                    $"Cannot unflatten through {intermediate.DeclaringType?.Name}.{intermediate.Name}: property has no public setter."));
                continue;
            }

            var ctor = intermediate.PropertyType.GetConstructor(Type.EmptyTypes);
            if (ctor is null || !ctor.IsPublic)
            {
                errors.Add(new ConfigurationError(
                    tm.SourceType, tm.DestinationType, pm.Name,
                    $"Cannot unflatten path {pm.Name}: intermediate type {intermediate.PropertyType.Name} has no public parameterless constructor."));
            }
        }

        // Leaf must have a setter (mirrors existing ForMember invariant).
        var leaf = path[^1];
        if (leaf.SetMethod is not { IsPublic: true })
        {
            errors.Add(new ConfigurationError(
                tm.SourceType, tm.DestinationType, pm.Name,
                $"Cannot write to leaf {leaf.DeclaringType?.Name}.{leaf.Name}: property has no public setter."));
        }
    }
}
```

(b) Wire `ValidatePaths` into the always-on rule list at the top of `Validate`. Locate (around line 16-19):

```csharp
        foreach (var tm in registry.AllTypeMaps)
        {
            // Enum rules (always-on; covers per-value overrides, fallback, foot-gun guard).
            ValidateEnum(tm, errors);
```

Add the path-validation call right after the enum-validation call:

```csharp
        foreach (var tm in registry.AllTypeMaps)
        {
            // Enum rules (always-on; covers per-value overrides, fallback, foot-gun guard).
            ValidateEnum(tm, errors);

            // Path rules (always-on; covers ForPath / mirrored unflatten paths).
            ValidatePaths(tm, errors);
```

(c) Adjust `ValidateDestination` so a `DestinationPath` binding counts as covering its top intermediate. Locate the existing `ValidateDestination` method (around line 41-75) and modify the matching logic. Replace:

```csharp
    private static void ValidateDestination(TypeMap tm, MapperRegistry registry, List<ConfigurationError> errors)
    {
        foreach (var prop in GetWritableProperties(tm.DestinationType))
        {
            var pm = tm.PropertyMaps.FirstOrDefault(p =>
                string.Equals(p.Name, prop.Name, StringComparison.Ordinal));

            if (pm is null)
            {
                errors.Add(new ConfigurationError(
                    tm.SourceType, tm.DestinationType, prop.Name,
                    "No mapping configured for destination member."));
                continue;
            }

            if (!pm.IsResolved)
            {
                errors.Add(new ConfigurationError(
                    tm.SourceType, tm.DestinationType, prop.Name,
                    "Destination member is unmapped (no source path, constant, or Ignore)."));
                continue;
            }

            if (pm.SourcePath is not null)
            {
                var srcType = pm.SourcePath.Members[^1].PropertyType;
                if (!IsAssignmentLegal(srcType, prop.PropertyType, registry))
                {
                    errors.Add(new ConfigurationError(
                        tm.SourceType, tm.DestinationType, prop.Name,
                        $"No registered map or implicit conversion from {srcType.Name} to {prop.PropertyType.Name}."));
                }
            }
        }
    }
```

with:

```csharp
    private static void ValidateDestination(TypeMap tm, MapperRegistry registry, List<ConfigurationError> errors)
    {
        // A DestinationPath binding counts as covering its TOP intermediate (path[0])
        // for MemberList.Destination purposes — the user's intent with ForPath(d => d.Customer.Name)
        // is "I'm writing into Customer." Without this rule, every multi-level unflatten
        // would also produce a spurious "Customer unmapped" error.
        var coveredTopIntermediates = new HashSet<string>(
            tm.PropertyMaps
                .Where(p => p.DestinationPath is { Count: > 1 })
                .Select(p => p.DestinationPath![0].Name),
            StringComparer.Ordinal);

        foreach (var prop in GetWritableProperties(tm.DestinationType))
        {
            if (coveredTopIntermediates.Contains(prop.Name)) continue;

            var pm = tm.PropertyMaps.FirstOrDefault(p =>
                string.Equals(p.Name, prop.Name, StringComparison.Ordinal));

            if (pm is null)
            {
                errors.Add(new ConfigurationError(
                    tm.SourceType, tm.DestinationType, prop.Name,
                    "No mapping configured for destination member."));
                continue;
            }

            if (!pm.IsResolved)
            {
                errors.Add(new ConfigurationError(
                    tm.SourceType, tm.DestinationType, prop.Name,
                    "Destination member is unmapped (no source path, constant, or Ignore)."));
                continue;
            }

            if (pm.SourcePath is not null)
            {
                var srcType = pm.SourcePath.Members[^1].PropertyType;
                if (!IsAssignmentLegal(srcType, prop.PropertyType, registry))
                {
                    errors.Add(new ConfigurationError(
                        tm.SourceType, tm.DestinationType, prop.Name,
                        $"No registered map or implicit conversion from {srcType.Name} to {prop.PropertyType.Name}."));
                }
            }
        }
    }
```

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~ConfigurationValidatorPathTests" --nologo`

Expected: 5/5 pass.

- [ ] **Step 5: Run full test suite**

Run: `dotnet test --nologo`

Expected: 291 tests pass (286 + 5).

- [ ] **Step 6: Commit**

```powershell
git add src/Atlas/Internal/ConfigurationValidator.cs tests/Atlas.Tests/ConfigurationValidatorPathTests.cs
git commit -m "Add validator path guards (parameterless ctor + setter + top-level coverage) (5 tests)"
```

---

## Task 6: `ReverseMap` fluent surface (sink plumbing)

**Files:**
- Modify: `src/Atlas/Configuration/IMappingExpression.cs`
- Modify: `src/Atlas/Configuration/MappingExpression.cs`
- Modify: `src/Atlas/MapperProfile.cs`
- Modify: `src/Atlas/MapperConfigurationExpression.cs`
- Create: `tests/Atlas.Tests/MappingExpressionReverseMapTests.cs`

Add `ReverseMap(MemberList memberList = MemberList.None)` to the fluent surface. Plumb a sink (`Action<TypeMap>`) into `MappingExpression` so `.ReverseMap()` can register the new TypeMap with the parent collection. Spec references: §4.1, §6.5.

- [ ] **Step 1: Write failing tests**

Create `tests/Atlas.Tests/MappingExpressionReverseMapTests.cs`:

```csharp
using Atlas.Configuration;
using Atlas.Internal;

namespace Atlas.Tests;

public class MappingExpressionReverseMapTests
{
    public sealed class A { public string? Foo { get; set; } }
    public sealed class B { public string? Foo { get; set; } }

    private sealed class P : MapperProfile { }   // empty profile we can extend per-test

    [Fact]
    public void ReverseMap_ReturnsExpression_OfReverseGenericArgs()
    {
        var cfg = new MapperConfigurationExpression();
        var fwd = cfg.CreateMap<A, B>();
        var rev = fwd.ReverseMap();

        Assert.IsAssignableFrom<IMappingExpression<B, A>>(rev);
    }

    [Fact]
    public void ReverseMap_DefaultMemberListIsNone()
    {
        var cfg = new MapperConfigurationExpression();
        cfg.CreateMap<A, B>().ReverseMap();

        var revTm = cfg.GetTypeMaps().Single(t => t.SourceType == typeof(B));
        Assert.Equal(MemberList.None, revTm.MemberList);
    }

    [Fact]
    public void ReverseMap_ExplicitMemberListHonoured()
    {
        var cfg = new MapperConfigurationExpression();
        cfg.CreateMap<A, B>().ReverseMap(MemberList.Destination);

        var revTm = cfg.GetTypeMaps().Single(t => t.SourceType == typeof(B));
        Assert.Equal(MemberList.Destination, revTm.MemberList);
    }

    [Fact]
    public void ReverseMap_CalledTwice_ReturnsSameInstance()
    {
        var cfg = new MapperConfigurationExpression();
        var fwd = cfg.CreateMap<A, B>();

        var rev1 = fwd.ReverseMap();
        var rev2 = fwd.ReverseMap();

        Assert.Same(rev1, rev2);
    }

    [Fact]
    public void ReverseMap_TwoCallsWithDifferentMemberList_Throws()
    {
        var cfg = new MapperConfigurationExpression();
        var fwd = cfg.CreateMap<A, B>();
        fwd.ReverseMap();   // default None

        var ex = Assert.Throws<AtlasConfigurationException>(() => fwd.ReverseMap(MemberList.Destination));
        Assert.Contains("None", ex.Message);
        Assert.Contains("Destination", ex.Message);
    }

    [Fact]
    public void ReverseMap_RegistersTypeMap_AndChainsForMember()
    {
        var cfg = new MapperConfigurationExpression();
        cfg.CreateMap<A, B>()
           .ReverseMap()
           .ForMember(d => d.Foo, opt => opt.Ignore());

        var revTm = cfg.GetTypeMaps().Single(t => t.SourceType == typeof(B));
        var pm = revTm.PropertyMaps.Single(p => p.Name == "Foo");
        Assert.True(pm.Ignored);
        Assert.True(pm.IsExplicit);
    }
}
```

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~MappingExpressionReverseMapTests" --nologo`

Expected: 6 failures — `ReverseMap` method does not exist.

- [ ] **Step 3: Add `ReverseMap` to `IMappingExpression`**

Edit `src/Atlas/Configuration/IMappingExpression.cs` — add the new method declaration after `ForPath` (added in Task 3) and before `ConvertUsing`:

```csharp
/// <summary>
/// Registers the inverse (TDestination, TSource) map and returns its fluent surface.
/// Conventions and source-side flattening are auto-inverted (so that
/// CustomerName ↔ Customer.Name) via the ReverseMapMirror build phase.
/// </summary>
/// <remarks>
/// What IS auto-inverted on the reverse direction:
/// <list type="bullet">
///   <item>Conventions (PascalCase name match, naming-style toggle).</item>
///   <item>Source-side flattening: forward <c>SourcePath = [Customer, Name]</c> writing
///         <c>dst.CustomerName</c> becomes a reverse binding writing <c>dst.Customer.Name</c>
///         from <c>src.CustomerName</c>.</item>
/// </list>
///
/// What IS NOT auto-inverted (reconfigure on the returned reverse expression if needed):
/// <list type="bullet">
///   <item><c>ForMember(MapFrom(expression))</c> — the forward expression is not inverted.</item>
///   <item><c>Ignore()</c> — does not propagate to the reverse direction.</item>
///   <item><c>ConvertUsing</c> — custom converters generally are not invertible.</item>
///   <item><c>Include</c>/<c>IncludeBase</c> — inheritance chains are not reversed.</item>
///   <item>Enum per-value overrides — the reverse pair gets default ByValue strategy with no overrides.</item>
///   <item>Constructor parameter mappings (<c>ForCtorParam</c>).</item>
/// </list>
///
/// The reverse map defaults to <see cref="MemberList.None"/>. Pass a different
/// <paramref name="memberList"/> for stricter validation.
///
/// Calling <c>ReverseMap()</c> twice on the same forward map returns the same reverse
/// expression instance (idempotent). The <paramref name="memberList"/> from the FIRST call
/// is locked; calling <c>ReverseMap(MemberList.X)</c> a second time with a different value
/// throws <see cref="AtlasConfigurationException"/>.
/// </remarks>
/// <exception cref="AtlasConfigurationException">
/// Thrown at configuration time if a TypeMap for <c>(TDestination, TSource)</c> is also
/// registered elsewhere via <see cref="MapperProfile.CreateMap"/> (or via another forward
/// map's <c>.ReverseMap()</c>); or if a second <c>.ReverseMap()</c> call passes a different
/// <paramref name="memberList"/> than the first.
/// </exception>
IMappingExpression<TDestination, TSource> ReverseMap(MemberList memberList = MemberList.None);
```

- [ ] **Step 4: Plumb sink + implement `ReverseMap` in `MappingExpression`**

Edit `src/Atlas/Configuration/MappingExpression.cs`:

(a) Add a `_sink` field and update the constructor:

Replace:

```csharp
    public TypeMap TypeMap { get; }

    public MappingExpression(TypeMap typeMap)
    {
        TypeMap = typeMap;
    }
```

with:

```csharp
    public TypeMap TypeMap { get; }
    private readonly Action<TypeMap>? _sink;

    public MappingExpression(TypeMap typeMap, Action<TypeMap>? sink = null)
    {
        TypeMap = typeMap;
        _sink = sink;
    }
```

(b) Add the `ReverseMap` method (place after the existing `Include`/`IncludeBase` methods, before the enum-surface section):

```csharp
    public IMappingExpression<TDestination, TSource> ReverseMap(MemberList memberList = MemberList.None)
    {
        TypeMap.EnsureMutable();

        if (TypeMap.CachedReverseExpression is MappingExpression<TDestination, TSource> existing)
        {
            var existingMemberList = existing.TypeMap.MemberList;
            if (existingMemberList != memberList)
                throw new AtlasConfigurationException(
                    $"ReverseMap on ({typeof(TSource).Name}, {typeof(TDestination).Name}) was previously " +
                    $"called with MemberList.{existingMemberList}; cannot now call with MemberList.{memberList}.");
            return existing;
        }

        if (_sink is null)
            throw new InvalidOperationException(
                "ReverseMap can only be called on a MappingExpression created via MapperProfile.CreateMap " +
                "or MapperConfigurationExpression.CreateMap (which provide a sink for the reverse TypeMap).");

        var reverseTm = new TypeMap(typeof(TDestination), typeof(TSource), memberList)
        {
            ReverseMapPair = TypeMap.Pair,
            RegistrationOrigin = $"CreateMap<{typeof(TSource).Name}, {typeof(TDestination).Name}>().ReverseMap()",
        };
        _sink(reverseTm);

        var reverseExpr = new MappingExpression<TDestination, TSource>(reverseTm, _sink);
        TypeMap.CachedReverseExpression = reverseExpr;
        return reverseExpr;
    }
```

- [ ] **Step 5: Update `MapperProfile.CreateMap` to set RegistrationOrigin and pass the sink**

Edit `src/Atlas/MapperProfile.cs` — replace the `CreateMap` method body:

```csharp
    protected IMappingExpression<TSource, TDestination> CreateMap<TSource, TDestination>(
        MemberList memberList = MemberList.Destination)
    {
        var map = new TypeMap(typeof(TSource), typeof(TDestination), memberList)
        {
            RegistrationOrigin = $"CreateMap<{typeof(TSource).Name}, {typeof(TDestination).Name}>()"
        };
        _typeMaps.Add(map);
        return new Atlas.Configuration.MappingExpression<TSource, TDestination>(map, _typeMaps.Add);
    }
```

(The `using Atlas.Configuration;` should already be at the top of the file. Use the fully-qualified name only if there's a conflict.)

- [ ] **Step 6: Update `MapperConfigurationExpression.CreateMap` similarly (without conflict guard yet — that's Task 7)**

Edit `src/Atlas/MapperConfigurationExpression.cs` — replace the `CreateMap` method body:

```csharp
    public IMappingExpression<TSource, TDestination> CreateMap<TSource, TDestination>(
        MemberList memberList = MemberList.Destination)
    {
        EnsureMutable();
        var map = new TypeMap(typeof(TSource), typeof(TDestination), memberList)
        {
            RegistrationOrigin = $"CreateMap<{typeof(TSource).Name}, {typeof(TDestination).Name}>()"
        };
        _typeMaps[map.Pair] = map; // last call wins (Task 7 will route through RegisterTypeMap with the conflict guard)
        return new MappingExpression<TSource, TDestination>(map, tm => _typeMaps[tm.Pair] = tm);
    }
```

- [ ] **Step 7: Run tests to verify pass**

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~MappingExpressionReverseMapTests" --nologo`

Expected: 6/6 pass.

- [ ] **Step 8: Run full test suite**

Run: `dotnet test --nologo`

Expected: 297 tests pass (291 + 6).

- [ ] **Step 9: Commit**

```powershell
git add src/Atlas/Configuration/IMappingExpression.cs src/Atlas/Configuration/MappingExpression.cs src/Atlas/MapperProfile.cs src/Atlas/MapperConfigurationExpression.cs tests/Atlas.Tests/MappingExpressionReverseMapTests.cs
git commit -m "Add ReverseMap fluent surface with sink plumbing (6 tests)"
```

---

## Task 7: Conflict guard at `MapperConfigurationExpression.RegisterTypeMap`

**Files:**
- Modify: `src/Atlas/MapperConfigurationExpression.cs`
- Create: `tests/Atlas.Tests/ReverseMapConflictTests.cs`

Replace the inline `_typeMaps[pair] = map` calls with a single `RegisterTypeMap` helper that performs the conflict check. The check fires when at least one side of a duplicate pair has `ReverseMapPair != null`. Spec references: §5.5, §6.4.

- [ ] **Step 1: Write failing tests**

Create `tests/Atlas.Tests/ReverseMapConflictTests.cs`:

```csharp
namespace Atlas.Tests;

public class ReverseMapConflictTests
{
    public sealed class S { public string? Foo { get; set; } }
    public sealed class D { public string? Foo { get; set; } }

    private sealed class ProfileA : MapperProfile
    {
        public ProfileA() { CreateMap<D, S>(); }
    }

    private sealed class ProfileB : MapperProfile
    {
        public ProfileB() { CreateMap<S, D>().ReverseMap(); }
    }

    private sealed class ProfileSingleConflict : MapperProfile
    {
        public ProfileSingleConflict()
        {
            CreateMap<S, D>().ReverseMap();
            CreateMap<D, S>();    // conflict — registered twice in this profile
        }
    }

    [Fact]
    public void CreateDestSrc_ThenReverseMapOnSrcDest_Throws_NamingBothSites()
    {
        var ex = Assert.Throws<AtlasConfigurationException>(() => new MapperConfiguration(c =>
        {
            c.AddProfile(new ProfileA());     // (D, S) registered, ReverseMapPair = null
            c.AddProfile(new ProfileB());     // (S, D) registered, then (D, S).ReverseMapPair = (S, D)
        }));

        Assert.Contains("CreateMap<D, S>()", ex.Message);
        Assert.Contains("CreateMap<S, D>().ReverseMap()", ex.Message);
    }

    [Fact]
    public void ReverseMapOnSrcDest_ThenCreateDestSrc_Throws_NamingBothSites()
    {
        var ex = Assert.Throws<AtlasConfigurationException>(() => new MapperConfiguration(c =>
        {
            c.AddProfile(new ProfileB());     // (S, D) registered, then (D, S) reverse
            c.AddProfile(new ProfileA());     // (D, S) registered — collides with the reverse
        }));

        Assert.Contains("CreateMap<D, S>()", ex.Message);
        Assert.Contains("CreateMap<S, D>().ReverseMap()", ex.Message);
    }

    [Fact]
    public void ReverseMapTwiceOnSameMap_DoesNotThrow()
    {
        // Idempotency check via the public surface — calling ReverseMap twice with the same MemberList
        // returns the same expression and does NOT register twice (so no conflict).
        var cfg = new MapperConfiguration(c =>
        {
            var fwd = c.CreateMap<S, D>();
            fwd.ReverseMap();
            fwd.ReverseMap();   // returns the same instance; no second register
        });

        // Sanity: there are exactly two TypeMaps registered (forward + one reverse).
        Assert.Equal(2, cfg.Internal_Registry.AllTypeMaps.Count);
    }

    [Fact]
    public void TwoProfilesEachReversingTheSamePair_Throws()
    {
        var ex = Assert.Throws<AtlasConfigurationException>(() => new MapperConfiguration(c =>
        {
            c.AddProfile(new ProfileB());     // (S, D) + (D, S) reverse
            c.AddProfile(new ProfileB());     // again — second (D, S) reverse collides
        }));

        Assert.Contains("CreateMap<S, D>().ReverseMap()", ex.Message);
    }

    [Fact]
    public void SingleProfile_DuplicatePair_DetectedAtHarvest()
    {
        var ex = Assert.Throws<AtlasConfigurationException>(() => new MapperConfiguration(c =>
            c.AddProfile(new ProfileSingleConflict())));

        Assert.Contains("CreateMap<D, S>()", ex.Message);
        Assert.Contains("CreateMap<S, D>().ReverseMap()", ex.Message);
    }
}
```

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~ReverseMapConflictTests" --nologo`

Expected: 4-5 failures. The third test (`ReverseMapTwiceOnSameMap_DoesNotThrow`) likely passes already because Task 6's idempotency caches return the same expression. The other tests fail because the conflict guard does not exist.

(Note: `cfg.Internal_Registry.AllTypeMaps.Count` is exposed via the existing `internal MapperRegistry Internal_Registry => _registry;` accessor in `MapperConfiguration.cs:87`.)

- [ ] **Step 3: Replace inline registrations with `RegisterTypeMap`**

Edit `src/Atlas/MapperConfigurationExpression.cs`:

(a) Add the new private helper method (place above `EnsureMutable`):

```csharp
    private void RegisterTypeMap(TypeMap newTm)
    {
        if (_typeMaps.TryGetValue(newTm.Pair, out var existing))
        {
            var existingIsReverse = existing.ReverseMapPair is not null;
            var newIsReverse = newTm.ReverseMapPair is not null;
            if (existingIsReverse || newIsReverse)
            {
                throw new AtlasConfigurationException(
                    $"Type pair ({newTm.SourceType.Name}, {newTm.DestinationType.Name}) is registered twice: " +
                    $"{existing.RegistrationOrigin} and {newTm.RegistrationOrigin}. " +
                    $"Pick one — either remove the duplicate, or rely solely on .ReverseMap() to produce the inverse.");
            }
            // Otherwise: preserve v1 last-write-wins behavior (silent overwrite).
        }
        _typeMaps[newTm.Pair] = newTm;
    }
```

(b) Replace the body of `CreateMap` (last line was `_typeMaps[map.Pair] = map; ... new MappingExpression<...>(map, tm => _typeMaps[tm.Pair] = tm);`):

```csharp
    public IMappingExpression<TSource, TDestination> CreateMap<TSource, TDestination>(
        MemberList memberList = MemberList.Destination)
    {
        EnsureMutable();
        var map = new TypeMap(typeof(TSource), typeof(TDestination), memberList)
        {
            RegistrationOrigin = $"CreateMap<{typeof(TSource).Name}, {typeof(TDestination).Name}>()"
        };
        RegisterTypeMap(map);
        return new MappingExpression<TSource, TDestination>(map, RegisterTypeMap);
    }
```

(c) Replace `AddProfile`:

```csharp
    public void AddProfile(MapperProfile profile)
    {
        EnsureMutable();
        foreach (var map in profile.GetTypeMaps())
        {
            RegisterTypeMap(map);
        }
    }
```

(d) Replace `AddMaps(params Assembly[])`:

```csharp
    public void AddMaps(params Assembly[] assemblies)
    {
        EnsureMutable();
        foreach (var profile in ProfileScanner.Discover(assemblies))
        {
            foreach (var map in profile.GetTypeMaps())
            {
                RegisterTypeMap(map);
            }
        }
    }
```

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~ReverseMapConflictTests" --nologo`

Expected: 5/5 pass.

- [ ] **Step 5: Run full test suite**

Run: `dotnet test --nologo`

Expected: 302 tests pass (297 + 5).

- [ ] **Step 6: Commit**

```powershell
git add src/Atlas/MapperConfigurationExpression.cs tests/Atlas.Tests/ReverseMapConflictTests.cs
git commit -m "Add conflict guard at MapperConfigurationExpression.RegisterTypeMap (5 tests)"
```

---

## Task 8: `ReverseMapMirror` — mirror algorithm

**Files:**
- Create: `src/Atlas/Internal/ReverseMapMirror.cs`
- Create: `tests/Atlas.Tests/Internal/ReverseMapMirrorTests.cs`

Implement the mirror algorithm. For each TypeMap with `ReverseMapPair != null`, fill remaining unmapped reverse bindings from the forward map's resolved PropertyMaps with directions flipped. Two skip rules: exact-name binding already exists, OR top-intermediate has a user-explicit binding. Spec references: §6.

- [ ] **Step 1: Write failing tests**

Create `tests/Atlas.Tests/Internal/ReverseMapMirrorTests.cs`:

```csharp
using System.Reflection;
using Atlas.Internal;

namespace Atlas.Tests.Internal;

public class ReverseMapMirrorTests
{
    public sealed class Customer { public string? Name { get; set; } public string? Email { get; set; } }
    public sealed class Address { public string? City { get; set; } }
    public sealed class CustomerWithAddress { public Address? HomeAddress { get; set; } }
    public sealed class Order { public int Id { get; set; } public Customer? Customer { get; set; } public decimal Total { get; set; } }
    public sealed class OrderDeep { public CustomerWithAddress? Customer { get; set; } }
    public sealed class OrderDto { public string? CustomerName { get; set; } public string? CustomerEmail { get; set; } public decimal Total { get; set; } public int Id { get; set; } }
    public sealed class OrderDeepDto { public string? CustomerHomeAddressCity { get; set; } }

    private static (TypeMap fwd, TypeMap rev) BuildPair(Type srcType, Type dstType)
    {
        var fwd = new TypeMap(srcType, dstType, MemberList.None);
        var rev = new TypeMap(dstType, srcType, MemberList.None) { ReverseMapPair = fwd.Pair };
        return (fwd, rev);
    }

    private static PropertyInfo P(Type t, string name) => t.GetProperty(name)!;

    [Fact]
    public void Mirror_SingleLevelConvention_FlipsToSingleLevelOnReverse()
    {
        // Forward: Order.Total → OrderDto.Total (single-level convention match).
        var (fwd, rev) = BuildPair(typeof(Order), typeof(OrderDto));
        var fwdPm = PropertyMap.ForProperty(P(typeof(OrderDto), "Total"));
        fwdPm.SourcePath = new SourceMemberPath(new[] { P(typeof(Order), "Total") });
        fwd.PropertyMaps.Add(fwdPm);

        ReverseMapMirror.Mirror(new[] { fwd, rev });

        var revPm = rev.PropertyMaps.Single(p => p.Name == "Total");
        Assert.Null(revPm.DestinationPath);
        Assert.Equal(P(typeof(Order), "Total"), revPm.DestinationProperty);
        Assert.NotNull(revPm.SourcePath);
        Assert.Equal("Total", revPm.SourcePath!.Members.Single().Name);
    }

    [Fact]
    public void Mirror_TwoLevelChain_FlipsToUnflattenPath()
    {
        // Forward: Order.Customer.Name → OrderDto.CustomerName (flattening).
        var (fwd, rev) = BuildPair(typeof(Order), typeof(OrderDto));
        var fwdPm = PropertyMap.ForProperty(P(typeof(OrderDto), "CustomerName"));
        fwdPm.SourcePath = new SourceMemberPath(new[] { P(typeof(Order), "Customer"), P(typeof(Customer), "Name") });
        fwd.PropertyMaps.Add(fwdPm);

        ReverseMapMirror.Mirror(new[] { fwd, rev });

        var revPm = rev.PropertyMaps.Single(p => p.Name == "Customer.Name");
        Assert.NotNull(revPm.DestinationPath);
        Assert.Collection(revPm.DestinationPath!,
            p => Assert.Equal("Customer", p.Name),
            p => Assert.Equal("Name", p.Name));
        Assert.NotNull(revPm.SourcePath);
        Assert.Equal("CustomerName", revPm.SourcePath!.Members.Single().Name);
    }

    [Fact]
    public void Mirror_ThreeLevelChain_FlipsToThreeLevelUnflattenPath()
    {
        var (fwd, rev) = BuildPair(typeof(OrderDeep), typeof(OrderDeepDto));
        var fwdPm = PropertyMap.ForProperty(P(typeof(OrderDeepDto), "CustomerHomeAddressCity"));
        fwdPm.SourcePath = new SourceMemberPath(new[]
        {
            P(typeof(OrderDeep), "Customer"),
            P(typeof(CustomerWithAddress), "HomeAddress"),
            P(typeof(Address), "City"),
        });
        fwd.PropertyMaps.Add(fwdPm);

        ReverseMapMirror.Mirror(new[] { fwd, rev });

        var revPm = rev.PropertyMaps.Single(p => p.Name == "Customer.HomeAddress.City");
        Assert.NotNull(revPm.DestinationPath);
        Assert.Equal(3, revPm.DestinationPath!.Count);
    }

    [Fact]
    public void Mirror_ReverseExplicitBinding_NotOverwritten()
    {
        // Skip-rule-1 — exact name match.
        var (fwd, rev) = BuildPair(typeof(Order), typeof(OrderDto));
        var fwdPm = PropertyMap.ForProperty(P(typeof(OrderDto), "Total"));
        fwdPm.SourcePath = new SourceMemberPath(new[] { P(typeof(Order), "Total") });
        fwd.PropertyMaps.Add(fwdPm);

        // Pre-existing reverse binding for "Total" (e.g., user .ForMember(d => d.Total, opt => opt.Ignore())).
        var explicitRev = PropertyMap.ForProperty(P(typeof(Order), "Total"));
        explicitRev.Ignored = true;
        explicitRev.IsExplicit = true;
        rev.PropertyMaps.Add(explicitRev);

        ReverseMapMirror.Mirror(new[] { fwd, rev });

        Assert.Single(rev.PropertyMaps, p => p.Name == "Total");
        Assert.True(rev.PropertyMaps.Single(p => p.Name == "Total").Ignored);
    }

    [Fact]
    public void Mirror_UserExplicitTopLevelBinding_SuppressesMultiLevelMirror()
    {
        // Skip-rule-2 — user mapped Customer wholesale on the reverse.
        var (fwd, rev) = BuildPair(typeof(Order), typeof(OrderDto));
        var fwdPmName = PropertyMap.ForProperty(P(typeof(OrderDto), "CustomerName"));
        fwdPmName.SourcePath = new SourceMemberPath(new[] { P(typeof(Order), "Customer"), P(typeof(Customer), "Name") });
        fwd.PropertyMaps.Add(fwdPmName);
        var fwdPmEmail = PropertyMap.ForProperty(P(typeof(OrderDto), "CustomerEmail"));
        fwdPmEmail.SourcePath = new SourceMemberPath(new[] { P(typeof(Order), "Customer"), P(typeof(Customer), "Email") });
        fwd.PropertyMaps.Add(fwdPmEmail);

        // Pre-existing reverse binding for "Customer" with IsExplicit = true.
        var explicitRev = PropertyMap.ForProperty(P(typeof(Order), "Customer"));
        explicitRev.HasConstant = true;
        explicitRev.ConstantValue = new Customer { Name = "preset" };
        explicitRev.IsExplicit = true;
        rev.PropertyMaps.Add(explicitRev);

        ReverseMapMirror.Mirror(new[] { fwd, rev });

        // Mirror should NOT add Customer.Name or Customer.Email — they would overwrite the user's preset.
        Assert.DoesNotContain(rev.PropertyMaps, p => p.Name == "Customer.Name");
        Assert.DoesNotContain(rev.PropertyMaps, p => p.Name == "Customer.Email");
    }

    [Fact]
    public void Mirror_ForwardIgnored_NotMirrored()
    {
        var (fwd, rev) = BuildPair(typeof(Order), typeof(OrderDto));
        var fwdPm = PropertyMap.ForProperty(P(typeof(OrderDto), "Total"));
        fwdPm.SourcePath = new SourceMemberPath(new[] { P(typeof(Order), "Total") });
        fwdPm.Ignored = true;   // forward says ignore
        fwd.PropertyMaps.Add(fwdPm);

        ReverseMapMirror.Mirror(new[] { fwd, rev });

        Assert.Empty(rev.PropertyMaps);
    }

    [Fact]
    public void Mirror_ForwardCustomExpression_NotMirrored()
    {
        var (fwd, rev) = BuildPair(typeof(Order), typeof(OrderDto));
        var fwdPm = PropertyMap.ForProperty(P(typeof(OrderDto), "Total"));
        // Forward had .MapFrom(s => s.Total + 1) — non-invertible.
        fwdPm.CustomExpression = (System.Linq.Expressions.Expression<Func<Order, decimal>>)(s => s.Total + 1m);
        fwd.PropertyMaps.Add(fwdPm);

        ReverseMapMirror.Mirror(new[] { fwd, rev });

        Assert.Empty(rev.PropertyMaps);
    }

    [Fact]
    public void Mirror_ForwardConstant_NotMirrored()
    {
        var (fwd, rev) = BuildPair(typeof(Order), typeof(OrderDto));
        var fwdPm = PropertyMap.ForProperty(P(typeof(OrderDto), "Total"));
        fwdPm.HasConstant = true;
        fwdPm.ConstantValue = 99m;
        fwd.PropertyMaps.Add(fwdPm);

        ReverseMapMirror.Mirror(new[] { fwd, rev });

        Assert.Empty(rev.PropertyMaps);
    }

    public sealed class WriteOnlyDest
    {
        private string? _backing;
        public string? WriteOnly { set => _backing = value; }   // no getter
        public string? Visible => _backing;
    }
    public sealed class SimpleSrc { public string? Name { get; set; } }

    [Fact]
    public void Mirror_ForwardDestPropertyNoGetter_NotMirrored()
    {
        // Forward: SimpleSrc → WriteOnlyDest with PM targeting WriteOnlyDest.WriteOnly.
        // Mirror cannot use a no-getter dest as a reverse source — must skip.
        var (fwd, rev) = BuildPair(typeof(SimpleSrc), typeof(WriteOnlyDest));
        var writeOnlyProp = typeof(WriteOnlyDest).GetProperty("WriteOnly")!;
        var fwdPm = PropertyMap.ForProperty(writeOnlyProp);
        fwdPm.SourcePath = new SourceMemberPath(new[] { P(typeof(SimpleSrc), "Name") });
        fwd.PropertyMaps.Add(fwdPm);

        ReverseMapMirror.Mirror(new[] { fwd, rev });

        Assert.Empty(rev.PropertyMaps);
    }

    public sealed class GetterOnlySrc { public string ReadOnlyName { get; } = "fixed"; }
    public sealed class SettableDto { public string? Name { get; set; } }

    [Fact]
    public void Mirror_ForwardSourceLeafNoSetterOnReverseDest_NotMirrored()
    {
        // Forward: GetterOnlySrc.ReadOnlyName → SettableDto.Name (get-only forward source).
        // Reverse mirror would try to write to GetterOnlySrc.ReadOnlyName (no setter on the reverse
        // destination = the forward source). FlipBinding returns null for that — must skip.
        var (fwd, rev) = BuildPair(typeof(GetterOnlySrc), typeof(SettableDto));
        var fwdPm = PropertyMap.ForProperty(P(typeof(SettableDto), "Name"));
        fwdPm.SourcePath = new SourceMemberPath(new[] { P(typeof(GetterOnlySrc), "ReadOnlyName") });
        fwd.PropertyMaps.Add(fwdPm);

        ReverseMapMirror.Mirror(new[] { fwd, rev });

        Assert.Empty(rev.PropertyMaps);
    }
}
```

(Step 1's tests use `TypeMap` directly via the `internal` accessor — `Atlas.Tests` already has `InternalsVisibleTo`. The tests construct `TypeMap` and `PropertyMap` instances rather than going through the fluent surface so the mirror logic can be exercised in isolation.)

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~ReverseMapMirrorTests" --nologo`

Expected: 10 failures — `ReverseMapMirror` does not exist.

- [ ] **Step 3: Implement `ReverseMapMirror`**

Create `src/Atlas/Internal/ReverseMapMirror.cs`:

```csharp
namespace Atlas.Internal;

/// <summary>
/// Mirrors forward TypeMap bindings into reverse TypeMaps. Runs after
/// <c>InheritanceMerger.Resolve</c> and <c>ConventionEngine.ResolveMissingMembers</c>
/// have populated forward maps, before any sealing. See spec §6.
/// </summary>
internal static class ReverseMapMirror
{
    /// <summary>
    /// For every TypeMap with a non-null <see cref="TypeMap.ReverseMapPair"/>: look up the
    /// forward TypeMap, then for each forward PropertyMap that is eligible for mirroring
    /// (per §6.2) AND not already covered on the reverse map (per skip rules in §6.1),
    /// create a reverse PropertyMap with directions flipped.
    /// </summary>
    public static void Mirror(IEnumerable<TypeMap> typeMaps)
    {
        var byPair = typeMaps.ToDictionary(t => t.Pair);

        foreach (var tm in byPair.Values)
        {
            if (tm.ReverseMapPair is not { } forwardPair) continue;
            if (!byPair.TryGetValue(forwardPair, out var forward)) continue;

            foreach (var fwdPm in forward.PropertyMaps)
            {
                if (!IsMirrorEligible(fwdPm)) continue;
                var mirrored = FlipBinding(fwdPm);
                if (mirrored is null) continue;

                // Skip-rule-1: exact-name binding already exists on the reverse.
                if (tm.PropertyMaps.Any(p => p.Name == mirrored.Name)) continue;

                // Skip-rule-2: user explicitly mapped the TOP intermediate as a whole.
                if (mirrored.DestinationPath is { Count: > 1 } path)
                {
                    var topName = path[0].Name;
                    if (tm.PropertyMaps.Any(p => p.Name == topName && p.IsExplicit))
                        continue;
                }

                tm.PropertyMaps.Add(mirrored);
            }
        }
    }

    private static bool IsMirrorEligible(PropertyMap fwdPm)
    {
        if (fwdPm.SourcePath is null) return false;
        if (fwdPm.Ignored) return false;
        if (fwdPm.HasConstant) return false;
        if (fwdPm.CustomExpression is not null) return false;
        if (fwdPm.DestinationProperty is null) return false;            // skip ctor-param bindings
        if (fwdPm.DestinationProperty.GetMethod is not { IsPublic: true }) return false;   // can't read on reverse
        return true;
    }

    private static PropertyMap? FlipBinding(PropertyMap fwdPm)
    {
        var fwdSourceChain = fwdPm.SourcePath!.Members;
        var fwdDestProp = fwdPm.DestinationProperty!;

        if (fwdSourceChain.Count == 1)
        {
            var revDestProp = fwdSourceChain[0];
            if (revDestProp.SetMethod is not { IsPublic: true }) return null;
            var revPm = PropertyMap.ForProperty(revDestProp);
            revPm.SourcePath = new SourceMemberPath(new[] { fwdDestProp });
            return revPm;
        }

        // Multi-level: reverse becomes an unflatten path.
        var revPath = fwdSourceChain;
        if (revPath[^1].SetMethod is not { IsPublic: true }) return null;
        var revPmMulti = PropertyMap.ForPath(revPath);
        revPmMulti.SourcePath = new SourceMemberPath(new[] { fwdDestProp });
        return revPmMulti;
    }
}
```

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~ReverseMapMirrorTests" --nologo`

Expected: 10/10 pass.

- [ ] **Step 5: Run full test suite**

Run: `dotnet test --nologo`

Expected: 312 tests pass (302 + 10).

- [ ] **Step 6: Commit**

```powershell
git add src/Atlas/Internal/ReverseMapMirror.cs tests/Atlas.Tests/Internal/ReverseMapMirrorTests.cs
git commit -m "Add ReverseMapMirror with skip rules + flip algorithm (10 tests)"
```

---

## Task 9: Wire `ReverseMapMirror.Mirror` into `MapperConfiguration` build sequence

**Files:**
- Modify: `src/Atlas/MapperConfiguration.cs`

Insert the mirror call between `ConventionEngine.ResolveMissingMembers` and `tm.Seal()`. No new tests in this task — the integration is exercised by Task 10's end-to-end tests. Spec references: §2.2, §5.4.

- [ ] **Step 1: Locate the insertion point**

Open `src/Atlas/MapperConfiguration.cs`. Find the constructor body (around line 39-48):

```csharp
        InheritanceMerger.Resolve(typeMaps, pairIndex);

        foreach (var tm in typeMaps)
            ConventionEngine.ResolveMissingMembers(tm, _conventionOptions, HasRegisteredMap);

        foreach (var tm in typeMaps)
            tm.Seal();

        expression.MarkBuilt();
        _registry = new MapperRegistry(typeMaps, _stringToEnumCache);
```

- [ ] **Step 2: Insert the Mirror call**

Replace the snippet with:

```csharp
        InheritanceMerger.Resolve(typeMaps, pairIndex);

        foreach (var tm in typeMaps)
            ConventionEngine.ResolveMissingMembers(tm, _conventionOptions, HasRegisteredMap);

        ReverseMapMirror.Mirror(typeMaps);

        foreach (var tm in typeMaps)
            tm.Seal();

        expression.MarkBuilt();
        _registry = new MapperRegistry(typeMaps, _stringToEnumCache);
```

- [ ] **Step 3: Run full test suite**

Run: `dotnet test --nologo`

Expected: 312 tests still pass. The Mirror call is a no-op for any TypeMap without `ReverseMapPair` set, so existing tests are not affected. Tests from Tasks 6-8 remain green.

- [ ] **Step 4: Commit**

```powershell
git add src/Atlas/MapperConfiguration.cs
git commit -m "Wire ReverseMapMirror.Mirror into MapperConfiguration build sequence (no new tests)"
```

---

## Task 10: End-to-end `MapperReverseMapTests`

**Files:**
- Create: `tests/Atlas.Tests/MapperReverseMapTests.cs`

Eight end-to-end tests that exercise the full pipeline (configure → build → map). Round-trip, unflattening, multi-level intermediates, ForPath override, MemberList interactions. Spec references: §10 (Worked Example).

- [ ] **Step 1: Write the tests**

Create `tests/Atlas.Tests/MapperReverseMapTests.cs`:

```csharp
namespace Atlas.Tests;

public class MapperReverseMapTests
{
    public class Customer { public string? Name { get; set; } public string? Email { get; set; } }
    public class Order
    {
        public int Id { get; set; }
        public Customer? Customer { get; set; }
        public decimal Subtotal { get; set; }
        public decimal Tax { get; set; }
    }
    public class OrderDto
    {
        public string? CustomerName { get; set; }
        public string? CustomerEmail { get; set; }
        public decimal OrderTotal { get; set; }
    }

    public class Address { public string? City { get; set; } }
    public class CustomerDeep { public Address? HomeAddress { get; set; } }
    public class OrderDeep { public CustomerDeep? Customer { get; set; } }
    public class OrderDeepDto { public string? CustomerHomeAddressCity { get; set; } }

    [Fact]
    public void RoundTrip_OrderDtoToOrder_FlattenedThenUnflattened()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap<Order, OrderDto>()
                .ForMember(d => d.OrderTotal, opt => opt.MapFrom(s => s.Subtotal + s.Tax))
                .ReverseMap());
        var mapper = cfg.CreateMapper();

        var entity = new Order { Id = 7, Customer = new Customer { Name = "Alice", Email = "a@x" }, Subtotal = 90m, Tax = 10m };
        var dto = mapper.Map<OrderDto>(entity);

        Assert.Equal("Alice", dto.CustomerName);
        Assert.Equal("a@x", dto.CustomerEmail);
        Assert.Equal(100m, dto.OrderTotal);

        var roundTripped = mapper.Map<Order>(dto);
        Assert.NotNull(roundTripped.Customer);
        Assert.Equal("Alice", roundTripped.Customer!.Name);
        Assert.Equal("a@x", roundTripped.Customer.Email);
        // Id, Subtotal, Tax are not on the DTO and not configured on reverse — defaults expected.
        Assert.Equal(0, roundTripped.Id);
        Assert.Equal(0m, roundTripped.Subtotal);
        Assert.Equal(0m, roundTripped.Tax);
    }

    [Fact]
    public void Reverse_UnflatteningWritesNestedIntermediate()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<Order, OrderDto>().ReverseMap());
        var mapper = cfg.CreateMapper();

        var dto = new OrderDto { CustomerName = "Bob", CustomerEmail = "b@x" };
        var entity = mapper.Map<Order>(dto);

        Assert.NotNull(entity.Customer);
        Assert.Equal("Bob", entity.Customer!.Name);
        Assert.Equal("b@x", entity.Customer.Email);
    }

    [Fact]
    public void Reverse_ThreeLevelUnflattenWorks()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<OrderDeep, OrderDeepDto>().ReverseMap());
        var mapper = cfg.CreateMapper();

        var dto = new OrderDeepDto { CustomerHomeAddressCity = "London" };
        var entity = mapper.Map<OrderDeep>(dto);

        Assert.NotNull(entity.Customer);
        Assert.NotNull(entity.Customer!.HomeAddress);
        Assert.Equal("London", entity.Customer.HomeAddress!.City);
    }

    public class OrderWithPricing
    {
        public Pricing? Pricing { get; set; }
    }
    public class Pricing { public decimal Total { get; set; } }
    public class PricingDto { public decimal OrderTotal { get; set; } }

    [Fact]
    public void Reverse_ForPathOverride_BeatsMirroredBinding()
    {
        // Forward: OrderWithPricing.Pricing.Total → PricingDto.OrderTotal (convention flattening).
        // Reverse override: ForPath(d => d.Pricing.Total, opt => opt.MapFrom(s => s.OrderTotal * 2)).
        var cfg = new MapperConfiguration(c => c.CreateMap<OrderWithPricing, PricingDto>()
            .ReverseMap()
            .ForPath(d => d.Pricing!.Total, opt => opt.MapFrom(s => s.OrderTotal * 2)));
        var mapper = cfg.CreateMapper();

        var dto = new PricingDto { OrderTotal = 50m };
        var entity = mapper.Map<OrderWithPricing>(dto);

        Assert.NotNull(entity.Pricing);
        Assert.Equal(100m, entity.Pricing!.Total);   // override doubled it
    }

    [Fact]
    public void Reverse_IgnoreOnReverse_Honoured()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<Order, OrderDto>()
            .ReverseMap()
            .ForMember(d => d.Customer, opt => opt.Ignore()));   // wholesale ignore Customer on reverse
        var mapper = cfg.CreateMapper();

        var dto = new OrderDto { CustomerName = "should not be written", CustomerEmail = "ignored" };
        var entity = mapper.Map<Order>(dto);

        // Mirror's skip-rule-2 sees IsExplicit Customer binding (Ignored=true) — Customer.Name/Email mirror entries suppressed.
        Assert.Null(entity.Customer);
    }

    [Fact]
    public void Reverse_MemberListDestination_TriggersValidationErrors()
    {
        // Reverse with MemberList.Destination should report unmapped Order properties (Id, Subtotal, Tax)
        // but NOT report Customer (path[0] coverage).
        var cfg = new MapperConfiguration(c => c.CreateMap<Order, OrderDto>()
            .ReverseMap(MemberList.Destination));

        var ex = Assert.Throws<AtlasConfigurationException>(() => cfg.AssertConfigurationIsValid());

        Assert.Contains("Id", ex.Message);
        Assert.Contains("Subtotal", ex.Message);
        Assert.Contains("Tax", ex.Message);
        Assert.DoesNotContain("Customer ", ex.Message);   // trailing space disambiguates from "Customer.Name" etc.
    }

    [Fact]
    public void Reverse_TwoLevelChain_PreservesValueViaRoundTrip()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<Order, OrderDto>().ReverseMap());
        var mapper = cfg.CreateMapper();

        var entity1 = new Order { Customer = new Customer { Name = "round" } };
        var dto = mapper.Map<OrderDto>(entity1);
        var entity2 = mapper.Map<Order>(dto);

        Assert.Equal("round", entity2.Customer!.Name);
    }

    [Fact]
    public void Reverse_UpdateInPlace_MapSourceDestExistingDest_Works()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<Order, OrderDto>().ReverseMap());
        var mapper = cfg.CreateMapper();

        var existingEntity = new Order { Id = 99, Customer = new Customer { Email = "preset@x" } };
        var dto = new OrderDto { CustomerName = "Updated" };

        mapper.Map<OrderDto, Order>(dto, existingEntity);

        Assert.Equal("Updated", existingEntity.Customer!.Name);
        Assert.Equal("preset@x", existingEntity.Customer.Email);  // preserved (DTO has no value to overwrite)
        Assert.Equal(99, existingEntity.Id);                      // preserved
    }
}
```

- [ ] **Step 2: Run tests to verify pass**

Run: `dotnet test tests/Atlas.Tests --filter "FullyQualifiedName~MapperReverseMapTests" --nologo`

Expected: 8/8 pass. (If any fail, it likely indicates an integration gap from earlier tasks — fix the root cause, do not adjust the test to match.)

- [ ] **Step 3: Run full test suite**

Run: `dotnet test --nologo`

Expected: 320 tests pass (312 + 8). Note: the design said ~319; the actual count is 320 due to one extra test added in Task 4 (`NestedAssign_TwoBindingsSharingPrefix_BothPopulate`).

- [ ] **Step 4: Commit**

```powershell
git add tests/Atlas.Tests/MapperReverseMapTests.cs
git commit -m "Add end-to-end MapperReverseMapTests (8 tests)"
```

---

## Task 11: README + coverage check

**Files:**
- Modify: `README.md`

Add a `## Reverse mapping` section to the README with a worked example. Remove "Reverse mapping & unflattening" from the Deferred-to-v2 list. Verify coverage.

- [ ] **Step 1: Locate the deferred-to-v2 list in README**

Read `README.md`. Find the section that lists deferred features (below the Enum surface section added by the previous feature). It should currently list "Reverse mapping & unflattening (`ReverseMap`)" as a deferred item.

- [ ] **Step 2: Remove the deferred entry and add the new section**

Two edits:

(a) Remove the `Reverse mapping & unflattening` bullet from the deferred-features list.

(b) Add a new section before the "Deferred to v2" list and after the "Enum surface" section:

```markdown
## Reverse mapping

Declare both directions with one call. Forward conventions and source-side flattening
auto-invert; the reverse map defaults to `MemberList.None`:

```csharp
public class OrderProfile : MapperProfile
{
    public OrderProfile()
    {
        CreateMap<Order, OrderDto>()
            .ForMember(d => d.OrderTotal, opt => opt.MapFrom(s => s.Subtotal + s.Tax))
            .ReverseMap();   // returns IMappingExpression<OrderDto, Order>
    }
}
```

Forward `Customer.Name → CustomerName` flattening becomes reverse `CustomerName → Customer.Name`
unflattening — intermediates are auto-instantiated via parameterless constructors. Use `ForPath`
on either direction to override or configure nested chains explicitly:

```csharp
.ReverseMap()
.ForPath(d => d.Pricing.Total, opt => opt.MapFrom(s => s.OrderTotal))
```

**What does NOT auto-invert** (reconfigure on the returned reverse expression if needed):
- `ForMember(MapFrom(expression))` — the forward expression is not inverted.
- `Ignore()` — does not propagate to the reverse direction.
- `ConvertUsing` — custom converters generally are not invertible.
- `Include`/`IncludeBase` — inheritance chains are not reversed.
- Enum per-value overrides — the reverse pair gets default ByValue strategy with no overrides.
- Constructor parameter mappings (`ForCtorParam`).

**Foot-gun guards** (caught by `AssertConfigurationIsValid`):
- Each intermediate type in a `ForPath` or mirrored unflatten path must have a public parameterless constructor.
- Each intermediate property must have a public setter.
- Declaring both `CreateMap<D, S>()` and `CreateMap<S, D>().ReverseMap()` for the same pair throws — pick one.
```

- [ ] **Step 3: Run coverage check**

Run: `dotnet test --nologo --collect:"XPlat Code Coverage" --results-directory ./TestResults`

Then locate the coverage report and verify Atlas core is at:
- Line ≥ 90%
- Branch ≥ 80%

If using `reportgenerator`:

```powershell
dotnet tool restore
reportgenerator -reports:./TestResults/**/coverage.cobertura.xml -targetdir:./TestResults/CoverageReport -reporttypes:Html
```

Then open `./TestResults/CoverageReport/index.html` and check the Atlas project's coverage row. If line < 90% or branch < 80%, identify the gap and add 1-2 targeted tests in the appropriate test file. Likely gaps:
- Edge cases in `BuildNestedAssign` (e.g., 4+ level paths)
- Edge cases in `ReverseMapMirror.IsMirrorEligible` not covered by §6.2 enumeration tests
- Mirror skip-rule interactions with Inheritance (forward map with both `.Include` and `.ReverseMap` — but Include is not reversed in scope A, so this should naturally pass)

Report the actual coverage numbers in the commit message.

- [ ] **Step 4: Update README's "Coverage" section if it exists**

If the README has a coverage table or status line (it did after the Enum feature), update it to reflect the new measured numbers. Use the actual measured percentages, not estimates.

- [ ] **Step 5: Run final full test suite**

Run: `dotnet test --nologo`

Expected: 320 tests pass (or whatever the actual final count was).

- [ ] **Step 6: Commit**

```powershell
git add README.md
git commit -m "docs: README — add reverse mapping section, refresh coverage numbers"
```

---

## Final review

After all 11 tasks land on the `feat/reverse-map` branch:

- [ ] **Step 1: Final-review by `superpowers:code-reviewer`**

The implementing controller (the agent driving subagent-driven-development) dispatches `superpowers:code-reviewer` over the full branch diff. The holistic review has caught critical bugs in prior features (e.g., the EnumSurface foot-gun guard) — do not skip it.

- [ ] **Step 2: Address any Critical / Important findings**

Per the review-catch frequency norm (~1 cross-task issue per feature from the holistic review), expect 0–2 issues. Fix in-branch with one or more `review fix:` commits (do not amend prior commits).

- [ ] **Step 3: Push and open PR**

Use `superpowers:finishing-a-development-branch` Option 2: push the branch, open a PR titled "Add reverse mapping & unflattening (ReverseMap + ForPath)" with the design doc summary in the body and the actual final test/coverage numbers.

- [ ] **Step 4: After merge — memory updates**

After the user confirms the PR is merged:
- Update `atlas_v2_design_docs_deferred.md` to mark feature #4 as shipped (linking to `docs/Atlas-Design-ReverseMap.md`) and to identify feature #5 (Before/after hooks) as next.
- Update `feedback_atlas_v2_workflow.md` baseline test count: 273 → 320 (or actual measured).
- If the holistic review surfaced a NEW class of bug not covered by `feedback_pseudocode_concrete_trace.md`, append it as Bug 4.

---

## Summary

- **11 tasks**, ~46 new tests (3 + 6 + 4 + 5 + 6 + 5 + 10 + 0 + 8 + 0 = 47 actually).
- **Test baseline:** 273 → ~320.
- **Coverage targets:** line ≥ 90%, branch ≥ 80% on `Atlas` core.
- **No new public types.** No new packages. No `Atlas.Projections` changes.
- **Branch:** `feat/reverse-map` cut from `main` HEAD `2db0b22` (after design + plan land).
- **Model selection** (per memory's per-task guidance): haiku for Tasks 1, 2, 7, 9, 11; sonnet for Tasks 3, 4, 5, 6, 8, 10.
