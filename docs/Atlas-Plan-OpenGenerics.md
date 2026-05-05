# Atlas v2 Open Generics — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add open-generic class maps so a single `cfg.CreateMap(typeof(Source<>), typeof(Destination<>))` applies to every closed instantiation at runtime via lazy materialization.

**Architecture:** New non-generic `CreateMap(Type, Type)` overloads on `MapperConfigurationExpression` and `MapperProfile` store `OpenGenericTypeMap` templates separately from closed `TypeMap`s. `MapperRegistry.GetTypeMap` gains a fallback path: on closed-pair miss, scan open registrations for an arity-matching template with matching generic-type-definitions, then materialize a closed `TypeMap` via `ConventionEngine` + `TransformerResolver` and cache via `ConcurrentDictionary.GetOrAdd`. Both `Atlas.Map<>()` and `Atlas.Projections.ProjectTo<>()` get open-generic support automatically through this single insertion point.

**Tech Stack:** .NET 10, C# 14 (preview), xUnit v3 (no FluentAssertions — `Assert.X()` only), `System.Collections.Concurrent.ConcurrentDictionary`, `System.Linq.Expressions`, EF Core (in-memory + SQLite for projection E2E).

**Branch & merge:** Cut `feat/open-generics` from `main` HEAD (currently `f489781`, the design-doc commit). All 8 implementation tasks land on this branch; final review then PR to `main`.

**Specs to read alongside this plan:**
- `C:\Repos\Atlas\docs\Atlas-Design-OpenGenerics.md` — every code section in this plan implements something specified there.
- `C:\Repos\Atlas\docs\Atlas-Plan-NullSubstitution.md` — structural template for this plan (same task rhythm).

---

## File Map

**Production code modified:**
- `src/Atlas/Internal/OpenGenericTypeMap.cs` (CREATE) — new internal record (registration template).
- `src/Atlas/MapperConfigurationExpression.cs` — add `_openGenericMaps` field + `CreateMap(Type, Type)` overload + `GetOpenGenericMaps()` accessor + `AddProfile` propagation.
- `src/Atlas/MapperProfile.cs` — same set of additions, scoped to profile.
- `src/Atlas/Internal/MapperRegistry.cs` — change `_typeMaps` from `Dictionary` to `ConcurrentDictionary`; add `_openGenericMaps`/`_globalTransformers`/`_conventionOptions` fields; extend constructor; add `GetTypeMap` fallback + `FindMatchingOpenGenericTemplate` + `MaterializeClosed` private methods.
- `src/Atlas/MapperConfiguration.cs` — new `_openGenericMaps`/`_globalTransformers` fields; both constructors pass new params to `MapperRegistry`.
- `README.md` — add Open Generics section + remove from "Deferred to v2" list + refresh test count.

**Production code NOT modified (deliberate per design §2.2):**
- `src/Atlas/Internal/TypeMap.cs` — unchanged.
- `src/Atlas/Internal/PropertyMap.cs` — unchanged.
- `src/Atlas/Internal/ConventionEngine.cs`, `ReverseMapMirror.cs`, `TransformerResolver.cs`, `InheritanceMerger.cs` — unchanged. (Materialization invokes `ConventionEngine.ResolveMissingMembers` + `TransformerResolver.Resolve` but doesn't modify them.)
- `src/Atlas/Internal/ConfigurationValidator.cs` — unchanged. Open-generic templates are excluded from validation by virtue of not being in `_typeMaps`.
- `src/Atlas/Internal/ExecutionPlanBuilder.cs` — unchanged. Materialized closed maps compile identically to manually-registered closed maps.
- `src/Atlas.Projections/Internal/ProjectionPlanBuilder.cs` — unchanged. `ProjectionPlanBuilder.Build` calls `registry.GetTypeMap(...)` which now does open-generic lookup-and-materialize.
- `src/Atlas.Projections/Internal/ProjectionCompatibility.cs` — unchanged. Materialized closed maps are projectable by default.

**Test code added:**
- `tests/Atlas.Tests/Internal/OpenGenericTypeMapTests.cs` — 3 tests (`Matches` predicate behavior).
- `tests/Atlas.Tests/MapperConfigurationExpressionOpenGenericTests.cs` — 5 tests (registration + validation errors + AddProfile propagation).
- `tests/Atlas.Tests/MapperProfileOpenGenericTests.cs` — 2 tests (profile registration + profile transformer applies to materialized pairs).
- `tests/Atlas.Tests/Internal/MapperRegistryOpenGenericTests.cs` — 5 tests (lookup-and-materialize behavior + cache + closed-pair-precedence).
- `tests/Atlas.Tests/MapperOpenGenericTests.cs` — 4 end-to-end tests via real `IMapper`.
- `tests/Atlas.Projections.Tests/Internal/ProjectionPlanBuilderOpenGenericTests.cs` — 2 tests (projection codegen for materialized pairs).
- `tests/Atlas.Projections.Tests.EFCore/ProjectTo_OpenGenericTests.cs` — 2 EF Core SQLite E2E tests (with new generic-typed fixture).
- `tests/Atlas.Tests/MapperConfigurationOpenGenericValidationTests.cs` — 2 tests (validator excludes open templates).

**Total new tests: 25.** Test baseline goes from **465 → 490**.

---

## Task 0 — Branch setup (5 min, controller-only)

Cut the feature branch and verify clean starting state.

**Files:** none modified.

- [ ] **Step 1: Verify on main, clean tree, design committed.**

```bash
cd C:/Repos/Atlas
git status
git log --oneline -3
```
Expected: `On branch main`, working tree clean, top commit is `f489781 docs: design for Atlas v2 #9 Open Generics`.

- [ ] **Step 2: Cut feature branch.**

```bash
git checkout -b feat/open-generics
```
Expected: `Switched to a new branch 'feat/open-generics'`.

- [ ] **Step 3: Confirm full build + tests still green on the branch.**

```bash
dotnet build C:/Repos/Atlas/Atlas.slnx -c Debug
dotnet test C:/Repos/Atlas/Atlas.slnx -c Debug --no-build
```
Expected: build succeeds; **465** tests pass across all test projects (386 Atlas.Tests + 67 Atlas.Projections.Tests + 12 Atlas.Projections.Tests.EFCore).

If the count differs, **stop and reconcile** — the baseline must match before adding new tests.

---

## Task 1 — `OpenGenericTypeMap` record

Create the new internal record that represents an open-generic registration template.

**Files:**
- Create: `src/Atlas/Internal/OpenGenericTypeMap.cs`
- Create: `tests/Atlas.Tests/Internal/OpenGenericTypeMapTests.cs`

**Allowlist for the implementer:** ONLY the two files above.

- [ ] **Step 1: Write the failing tests.**

Create `tests/Atlas.Tests/Internal/OpenGenericTypeMapTests.cs`:

```csharp
using Atlas.Internal;

namespace Atlas.Tests.Internal;

public class OpenGenericTypeMapTests
{
    public class Wrapper<T> { public T Value { get; set; } = default!; }
    public class WrapperDto<T> { public T Value { get; set; } = default!; }
    public class Other<T> { public T Value { get; set; } = default!; }

    [Fact]
    public void Matches_ArityMatchingClosedPair_ReturnsTrue()
    {
        var template = new OpenGenericTypeMap(
            typeof(Wrapper<>), typeof(WrapperDto<>),
            MemberList.None,
            "CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>))");

        var closedPair = new TypePair(typeof(Wrapper<int>), typeof(WrapperDto<int>));

        Assert.True(template.Matches(closedPair));
    }

    [Fact]
    public void Matches_DifferentGenericTypeDefinitions_ReturnsFalse()
    {
        var template = new OpenGenericTypeMap(
            typeof(Wrapper<>), typeof(WrapperDto<>),
            MemberList.None,
            "CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>))");

        // Source GTD differs.
        var pair1 = new TypePair(typeof(Other<int>), typeof(WrapperDto<int>));
        Assert.False(template.Matches(pair1));

        // Destination GTD differs.
        var pair2 = new TypePair(typeof(Wrapper<int>), typeof(Other<int>));
        Assert.False(template.Matches(pair2));
    }

    [Fact]
    public void Matches_NonConstructedGenericType_ReturnsFalse()
    {
        var template = new OpenGenericTypeMap(
            typeof(Wrapper<>), typeof(WrapperDto<>),
            MemberList.None,
            "CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>))");

        // Source is non-generic.
        var pair1 = new TypePair(typeof(string), typeof(WrapperDto<int>));
        Assert.False(template.Matches(pair1));

        // Destination is non-generic.
        var pair2 = new TypePair(typeof(Wrapper<int>), typeof(string));
        Assert.False(template.Matches(pair2));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj --filter "FullyQualifiedName~Atlas.Tests.Internal.OpenGenericTypeMapTests"
```
Expected: build error (`'OpenGenericTypeMap' does not exist`).

- [ ] **Step 3: Create `OpenGenericTypeMap`.**

Create `src/Atlas/Internal/OpenGenericTypeMap.cs`:

```csharp
namespace Atlas.Internal;

/// <summary>
/// Registration template for an open-generic class map. Different shape from
/// <see cref="TypeMap"/> — has no <see cref="TypeMap.PropertyMaps"/>; those are derived
/// per closed pair via the convention engine at materialization time.
/// </summary>
internal sealed class OpenGenericTypeMap
{
    public Type SourceTypeDefinition { get; }
    public Type DestinationTypeDefinition { get; }
    public MemberList MemberList { get; }
    public string RegistrationOrigin { get; }
    public MapperProfile? OriginatingProfile { get; }

    public OpenGenericTypeMap(
        Type sourceTypeDefinition,
        Type destinationTypeDefinition,
        MemberList memberList,
        string registrationOrigin,
        MapperProfile? originatingProfile = null)
    {
        SourceTypeDefinition = sourceTypeDefinition;
        DestinationTypeDefinition = destinationTypeDefinition;
        MemberList = memberList;
        RegistrationOrigin = registrationOrigin;
        OriginatingProfile = originatingProfile;
    }

    /// <summary>
    /// True when this template can materialize a <see cref="TypeMap"/> for the given
    /// closed pair — i.e., both source and destination are constructed-generic types
    /// whose generic-type-definitions match the registered template.
    /// </summary>
    public bool Matches(TypePair closedPair)
    {
        if (!closedPair.Source.IsConstructedGenericType) return false;
        if (!closedPair.Destination.IsConstructedGenericType) return false;
        return closedPair.Source.GetGenericTypeDefinition() == SourceTypeDefinition
            && closedPair.Destination.GetGenericTypeDefinition() == DestinationTypeDefinition;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj --filter "FullyQualifiedName~Atlas.Tests.Internal.OpenGenericTypeMapTests"
```
Expected: 3/3 PASS.

- [ ] **Step 5: Run the full Atlas.Tests project to confirm no regressions.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj
```
Expected: 386 pre-existing + 3 new = 389 PASS.

- [ ] **Step 6: Commit.**

```bash
git add src/Atlas/Internal/OpenGenericTypeMap.cs tests/Atlas.Tests/Internal/OpenGenericTypeMapTests.cs
git commit -m "$(cat <<'EOF'
OpenGenericTypeMap record for open-generic registration templates (3 tests)

New internal record with SourceTypeDefinition, DestinationTypeDefinition,
MemberList, RegistrationOrigin, OriginatingProfile, and a Matches(TypePair)
predicate. Different shape from TypeMap — no PropertyMaps; those are derived
per closed pair via the convention engine at materialization time.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 2 — `MapperConfigurationExpression.CreateMap(Type, Type)` overload

Add the non-generic `CreateMap(Type, Type, MemberList)` overload to the root config, plus `_openGenericMaps` storage, `GetOpenGenericMaps()` accessor, and `AddProfile` propagation.

**Files:**
- Modify: `src/Atlas/MapperConfigurationExpression.cs`
- Create: `tests/Atlas.Tests/MapperConfigurationExpressionOpenGenericTests.cs`

**Allowlist for the implementer:** ONLY the two files above.

- [ ] **Step 1: Write the failing tests.**

Create `tests/Atlas.Tests/MapperConfigurationExpressionOpenGenericTests.cs`:

```csharp
using Atlas.Internal;

namespace Atlas.Tests;

public class MapperConfigurationExpressionOpenGenericTests
{
    public class Wrapper<T> { public T Value { get; set; } = default!; }
    public class WrapperDto<T> { public T Value { get; set; } = default!; }
    public class Pair<T1, T2> { }

    public sealed class TestProfile : MapperProfile
    {
        public TestProfile() => CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>));
    }

    [Fact]
    public void CreateMap_StoresOpenGenericRegistration()
    {
        var expr = new MapperConfigurationExpression();

        expr.CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>));

        var registrations = expr.GetOpenGenericMaps();
        Assert.Single(registrations);
        Assert.Equal(typeof(Wrapper<>), registrations[0].SourceTypeDefinition);
        Assert.Equal(typeof(WrapperDto<>), registrations[0].DestinationTypeDefinition);
        Assert.Equal(MemberList.None, registrations[0].MemberList);
    }

    [Fact]
    public void CreateMap_NotAGenericTypeDefinition_ThrowsAtlasConfigurationException()
    {
        var expr = new MapperConfigurationExpression();

        var ex = Assert.Throws<AtlasConfigurationException>(() =>
            expr.CreateMap(typeof(Wrapper<int>), typeof(WrapperDto<>)));

        Assert.Contains("Source", ex.Message);
        Assert.Contains("open generic type definition", ex.Message);
    }

    [Fact]
    public void CreateMap_ArityMismatch_ThrowsAtlasConfigurationException()
    {
        var expr = new MapperConfigurationExpression();

        var ex = Assert.Throws<AtlasConfigurationException>(() =>
            expr.CreateMap(typeof(Wrapper<>), typeof(Pair<,>)));

        Assert.Contains("arity mismatch", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateMap_NullArgs_ThrowsArgumentNullException()
    {
        var expr = new MapperConfigurationExpression();

        Assert.Throws<ArgumentNullException>(() => expr.CreateMap(null!, typeof(WrapperDto<>)));
        Assert.Throws<ArgumentNullException>(() => expr.CreateMap(typeof(Wrapper<>), null!));
    }

    [Fact]
    public void AddProfile_PropagatesOpenGenericRegistrations()
    {
        var expr = new MapperConfigurationExpression();
        expr.AddProfile<TestProfile>();

        var registrations = expr.GetOpenGenericMaps();
        Assert.Single(registrations);
        Assert.Equal(typeof(Wrapper<>), registrations[0].SourceTypeDefinition);
        Assert.NotNull(registrations[0].OriginatingProfile);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj --filter "FullyQualifiedName~Atlas.Tests.MapperConfigurationExpressionOpenGenericTests"
```
Expected: build error (`'MapperConfigurationExpression' does not contain a definition for 'CreateMap' [non-generic overload]` or `GetOpenGenericMaps`).

- [ ] **Step 3: Add the field, the method, and the accessor.**

In `src/Atlas/MapperConfigurationExpression.cs`, add a new private field after the existing `_typeMaps` declaration:

```csharp
    private readonly List<OpenGenericTypeMap> _openGenericMaps = new();
```

Add the new `CreateMap(Type, Type, MemberList)` method after the existing `CreateMap<TSource, TDestination>` method:

```csharp
    /// <summary>
    /// Registers an open-generic class map. A single registration applies to every closed
    /// instantiation at runtime via lazy materialization.
    /// </summary>
    /// <param name="sourceType">An open generic type definition (e.g., <c>typeof(Source&lt;&gt;)</c>).</param>
    /// <param name="destinationType">An open generic type definition with the same arity.</param>
    /// <param name="memberList">Validation policy for materialized closed pairs. Default <see cref="MemberList.None"/>.</param>
    /// <exception cref="AtlasConfigurationException">
    /// Thrown if either type is not an open generic type definition, or if the source and
    /// destination have different generic arities.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// Thrown if <paramref name="sourceType"/> or <paramref name="destinationType"/> is null.
    /// </exception>
    public void CreateMap(Type sourceType, Type destinationType,
                          MemberList memberList = MemberList.None)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(sourceType);
        ArgumentNullException.ThrowIfNull(destinationType);

        if (!sourceType.IsGenericTypeDefinition)
            throw new AtlasConfigurationException(new List<ConfigurationError>
            {
                new(sourceType, destinationType, "(register)",
                    $"Source must be an open generic type definition; got '{sourceType.Name}'. " +
                    "Use CreateMap<TSource, TDestination>() for closed types.")
            });

        if (!destinationType.IsGenericTypeDefinition)
            throw new AtlasConfigurationException(new List<ConfigurationError>
            {
                new(sourceType, destinationType, "(register)",
                    $"Destination must be an open generic type definition; got '{destinationType.Name}'. " +
                    "Use CreateMap<TSource, TDestination>() for closed types.")
            });

        var sourceArity = sourceType.GetGenericArguments().Length;
        var destArity = destinationType.GetGenericArguments().Length;
        if (sourceArity != destArity)
            throw new AtlasConfigurationException(new List<ConfigurationError>
            {
                new(sourceType, destinationType, "(register)",
                    $"Generic arity mismatch: source has {sourceArity} type parameter(s), destination has {destArity}.")
            });

        var openMap = new OpenGenericTypeMap(
            sourceType,
            destinationType,
            memberList,
            $"CreateMap(typeof({sourceType.Name}), typeof({destinationType.Name}))");

        _openGenericMaps.Add(openMap);
    }

    /// <summary>Read-only snapshot of registered open-generic templates. Used by MapperConfiguration.</summary>
    internal IReadOnlyList<OpenGenericTypeMap> GetOpenGenericMaps() => _openGenericMaps;
```

- [ ] **Step 4: Extend `AddProfile` to propagate open-generic registrations.**

In `src/Atlas/MapperConfigurationExpression.cs`, find the existing `AddProfile(MapperProfile profile)` method and add one new loop AFTER the existing closed-typemap loop:

```csharp
    public void AddProfile(MapperProfile profile)
    {
        EnsureMutable();
        foreach (var map in profile.GetTypeMaps())
        {
            RegisterTypeMap(map);
        }
        foreach (var openMap in profile.GetOpenGenericMaps())
        {
            _openGenericMaps.Add(openMap);
        }
    }
```

Note: `profile.GetOpenGenericMaps()` doesn't exist yet — that's added in Task 3. The Task 2 build will fail until Task 3 is implemented. **This is intentional cross-task dependency** — see Step 6 for how to handle.

- [ ] **Step 5: Extend `AddMaps(params Assembly[])` to propagate open-generic registrations.**

In `src/Atlas/MapperConfigurationExpression.cs`, find the existing `AddMaps(params Assembly[] assemblies)` method and extend the inner loop:

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
            foreach (var openMap in profile.GetOpenGenericMaps())
            {
                _openGenericMaps.Add(openMap);
            }
        }
    }
```

- [ ] **Step 6: Address the cross-task dependency on `MapperProfile.GetOpenGenericMaps()`.**

`AddProfile` and `AddMaps` reference `profile.GetOpenGenericMaps()` which is added in Task 3. To keep the build green during Task 2, temporarily add a stub on `MapperProfile`. **This stub is replaced by Task 3's full implementation** — Task 3's spec reviewer should verify the full implementation supersedes the stub.

In `src/Atlas/MapperProfile.cs`, add a TEMPORARY stub method (will be replaced by Task 3):

```csharp
    /// <summary>TEMPORARY STUB — replaced by full implementation in Task 3.</summary>
    internal IReadOnlyList<OpenGenericTypeMap> GetOpenGenericMaps() => Array.Empty<OpenGenericTypeMap>();
```

This change to `MapperProfile.cs` is a Task 2 modification, not a Task 3 anticipation. Task 2's allowlist therefore extends to include `src/Atlas/MapperProfile.cs` for this single-method addition only. **Spec reviewer note:** if Task 2's diff to `MapperProfile.cs` is anything other than this single stub method addition, that's a scope violation.

- [ ] **Step 7: Run the tests to verify they pass.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj --filter "FullyQualifiedName~Atlas.Tests.MapperConfigurationExpressionOpenGenericTests"
```
Expected: 5/5 PASS.

- [ ] **Step 8: Run all Atlas.Tests to confirm no regressions.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj
```
Expected: 389 (Task 1 baseline) + 5 = 394 PASS.

- [ ] **Step 9: Commit.**

```bash
git add src/Atlas/MapperConfigurationExpression.cs src/Atlas/MapperProfile.cs tests/Atlas.Tests/MapperConfigurationExpressionOpenGenericTests.cs
git commit -m "$(cat <<'EOF'
MapperConfigurationExpression gains CreateMap(Type, Type) (5 tests)

New non-generic overload for open-generic class map registration. Validates
both types are IsGenericTypeDefinition with matching arity. AddProfile and
AddMaps propagate open-generic registrations from profile into root config.
Temporary stub MapperProfile.GetOpenGenericMaps added to keep build green;
will be replaced by full implementation in Task 3.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3 — `MapperProfile.CreateMap(Type, Type)` mirror

Add the non-generic `CreateMap(Type, Type, MemberList)` overload to `MapperProfile`, replacing the temporary stub from Task 2.

**Files:**
- Modify: `src/Atlas/MapperProfile.cs`
- Create: `tests/Atlas.Tests/MapperProfileOpenGenericTests.cs`

**Allowlist for the implementer:** ONLY the two files above.

- [ ] **Step 1: Write the failing tests.**

Create `tests/Atlas.Tests/MapperProfileOpenGenericTests.cs`:

```csharp
using Atlas.Internal;

namespace Atlas.Tests;

public class MapperProfileOpenGenericTests
{
    public class Wrapper<T> { public T Value { get; set; } = default!; }
    public class WrapperDto<T> { public T Value { get; set; } = default!; }

    public sealed class WrapperProfile : MapperProfile
    {
        public WrapperProfile()
        {
            CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>));
            ValueTransformers.Add<string>(s => s == null ? null! : s.Trim());
        }
    }

    [Fact]
    public void CreateMap_OnProfile_StoresWithOriginatingProfile()
    {
        var profile = new WrapperProfile();

        var registrations = profile.GetOpenGenericMaps();

        Assert.Single(registrations);
        Assert.Same(profile, registrations[0].OriginatingProfile);
        Assert.Equal(typeof(Wrapper<>), registrations[0].SourceTypeDefinition);
        Assert.Equal(typeof(WrapperDto<>), registrations[0].DestinationTypeDefinition);
    }

    [Fact]
    public void ProfileValueTransformer_AppliesToMaterializedClosedPair()
    {
        // End-to-end: register profile via AddProfile, materialize Wrapper<string> at runtime,
        // verify the profile-level Trim transformer fires.
        var cfg = new MapperConfiguration(c => c.AddProfile<WrapperProfile>());
        var mapper = cfg.CreateMapper();

        var dto = mapper.Map<WrapperDto<string>>(new Wrapper<string> { Value = "  hello  " });

        Assert.Equal("hello", dto.Value);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj --filter "FullyQualifiedName~Atlas.Tests.MapperProfileOpenGenericTests"
```
Expected: tests FAIL — `CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>))` doesn't exist on `MapperProfile`. The first test fails on the missing method; the second test fails because materialization isn't wired up yet (Task 5).

- [ ] **Step 3: Add the field and replace the Task-2 stub with the full method.**

In `src/Atlas/MapperProfile.cs`, add a new private field after the existing `_typeMaps`:

```csharp
    private readonly List<OpenGenericTypeMap> _openGenericMaps = new();
```

Add the `CreateMap(Type, Type, MemberList)` method after the existing `CreateMap<TSource, TDestination>` method:

```csharp
    /// <summary>
    /// Registers an open-generic class map scoped to this profile. See
    /// <see cref="MapperConfigurationExpression.CreateMap(Type, Type, MemberList)"/> for
    /// full semantics. Profile-level value transformers apply to materialized closed pairs.
    /// </summary>
    /// <exception cref="AtlasConfigurationException">
    /// Thrown if either type is not an open generic type definition, or if the source and
    /// destination have different generic arities.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// Thrown if <paramref name="sourceType"/> or <paramref name="destinationType"/> is null.
    /// </exception>
    protected void CreateMap(Type sourceType, Type destinationType,
                             MemberList memberList = MemberList.None)
    {
        ArgumentNullException.ThrowIfNull(sourceType);
        ArgumentNullException.ThrowIfNull(destinationType);

        if (!sourceType.IsGenericTypeDefinition)
            throw new AtlasConfigurationException(new List<ConfigurationError>
            {
                new(sourceType, destinationType, "(register)",
                    $"Source must be an open generic type definition; got '{sourceType.Name}'. " +
                    "Use CreateMap<TSource, TDestination>() for closed types.")
            });

        if (!destinationType.IsGenericTypeDefinition)
            throw new AtlasConfigurationException(new List<ConfigurationError>
            {
                new(sourceType, destinationType, "(register)",
                    $"Destination must be an open generic type definition; got '{destinationType.Name}'. " +
                    "Use CreateMap<TSource, TDestination>() for closed types.")
            });

        var sourceArity = sourceType.GetGenericArguments().Length;
        var destArity = destinationType.GetGenericArguments().Length;
        if (sourceArity != destArity)
            throw new AtlasConfigurationException(new List<ConfigurationError>
            {
                new(sourceType, destinationType, "(register)",
                    $"Generic arity mismatch: source has {sourceArity} type parameter(s), destination has {destArity}.")
            });

        var openMap = new OpenGenericTypeMap(
            sourceType,
            destinationType,
            memberList,
            $"CreateMap(typeof({sourceType.Name}), typeof({destinationType.Name}))",
            originatingProfile: this);

        _openGenericMaps.Add(openMap);
    }
```

Replace the temporary stub `GetOpenGenericMaps` method (added in Task 2) with the full implementation:

```csharp
    /// <summary>Used by <see cref="MapperConfigurationExpression"/> to harvest the registered open-generic templates.</summary>
    internal IReadOnlyList<OpenGenericTypeMap> GetOpenGenericMaps() => _openGenericMaps;
```

(The Task 2 stub returned `Array.Empty<OpenGenericTypeMap>()`. The Task 3 replacement returns the actual list.)

- [ ] **Step 4: Run the tests to verify the FIRST test passes.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj --filter "FullyQualifiedName~Atlas.Tests.MapperProfileOpenGenericTests.CreateMap_OnProfile_StoresWithOriginatingProfile"
```
Expected: 1/1 PASS.

The second test (`ProfileValueTransformer_AppliesToMaterializedClosedPair`) will still FAIL — materialization isn't wired up until Task 5. **This is expected.** Note this in your report and continue.

- [ ] **Step 5: Run all Atlas.Tests to confirm no regressions.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj
```
Expected: 394 (Task 2 baseline) + 1 (Task 3 first test passes) = 395 PASS, with `ProfileValueTransformer_AppliesToMaterializedClosedPair` FAILING. Report this state — Task 5 will green it.

If a test other than `ProfileValueTransformer_AppliesToMaterializedClosedPair` fails, STOP and report `BLOCKED`.

- [ ] **Step 6: Commit.**

```bash
git add src/Atlas/MapperProfile.cs tests/Atlas.Tests/MapperProfileOpenGenericTests.cs
git commit -m "$(cat <<'EOF'
MapperProfile gains CreateMap(Type, Type) (1 of 2 tests passing)

Mirror of MapperConfigurationExpression's CreateMap(Type, Type) overload.
Stores open-generic registrations on the profile with OriginatingProfile = this.
Replaces the temporary stub from Task 2.

The ProfileValueTransformer_AppliesToMaterializedClosedPair test currently
FAILS — materialization isn't wired up until Task 5. Acknowledged in plan.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 4 — `MapperRegistry` constructor refactor + lookup-and-materialize

The largest task. Three parallel changes:
1. `_typeMaps` field type changes from `Dictionary` to `ConcurrentDictionary`.
2. Constructor gains 3 new optional parameters.
3. `GetTypeMap` gains lookup-and-materialize fallback, with two new private helper methods.

Plus `MapperConfiguration` constructor wiring (both overloads) to pass the new params.

**Files:**
- Modify: `src/Atlas/Internal/MapperRegistry.cs`
- Modify: `src/Atlas/MapperConfiguration.cs`
- Create: `tests/Atlas.Tests/Internal/MapperRegistryOpenGenericTests.cs`

**Allowlist for the implementer:** ONLY the three files above.

- [ ] **Step 1: Write the failing tests.**

Create `tests/Atlas.Tests/Internal/MapperRegistryOpenGenericTests.cs`:

```csharp
using Atlas.Internal;

namespace Atlas.Tests.Internal;

public class MapperRegistryOpenGenericTests
{
    public class Wrapper<T> { public T Value { get; set; } = default!; }
    public class WrapperDto<T> { public T Value { get; set; } = default!; }
    public class Customer { public string Name { get; set; } = ""; }
    public class CustomerDto { public string Name { get; set; } = ""; }

    [Fact]
    public void GetTypeMap_PrimitiveTypeArg_MaterializesAndCaches()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>)));
        var registry = cfg.Internal_Registry;
        var pair = new TypePair(typeof(Wrapper<int>), typeof(WrapperDto<int>));

        var first = registry.GetTypeMap(pair);
        var second = registry.GetTypeMap(pair);

        Assert.NotNull(first);
        Assert.Same(first, second);   // cache hit on second call
        Assert.Equal(typeof(Wrapper<int>), first!.SourceType);
        Assert.Equal(typeof(WrapperDto<int>), first.DestinationType);
        Assert.True(first.IsSealed);
    }

    [Fact]
    public void GetTypeMap_ReferenceTypeArg_MaterializesAndCaches()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>)));
        var registry = cfg.Internal_Registry;
        var pair = new TypePair(typeof(Wrapper<Customer>), typeof(WrapperDto<Customer>));

        var tm = registry.GetTypeMap(pair);

        Assert.NotNull(tm);
        Assert.Equal(typeof(Wrapper<Customer>), tm!.SourceType);
        Assert.Single(tm.PropertyMaps.Where(p => p.Name == "Value"));
    }

    [Fact]
    public void GetTypeMap_NestedClosedPairAlreadyRegistered_UsesExistingMap()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>));
            c.CreateMap<Customer, CustomerDto>();
        });
        var registry = cfg.Internal_Registry;

        // Materializing (Wrapper<Customer>, WrapperDto<CustomerDto>) — heterogeneous T positions.
        var pair = new TypePair(typeof(Wrapper<Customer>), typeof(WrapperDto<CustomerDto>));
        var tm = registry.GetTypeMap(pair);

        Assert.NotNull(tm);
        // Convention engine should resolve Value: Customer → CustomerDto via the registered nested map.
        var valuePm = tm!.PropertyMaps.Single(p => p.Name == "Value");
        Assert.NotNull(valuePm.SourcePath);
    }

    [Fact]
    public void GetTypeMap_ClosedPairTakesPrecedenceOverOpenGeneric()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>));
            c.CreateMap<Wrapper<int>, WrapperDto<int>>(MemberList.None);
        });
        var registry = cfg.Internal_Registry;
        var pair = new TypePair(typeof(Wrapper<int>), typeof(WrapperDto<int>));

        var tm = registry.GetTypeMap(pair);

        Assert.NotNull(tm);
        // RegistrationOrigin should reflect the closed-pair registration, not "(closed at runtime as ...)".
        Assert.DoesNotContain("(closed at runtime", tm!.RegistrationOrigin);
    }

    [Fact]
    public void GetTypeMap_NoMatchingOpenGeneric_ReturnsNull()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>)));
        var registry = cfg.Internal_Registry;

        // (Customer, CustomerDto) — neither generic, no template matches.
        var pair = new TypePair(typeof(Customer), typeof(CustomerDto));

        Assert.Null(registry.GetTypeMap(pair));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj --filter "FullyQualifiedName~Atlas.Tests.Internal.MapperRegistryOpenGenericTests"
```
Expected: tests FAIL — lookup-and-materialize logic not wired up.

- [ ] **Step 3: Refactor `MapperRegistry._typeMaps` to `ConcurrentDictionary`.**

In `src/Atlas/Internal/MapperRegistry.cs`, at the top of the class:

1. Add the using directive at the top of the file:
```csharp
using System.Collections.Concurrent;
```

2. Change the field type:
```csharp
    // BEFORE: private readonly Dictionary<TypePair, TypeMap> _typeMaps;
    private readonly ConcurrentDictionary<TypePair, TypeMap> _typeMaps;
```

3. Update the constructor's `_typeMaps` initialization (the change is from `.ToDictionary` to wrapping in ConcurrentDictionary):

The new constructor signature with all new parameters (replace the existing constructor entirely):

```csharp
    public MapperRegistry(
        IEnumerable<TypeMap> typeMaps,
        StringToEnumCache? stringToEnumCache = null,
        IServiceProvider? serviceProvider = null,
        IReadOnlyList<OpenGenericTypeMap>? openGenericMaps = null,
        ValueTransformerCollection? globalTransformers = null,
        ConventionOptions? conventionOptions = null)
    {
        _typeMaps = new ConcurrentDictionary<TypePair, TypeMap>(
            typeMaps.ToDictionary(t => t.Pair));
        StringToEnumCache = stringToEnumCache ?? new StringToEnumCache();
        ServiceProvider = serviceProvider;
        _openGenericMaps = openGenericMaps ?? Array.Empty<OpenGenericTypeMap>();
        _globalTransformers = globalTransformers ?? new ValueTransformerCollection();
        _conventionOptions = conventionOptions ?? new ConventionOptions(
            NamingConvention.PascalCase, NamingConvention.PascalCase, CaseSensitive: true);
    }
```

4. Add the three new private fields below the existing field declarations:

```csharp
    private readonly IReadOnlyList<OpenGenericTypeMap> _openGenericMaps;
    private readonly ValueTransformerCollection _globalTransformers;
    private readonly ConventionOptions _conventionOptions;
```

- [ ] **Step 4: Add `FindMatchingOpenGenericTemplate` and `MaterializeClosed` private methods.**

In `src/Atlas/Internal/MapperRegistry.cs`, add these two private methods (location: after the existing `Register` method, before the `// ---- Update-in-place delegates ----` comment block):

```csharp
    private OpenGenericTypeMap? FindMatchingOpenGenericTemplate(TypePair pair)
    {
        // Linear scan — open-generic registrations are typically a handful per app,
        // not enough to warrant a hashed lookup.
        foreach (var template in _openGenericMaps)
        {
            if (template.Matches(pair)) return template;
        }
        return null;
    }

    private TypeMap MaterializeClosed(OpenGenericTypeMap template, TypePair closedPair)
    {
        var tm = new TypeMap(closedPair.Source, closedPair.Destination, template.MemberList)
        {
            OriginatingProfile = template.OriginatingProfile,
            RegistrationOrigin = $"{template.RegistrationOrigin} " +
                                 $"(closed at runtime as ({closedPair.Source.Name}, {closedPair.Destination.Name}))"
        };

        // HasRegisteredMap probe — the convention engine uses this to decide whether to
        // emit a nested-map invoke for non-primitive property types. Reads the live
        // ConcurrentDictionary so previously-materialized closed pairs are visible.
        bool HasRegisteredMap(Type s, Type d) => _typeMaps.ContainsKey(new TypePair(s, d));
        ConventionEngine.ResolveMissingMembers(tm, _conventionOptions, HasRegisteredMap);

        // Profile/global value transformers via the existing resolver.
        TransformerResolver.Resolve(new[] { tm }, _globalTransformers);

        tm.Seal();
        return tm;
    }
```

- [ ] **Step 5: Add the lookup-and-materialize fallback to `GetTypeMap`.**

In `src/Atlas/Internal/MapperRegistry.cs`, replace the existing `GetTypeMap` method body:

```csharp
    public TypeMap? GetTypeMap(TypePair pair)
    {
        // Hot path: exact closed-pair match. ConcurrentDictionary read is lock-free.
        if (_typeMaps.TryGetValue(pair, out var m)) return m;

        // Fast bail when no open-generic registrations exist — hot-path zero-cost
        // for users who don't use the feature.
        if (_openGenericMaps.Count == 0) return null;

        // Closed-pair miss. Search open-generic registrations.
        var template = FindMatchingOpenGenericTemplate(pair);
        if (template is null) return null;

        // Materialize the closed pair via GetOrAdd. Under contention, the factory may
        // run more than once but only one TypeMap is stored — materialization is
        // idempotent (deterministic given the same pair + template + convention options).
        return _typeMaps.GetOrAdd(pair, p => MaterializeClosed(template, p));
    }
```

- [ ] **Step 6: Add the new fields to `MapperConfiguration` and pass them through.**

In `src/Atlas/MapperConfiguration.cs`:

1. Add two new private readonly fields after the existing `_serviceProvider` field:

```csharp
    private readonly IReadOnlyList<OpenGenericTypeMap> _openGenericMaps;
    private readonly ValueTransformerCollection _globalTransformers;
```

2. Modify the primary constructor `MapperConfiguration(MapperConfigurationExpression expression)` body. Insert the field assignments AFTER the existing `_enumValidationEnabled = expression.EnumValidationEnabled;` line:

```csharp
        _openGenericMaps = expression.GetOpenGenericMaps().ToList();
        _globalTransformers = expression.ValueTransformers;
```

Then change the final `_registry = new MapperRegistry(...)` call to pass the new parameters:

```csharp
        _registry = new MapperRegistry(
            typeMaps,
            _stringToEnumCache,
            openGenericMaps: _openGenericMaps,
            globalTransformers: _globalTransformers,
            conventionOptions: _conventionOptions);
```

3. Modify the DI-aware constructor `MapperConfiguration(MapperConfigurationExpression expression, IServiceProvider serviceProvider)` to pass the new fields when replacing the registry:

```csharp
    public MapperConfiguration(MapperConfigurationExpression expression, IServiceProvider serviceProvider)
        : this(expression)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        _serviceProvider = serviceProvider;
        _registry = new MapperRegistry(
            _registry.AllTypeMaps.ToList(),
            _stringToEnumCache,
            serviceProvider,
            openGenericMaps: _openGenericMaps,
            globalTransformers: _globalTransformers,
            conventionOptions: _conventionOptions);
    }
```

- [ ] **Step 7: Add the `using` directive for `OpenGenericTypeMap` to `MapperConfiguration.cs`.**

The file already has `using Atlas.Internal;` per the existing imports. Verify that line is present at the top of the file (it should be already; this is just a verification step). If absent, add it.

- [ ] **Step 8: Run the new tests to verify they pass.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj --filter "FullyQualifiedName~Atlas.Tests.Internal.MapperRegistryOpenGenericTests"
```
Expected: 5/5 PASS.

- [ ] **Step 9: Run the previously-failing Task 3 test to verify it now passes.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj --filter "FullyQualifiedName~Atlas.Tests.MapperProfileOpenGenericTests.ProfileValueTransformer_AppliesToMaterializedClosedPair"
```
Expected: 1/1 PASS.

- [ ] **Step 10: Run all Atlas.Tests to confirm no regressions.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj
```
Expected: 395 (Task 3 baseline with 1 test failing) + 5 (Task 4 new tests) + 1 (Task 3 test now greens) = 401 PASS.

- [ ] **Step 11: Commit.**

```bash
git add src/Atlas/Internal/MapperRegistry.cs src/Atlas/MapperConfiguration.cs tests/Atlas.Tests/Internal/MapperRegistryOpenGenericTests.cs
git commit -m "$(cat <<'EOF'
MapperRegistry lookup-and-materialize for open generics (5 tests + 1 backfill)

Three parallel changes:
- _typeMaps field changes from Dictionary to ConcurrentDictionary so
  GetTypeMap's lookup-and-materialize fallback can use GetOrAdd safely.
- MapperRegistry constructor gains optional openGenericMaps,
  globalTransformers, conventionOptions parameters (nullable with sensible
  defaults for test-helper compatibility).
- GetTypeMap closed-pair miss now scans open-generic registrations,
  materializes a closed TypeMap via ConventionEngine + TransformerResolver,
  and caches the result under the closed pair.

MapperConfiguration both constructors pass the new params (with new
_openGenericMaps and _globalTransformers fields surviving the DI-aware
constructor's registry replacement).

This single insertion point in GetTypeMap means both Atlas core and
Atlas.Projections get open-generic support automatically.

Greens the previously-failing
ProfileValueTransformer_AppliesToMaterializedClosedPair test from Task 3.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 5 — End-to-end Mapper integration tests

Real `IMapper.Map<>()` calls covering primitive type-args, reference type-args, higher arity, and update-in-place.

**Files:**
- Create: `tests/Atlas.Tests/MapperOpenGenericTests.cs`

**Allowlist for the implementer:** ONLY the test file. No production code change. If a production change is required to make any test pass, the implementer must report `DONE_WITH_CONCERNS`.

- [ ] **Step 1: Write the tests.**

Create `tests/Atlas.Tests/MapperOpenGenericTests.cs`:

```csharp
namespace Atlas.Tests;

public class MapperOpenGenericTests
{
    public class Wrapper<T>
    {
        public T Value { get; set; } = default!;
        public DateTime CreatedAt { get; set; }
        public List<T> History { get; set; } = new();
    }

    public class WrapperDto<T>
    {
        public T Value { get; set; } = default!;
        public DateTime CreatedAt { get; set; }
        public List<T> History { get; set; } = new();
    }

    public class Customer
    {
        public string Name { get; set; } = "";
        public int Score { get; set; }
    }

    public class CustomerDto
    {
        public string Name { get; set; } = "";
        public int Score { get; set; }
    }

    public class TwoArg<T1, T2>
    {
        public T1 First { get; set; } = default!;
        public T2 Second { get; set; } = default!;
    }

    public class TwoArgDto<T1, T2>
    {
        public T1 First { get; set; } = default!;
        public T2 Second { get; set; } = default!;
    }

    [Fact]
    public void Map_PrimitiveTypeArg_HeadlineExample()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>)));
        var mapper = cfg.CreateMapper();

        var dto = mapper.Map<WrapperDto<int>>(new Wrapper<int>
        {
            Value = 42,
            CreatedAt = new DateTime(2024, 1, 1),
            History = new List<int> { 1, 2, 3 }
        });

        Assert.Equal(42, dto.Value);
        Assert.Equal(new DateTime(2024, 1, 1), dto.CreatedAt);
        Assert.Equal(new[] { 1, 2, 3 }, dto.History);
    }

    [Fact]
    public void Map_ReferenceTypeArg_NestedMapResolved()
    {
        // Heterogeneous T positions: Wrapper<Customer> source → WrapperDto<CustomerDto> dest.
        // The Customer → CustomerDto registered closed pair handles the nested mapping.
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>));
            c.CreateMap<Customer, CustomerDto>();
        });
        var mapper = cfg.CreateMapper();

        var dto = mapper.Map<WrapperDto<CustomerDto>>(new Wrapper<Customer>
        {
            Value = new Customer { Name = "Alice", Score = 100 },
            CreatedAt = new DateTime(2024, 6, 1),
            History = new List<Customer> { new() { Name = "Bob", Score = 50 } }
        });

        Assert.Equal("Alice", dto.Value.Name);
        Assert.Equal(100, dto.Value.Score);
        Assert.Single(dto.History);
        Assert.Equal("Bob", dto.History[0].Name);
    }

    [Fact]
    public void Map_HigherArity_TupleStyle()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap(typeof(TwoArg<,>), typeof(TwoArgDto<,>)));
        var mapper = cfg.CreateMapper();

        var dto = mapper.Map<TwoArgDto<int, string>>(new TwoArg<int, string>
        {
            First = 7,
            Second = "hello"
        });

        Assert.Equal(7, dto.First);
        Assert.Equal("hello", dto.Second);
    }

    [Fact]
    public void Update_OpenGeneric_AppliesUniformly()
    {
        // Update-in-place calls BuildSourceExpression too, so materialized closed pairs
        // work uniformly for update via the same TypeMap path.
        var cfg = new MapperConfiguration(c =>
            c.CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>)));
        var mapper = cfg.CreateMapper();

        var existing = new WrapperDto<int> { Value = 99 };
        mapper.Map(new Wrapper<int> { Value = 7 }, existing);

        Assert.Equal(7, existing.Value);
    }
}
```

- [ ] **Step 2: Run the tests to verify they pass.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj --filter "FullyQualifiedName~Atlas.Tests.MapperOpenGenericTests"
```
Expected: 4/4 PASS (the production code is already complete from Tasks 1-4; these are integration tests).

- [ ] **Step 3: Run all Atlas.Tests to confirm no regressions.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj
```
Expected: 401 (Task 4 baseline) + 4 = 405 PASS.

- [ ] **Step 4: Commit.**

```bash
git add tests/Atlas.Tests/MapperOpenGenericTests.cs
git commit -m "$(cat <<'EOF'
End-to-end Mapper open-generic tests (4 tests)

Headline example with primitive type-arg, reference type-arg with nested
map resolved heterogeneously, higher-arity (Tuple-style), and update-in-place.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 6 — Projection codegen tests + EF Core E2E

Two test files. The projection codegen tests verify `ProjectionPlanBuilder.Build` works on materialized closed pairs (no production code change needed — `MapperRegistry.GetTypeMap` already handles it). The EF Core tests verify SQL generation end-to-end.

**Files:**
- Create: `tests/Atlas.Projections.Tests/Internal/ProjectionPlanBuilderOpenGenericTests.cs`
- Create: `tests/Atlas.Projections.Tests.EFCore/ProjectTo_OpenGenericTests.cs`
- Create: `tests/Atlas.Projections.Tests.EFCore/Fixtures/ContainerContext.cs` (new generic-typed EF fixture)

**Allowlist for the implementer:** ONLY the three files above. No production code change. If a production change is required, report `DONE_WITH_CONCERNS`.

- [ ] **Step 1: Write the projection codegen tests.**

Create `tests/Atlas.Projections.Tests/Internal/ProjectionPlanBuilderOpenGenericTests.cs`:

```csharp
using System.Linq.Expressions;
using Atlas;
using Atlas.Configuration;
using Atlas.Internal;
using Atlas.Projections;
using Atlas.Projections.Internal;

namespace Atlas.Projections.Tests.Internal;

public class ProjectionPlanBuilderOpenGenericTests
{
    public class Wrapper<T> { public T Value { get; set; } = default!; }
    public class WrapperDto<T> { public T Value { get; set; } = default!; }

    private static MapperRegistry BuildRegistry(Action<MapperConfigurationExpression> configure)
    {
        var cfg = new MapperConfiguration(configure);
        return cfg.Internal_Registry;
    }

    [Fact]
    public void Projection_OpenGenericTemplate_ProducesCorrectMemberInit()
    {
        var registry = BuildRegistry(c => c.CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>)));

        var lambda = ProjectionPlanBuilder.Build(
            registry,
            new TypePair(typeof(Wrapper<int>), typeof(WrapperDto<int>)),
            maxDepth: 5);

        // Build returns a valid lambda; body should be a MemberInit on WrapperDto<int>.
        Assert.NotNull(lambda);
        var memberInit = Assert.IsType<MemberInitExpression>(lambda.Body);
        Assert.Equal(typeof(WrapperDto<int>), memberInit.Type);
        Assert.Single(memberInit.Bindings.OfType<MemberAssignment>().Where(b => b.Member.Name == "Value"));
    }

    [Fact]
    public void Projection_ClosedPairTakesPrecedence()
    {
        // Register both an open template AND a specific closed pair with a custom
        // MapFrom — projection should use the closed pair, not the template.
        var registry = BuildRegistry(c =>
        {
            c.CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>));
            c.CreateMap<Wrapper<int>, WrapperDto<int>>(MemberList.None)
                .ForMember(d => d.Value, opt => opt.MapFrom(s => s.Value * 2));
        });

        var lambda = ProjectionPlanBuilder.Build(
            registry,
            new TypePair(typeof(Wrapper<int>), typeof(WrapperDto<int>)),
            maxDepth: 5);

        // The Value binding should reflect the closed-pair's MapFrom (s.Value * 2),
        // visible as a Multiply node in the binding's expression tree.
        var memberInit = (MemberInitExpression)lambda.Body;
        var valueBinding = memberInit.Bindings.OfType<MemberAssignment>()
            .Single(b => b.Member.Name == "Value");

        Assert.True(ContainsMultiply(valueBinding.Expression));
    }

    private static bool ContainsMultiply(Expression node)
    {
        var visitor = new MultiplyFinder();
        visitor.Visit(node);
        return visitor.Found;
    }

    private sealed class MultiplyFinder : ExpressionVisitor
    {
        public bool Found { get; private set; }
        protected override Expression VisitBinary(BinaryExpression node)
        {
            if (node.NodeType == ExpressionType.Multiply) Found = true;
            return base.VisitBinary(node);
        }
    }
}
```

- [ ] **Step 2: Create the EF Core fixture.**

Create `tests/Atlas.Projections.Tests.EFCore/Fixtures/ContainerContext.cs`:

```csharp
using Microsoft.EntityFrameworkCore;

namespace Atlas.Projections.Tests.EFCore.Fixtures;

public class Container<T>
{
    public int Id { get; set; }
    public string Label { get; set; } = "";
    public T Value { get; set; } = default!;
}

public class ContainerDto<T>
{
    public int Id { get; set; }
    public string Label { get; set; } = "";
    public T Value { get; set; } = default!;
}

public sealed class ContainerContext : DbContext
{
    public DbSet<Container<string>> StringContainers => Set<Container<string>>();

    public ContainerContext(DbContextOptions<ContainerContext> options) : base(options) { }

    public static ContainerContext CreateInMemory()
    {
        var options = new DbContextOptionsBuilder<ContainerContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        var ctx = new ContainerContext(options);
        ctx.Database.OpenConnection();
        ctx.Database.EnsureCreated();
        return ctx;
    }

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<Container<string>>(b =>
        {
            b.ToTable("Containers");
            b.HasKey(c => c.Id);
        });
    }

    public void Seed()
    {
        StringContainers.Add(new Container<string> { Id = 1, Label = "first", Value = "alpha" });
        StringContainers.Add(new Container<string> { Id = 2, Label = "second", Value = "beta" });
        SaveChanges();
    }
}
```

- [ ] **Step 3: Write the EF Core E2E tests.**

Create `tests/Atlas.Projections.Tests.EFCore/ProjectTo_OpenGenericTests.cs`:

```csharp
using Atlas;
using Atlas.Configuration;
using Atlas.Projections;
using Atlas.Projections.Tests.EFCore.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Projections.Tests.EFCore;

public class ProjectTo_OpenGenericTests
{
    [Fact]
    public void ProjectTo_OpenGeneric_GeneratesValidSql()
    {
        var config = new MapperConfiguration(c =>
            c.CreateMap(typeof(Container<>), typeof(ContainerDto<>)));
        using var ctx = ContainerContext.CreateInMemory();
        ctx.Seed();

        var sql = ctx.StringContainers.ProjectTo<ContainerDto<string>>(config).ToQueryString();

        // Should generate a SELECT against the Containers table.
        Assert.Contains("SELECT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Containers", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProjectTo_OpenGeneric_RowsRoundtrip()
    {
        var config = new MapperConfiguration(c =>
            c.CreateMap(typeof(Container<>), typeof(ContainerDto<>)));
        using var ctx = ContainerContext.CreateInMemory();
        ctx.Seed();

        var dtos = ctx.StringContainers.OrderBy(c => c.Id).ProjectTo<ContainerDto<string>>(config).ToList();

        Assert.Equal(2, dtos.Count);
        Assert.Equal(1, dtos[0].Id);
        Assert.Equal("first", dtos[0].Label);
        Assert.Equal("alpha", dtos[0].Value);
        Assert.Equal(2, dtos[1].Id);
        Assert.Equal("second", dtos[1].Label);
        Assert.Equal("beta", dtos[1].Value);
    }
}
```

- [ ] **Step 4: Run the projection codegen tests.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Projections.Tests/Atlas.Projections.Tests.csproj --filter "FullyQualifiedName~Atlas.Projections.Tests.Internal.ProjectionPlanBuilderOpenGenericTests"
```
Expected: 2/2 PASS.

- [ ] **Step 5: Run the EF Core tests.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Projections.Tests.EFCore/Atlas.Projections.Tests.EFCore.csproj --filter "FullyQualifiedName~Atlas.Projections.Tests.EFCore.ProjectTo_OpenGenericTests"
```
Expected: 2/2 PASS.

- [ ] **Step 6: Run all projection + EFCore tests to confirm no regressions.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Projections.Tests/Atlas.Projections.Tests.csproj
dotnet test C:/Repos/Atlas/tests/Atlas.Projections.Tests.EFCore/Atlas.Projections.Tests.EFCore.csproj
```
Expected: 67 + 2 = 69 PASS in `Atlas.Projections.Tests`; 12 + 2 = 14 PASS in `Atlas.Projections.Tests.EFCore`.

- [ ] **Step 7: Commit.**

```bash
git add tests/Atlas.Projections.Tests/Internal/ProjectionPlanBuilderOpenGenericTests.cs \
        tests/Atlas.Projections.Tests.EFCore/ProjectTo_OpenGenericTests.cs \
        tests/Atlas.Projections.Tests.EFCore/Fixtures/ContainerContext.cs
git commit -m "$(cat <<'EOF'
Projection + EF Core E2E tests for open generics (4 tests)

Projection codegen tests verify ProjectionPlanBuilder.Build works on
materialized closed pairs and that closed-pair-takes-precedence is honored
by projection too.

EF Core tests use a new ContainerContext fixture with a closed-typed
generic entity (DbSet<Container<string>>); confirm SQL generation and
row roundtrip end-to-end via the open-generic-materialized projection.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 7 — Validator integration tests

Verify `AssertConfigurationIsValid` excludes open-generic templates and runs successfully when only open generics are registered.

**Files:**
- Create: `tests/Atlas.Tests/MapperConfigurationOpenGenericValidationTests.cs`

**Allowlist for the implementer:** ONLY the test file. No production code change.

- [ ] **Step 1: Write the tests.**

Create `tests/Atlas.Tests/MapperConfigurationOpenGenericValidationTests.cs`:

```csharp
namespace Atlas.Tests;

public class MapperConfigurationOpenGenericValidationTests
{
    public class Wrapper<T> { public T Value { get; set; } = default!; }
    public class WrapperDto<T> { public T Value { get; set; } = default!; }
    public class Customer { public string Name { get; set; } = ""; }
    public class CustomerDto { public string Name { get; set; } = ""; }

    [Fact]
    public void AssertConfigurationIsValid_OpenGenericOnly_DoesNotThrow()
    {
        var cfg = new MapperConfiguration(c =>
            c.CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>)));

        // Open-generic templates are excluded from validation per the design's §6.1.
        // No closed pairs registered → validator iterates an empty AllTypeMaps and exits cleanly.
        cfg.AssertConfigurationIsValid();
    }

    [Fact]
    public void AssertConfigurationIsValid_OpenGenericPlusClosedPairs_ValidatesClosedPairsOnly()
    {
        // Closed pair Customer → CustomerDto WILL be validated (uses MemberList.Destination
        // by default per CreateMap<TS, TD> overload). Validation should pass since
        // CustomerDto.Name is mapped via convention from Customer.Name.
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>));
            c.CreateMap<Customer, CustomerDto>();
        });

        cfg.AssertConfigurationIsValid();   // no throw
    }
}
```

- [ ] **Step 2: Run the tests to verify they pass.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj --filter "FullyQualifiedName~Atlas.Tests.MapperConfigurationOpenGenericValidationTests"
```
Expected: 2/2 PASS.

- [ ] **Step 3: Run all Atlas.Tests to confirm no regressions.**

```bash
dotnet test C:/Repos/Atlas/tests/Atlas.Tests/Atlas.Tests.csproj
```
Expected: 405 (Task 5 baseline) + 2 = 407 PASS.

- [ ] **Step 4: Commit.**

```bash
git add tests/Atlas.Tests/MapperConfigurationOpenGenericValidationTests.cs
git commit -m "$(cat <<'EOF'
Validator integration tests for open generics (2 tests)

Open-generic templates excluded from AssertConfigurationIsValid; validator
runs successfully when only open generics are registered AND when open
generics coexist with closed pairs.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 8 — README + final coverage

Add the Open Generics section to the README, refresh the test count, remove the "Deferred to v2" entry, run final coverage check.

**Files:**
- Modify: `README.md`

**Allowlist for the implementer:** ONLY the README.

- [ ] **Step 1: Run the full solution test suite to confirm cumulative state.**

```bash
dotnet test C:/Repos/Atlas/Atlas.slnx
```
Expected: **490 PASS** (407 Atlas.Tests + 69 Atlas.Projections.Tests + 14 Atlas.Projections.Tests.EFCore).

If the count is off, STOP and report `BLOCKED`.

- [ ] **Step 2: Add the Open Generics section to `README.md`.**

Insert the following after the existing "Null substitution" section and before "What's in v1":

```markdown
## Open generics

A single `CreateMap(typeof(Source<>), typeof(Destination<>))` registration applies to
every closed instantiation at runtime. Atlas materializes a closed `TypeMap` per closed
pair on first use via `MapperRegistry.GetTypeMap`'s lazy fallback, then caches it.

```csharp
public class Wrapper<T> { public T Value { get; set; } }
public class WrapperDto<T> { public T Value { get; set; } }

var cfg = new MapperConfiguration(c =>
{
    c.CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>));
});
var mapper = cfg.CreateMapper();

var intDto = mapper.Map<WrapperDto<int>>(new Wrapper<int> { Value = 42 });
var stringDto = mapper.Map<WrapperDto<string>>(new Wrapper<string> { Value = "hi" });
```

**Closed-pair-takes-precedence rule:** when a user has registered both the open
template AND a specific closed pair, the closed pair wins. This is the documented
escape hatch for per-member overrides on a specific instantiation.

**Convention-only:** open-generic registrations carry no fluent surface — no
`ForMember`, no `Include`, no `BeforeMap`, no `NullSubstitute`, no `ReverseMap`.
Users who need any of these register the specific closed pair via the generic
`CreateMap<TSrc, TDst>()` overload.

**Validation:** open-generic templates are excluded from `AssertConfigurationIsValid()`
per the "not every closed combination is valid" rule. Materialized closed pairs that
exist by validation time will be validated as a side effect of being in the closed-pair
registry.

**Translates to ProjectTo:** `query.ProjectTo<WrapperDto<int>>(cfg)` triggers
materialization on first call and reuses the cached closed `TypeMap` for subsequent
projections.
```

- [ ] **Step 3: Remove the deferred entry.**

Find the "Deferred to v2" list. Delete the line:

```
- Open generics
```

Leave the rest of the bullet list intact (Dynamic/dictionary, Reference handling, Attribute-based, Expression translation).

- [ ] **Step 4: Sanity-check the build.**

```bash
dotnet build C:/Repos/Atlas/Atlas.slnx -c Debug
```
Expected: 0 warnings, 0 errors.

- [ ] **Step 5: Final test run.**

```bash
dotnet test C:/Repos/Atlas/Atlas.slnx
```
Expected: 490 PASS.

- [ ] **Step 6: Commit.**

```bash
git add README.md
git commit -m "$(cat <<'EOF'
docs: README — add open generics section, remove from deferred list

Open generics (CreateMap(Type, Type) overloads on MapperConfigurationExpression
+ MapperProfile with lazy materialization) is now shipped (Atlas v2 #9).
Documents the headline example, closed-pair-precedence rule, convention-only
constraint, validator behavior, and ProjectTo support.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

- [ ] **Step 7: Coverage spot-check.**

```bash
dotnet test C:/Repos/Atlas/Atlas.slnx -c Debug --collect:"XPlat Code Coverage" --results-directory C:/Repos/Atlas/TestResults/open-generics
```
Expected: produces `coverage.cobertura.xml` per test project.

If a coverage gate fails on `Atlas` or `Atlas.Projections`, STOP and add the missing branch coverage in a follow-up commit. Likely missing-branch sites: `_openGenericMaps.Count == 0` fast bail, the `GetOrAdd` factory invocation, the unreachable `null` return in `FindMatchingOpenGenericTemplate`.

---

## Final review (controller, before opening the PR)

- [ ] **Run the full holistic review using `superpowers:code-reviewer` on the entire `feat/open-generics` branch vs. `main`.**

  Non-negotiable per the established workflow rhythm. Open generics introduces a runtime mutation pattern (lazy materialization) that didn't exist before — high-value target for cross-task review. NullSubstitution (#8) was the empirical proof that holistic review catches things even when per-task reviews are clean.

- [ ] **Confirm cross-package consumer audit (Bug-4 lesson) was honoured.**

  The single insertion point in `MapperRegistry.GetTypeMap` means both `Atlas` core and `Atlas.Projections` get open-generic support automatically — no separate wire-in for `Atlas.Projections` is needed. Verify via `git grep -n "GetTypeMap\b" src/` and confirm `ProjectionPlanBuilder` calls it (line 16 of `src/Atlas.Projections/Internal/ProjectionPlanBuilder.cs`).

- [ ] **Confirm no scope-identifying TypeMap metadata was added (Bug-5 lesson).**

  `git diff main...HEAD -- src/Atlas/Internal/TypeMap.cs` should show no output.

- [ ] **Confirm `ProjectionCompatibility` was NOT modified.**

  `git diff main...HEAD -- src/Atlas.Projections/Internal/ProjectionCompatibility.cs` should show no output.

- [ ] **Confirm `ConfigurationValidator` was NOT modified.**

  `git diff main...HEAD -- src/Atlas/Internal/ConfigurationValidator.cs` should show no output.

- [ ] **Confirm `ConventionEngine`, `ReverseMapMirror`, `TransformerResolver`, `InheritanceMerger`, `ExecutionPlanBuilder`, `ProjectionPlanBuilder` were NOT modified.**

  ```bash
  git diff --stat main...HEAD -- src/Atlas/Internal/ConventionEngine.cs \
                                   src/Atlas/Internal/ReverseMapMirror.cs \
                                   src/Atlas/Internal/TransformerResolver.cs \
                                   src/Atlas/Internal/InheritanceMerger.cs \
                                   src/Atlas/Internal/ExecutionPlanBuilder.cs \
                                   src/Atlas.Projections/Internal/ProjectionPlanBuilder.cs
  ```
  Expected: empty output.

- [ ] **Push and open the PR.**

  ```bash
  git push -u origin feat/open-generics
  gh pr create --title "Atlas v2 #9 — Open Generics" --body "$(cat <<'EOF'
## Summary
- Adds `void CreateMap(Type sourceType, Type destinationType, MemberList = MemberList.None)` non-generic overloads on `MapperConfigurationExpression` and `MapperProfile`.
- A single `cfg.CreateMap(typeof(Source<>), typeof(Destination<>))` registration applies to every closed instantiation at runtime via lazy materialization.
- Closed-pair registrations take precedence over open-generic matches.
- Open-generic templates are convention-only (no fluent surface).
- Open templates excluded from `AssertConfigurationIsValid` per the reference doc.
- Both `Atlas.Map<>()` and `Atlas.Projections.ProjectTo<>()` get support automatically via the single insertion point in `MapperRegistry.GetTypeMap`.

## Implementation notes

- New `OpenGenericTypeMap` internal record (registration template, no `PropertyMap`s).
- `MapperRegistry._typeMaps` changes from `Dictionary` to `ConcurrentDictionary` for thread-safe lazy mutation.
- `MapperRegistry.GetTypeMap` extended with lookup-and-materialize fallback: closed-pair miss → linear scan over open registrations → `ConcurrentDictionary.GetOrAdd` materializes via `ConventionEngine` + `TransformerResolver` and caches.
- `MapperConfiguration` both constructors pass new `openGenericMaps`/`globalTransformers`/`conventionOptions` parameters to `MapperRegistry`.
- Two registration-time validation rules (both types must be `IsGenericTypeDefinition`; matching arity).

## Test plan

- [x] All existing tests still pass (465 → 490, +25 new)
- [x] Coverage gates met on Atlas core (line ≥ 90%, branch ≥ 80%)
- [x] Coverage gates met on Atlas.Projections
- [x] EF Core E2E confirms SQL generation + row roundtrip via materialized closed pair
- [x] In-memory E2E covers primitive type-arg, reference type-arg with nested map, higher arity, update-in-place
- [x] Closed-pair-takes-precedence rule verified end-to-end
- [x] Profile-level value transformer applies to materialized closed pair
- [x] Validator excludes open templates
- [x] Holistic `superpowers:code-reviewer` pass clean — no Critical or Important blockers

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
  ```

---

## Implementer Notes (per-task ground rules)

These are repeated in the design's §10 but reproduced here so the implementer-subagent sees them in-context.

1. **Cross-package consumer audit (Bug-4 lesson).** The new lookup-and-materialize logic lives in `MapperRegistry.GetTypeMap`, which is consumed by both `Atlas` core (via `Mapper.Map<>`) and `Atlas.Projections` (via `ProjectionPlanBuilder.Build`). Because the change is at a single point of insertion, both packages get open-generic support automatically. **No separate wire-in for `Atlas.Projections` is needed** — the spec reviewer should verify this and not flag the absence of projection-side production-code changes as missing.

2. **NOT scope-identifying TypeMap metadata (Bug-5 lesson).** Open-generic registrations are stored in a SEPARATE `_openGenericMaps` list, not on `TypeMap`. The materialized closed `TypeMap`s carry `OriginatingProfile` (inherited from the template) and `RegistrationOrigin` (annotated to indicate runtime materialization). No new `TypeMap` field added.

3. **Convention-only at materialization is a deliberate constraint.** The materialization pipeline runs `ConventionEngine.ResolveMissingMembers` and `TransformerResolver.Resolve` but NOT `InheritanceMerger.Resolve` or `ReverseMapMirror.Mirror`. Per-task review should confirm these are NOT called during `MaterializeClosed`.

4. **Bug-6 lesson — `ConvertOrMap` already handles asymmetric Nullable<T>.** Materialized closed pairs use the same codegen as manually-registered ones, so any `Nullable<T>` source + non-nullable destination (or vice versa) goes through the asymmetric-nullable widening branches added in #8. No new test scenarios needed.

5. **Thread-safety via `ConcurrentDictionary` change.** Per-task review should verify all existing reads still work (`ConcurrentDictionary` exposes the same surface as `Dictionary` for our uses); no write paths added beyond `GetOrAdd`; `AllTypeMaps` (`_typeMaps.Values`) still returns a snapshot semantically equivalent to before.

6. **Validator non-coverage of open-generic templates is documented behavior.** The validator iterates `_typeMaps.Values`. Open-generic templates aren't in there. Materialized closed pairs that exist by validation time WILL be validated as a side effect.

7. **`MapperRegistry` constructor signature change — backward compatibility for tests.** The new `openGenericMaps`, `globalTransformers`, `conventionOptions` parameters are nullable with defaults. Existing test helpers keep compiling.

8. **Watch for tests that quietly diverge from the plan (NullSubstitution Task 8 lesson).** If a test in this plan turns out to need adjustment, report `DONE_WITH_CONCERNS`. A passing test on a different code path may hide a bug on the intended path.

9. **xUnit v3 only — no FluentAssertions.** All assertions use `Assert.X()` style.

10. **Holistic review is non-negotiable.** Even if every per-task review passes cleanly, the controller MUST run `superpowers:code-reviewer` over the whole branch before opening the PR.
