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
