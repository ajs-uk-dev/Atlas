using System.Collections.Generic;
using System.Dynamic;
using Atlas;

namespace Atlas.Tests;

public class MapperPocoToDictTests
{
    [Fact]
    public void Map_PocoToExpandoObject_ReturnsExpandoObject()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var p = new SimplePoco { Id = 42, Name = "alice" };
        var d = mapper.Map<ExpandoObject>(p);
        Assert.IsType<ExpandoObject>(d);
        var dict = (IDictionary<string, object?>)d;
        Assert.Equal(42, dict["Id"]);
        Assert.Equal("alice", dict["Name"]);
    }

    [Fact]
    public void Map_PocoToDictionaryStringObject_ReturnsDictionaryStringObject()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var p = new SimplePoco { Id = 42, Name = "alice" };
        var d = mapper.Map<Dictionary<string, object>>(p);
        Assert.IsType<Dictionary<string, object>>(d);
        Assert.Equal(42, d["Id"]);
        Assert.Equal("alice", d["Name"]);
    }

    [Fact]
    public void Map_PocoToIDictionaryStringObject_ReturnsExpandoObjectAsAbstraction()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var p = new SimplePoco { Id = 42, Name = "alice" };
        IDictionary<string, object> d = mapper.Map<IDictionary<string, object>>(p);
        Assert.IsType<ExpandoObject>(d);
        Assert.Equal(42, d["Id"]);
    }

    [Fact]
    public void Map_NullPropertyValue_WrittenAsNullDictValue()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var p = new SimplePoco { Id = 0, Name = null };
        var d = mapper.Map<Dictionary<string, object>>(p);
        Assert.True(d.ContainsKey("Name"));
        Assert.Null(d["Name"]);
    }

    [Fact]
    public void Map_DateTimeProperty_EmitsAsBoxedDateTime()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var p = new DatePoco { When = new DateTime(2026, 5, 6) };
        var d = mapper.Map<Dictionary<string, object>>(p);
        Assert.Equal(new DateTime(2026, 5, 6), d["When"]);
    }

    [Fact]
    public void Map_GuidProperty_EmitsAsBoxedGuid()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var p = new GuidPoco { Identifier = Guid.Parse("11111111-2222-3333-4444-555555555555") };
        var d = mapper.Map<Dictionary<string, object>>(p);
        Assert.Equal(Guid.Parse("11111111-2222-3333-4444-555555555555"), d["Identifier"]);
    }

    [Fact]
    public void Map_NullPocoSource_ReturnsDefault()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        SimplePoco? p = null;
        var d = mapper.Map<SimplePoco?, ExpandoObject?>(p);
        Assert.Null(d);
    }

    [Fact]
    public void Map_NestedPoco_EmitsAsNestedExpandoObject_RegardlessOfOuterShape()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var p = new OrderPoco { Customer = new CustomerPoco { Name = "alice" } };

        var asDict = mapper.Map<Dictionary<string, object>>(p);
        Assert.IsType<ExpandoObject>(asDict["Customer"]);
        Assert.Equal("alice", ((IDictionary<string, object?>)asDict["Customer"])["Name"]);

        var asExpando = mapper.Map<ExpandoObject>(p);
        Assert.IsType<ExpandoObject>(((IDictionary<string, object?>)asExpando)["Customer"]);
    }

    [Fact]
    public void Map_NullNestedPoco_EmitsAsNullDictValue()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var p = new OrderPoco { Customer = null };
        var d = mapper.Map<Dictionary<string, object>>(p);
        Assert.True(d.ContainsKey("Customer"));
        Assert.Null(d["Customer"]);
    }

    [Fact]
    public void Map_ListOfPrimitives_EmitsAsListOfBoxedObjects()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var p = new NumberListPoco { Numbers = new List<int> { 1, 2, 3 } };
        var d = mapper.Map<Dictionary<string, object>>(p);
        Assert.IsAssignableFrom<List<object?>>(d["Numbers"]);
        Assert.Equal(new object?[] { 1, 2, 3 }, (List<object?>)d["Numbers"]!);
    }

    [Fact]
    public void Map_ListOfPocos_EmitsAsListOfExpandoObjects()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var p = new OrderWithLinesPoco
        {
            Lines = new List<OrderLinePoco> { new() { Sku = "X" }, new() { Sku = "Y" } }
        };
        var d = mapper.Map<Dictionary<string, object>>(p);
        var list = (List<object?>)d["Lines"]!;
        Assert.Equal(2, list.Count);
        Assert.IsType<ExpandoObject>(list[0]);
        Assert.Equal("X", ((IDictionary<string, object?>)list[0]!)["Sku"]);
    }

    [Fact]
    public void Map_TypedPocoDictionary_EmitsAsExpandoObjectKeyedByStringification()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var p = new InventoryPoco
        {
            Items = new Dictionary<string, OrderLinePoco>
            {
                ["A"] = new() { Sku = "X" },
                ["B"] = new() { Sku = "Y" }
            }
        };
        var d = mapper.Map<Dictionary<string, object>>(p);
        var nested = (IDictionary<string, object?>)d["Items"]!;
        Assert.IsType<ExpandoObject>(nested);
        Assert.Equal("X", ((IDictionary<string, object?>)nested["A"]!)["Sku"]);
    }

    [Fact]
    public void Map_DictionaryWithIntKeys_EmitsKeysAsStringRepresentation()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var p = new IntKeyedPoco
        {
            ById = new Dictionary<int, OrderLinePoco>
            {
                [1] = new() { Sku = "X" },
                [2] = new() { Sku = "Y" }
            }
        };
        var d = mapper.Map<Dictionary<string, object>>(p);
        var nested = (IDictionary<string, object?>)d["ById"]!;
        Assert.True(nested.ContainsKey("1"));
        Assert.True(nested.ContainsKey("2"));
    }

    [Fact]
    public void Map_EnumProperty_EmitsAsUnderlyingInteger()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var p = new StatusPoco { Status = Status.Active };
        var d = mapper.Map<Dictionary<string, object>>(p);
        Assert.Equal((int)Status.Active, d["Status"]);
    }

    [Fact]
    public void Map_ReadOnlyProperty_IsEmittedOnPocoToDict()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var p = new ReadOnlyPropPoco("alice");
        var d = mapper.Map<Dictionary<string, object>>(p);
        Assert.Equal("alice", d["Name"]);
    }

    [Fact]
    public void Map_UpdateInPlace_OverwritesMatchingKeysPreservesOthers()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var existing = new Dictionary<string, object>
        {
            ["UnrelatedKey"] = "preserved",
            ["Id"] = 0
        };
        var p = new SimplePoco { Id = 42, Name = "alice" };
        mapper.Map(p, existing);

        Assert.Equal(42, existing["Id"]);
        Assert.Equal("alice", existing["Name"]);
        Assert.Equal("preserved", existing["UnrelatedKey"]);
    }

    [Fact]
    public void RoundTrip_PocoToExpandoToPoco_ProducesEquivalentObject()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var original = new SimplePoco { Id = 42, Name = "alice" };
        var asExpando = mapper.Map<ExpandoObject>(original);
        var roundTripped = mapper.Map<SimplePoco>((IDictionary<string, object?>)asExpando);
        Assert.Equal(42, roundTripped.Id);
        Assert.Equal("alice", roundTripped.Name);
    }

    [Fact]
    public void Map_PocoWithDictionaryStringObjectProperty_PassesThrough()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        var bag = new Dictionary<string, object> { ["k"] = "v", ["n"] = 42 };
        var p = new WithDictBagPoco { Bag = bag };
        var d = mapper.Map<ExpandoObject>(p);
        var dict = (IDictionary<string, object?>)d;
        Assert.True(dict.ContainsKey("Bag"));
        var nestedBag = dict["Bag"];
        Assert.NotNull(nestedBag);
        // Pass-through semantics — the source dict instance is stored directly.
        Assert.Same(bag, nestedBag);
    }

    [Fact]
    public void Map_PocoWithExpandoObjectProperty_PassesThrough()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        dynamic e = new ExpandoObject();
        e.k = "v";
        var p = new WithExpandoPoco { Expando = (ExpandoObject)e };
        var d = mapper.Map<Dictionary<string, object>>(p);
        Assert.True(d.ContainsKey("Expando"));
        Assert.Same((object)e, d["Expando"]);
    }

    private sealed class SimplePoco
    {
        public int Id { get; set; }
        public string? Name { get; set; }
    }
    private sealed class DatePoco { public DateTime When { get; set; } }
    private sealed class GuidPoco { public Guid Identifier { get; set; } }

    // New fixtures for Task 8 tests
    private sealed class CustomerPoco { public string? Name { get; set; } }
    private sealed class OrderPoco { public CustomerPoco? Customer { get; set; } }
    private sealed class NumberListPoco { public List<int>? Numbers { get; set; } }
    private sealed class OrderLinePoco { public string? Sku { get; set; } }
    private sealed class OrderWithLinesPoco { public List<OrderLinePoco>? Lines { get; set; } }
    private sealed class InventoryPoco { public Dictionary<string, OrderLinePoco>? Items { get; set; } }
    private sealed class IntKeyedPoco { public Dictionary<int, OrderLinePoco>? ById { get; set; } }
    private enum Status { Inactive = 0, Active = 1 }
    private sealed class StatusPoco { public Status Status { get; set; } }
    private sealed class ReadOnlyPropPoco
    {
        public ReadOnlyPropPoco(string name) { Name = name; }
        public string Name { get; }
    }
    private sealed class WithDictBagPoco { public Dictionary<string, object>? Bag { get; set; } }
    private sealed class WithExpandoPoco { public ExpandoObject? Expando { get; set; } }
}
