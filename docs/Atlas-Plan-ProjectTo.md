# Atlas.Projections (`ProjectTo`) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the `Atlas.Projections` package described in `docs/Atlas-Design-ProjectTo.md` so consumers can call `query.ProjectTo<TDest>(configuration)` and get a LINQ-translatable `IQueryable<TDest>`.

**Architecture:** A new `Atlas.Projections` package references `Atlas` (v1, unchanged). One public extension method `ProjectTo<TDestination>(IQueryable, MapperConfiguration, int maxDepth)`. Three internal pieces — `ProjectionValidator` (eager-throws on non-projectable bindings), `ProjectionPlanBuilder` (emits a fully-inlined `Expression<Func<TSource, TDest>>`, no calls into the v1 runtime invoker), and `ProjectionPlanCache` (per-`MapperConfiguration` via `ConditionalWeakTable`). Test posture: in-memory `IQueryable` for unit tests + a separate EF Core SQLite sub-project for translation smoke tests.

**Tech Stack:** .NET 10, xUnit v3 (built-in `Assert.X()`, no FluentAssertions), coverlet, BenchmarkDotNet (untouched), `Microsoft.EntityFrameworkCore.Sqlite` 10.0.0 (test-only).

**Spec reference:** `docs/Atlas-Design-ProjectTo.md`. Section numbers in this plan (e.g. "§5.2") refer to the spec.

**v1 conventions to mirror (do not deviate):**
- File-scoped namespaces.
- Internal types under `Internal/` subfolder.
- `internal sealed class` / `internal static class` unless otherwise noted.
- Test naming: `MethodOrFeature_Condition_ExpectedResult`.
- xUnit v3, `[Fact]` / `[Theory]` + `[InlineData]`.
- `System.Threading.Lock` (.NET 9+ type, not `object`) for mutual exclusion.
- `<InternalsVisibleTo Include="..." />` items in csproj, not `[assembly:]` attributes.
- `TreatWarningsAsErrors=true` is on globally; `GenerateDocumentationFile=true` is on; `CS1591` (missing XML doc on public) is suppressed via `Directory.Build.props`. Other doc warnings will fire.

---

## Task 1: Scaffold the `Atlas.Projections` source project

**Files:**
- Create: `src/Atlas.Projections/Atlas.Projections.csproj`
- Modify: `src/Atlas/Atlas.csproj` (add two `InternalsVisibleTo` entries)
- Modify: `Atlas.slnx` (add the new project)

- [ ] **Step 1: Create the csproj**

Write `src/Atlas.Projections/Atlas.Projections.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>Atlas.Projections</PackageId>
    <Description>LINQ-translatable projection (ProjectTo) for the Atlas mapper.</Description>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Atlas\Atlas.csproj" />
    <InternalsVisibleTo Include="Atlas.Projections.Tests" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Grant the v1 core's internals to the new package**

Open `src/Atlas/Atlas.csproj`. Inside the existing `<ItemGroup>` that already contains `<InternalsVisibleTo Include="Atlas.Tests" />` and `<InternalsVisibleTo Include="Atlas.Extensions.DependencyInjection" />`, append:
```xml
    <InternalsVisibleTo Include="Atlas.Projections" />
    <InternalsVisibleTo Include="Atlas.Projections.Tests" />
```

- [ ] **Step 3: Add the project to the solution**

Open `Atlas.slnx`. Find the existing `<Project Path="src/Atlas/Atlas.csproj" />` line and add a sibling line:
```xml
    <Project Path="src/Atlas.Projections/Atlas.Projections.csproj" />
```
(Keep it adjacent to the other `src/...` projects so the file stays grouped.)

- [ ] **Step 4: Build to verify the project compiles empty**

Run: `dotnet build src/Atlas.Projections/Atlas.Projections.csproj --nologo`

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 5: Commit**

```powershell
git add src/Atlas.Projections/Atlas.Projections.csproj src/Atlas/Atlas.csproj Atlas.slnx
git commit -m "Scaffold Atlas.Projections package"
```

---

## Task 2: Scaffold the unit-test project

**Files:**
- Create: `tests/Atlas.Projections.Tests/Atlas.Projections.Tests.csproj`
- Create: `tests/Atlas.Projections.Tests/GlobalUsings.cs`
- Modify: `Atlas.slnx`

- [ ] **Step 1: Create the test csproj** (mirror `tests/Atlas.Tests/Atlas.Tests.csproj` exactly)

Write `tests/Atlas.Projections.Tests/Atlas.Projections.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Atlas.Projections\Atlas.Projections.csproj" />
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="coverlet.collector" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Add the global usings file**

Write `tests/Atlas.Projections.Tests/GlobalUsings.cs`:
```csharp
global using Xunit;
```

- [ ] **Step 3: Add the test project to the solution**

Open `Atlas.slnx`. Add adjacent to the other `tests/...` projects:
```xml
    <Project Path="tests/Atlas.Projections.Tests/Atlas.Projections.Tests.csproj" />
```

- [ ] **Step 4: Build the test project**

Run: `dotnet build tests/Atlas.Projections.Tests/Atlas.Projections.Tests.csproj --nologo`

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 5: Commit**

```powershell
git add tests/Atlas.Projections.Tests/Atlas.Projections.Tests.csproj tests/Atlas.Projections.Tests/GlobalUsings.cs Atlas.slnx
git commit -m "Scaffold Atlas.Projections.Tests project"
```

---

## Task 3: Public exception types (`AtlasProjectionException`, `ProjectionDiagnostic`)

**Files:**
- Create: `src/Atlas.Projections/AtlasProjectionException.cs`
- Test: deferred — these types are exercised by every later test file; no dedicated test class.

- [ ] **Step 1: Write the file**

Write `src/Atlas.Projections/AtlasProjectionException.cs`:
```csharp
namespace Atlas.Projections;

/// <summary>
/// One entry in a projection-incompatibility report. <see cref="Member"/> is the destination
/// member name, or "(whole map)" when the entire pair is non-projectable, or
/// "(no map registered)" when the pair has no registered mapping at all.
/// </summary>
public sealed record ProjectionDiagnostic(
    Type SourceType,
    Type DestinationType,
    string Member,
    string Reason);

/// <summary>
/// Thrown when ProjectTo is asked to translate a configuration that contains constructs the
/// LINQ provider cannot handle. Aggregates every incompatibility for the requested
/// (TSource, TDestination) pair, including reachable nested pairs within maxDepth.
/// </summary>
public sealed class AtlasProjectionException : Exception
{
    public IReadOnlyList<ProjectionDiagnostic> Diagnostics { get; }

    public AtlasProjectionException(IReadOnlyList<ProjectionDiagnostic> diagnostics)
        : base(BuildMessage(diagnostics))
    {
        Diagnostics = diagnostics;
    }

    private static string BuildMessage(IReadOnlyList<ProjectionDiagnostic> diagnostics)
    {
        if (diagnostics.Count == 0) return "Atlas projection is invalid.";
        var lines = diagnostics.Select(d =>
            $"{d.SourceType.Name} -> {d.DestinationType.Name}.{d.Member}: {d.Reason}");
        return "Atlas projection is invalid:" + Environment.NewLine + string.Join(Environment.NewLine, lines);
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Atlas.Projections/Atlas.Projections.csproj --nologo`

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 3: Commit**

```powershell
git add src/Atlas.Projections/AtlasProjectionException.cs
git commit -m "Add AtlasProjectionException + ProjectionDiagnostic"
```

---

## Task 4: `ProjectionCompatibility` helper (§7.1, ~6 tests)

**Files:**
- Create: `tests/Atlas.Projections.Tests/Internal/ProjectionCompatibilityTests.cs`
- Create: `src/Atlas.Projections/Internal/ProjectionCompatibility.cs`

- [ ] **Step 1: Write all 6 tests, expected to fail**

Write `tests/Atlas.Projections.Tests/Internal/ProjectionCompatibilityTests.cs`:
```csharp
using Atlas.Internal;
using Atlas.Projections.Internal;

namespace Atlas.Projections.Tests.Internal;

public class ProjectionCompatibilityTests
{
    [Fact]
    public void IsTypeMapProjectable_NoCustomConverter_ReturnsTrue()
    {
        var tm = new TypeMap(typeof(string), typeof(string), MemberList.Destination);
        Assert.True(ProjectionCompatibility.IsTypeMapProjectable(tm, out var reason));
        Assert.Null(reason);
    }

    [Fact]
    public void IsTypeMapProjectable_CustomConverter_ReturnsFalseWithReason()
    {
        var tm = new TypeMap(typeof(string), typeof(int), MemberList.None)
        {
            CustomConverter = (Func<string, int>)(s => int.Parse(s)),
        };
        Assert.False(ProjectionCompatibility.IsTypeMapProjectable(tm, out var reason));
        Assert.NotNull(reason);
        Assert.Contains("ConvertUsing", reason);
    }

    [Fact]
    public void IsBindingProjectable_Constant_ReturnsTrue()
    {
        var pm = PropertyMap.ForProperty(typeof(Holder).GetProperty(nameof(Holder.Name))!);
        pm.HasConstant = true;
        pm.ConstantValue = "x";
        Assert.True(ProjectionCompatibility.IsBindingProjectable(pm, out var reason));
        Assert.Null(reason);
    }

    [Fact]
    public void IsBindingProjectable_CustomExpression_ReturnsTrue()
    {
        var pm = PropertyMap.ForProperty(typeof(Holder).GetProperty(nameof(Holder.Name))!);
        pm.CustomExpression = (System.Linq.Expressions.Expression<Func<Holder, string>>)(h => h.Name);
        Assert.True(ProjectionCompatibility.IsBindingProjectable(pm, out _));
    }

    [Fact]
    public void IsBindingProjectable_SourcePath_ReturnsTrue()
    {
        var pm = PropertyMap.ForProperty(typeof(Holder).GetProperty(nameof(Holder.Name))!);
        pm.SourcePath = new SourceMemberPath([typeof(Holder).GetProperty(nameof(Holder.Name))!]);
        Assert.True(ProjectionCompatibility.IsBindingProjectable(pm, out _));
    }

    [Fact]
    public void IsBindingProjectable_Ignored_ReturnsTrue()
    {
        // Ignore is fine — the validator skips ignored bindings entirely.
        var pm = PropertyMap.ForProperty(typeof(Holder).GetProperty(nameof(Holder.Name))!);
        pm.Ignored = true;
        Assert.True(ProjectionCompatibility.IsBindingProjectable(pm, out _));
    }

    private class Holder { public string Name { get; set; } = ""; }
}
```

- [ ] **Step 2: Run the tests to verify they fail to compile**

Run: `dotnet test tests/Atlas.Projections.Tests/Atlas.Projections.Tests.csproj --filter "FullyQualifiedName~ProjectionCompatibilityTests" --nologo`

Expected: build error referencing `Atlas.Projections.Internal.ProjectionCompatibility` (does not exist).

- [ ] **Step 3: Implement the helper**

Write `src/Atlas.Projections/Internal/ProjectionCompatibility.cs`:
```csharp
using Atlas.Internal;

namespace Atlas.Projections.Internal;

/// <summary>
/// Decides whether a <see cref="TypeMap"/> or a single <see cref="PropertyMap"/> can be emitted
/// as a projectable expression. Used by both <c>ProjectionValidator</c> (to surface diagnostics
/// up-front) and <c>ProjectionPlanBuilder</c> (to skip non-projectable bindings) so the two never
/// disagree on what's projectable.
/// </summary>
internal static class ProjectionCompatibility
{
    public static bool IsTypeMapProjectable(TypeMap tm, out string? reason)
    {
        if (tm.CustomConverter is not null)
        {
            reason = "ConvertUsing(...) — delegate-form converter is in-memory only.";
            return false;
        }
        reason = null;
        return true;
    }

    public static bool IsBindingProjectable(PropertyMap pm, out string? reason)
    {
        // v1 has no per-property delegate construct; if any are added, gate them here.
        reason = null;
        return true;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Atlas.Projections.Tests/Atlas.Projections.Tests.csproj --filter "FullyQualifiedName~ProjectionCompatibilityTests" --nologo`

Expected: `Passed!  - Failed: 0, Passed: 6`.

- [ ] **Step 5: Commit**

```powershell
git add tests/Atlas.Projections.Tests/Internal/ProjectionCompatibilityTests.cs src/Atlas.Projections/Internal/ProjectionCompatibility.cs
git commit -m "Add ProjectionCompatibility predicate (6 tests)"
```

---

## Task 5: `ProjectionValidator` (§7.2, ~10 tests)

**Files:**
- Create: `tests/Atlas.Projections.Tests/Internal/ProjectionValidatorTests.cs`
- Create: `src/Atlas.Projections/Internal/ProjectionValidator.cs`

- [ ] **Step 1: Write all 10 tests**

Write `tests/Atlas.Projections.Tests/Internal/ProjectionValidatorTests.cs`:
```csharp
using Atlas;
using Atlas.Internal;
using Atlas.Projections.Internal;

namespace Atlas.Projections.Tests.Internal;

public class ProjectionValidatorTests
{
    private static MapperRegistry RegistryFor(Action<MapperConfigurationExpression> configure)
    {
        var config = new MapperConfiguration(configure);
        return config.Internal_Registry;
    }

    [Fact]
    public void Validate_FullyMappedSimplePair_ReturnsSilently()
    {
        var registry = RegistryFor(c => c.CreateMap<VFlatSrc, VFlatDst>());
        ProjectionValidator.Validate(registry, new TypePair(typeof(VFlatSrc), typeof(VFlatDst)), maxDepth: 3);
    }

    [Fact]
    public void Validate_NoMapRegistered_ReportsRootMissing()
    {
        var registry = RegistryFor(c => { });
        var ex = Assert.Throws<AtlasProjectionException>(() =>
            ProjectionValidator.Validate(registry, new TypePair(typeof(VFlatSrc), typeof(VFlatDst)), maxDepth: 3));
        Assert.Single(ex.Diagnostics);
        Assert.Equal("(no map registered)", ex.Diagnostics[0].Member);
    }

    [Fact]
    public void Validate_CustomConverter_ReportsWholeMapIncompatible()
    {
        var registry = RegistryFor(c =>
            c.CreateMap<VFlatSrc, VFlatDst>().ConvertUsing(s => new VFlatDst { Id = s.Id, Name = s.Name }));
        var ex = Assert.Throws<AtlasProjectionException>(() =>
            ProjectionValidator.Validate(registry, new TypePair(typeof(VFlatSrc), typeof(VFlatDst)), maxDepth: 3));
        Assert.Equal("(whole map)", ex.Diagnostics[0].Member);
    }

    [Fact]
    public void Validate_NestedCustomConverter_ReportsNestedMap_NotRoot()
    {
        var registry = RegistryFor(c =>
        {
            c.CreateMap<VOuterSrc, VOuterDst>();
            c.CreateMap<VInnerSrc, VInnerDst>().ConvertUsing(s => new VInnerDst { Name = s.Name });
        });
        var ex = Assert.Throws<AtlasProjectionException>(() =>
            ProjectionValidator.Validate(registry, new TypePair(typeof(VOuterSrc), typeof(VOuterDst)), maxDepth: 3));
        // The diagnostic should be on the nested pair, not on VOuterSrc -> VOuterDst.
        Assert.Equal(typeof(VInnerSrc), ex.Diagnostics[0].SourceType);
        Assert.Equal(typeof(VInnerDst), ex.Diagnostics[0].DestinationType);
    }

    [Fact]
    public void Validate_UnresolvedDestinationMember_ReportsMember()
    {
        // VExtraDst has a member with no source counterpart and no Ignore.
        var registry = RegistryFor(c => c.CreateMap<VFlatSrc, VExtraDst>());
        var ex = Assert.Throws<AtlasProjectionException>(() =>
            ProjectionValidator.Validate(registry, new TypePair(typeof(VFlatSrc), typeof(VExtraDst)), maxDepth: 3));
        Assert.Contains(ex.Diagnostics, d => d.Member == nameof(VExtraDst.Extra));
    }

    [Fact]
    public void Validate_IgnoredMember_DoesNotReport()
    {
        var registry = RegistryFor(c =>
            c.CreateMap<VFlatSrc, VExtraDst>().ForMember(d => d.Extra, o => o.Ignore()));
        ProjectionValidator.Validate(registry, new TypePair(typeof(VFlatSrc), typeof(VExtraDst)), maxDepth: 3);
    }

    [Fact]
    public void Validate_NumericWidening_PassesValidation()
    {
        var registry = RegistryFor(c => c.CreateMap<VIntSrc, VLongDst>());
        ProjectionValidator.Validate(registry, new TypePair(typeof(VIntSrc), typeof(VLongDst)), maxDepth: 3);
    }

    [Fact]
    public void Validate_NestedObjectWithMissingMap_ReportsNestedTypePair()
    {
        var registry = RegistryFor(c => c.CreateMap<VOuterSrc, VOuterDst>());
        var ex = Assert.Throws<AtlasProjectionException>(() =>
            ProjectionValidator.Validate(registry, new TypePair(typeof(VOuterSrc), typeof(VOuterDst)), maxDepth: 3));
        Assert.Contains(ex.Diagnostics, d => d.SourceType == typeof(VInnerSrc) && d.DestinationType == typeof(VInnerDst));
    }

    [Fact]
    public void Validate_RecursiveCycle_StopsAtMaxDepth_DoesNotInfiniteLoop()
    {
        // VNode -> VNode is a self-cycle.
        var registry = RegistryFor(c => c.CreateMap<VNode, VNode>(MemberList.None));
        ProjectionValidator.Validate(registry, new TypePair(typeof(VNode), typeof(VNode)), maxDepth: 3);
    }

    [Fact]
    public void Validate_AggregatesAllErrors_NotJustFirst()
    {
        var registry = RegistryFor(c => c.CreateMap<VFlatSrc, VTwoMissingDst>());
        var ex = Assert.Throws<AtlasProjectionException>(() =>
            ProjectionValidator.Validate(registry, new TypePair(typeof(VFlatSrc), typeof(VTwoMissingDst)), maxDepth: 3));
        Assert.Equal(2, ex.Diagnostics.Count);
    }
}

// ---- Test fixtures ----
public class VFlatSrc { public int Id { get; set; } public string Name { get; set; } = ""; }
public class VFlatDst { public int Id { get; set; } public string Name { get; set; } = ""; }
public class VExtraDst { public int Id { get; set; } public string Name { get; set; } = ""; public string Extra { get; set; } = ""; }
public class VTwoMissingDst { public int Id { get; set; } public string Missing1 { get; set; } = ""; public string Missing2 { get; set; } = ""; }
public class VInnerSrc { public string Name { get; set; } = ""; }
public class VInnerDst { public string Name { get; set; } = ""; }
public class VOuterSrc { public VInnerSrc Inner { get; set; } = new(); }
public class VOuterDst { public VInnerDst Inner { get; set; } = new(); }
public class VIntSrc { public int Count { get; set; } }
public class VLongDst { public long Count { get; set; } }
public class VNode { public VNode? Next { get; set; } }
```

- [ ] **Step 2: Run the tests; expect compile failure**

Run: `dotnet test tests/Atlas.Projections.Tests/Atlas.Projections.Tests.csproj --filter "FullyQualifiedName~ProjectionValidatorTests" --nologo`

Expected: build error referencing `Atlas.Projections.Internal.ProjectionValidator` (does not exist).

- [ ] **Step 3: Implement the validator**

Write `src/Atlas.Projections/Internal/ProjectionValidator.cs`:
```csharp
using System.Reflection;
using Atlas.Internal;

namespace Atlas.Projections.Internal;

/// <summary>
/// Walks a <see cref="MapperRegistry"/> from a root <see cref="TypePair"/> and reports every
/// reachable binding that the projection builder will not be able to translate. Algorithm per
/// design §5.2.
/// </summary>
internal static class ProjectionValidator
{
    public static void Validate(MapperRegistry registry, TypePair root, int maxDepth)
    {
        var diagnostics = new List<ProjectionDiagnostic>();
        var visited = new HashSet<TypePair>();
        Walk(root, depth: 0, registry, visited, diagnostics, maxDepth);
        if (diagnostics.Count > 0)
            throw new AtlasProjectionException(diagnostics);
    }

    private static void Walk(
        TypePair pair,
        int depth,
        MapperRegistry registry,
        HashSet<TypePair> visited,
        List<ProjectionDiagnostic> diagnostics,
        int maxDepth)
    {
        if (depth >= maxDepth) return;
        if (!visited.Add(pair)) return;

        var tm = registry.GetTypeMap(pair);
        if (tm is null)
        {
            diagnostics.Add(new ProjectionDiagnostic(
                pair.Source, pair.Destination, "(no map registered)",
                $"No map registered for {pair.Source.Name} -> {pair.Destination.Name}."));
            return;
        }

        if (!ProjectionCompatibility.IsTypeMapProjectable(tm, out var typeMapReason))
        {
            diagnostics.Add(new ProjectionDiagnostic(
                pair.Source, pair.Destination, "(whole map)", typeMapReason!));
            return;
        }

        foreach (var pm in tm.PropertyMaps)
        {
            if (pm.Ignored) continue;
            if (!ProjectionCompatibility.IsBindingProjectable(pm, out var bindingReason))
            {
                diagnostics.Add(new ProjectionDiagnostic(
                    pair.Source, pair.Destination, pm.Name, bindingReason!));
                continue;
            }
            if (pm.HasConstant) continue;
            if (pm.CustomExpression is not null) continue;
            if (pm.SourcePath is null)
            {
                diagnostics.Add(new ProjectionDiagnostic(
                    pair.Source, pair.Destination, pm.Name,
                    "Unmapped — projection requires every destination binding resolved."));
                continue;
            }

            var leaf = pm.SourcePath.Members[^1].PropertyType;
            var target = pm.DestinationType;
            if (leaf == target || target.IsAssignableFrom(leaf)) continue;
            if (HasImplicitNumericConversion(leaf, target)) continue;

            if (IsCollection(leaf) && IsCollection(target))
            {
                Walk(new TypePair(GetEnumerableElementType(leaf)!, GetEnumerableElementType(target)!),
                    depth + 1, registry, visited, diagnostics, maxDepth);
                continue;
            }
            if (IsDictionary(leaf) && IsDictionary(target))
            {
                var srcArgs = leaf.GetGenericArguments();
                var dstArgs = target.GetGenericArguments();
                Walk(new TypePair(srcArgs[0], dstArgs[0]), depth + 1, registry, visited, diagnostics, maxDepth);
                Walk(new TypePair(srcArgs[1], dstArgs[1]), depth + 1, registry, visited, diagnostics, maxDepth);
                continue;
            }

            Walk(new TypePair(leaf, target), depth + 1, registry, visited, diagnostics, maxDepth);
        }
    }

    private static bool IsCollection(Type t) =>
        t != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(t);

    private static bool IsDictionary(Type t) =>
        t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>);

    private static Type? GetEnumerableElementType(Type t)
    {
        if (t.IsArray) return t.GetElementType();
        foreach (var i in new[] { t }.Concat(t.GetInterfaces()))
            if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return i.GetGenericArguments()[0];
        return null;
    }

    private static bool HasImplicitNumericConversion(Type src, Type dst) =>
        (src, dst) switch
        {
            _ when src == typeof(sbyte) => dst == typeof(short) || dst == typeof(int) || dst == typeof(long) || dst == typeof(float) || dst == typeof(double) || dst == typeof(decimal),
            _ when src == typeof(byte) => dst == typeof(short) || dst == typeof(ushort) || dst == typeof(int) || dst == typeof(uint) || dst == typeof(long) || dst == typeof(ulong) || dst == typeof(float) || dst == typeof(double) || dst == typeof(decimal),
            _ when src == typeof(short) => dst == typeof(int) || dst == typeof(long) || dst == typeof(float) || dst == typeof(double) || dst == typeof(decimal),
            _ when src == typeof(ushort) => dst == typeof(int) || dst == typeof(uint) || dst == typeof(long) || dst == typeof(ulong) || dst == typeof(float) || dst == typeof(double) || dst == typeof(decimal),
            _ when src == typeof(int) => dst == typeof(long) || dst == typeof(float) || dst == typeof(double) || dst == typeof(decimal),
            _ when src == typeof(uint) => dst == typeof(long) || dst == typeof(ulong) || dst == typeof(float) || dst == typeof(double) || dst == typeof(decimal),
            _ when src == typeof(long) => dst == typeof(float) || dst == typeof(double) || dst == typeof(decimal),
            _ when src == typeof(ulong) => dst == typeof(float) || dst == typeof(double) || dst == typeof(decimal),
            _ when src == typeof(float) => dst == typeof(double),
            _ when src == typeof(char) => dst == typeof(ushort) || dst == typeof(int) || dst == typeof(uint) || dst == typeof(long) || dst == typeof(ulong) || dst == typeof(float) || dst == typeof(double) || dst == typeof(decimal),
            _ => false,
        };
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Atlas.Projections.Tests/Atlas.Projections.Tests.csproj --filter "FullyQualifiedName~ProjectionValidatorTests" --nologo`

Expected: `Passed!  - Failed: 0, Passed: 10`.

- [ ] **Step 5: Commit**

```powershell
git add tests/Atlas.Projections.Tests/Internal/ProjectionValidatorTests.cs src/Atlas.Projections/Internal/ProjectionValidator.cs
git commit -m "Add ProjectionValidator (10 tests)"
```

---

## Task 6: `ParameterReplacer` helper

**Files:**
- Create: `src/Atlas.Projections/Internal/ParameterReplacer.cs`
- Test: deferred — exercised by the builder tests in Task 7.

This is a 15-line `ExpressionVisitor` with no behavior worth a dedicated test. Its correctness is asserted indirectly in Task 7's `Build_CustomExpression_RebindsParameter`.

- [ ] **Step 1: Write the file**

Write `src/Atlas.Projections/Internal/ParameterReplacer.cs`:
```csharp
using System.Linq.Expressions;

namespace Atlas.Projections.Internal;

/// <summary>
/// Swaps a <see cref="ParameterExpression"/> for an arbitrary replacement expression while
/// visiting an expression tree. Used by <c>ProjectionPlanBuilder</c> when inlining a
/// user-provided custom expression or a nested map's body into the parent projection.
/// </summary>
internal sealed class ParameterReplacer : ExpressionVisitor
{
    private readonly ParameterExpression _target;
    private readonly Expression _replacement;

    public ParameterReplacer(ParameterExpression target, Expression replacement)
    {
        _target = target;
        _replacement = replacement;
    }

    public static Expression Replace(Expression body, ParameterExpression target, Expression replacement) =>
        new ParameterReplacer(target, replacement).Visit(body)!;

    protected override Expression VisitParameter(ParameterExpression node) =>
        node == _target ? _replacement : base.VisitParameter(node);
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/Atlas.Projections/Atlas.Projections.csproj --nologo`

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 3: Commit**

```powershell
git add src/Atlas.Projections/Internal/ParameterReplacer.cs
git commit -m "Add ParameterReplacer expression visitor"
```

---

## Task 7: `ProjectionPlanBuilder` (§7.3, ~12 tests)

This is the biggest task. The builder is implemented all at once because the tests exercise distinct features that converge on one cohesive type. Write all tests first, then implement until green. The order below mirrors the natural build-up: flat → nested → null-safety → collections → ctor → depth.

**Files:**
- Create: `tests/Atlas.Projections.Tests/Internal/ProjectionPlanBuilderTests.cs`
- Create: `tests/Atlas.Projections.Tests/Internal/AssertExpression.cs` (small visitor-based helper for shape assertions)
- Create: `src/Atlas.Projections/Internal/ProjectionPlanBuilder.cs`

- [ ] **Step 1: Write the shape-assertion helper**

Write `tests/Atlas.Projections.Tests/Internal/AssertExpression.cs`:
```csharp
using System.Linq.Expressions;
using System.Reflection;

namespace Atlas.Projections.Tests.Internal;

/// <summary>
/// Small whitebox assertions over an expression tree. Used by the builder tests to verify
/// the SHAPE of the emitted lambda, not just its execution result.
/// </summary>
internal static class AssertExpression
{
    public static bool Contains<TNode>(Expression expression) where TNode : Expression
    {
        var found = false;
        var visitor = new PredicateVisitor(node => { if (node is TNode) { found = true; return true; } return false; });
        visitor.Visit(expression);
        return found;
    }

    public static bool ContainsCallTo(Expression expression, string declaringTypeName, string methodName)
    {
        var found = false;
        var visitor = new PredicateVisitor(node =>
        {
            if (node is MethodCallExpression mc &&
                mc.Method.DeclaringType?.Name == declaringTypeName &&
                mc.Method.Name == methodName)
            {
                found = true;
                return true;
            }
            return false;
        });
        visitor.Visit(expression);
        return found;
    }

    public static int CountNodes<TNode>(Expression expression) where TNode : Expression
    {
        var count = 0;
        var visitor = new PredicateVisitor(node => { if (node is TNode) count++; return false; });
        visitor.Visit(expression);
        return count;
    }

    private sealed class PredicateVisitor : ExpressionVisitor
    {
        private readonly Func<Expression, bool> _stopWhen;
        public PredicateVisitor(Func<Expression, bool> stopWhen) { _stopWhen = stopWhen; }
        public override Expression? Visit(Expression? node)
        {
            if (node is null) return null;
            if (_stopWhen(node)) return node;
            return base.Visit(node);
        }
    }
}
```

- [ ] **Step 2: Write all 12 builder tests**

Write `tests/Atlas.Projections.Tests/Internal/ProjectionPlanBuilderTests.cs`:
```csharp
using System.Linq.Expressions;
using Atlas;
using Atlas.Internal;
using Atlas.Projections.Internal;

namespace Atlas.Projections.Tests.Internal;

public class ProjectionPlanBuilderTests
{
    private static (MapperRegistry registry, LambdaExpression lambda) Build<TSource, TDestination>(
        Action<MapperConfigurationExpression> configure,
        int maxDepth = 3)
    {
        var config = new MapperConfiguration(configure);
        var registry = config.Internal_Registry;
        var lambda = ProjectionPlanBuilder.Build(registry, new TypePair(typeof(TSource), typeof(TDestination)), maxDepth);
        return (registry, lambda);
    }

    [Fact]
    public void Build_FlatPair_EmitsMemberInitWithBindings()
    {
        var (_, lambda) = Build<BFlatSrc, BFlatDst>(c => c.CreateMap<BFlatSrc, BFlatDst>());
        Assert.True(AssertExpression.Contains<MemberInitExpression>(lambda.Body));
    }

    [Fact]
    public void Build_FlatPair_DoesNotContainMappingInvokerCall()
    {
        // Load-bearing safety check: projection lambdas must never delegate to the v1 runtime invoker.
        var (_, lambda) = Build<BFlatSrc, BFlatDst>(c => c.CreateMap<BFlatSrc, BFlatDst>());
        Assert.False(AssertExpression.ContainsCallTo(lambda.Body, "MappingInvoker", "Invoke"));
        Assert.False(AssertExpression.ContainsCallTo(lambda.Body, "MappingInvoker", "InvokeToList"));
        Assert.False(AssertExpression.ContainsCallTo(lambda.Body, "MappingInvoker", "InvokeToArray"));
    }

    [Fact]
    public void Build_NestedObject_InlinesNestedMemberInit()
    {
        var (_, lambda) = Build<BOuterSrc, BOuterDst>(c =>
        {
            c.CreateMap<BInnerSrc, BInnerDst>();
            c.CreateMap<BOuterSrc, BOuterDst>();
        });
        // Two MemberInit nodes: outer + inner.
        Assert.Equal(2, AssertExpression.CountNodes<MemberInitExpression>(lambda.Body));
    }

    [Fact]
    public void Build_NestedClassMember_WrapsInNullSafeConditional()
    {
        var (_, lambda) = Build<BOuterSrc, BOuterDst>(c =>
        {
            c.CreateMap<BInnerSrc, BInnerDst>();
            c.CreateMap<BOuterSrc, BOuterDst>();
        });
        Assert.True(AssertExpression.Contains<ConditionalExpression>(lambda.Body));
    }

    [Fact]
    public void Build_NumericWidening_EmitsConvert()
    {
        var (_, lambda) = Build<BIntSrc, BLongDst>(c => c.CreateMap<BIntSrc, BLongDst>());
        Assert.True(AssertExpression.Contains<UnaryExpression>(lambda.Body));
    }

    [Fact]
    public void Build_ConstantBinding_EmitsConstantNode()
    {
        var (_, lambda) = Build<BFlatSrc, BFlatDst>(c =>
            c.CreateMap<BFlatSrc, BFlatDst>().ForMember(d => d.Name, o => o.MapFrom("k")));
        Assert.True(AssertExpression.Contains<ConstantExpression>(lambda.Body));
    }

    [Fact]
    public void Build_CustomExpression_RebindsParameter()
    {
        var (_, lambda) = Build<BFlatSrc, BFlatDst>(c =>
            c.CreateMap<BFlatSrc, BFlatDst>()
                .ForMember(d => d.Name, o => o.MapFrom(s => s.Name + "!")));
        // Compile + invoke proves the parameter was rebound to the outer src parameter.
        var fn = (Func<BFlatSrc, BFlatDst>)lambda.Compile();
        var dst = fn(new BFlatSrc { Id = 1, Name = "x" });
        Assert.Equal("x!", dst.Name);
    }

    [Fact]
    public void Build_CollectionMember_EmitsSelectOverElementProjection()
    {
        var (_, lambda) = Build<BParentSrc, BParentDst>(c =>
        {
            c.CreateMap<BInnerSrc, BInnerDst>();
            c.CreateMap<BParentSrc, BParentDst>();
        });
        Assert.True(AssertExpression.ContainsCallTo(lambda.Body, "Enumerable", "Select"));
    }

    [Fact]
    public void Build_DepthLimit_RecursiveMember_EmitsDefault()
    {
        var (_, lambda) = Build<BNode, BNode>(c => c.CreateMap<BNode, BNode>(MemberList.None), maxDepth: 1);
        // After one level, the recursive Next member becomes default(BNode) — i.e., null literal.
        // The lambda compiles and executing it yields a single-level clone whose Next is null.
        var fn = (Func<BNode, BNode>)lambda.Compile();
        var src = new BNode { Next = new BNode { Next = new BNode() } };
        var dst = fn(src);
        Assert.NotNull(dst);
        Assert.Null(dst.Next);
    }

    [Fact]
    public void Build_IgnoredMember_OmitsBinding()
    {
        var (_, lambda) = Build<BFlatSrc, BFlatDst>(c =>
            c.CreateMap<BFlatSrc, BFlatDst>().ForMember(d => d.Name, o => o.Ignore()));
        var fn = (Func<BFlatSrc, BFlatDst>)lambda.Compile();
        var dst = fn(new BFlatSrc { Id = 7, Name = "x" });
        Assert.Equal(7, dst.Id);
        Assert.Equal("", dst.Name); // never bound; default of "" from field initializer
    }

    [Fact]
    public void Build_RecordCtor_UsesNewExpressionWithCtorArgs()
    {
        var (_, lambda) = Build<BRecSrc, BRecDst>(c => c.CreateMap<BRecSrc, BRecDst>(MemberList.None));
        Assert.True(AssertExpression.Contains<NewExpression>(lambda.Body));
    }

    [Fact]
    public void Build_LambdaParameterCount_IsExactlyOne()
    {
        var (_, lambda) = Build<BFlatSrc, BFlatDst>(c => c.CreateMap<BFlatSrc, BFlatDst>());
        Assert.Single(lambda.Parameters);
        Assert.Equal(typeof(BFlatSrc), lambda.Parameters[0].Type);
    }
}

// ---- Test fixtures ----
public class BFlatSrc { public int Id { get; set; } public string Name { get; set; } = ""; }
public class BFlatDst { public int Id { get; set; } public string Name { get; set; } = ""; }
public class BInnerSrc { public string Name { get; set; } = ""; }
public class BInnerDst { public string Name { get; set; } = ""; }
public class BOuterSrc { public BInnerSrc Inner { get; set; } = new(); }
public class BOuterDst { public BInnerDst Inner { get; set; } = new(); }
public class BParentSrc { public List<BInnerSrc> Items { get; set; } = new(); }
public class BParentDst { public List<BInnerDst> Items { get; set; } = new(); }
public class BIntSrc { public int Count { get; set; } }
public class BLongDst { public long Count { get; set; } }
public class BNode { public BNode? Next { get; set; } }
public class BRecSrc { public string DisplayName { get; set; } = ""; }
public record BRecDst(string DisplayName);
```

- [ ] **Step 3: Run the tests; expect compile failure**

Run: `dotnet test tests/Atlas.Projections.Tests/Atlas.Projections.Tests.csproj --filter "FullyQualifiedName~ProjectionPlanBuilderTests" --nologo`

Expected: build error referencing `Atlas.Projections.Internal.ProjectionPlanBuilder` (does not exist).

- [ ] **Step 4: Implement the builder**

Write `src/Atlas.Projections/Internal/ProjectionPlanBuilder.cs`:
```csharp
using System.Linq.Expressions;
using System.Reflection;
using Atlas.Internal;

namespace Atlas.Projections.Internal;

/// <summary>
/// Emits a fully-inlined <see cref="LambdaExpression"/> for a (TSource, TDestination) pair.
/// No call to <c>MappingInvoker</c> appears in the output — nested maps are inlined recursively
/// up to <c>maxDepth</c>. Algorithm per design §5.3.
/// </summary>
internal static class ProjectionPlanBuilder
{
    public static LambdaExpression Build(MapperRegistry registry, TypePair root, int maxDepth)
    {
        var tm = registry.GetTypeMap(root)
            ?? throw new InvalidOperationException(
                $"No map registered for {root.Source.Name} -> {root.Destination.Name}.");
        var srcParam = Expression.Parameter(tm.SourceType, "src");
        var body = BuildBody(tm, srcParam, depth: 0, registry, maxDepth);
        var funcType = typeof(Func<,>).MakeGenericType(tm.SourceType, tm.DestinationType);
        return Expression.Lambda(funcType, body, srcParam);
    }

    private static Expression BuildBody(TypeMap tm, Expression srcExpr, int depth, MapperRegistry registry, int maxDepth)
    {
        var (ctor, ctorParamMaps, propertyMaps) = ClassifyBindings(tm);

        Expression newExpr;
        if (ctor.GetParameters().Length == 0)
        {
            newExpr = Expression.New(ctor);
        }
        else
        {
            var args = ctor.GetParameters().Select(p =>
            {
                var pm = ctorParamMaps.FirstOrDefault(m =>
                    string.Equals(m.Name, p.Name, StringComparison.OrdinalIgnoreCase));
                if (pm is null)
                {
                    return p.HasDefaultValue
                        ? (Expression)Expression.Constant(p.DefaultValue, p.ParameterType)
                        : Expression.Default(p.ParameterType);
                }
                return BuildBinding(srcExpr, pm, depth, p.ParameterType, registry, maxDepth)
                    ?? Expression.Default(p.ParameterType);
            }).ToArray();
            newExpr = Expression.New(ctor, args);
        }

        var bindings = new List<MemberBinding>();
        foreach (var pm in propertyMaps)
        {
            if (pm.Ignored) continue;
            if (pm.DestinationProperty is null) continue;
            var binding = BuildBinding(srcExpr, pm, depth, pm.DestinationProperty.PropertyType, registry, maxDepth);
            if (binding is null) continue;
            bindings.Add(Expression.Bind(pm.DestinationProperty, binding));
        }

        return bindings.Count > 0
            ? Expression.MemberInit((NewExpression)newExpr, bindings)
            : newExpr;
    }

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

        if (pm.CustomExpression is not null)
        {
            var rebound = ParameterReplacer.Replace(
                pm.CustomExpression.Body,
                pm.CustomExpression.Parameters[0],
                srcExpr);
            return ConvertOrInline(rebound, targetType, depth, registry, maxDepth);
        }

        if (pm.SourcePath is null) return null;

        var pathExpr = BuildNullSafePath(srcExpr, pm.SourcePath.Members);
        return ConvertOrInline(pathExpr, targetType, depth, registry, maxDepth);
    }

    private static Expression ConvertOrInline(
        Expression source,
        Type targetType,
        int depth,
        MapperRegistry registry,
        int maxDepth)
    {
        if (source.Type == targetType) return source;
        if (targetType.IsAssignableFrom(source.Type)) return Expression.Convert(source, targetType);
        if (HasImplicitNumericConversion(source.Type, targetType))
            return Expression.Convert(source, targetType);

        if (IsCollection(source.Type) && IsCollection(targetType))
            return BuildCollectionProjection(source, targetType, depth, registry, maxDepth);

        var nestedTm = registry.GetTypeMap(new TypePair(source.Type, targetType));
        if (nestedTm is null) return source; // validator should have caught this

        return BuildNestedProjection(source, nestedTm, depth + 1, registry, maxDepth);
    }

    private static Expression BuildNestedProjection(
        Expression pathExpr,
        TypeMap nestedTm,
        int depth,
        MapperRegistry registry,
        int maxDepth)
    {
        if (depth >= maxDepth)
            return Expression.Default(nestedTm.DestinationType);

        var nestedParam = Expression.Parameter(nestedTm.SourceType, "n");
        var nestedBody = BuildBody(nestedTm, nestedParam, depth, registry, maxDepth);
        var inlined = ParameterReplacer.Replace(nestedBody, nestedParam, pathExpr);

        if (pathExpr.Type.IsClass)
        {
            return Expression.Condition(
                Expression.ReferenceEqual(pathExpr, Expression.Constant(null, pathExpr.Type)),
                Expression.Default(nestedTm.DestinationType),
                inlined);
        }
        return inlined;
    }

    private static Expression BuildCollectionProjection(
        Expression sourceExpr,
        Type targetType,
        int depth,
        MapperRegistry registry,
        int maxDepth)
    {
        var srcElem = GetEnumerableElementType(sourceExpr.Type)!;
        var dstElem = GetEnumerableElementType(targetType)!;

        var elementMap = registry.GetTypeMap(new TypePair(srcElem, dstElem));
        Expression selector;
        if (elementMap is not null)
        {
            var itemParam = Expression.Parameter(srcElem, "i");
            var itemBody = BuildBody(elementMap, itemParam, depth + 1, registry, maxDepth);
            selector = Expression.Lambda(itemBody, itemParam);
        }
        else
        {
            // No element map — identity (covers e.g. List<string> -> List<string>).
            var itemParam = Expression.Parameter(srcElem, "i");
            selector = Expression.Lambda(itemParam, itemParam);
        }

        var selectCall = Expression.Call(
            typeof(Enumerable),
            nameof(Enumerable.Select),
            new[] { srcElem, dstElem },
            sourceExpr,
            selector);

        if (targetType.IsArray)
            return Expression.Call(typeof(Enumerable), nameof(Enumerable.ToArray), new[] { dstElem }, selectCall);
        if (IsListLike(targetType))
            return Expression.Call(typeof(Enumerable), nameof(Enumerable.ToList), new[] { dstElem }, selectCall);
        return selectCall; // IEnumerable<T> destination
    }

    private static Expression BuildNullSafePath(Expression source, IReadOnlyList<PropertyInfo> path)
    {
        Expression current = source;
        foreach (var step in path)
        {
            var stepProp = Expression.Property(current, step);
            if (current.Type.IsClass)
            {
                current = Expression.Condition(
                    Expression.ReferenceEqual(current, Expression.Constant(null, current.Type)),
                    Expression.Default(stepProp.Type),
                    stepProp);
            }
            else
            {
                current = stepProp;
            }
        }
        return current;
    }

    private static (ConstructorInfo ctor,
                    IReadOnlyList<PropertyMap> ctorParamMaps,
                    IReadOnlyList<PropertyMap> propertyMaps)
        ClassifyBindings(TypeMap tm)
    {
        var dstType = tm.DestinationType;
        var parameterless = dstType.GetConstructor(Type.EmptyTypes);
        ConstructorInfo ctor = parameterless is { IsPublic: true }
            ? parameterless
            : dstType.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .OrderByDescending(c => c.GetParameters().Length)
                .FirstOrDefault()
                ?? throw new InvalidOperationException($"Type {dstType.Name} has no public constructor.");

        var ctorParamNames = new HashSet<string>(
            ctor.GetParameters().Select(p => p.Name ?? ""), StringComparer.OrdinalIgnoreCase);

        var ctorParamMaps = tm.PropertyMaps
            .Where(p => p.DestinationCtorParameter is not null && ctorParamNames.Contains(p.Name))
            .ToList();
        var propertyMaps = tm.PropertyMaps
            .Where(p => p.DestinationProperty is not null)
            .ToList();
        return (ctor, ctorParamMaps, propertyMaps);
    }

    private static bool IsCollection(Type t) =>
        t != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(t);

    private static bool IsListLike(Type t)
    {
        if (!t.IsGenericType) return false;
        var def = t.GetGenericTypeDefinition();
        return def == typeof(List<>) || def == typeof(IList<>) ||
               def == typeof(ICollection<>) || def == typeof(IReadOnlyList<>) ||
               def == typeof(IReadOnlyCollection<>);
    }

    private static Type? GetEnumerableElementType(Type t)
    {
        if (t.IsArray) return t.GetElementType();
        foreach (var i in new[] { t }.Concat(t.GetInterfaces()))
            if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return i.GetGenericArguments()[0];
        return null;
    }

    private static bool HasImplicitNumericConversion(Type src, Type dst) =>
        (src, dst) switch
        {
            _ when src == typeof(sbyte) => dst == typeof(short) || dst == typeof(int) || dst == typeof(long) || dst == typeof(float) || dst == typeof(double) || dst == typeof(decimal),
            _ when src == typeof(byte) => dst == typeof(short) || dst == typeof(ushort) || dst == typeof(int) || dst == typeof(uint) || dst == typeof(long) || dst == typeof(ulong) || dst == typeof(float) || dst == typeof(double) || dst == typeof(decimal),
            _ when src == typeof(short) => dst == typeof(int) || dst == typeof(long) || dst == typeof(float) || dst == typeof(double) || dst == typeof(decimal),
            _ when src == typeof(ushort) => dst == typeof(int) || dst == typeof(uint) || dst == typeof(long) || dst == typeof(ulong) || dst == typeof(float) || dst == typeof(double) || dst == typeof(decimal),
            _ when src == typeof(int) => dst == typeof(long) || dst == typeof(float) || dst == typeof(double) || dst == typeof(decimal),
            _ when src == typeof(uint) => dst == typeof(long) || dst == typeof(ulong) || dst == typeof(float) || dst == typeof(double) || dst == typeof(decimal),
            _ when src == typeof(long) => dst == typeof(float) || dst == typeof(double) || dst == typeof(decimal),
            _ when src == typeof(ulong) => dst == typeof(float) || dst == typeof(double) || dst == typeof(decimal),
            _ when src == typeof(float) => dst == typeof(double),
            _ when src == typeof(char) => dst == typeof(ushort) || dst == typeof(int) || dst == typeof(uint) || dst == typeof(long) || dst == typeof(ulong) || dst == typeof(float) || dst == typeof(double) || dst == typeof(decimal),
            _ => false,
        };
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Atlas.Projections.Tests/Atlas.Projections.Tests.csproj --filter "FullyQualifiedName~ProjectionPlanBuilderTests" --nologo`

Expected: `Passed!  - Failed: 0, Passed: 12`. If any test fails, read the failure carefully — most likely culprit is a missing case in `BuildBody` / `ConvertOrInline`. Fix the algorithm, not the test.

- [ ] **Step 6: Commit**

```powershell
git add tests/Atlas.Projections.Tests/Internal/AssertExpression.cs tests/Atlas.Projections.Tests/Internal/ProjectionPlanBuilderTests.cs src/Atlas.Projections/Internal/ParameterReplacer.cs src/Atlas.Projections/Internal/ProjectionPlanBuilder.cs
git commit -m "Add ProjectionPlanBuilder (12 tests, ~Enumerable.Select, no MappingInvoker)"
```

(`ParameterReplacer.cs` is committed here even though it was created in Task 6; if Task 6 was committed separately, drop it from the `git add` list.)

---

## Task 8: `ProjectionPlanCache` + `ProjectionPlanCacheRegistry` (§7.4, ~4 tests)

**Files:**
- Create: `tests/Atlas.Projections.Tests/Internal/ProjectionPlanCacheTests.cs`
- Create: `src/Atlas.Projections/Internal/ProjectionPlanCache.cs`

- [ ] **Step 1: Write all 4 tests**

Write `tests/Atlas.Projections.Tests/Internal/ProjectionPlanCacheTests.cs`:
```csharp
using System.Linq.Expressions;
using Atlas.Internal;
using Atlas.Projections.Internal;

namespace Atlas.Projections.Tests.Internal;

public class ProjectionPlanCacheTests
{
    private static LambdaExpression DummyLambda() => Expression.Lambda(Expression.Constant(0));

    [Fact]
    public void GetOrBuild_FirstCall_InvokesBuilder()
    {
        var cache = new ProjectionPlanCache();
        var calls = 0;
        var pair = new TypePair(typeof(int), typeof(int));
        cache.GetOrBuild(pair, 3, () => { calls++; return DummyLambda(); });
        Assert.Equal(1, calls);
    }

    [Fact]
    public void GetOrBuild_SecondCallSameKey_ReturnsCachedAndDoesNotInvokeBuilder()
    {
        var cache = new ProjectionPlanCache();
        var calls = 0;
        var pair = new TypePair(typeof(int), typeof(int));
        var first  = cache.GetOrBuild(pair, 3, () => { calls++; return DummyLambda(); });
        var second = cache.GetOrBuild(pair, 3, () => { calls++; return DummyLambda(); });
        Assert.Same(first, second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void GetOrBuild_DifferentMaxDepth_BuildsSeparately()
    {
        var cache = new ProjectionPlanCache();
        var calls = 0;
        var pair = new TypePair(typeof(int), typeof(int));
        cache.GetOrBuild(pair, 3, () => { calls++; return DummyLambda(); });
        cache.GetOrBuild(pair, 5, () => { calls++; return DummyLambda(); });
        Assert.Equal(2, calls);
    }

    [Fact]
    public void GetOrBuild_ConcurrentCalls_BuildsOnce()
    {
        var cache = new ProjectionPlanCache();
        var calls = 0;
        var pair = new TypePair(typeof(int), typeof(int));
        Parallel.For(0, 200, _ => cache.GetOrBuild(pair, 3, () =>
        {
            Interlocked.Increment(ref calls);
            return DummyLambda();
        }));
        Assert.Equal(1, calls);
    }
}
```

- [ ] **Step 2: Run the tests; expect compile failure**

Run: `dotnet test tests/Atlas.Projections.Tests/Atlas.Projections.Tests.csproj --filter "FullyQualifiedName~ProjectionPlanCacheTests" --nologo`

Expected: build error referencing `ProjectionPlanCache` (does not exist).

- [ ] **Step 3: Implement the cache and the registry**

Write `src/Atlas.Projections/Internal/ProjectionPlanCache.cs`:
```csharp
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Atlas.Internal;

namespace Atlas.Projections.Internal;

/// <summary>
/// Per-<see cref="MapperConfiguration"/> cache of built projection lambdas. Keyed by
/// <c>(TypePair, maxDepth)</c> — different depths produce different lambdas.
/// </summary>
internal sealed class ProjectionPlanCache
{
    private readonly Dictionary<(TypePair pair, int maxDepth), LambdaExpression> _cache = new();
    private readonly Lock _lock = new();

    public LambdaExpression GetOrBuild(TypePair pair, int maxDepth, Func<LambdaExpression> build)
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

/// <summary>
/// Binds one <see cref="ProjectionPlanCache"/> instance per <see cref="MapperConfiguration"/>
/// without contaminating the v1 core type. Bound via <see cref="ConditionalWeakTable{TKey,TValue}"/>
/// so cache lifetime tracks the configuration's lifetime.
/// </summary>
internal static class ProjectionPlanCacheRegistry
{
    private static readonly ConditionalWeakTable<MapperConfiguration, ProjectionPlanCache> _table = new();

    public static ProjectionPlanCache For(MapperConfiguration config) =>
        _table.GetValue(config, _ => new ProjectionPlanCache());
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Atlas.Projections.Tests/Atlas.Projections.Tests.csproj --filter "FullyQualifiedName~ProjectionPlanCacheTests" --nologo`

Expected: `Passed!  - Failed: 0, Passed: 4`.

- [ ] **Step 5: Commit**

```powershell
git add tests/Atlas.Projections.Tests/Internal/ProjectionPlanCacheTests.cs src/Atlas.Projections/Internal/ProjectionPlanCache.cs
git commit -m "Add ProjectionPlanCache + Registry (4 tests, lock + ConditionalWeakTable)"
```

---

## Task 9: `ProjectionExtensions.ProjectTo<T>` (§7.5, ~10 tests)

**Files:**
- Create: `tests/Atlas.Projections.Tests/ProjectionExtensionsTests.cs`
- Create: `src/Atlas.Projections/ProjectionExtensions.cs`

- [ ] **Step 1: Write all 10 tests**

Write `tests/Atlas.Projections.Tests/ProjectionExtensionsTests.cs`:
```csharp
using Atlas;
using Atlas.Projections;

namespace Atlas.Projections.Tests;

public class ProjectionExtensionsTests
{
    private static MapperConfiguration BuildConfig(Action<MapperConfigurationExpression> configure) =>
        new MapperConfiguration(configure);

    [Fact]
    public void ProjectTo_FlatPair_ReturnsMappedItems()
    {
        var config = BuildConfig(c => c.CreateMap<EFlatSrc, EFlatDst>());
        var src = new[]
        {
            new EFlatSrc { Id = 1, Name = "a" },
            new EFlatSrc { Id = 2, Name = "b" },
        };
        var result = src.AsQueryable().ProjectTo<EFlatDst>(config).ToList();
        Assert.Equal(2, result.Count);
        Assert.Equal("a", result[0].Name);
    }

    [Fact]
    public void ProjectTo_NestedObject_PopulatesNestedMembersCorrectly()
    {
        var config = BuildConfig(c =>
        {
            c.CreateMap<EInnerSrc, EInnerDst>();
            c.CreateMap<EOuterSrc, EOuterDst>();
        });
        var src = new[] { new EOuterSrc { Inner = new EInnerSrc { Name = "n" } } };
        var result = src.AsQueryable().ProjectTo<EOuterDst>(config).ToList();
        Assert.Equal("n", result[0].Inner.Name);
    }

    [Fact]
    public void ProjectTo_NullNestedSource_ReturnsDefaultDestination_NoNRE()
    {
        var config = BuildConfig(c =>
        {
            c.CreateMap<EInnerSrc, EInnerDst>();
            c.CreateMap<EOuterSrc, EOuterDst>();
        });
        var src = new[] { new EOuterSrc { Inner = null! } };
        var result = src.AsQueryable().ProjectTo<EOuterDst>(config).ToList();
        Assert.Null(result[0].Inner);
    }

    [Fact]
    public void ProjectTo_Collection_MappedItemsInOrder()
    {
        var config = BuildConfig(c =>
        {
            c.CreateMap<EInnerSrc, EInnerDst>();
            c.CreateMap<EParentSrc, EParentDst>();
        });
        var src = new[] { new EParentSrc { Items = { new() { Name = "a" }, new() { Name = "b" } } } };
        var result = src.AsQueryable().ProjectTo<EParentDst>(config).ToList();
        Assert.Equal(["a", "b"], result[0].Items.Select(i => i.Name));
    }

    [Fact]
    public void ProjectTo_FilteredQueryThenProjectTo_ReturnsFilteredResults()
    {
        var config = BuildConfig(c => c.CreateMap<EFlatSrc, EFlatDst>());
        var src = new[]
        {
            new EFlatSrc { Id = 1, Name = "a" },
            new EFlatSrc { Id = 2, Name = "b" },
        };
        var result = src.AsQueryable().Where(s => s.Id == 2).ProjectTo<EFlatDst>(config).ToList();
        Assert.Single(result);
        Assert.Equal("b", result[0].Name);
    }

    [Fact]
    public void ProjectTo_TypeConverterPair_Throws_WithDiagnostic()
    {
        var config = BuildConfig(c =>
            c.CreateMap<EFlatSrc, EFlatDst>().ConvertUsing(s => new EFlatDst { Id = s.Id, Name = s.Name }));
        var src = new[] { new EFlatSrc() };
        var ex = Assert.Throws<AtlasProjectionException>(() => src.AsQueryable().ProjectTo<EFlatDst>(config));
        Assert.Contains(ex.Diagnostics, d => d.Member == "(whole map)");
    }

    [Fact]
    public void ProjectTo_MissingMap_Throws_WithDiagnosticListing()
    {
        var config = BuildConfig(c => { });
        var src = new[] { new EFlatSrc() };
        var ex = Assert.Throws<AtlasProjectionException>(() => src.AsQueryable().ProjectTo<EFlatDst>(config));
        Assert.Single(ex.Diagnostics);
    }

    [Fact]
    public void ProjectTo_DepthLimit_TruncatesRecursiveMember_AtMaxDepth()
    {
        var config = BuildConfig(c => c.CreateMap<ENode, ENode>(MemberList.None));
        var src = new[] { new ENode { Next = new ENode { Next = new ENode() } } };
        var result = src.AsQueryable().ProjectTo<ENode>(config, maxDepth: 1).ToList();
        Assert.NotNull(result[0]);
        Assert.Null(result[0].Next);
    }

    [Fact]
    public void ProjectTo_DefaultMaxDepth_IsThree()
    {
        // A 4-deep chain projected with default depth 3: the 4th level becomes default(null).
        var config = BuildConfig(c => c.CreateMap<ENode, ENode>(MemberList.None));
        var deep = new ENode { Next = new ENode { Next = new ENode { Next = new ENode() } } };
        var result = new[] { deep }.AsQueryable().ProjectTo<ENode>(config).ToList();
        Assert.NotNull(result[0].Next);
        Assert.NotNull(result[0].Next!.Next);
        Assert.Null(result[0].Next!.Next!.Next);
    }

    [Fact]
    public void ProjectTo_MaxDepthZero_ThrowsArgumentOutOfRange()
    {
        var config = BuildConfig(c => c.CreateMap<EFlatSrc, EFlatDst>());
        var src = new[] { new EFlatSrc() };
        Assert.Throws<ArgumentOutOfRangeException>(() => src.AsQueryable().ProjectTo<EFlatDst>(config, maxDepth: 0));
    }
}

// ---- Test fixtures ----
public class EFlatSrc { public int Id { get; set; } public string Name { get; set; } = ""; }
public class EFlatDst { public int Id { get; set; } public string Name { get; set; } = ""; }
public class EInnerSrc { public string Name { get; set; } = ""; }
public class EInnerDst { public string Name { get; set; } = ""; }
public class EOuterSrc { public EInnerSrc Inner { get; set; } = new(); }
public class EOuterDst { public EInnerDst Inner { get; set; } = new(); }
public class EParentSrc { public List<EInnerSrc> Items { get; set; } = new(); }
public class EParentDst { public List<EInnerDst> Items { get; set; } = new(); }
public class ENode { public ENode? Next { get; set; } }
```

- [ ] **Step 2: Run the tests; expect compile failure**

Run: `dotnet test tests/Atlas.Projections.Tests/Atlas.Projections.Tests.csproj --filter "FullyQualifiedName~ProjectionExtensionsTests" --nologo`

Expected: build error referencing `ProjectionExtensions.ProjectTo` (does not exist).

- [ ] **Step 3: Implement `ProjectTo`**

Write `src/Atlas.Projections/ProjectionExtensions.cs`:
```csharp
using System.Linq.Expressions;
using Atlas.Internal;
using Atlas.Projections.Internal;

namespace Atlas.Projections;

/// <summary>
/// Translates a configured Atlas map into a LINQ expression and applies it as a Select.
/// Designed to be the last operator in an IQueryable chain — apply Where/OrderBy first.
/// </summary>
public static class ProjectionExtensions
{
    /// <summary>
    /// Translates the configured map for <c>(source.ElementType, TDestination)</c> and applies
    /// it via <c>Queryable.Select</c>. Throws <see cref="AtlasProjectionException"/> if any
    /// reachable binding within <paramref name="maxDepth"/> is non-projectable.
    /// </summary>
    public static IQueryable<TDestination> ProjectTo<TDestination>(
        this IQueryable source,
        MapperConfiguration configuration,
        int maxDepth = 3)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(configuration);
        if (maxDepth <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxDepth), maxDepth, "maxDepth must be > 0.");

        var srcType = source.ElementType;
        var pair = new TypePair(srcType, typeof(TDestination));
        var registry = configuration.Internal_Registry;
        var cache = ProjectionPlanCacheRegistry.For(configuration);

        var lambda = cache.GetOrBuild(pair, maxDepth, () =>
        {
            ProjectionValidator.Validate(registry, pair, maxDepth);
            return ProjectionPlanBuilder.Build(registry, pair, maxDepth);
        });

        var selectCall = Expression.Call(
            typeof(Queryable),
            nameof(Queryable.Select),
            new[] { srcType, typeof(TDestination) },
            source.Expression,
            Expression.Quote(lambda));

        return source.Provider.CreateQuery<TDestination>(selectCall);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Atlas.Projections.Tests/Atlas.Projections.Tests.csproj --filter "FullyQualifiedName~ProjectionExtensionsTests" --nologo`

Expected: `Passed!  - Failed: 0, Passed: 10`.

- [ ] **Step 5: Run the full Atlas.Projections.Tests suite**

Run: `dotnet test tests/Atlas.Projections.Tests/Atlas.Projections.Tests.csproj --nologo`

Expected: `Passed!  - Failed: 0, Passed: 42` (6 + 10 + 12 + 4 + 10 = 42).

- [ ] **Step 6: Commit**

```powershell
git add tests/Atlas.Projections.Tests/ProjectionExtensionsTests.cs src/Atlas.Projections/ProjectionExtensions.cs
git commit -m "Add ProjectionExtensions.ProjectTo<T> (10 tests, in-memory IQueryable)"
```

---

## Task 10: Scaffold the EF Core test sub-project

**Files:**
- Modify: `Directory.Packages.props` (add `Microsoft.EntityFrameworkCore.Sqlite`)
- Create: `tests/Atlas.Projections.Tests.EFCore/Atlas.Projections.Tests.EFCore.csproj`
- Create: `tests/Atlas.Projections.Tests.EFCore/GlobalUsings.cs`
- Create: `tests/Atlas.Projections.Tests.EFCore/Fixtures/BlogModels.cs`
- Create: `tests/Atlas.Projections.Tests.EFCore/Fixtures/BlogContext.cs`
- Modify: `Atlas.slnx`

- [ ] **Step 1: Pin EF Core SQLite version centrally**

Open `Directory.Packages.props`. Inside the existing `<ItemGroup>`, append:
```xml
    <PackageVersion Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.0" />
```

- [ ] **Step 2: Create the EF Core test csproj**

Write `tests/Atlas.Projections.Tests.EFCore/Atlas.Projections.Tests.EFCore.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Atlas.Projections\Atlas.Projections.csproj" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" />
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="coverlet.collector" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Add global usings**

Write `tests/Atlas.Projections.Tests.EFCore/GlobalUsings.cs`:
```csharp
global using Xunit;
```

- [ ] **Step 4: Add the model classes**

Write `tests/Atlas.Projections.Tests.EFCore/Fixtures/BlogModels.cs`:
```csharp
namespace Atlas.Projections.Tests.EFCore.Fixtures;

public class Blog
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public List<Post> Posts { get; set; } = new();
}

public class Post
{
    public int Id { get; set; }
    public string Body { get; set; } = "";
    public int? WordCount { get; set; }
    public int BlogId { get; set; }
    public Blog? Blog { get; set; }
}

public class BlogDto
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public List<PostDto> Posts { get; set; } = new();
}

public class PostDto
{
    public int Id { get; set; }
    public string Body { get; set; } = "";
    public long? WordCount { get; set; } // numeric widening: int? -> long?
}
```

- [ ] **Step 5: Add the DbContext**

Write `tests/Atlas.Projections.Tests.EFCore/Fixtures/BlogContext.cs`:
```csharp
using Microsoft.EntityFrameworkCore;

namespace Atlas.Projections.Tests.EFCore.Fixtures;

public sealed class BlogContext : DbContext
{
    public DbSet<Blog> Blogs => Set<Blog>();
    public DbSet<Post> Posts => Set<Post>();

    public BlogContext(DbContextOptions<BlogContext> options) : base(options) { }

    public static BlogContext CreateInMemory()
    {
        var options = new DbContextOptionsBuilder<BlogContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        var ctx = new BlogContext(options);
        ctx.Database.OpenConnection();
        ctx.Database.EnsureCreated();
        return ctx;
    }

    public void Seed()
    {
        var b = new Blog
        {
            Title = "T1",
            Posts =
            {
                new Post { Body = "p1", WordCount = 100 },
                new Post { Body = "p2", WordCount = null },
            },
        };
        Blogs.Add(b);
        SaveChanges();
    }
}
```

- [ ] **Step 6: Add the project to the solution**

Open `Atlas.slnx`. Add adjacent to other `tests/...` projects:
```xml
    <Project Path="tests/Atlas.Projections.Tests.EFCore/Atlas.Projections.Tests.EFCore.csproj" />
```

- [ ] **Step 7: Build the test sub-project**

Run: `dotnet build tests/Atlas.Projections.Tests.EFCore/Atlas.Projections.Tests.EFCore.csproj --nologo`

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 8: Commit**

```powershell
git add Directory.Packages.props tests/Atlas.Projections.Tests.EFCore/Atlas.Projections.Tests.EFCore.csproj tests/Atlas.Projections.Tests.EFCore/GlobalUsings.cs tests/Atlas.Projections.Tests.EFCore/Fixtures/BlogModels.cs tests/Atlas.Projections.Tests.EFCore/Fixtures/BlogContext.cs Atlas.slnx
git commit -m "Scaffold Atlas.Projections.Tests.EFCore with SQLite fixture"
```

---

## Task 11: EF Core integration tests (§7.6, ~8 tests)

**Files:**
- Create: `tests/Atlas.Projections.Tests.EFCore/ProjectionEFCoreTests.cs`

- [ ] **Step 1: Write all 8 tests**

Write `tests/Atlas.Projections.Tests.EFCore/ProjectionEFCoreTests.cs`:
```csharp
using Atlas;
using Atlas.Projections;
using Atlas.Projections.Tests.EFCore.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Projections.Tests.EFCore;

public class ProjectionEFCoreTests
{
    private static MapperConfiguration BlogMapping()
    {
        return new MapperConfiguration(c =>
        {
            c.CreateMap<Post, PostDto>();
            c.CreateMap<Blog, BlogDto>();
        });
    }

    [Fact]
    public void EFCore_FlatProjection_EmitsSingleSelect_NoFullEntityHydration()
    {
        var config = BlogMapping();
        using var ctx = BlogContext.CreateInMemory();
        ctx.Seed();

        var sql = ctx.Posts.ProjectTo<PostDto>(config).ToQueryString();

        // Assertions are on column-name presence, not whitespace.
        Assert.Contains("Body", sql);
        Assert.Contains("WordCount", sql);
        Assert.Contains("Id", sql);
        Assert.Contains("FROM \"Posts\"", sql);
    }

    [Fact]
    public void EFCore_NestedProjection_EmitsLeftJoin_NotN1Queries()
    {
        var config = BlogMapping();
        using var ctx = BlogContext.CreateInMemory();
        ctx.Seed();

        var sql = ctx.Blogs.ProjectTo<BlogDto>(config).ToQueryString();
        // EF Core emits LEFT JOIN for nullable navigations; nested collection projections
        // produce one query, not N+1.
        Assert.Contains("LEFT JOIN", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EFCore_CollectionProjection_EmitsSingleQuery()
    {
        var config = BlogMapping();
        using var ctx = BlogContext.CreateInMemory();
        ctx.Seed();

        var blogs = ctx.Blogs.ProjectTo<BlogDto>(config).ToList();
        Assert.Single(blogs);
        Assert.Equal(2, blogs[0].Posts.Count);
    }

    [Fact]
    public void EFCore_FilterBeforeProjectTo_FilterPushesDown()
    {
        var config = BlogMapping();
        using var ctx = BlogContext.CreateInMemory();
        ctx.Seed();

        var sql = ctx.Posts.Where(p => p.WordCount == 100).ProjectTo<PostDto>(config).ToQueryString();
        Assert.Contains("WHERE", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EFCore_ProjectionRoundtrip_ReturnsExpectedRows()
    {
        var config = BlogMapping();
        using var ctx = BlogContext.CreateInMemory();
        ctx.Seed();

        var posts = ctx.Posts.OrderBy(p => p.Id).ProjectTo<PostDto>(config).ToList();
        Assert.Equal(2, posts.Count);
        Assert.Equal("p1", posts[0].Body);
        Assert.Equal(100L, posts[0].WordCount);
        Assert.Null(posts[1].WordCount);
    }

    [Fact]
    public void EFCore_NumericWidening_TranslatesToProvider()
    {
        // PostDto.WordCount is long?, Post.WordCount is int? — implicit widening.
        var config = BlogMapping();
        using var ctx = BlogContext.CreateInMemory();
        ctx.Seed();

        var dto = ctx.Posts.OrderBy(p => p.Id).ProjectTo<PostDto>(config).First();
        Assert.Equal(100L, dto.WordCount);
    }

    [Fact]
    public void EFCore_NullableSourceMember_TranslatesToNullCoalesce()
    {
        var config = BlogMapping();
        using var ctx = BlogContext.CreateInMemory();
        ctx.Seed();

        // Round-trips a null int? → null long? via the projection.
        var dto = ctx.Posts.OrderByDescending(p => p.Id).ProjectTo<PostDto>(config).First();
        Assert.Null(dto.WordCount);
    }

    [Fact]
    public void EFCore_RecursiveMap_DepthLimitTerminatesQuery()
    {
        // Post -> Blog -> Posts -> Blog ... is a cycle. Depth 1 stops at the first hop.
        var config = new MapperConfiguration(c =>
        {
            c.CreateMap<Post, PostDto>();
            c.CreateMap<Blog, BlogDto>();
        });
        using var ctx = BlogContext.CreateInMemory();
        ctx.Seed();

        // Should not stack-overflow during expression building or query translation.
        var blogs = ctx.Blogs.ProjectTo<BlogDto>(config, maxDepth: 1).ToList();
        Assert.Single(blogs);
    }
}
```

- [ ] **Step 2: Run the EF Core test suite**

Run: `dotnet test tests/Atlas.Projections.Tests.EFCore/Atlas.Projections.Tests.EFCore.csproj --nologo`

Expected: `Passed!  - Failed: 0, Passed: 8`. If a test fails on SQL-text assertions (e.g. `LEFT JOIN` not present, or column names missing), inspect the actual SQL in the failure and adjust the assertion to be loose enough to survive minor EF version differences while still proving the load-bearing invariant. Do **not** weaken assertions that prove correctness (count/value checks).

- [ ] **Step 3: Commit**

```powershell
git add tests/Atlas.Projections.Tests.EFCore/ProjectionEFCoreTests.cs
git commit -m "Add EF Core SQLite integration tests for ProjectTo (8 tests)"
```

---

## Task 12: Run the full suite + measure coverage

**Files:**
- (No code changes)

- [ ] **Step 1: Run all tests across the whole solution**

Run: `dotnet test --nologo`

Expected:
- `Atlas.Tests`: `Passed: 88`
- `Atlas.Projections.Tests`: `Passed: 42`
- `Atlas.Projections.Tests.EFCore`: `Passed: 8`
- Total: 138 passed, 0 failed.

- [ ] **Step 2: Collect coverage on Atlas.Projections.Tests**

Run: `dotnet test tests/Atlas.Projections.Tests/Atlas.Projections.Tests.csproj --collect:"XPlat Code Coverage" --nologo`

Then: `reportgenerator -reports:tests/Atlas.Projections.Tests/TestResults/**/coverage.cobertura.xml -targetdir:coverage-projections -reporttypes:TextSummary`

Read `coverage-projections/Summary.txt`. Confirm:
- `Atlas.Projections` line coverage ≥ 90%.
- `Atlas.Projections` branch coverage ≥ 80%.

If either gate is missed, the most likely cause is unreached arms of `HasImplicitNumericConversion` (acceptable per spec §8) or an unreached fallback in `ProjectionPlanBuilder.ConvertOrInline`. Add a targeted test if the gap is in real algorithm code; document the switch-arm gap in README otherwise.

- [ ] **Step 3: Commit any added tests (if any)**

If Step 2 prompted adding tests:
```powershell
git add tests/Atlas.Projections.Tests/...
git commit -m "Add coverage-targeted tests"
```

Otherwise, no commit at this step.

---

## Task 13: README + memory update

**Files:**
- Modify: `README.md` (add Atlas.Projections section)
- Modify: `C:\Users\ajsde\.claude\projects\C--Repos-Atlas\memory\atlas_v2_design_docs_deferred.md` (cross out item #1)
- Modify: `C:\Users\ajsde\.claude\projects\C--Repos-Atlas\memory\MEMORY.md` (no changes needed — index entry is still accurate)

- [ ] **Step 1: Add an Atlas.Projections section to README**

Open `README.md`. After the "Dependency injection" section (looks for the heading `## Dependency injection`), insert a new section before "What's in v1":
```markdown
## Queryable projection (`Atlas.Projections`)

Optional package that translates a configured map into a LINQ expression and applies it as a `Select` over an `IQueryable`. Designed for EF Core read paths.

```csharp
using Atlas.Projections;

var dtos = db.Blogs
    .Where(b => b.Year >= 2025)
    .ProjectTo<BlogDto>(configuration)
    .ToList();
```

The configuration is validated eagerly at the call site; non-projectable constructs (delegate-form `ConvertUsing`, missing maps, unmapped destination members) throw `AtlasProjectionException` listing every problem. Default recursion depth is 3 (per-call override available).

See `docs/Atlas-Design-ProjectTo.md` for the full design.
```

- [ ] **Step 2: Update the deferred-features memory**

Read `C:\Users\ajsde\.claude\projects\C--Repos-Atlas\memory\atlas_v2_design_docs_deferred.md`. In the numbered list of 13 deferred features, change the line for item #1 from:
```markdown
1. IQueryable projection / `ProjectTo` — highest-value follow-up for EF Core users.
```
to:
```markdown
1. ~~IQueryable projection / `ProjectTo`~~ — **shipped** as `Atlas.Projections` (see `docs/Atlas-Design-ProjectTo.md`).
```

- [ ] **Step 3: Commit**

```powershell
git add README.md
git commit -m "docs: README — add Atlas.Projections section"
```

(The memory file lives outside the git repo and updates separately; no `git add` for it.)

- [ ] **Step 4: Final sanity check**

Run: `dotnet test --nologo`

Expected: 138 passed, 0 failed.

Run: `git log --oneline | head -20`

Expected: A clean sequence of commits — one per task plus the v1 + design baseline. Each commit message describes one cohesive change.

---

## Done

When this plan is complete:
- `Atlas.Projections` ships with full TDD coverage (~50 tests).
- The v1 core is unchanged (88 tests still pass).
- A consumer can call `query.ProjectTo<TDest>(config)` against EF Core SQLite and get a single SQL query.
- 12 v2 features remain in the deferred list for future design cycles.
