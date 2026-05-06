using System.Collections.Generic;
using System.Dynamic;
using Atlas;

namespace Atlas.Tests;

public class MapperDictToPocoTests
{
    [Fact]
    public void Map_DictWithIntValue_PopulatesIntProperty()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var dict = new Dictionary<string, object> { ["Id"] = 42 };
        var p = mapper.Map<SimplePoco>(dict);
        Assert.Equal(42, p.Id);
    }

    [Fact]
    public void Map_DictWithStringValue_PopulatesStringProperty()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var dict = new Dictionary<string, object> { ["Name"] = "alice" };
        var p = mapper.Map<SimplePoco>(dict);
        Assert.Equal("alice", p.Name);
    }

    [Fact]
    public void Map_DictWithLongValue_WidensToInt_NumericConversion()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var dict = new Dictionary<string, object> { ["Id"] = 42L };
        var p = mapper.Map<SimplePoco>(dict);
        Assert.Equal(42, p.Id);
    }

    [Fact]
    public void Map_DictWithStringValue_ParsesToGuid()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var dict = new Dictionary<string, object> { ["Token"] = "550e8400-e29b-41d4-a716-446655440000" };
        var p = mapper.Map<GuidPoco>(dict);
        Assert.Equal(Guid.Parse("550e8400-e29b-41d4-a716-446655440000"), p.Token);
    }

    [Fact]
    public void Map_DictWithStringValue_ParsesToDateTime()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var dict = new Dictionary<string, object> { ["When"] = "2026-05-06" };
        var p = mapper.Map<DatePoco>(dict);
        Assert.Equal(new DateTime(2026, 5, 6), p.When);
    }

    [Fact]
    public void Map_DictMissingKey_LeavesDestinationAtDefault()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var dict = new Dictionary<string, object> { };
        var p = mapper.Map<SimplePoco>(dict);
        Assert.Equal(0, p.Id);
        Assert.Null(p.Name);
    }

    [Fact]
    public void Map_DictWithNullValue_AssignsNullToReferenceType()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var dict = new Dictionary<string, object> { ["Name"] = null! };
        var p = mapper.Map<SimplePoco>(dict);
        Assert.Null(p.Name);
    }

    [Fact]
    public void Map_DictWithNullValue_AssignsDefaultToNonNullableValueType()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var dict = new Dictionary<string, object> { ["Id"] = null! };
        var p = mapper.Map<SimplePoco>(dict);
        Assert.Equal(0, p.Id);
    }

    [Fact]
    public void Map_DictWithNullValue_AssignsNullToNullableValueType()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var dict = new Dictionary<string, object> { ["MaybeAge"] = null! };
        var p = mapper.Map<NullableIntPoco>(dict);
        Assert.Null(p.MaybeAge);
    }

    [Fact]
    public void Map_DictWithIncompatibleType_ThrowsAtlasMappingException()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var dict = new Dictionary<string, object> { ["Id"] = "not-a-number" };
        var ex = Assert.Throws<AtlasMappingException>(() => mapper.Map<SimplePoco>(dict));
        Assert.Contains("Id", ex.Message);
    }

    [Fact]
    public void Map_ExpandoObjectSource_PopulatesProperties()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        dynamic e = new ExpandoObject();
        e.Id = 7;
        e.Name = "x";
        var p = mapper.Map<ExpandoObject, SimplePoco>(e);
        Assert.Equal(7, p.Id);
        Assert.Equal("x", p.Name);
    }

    [Fact]
    public void Map_NestedPocoFromTopLevelNestedDict()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var dict = new Dictionary<string, object>
        {
            ["Customer"] = new Dictionary<string, object> { ["Name"] = "alice" }
        };
        var p = mapper.Map<OrderPoco>(dict);
        Assert.NotNull(p.Customer);
        Assert.Equal("alice", p.Customer!.Name);
    }

    [Fact]
    public void Map_NestedPocoFromTopLevelTypedInstance()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var customer = new CustomerPoco { Name = "alice" };
        var dict = new Dictionary<string, object> { ["Customer"] = customer };
        var p = mapper.Map<OrderPoco>(dict);
        Assert.Same(customer, p.Customer);
    }

    [Fact]
    public void Map_NestedPocoFromDotNotationFallback()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var dict = new Dictionary<string, object> { ["Customer.Name"] = "alice" };
        var p = mapper.Map<OrderPoco>(dict);
        Assert.NotNull(p.Customer);
        Assert.Equal("alice", p.Customer!.Name);
    }

    [Fact]
    public void Map_TopLevelKeyWinsOverDotNotationSiblings()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var dict = new Dictionary<string, object>
        {
            ["Customer"] = new Dictionary<string, object> { ["Name"] = "from-nested" },
            ["Customer.Name"] = "from-dot-notation"  // ignored — top-level wins
        };
        var p = mapper.Map<OrderPoco>(dict);
        Assert.Equal("from-nested", p.Customer!.Name);
    }

    [Fact]
    public void Map_DeepDotNotationMultiplePrefixes()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var dict = new Dictionary<string, object> { ["Customer.Address.City"] = "NYC" };
        var p = mapper.Map<DeepOrderPoco>(dict);
        Assert.Equal("NYC", p.Customer!.Address!.City);
    }

    [Fact]
    public void Map_ListOfPrimitives_FromIEnumerableSource()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var dict = new Dictionary<string, object> { ["Numbers"] = new[] { 1, 2, 3 } };
        var p = mapper.Map<NumberListPoco>(dict);
        Assert.Equal(new[] { 1, 2, 3 }, p.Numbers!);
    }

    [Fact]
    public void Map_ListOfPocos_FromListOfDicts()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var dict = new Dictionary<string, object>
        {
            ["Lines"] = new List<IDictionary<string, object>>
            {
                new Dictionary<string, object> { ["Sku"] = "X" },
                new Dictionary<string, object> { ["Sku"] = "Y" }
            }
        };
        var p = mapper.Map<OrderWithLinesPoco>(dict);
        Assert.Equal(2, p.Lines!.Count);
        Assert.Equal("X", p.Lines[0].Sku);
        Assert.Equal("Y", p.Lines[1].Sku);
    }

    [Fact]
    public void Map_ArrayDestination_FromIEnumerableSource()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var dict = new Dictionary<string, object> { ["Tags"] = new[] { "a", "b" } };
        var p = mapper.Map<TagsArrayPoco>(dict);
        Assert.Equal(new[] { "a", "b" }, p.Tags!);
    }

    [Fact]
    public void Map_ExcessDictKeys_AreSilentlyIgnored()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var dict = new Dictionary<string, object>
        {
            ["Id"] = 1,
            ["Name"] = "alice",
            ["UnknownExtra"] = "ignored"
        };
        var p = mapper.Map<SimplePoco>(dict);
        Assert.Equal(1, p.Id);
        Assert.Equal("alice", p.Name);
    }

    [Fact]
    public void Map_OuterCollectionPair_RecursesIntoDynamicElementMap()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var src = new List<IDictionary<string, object>>
        {
            new Dictionary<string, object> { ["Id"] = 1, ["Name"] = "a" },
            new Dictionary<string, object> { ["Id"] = 2, ["Name"] = "b" }
        };
        var result = mapper.Map<List<IDictionary<string, object>>, List<SimplePoco>>(src);
        Assert.Equal(2, result.Count);
        Assert.Equal(1, result[0].Id);
        Assert.Equal("b", result[1].Name);
    }

    private sealed class SimplePoco
    {
        public int Id { get; set; }
        public string? Name { get; set; }
    }
    private sealed class GuidPoco { public Guid Token { get; set; } }
    private sealed class DatePoco { public DateTime When { get; set; } }
    private sealed class NullableIntPoco { public int? MaybeAge { get; set; } }
    private sealed class CustomerPoco { public string? Name { get; set; } }
    private sealed class OrderPoco { public CustomerPoco? Customer { get; set; } }
    private sealed class AddressPoco { public string? City { get; set; } }
    private sealed class DeepCustomerPoco { public AddressPoco? Address { get; set; } }
    private sealed class DeepOrderPoco { public DeepCustomerPoco? Customer { get; set; } }
    private sealed class NumberListPoco { public List<int>? Numbers { get; set; } }
    private sealed class OrderLinePoco { public string? Sku { get; set; } }
    private sealed class OrderWithLinesPoco { public List<OrderLinePoco>? Lines { get; set; } }
    private sealed class TagsArrayPoco { public string[]? Tags { get; set; } }
}
