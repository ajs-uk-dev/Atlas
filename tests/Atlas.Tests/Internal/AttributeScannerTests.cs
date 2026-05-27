using System.Linq.Expressions;
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
    public void IsAttributeMapCandidate_NoMap_False()
    {
        Assert.False(AttributeScanner.IsAttributeMapCandidate(typeof(UndecoratedFixture)));
    }

    [Fact]
    public void IsAttributeMapCandidate_NestedPublicDecorated_True()
    {
        // A public type nested in a public type is reachable by generated mapping code,
        // so it is now a valid attribute-map candidate (previously silently excluded).
        Assert.True(AttributeScanner.IsAttributeMapCandidate(
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

    // ---- Accessibility helper ----

    [Fact]
    public void IsPubliclyAccessible_TopLevelPublic_True()
        => Assert.True(AttributeScanner.IsPubliclyAccessible(typeof(PublicAttributeFixture)));

    [Fact]
    public void IsPubliclyAccessible_TopLevelInternal_False()
        => Assert.False(AttributeScanner.IsPubliclyAccessible(typeof(InternalHost)));

    [Fact]
    public void IsPubliclyAccessible_PublicNestedInPublic_True()
        => Assert.True(AttributeScanner.IsPubliclyAccessible(typeof(HostForNested.NestedAttributeFixture)));

    [Fact]
    public void IsPubliclyAccessible_PublicNestedInInternal_False()
        => Assert.False(AttributeScanner.IsPubliclyAccessible(typeof(InternalHost.PublicNestedInInternal)));

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
        [Map(typeof(NestedSource))]
        public class NestedAttributeFixture
        {
            public int X { get; set; }
        }
    }

    // Internal host with a public nested type — public-nested-in-internal is NOT accessible.
    internal class InternalHost
    {
        public class PublicNestedInInternal { public int X { get; set; } }
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
            e.Reason.Contains("[Map]") && e.Reason.Contains("open-generic source"));
    }

    [Fact]
    public void OpenGenericDestination_RejectedWithMessage()
    {
        var ex = Assert.Throws<AtlasConfigurationException>(() =>
            AttributeScanner.Discover(typeof(OpenGenericDestDto<>).Assembly, new MapperConfigurationExpression()));
        Assert.Contains(ex.Errors, e =>
            e.Reason.Contains("[Map]") && e.Reason.Contains("open-generic"));
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
        // candidate filter rejects. MapAttribute's [AttributeUsage(Class)] also blocks
        // [Map] on enums at compile time. This test asserts the filter rejection.
        var enumType = typeof(SomeEnum);
        Assert.False(AttributeScanner.IsAttributeMapCandidate(enumType));
    }

    [Fact]
    public void DynamicShapeSource_Dictionary_RejectedWithMessage()
    {
        var ex = Assert.Throws<AtlasConfigurationException>(() =>
            AttributeScanner.Discover(typeof(DictionarySourceDto).Assembly, new MapperConfigurationExpression()));
        Assert.Contains(ex.Errors, e =>
            e.Reason.Contains("[Map]") && e.Reason.Contains("dynamic shape"));
    }

    [Fact]
    public void DynamicShapeSource_Expando_RejectedWithMessage()
    {
        var ex = Assert.Throws<AtlasConfigurationException>(() =>
            AttributeScanner.Discover(typeof(ExpandoSourceDto).Assembly, new MapperConfigurationExpression()));
        Assert.Contains(ex.Errors, e =>
            e.Reason.Contains("[Map]") && e.Reason.Contains("dynamic shape"));
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

    [Fact]
    public void OpenGenericMessages_DoNotContainDoubleAngleBrackets()
    {
        var ex = Assert.Throws<AtlasConfigurationException>(() =>
            AttributeScanner.Discover(typeof(OpenGenericSourceFixture).Assembly, new MapperConfigurationExpression()));
        foreach (var error in ex.Errors)
        {
            Assert.DoesNotContain("<><>", error.Reason);
        }
    }

    [Fact]
    public void DynamicShapeMessage_UsesCSharpKeywords()
    {
        var ex = Assert.Throws<AtlasConfigurationException>(() =>
            AttributeScanner.Discover(typeof(DictionarySourceDto).Assembly, new MapperConfigurationExpression()));
        var dictError = ex.Errors.First(e => e.Reason.Contains("dynamic shape") && e.SourceType == typeof(System.Collections.Generic.Dictionary<string, object>));
        Assert.Contains("Dictionary<string, object>", dictError.Reason);
        Assert.DoesNotContain("Dictionary<String, Object>", dictError.Reason);
    }

    [Fact]
    public void NonPublicDecorated_RejectedWithMustBePublicMessage()
    {
        // The internal [Map] fixture used to be silently skipped; it now surfaces a clear error.
        var ex = Assert.Throws<AtlasConfigurationException>(() =>
            AttributeScanner.Discover(typeof(AttributeScannerTests).Assembly, new MapperConfigurationExpression()));
        Assert.Contains(ex.Errors, e =>
            e.Reason.Contains("must be public") && e.Reason.Contains("InternalAttributeFixture"));
    }
}

public class SomeSource { public int X { get; set; } }
public enum SomeEnum { A, B }

public interface SomeInterfaceFixture { int X { get; } }

[Map(typeof(System.Collections.Generic.List<>))]
public class OpenGenericSourceFixture { public int X { get; set; } }   // open-generic SOURCE

[Map(typeof(SomeSource))]
public class OpenGenericDestDto<T> where T : class { public int X { get; set; } }   // open-generic DEST

[Map(typeof(System.Collections.Generic.Dictionary<string, object>))]
public class DictionarySourceDto { public int X { get; set; } }

[Map(typeof(System.Dynamic.ExpandoObject))]
public class ExpandoSourceDto { public int X { get; set; } }

[Map(typeof(AttributeScannerTests.PublicSource))]
public class PublicAttributeFixture
{
    public int X { get; set; }
}

public class UndecoratedFixture
{
    public int X { get; set; }
}

[Map(typeof(AttributeScannerTests.AbstractSource))]
public abstract class AbstractAttributeFixture
{
    public int X { get; set; }
}

[Map(typeof(AttributeScannerTests.InternalSource))]
internal class InternalAttributeFixture
{
    public int X { get; set; }
}

public class AttributeScannerPathTests
{
    private static (LambdaExpression? lambda, Type? leaf, List<ConfigurationError> errors) Walk(Type src, string path)
    {
        var errors = new List<ConfigurationError>();
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
