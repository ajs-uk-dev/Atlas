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
        var p = new GuidPoco { Token = Guid.Parse("550e8400-e29b-41d4-a716-446655440000") };
        var d = mapper.Map<Dictionary<string, object>>(p);
        Assert.Equal(Guid.Parse("550e8400-e29b-41d4-a716-446655440000"), d["Token"]);
    }

    [Fact]
    public void Map_NullPocoSource_ReturnsDefault()
    {
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();
        SimplePoco? p = null;
        var d = mapper.Map<SimplePoco?, ExpandoObject?>(p);
        Assert.Null(d);
    }

    private sealed class SimplePoco
    {
        public int Id { get; set; }
        public string? Name { get; set; }
    }
    private sealed class DatePoco { public DateTime When { get; set; } }
    private sealed class GuidPoco { public Guid Token { get; set; } }
}
