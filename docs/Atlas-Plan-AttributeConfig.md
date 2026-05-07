# Atlas v2 Attribute-Based Configuration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship Atlas v2 feature #12 — attribute-based class declarations as a parallel front-end to the fluent API. Decorate destination classes with `[AutoMap(typeof(TSource))]` and properties with `[Ignore]` / `[SourceMember(name)]` / `[NullSubstitute(value)]`; discovery integrates with the existing `cfg.AddMaps(asm)` and `services.AddAtlas(asm)` entry points; the scanner translates attributes into existing fluent calls so the entire downstream pipeline (validation, propagation, projection, codegen) is unchanged.

**Architecture:** Translate-to-fluent. The new `Atlas.Internal.AttributeScanner` enumerates `[AutoMap]`-decorated types in scanned assemblies and, for each, calls `cfg.CreateMap<S,D>()` via `MakeGenericMethod`, then applies member-level attributes via reflection-built `Expression.Lambda` callbacks for `IMappingExpression<,>.ForMember<TMember>(selector, options)`. Class-level flags drive `.PreserveReferences()` and `.ReverseMap()` calls. One additional surgical change: the existing `MapperConfigurationExpression.RegisterTypeMap` reverse-only duplicate guard tightens to a universal duplicate-pair rule.

**Tech Stack:** C# 14 preview, `System.Linq.Expressions`, `System.Reflection`, `System.Runtime.ExceptionServices.ExceptionDispatchInfo`, xUnit v3 (plain `Assert.X()` only — NO FluentAssertions per project convention).

**Branch:** `feat/attribute-config`, cut from `main` HEAD `96ad3d9` (the design commit for #12).

**Reference design:** `C:\Repos\Atlas\docs\Atlas-Design-AttributeConfig.md` — primary spec. All section references (e.g., "design §5.4") point at it.

---

## File Map

### New files (production)

- `C:\Repos\Atlas\src\Atlas\AutoMapAttribute.cs` — class-level attribute carrying `SourceType`, `MemberList`, `ReverseMap`, `PreserveReferences`. Sealed, `AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)`.
- `C:\Repos\Atlas\src\Atlas\IgnoreAttribute.cs` — marker attribute (no constructor args). Sealed, property-targeted.
- `C:\Repos\Atlas\src\Atlas\SourceMemberAttribute.cs` — single-arg attribute carrying `MemberName` (string, supports dotted paths). Sealed, property-targeted.
- `C:\Repos\Atlas\src\Atlas\NullSubstituteAttribute.cs` — single-arg attribute carrying `ConstantValue` (object). Sealed, property-targeted, rejects literal `null`.
- `C:\Repos\Atlas\src\Atlas\Internal\AttributeScanner.cs` — internal static class with `Discover(Assembly, MapperConfigurationExpression)` plus private helpers `IsAttributeMapCandidate`, `ProcessAutoMapType`, `ValidateAutoMapTarget`, `BuildSourcePathExpression`, `InvokeCreateMap`, `ApplyMemberAttributes`, `ApplyClassLevelFlags`, `ResolveSourceMemberByConvention`, `ValidateNullSubstituteCompatibility`.

### Modified files (production)

- `C:\Repos\Atlas\src\Atlas\MapperConfigurationExpression.cs` — append `AttributeScanner.Discover(asm, this)` call inside the existing `AddMaps(params Assembly[])` foreach loop (line 167-180 area). Tighten `RegisterTypeMap` (line 115-134) duplicate-pair check from reverse-only to universal.

### New files (tests)

- `C:\Repos\Atlas\tests\Atlas.Tests\AutoMapAttributeTests.cs` — class-level attribute behaviors (~7 tests).
- `C:\Repos\Atlas\tests\Atlas.Tests\IgnoreAttributeTests.cs` — `[Ignore]` member behavior (~4 tests).
- `C:\Repos\Atlas\tests\Atlas.Tests\SourceMemberAttributeTests.cs` — `[SourceMember]` redirection + path resolution (~8 tests).
- `C:\Repos\Atlas\tests\Atlas.Tests\NullSubstituteAttributeTests.cs` — `[NullSubstitute]` constant behavior (~6 tests).
- `C:\Repos\Atlas\tests\Atlas.Tests\Internal\AttributeScannerTests.cs` — discovery + translation mechanics (~14 tests).
- `C:\Repos\Atlas\tests\Atlas.Tests\AttributeFluentInteractionTests.cs` — Q4 conflict policy + mixed mode (~6 tests).
- `C:\Repos\Atlas\tests\Atlas.Tests\AttributeIntegrationTests.cs` — end-to-end DI + multi-attribute scenarios (~6 tests).
- `C:\Repos\Atlas\tests\Atlas.Projections.Tests\AttributeProjectionTests.cs` — projection support and rejection for projection-incompatible attribute typemaps (~6 tests).

### Modified files (docs)

- `C:\Repos\Atlas\README.md` — add "Attribute-based configuration" section + "Migration notes" subsection. Remove #12 from the deferred-features list.

### Test count delta target

Baseline from PR #11: **634 PASS** (73 Atlas.Tests internal + 547 Atlas.Tests top-level + 14 Projections + … the actual layout is `Atlas.Tests` ~= 620 + `Atlas.Projections.Tests` ~14; check after Task 0).

After this feature: **~691 PASS** (≈ +57 net):
- +6 in `AutoMapAttributeTests` (constructor validation + property setters in Task 1; behavioral coverage shipped piecewise across Task 5 + Task 9)
- +1 in `IgnoreAttributeTests` (existence + AttributeUsage assertion in Task 1; behavioral coverage in Task 6)
- +1 in `SourceMemberAttributeTests` (constructor null-check in Task 1; behavioral + path-resolution coverage spread across Tasks 4/7)
- +1 in `NullSubstituteAttributeTests` (constructor null-check + property in Task 1; behavioral + validator coverage in Task 8)
- +14 in `AttributeScannerTests` (mechanics, spread across Tasks 2/3/5/9/10)
- +6 in `AttributeFluentInteractionTests` (Task 9, Task 10)
- +6 in `AttributeIntegrationTests` (Task 12)
- +6 in `AttributeProjectionTests` (Task 11)
- The `Tests Per File` numbers above sum higher than +57 because some test files are populated incrementally; the total is the union, ≈57 net.

Per-feature plan-arithmetic-drift discipline (memory feedback): the implementer's actual count is authoritative; treat ≈57 as approximate.

---

## Task 0 — Branch setup

**Files:** none (controller-only operation).

- [ ] **Step 0.1: Verify clean state on `main`**

```pwsh
cd C:\Repos\Atlas
git status
git log --oneline -3
```

Expected: working tree clean; HEAD at `96ad3d9` ("Atlas v2 #12 design: Attribute-Based Configuration") or further if subsequent commits land.

- [ ] **Step 0.2: Cut feature branch**

```pwsh
git checkout -b feat/attribute-config
```

Expected: switched to a new branch `feat/attribute-config`.

- [ ] **Step 0.3: Confirm baseline test count**

```pwsh
dotnet test --nologo 2>&1 | Select-String -Pattern "Passed:|Failed:|Skipped:"
```

Expected: total `Passed: 634, Failed: 0, Skipped: 0` across the three test projects (Atlas.Tests + Atlas.Projections.Tests + Atlas.Projections.Tests.EFCore).

If the count differs, investigate before proceeding — a non-clean baseline means the feature is being implemented atop unstable foundations.

---

## Task 1 — Public attribute types

**Goal:** Stand up the four public attribute classes. Pure data shapes — `ArgumentNullException.ThrowIfNull` guards on constructors, settable properties on `AutoMapAttribute`. No scanner integration yet.

**Files:**
- Create: `C:\Repos\Atlas\src\Atlas\AutoMapAttribute.cs`
- Create: `C:\Repos\Atlas\src\Atlas\IgnoreAttribute.cs`
- Create: `C:\Repos\Atlas\src\Atlas\SourceMemberAttribute.cs`
- Create: `C:\Repos\Atlas\src\Atlas\NullSubstituteAttribute.cs`
- Create: `C:\Repos\Atlas\tests\Atlas.Tests\AutoMapAttributeTests.cs`
- Create: `C:\Repos\Atlas\tests\Atlas.Tests\IgnoreAttributeTests.cs`
- Create: `C:\Repos\Atlas\tests\Atlas.Tests\SourceMemberAttributeTests.cs`
- Create: `C:\Repos\Atlas\tests\Atlas.Tests\NullSubstituteAttributeTests.cs`

**Allowlist for the implementer subagent:** the eight files above, no others.

- [ ] **Step 1.1: Write failing tests for `AutoMapAttribute`**

Contents of `tests/Atlas.Tests/AutoMapAttributeTests.cs`:

```csharp
using System.Reflection;

namespace Atlas.Tests;

public class AutoMapAttributeTests
{
    [Fact]
    public void Ctor_NullSourceType_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new AutoMapAttribute(null!));
    }

    [Fact]
    public void Ctor_SourceTypeAssigned()
    {
        var attr = new AutoMapAttribute(typeof(string));
        Assert.Equal(typeof(string), attr.SourceType);
    }

    [Fact]
    public void Defaults_MemberListIsDestination_FlagsAreFalse()
    {
        var attr = new AutoMapAttribute(typeof(string));
        Assert.Equal(MemberList.Destination, attr.MemberList);
        Assert.False(attr.ReverseMap);
        Assert.False(attr.PreserveReferences);
    }

    [Fact]
    public void Properties_AreSettable()
    {
        var attr = new AutoMapAttribute(typeof(string))
        {
            MemberList = MemberList.Source,
            ReverseMap = true,
            PreserveReferences = true,
        };
        Assert.Equal(MemberList.Source, attr.MemberList);
        Assert.True(attr.ReverseMap);
        Assert.True(attr.PreserveReferences);
    }

    [Fact]
    public void AttributeUsage_TargetsClassOnly_NotInheritedNotMultiple()
    {
        var usage = typeof(AutoMapAttribute).GetCustomAttribute<AttributeUsageAttribute>();
        Assert.NotNull(usage);
        Assert.Equal(AttributeTargets.Class, usage!.ValidOn);
        Assert.False(usage.Inherited);
        Assert.False(usage.AllowMultiple);
    }

    [Fact]
    public void Sealed()
    {
        Assert.True(typeof(AutoMapAttribute).IsSealed);
    }
}
```

Contents of `tests/Atlas.Tests/IgnoreAttributeTests.cs` (only construction-level test in this task; behavioral test added in Task 6):

```csharp
using System.Reflection;

namespace Atlas.Tests;

public class IgnoreAttributeTests
{
    [Fact]
    public void AttributeUsage_TargetsPropertyOnly_NotInheritedNotMultiple_Sealed()
    {
        var usage = typeof(IgnoreAttribute).GetCustomAttribute<AttributeUsageAttribute>();
        Assert.NotNull(usage);
        Assert.Equal(AttributeTargets.Property, usage!.ValidOn);
        Assert.False(usage.Inherited);
        Assert.False(usage.AllowMultiple);
        Assert.True(typeof(IgnoreAttribute).IsSealed);
    }
}
```

Contents of `tests/Atlas.Tests/SourceMemberAttributeTests.cs` (only construction-level in this task):

```csharp
using System.Reflection;

namespace Atlas.Tests;

public class SourceMemberAttributeTests
{
    [Fact]
    public void Ctor_NullName_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SourceMemberAttribute(null!));
    }

    [Fact]
    public void Ctor_NameAssigned()
    {
        var attr = new SourceMemberAttribute("Customer.Name");
        Assert.Equal("Customer.Name", attr.MemberName);
    }

    [Fact]
    public void AttributeUsage_TargetsPropertyOnly_NotInheritedNotMultiple_Sealed()
    {
        var usage = typeof(SourceMemberAttribute).GetCustomAttribute<AttributeUsageAttribute>();
        Assert.NotNull(usage);
        Assert.Equal(AttributeTargets.Property, usage!.ValidOn);
        Assert.False(usage.Inherited);
        Assert.False(usage.AllowMultiple);
        Assert.True(typeof(SourceMemberAttribute).IsSealed);
    }
}
```

Contents of `tests/Atlas.Tests/NullSubstituteAttributeTests.cs` (only construction-level in this task):

```csharp
using System.Reflection;

namespace Atlas.Tests;

public class NullSubstituteAttributeTests
{
    [Fact]
    public void Ctor_NullValue_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new NullSubstituteAttribute(null!));
    }

    [Fact]
    public void Ctor_ConstantValueAssigned()
    {
        var attr = new NullSubstituteAttribute("(none)");
        Assert.Equal("(none)", attr.ConstantValue);
    }

    [Fact]
    public void AttributeUsage_TargetsPropertyOnly_NotInheritedNotMultiple_Sealed()
    {
        var usage = typeof(NullSubstituteAttribute).GetCustomAttribute<AttributeUsageAttribute>();
        Assert.NotNull(usage);
        Assert.Equal(AttributeTargets.Property, usage!.ValidOn);
        Assert.False(usage.Inherited);
        Assert.False(usage.AllowMultiple);
        Assert.True(typeof(NullSubstituteAttribute).IsSealed);
    }
}
```

- [ ] **Step 1.2: Run tests to verify they fail (compile error: types don't exist)**

```pwsh
dotnet test --nologo --filter "FullyQualifiedName~AutoMapAttributeTests|FullyQualifiedName~IgnoreAttributeTests|FullyQualifiedName~SourceMemberAttributeTests|FullyQualifiedName~NullSubstituteAttributeTests"
```

Expected: build error referencing missing types `AutoMapAttribute`, `IgnoreAttribute`, `SourceMemberAttribute`, `NullSubstituteAttribute`.

- [ ] **Step 1.3: Create `AutoMapAttribute.cs`**

Contents of `src/Atlas/AutoMapAttribute.cs`:

```csharp
namespace Atlas;

/// <summary>
/// Class-level attribute declaring that the decorated class is the destination type
/// of a mapping from <see cref="SourceType"/>. Equivalent to a fluent
/// <c>cfg.CreateMap&lt;TSource, TDestination&gt;(MemberList)</c> registration.
/// </summary>
/// <remarks>
/// Discovered by <see cref="MapperConfigurationExpression.AddMaps(System.Reflection.Assembly[])"/>
/// during the same scan that finds <see cref="MapperProfile"/> subclasses. Member-level
/// customization comes from <see cref="IgnoreAttribute"/>, <see cref="SourceMemberAttribute"/>,
/// and <see cref="NullSubstituteAttribute"/> on the decorated class's properties.
/// Configuring the same (TSource, TDestination) pair both via attributes AND via fluent
/// <c>CreateMap</c> throws <see cref="AtlasConfigurationException"/> at registration time.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class AutoMapAttribute : Attribute
{
    public AutoMapAttribute(Type sourceType)
    {
        ArgumentNullException.ThrowIfNull(sourceType);
        SourceType = sourceType;
    }

    /// <summary>The source type for this mapping (positional argument).</summary>
    public Type SourceType { get; }

    /// <summary>
    /// Validation policy for this mapping. Defaults to <see cref="MemberList.Destination"/> —
    /// the same default fluent <c>CreateMap</c> uses.
    /// </summary>
    public MemberList MemberList { get; set; } = MemberList.Destination;

    /// <summary>
    /// If <c>true</c>, the scanner additionally calls <c>.ReverseMap()</c> on the translated
    /// registration. Member-level attribute config (Ignore, SourceMember, NullSubstitute)
    /// describes the FORWARD direction only and does not auto-flip.
    /// </summary>
    public bool ReverseMap { get; set; }

    /// <summary>
    /// If <c>true</c>, the scanner calls <c>.PreserveReferences()</c> on the translated
    /// registration. When <see cref="ReverseMap"/> is also <c>true</c>, the flag propagates
    /// to the reverse pair via the bidirectional propagation machinery shipped in PR #11.
    /// </summary>
    public bool PreserveReferences { get; set; }
}
```

- [ ] **Step 1.4: Create `IgnoreAttribute.cs`**

Contents of `src/Atlas/IgnoreAttribute.cs`:

```csharp
namespace Atlas;

/// <summary>
/// Member-level attribute marking a destination property as ignored (excluded from mapping
/// AND from validation). Equivalent to fluent
/// <c>ForMember(d =&gt; d.X, opt =&gt; opt.Ignore())</c>.
/// </summary>
/// <remarks>
/// Has effect only when applied to a property of a class decorated with
/// <see cref="AutoMapAttribute"/>. Silently no-op otherwise (no error). Combined with
/// <see cref="SourceMemberAttribute"/> or <see cref="NullSubstituteAttribute"/> on the
/// same property, <see cref="IgnoreAttribute"/> short-circuits — the property is never
/// assigned, so the other attributes' configuration is unreachable.
/// </remarks>
[AttributeUsage(AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
public sealed class IgnoreAttribute : Attribute { }
```

- [ ] **Step 1.5: Create `SourceMemberAttribute.cs`**

Contents of `src/Atlas/SourceMemberAttribute.cs`:

```csharp
namespace Atlas;

/// <summary>
/// Member-level attribute redirecting a destination property to a different source-side
/// member by name. Equivalent to fluent
/// <c>ForMember(d =&gt; d.X, opt =&gt; opt.MapFrom(s =&gt; s.OtherName))</c>, except that
/// the right-hand side is a name (or dotted path), not a lambda.
/// </summary>
/// <remarks>
/// Resolved at config-build time. The path uses dotted segments for source-side flattening
/// (e.g., <c>"Customer.Address.City"</c>); each segment must resolve to a public readable
/// property or field on the source-side type at that depth. If resolution fails, the scanner
/// accumulates a <see cref="ConfigurationError"/> and the eventual
/// <see cref="AtlasConfigurationException"/> names the offending segment and the type it
/// was looked up on.
/// </remarks>
[AttributeUsage(AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
public sealed class SourceMemberAttribute : Attribute
{
    public SourceMemberAttribute(string memberName)
    {
        ArgumentNullException.ThrowIfNull(memberName);
        MemberName = memberName;
    }

    public string MemberName { get; }
}
```

- [ ] **Step 1.6: Create `NullSubstituteAttribute.cs`**

Contents of `src/Atlas/NullSubstituteAttribute.cs`:

```csharp
namespace Atlas;

/// <summary>
/// Member-level attribute supplying a constant fallback value used when the resolved source
/// member is <c>null</c>. Equivalent to fluent
/// <c>ForMember(d =&gt; d.X, opt =&gt; opt.NullSubstitute(constant))</c>.
/// </summary>
/// <remarks>
/// Has effect only when applied to a property of a class decorated with
/// <see cref="AutoMapAttribute"/>. The validator rejects substitutes whose source-member
/// type is non-nullable (the substitute would be unreachable) or whose substitute type is
/// not assignable to the source-member type. The constructor itself rejects literal
/// <c>null</c> as the substitute value.
/// </remarks>
[AttributeUsage(AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
public sealed class NullSubstituteAttribute : Attribute
{
    public NullSubstituteAttribute(object constantValue)
    {
        ArgumentNullException.ThrowIfNull(constantValue);
        ConstantValue = constantValue;
    }

    public object ConstantValue { get; }
}
```

- [ ] **Step 1.7: Run tests to verify they pass**

```pwsh
dotnet test --nologo --filter "FullyQualifiedName~AutoMapAttributeTests|FullyQualifiedName~IgnoreAttributeTests|FullyQualifiedName~SourceMemberAttributeTests|FullyQualifiedName~NullSubstituteAttributeTests"
```

Expected: 9 tests pass (6 in `AutoMapAttributeTests`, 1 each in `IgnoreAttributeTests`/`SourceMemberAttributeTests`/`NullSubstituteAttributeTests`'s `AttributeUsage` test, plus 1 each construction test for SourceMember and NullSubstitute = 1+1+1=3 + 6 = 9 total… recount: AutoMap=6, Ignore=1, SourceMember=3, NullSubstitute=3 = 13 total. Verify the implementer's actual count and adjust the running tally for §13.)

- [ ] **Step 1.8: Run full test suite to verify zero regressions**

```pwsh
dotnet test --nologo 2>&1 | Select-String -Pattern "Passed:|Failed:|Skipped:"
```

Expected: total passed = 634 baseline + new tests from this task (≈647). Failed = 0.

- [ ] **Step 1.9: Commit**

```pwsh
git add src/Atlas/AutoMapAttribute.cs src/Atlas/IgnoreAttribute.cs src/Atlas/SourceMemberAttribute.cs src/Atlas/NullSubstituteAttribute.cs `
        tests/Atlas.Tests/AutoMapAttributeTests.cs tests/Atlas.Tests/IgnoreAttributeTests.cs tests/Atlas.Tests/SourceMemberAttributeTests.cs tests/Atlas.Tests/NullSubstituteAttributeTests.cs
git commit -m "Public attribute types: [AutoMap], [Ignore], [SourceMember], [NullSubstitute] (Task 1)`n`nFour sealed attribute classes per design §3. No scanner integration yet — pure`ndata shapes with ArgumentNullException.ThrowIfNull guards. AttributeUsage:`nclass-level on AutoMap (not inherited, not multiple); property-level on the`nthree member attributes. NullSubstituteAttribute rejects literal null at`nconstruction (per design §3 / §11 O2)."
```

---

## Task 2 — `AttributeScanner` skeleton + assembly enumeration filter

**Goal:** Stand up `Atlas.Internal.AttributeScanner` with the `Discover` entry point and the `IsAttributeMapCandidate` filter. The scanner enumerates types but does NOT yet translate them — `ProcessAutoMapType` is a stub that is wired up in subsequent tasks. This task verifies the discovery surface (top-level / public / non-abstract / non-nested / decorated with `[AutoMap]`) before any translation logic exists.

**Files:**
- Create: `C:\Repos\Atlas\src\Atlas\Internal\AttributeScanner.cs`
- Create: `C:\Repos\Atlas\tests\Atlas.Tests\Internal\AttributeScannerTests.cs`

**Allowlist for the implementer subagent:** the two files above.

- [ ] **Step 2.1: Write failing tests for the discovery filter**

Contents of `tests/Atlas.Tests/Internal/AttributeScannerTests.cs`:

```csharp
using System.Reflection;
using Atlas.Internal;

namespace Atlas.Tests.Internal;

public class AttributeScannerTests
{
    // ---- Filter tests (Task 2) ----

    [Fact]
    public void IsAttributeMapCandidate_TopLevelPublicDecorated_True()
    {
        Assert.True(AttributeScanner.IsAttributeMapCandidate(typeof(PublicAttributeFixture)));
    }

    [Fact]
    public void IsAttributeMapCandidate_NoAutoMap_False()
    {
        Assert.False(AttributeScanner.IsAttributeMapCandidate(typeof(UndecoratedFixture)));
    }

    [Fact]
    public void IsAttributeMapCandidate_NestedDecorated_False()
    {
        Assert.False(AttributeScanner.IsAttributeMapCandidate(typeof(NestedAttributeFixture)));
    }

    [Fact]
    public void IsAttributeMapCandidate_NonPublicDecorated_False()
    {
        // The internal fixture sits in the same assembly; reflection sees it only via fully-qualified lookup.
        var type = typeof(AttributeScannerTests).Assembly.GetType("Atlas.Tests.Internal.InternalAttributeFixture", throwOnError: false);
        Assert.NotNull(type);
        Assert.False(AttributeScanner.IsAttributeMapCandidate(type!));
    }

    [Fact]
    public void IsAttributeMapCandidate_AbstractDecorated_False()
    {
        Assert.False(AttributeScanner.IsAttributeMapCandidate(typeof(AbstractAttributeFixture)));
    }

    [Fact]
    public void Discover_NonAttributeAssembly_NoOp()
    {
        var cfg = new MapperConfigurationExpression();
        AttributeScanner.Discover(typeof(string).Assembly, cfg);
        Assert.Empty(cfg.GetTypeMaps());
    }

    // Source classes for fixtures
    public class PublicSource { public int X { get; set; } }
    public class UndecoratedSource { public int X { get; set; } }
    public class NestedSource { public int X { get; set; } }
    public class AbstractSource { public int X { get; set; } }
    public class InternalSource { public int X { get; set; } }

    // Top-level fixture (the inner class is the nested-fixture case)
    public class HostForNested
    {
        [AutoMap(typeof(NestedSource))]
        public class NestedAttributeFixture
        {
            public int X { get; set; }
        }
    }
}

[AutoMap(typeof(AttributeScannerTests.PublicSource))]
public class PublicAttributeFixture
{
    public int X { get; set; }
}

public class UndecoratedFixture
{
    public int X { get; set; }
}

[AutoMap(typeof(AttributeScannerTests.AbstractSource))]
public abstract class AbstractAttributeFixture
{
    public int X { get; set; }
}

[AutoMap(typeof(AttributeScannerTests.InternalSource))]
internal class InternalAttributeFixture
{
    public int X { get; set; }
}

// Alias the nested type at namespace scope for the test's typeof() to find it
file sealed class _NestedRefAlias { }
```

Note: the nested-fixture test references `typeof(NestedAttributeFixture)` but `NestedAttributeFixture` is defined inside `HostForNested`. Adjust the test to use `typeof(AttributeScannerTests.HostForNested.NestedAttributeFixture)`:

```csharp
    [Fact]
    public void IsAttributeMapCandidate_NestedDecorated_False()
    {
        Assert.False(AttributeScanner.IsAttributeMapCandidate(
            typeof(AttributeScannerTests.HostForNested.NestedAttributeFixture)));
    }
```

- [ ] **Step 2.2: Run tests to verify they fail**

```pwsh
dotnet test --nologo --filter "FullyQualifiedName~AttributeScannerTests"
```

Expected: build error referencing missing type `Atlas.Internal.AttributeScanner`.

- [ ] **Step 2.3: Create `AttributeScanner.cs` skeleton**

Contents of `src/Atlas/Internal/AttributeScanner.cs`:

```csharp
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Atlas.Internal;

/// <summary>
/// Discovers <see cref="AutoMapAttribute"/>-decorated types in scanned assemblies and registers
/// each into the configuration via the same fluent calls a hand-written profile would make.
/// See <c>docs/Atlas-Design-AttributeConfig.md</c> §4.1 / §5.
/// </summary>
internal static class AttributeScanner
{
    /// <summary>
    /// Top-level entry point. Enumerates public top-level non-abstract decorated types and
    /// processes each. Errors are accumulated; a fatal duplicate-pair throws immediately.
    /// </summary>
    public static void Discover(Assembly assembly, MapperConfigurationExpression cfg)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(cfg);

        var errors = new List<ConfigurationError>();
        foreach (var type in assembly.GetTypes())
        {
            if (!IsAttributeMapCandidate(type))
                continue;

            ProcessAutoMapType(type, cfg, errors);
        }

        if (errors.Count > 0)
            throw new AtlasConfigurationException(errors);
    }

    /// <summary>
    /// True when <paramref name="t"/> is a top-level public non-abstract non-interface
    /// non-nested non-enum class decorated with <see cref="AutoMapAttribute"/>.
    /// Static classes (encoded as <c>IsAbstract &amp;&amp; IsSealed</c>) are excluded.
    /// </summary>
    public static bool IsAttributeMapCandidate(Type t)
    {
        return t.IsClass
            && t.IsPublic
            && !t.IsAbstract
            && !t.IsNested
            && t.GetCustomAttribute<AutoMapAttribute>(inherit: false) is not null;
    }

    /// <summary>
    /// Translates one [AutoMap]-decorated type into fluent calls. Stub in Task 2 — fully
    /// implemented across Tasks 3 (validation), 4 (path resolution), 5 (CreateMap +
    /// class-level flags), 6/7/8 (member attributes).
    /// </summary>
    private static void ProcessAutoMapType(Type decoratedType, MapperConfigurationExpression cfg, List<ConfigurationError> errors)
    {
        // Task 2 stub. Real logic lands in Tasks 3-8.
    }
}
```

- [ ] **Step 2.4: Run tests to verify they pass**

```pwsh
dotnet test --nologo --filter "FullyQualifiedName~AttributeScannerTests"
```

Expected: 6 tests pass.

- [ ] **Step 2.5: Run full suite — zero regressions**

```pwsh
dotnet test --nologo 2>&1 | Select-String -Pattern "Passed:|Failed:|Skipped:"
```

Expected: 6 new tests added; total = baseline + Task 1 + 6.

- [ ] **Step 2.6: Commit**

```pwsh
git add src/Atlas/Internal/AttributeScanner.cs tests/Atlas.Tests/Internal/AttributeScannerTests.cs
git commit -m "AttributeScanner skeleton + IsAttributeMapCandidate filter (Task 2)`n`nNew internal static class Atlas.Internal.AttributeScanner with Discover entry`npoint and IsAttributeMapCandidate filter per design §4.1. ProcessAutoMapType is`na stub completed in subsequent tasks. Filter: top-level, public, non-abstract,`nnon-nested, non-interface, non-enum, decorated with [AutoMap]."
```

---

## Task 3 — `ValidateAutoMapTarget` rejection rules (§6 rules 1-3)

**Goal:** Reject `[AutoMap]` whose source is open-generic / dynamic-shape, and whose decorated type is open-generic / abstract / interface / static / enum. Per design §6 rules 1, 2, 3 and §9.15. Errors accumulated, never thrown immediately (the scanner runs all types before throwing aggregated).

**Files:**
- Modify: `C:\Repos\Atlas\src\Atlas\Internal\AttributeScanner.cs` (add `ValidateAutoMapTarget` and helpers; wire into `ProcessAutoMapType`)
- Modify: `C:\Repos\Atlas\tests\Atlas.Tests\Internal\AttributeScannerTests.cs` (add validation tests)

**Allowlist for the implementer subagent:** the two files above.

- [ ] **Step 3.1: Write failing tests for validation rules**

Append to `tests/Atlas.Tests/Internal/AttributeScannerTests.cs`:

```csharp
public class AttributeScannerValidationTests
{
    [Fact]
    public void OpenGenericSource_RejectedWithMessage()
    {
        var asm = BuildSingleTypeAssembly_OpenGenericSource();
        // Use a fresh MapperConfigurationExpression and the synthetic assembly,
        // OR use the real assembly via a fixture — pick whichever matches the test fixture pattern.
        // Below uses a fixture in this assembly:
        var ex = Assert.Throws<AtlasConfigurationException>(() =>
            AttributeScanner.Discover(typeof(OpenGenericSourceDto).Assembly, new MapperConfigurationExpression()));
        Assert.Contains(ex.Errors, e =>
            e.Reason.Contains("[AutoMap]") && e.Reason.Contains("open-generic source"));
    }

    [Fact]
    public void OpenGenericDestination_RejectedWithMessage()
    {
        var ex = Assert.Throws<AtlasConfigurationException>(() =>
            AttributeScanner.Discover(typeof(OpenGenericDestDto<>).Assembly, new MapperConfigurationExpression()));
        Assert.Contains(ex.Errors, e =>
            e.Reason.Contains("[AutoMap]") && e.Reason.Contains("open-generic"));
    }

    [Fact]
    public void Interface_NotDiscovered()
    {
        // Interfaces should be filtered by IsAttributeMapCandidate (IsClass = false), so
        // attribute on an interface never reaches ValidateAutoMapTarget.
        var ex = Record.Exception(() =>
            AttributeScanner.Discover(typeof(SomeInterfaceFixture).Assembly, new MapperConfigurationExpression()));
        // No exception expected because interfaces are not candidates; if AttributeUsage targets only Class, this is moot.
        Assert.Null(ex);
    }

    [Fact]
    public void EnumDecorated_RejectedWithMessage()
    {
        // Enums are technically classes (IsClass = false actually); verify either filter or rule rejects.
        // [AttributeUsage(Class)] excludes enums at compile time, so the test fixture below would not compile.
        // This test asserts the filter excludes enum types if somehow attribute encoding allowed it (defense in depth).
        var enumType = typeof(SomeEnum);
        Assert.False(AttributeScanner.IsAttributeMapCandidate(enumType));
    }

    [Fact]
    public void DynamicShapeSource_RejectedWithMessage()
    {
        var ex = Assert.Throws<AtlasConfigurationException>(() =>
            AttributeScanner.Discover(typeof(DictionarySourceDto).Assembly, new MapperConfigurationExpression()));
        Assert.Contains(ex.Errors, e =>
            e.Reason.Contains("[AutoMap]") && e.Reason.Contains("dynamic shape"));
    }

    [Fact]
    public void ExpandoSource_RejectedWithMessage()
    {
        var ex = Assert.Throws<AtlasConfigurationException>(() =>
            AttributeScanner.Discover(typeof(ExpandoSourceDto).Assembly, new MapperConfigurationExpression()));
        Assert.Contains(ex.Errors, e =>
            e.Reason.Contains("[AutoMap]") && e.Reason.Contains("dynamic shape"));
    }

    [Fact]
    public void MultipleErrors_AllReported()
    {
        // Discover a synthetic assembly with two bad fixtures; expect both errors in the aggregated exception.
        var ex = Assert.Throws<AtlasConfigurationException>(() =>
            AttributeScanner.Discover(typeof(OpenGenericSourceDto).Assembly, new MapperConfigurationExpression()));
        // The test assembly contains (at minimum) OpenGenericSourceDto and OpenGenericDestDto, plus DictionarySourceDto, plus ExpandoSourceDto.
        // Verify the aggregated exception lists more than one rejection.
        Assert.True(ex.Errors.Count >= 2,
            $"Expected at least 2 errors in aggregated exception, got {ex.Errors.Count}.");
    }
}

public class SomeSource { public int X { get; set; } }
public enum SomeEnum { A, B }

public interface SomeInterfaceFixture { int X { get; } }

public class OpenGenericSourceDto<T> where T : class { public int X { get; set; } }   // not a candidate (open-generic dest catches it)

[AutoMap(typeof(System.Collections.Generic.List<>))]
public class OpenGenericSourceFixture { public int X { get; set; } }   // open-generic SOURCE

[AutoMap(typeof(SomeSource))]
public class OpenGenericDestDto<T> where T : class { public int X { get; set; } }   // open-generic DEST

[AutoMap(typeof(System.Collections.Generic.Dictionary<string, object>))]
public class DictionarySourceDto { public int X { get; set; } }

[AutoMap(typeof(System.Dynamic.ExpandoObject))]
public class ExpandoSourceDto { public int X { get; set; } }
```

Notes for the implementer:
- The test fixtures live in `Atlas.Tests` namespace (top-level of the test assembly), which is a normal pattern. They WILL be discovered by any `AddMaps(typeof(this).Assembly)` call elsewhere in the test suite — make sure other tests don't mix them in.
- `OpenGenericDestDto<>` is a generic type definition; calling `typeof(OpenGenericDestDto<>)` (the open form) is the candidate check. Since `IsGenericTypeDefinition == true` and `IsAbstract == false`, the filter sees it as a candidate; the `ValidateAutoMapTarget` rule rejects.

- [ ] **Step 3.2: Run tests to verify they fail**

```pwsh
dotnet test --nologo --filter "FullyQualifiedName~AttributeScannerValidationTests"
```

Expected: tests fail because `ProcessAutoMapType` is still a stub — no errors are accumulated, so `Discover` returns without throwing.

- [ ] **Step 3.3: Implement `ValidateAutoMapTarget` and helpers; wire into `ProcessAutoMapType`**

Replace the body of `AttributeScanner.cs`'s `ProcessAutoMapType` and add helpers below it:

```csharp
private static void ProcessAutoMapType(Type decoratedType, MapperConfigurationExpression cfg, List<ConfigurationError> errors)
{
    var attr = decoratedType.GetCustomAttribute<AutoMapAttribute>(inherit: false)!;
    if (!ValidateAutoMapTarget(decoratedType, attr, errors))
        return;

    // CreateMap + member attribute application + class-level flags lands in Tasks 5/6/7/8.
}

private static bool ValidateAutoMapTarget(Type decoratedType, AutoMapAttribute attr, List<ConfigurationError> errors)
{
    var srcType = attr.SourceType;
    var dstType = decoratedType;

    // Rule: dest is open-generic
    if (dstType.IsGenericTypeDefinition || dstType.ContainsGenericParameters)
    {
        errors.Add(new(srcType, dstType, "(register)",
            $"[AutoMap] applied to open-generic type '{FormatTypeName(dstType)}'. " +
            $"Use cfg.CreateMap(typeof({FormatTypeName(srcType)}<>), typeof({FormatTypeName(dstType)}<>)) for open-generic registrations."));
        return false;
    }

    // Rule: dest is enum (defense in depth; AttributeUsage(Class) usually catches at compile)
    if (dstType.IsEnum)
    {
        errors.Add(new(srcType, dstType, "(register)",
            $"[AutoMap] applied to enum '{dstType.Name}'. Use cfg.CreateMap<TSrcEnum, {dstType.Name}>().MapByName() (or similar) for enum-to-enum mappings."));
        return false;
    }

    // Rule: dest is interface (filter usually catches; defense in depth)
    if (dstType.IsInterface)
    {
        errors.Add(new(srcType, dstType, "(register)",
            $"[AutoMap] applied to interface '{dstType.Name}'. Atlas cannot instantiate interfaces; use a concrete destination type."));
        return false;
    }

    // Rule: dest is static (encoded as IsAbstract && IsSealed in CLR)
    if (dstType is { IsAbstract: true, IsSealed: true })
    {
        errors.Add(new(srcType, dstType, "(register)",
            $"[AutoMap] applied to static type '{dstType.Name}'. Static types cannot be mapping destinations."));
        return false;
    }

    // Rule: dest is abstract (filter usually catches; defense in depth — IsAbstract without IsSealed)
    if (dstType.IsAbstract)
    {
        errors.Add(new(srcType, dstType, "(register)",
            $"[AutoMap] applied to abstract type '{dstType.Name}'. Atlas cannot instantiate abstract destinations."));
        return false;
    }

    // Rule: source is open-generic
    if (srcType.IsGenericTypeDefinition)
    {
        errors.Add(new(srcType, dstType, "(register)",
            $"[AutoMap] on '{dstType.Name}' specifies open-generic source type '{FormatTypeName(srcType)}'. " +
            $"Open generics use cfg.CreateMap(typeof({FormatTypeName(srcType)}<>), typeof({FormatTypeName(dstType)}<>)) — " +
            $"attribute syntax is not supported for open generics."));
        return false;
    }

    // Rule: source is a recognized dynamic shape
    if (DynamicShape.IsDynamicShape(srcType))
    {
        errors.Add(new(srcType, dstType, "(register)",
            $"[AutoMap] on '{dstType.Name}' specifies a recognized dynamic shape ('{FormatTypeName(srcType)}'). " +
            $"Dynamic mapping is convention-only and requires no registration — remove the attribute and call mapper.Map<{dstType.Name}>(dictInstance) directly. " +
            $"To explicitly register a non-dynamic mapping for this pair, use cfg.CreateMap<{FormatTypeName(srcType)}, {dstType.Name}>() in a profile."));
        return false;
    }

    return true;
}

private static string FormatTypeName(Type t)
{
    if (!t.IsGenericType) return t.Name;
    var name = t.Name;
    var tickIdx = name.IndexOf('`');
    if (tickIdx >= 0) name = name[..tickIdx];
    if (t.IsGenericTypeDefinition) return $"{name}<>";
    var args = string.Join(", ", t.GetGenericArguments().Select(FormatTypeName));
    return $"{name}<{args}>";
}
```

Note: `DynamicShape.IsDynamicShape` is the exact internal API name (verified in `src/Atlas/Internal/DynamicShape.cs`).

- [ ] **Step 3.4: Run tests to verify they pass**

```pwsh
dotnet test --nologo --filter "FullyQualifiedName~AttributeScannerValidationTests"
```

Expected: 7 new tests pass.

- [ ] **Step 3.5: Run full suite — zero regressions**

```pwsh
dotnet test --nologo 2>&1 | Select-String -Pattern "Passed:|Failed:|Skipped:"
```

- [ ] **Step 3.6: Commit**

```pwsh
git add src/Atlas/Internal/AttributeScanner.cs tests/Atlas.Tests/Internal/AttributeScannerTests.cs
git commit -m "AttributeScanner: ValidateAutoMapTarget rules 1-3 (Task 3)`n`nReject [AutoMap] when destination is open-generic / abstract / interface /`nstatic / enum, OR when source is open-generic / dynamic shape. Per design §6.`nErrors accumulated for aggregate AtlasConfigurationException at end of scan.`nUses existing DynamicShape.IsDynamicShape internal helper from PR #10."
```

---

## Task 4 — `BuildSourcePathExpression` path walker (§5.6)

**Goal:** Implement the source-path resolver used by `[SourceMember]` (Task 7). Walks dotted segments against the source type, validates each segment is a public readable property or a public field, returns the chained `MemberExpression` plus leaf type. Errors accumulated for missing-segment / non-readable cases.

**Files:**
- Modify: `C:\Repos\Atlas\src\Atlas\Internal\AttributeScanner.cs`
- Modify: `C:\Repos\Atlas\tests\Atlas.Tests\Internal\AttributeScannerTests.cs` (path resolver tests)

**Allowlist for the implementer subagent:** the two files above.

- [ ] **Step 4.1: Write failing tests for path walker**

Append to `AttributeScannerTests.cs`:

```csharp
public class AttributeScannerPathTests
{
    private static (LambdaExpression? lambda, Type? leaf, List<ConfigurationError> errors) Walk(Type src, string path)
    {
        var errors = new List<ConfigurationError>();
        // Use reflection to call the private BuildSourcePathExpression method for unit testing,
        // OR test it indirectly via a public test harness method. Recommended: add an internal
        // overload of BuildSourcePathExpression with [InternalsVisibleTo("Atlas.Tests")] so tests
        // can call it directly. Atlas already exposes internals via Atlas.Tests friend assembly
        // (see existing Internal tests like MappingContextTests).
        var lambda = AttributeScanner.BuildSourcePathExpressionForTest(
            src, path, "TestDest", typeof(TestDest), errors, out var leaf);
        return (lambda, leaf, errors);
    }

    public class TestDest { public string Field { get; set; } = ""; }

    public class FlatSource { public int X { get; set; } public string Y { get; set; } = ""; }

    public class NestedSource
    {
        public Customer Customer { get; set; } = new();
    }
    public class Customer
    {
        public string Name { get; set; } = "";
        public Address Address { get; set; } = new();
    }
    public class Address { public string City { get; set; } = ""; }

    public class WriteOnlySource
    {
        private string _x = "";
        public string X { set { _x = value; } }
    }

    public class FieldSource
    {
        public int Field;
    }

    [Fact]
    public void Flat_ResolvesProperty()
    {
        var (lambda, leaf, errors) = Walk(typeof(FlatSource), "X");
        Assert.NotNull(lambda);
        Assert.Equal(typeof(int), leaf);
        Assert.Empty(errors);
    }

    [Fact]
    public void Dotted_ResolvesNestedProperty()
    {
        var (lambda, leaf, errors) = Walk(typeof(NestedSource), "Customer.Name");
        Assert.NotNull(lambda);
        Assert.Equal(typeof(string), leaf);
        Assert.Empty(errors);
    }

    [Fact]
    public void DottedTwoLevel_ResolvesDeepNestedProperty()
    {
        var (lambda, leaf, errors) = Walk(typeof(NestedSource), "Customer.Address.City");
        Assert.NotNull(lambda);
        Assert.Equal(typeof(string), leaf);
        Assert.Empty(errors);
    }

    [Fact]
    public void MissingSegment_ProducesStructuredError()
    {
        var (lambda, leaf, errors) = Walk(typeof(NestedSource), "Customer.Missing");
        Assert.Null(lambda);
        Assert.Null(leaf);
        Assert.Single(errors);
        Assert.Contains("Missing", errors[0].Reason);
        Assert.Contains("Customer", errors[0].Reason);
    }

    [Fact]
    public void WriteOnlyLeaf_ProducesNonReadableError()
    {
        var (lambda, leaf, errors) = Walk(typeof(WriteOnlySource), "X");
        Assert.Null(lambda);
        Assert.Single(errors);
        Assert.Contains("no public getter", errors[0].Reason);
    }

    [Fact]
    public void FieldLeaf_Resolves()
    {
        var (lambda, leaf, errors) = Walk(typeof(FieldSource), "Field");
        Assert.NotNull(lambda);
        Assert.Equal(typeof(int), leaf);
        Assert.Empty(errors);
    }
}
```

- [ ] **Step 4.2: Run tests to verify they fail (compile error: `BuildSourcePathExpressionForTest` doesn't exist)**

```pwsh
dotnet test --nologo --filter "FullyQualifiedName~AttributeScannerPathTests"
```

Expected: build error. Atlas already has `[InternalsVisibleTo("Atlas.Tests")]` (verified by existence of `MappingContextTests`). The test method name signals it's a test-only entry point.

- [ ] **Step 4.3: Implement `BuildSourcePathExpression` and the test entry point**

Add to `AttributeScanner.cs`:

```csharp
using System.Linq.Expressions;

// ... inside class AttributeScanner ...

/// <summary>
/// Walks <paramref name="dottedPath"/> against <paramref name="srcType"/>, building a
/// chained MemberExpression. Each segment must resolve to a public readable property or
/// a public field. Errors are appended to <paramref name="errors"/> and the method returns
/// <c>null</c> on failure.
/// </summary>
internal static LambdaExpression? BuildSourcePathExpression(
    Type srcType, string dottedPath, string destMemberName, Type decoratedType,
    List<ConfigurationError> errors, out Type? leafType)
{
    leafType = null;
    var segments = dottedPath.Split('.');
    var srcParam = Expression.Parameter(srcType, "s");
    Expression current = srcParam;
    Type currentType = srcType;

    for (int i = 0; i < segments.Length; i++)
    {
        var segment = segments[i];

        var prop = currentType.GetProperty(segment, BindingFlags.Public | BindingFlags.Instance);
        FieldInfo? field = null;
        if (prop is null)
            field = currentType.GetField(segment, BindingFlags.Public | BindingFlags.Instance);

        if (prop is null && field is null)
        {
            errors.Add(new(srcType, decoratedType, destMemberName,
                $"[SourceMember(\"{dottedPath}\")] on '{decoratedType.Name}.{destMemberName}' — " +
                $"segment '{segment}' not found on '{currentType.Name}'."));
            return null;
        }

        if (prop is { CanRead: false })
        {
            errors.Add(new(srcType, decoratedType, destMemberName,
                $"[SourceMember(\"{dottedPath}\")] on '{decoratedType.Name}.{destMemberName}' — " +
                $"segment '{segment}' on '{currentType.Name}' has no public getter."));
            return null;
        }

        if (prop is not null)
        {
            current = Expression.Property(current, prop);
            currentType = prop.PropertyType;
        }
        else
        {
            current = Expression.Field(current, field!);
            currentType = field!.FieldType;
        }
    }

    leafType = currentType;
    var funcType = typeof(Func<,>).MakeGenericType(srcType, leafType);
    return Expression.Lambda(funcType, current, srcParam);
}

// Test-only entry point (visible via InternalsVisibleTo to Atlas.Tests)
internal static LambdaExpression? BuildSourcePathExpressionForTest(
    Type srcType, string dottedPath, string destMemberName, Type decoratedType,
    List<ConfigurationError> errors, out Type? leafType) =>
    BuildSourcePathExpression(srcType, dottedPath, destMemberName, decoratedType, errors, out leafType);
```

- [ ] **Step 4.4: Run tests to verify they pass**

```pwsh
dotnet test --nologo --filter "FullyQualifiedName~AttributeScannerPathTests"
```

Expected: 6 new tests pass.

- [ ] **Step 4.5: Run full suite — zero regressions**

```pwsh
dotnet test --nologo 2>&1 | Select-String -Pattern "Passed:|Failed:|Skipped:"
```

- [ ] **Step 4.6: Commit**

```pwsh
git add src/Atlas/Internal/AttributeScanner.cs tests/Atlas.Tests/Internal/AttributeScannerTests.cs
git commit -m "AttributeScanner: BuildSourcePathExpression path walker (Task 4)`n`nDotted-path resolver per design §5.6. Walks segments against source type using`nBindingFlags.Public | Instance for both properties and fields. Structured errors`nfor missing-segment and write-only-leaf cases. Test entry point exposed via`nInternalsVisibleTo to Atlas.Tests."
```

---

## Task 5 — `InvokeCreateMap` + `ApplyClassLevelFlags` (§5.2 + §5.5)

**Goal:** Wire up the heart of attribute → fluent translation: invoke `cfg.CreateMap<TSrc, TDst>(MemberList)` via `MakeGenericMethod`, set `RegistrationOrigin` to identify the attribute source, then call `.PreserveReferences()` and `.ReverseMap()` based on attribute flags. Member-level attributes still skipped — that's Tasks 6/7/8. After this task, attribute discovery produces correctly-flagged convention-only maps end-to-end.

**Files:**
- Modify: `C:\Repos\Atlas\src\Atlas\Internal\AttributeScanner.cs`
- Modify: `C:\Repos\Atlas\tests\Atlas.Tests\AutoMapAttributeTests.cs` (behavioral tests for the class-level flags)
- Modify: `C:\Repos\Atlas\tests\Atlas.Tests\Internal\AttributeScannerTests.cs` (mechanic tests)

**Allowlist for the implementer subagent:** the three files above.

- [ ] **Step 5.1: Write failing tests for class-level translation**

Append to `tests/Atlas.Tests/AutoMapAttributeTests.cs`:

```csharp
public class AutoMapAttributeBehaviorTests
{
    [Fact]
    public void AttributeMap_ConventionOnlyMember_Resolves()
    {
        var cfg = new MapperConfiguration(c => c.AddMaps(typeof(AttributeMapMinimumDto).Assembly));
        var mapper = cfg.CreateMapper();
        var dto = mapper.Map<AttributeMapMinimumDto>(new AttributeMapMinimumSource { Id = 7 });
        Assert.Equal(7, dto.Id);
    }

    [Fact]
    public void AttributeMap_MemberListSource_PassesValidation_WhenSourceCovered()
    {
        var cfg = new MapperConfiguration(c => c.AddMaps(typeof(AttributeMapSourceListDto).Assembly));
        // Should NOT throw — every source member maps to a destination by convention.
        cfg.AssertConfigurationIsValid();
    }

    [Fact]
    public void AttributeMap_RegistrationOrigin_NamesAttribute()
    {
        var expr = new MapperConfigurationExpression();
        Atlas.Internal.AttributeScanner.Discover(typeof(AttributeMapMinimumDto).Assembly, expr);
        var tm = expr.GetTypeMapsForTest()
                     .First(t => t.SourceType == typeof(AttributeMapMinimumSource)
                              && t.DestinationType == typeof(AttributeMapMinimumDto));
        Assert.Contains("[AutoMap", tm.RegistrationOrigin);
        Assert.Contains(nameof(AttributeMapMinimumDto), tm.RegistrationOrigin);
    }

    [Fact]
    public void AttributeMap_PreserveReferencesTrue_FlagPropagated()
    {
        var expr = new MapperConfigurationExpression();
        Atlas.Internal.AttributeScanner.Discover(typeof(AttributeMapPreserveDto).Assembly, expr);
        var tm = expr.GetTypeMapsForTest()
                     .First(t => t.DestinationType == typeof(AttributeMapPreserveDto));
        Assert.True(tm.PreserveReferences);
    }

    [Fact]
    public void AttributeMap_ReverseMapTrue_ReversePairRegistered()
    {
        var expr = new MapperConfigurationExpression();
        Atlas.Internal.AttributeScanner.Discover(typeof(AttributeMapReverseDto).Assembly, expr);
        Assert.Contains(expr.GetTypeMapsForTest(), t =>
            t.SourceType == typeof(AttributeMapReverseDto) && t.DestinationType == typeof(AttributeMapReverseSource));
        Assert.Contains(expr.GetTypeMapsForTest(), t =>
            t.SourceType == typeof(AttributeMapReverseSource) && t.DestinationType == typeof(AttributeMapReverseDto));
    }

    [Fact]
    public void AttributeMap_PreserveReferencesAndReverseMap_FlagPropagatesToReversePair()
    {
        var expr = new MapperConfigurationExpression();
        Atlas.Internal.AttributeScanner.Discover(typeof(AttributeMapPreserveReverseDto).Assembly, expr);
        var fwd = expr.GetTypeMapsForTest()
                      .First(t => t.SourceType == typeof(AttributeMapPreserveReverseSource));
        var rev = expr.GetTypeMapsForTest()
                      .First(t => t.DestinationType == typeof(AttributeMapPreserveReverseSource));
        Assert.True(fwd.PreserveReferences);
        Assert.True(rev.PreserveReferences);
    }
}

public class AttributeMapMinimumSource { public int Id { get; set; } }
[AutoMap(typeof(AttributeMapMinimumSource))]
public class AttributeMapMinimumDto { public int Id { get; set; } }

public class AttributeMapSourceListSource { public int Id { get; set; } public string Name { get; set; } = ""; }
[AutoMap(typeof(AttributeMapSourceListSource), MemberList = MemberList.Source)]
public class AttributeMapSourceListDto { public int Id { get; set; } public string Name { get; set; } = ""; }

public class AttributeMapPreserveSource { public int Id { get; set; } }
[AutoMap(typeof(AttributeMapPreserveSource), PreserveReferences = true)]
public class AttributeMapPreserveDto { public int Id { get; set; } }

public class AttributeMapReverseSource { public int Id { get; set; } }
[AutoMap(typeof(AttributeMapReverseSource), ReverseMap = true)]
public class AttributeMapReverseDto { public int Id { get; set; } }

public class AttributeMapPreserveReverseSource { public int Id { get; set; } }
[AutoMap(typeof(AttributeMapPreserveReverseSource), PreserveReferences = true, ReverseMap = true)]
public class AttributeMapPreserveReverseDto { public int Id { get; set; } }
```

The above tests assume `MapperConfigurationExpression.GetTypeMapsForTest()` exists or is accessible. The existing `internal IReadOnlyList<TypeMap> GetTypeMaps()` (verified at line 183) is already exposed via `[InternalsVisibleTo("Atlas.Tests")]`. Use `GetTypeMaps()` directly in tests rather than adding a new method.

Replace `GetTypeMapsForTest()` → `GetTypeMaps()` throughout the test code above.

- [ ] **Step 5.2: Run tests to verify they fail**

```pwsh
dotnet test --nologo --filter "FullyQualifiedName~AutoMapAttributeBehaviorTests"
```

Expected: tests fail because (a) the scanner isn't yet wired into `AddMaps` (so `cfg.CreateMapper()`-routed tests find no map), and (b) `ProcessAutoMapType` is still a Task-3 stub past validation.

Note: Tests #1 and #2 use `c.AddMaps(asm)` which won't trigger discovery yet (Task 10 wires it). For now, those tests should be marked as expected-to-fail until Task 10. Reorganize them so this task only verifies the scanner output via direct `AttributeScanner.Discover(asm, expr)` invocation; the `AddMaps`-based tests move to Task 10.

Adjusted plan: tests #1 and #2 above belong in Task 10 (after the wiring). For Task 5, only tests #3-#6 (which use direct `AttributeScanner.Discover`) belong here. **Move tests #1 and #2 to Task 10's test list** (this plan tracks the move; the implementer should only add tests #3-#6 in Task 5).

- [ ] **Step 5.3: Implement `InvokeCreateMap`, `ApplyClassLevelFlags`, and wire into `ProcessAutoMapType`**

Update `AttributeScanner.cs` — add static `MethodInfo` cache + new helpers:

```csharp
// At class scope (file-level static initializers):
private static readonly MethodInfo CreateMapOpenMethodInfo =
    typeof(MapperConfigurationExpression)
        .GetMethods()
        .Single(m => m.Name == nameof(MapperConfigurationExpression.CreateMap)
                  && m.IsGenericMethodDefinition
                  && m.GetParameters().Length == 1
                  && m.GetParameters()[0].ParameterType == typeof(MemberList));
```

Update `ProcessAutoMapType`:

```csharp
private static void ProcessAutoMapType(Type decoratedType, MapperConfigurationExpression cfg, List<ConfigurationError> errors)
{
    var attr = decoratedType.GetCustomAttribute<AutoMapAttribute>(inherit: false)!;
    if (!ValidateAutoMapTarget(decoratedType, attr, errors))
        return;

    var srcType = attr.SourceType;
    var mappingExpression = InvokeCreateMap(cfg, srcType, decoratedType, attr.MemberList);
    SetRegistrationOrigin(cfg, srcType, decoratedType);

    // Member-level attribute application lands in Tasks 6/7/8.
    // ApplyMemberAttributes(mappingExpression, srcType, decoratedType, errors);

    ApplyClassLevelFlags(mappingExpression, srcType, decoratedType, attr);
}
```

Add the helpers:

```csharp
private static object InvokeCreateMap(MapperConfigurationExpression cfg, Type srcType, Type dstType, MemberList memberList)
{
    var createMapClosed = CreateMapOpenMethodInfo.MakeGenericMethod(srcType, dstType);
    try
    {
        return createMapClosed.Invoke(cfg, [memberList])!;
    }
    catch (TargetInvocationException tie) when (tie.InnerException is AtlasConfigurationException acex)
    {
        // Universal duplicate-pair rule fired (Task 9). Unwrap so the user sees the proper exception type.
        ExceptionDispatchInfo.Capture(acex).Throw();
        throw; // unreachable
    }
}

/// <summary>
/// Sets <see cref="TypeMap.RegistrationOrigin"/> on the just-created TypeMap so error messages
/// for duplicate-pair conflicts cite the attribute source rather than a synthesized fluent call.
/// </summary>
private static void SetRegistrationOrigin(MapperConfigurationExpression cfg, Type srcType, Type dstType)
{
    var pair = new TypePair(srcType, dstType);
    var tm = cfg.GetTypeMaps().FirstOrDefault(t => t.SourceType == srcType && t.DestinationType == dstType);
    if (tm is not null)
    {
        tm.RegistrationOrigin = $"[AutoMap(typeof({srcType.Name}))] on {dstType.Name}";
    }
}

private static void ApplyClassLevelFlags(object mappingExpression, Type srcType, Type dstType, AutoMapAttribute attr)
{
    var imappingExprClosed = typeof(Atlas.Configuration.IMappingExpression<,>).MakeGenericType(srcType, dstType);

    if (attr.PreserveReferences)
    {
        var method = imappingExprClosed.GetMethod(
            nameof(Atlas.Configuration.IMappingExpression<object, object>.PreserveReferences),
            Type.EmptyTypes)!;
        method.Invoke(mappingExpression, null);
    }

    if (attr.ReverseMap)
    {
        var method = imappingExprClosed.GetMethod(
            nameof(Atlas.Configuration.IMappingExpression<object, object>.ReverseMap),
            [typeof(MemberList)])!;
        method.Invoke(mappingExpression, [MemberList.None]);
    }
}
```

- [ ] **Step 5.4: Run tests to verify they pass**

```pwsh
dotnet test --nologo --filter "FullyQualifiedName~AutoMapAttributeBehaviorTests&DisplayName~RegistrationOrigin|DisplayName~PreserveReferencesTrue_FlagPropagated|DisplayName~ReverseMapTrue_ReversePairRegistered|DisplayName~PreserveReferencesAndReverseMap"
```

Expected: 4 new tests pass (#3-#6 from above).

- [ ] **Step 5.5: Run full suite — zero regressions**

```pwsh
dotnet test --nologo 2>&1 | Select-String -Pattern "Passed:|Failed:|Skipped:"
```

- [ ] **Step 5.6: Commit**

```pwsh
git add src/Atlas/Internal/AttributeScanner.cs tests/Atlas.Tests/AutoMapAttributeTests.cs tests/Atlas.Tests/Internal/AttributeScannerTests.cs
git commit -m "AttributeScanner: InvokeCreateMap + ApplyClassLevelFlags (Task 5)`n`nWire cfg.CreateMap<S,D>() via MakeGenericMethod per design §5.2; set`nRegistrationOrigin to '[AutoMap(typeof(...))] on Dst'. ApplyClassLevelFlags`nemits .PreserveReferences() then .ReverseMap() in fixed order (bidirectional`npropagation handles either order safely per PR #11). TargetInvocationException`nunwraps to AtlasConfigurationException via ExceptionDispatchInfo (matches`nPR #10 Mapper.cs pattern). Member-level attributes deferred to Tasks 6-8."
```

---

## Task 6 — `[Ignore]` member attribute (§5.4)

**Goal:** First member-level attribute. The simplest because it has no source-side dependencies — just calls `opt.Ignore()` inside the `ForMember` callback. Builds the reflection scaffolding (`IMemberConfigurationExpression<,,>` resolution, `Expression.Lambda` with optional `Block`) that Tasks 7 and 8 will extend.

**Files:**
- Modify: `C:\Repos\Atlas\src\Atlas\Internal\AttributeScanner.cs`
- Modify: `C:\Repos\Atlas\tests\Atlas.Tests\IgnoreAttributeTests.cs`

**Allowlist for the implementer subagent:** the two files above.

- [ ] **Step 6.1: Write failing tests for `[Ignore]` behavior**

Replace `tests/Atlas.Tests/IgnoreAttributeTests.cs` (preserving the existing `AttributeUsage` test):

```csharp
using System.Reflection;

namespace Atlas.Tests;

public class IgnoreAttributeTests
{
    [Fact]
    public void AttributeUsage_TargetsPropertyOnly_NotInheritedNotMultiple_Sealed()
    {
        var usage = typeof(IgnoreAttribute).GetCustomAttribute<AttributeUsageAttribute>();
        Assert.NotNull(usage);
        Assert.Equal(AttributeTargets.Property, usage!.ValidOn);
        Assert.False(usage.Inherited);
        Assert.False(usage.AllowMultiple);
        Assert.True(typeof(IgnoreAttribute).IsSealed);
    }

    [Fact]
    public void Ignored_PropertyExcludedFromMapping()
    {
        var expr = new MapperConfigurationExpression();
        Atlas.Internal.AttributeScanner.Discover(typeof(IgnoreFixtureDto).Assembly, expr);
        var cfg = new MapperConfiguration(c =>
        {
            c.AddProfile(new IgnoreFixtureProfile()); // a profile that re-registers nothing
        });
        // Use Discover-built config:
        var built = new MapperConfiguration(c => Atlas.Internal.AttributeScanner.Discover(
            typeof(IgnoreFixtureDto).Assembly, c));
        var mapper = built.CreateMapper();
        var src = new IgnoreFixtureSource { Id = 1, Skipped = "should be skipped" };
        var dto = mapper.Map<IgnoreFixtureDto>(src);
        Assert.Equal(1, dto.Id);
        Assert.Null(dto.Skipped);
    }

    [Fact]
    public void Ignored_PropertyExcludedFromValidation()
    {
        var built = new MapperConfiguration(c => Atlas.Internal.AttributeScanner.Discover(
            typeof(IgnoreFixtureValidationDto).Assembly, c));
        // MemberList.Destination on this DTO; Skipped is unmapped but [Ignore]'d, so validation passes.
        built.AssertConfigurationIsValid();
    }

    [Fact]
    public void Ignore_OnPropertyWithoutAutoMap_SilentlyNoOp()
    {
        // Class is NOT decorated with [AutoMap]; its [Ignore] property is silently ignored.
        // No registration is created. Verify Discover doesn't throw and no TypeMap exists.
        var expr = new MapperConfigurationExpression();
        Atlas.Internal.AttributeScanner.Discover(typeof(IgnoreOrphanFixture).Assembly, expr);
        Assert.DoesNotContain(expr.GetTypeMaps(), t => t.DestinationType == typeof(IgnoreOrphanFixture));
    }

    [Fact]
    public void Ignored_UpdateInPlace_PreservesExistingValue()
    {
        var built = new MapperConfiguration(c => Atlas.Internal.AttributeScanner.Discover(
            typeof(IgnoreFixtureDto).Assembly, c));
        var mapper = built.CreateMapper();
        var existing = new IgnoreFixtureDto { Id = 0, Skipped = "do not touch" };
        var src = new IgnoreFixtureSource { Id = 99, Skipped = "ignored" };
        mapper.Map(src, existing);
        Assert.Equal(99, existing.Id);
        Assert.Equal("do not touch", existing.Skipped);
    }
}

public class IgnoreFixtureSource
{
    public int Id { get; set; }
    public string? Skipped { get; set; }
}

[AutoMap(typeof(IgnoreFixtureSource))]
public class IgnoreFixtureDto
{
    public int Id { get; set; }
    [Ignore]
    public string? Skipped { get; set; }
}

[AutoMap(typeof(IgnoreFixtureSource), MemberList = MemberList.Destination)]
public class IgnoreFixtureValidationDto
{
    public int Id { get; set; }
    [Ignore]
    public string? Skipped { get; set; }   // unmapped without [Ignore]; covered by [Ignore]
}

public class IgnoreOrphanFixture
{
    public int Id { get; set; }
    [Ignore]
    public string Skipped { get; set; } = "";
}

// Empty profile placeholder for the test in IgnoreFixtureDto-related tests.
public class IgnoreFixtureProfile : MapperProfile { }
```

- [ ] **Step 6.2: Run tests to verify they fail**

```pwsh
dotnet test --nologo --filter "FullyQualifiedName~IgnoreAttributeTests"
```

Expected: tests fail because `ProcessAutoMapType` does not yet apply member-level attributes — `Skipped` is convention-mapped, not ignored.

- [ ] **Step 6.3: Implement `ApplyMemberAttributes` for `[Ignore]`**

Update `AttributeScanner.cs`:

```csharp
// At class scope:
private const string IgnoreMethodName =
    nameof(Atlas.Configuration.IMemberConfigurationExpression<object, object, object>.Ignore);

// In ProcessAutoMapType, uncomment the ApplyMemberAttributes call:
private static void ProcessAutoMapType(Type decoratedType, MapperConfigurationExpression cfg, List<ConfigurationError> errors)
{
    var attr = decoratedType.GetCustomAttribute<AutoMapAttribute>(inherit: false)!;
    if (!ValidateAutoMapTarget(decoratedType, attr, errors))
        return;

    var srcType = attr.SourceType;
    var mappingExpression = InvokeCreateMap(cfg, srcType, decoratedType, attr.MemberList);
    SetRegistrationOrigin(cfg, srcType, decoratedType);

    ApplyMemberAttributes(mappingExpression, srcType, decoratedType, errors);
    ApplyClassLevelFlags(mappingExpression, srcType, decoratedType, attr);
}

/// <summary>
/// Iterates destination properties and applies [Ignore] / [SourceMember] / [NullSubstitute]
/// per-property via reflection-built ForMember invocations. See design §5.4.
/// </summary>
private static void ApplyMemberAttributes(object mappingExpression, Type srcType, Type dstType, List<ConfigurationError> errors)
{
    var imappingExprClosed = typeof(Atlas.Configuration.IMappingExpression<,>).MakeGenericType(srcType, dstType);
    var forMemberOpen = imappingExprClosed.GetMethods()
        .Single(m => m.Name == nameof(Atlas.Configuration.IMappingExpression<object, object>.ForMember)
                  && m.IsGenericMethodDefinition);

    foreach (var prop in dstType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
    {
        var ignore = prop.GetCustomAttribute<IgnoreAttribute>(inherit: false);
        var sourceMember = prop.GetCustomAttribute<SourceMemberAttribute>(inherit: false);
        var nullSubstitute = prop.GetCustomAttribute<NullSubstituteAttribute>(inherit: false);

        if (ignore is null && sourceMember is null && nullSubstitute is null)
            continue;

        var memberType = prop.PropertyType;
        var imemberConfigClosed = typeof(Atlas.Configuration.IMemberConfigurationExpression<,,>)
            .MakeGenericType(srcType, dstType, memberType);
        var optParam = Expression.Parameter(imemberConfigClosed, "opt");

        var statements = new List<Expression>();

        if (ignore is not null)
        {
            // [Ignore] short-circuits — emit only Ignore() and ignore other attributes on this property.
            var ignoreMethod = imemberConfigClosed.GetMethod(IgnoreMethodName, Type.EmptyTypes)!;
            statements.Add(Expression.Call(optParam, ignoreMethod));
        }
        else
        {
            // [SourceMember] and [NullSubstitute] handled in Tasks 7 and 8.
        }

        if (statements.Count == 0)
            continue;

        var body = statements.Count == 1 ? statements[0] : (Expression)Expression.Block(statements);
        var actionType = typeof(Action<>).MakeGenericType(imemberConfigClosed);
        var optionsCallback = Expression.Lambda(actionType, body, optParam).Compile();

        // Build d => d.X selector
        var dstParam = Expression.Parameter(dstType, "d");
        var memberAccess = Expression.Property(dstParam, prop);
        var funcType = typeof(Func<,>).MakeGenericType(dstType, memberType);
        var selector = Expression.Lambda(funcType, memberAccess, dstParam);

        var forMemberClosed = forMemberOpen.MakeGenericMethod(memberType);
        try
        {
            forMemberClosed.Invoke(mappingExpression, [selector, optionsCallback]);
        }
        catch (TargetInvocationException tie) when (tie.InnerException is AtlasConfigurationException acex)
        {
            ExceptionDispatchInfo.Capture(acex).Throw();
        }
    }
}
```

- [ ] **Step 6.4: Run tests to verify they pass**

```pwsh
dotnet test --nologo --filter "FullyQualifiedName~IgnoreAttributeTests"
```

Expected: 5 tests pass (1 existing AttributeUsage + 4 new behavioral).

- [ ] **Step 6.5: Run full suite — zero regressions**

```pwsh
dotnet test --nologo 2>&1 | Select-String -Pattern "Passed:|Failed:|Skipped:"
```

- [ ] **Step 6.6: Commit**

```pwsh
git add src/Atlas/Internal/AttributeScanner.cs tests/Atlas.Tests/IgnoreAttributeTests.cs
git commit -m "AttributeScanner: ApplyMemberAttributes for [Ignore] (Task 6)`n`nFirst member-level attribute. Reflection scaffolding to resolve the closed`nIMemberConfigurationExpression<TS,TD,TM>, build an Action<...> lambda via`nExpression.Lambda+Compile, and invoke ForMember<TMember>(selector, callback)`nvia MakeGenericMethod. [Ignore] short-circuits — emits only opt.Ignore().`nAdditional attributes on the same property are skipped in this task; full`ncomposition lands in Tasks 7-8."
```

---

## Task 7 — `[SourceMember]` member attribute (§5.4)

**Goal:** Second member-level attribute. Routes through the path walker from Task 4. `[Ignore]` continues to short-circuit; otherwise `[SourceMember]` produces an `opt.MapFrom<TSourceMember>(s => s.Path)` call inside the `ForMember` callback. Tests cover flat, dotted, multi-level, and error cases.

**Files:**
- Modify: `C:\Repos\Atlas\src\Atlas\Internal\AttributeScanner.cs`
- Modify: `C:\Repos\Atlas\tests\Atlas.Tests\SourceMemberAttributeTests.cs`

**Allowlist for the implementer subagent:** the two files above.

- [ ] **Step 7.1: Write failing tests for `[SourceMember]` behavior**

Append to `tests/Atlas.Tests/SourceMemberAttributeTests.cs` (preserving the existing 3 tests):

```csharp
public class SourceMemberAttributeBehaviorTests
{
    [Fact]
    public void FlatRedirect_RedirectsToOtherSourceMember()
    {
        var built = new MapperConfiguration(c => Atlas.Internal.AttributeScanner.Discover(
            typeof(SourceMemberFlatDto).Assembly, c));
        var mapper = built.CreateMapper();
        var dto = mapper.Map<SourceMemberFlatDto>(new SourceMemberFlatSource { OriginalName = "Alice" });
        Assert.Equal("Alice", dto.RedirectedName);
    }

    [Fact]
    public void DottedPath_FlattensFromNestedSource()
    {
        var built = new MapperConfiguration(c => Atlas.Internal.AttributeScanner.Discover(
            typeof(SourceMemberDottedDto).Assembly, c));
        var mapper = built.CreateMapper();
        var src = new SourceMemberDottedSource { Customer = new SourceMemberCustomer { Name = "Bob" } };
        var dto = mapper.Map<SourceMemberDottedDto>(src);
        Assert.Equal("Bob", dto.CustomerName);
    }

    [Fact]
    public void MultiLevelDottedPath_FlattensFromDeepSource()
    {
        var built = new MapperConfiguration(c => Atlas.Internal.AttributeScanner.Discover(
            typeof(SourceMemberDeepDto).Assembly, c));
        var mapper = built.CreateMapper();
        var src = new SourceMemberDeepSource { Customer = new SourceMemberDeepCustomer { Address = new SourceMemberDeepAddress { City = "London" } } };
        var dto = mapper.Map<SourceMemberDeepDto>(src);
        Assert.Equal("London", dto.City);
    }

    [Fact]
    public void BadPath_FailsAtConfigBuildWithStructuredError()
    {
        var ex = Assert.Throws<AtlasConfigurationException>(() =>
        {
            var expr = new MapperConfigurationExpression();
            Atlas.Internal.AttributeScanner.Discover(typeof(SourceMemberBadPathDto).Assembly, expr);
        });
        Assert.Contains(ex.Errors, e => e.Reason.Contains("Customer.Missing"));
    }

    [Fact]
    public void IgnoreShortCircuitsSourceMember_OnSameProperty()
    {
        var built = new MapperConfiguration(c => Atlas.Internal.AttributeScanner.Discover(
            typeof(SourceMemberIgnoreShortCircuitDto).Assembly, c));
        var mapper = built.CreateMapper();
        var src = new SourceMemberIgnoreShortCircuitSource { OriginalName = "Eve" };
        var dto = mapper.Map<SourceMemberIgnoreShortCircuitDto>(src);
        Assert.Null(dto.RedirectedName);   // Ignored — SourceMember is unreachable
    }
}

public class SourceMemberFlatSource { public string OriginalName { get; set; } = ""; }
[AutoMap(typeof(SourceMemberFlatSource))]
public class SourceMemberFlatDto
{
    [SourceMember(nameof(SourceMemberFlatSource.OriginalName))]
    public string RedirectedName { get; set; } = "";
}

public class SourceMemberCustomer { public string Name { get; set; } = ""; }
public class SourceMemberDottedSource { public SourceMemberCustomer Customer { get; set; } = new(); }
[AutoMap(typeof(SourceMemberDottedSource))]
public class SourceMemberDottedDto
{
    [SourceMember("Customer.Name")]
    public string CustomerName { get; set; } = "";
}

public class SourceMemberDeepAddress { public string City { get; set; } = ""; }
public class SourceMemberDeepCustomer { public SourceMemberDeepAddress Address { get; set; } = new(); }
public class SourceMemberDeepSource { public SourceMemberDeepCustomer Customer { get; set; } = new(); }
[AutoMap(typeof(SourceMemberDeepSource))]
public class SourceMemberDeepDto
{
    [SourceMember("Customer.Address.City")]
    public string City { get; set; } = "";
}

public class SourceMemberBadPathSource { public SourceMemberCustomer Customer { get; set; } = new(); }
[AutoMap(typeof(SourceMemberBadPathSource))]
public class SourceMemberBadPathDto
{
    [SourceMember("Customer.Missing")]
    public string Bad { get; set; } = "";
}

public class SourceMemberIgnoreShortCircuitSource { public string OriginalName { get; set; } = ""; }
[AutoMap(typeof(SourceMemberIgnoreShortCircuitSource))]
public class SourceMemberIgnoreShortCircuitDto
{
    [Ignore]
    [SourceMember(nameof(SourceMemberIgnoreShortCircuitSource.OriginalName))]
    public string? RedirectedName { get; set; }
}
```

- [ ] **Step 7.2: Run tests to verify they fail**

```pwsh
dotnet test --nologo --filter "FullyQualifiedName~SourceMemberAttributeBehaviorTests"
```

Expected: tests fail because `ApplyMemberAttributes` only handles `[Ignore]`.

- [ ] **Step 7.3: Extend `ApplyMemberAttributes` to handle `[SourceMember]`**

Update the `else` branch in `ApplyMemberAttributes`:

```csharp
private const string MapFromMethodName =
    nameof(Atlas.Configuration.IMemberConfigurationExpression<object, object, object>.MapFrom);

// ... inside ApplyMemberAttributes, replace the empty else branch ...

if (ignore is not null)
{
    var ignoreMethod = imemberConfigClosed.GetMethod(IgnoreMethodName, Type.EmptyTypes)!;
    statements.Add(Expression.Call(optParam, ignoreMethod));
}
else
{
    Type? sourceMemberType = null;
    if (sourceMember is not null)
    {
        var sourceLambda = BuildSourcePathExpression(srcType, sourceMember.MemberName,
            prop.Name, dstType, errors, out sourceMemberType);

        if (sourceLambda is not null && sourceMemberType is not null)
        {
            var mapFromOpen = imemberConfigClosed.GetMethods()
                .Single(m => m.Name == MapFromMethodName
                          && m.IsGenericMethodDefinition
                          && m.GetParameters().Length == 1
                          && m.GetParameters()[0].ParameterType.IsGenericType
                          && m.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == typeof(Expression<>));
            var mapFromClosed = mapFromOpen.MakeGenericMethod(sourceMemberType);
            statements.Add(Expression.Call(optParam, mapFromClosed,
                Expression.Constant(sourceLambda, sourceLambda.GetType())));
        }
    }

    // [NullSubstitute] handling lands in Task 8.
}
```

- [ ] **Step 7.4: Run tests to verify they pass**

```pwsh
dotnet test --nologo --filter "FullyQualifiedName~SourceMemberAttributeBehaviorTests"
```

Expected: 5 new tests pass.

- [ ] **Step 7.5: Run full suite — zero regressions**

```pwsh
dotnet test --nologo 2>&1 | Select-String -Pattern "Passed:|Failed:|Skipped:"
```

- [ ] **Step 7.6: Commit**

```pwsh
git add src/Atlas/Internal/AttributeScanner.cs tests/Atlas.Tests/SourceMemberAttributeTests.cs
git commit -m "AttributeScanner: ApplyMemberAttributes for [SourceMember] (Task 7)`n`nReflection-built MapFrom<TSourceMember>(Expression<Func<TSrc, TSourceMember>>)`ncall inside ForMember callback. Routes through Task 4's BuildSourcePathExpression`nfor flat / dotted / multi-level resolution. [Ignore] continues to short-circuit;`nbad-path produces structured ConfigurationError per §6 rule 4."
```

---

## Task 8 — `[NullSubstitute]` member attribute + validator rules (§5.4 + §6 rules 5-6)

**Goal:** Third and final member-level attribute. Routes through `opt.NullSubstitute<TSourceMember>(constant)`; resolves source-member type from `[SourceMember]` if present, else from convention. Validator runs eagerly to catch unreachable substitutes (non-nullable source) and type mismatches before the fluent path's runtime backstop fires.

**Files:**
- Modify: `C:\Repos\Atlas\src\Atlas\Internal\AttributeScanner.cs`
- Modify: `C:\Repos\Atlas\tests\Atlas.Tests\NullSubstituteAttributeTests.cs`

**Allowlist for the implementer subagent:** the two files above.

- [ ] **Step 8.1: Write failing tests for `[NullSubstitute]` behavior**

Append to `tests/Atlas.Tests/NullSubstituteAttributeTests.cs` (preserving the 3 existing tests):

```csharp
public class NullSubstituteAttributeBehaviorTests
{
    [Fact]
    public void NullSubstitute_String_ReplacesNullWithConstant()
    {
        var built = new MapperConfiguration(c => Atlas.Internal.AttributeScanner.Discover(
            typeof(NullSubstituteStringDto).Assembly, c));
        var mapper = built.CreateMapper();
        var dto = mapper.Map<NullSubstituteStringDto>(new NullSubstituteStringSource { Email = null });
        Assert.Equal("(no email)", dto.Email);
    }

    [Fact]
    public void NullSubstitute_NullableInt_ReplacesNullWithConstant()
    {
        var built = new MapperConfiguration(c => Atlas.Internal.AttributeScanner.Discover(
            typeof(NullSubstituteIntDto).Assembly, c));
        var mapper = built.CreateMapper();
        var dto = mapper.Map<NullSubstituteIntDto>(new NullSubstituteIntSource { Count = null });
        Assert.Equal(0, dto.Count);
    }

    [Fact]
    public void NullSubstitute_NonNullableSource_RejectedWithUnreachableMessage()
    {
        var ex = Assert.Throws<AtlasConfigurationException>(() =>
        {
            var expr = new MapperConfigurationExpression();
            Atlas.Internal.AttributeScanner.Discover(typeof(NullSubstituteUnreachableDto).Assembly, expr);
        });
        Assert.Contains(ex.Errors, e =>
            e.Reason.Contains("non-nullable") && e.Reason.Contains("unreachable"));
    }

    [Fact]
    public void NullSubstitute_TypeMismatch_RejectedWithStructuredMessage()
    {
        var ex = Assert.Throws<AtlasConfigurationException>(() =>
        {
            var expr = new MapperConfigurationExpression();
            Atlas.Internal.AttributeScanner.Discover(typeof(NullSubstituteTypeMismatchDto).Assembly, expr);
        });
        Assert.Contains(ex.Errors, e =>
            e.Reason.Contains("not assignable to source-member type"));
    }

    [Fact]
    public void NullSubstitute_CombinedWithSourceMember_BothApply()
    {
        var built = new MapperConfiguration(c => Atlas.Internal.AttributeScanner.Discover(
            typeof(NullSubstituteWithSourceMemberDto).Assembly, c));
        var mapper = built.CreateMapper();
        var src = new NullSubstituteWithSourceMemberSource
        {
            Customer = new NullSubstituteWithSourceMemberCustomer { Email = null }
        };
        var dto = mapper.Map<NullSubstituteWithSourceMemberDto>(src);
        Assert.Equal("(no email)", dto.CustomerEmail);
    }

    [Fact]
    public void NullSubstitute_IgnoreShortCircuits()
    {
        var built = new MapperConfiguration(c => Atlas.Internal.AttributeScanner.Discover(
            typeof(NullSubstituteIgnoreShortCircuitDto).Assembly, c));
        var mapper = built.CreateMapper();
        var src = new NullSubstituteIgnoreShortCircuitSource { Email = "alice@example.com" };
        var dto = mapper.Map<NullSubstituteIgnoreShortCircuitDto>(src);
        Assert.Null(dto.Email);   // Ignored — NullSubstitute unreachable
    }
}

public class NullSubstituteStringSource { public string? Email { get; set; } }
[AutoMap(typeof(NullSubstituteStringSource))]
public class NullSubstituteStringDto
{
    [NullSubstitute("(no email)")]
    public string Email { get; set; } = "";
}

public class NullSubstituteIntSource { public int? Count { get; set; } }
[AutoMap(typeof(NullSubstituteIntSource))]
public class NullSubstituteIntDto
{
    [NullSubstitute(0)]
    public int Count { get; set; }
}

public class NullSubstituteUnreachableSource { public int Count { get; set; } }   // non-nullable!
[AutoMap(typeof(NullSubstituteUnreachableSource))]
public class NullSubstituteUnreachableDto
{
    [NullSubstitute(0)]
    public int Count { get; set; }
}

public class NullSubstituteTypeMismatchSource { public int? Count { get; set; } }
[AutoMap(typeof(NullSubstituteTypeMismatchSource))]
public class NullSubstituteTypeMismatchDto
{
    [NullSubstitute("not-an-int")]   // string is not assignable to int?
    public int Count { get; set; }
}

public class NullSubstituteWithSourceMemberCustomer { public string? Email { get; set; } }
public class NullSubstituteWithSourceMemberSource { public NullSubstituteWithSourceMemberCustomer Customer { get; set; } = new(); }
[AutoMap(typeof(NullSubstituteWithSourceMemberSource))]
public class NullSubstituteWithSourceMemberDto
{
    [SourceMember("Customer.Email")]
    [NullSubstitute("(no email)")]
    public string CustomerEmail { get; set; } = "";
}

public class NullSubstituteIgnoreShortCircuitSource { public string? Email { get; set; } }
[AutoMap(typeof(NullSubstituteIgnoreShortCircuitSource))]
public class NullSubstituteIgnoreShortCircuitDto
{
    [Ignore]
    [NullSubstitute("(unreachable)")]
    public string? Email { get; set; }
}
```

- [ ] **Step 8.2: Run tests to verify they fail**

```pwsh
dotnet test --nologo --filter "FullyQualifiedName~NullSubstituteAttributeBehaviorTests"
```

- [ ] **Step 8.3: Extend `ApplyMemberAttributes` for `[NullSubstitute]` + add validator helpers**

Update `AttributeScanner.cs`:

```csharp
private const string NullSubstituteMethodName =
    nameof(Atlas.Configuration.IMemberConfigurationExpression<object, object, object>.NullSubstitute);

// ... inside ApplyMemberAttributes, after the [SourceMember] block, add: ...

if (nullSubstitute is not null)
{
    // Resolve TSourceMember: use SourceMember leaf if present, else convention-resolve.
    Type? resolvedSourceType = sourceMemberType;
    if (resolvedSourceType is null)
    {
        resolvedSourceType = ResolveSourceMemberByConvention(srcType, prop, errors);
    }

    if (resolvedSourceType is not null
        && ValidateNullSubstituteCompatibility(resolvedSourceType, nullSubstitute.ConstantValue,
                                               srcType, dstType, prop.Name, errors))
    {
        var constantOverloadOpen = imemberConfigClosed.GetMethods()
            .Single(m => m.Name == NullSubstituteMethodName
                      && m.IsGenericMethodDefinition
                      && m.GetParameters().Length == 1
                      && !m.GetParameters()[0].ParameterType.IsGenericType);
        var constantOverloadClosed = constantOverloadOpen.MakeGenericMethod(resolvedSourceType);

        // Convert the boxed attribute constant to the resolved source type for type-correct emit.
        var convertedConstant = ConvertAttributeConstant(nullSubstitute.ConstantValue, resolvedSourceType);
        statements.Add(Expression.Call(optParam, constantOverloadClosed,
            Expression.Constant(convertedConstant, resolvedSourceType)));
    }
}
```

Add the helpers:

```csharp
/// <summary>
/// Resolves the source-member type for a destination property by convention
/// (matching the property name on the source type). Returns <c>null</c> if no
/// matching member exists; in that case the runtime convention engine handles
/// it (or convention-validation flags the unmapped destination).
/// </summary>
private static Type? ResolveSourceMemberByConvention(Type srcType, PropertyInfo destProp, List<ConfigurationError> errors)
{
    var prop = srcType.GetProperty(destProp.Name, BindingFlags.Public | BindingFlags.Instance);
    if (prop is not null) return prop.PropertyType;

    var field = srcType.GetField(destProp.Name, BindingFlags.Public | BindingFlags.Instance);
    if (field is not null) return field.FieldType;

    return null;
}

/// <summary>
/// Eager validator for [NullSubstitute] per design §6 rules 5 &amp; 6. Returns true
/// if the substitute is compatible; appends a structured error and returns false
/// otherwise.
/// </summary>
private static bool ValidateNullSubstituteCompatibility(
    Type sourceMemberType, object constantValue,
    Type srcType, Type dstType, string destMemberName,
    List<ConfigurationError> errors)
{
    // Rule 6: source must be reference type or Nullable<T>.
    var underlying = Nullable.GetUnderlyingType(sourceMemberType);
    var isReferenceType = !sourceMemberType.IsValueType;
    var isNullable = underlying is not null;

    if (!isReferenceType && !isNullable)
    {
        errors.Add(new(srcType, dstType, destMemberName,
            $"[NullSubstitute({FormatConstant(constantValue)})] on '{dstType.Name}.{destMemberName}' — " +
            $"source-member type '{sourceMemberType.Name}' is non-nullable; the substitute is unreachable. " +
            $"Use a different default mechanism or remove the attribute."));
        return false;
    }

    // Rule 5: substitute must be assignable to source-member type (or its underlying type for Nullable<T>).
    var targetType = underlying ?? sourceMemberType;
    var constantType = constantValue.GetType();

    if (!targetType.IsAssignableFrom(constantType))
    {
        // Allow numeric coercion (matches existing fluent NullSubstitute behavior).
        if (!IsNumericallyCoercible(constantType, targetType))
        {
            errors.Add(new(srcType, dstType, destMemberName,
                $"[NullSubstitute({FormatConstant(constantValue)})] on '{dstType.Name}.{destMemberName}' — " +
                $"substitute type '{constantType.Name}' is not assignable to source-member type " +
                $"'{(isNullable ? $"Nullable<{targetType.Name}>" : targetType.Name)}'."));
            return false;
        }
    }

    return true;
}

private static bool IsNumericallyCoercible(Type from, Type to)
{
    // Mirror the existing NumericConversions.HasImplicitConversion logic, simplified.
    // Atlas already has this in src/Atlas/Internal/NumericConversions.cs — reuse it.
    return Atlas.Internal.NumericConversions.HasImplicitConversion(from, to);
}

private static object ConvertAttributeConstant(object value, Type targetType)
{
    var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
    if (underlying.IsAssignableFrom(value.GetType())) return value;
    return Convert.ChangeType(value, underlying);
}

private static string FormatConstant(object value)
{
    return value switch
    {
        string s => $"\"{s}\"",
        char c => $"'{c}'",
        null => "null",
        _ => value.ToString() ?? "?"
    };
}
```

Note: `Atlas.Internal.NumericConversions.HasImplicitConversion(Type, Type)` is the existing helper used by the fluent path. Verify the actual method signature in `src/Atlas/Internal/NumericConversions.cs` and adjust the call if the name differs.

- [ ] **Step 8.4: Run tests to verify they pass**

```pwsh
dotnet test --nologo --filter "FullyQualifiedName~NullSubstituteAttributeBehaviorTests"
```

Expected: 6 new tests pass.

- [ ] **Step 8.5: Run full suite — zero regressions**

```pwsh
dotnet test --nologo 2>&1 | Select-String -Pattern "Passed:|Failed:|Skipped:"
```

- [ ] **Step 8.6: Commit**

```pwsh
git add src/Atlas/Internal/AttributeScanner.cs tests/Atlas.Tests/NullSubstituteAttributeTests.cs
git commit -m "AttributeScanner: ApplyMemberAttributes for [NullSubstitute] + validator rules (Task 8)`n`nReflection-built NullSubstitute<TSourceMember>(constant) call inside ForMember`ncallback. Type resolution: SourceMember leaf if present, else convention-by-name`non source. Eager validator catches non-nullable source (unreachable substitute)`nand type mismatch per design §6 rules 5 & 6 with attribute-named error messages.`nReuses Atlas.Internal.NumericConversions.HasImplicitConversion for numeric`ncoercion compatibility (matches existing fluent NullSubstitute behavior)."
```

---

## Task 9 — Universal duplicate-pair rule

**Goal:** Tighten `MapperConfigurationExpression.RegisterTypeMap` to throw on any second registration for the same `(TSource, TDestination)` pair, regardless of origin (per design §6.7). This is the prerequisite for safely wiring the scanner into `AddMaps` (Task 10) — without it, fluent + attribute conflicts on the same pair silently last-write-wins.

**Files:**
- Modify: `C:\Repos\Atlas\src\Atlas\MapperConfigurationExpression.cs` (line 115-134)
- Modify: `C:\Repos\Atlas\tests\Atlas.Tests\MapperConfigurationExpressionTests.cs` (add duplicate-pair tests; create the file if it doesn't already cover this)

Verify which existing test file covers `RegisterTypeMap` behavior; the obvious candidate is `MapperConfigurationExpressionTests.cs`. If no existing test exercises duplicate registration, add a dedicated `DuplicatePairTests` test class to `MapperConfigurationExpressionTests.cs`.

**Allowlist for the implementer subagent:** the two files above (or whichever existing test file is the natural home).

- [ ] **Step 9.1: Verify no existing test depends on silent last-write-wins**

```pwsh
cd C:\Repos\Atlas
git grep -n "CreateMap" tests/Atlas.Tests/ | findstr /R "CreateMap.*CreateMap" | findstr /V "CreateMap<.*ReverseMap"
```

This grep is approximate; manually inspect any results to confirm none exercise "two `CreateMap` calls for the same pair without `.ReverseMap()` and expect the second to silently win." Per design §6.7, this enumeration must show zero hits before tightening. If a hit exists, halt and ask the user how to migrate the test.

- [ ] **Step 9.2: Write failing tests for the universal duplicate-pair rule**

Append to `tests/Atlas.Tests/MapperConfigurationExpressionTests.cs`:

```csharp
public class DuplicatePairTests
{
    public class DupSrc { public int X { get; set; } }
    public class DupDst { public int X { get; set; } }

    [Fact]
    public void TwoFluentCreateMapCalls_SamePair_Throws()
    {
        var ex = Assert.Throws<AtlasConfigurationException>(() =>
        {
            var expr = new MapperConfigurationExpression();
            expr.CreateMap<DupSrc, DupDst>();
            expr.CreateMap<DupSrc, DupDst>();
        });
        Assert.Contains(ex.Errors, e => e.Reason.Contains("registered twice"));
    }

    [Fact]
    public void DuplicateMessage_NamesBothOrigins()
    {
        var ex = Assert.Throws<AtlasConfigurationException>(() =>
        {
            var expr = new MapperConfigurationExpression();
            expr.CreateMap<DupSrc, DupDst>();
            expr.CreateMap<DupSrc, DupDst>();
        });
        // Both origins should be cited in the message; second is also CreateMap<...>().
        Assert.Contains(ex.Errors, e => e.Reason.Contains("CreateMap<DupSrc, DupDst>()"));
    }
}
```

- [ ] **Step 9.3: Run tests to verify they fail (existing v1 silently last-write-wins)**

```pwsh
dotnet test --nologo --filter "FullyQualifiedName~DuplicatePairTests"
```

Expected: tests fail because the silent overwrite path doesn't throw.

- [ ] **Step 9.4: Tighten `RegisterTypeMap`**

Replace the body of `RegisterTypeMap` in `MapperConfigurationExpression.cs` (existing line 115-134):

```csharp
private void RegisterTypeMap(TypeMap newTm)
{
    if (_typeMaps.TryGetValue(newTm.Pair, out var existing))
    {
        throw new AtlasConfigurationException(new List<ConfigurationError>
        {
            new(newTm.SourceType, newTm.DestinationType, "(register)",
                $"Type pair ({newTm.SourceType.Name}, {newTm.DestinationType.Name}) is registered twice: " +
                $"{existing.RegistrationOrigin} and {newTm.RegistrationOrigin}. " +
                $"Pick one — every (TSource, TDestination) pair must have a single registration.")
        });
    }
    _typeMaps[newTm.Pair] = newTm;
}
```

- [ ] **Step 9.5: Run tests to verify they pass**

```pwsh
dotnet test --nologo --filter "FullyQualifiedName~DuplicatePairTests"
```

Expected: 2 new tests pass.

- [ ] **Step 9.6: Run full suite — zero regressions (or document any expected breakage)**

```pwsh
dotnet test --nologo 2>&1 | Select-String -Pattern "Passed:|Failed:|Skipped:"
```

If any existing test fails, inspect — it likely depends on silent overwrite. Per design §11 R1, the verification claim is "zero hits before tightening." Halt if regressions occur and report to the user.

- [ ] **Step 9.7: Commit**

```pwsh
git add src/Atlas/MapperConfigurationExpression.cs tests/Atlas.Tests/MapperConfigurationExpressionTests.cs
git commit -m "Universal duplicate-pair rule in RegisterTypeMap (Task 9)`n`nTighten RegisterTypeMap from reverse-only-throw to throw on any second`nregistration for the same (TSource, TDestination) pair, regardless of origin.`nPer design §6.7. Behavior change: previous silent last-write-wins for non-`nreverse duplicates is now a loud error. Verified non-regressive against the`nbaseline test suite via plan-prerequisite grep. Error message names both`nregistration origins so the user can locate the duplicate site."
```

---

## Task 10 — Wire `AttributeScanner.Discover` into `MapperConfigurationExpression.AddMaps`

**Goal:** The single insertion point that activates attribute discovery end-to-end. After this task, `cfg.AddMaps(asm)` and `services.AddAtlas(asm)` discover both `MapperProfile` subclasses (existing) and `[AutoMap]`-decorated types (new). Tests cover the conflict scenarios that the universal duplicate rule from Task 9 enables.

**Files:**
- Modify: `C:\Repos\Atlas\src\Atlas\MapperConfigurationExpression.cs` (the `AddMaps(params Assembly[])` method, around line 167-180)
- Create: `C:\Repos\Atlas\tests\Atlas.Tests\AttributeFluentInteractionTests.cs`
- Modify: `C:\Repos\Atlas\tests\Atlas.Tests\AutoMapAttributeTests.cs` (add the two end-to-end tests deferred from Task 5)

**Allowlist for the implementer subagent:** the three files above.

- [ ] **Step 10.1: Write failing tests for end-to-end discovery + conflict scenarios**

Create `tests/Atlas.Tests/AttributeFluentInteractionTests.cs`:

```csharp
namespace Atlas.Tests;

public class AttributeFluentInteractionTests
{
    [Fact]
    public void AddMaps_DiscoversAttributeDecoratedType()
    {
        var cfg = new MapperConfiguration(c => c.AddMaps(typeof(InteractionDtoA).Assembly));
        var mapper = cfg.CreateMapper();
        var dto = mapper.Map<InteractionDtoA>(new InteractionSrcA { Id = 5 });
        Assert.Equal(5, dto.Id);
    }

    [Fact]
    public void AddAtlas_DI_DiscoversAttributeDecoratedType()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddAtlas(typeof(InteractionDtoA).Assembly);
        using var sp = services.BuildServiceProvider();
        var mapper = sp.GetRequiredService<IMapper>();
        var dto = mapper.Map<InteractionDtoA>(new InteractionSrcA { Id = 5 });
        Assert.Equal(5, dto.Id);
    }

    [Fact]
    public void AttributeAndFluent_SamePair_ThrowsWithBothOrigins()
    {
        // The attribute-decorated InteractionDtoB is discovered via AddMaps; the
        // profile registers (InteractionSrcB, InteractionDtoB) fluently. Conflict.
        var ex = Assert.Throws<AtlasConfigurationException>(() =>
        {
            new MapperConfiguration(c =>
            {
                c.AddProfile(new InteractionConflictProfile());
                c.AddMaps(typeof(InteractionDtoB).Assembly);   // discovers [AutoMap] on InteractionDtoB
            });
        });
        Assert.Contains(ex.Errors, e =>
            e.Reason.Contains("registered twice")
            && e.Reason.Contains("InteractionSrcB")
            && e.Reason.Contains("InteractionDtoB"));
    }

    [Fact]
    public void AttributeMap_GlobalTransformer_Fires()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.ValueTransformers.Add<string>(s => s + "!");
            c.AddMaps(typeof(InteractionDtoA).Assembly);
        });
        var mapper = cfg.CreateMapper();
        var dto = mapper.Map<InteractionGlobalTransformerDto>(
            new InteractionGlobalTransformerSource { Name = "Hello" });
        Assert.Equal("Hello!", dto.Name);
    }

    [Fact]
    public void AttributeMap_ProfileScopeTransformer_DoesNotFire()
    {
        // OriginatingProfile is null on attribute-declared TypeMaps (matches DynamicMapping #10).
        var cfg = new MapperConfiguration(c =>
        {
            c.AddProfile(new InteractionTransformerProfile());
            c.AddMaps(typeof(InteractionDtoA).Assembly);
        });
        var mapper = cfg.CreateMapper();
        var dto = mapper.Map<InteractionGlobalTransformerDto>(
            new InteractionGlobalTransformerSource { Name = "Hello" });
        Assert.Equal("Hello", dto.Name);   // profile transformer did NOT fire
    }

    [Fact]
    public void AttributeAddMaps_ProfileFirst_AttributeSecond_ConflictMessageOrderingMatchesIntent()
    {
        var ex = Assert.Throws<AtlasConfigurationException>(() =>
        {
            new MapperConfiguration(c =>
            {
                c.AddProfile(new InteractionConflictProfile());
                c.AddMaps(typeof(InteractionDtoB).Assembly);
            });
        });
        var error = ex.Errors.First(e => e.Reason.Contains("registered twice"));
        // The user's intent: profile is the explicit declaration; attribute is the surprise.
        // Verify the existing-origin (profile) appears before the new-origin (attribute) in the message.
        var profileIdx = error.Reason.IndexOf("InteractionConflictProfile");
        var attributeIdx = error.Reason.IndexOf("[AutoMap");
        Assert.True(profileIdx >= 0 && attributeIdx >= 0 && profileIdx < attributeIdx,
            $"Profile origin must precede attribute origin in error message. Got: {error.Reason}");
    }
}

public class InteractionSrcA { public int Id { get; set; } }
[AutoMap(typeof(InteractionSrcA))]
public class InteractionDtoA { public int Id { get; set; } }

public class InteractionSrcB { public int Id { get; set; } }
[AutoMap(typeof(InteractionSrcB))]
public class InteractionDtoB { public int Id { get; set; } }

public class InteractionConflictProfile : MapperProfile
{
    public InteractionConflictProfile()
    {
        CreateMap<InteractionSrcB, InteractionDtoB>();
    }
}

public class InteractionGlobalTransformerSource { public string Name { get; set; } = ""; }
[AutoMap(typeof(InteractionGlobalTransformerSource))]
public class InteractionGlobalTransformerDto { public string Name { get; set; } = ""; }

public class InteractionTransformerProfile : MapperProfile
{
    public InteractionTransformerProfile()
    {
        ValueTransformers.Add<string>(s => s + "?");
    }
}
```

Append to `tests/Atlas.Tests/AutoMapAttributeTests.cs` (the two tests deferred from Task 5):

```csharp
public class AutoMapAttributeEndToEndTests
{
    [Fact]
    public void AttributeMap_ConventionOnlyMember_Resolves_ViaAddMaps()
    {
        var cfg = new MapperConfiguration(c => c.AddMaps(typeof(AttributeMapMinimumDto).Assembly));
        var mapper = cfg.CreateMapper();
        var dto = mapper.Map<AttributeMapMinimumDto>(new AttributeMapMinimumSource { Id = 7 });
        Assert.Equal(7, dto.Id);
    }

    [Fact]
    public void AttributeMap_MemberListSource_PassesValidation_WhenSourceCovered_ViaAddMaps()
    {
        var cfg = new MapperConfiguration(c => c.AddMaps(typeof(AttributeMapSourceListDto).Assembly));
        cfg.AssertConfigurationIsValid();
    }
}
```

- [ ] **Step 10.2: Run tests to verify they fail**

```pwsh
dotnet test --nologo --filter "FullyQualifiedName~AttributeFluentInteractionTests|FullyQualifiedName~AutoMapAttributeEndToEndTests"
```

Expected: tests fail because `AddMaps` doesn't yet invoke `AttributeScanner.Discover`.

- [ ] **Step 10.3: Wire the scanner into `AddMaps`**

In `MapperConfigurationExpression.cs`, modify `AddMaps(params Assembly[])` (around line 166-180):

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
    foreach (var asm in assemblies)
    {
        AttributeScanner.Discover(asm, this);
    }
}
```

- [ ] **Step 10.4: Run tests to verify they pass**

```pwsh
dotnet test --nologo --filter "FullyQualifiedName~AttributeFluentInteractionTests|FullyQualifiedName~AutoMapAttributeEndToEndTests"
```

Expected: 8 new tests pass.

- [ ] **Step 10.5: Run full suite — zero regressions**

```pwsh
dotnet test --nologo 2>&1 | Select-String -Pattern "Passed:|Failed:|Skipped:"
```

Important: if earlier `AddAtlasExtensionTests` or other tests scan the test assembly and the test assembly now contains many `[AutoMap]`-decorated fixtures, conflicts may surface. If so, audit the affected test for what fixture classes it expects — likely the test scans `typeof(AddAtlasMarker).Assembly` which is the entire test assembly. Two mitigation paths:
- **Preferred:** rename test fixture classes so no two tests' fixtures register the same `(TSrc, TDst)` pair. Already enforced by the unique-name conventions used throughout this plan's fixture types.
- **Fallback:** scope problematic tests to a marker assembly OR move attribute-fixture types to a private nested location (which the filter excludes via `IsNested = true`).

If the implementer hits regressions here, document them and propose a plan back to the user before continuing.

- [ ] **Step 10.6: Commit**

```pwsh
git add src/Atlas/MapperConfigurationExpression.cs tests/Atlas.Tests/AttributeFluentInteractionTests.cs tests/Atlas.Tests/AutoMapAttributeTests.cs
git commit -m "Wire AttributeScanner.Discover into MapperConfigurationExpression.AddMaps (Task 10)`n`nSingle insertion point per design §4.2: append AttributeScanner.Discover loop`nafter ProfileScanner.Discover loop inside AddMaps. Order: profiles first,`nattributes second — ensures duplicate-pair conflicts surface with the profile`nas existing.RegistrationOrigin and the attribute as newTm.RegistrationOrigin,`nmatching the user's mental model of 'profile is explicit; attribute is the`nsurprise'. End-to-end tests cover cfg.AddMaps + services.AddAtlas paths plus`nthe full Q4 conflict matrix (fluent×2, fluent+attribute, profile+attribute,`nglobal vs profile-scope value transformers)."
```

---

## Task 11 — Atlas.Projections support tests

**Goal:** Verify attribute-declared TypeMaps participate in `query.ProjectTo<T>()` correctly, and that projection-incompatible attribute typemaps (PreserveReferences, hooks attached via mixed-mode profiles) are rejected at projection-build with the existing dual-gate. No production changes anticipated — projection sees `TypeMap` instances regardless of origin.

**Files:**
- Create: `C:\Repos\Atlas\tests\Atlas.Projections.Tests\AttributeProjectionTests.cs`

**Allowlist for the implementer subagent:** the file above.

- [ ] **Step 11.1: Inspect existing projection test patterns**

```pwsh
cd C:\Repos\Atlas
ls tests/Atlas.Projections.Tests/
head -40 tests/Atlas.Projections.Tests/ProjectionRejectsHooksTests.cs
```

Use the existing rejection-test pattern as a template for the PreserveReferences/Hooks rejection tests in this task.

- [ ] **Step 11.2: Write failing tests for projection support and rejection**

Create `tests/Atlas.Projections.Tests/AttributeProjectionTests.cs`:

```csharp
using Atlas;
using Atlas.Projections;

namespace Atlas.Projections.Tests;

public class AttributeProjectionTests
{
    [Fact]
    public void AttributeMap_ProjectsViaProjectTo()
    {
        var cfg = new MapperConfiguration(c => c.AddMaps(typeof(ProjectionAttrDto).Assembly));
        var src = new[]
        {
            new ProjectionAttrSource { Id = 1, Name = "A" },
            new ProjectionAttrSource { Id = 2, Name = "B" },
        };
        var result = src.AsQueryable().ProjectTo<ProjectionAttrDto>(cfg).ToArray();
        Assert.Equal(2, result.Length);
        Assert.Equal(1, result[0].Id);
        Assert.Equal("A", result[0].Name);
    }

    [Fact]
    public void IgnoreAttribute_ExcludesMemberFromProjection()
    {
        var cfg = new MapperConfiguration(c => c.AddMaps(typeof(ProjectionIgnoreDto).Assembly));
        var src = new[]
        {
            new ProjectionIgnoreSource { Id = 1, Skipped = "should-not-appear" },
        };
        var result = src.AsQueryable().ProjectTo<ProjectionIgnoreDto>(cfg).Single();
        Assert.Equal(1, result.Id);
        Assert.Null(result.Skipped);
    }

    [Fact]
    public void SourceMember_DottedPath_ProjectsAsNavigation()
    {
        var cfg = new MapperConfiguration(c => c.AddMaps(typeof(ProjectionDottedDto).Assembly));
        var src = new[]
        {
            new ProjectionDottedSource { Id = 1, Customer = new ProjectionDottedCustomer { Name = "X" } },
        };
        var result = src.AsQueryable().ProjectTo<ProjectionDottedDto>(cfg).Single();
        Assert.Equal("X", result.CustomerName);
    }

    [Fact]
    public void NullSubstitute_TranslatesToCoalesce()
    {
        var cfg = new MapperConfiguration(c => c.AddMaps(typeof(ProjectionNullSubDto).Assembly));
        var src = new[]
        {
            new ProjectionNullSubSource { Id = 1, MaybeName = null },
            new ProjectionNullSubSource { Id = 2, MaybeName = "set" },
        };
        var result = src.AsQueryable().ProjectTo<ProjectionNullSubDto>(cfg).ToArray();
        Assert.Equal("(none)", result[0].Name);
        Assert.Equal("set", result[1].Name);
    }

    [Fact]
    public void PreserveReferencesAttribute_RejectedAtProjectionBuild()
    {
        var cfg = new MapperConfiguration(c => c.AddMaps(typeof(ProjectionPreserveDto).Assembly));
        var src = Array.Empty<ProjectionPreserveSource>();
        var ex = Assert.Throws<AtlasProjectionException>(() =>
            src.AsQueryable().ProjectTo<ProjectionPreserveDto>(cfg).ToArray());
        Assert.Contains("PreserveReferences", ex.Message);
    }

    [Fact]
    public void AttributeMapWithMixedModeHooks_RejectedAtProjectionBuild()
    {
        // Mixed mode is currently impossible because Q4 forbids same-pair fluent + attribute.
        // The closest analog: attribute-declared map for one pair, profile with hooks for an
        // unrelated dependency pair. Because hooks live on a different TypeMap, projection of
        // the attribute pair should still succeed UNLESS the dependency pair is included.
        // For minimal v1 coverage, skip this test or mark it as documenting the design boundary.
        // Implementer note: if a clean test scenario can be constructed, write it; otherwise
        // delete this test method and reduce the count to 5.
    }
}

public class ProjectionAttrSource { public int Id { get; set; } public string Name { get; set; } = ""; }
[AutoMap(typeof(ProjectionAttrSource))]
public class ProjectionAttrDto { public int Id { get; set; } public string Name { get; set; } = ""; }

public class ProjectionIgnoreSource { public int Id { get; set; } public string? Skipped { get; set; } }
[AutoMap(typeof(ProjectionIgnoreSource))]
public class ProjectionIgnoreDto { public int Id { get; set; } [Ignore] public string? Skipped { get; set; } }

public class ProjectionDottedCustomer { public string Name { get; set; } = ""; }
public class ProjectionDottedSource { public int Id { get; set; } public ProjectionDottedCustomer Customer { get; set; } = new(); }
[AutoMap(typeof(ProjectionDottedSource))]
public class ProjectionDottedDto
{
    public int Id { get; set; }
    [SourceMember("Customer.Name")] public string CustomerName { get; set; } = "";
}

public class ProjectionNullSubSource { public int Id { get; set; } public string? MaybeName { get; set; } }
[AutoMap(typeof(ProjectionNullSubSource))]
public class ProjectionNullSubDto
{
    public int Id { get; set; }
    [SourceMember(nameof(ProjectionNullSubSource.MaybeName))]
    [NullSubstitute("(none)")]
    public string Name { get; set; } = "";
}

public class ProjectionPreserveSource { public int Id { get; set; } }
[AutoMap(typeof(ProjectionPreserveSource), PreserveReferences = true)]
public class ProjectionPreserveDto { public int Id { get; set; } }
```

Note: the sixth test (mixed-mode hooks) is documented as a design-boundary placeholder. The implementer should delete it if no clean scenario exists, or flesh it out with `MapperProfile.AddMaps`-style dependency wiring if one is straightforward.

- [ ] **Step 11.3: Run tests to verify they fail (or pass already, depending on what existing infrastructure handles)**

```pwsh
dotnet test --nologo --filter "FullyQualifiedName~AttributeProjectionTests"
```

Expected: tests should pass directly (no production change needed) IF projection's `IsTypeMapProjectable` and `RejectPreserveReferencesOrThrow` correctly handle attribute-declared TypeMaps. If any test fails, investigate — the projection code may have an undocumented assumption (e.g., explicit `OriginatingProfile != null`) that attribute maps violate.

- [ ] **Step 11.4: Run full suite — zero regressions**

```pwsh
dotnet test --nologo 2>&1 | Select-String -Pattern "Passed:|Failed:|Skipped:"
```

- [ ] **Step 11.5: Commit**

```pwsh
git add tests/Atlas.Projections.Tests/AttributeProjectionTests.cs
git commit -m "Atlas.Projections: attribute typemap projection tests (Task 11)`n`nVerify attribute-declared TypeMaps participate in ProjectTo correctly with`n[Ignore], [SourceMember] dotted paths (translates to SQL navigation),`nand [NullSubstitute] (translates to SQL COALESCE). PreserveReferences`nattribute typemaps rejected at projection-build via existing dual-gate.`nNo production changes — design's translate-to-fluent architecture means`nprojection sees normal TypeMaps regardless of origin (per design §7.11)."
```

---

## Task 12 — Integration tests + multi-attribute scenarios

**Goal:** End-to-end tests exercising the full feature surface in realistic scenarios. Multi-attribute composition on one property; cycle-safe attribute DTO; reverse-map propagation; DI-resolved usage.

**Files:**
- Create: `C:\Repos\Atlas\tests\Atlas.Tests\AttributeIntegrationTests.cs`

**Allowlist for the implementer subagent:** the file above.

- [ ] **Step 12.1: Write failing tests for integration scenarios**

Create `tests/Atlas.Tests/AttributeIntegrationTests.cs`:

```csharp
namespace Atlas.Tests;

public class AttributeIntegrationTests
{
    [Fact]
    public void DI_AddAtlas_ResolvedMapper_UsesAttributeMap()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddAtlas(typeof(IntegrationDto).Assembly);
        using var sp = services.BuildServiceProvider();
        var mapper = sp.GetRequiredService<IMapper>();
        var dto = mapper.Map<IntegrationDto>(new IntegrationSource
        {
            Id = 42,
            Customer = new IntegrationCustomer { FirstName = "Alice", Email = null },
            Skipped = "ignored",
        });
        Assert.Equal(42, dto.Id);
        Assert.Equal("Alice", dto.CustomerFirstName);
        Assert.Equal("(no email)", dto.CustomerEmail);
        Assert.Null(dto.SkippedField);
    }

    [Fact]
    public void MultiAttribute_OnOneProperty_BothApply_InCorrectOrder()
    {
        var cfg = new MapperConfiguration(c => c.AddMaps(typeof(IntegrationDto).Assembly));
        var mapper = cfg.CreateMapper();
        var dto = mapper.Map<IntegrationDto>(new IntegrationSource
        {
            Customer = new IntegrationCustomer { FirstName = "x", Email = null },
        });
        // SourceMember redirects, NullSubstitute supplies fallback.
        Assert.Equal("(no email)", dto.CustomerEmail);
    }

    [Fact]
    public void IgnoreShortCircuits_OnPropertyAlsoBearingSourceMember_OrNullSubstitute()
    {
        var cfg = new MapperConfiguration(c => c.AddMaps(typeof(IntegrationShortCircuitDto).Assembly));
        var mapper = cfg.CreateMapper();
        var dto = mapper.Map<IntegrationShortCircuitDto>(new IntegrationShortCircuitSource
        {
            Customer = new IntegrationCustomer { Email = null }
        });
        Assert.Null(dto.SkippedDespiteAttributes);
    }

    [Fact]
    public void AttributeWithReverseMap_BothDirectionsWork()
    {
        var cfg = new MapperConfiguration(c => c.AddMaps(typeof(IntegrationReverseDto).Assembly));
        var mapper = cfg.CreateMapper();
        var src = new IntegrationReverseSource { Id = 7 };
        var dto = mapper.Map<IntegrationReverseDto>(src);
        var roundTrip = mapper.Map<IntegrationReverseSource>(dto);
        Assert.Equal(7, dto.Id);
        Assert.Equal(7, roundTrip.Id);
    }

    [Fact]
    public void AttributeWithPreserveReferences_BreaksCycle()
    {
        var cfg = new MapperConfiguration(c => c.AddMaps(typeof(IntegrationCycleDto).Assembly));
        var mapper = cfg.CreateMapper();
        var src = new IntegrationCycleSource { Id = 1 };
        src.Self = src;
        var dto = mapper.Map<IntegrationCycleDto>(src);
        Assert.Same(dto, dto.Self);
    }

    [Fact]
    public void AttributeMap_AssertConfigurationIsValid_Passes()
    {
        var cfg = new MapperConfiguration(c => c.AddMaps(typeof(IntegrationDto).Assembly));
        cfg.AssertConfigurationIsValid();   // No-throw expected.
    }
}

public class IntegrationCustomer
{
    public string FirstName { get; set; } = "";
    public string? Email { get; set; }
}

public class IntegrationSource
{
    public int Id { get; set; }
    public IntegrationCustomer Customer { get; set; } = new();
    public string? Skipped { get; set; }
}

[AutoMap(typeof(IntegrationSource), MemberList = MemberList.Destination)]
public class IntegrationDto
{
    public int Id { get; set; }

    [SourceMember("Customer.FirstName")]
    public string CustomerFirstName { get; set; } = "";

    [SourceMember("Customer.Email")]
    [NullSubstitute("(no email)")]
    public string CustomerEmail { get; set; } = "";

    [Ignore]
    public string? SkippedField { get; set; }
}

public class IntegrationShortCircuitSource
{
    public IntegrationCustomer Customer { get; set; } = new();
}

[AutoMap(typeof(IntegrationShortCircuitSource))]
public class IntegrationShortCircuitDto
{
    [Ignore]
    [SourceMember("Customer.FirstName")]
    [NullSubstitute("(unreachable)")]
    public string? SkippedDespiteAttributes { get; set; }
}

public class IntegrationReverseSource { public int Id { get; set; } }
[AutoMap(typeof(IntegrationReverseSource), ReverseMap = true)]
public class IntegrationReverseDto { public int Id { get; set; } }

public class IntegrationCycleSource
{
    public int Id { get; set; }
    public IntegrationCycleSource? Self { get; set; }
}
[AutoMap(typeof(IntegrationCycleSource), PreserveReferences = true)]
public class IntegrationCycleDto
{
    public int Id { get; set; }
    public IntegrationCycleDto? Self { get; set; }
}
```

- [ ] **Step 12.2: Run tests to verify they fail or pass appropriately**

```pwsh
dotnet test --nologo --filter "FullyQualifiedName~AttributeIntegrationTests"
```

Expected: all tests should pass IF Tasks 1-10 are correct. This task primarily verifies the feature works end-to-end; if any test fails, it points to a regression introduced earlier that wasn't caught by per-task tests.

- [ ] **Step 12.3: Run full suite — zero regressions**

```pwsh
dotnet test --nologo 2>&1 | Select-String -Pattern "Passed:|Failed:|Skipped:"
```

- [ ] **Step 12.4: Commit**

```pwsh
git add tests/Atlas.Tests/AttributeIntegrationTests.cs
git commit -m "Integration tests: full attribute feature surface (Task 12)`n`nEnd-to-end tests covering multi-attribute composition on one property,`n[Ignore] short-circuit precedence, ReverseMap round-trip via attribute,`nPreserveReferences cycle-break via attribute, AssertConfigurationIsValid`non a fully-attribute-declared map. DI-resolved IMapper exercises the full`nAddAtlas → AddMaps → AttributeScanner.Discover → fluent translation chain."
```

---

## Task 13 — README delta + final verification

**Goal:** User-facing documentation. Add the "Attribute-based configuration" section between the existing "Configuration" and "Reference handling for cycles" sections; add a "Migration notes" subsection; remove #12 from the deferred-features list.

**Files:**
- Modify: `C:\Repos\Atlas\README.md`

**Allowlist for the implementer subagent:** the README only.

- [ ] **Step 13.1: Inspect existing README structure**

```pwsh
cd C:\Repos\Atlas
grep -n "^## \|^### " README.md
```

Identify the section anchor for the insertion point: directly after the "Configuration" section header, or before "Reference handling for cycles" — whichever maps to the order specified in the design §12.1.

- [ ] **Step 13.2: Add the "Attribute-based configuration" section**

Insert the following content between "Configuration" and "Reference handling for cycles":

```markdown
## Attribute-based configuration

Decorate destination classes with `[AutoMap(typeof(SourceType))]` to declare mappings without writing a profile. Attributes coexist with profiles; both are discovered by `cfg.AddMaps(asm)` and `services.AddAtlas(asm)`.

```csharp
[AutoMap(typeof(Order))]
public class OrderDto
{
    public int Id { get; init; }

    [SourceMember("Customer.Name")]
    public string CustomerName { get; init; } = "";

    [Ignore]
    public decimal Total { get; init; }

    [NullSubstitute("(no email)")]
    public string Email { get; init; } = "";
}

services.AddAtlas(typeof(OrderDto).Assembly);
// Discovers OrderDto via [AutoMap]; mapping is convention + member-attribute driven.
```

### What attributes can express

| Feature | Attribute |
| --- | --- |
| Class declaration | `[AutoMap(typeof(SourceType))]` |
| Validation policy | `[AutoMap(MemberList = MemberList.Source)]` |
| Auto-reverse | `[AutoMap(ReverseMap = true)]` |
| Cycle-safe (PreserveReferences) | `[AutoMap(PreserveReferences = true)]` |
| Skip member | `[Ignore]` |
| Source-member redirect | `[SourceMember("Customer.Name")]` (incl. dotted paths) |
| Null fallback | `[NullSubstitute("default")]` |

### What attributes can't express

Attributes can't carry lambdas. Use a fluent profile (or a fluent `cfg.CreateMap<>` call) for: `MapFrom(expr)`, `Condition` / `PreCondition`, `BeforeMap` / `AfterMap` lambdas or typed actions, `ConvertUsing`, `AddTransform`, `Include` / `IncludeBase`, `ForCtorParam`, `ForPath`, factory-form `NullSubstitute`, per-value enum overrides.

### Conflict rule

A `(TSource, TDestination)` pair must be declared exactly once. Declaring the same pair via both an attribute and a fluent `CreateMap` throws at config-build naming both registration sites. The same rule applies to two fluent `CreateMap` calls for the same pair (behavior change in v2 — see Migration notes below).

### Profile-scope value transformer note

Profile-scope value transformers do NOT fire on attribute-declared TypeMaps (they have no originating profile). Use global-scope transformers (`cfg.ValueTransformers.Add<T>(...)`) for cross-cutting transforms, or fluent profile-declared maps for profile-scoped ones.
```

- [ ] **Step 13.3: Add the "Migration notes" subsection**

Append to the existing "Configuration" section (or place adjacent to "Attribute-based configuration"):

```markdown
### Migration notes

#### v1 → v2 with #12: duplicate `CreateMap` is now an error

Previous v1 behavior on duplicate non-reverse `CreateMap` calls was silent last-write-wins. With #12 shipped, any second registration for the same `(TSource, TDestination)` pair throws `AtlasConfigurationException` at config-build, regardless of registration origin (profile fluent, scanner-translated attribute, repeated `cfg.CreateMap` on the configuration root, `.ReverseMap()`).

Suggested migration: run existing tests against the new version. If any throw `AtlasConfigurationException` mentioning duplicate registration, the test exposed a latent configuration bug — pick one of the two registration sites and remove the other. The error message names both registration origins so the offending duplicate is easy to find.
```

- [ ] **Step 13.4: Remove #12 from the deferred-features list**

Locate the deferred-features section in the README (or the link to `docs/...deferred...`); cross #12 off the list, leaving #13 as the only remaining deferred feature. Match the format used by previously-shipped features (`~~strikethrough~~`, ` — **shipped**` annotation).

- [ ] **Step 13.5: Verify the README renders cleanly**

```pwsh
cd C:\Repos\Atlas
git diff README.md | Select-String -Pattern "^\+|^-" | Select-Object -First 80
```

Spot-check that markdown renders correctly (no broken table separators, no orphaned heading levels).

- [ ] **Step 13.6: Run the full test suite one more time as the final acceptance check**

```pwsh
dotnet test --nologo 2>&1 | Select-String -Pattern "Passed:|Failed:|Skipped:"
```

Expected: total `Passed: ~691, Failed: 0, Skipped: 0` (≈634 baseline + 57 net new).

- [ ] **Step 13.7: Commit**

```pwsh
git add README.md
git commit -m "docs: README — add attribute config section + migration notes (Task 13)`n`nNew section between Configuration and Reference handling for cycles. Covers`nthe four attribute types, what they express, what they cannot express`n(lambdas → profile), Q4 conflict rule + migration note for the universal`nduplicate-pair behavior change. Remove #12 from the deferred-features list.`nFinal pre-PR test verification: ~691 PASS / 0 FAIL / 0 SKIP."
```

- [ ] **Step 13.8: Push branch and open PR**

```pwsh
git push -u origin feat/attribute-config
gh pr create --base main --head feat/attribute-config `
  --title "Atlas v2 #12: Attribute-Based Configuration" `
  --body @"
Implements Atlas v2 feature #12 per ``docs/Atlas-Design-AttributeConfig.md`` (1672 lines, 14 sections; design merged at ``96ad3d9``).

## Summary

Adds attribute-based class declarations as a parallel front-end to the fluent API. Decorate destination classes with ``[AutoMap(typeof(TSource))]`` and properties with ``[Ignore]`` / ``[SourceMember(name)]`` / ``[NullSubstitute(value)]``; discovery integrates with the existing ``cfg.AddMaps(asm)`` and ``services.AddAtlas(asm)`` entry points. The new ``AttributeScanner`` translates attributes into existing fluent calls so the entire downstream pipeline (validation, propagation, projection, codegen) is unchanged.

## Behavior change

The existing ``RegisterTypeMap`` reverse-only duplicate guard tightens to a universal duplicate-pair rule. Two ``CreateMap<S,D>()`` calls for the same pair now throw at registration regardless of origin. See ``README.md`` § Migration notes.

## Test count

Baseline 634 → ≈691 (≈+57 net). Per-feature plan-arithmetic-drift discipline: actual count is authoritative.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
"@
```

---

## Final review

After Task 13, the PR is open and the test suite is green. Before merge:

1. **Holistic-review the entire diff for cross-task issues.** Spec-reviewer + code-quality-reviewer subagents per task land surgical fixes; the holistic review catches scope-spanning issues per the PR #11 lesson (where the bidirectional-propagation issue was Q5-flagged but not caught until holistic). Specific holistic checks for #12:
   - Q4 conflict policy fires correctly in both orderings (profile→attribute AND attribute→profile, verified by registration order in `AddMaps`).
   - `RegistrationOrigin` strings are stable across all attribute paths (every `[AutoMap]` discovery sets the same format string).
   - Member-attribute composition order is deterministic (`[Ignore]` short-circuits → else: `[SourceMember]` → `[NullSubstitute]`); no test relies on the unsupported `[Ignore]` + sibling-attribute combination producing anything other than ignored behavior.
   - `MakeGenericMethod` failures all wrap into structured `ConfigurationError` rather than reaching the user as bare `ArgumentException` — verify by deliberately constructing a malformed type and confirming the error message names the property.
2. **Memory updates post-merge.** Update `MEMORY.md`, `atlas_v2_design_docs_deferred.md`, `feedback_atlas_v2_workflow.md`, and `feedback_pseudocode_concrete_trace.md` per the established post-merge cleanup pattern. The shipped recap mirrors PR #11's content shape.
3. **Branch cleanup.** Delete `feat/attribute-config` locally and on remote.

---

**End of plan.**
