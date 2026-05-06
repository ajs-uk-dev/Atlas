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

    private sealed class SimplePoco
    {
        public int Id { get; set; }
        public string? Name { get; set; }
    }
    private sealed class GuidPoco { public Guid Token { get; set; } }
    private sealed class DatePoco { public DateTime When { get; set; } }
    private sealed class NullableIntPoco { public int? MaybeAge { get; set; } }
}
