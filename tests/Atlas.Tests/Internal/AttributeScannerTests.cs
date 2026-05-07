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
        Assert.False(AttributeScanner.IsAttributeMapCandidate(
            typeof(AttributeScannerTests.HostForNested.NestedAttributeFixture)));
    }

    [Fact]
    public void IsAttributeMapCandidate_NonPublicDecorated_False()
    {
        var type = typeof(AttributeScannerTests).Assembly.GetType(
            "Atlas.Tests.Internal.InternalAttributeFixture", throwOnError: false);
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

    // Source classes for fixtures (no attributes — used as TSource references)
    public class PublicSource { public int X { get; set; } }
    public class UndecoratedSource { public int X { get; set; } }
    public class NestedSource { public int X { get; set; } }
    public class AbstractSource { public int X { get; set; } }
    public class InternalSource { public int X { get; set; } }

    // Top-level host class containing a nested-fixture
    public class HostForNested
    {
        [AutoMap(typeof(NestedSource))]
        public class NestedAttributeFixture
        {
            public int X { get; set; }
        }
    }
}

public class AttributeScannerValidationTests
{
    [Fact]
    public void OpenGenericSource_RejectedWithMessage()
    {
        var ex = Assert.Throws<AtlasConfigurationException>(() =>
            AttributeScanner.Discover(typeof(OpenGenericSourceFixture).Assembly, new MapperConfigurationExpression()));
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
    public void Interface_NotACandidate()
    {
        Assert.False(AttributeScanner.IsAttributeMapCandidate(typeof(SomeInterfaceFixture)));
    }

    [Fact]
    public void EnumDecorated_RejectedAtFilterLevel()
    {
        // Enums are technically not classes (Type.IsClass returns false for enums), so the
        // candidate filter rejects. AutoMapAttribute's [AttributeUsage(Class)] also blocks
        // [AutoMap] on enums at compile time. This test asserts the filter rejection.
        var enumType = typeof(SomeEnum);
        Assert.False(AttributeScanner.IsAttributeMapCandidate(enumType));
    }

    [Fact]
    public void DynamicShapeSource_Dictionary_RejectedWithMessage()
    {
        var ex = Assert.Throws<AtlasConfigurationException>(() =>
            AttributeScanner.Discover(typeof(DictionarySourceDto).Assembly, new MapperConfigurationExpression()));
        Assert.Contains(ex.Errors, e =>
            e.Reason.Contains("[AutoMap]") && e.Reason.Contains("dynamic shape"));
    }

    [Fact]
    public void DynamicShapeSource_Expando_RejectedWithMessage()
    {
        var ex = Assert.Throws<AtlasConfigurationException>(() =>
            AttributeScanner.Discover(typeof(ExpandoSourceDto).Assembly, new MapperConfigurationExpression()));
        Assert.Contains(ex.Errors, e =>
            e.Reason.Contains("[AutoMap]") && e.Reason.Contains("dynamic shape"));
    }

    [Fact]
    public void MultipleErrors_AllReported()
    {
        // The test assembly contains multiple bad fixtures; verify the aggregated exception
        // lists more than one rejection.
        var ex = Assert.Throws<AtlasConfigurationException>(() =>
            AttributeScanner.Discover(typeof(OpenGenericSourceFixture).Assembly, new MapperConfigurationExpression()));
        Assert.True(ex.Errors.Count >= 2,
            $"Expected at least 2 errors in aggregated exception, got {ex.Errors.Count}.");
    }
}

public class SomeSource { public int X { get; set; } }
public enum SomeEnum { A, B }

public interface SomeInterfaceFixture { int X { get; } }

[AutoMap(typeof(System.Collections.Generic.List<>))]
public class OpenGenericSourceFixture { public int X { get; set; } }   // open-generic SOURCE

[AutoMap(typeof(SomeSource))]
public class OpenGenericDestDto<T> where T : class { public int X { get; set; } }   // open-generic DEST

[AutoMap(typeof(System.Collections.Generic.Dictionary<string, object>))]
public class DictionarySourceDto { public int X { get; set; } }

[AutoMap(typeof(System.Dynamic.ExpandoObject))]
public class ExpandoSourceDto { public int X { get; set; } }

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
