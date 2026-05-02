using System.Reflection;
using Atlas;
using Atlas.Internal;

namespace Atlas.Tests.Internal;

#pragma warning disable IDE1006 // intentionally non-standard naming for snake_case fixtures
#pragma warning disable CS9113  // unused parameter in indexer fixture

public class ConventionEngineTests
{
    private static ConventionOptions PascalBoth(bool caseSensitive = true) =>
        new(NamingConvention.PascalCase, NamingConvention.PascalCase, caseSensitive);

    private static PropertyInfo Prop<T>(string name) =>
        typeof(T).GetProperty(name)!;

    [Fact]
    public void DirectMatch_SameNameSameType_Resolves()
    {
        var path = ConventionEngine.TryResolve(typeof(FlatSrc), Prop<FlatDst>(nameof(FlatDst.Id)), PascalBoth());

        Assert.NotNull(path);
        Assert.Single(path.Members);
        Assert.Equal(nameof(FlatSrc.Id), path.Members[0].Name);
    }

    [Fact]
    public void DirectMatch_DifferentTypes_LeavesUnresolved()
    {
        var path = ConventionEngine.TryResolve(typeof(TypeMismatchSrc), Prop<TypeMismatchDst>(nameof(TypeMismatchDst.Id)), PascalBoth());

        Assert.Null(path);
    }

    [Fact]
    public void Flattening_TwoLevels_Resolves()
    {
        var path = ConventionEngine.TryResolve(typeof(FlatteningSrc), Prop<FlatteningDst>(nameof(FlatteningDst.CustomerName)), PascalBoth());

        Assert.NotNull(path);
        Assert.Equal(2, path.Members.Count);
        Assert.Equal(nameof(FlatteningSrc.Customer), path.Members[0].Name);
        Assert.Equal(nameof(FlatCustomer.Name), path.Members[1].Name);
    }

    [Fact]
    public void Flattening_ThreeLevels_Resolves()
    {
        var path = ConventionEngine.TryResolve(typeof(ThreeLevelSrc), Prop<ThreeLevelDst>(nameof(ThreeLevelDst.CustomerAddressCity)), PascalBoth());

        Assert.NotNull(path);
        Assert.Equal(3, path.Members.Count);
        Assert.Equal("Customer", path.Members[0].Name);
        Assert.Equal("Address", path.Members[1].Name);
        Assert.Equal("City", path.Members[2].Name);
    }

    [Fact]
    public void NamingConvention_SnakeSourceToPascalDest_Resolves()
    {
        var options = new ConventionOptions(NamingConvention.SnakeCase, NamingConvention.PascalCase, true);
        var path = ConventionEngine.TryResolve(typeof(SnakeSrc), Prop<PascalDst>(nameof(PascalDst.CustomerName)), options);

        Assert.NotNull(path);
        Assert.Single(path.Members);
        Assert.Equal("customer_name", path.Members[0].Name);
    }

    [Fact]
    public void NamingConvention_PascalSourceToCamelDest_Resolves()
    {
        var options = new ConventionOptions(NamingConvention.PascalCase, NamingConvention.CamelCase, true);
        var path = ConventionEngine.TryResolve(typeof(PascalSrc), Prop<CamelDst>(nameof(CamelDst.customerName)), options);

        Assert.NotNull(path);
        Assert.Single(path.Members);
        Assert.Equal("CustomerName", path.Members[0].Name);
    }

    [Fact]
    public void CaseSensitive_LowerToUpper_DoesNotResolve()
    {
        // Both sides claim PascalCase but member names differ in case. CaseSensitive=true => no match.
        var path = ConventionEngine.TryResolve(typeof(CaseLowerSrc), Prop<CaseUpperDst>(nameof(CaseUpperDst.Email)), PascalBoth(caseSensitive: true));

        Assert.Null(path);
    }

    [Fact]
    public void CaseInsensitive_LowerToUpper_Resolves()
    {
        var path = ConventionEngine.TryResolve(typeof(CaseLowerSrc), Prop<CaseUpperDst>(nameof(CaseUpperDst.Email)), PascalBoth(caseSensitive: false));

        Assert.NotNull(path);
        Assert.Single(path.Members);
        Assert.Equal("email", path.Members[0].Name);
    }

    [Fact]
    public void Indexer_OnSource_IsSkipped()
    {
        // Indexer has the default name "Item" and would match dest.Item if not filtered.
        var path = ConventionEngine.TryResolve(typeof(IndexerSrc), Prop<IndexerDst>(nameof(IndexerDst.Item)), PascalBoth());

        Assert.Null(path);
    }

    [Fact]
    public void PrivateGetter_OnSource_IsSkipped()
    {
        // The 'Hidden' source property has a private getter; engine treats it as not readable.
        var path = ConventionEngine.TryResolve(typeof(PrivateGetterSrc), Prop<PrivateGetterDst>(nameof(PrivateGetterDst.Hidden)), PascalBoth());

        Assert.Null(path);
    }

    [Fact]
    public void InitOnlySetter_OnDestination_CountsAsWritable()
    {
        // Engine resolves source -> destination; init-only on the destination is a writable destination concern.
        // Verify the engine resolves source 'Name' regardless of destination setter shape.
        var dest = Prop<InitOnlyDst>(nameof(InitOnlyDst.Name));
        Assert.True(dest.CanWrite, "Init-only property must report CanWrite=true");
        Assert.True(dest.SetMethod is { IsPublic: true });

        var path = ConventionEngine.TryResolve(typeof(InitOnlySrc), dest, PascalBoth());
        Assert.NotNull(path);
    }

    [Fact]
    public void RequiredProperty_OnDestination_CountsAsWritable()
    {
        var dest = Prop<RequiredDst>(nameof(RequiredDst.Name));
        Assert.True(dest.CanWrite);
        Assert.True(dest.SetMethod is { IsPublic: true });

        var path = ConventionEngine.TryResolve(typeof(RequiredSrc), dest, PascalBoth());
        Assert.NotNull(path);
    }
}

// ---- Test fixtures (file-scoped) ----

file class FlatSrc { public int Id { get; set; } }
file class FlatDst { public int Id { get; set; } }

file class TypeMismatchSrc { public int Id { get; set; } }
file class TypeMismatchDst { public string Id { get; set; } = ""; }

file class FlatteningSrc { public FlatCustomer Customer { get; set; } = new(); }
file class FlatteningDst { public string CustomerName { get; set; } = ""; }
file class FlatCustomer { public string Name { get; set; } = ""; }

file class ThreeLevelSrc { public Lvl1 Customer { get; set; } = new(); }
file class Lvl1 { public Lvl2 Address { get; set; } = new(); }
file class Lvl2 { public string City { get; set; } = ""; }
file class ThreeLevelDst { public string CustomerAddressCity { get; set; } = ""; }

file class SnakeSrc { public string customer_name { get; set; } = ""; }
file class PascalDst { public string CustomerName { get; set; } = ""; }

file class PascalSrc { public string CustomerName { get; set; } = ""; }
file class CamelDst { public string customerName { get; set; } = ""; }

file class CaseLowerSrc { public string email { get; set; } = ""; }
file class CaseUpperDst { public string Email { get; set; } = ""; }

file class IndexerSrc
{
    public string this[int i] { get => ""; set { } }
}
file class IndexerDst { public string Item { get; set; } = ""; }

file class PrivateGetterSrc { public string Hidden { private get; set; } = ""; }
file class PrivateGetterDst { public string Hidden { get; set; } = ""; }

file class InitOnlySrc { public string Name { get; set; } = ""; }
file class InitOnlyDst { public string Name { get; init; } = ""; }

file class RequiredSrc { public string Name { get; set; } = ""; }
file class RequiredDst { public required string Name { get; set; } }
