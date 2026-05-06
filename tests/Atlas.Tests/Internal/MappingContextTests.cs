using Atlas.Internal;

namespace Atlas.Tests.Internal;

public class MappingContextTests
{
    [Fact]
    public void TryGet_ReturnsFalse_WhenSourceNotRegistered()
    {
        var ctx = new MappingContext();
        var result = ctx.TryGet(new object(), typeof(string), out var dst);
        Assert.False(result);
        Assert.Null(dst);
    }

    [Fact]
    public void Register_ThenTryGet_ReturnsRegisteredInstance()
    {
        var ctx = new MappingContext();
        var src = new object();
        var dst = "alice";
        ctx.Register(src, typeof(string), dst);

        var found = ctx.TryGet(src, typeof(string), out var result);
        Assert.True(found);
        Assert.Same(dst, result);
    }

    [Fact]
    public void Register_SameSource_DifferentDestinationTypes_StoresSeparately()
    {
        var ctx = new MappingContext();
        var src = new object();
        ctx.Register(src, typeof(string), "as-string");
        ctx.Register(src, typeof(int), 42);

        Assert.True(ctx.TryGet(src, typeof(string), out var asString));
        Assert.True(ctx.TryGet(src, typeof(int), out var asInt));
        Assert.Equal("as-string", asString);
        Assert.Equal(42, asInt);
    }

    [Fact]
    public void Register_TwoSourceInstances_WithEqualEqualsButDifferentReferences_StoresSeparately()
    {
        var ctx = new MappingContext();
        var src1 = new ValueEqPerson { Id = 42 };
        var src2 = new ValueEqPerson { Id = 42 };
        // src1.Equals(src2) is true (overridden), but ReferenceEquals(src1, src2) is false
        Assert.True(src1.Equals(src2));
        Assert.False(ReferenceEquals(src1, src2));

        ctx.Register(src1, typeof(string), "first");
        ctx.Register(src2, typeof(string), "second");

        Assert.True(ctx.TryGet(src1, typeof(string), out var found1));
        Assert.True(ctx.TryGet(src2, typeof(string), out var found2));
        Assert.Equal("first", found1);
        Assert.Equal("second", found2);
    }

    [Fact]
    public void Register_OverwriteSameKey_KeepsLastValue()
    {
        var ctx = new MappingContext();
        var src = new object();
        ctx.Register(src, typeof(string), "first");
        ctx.Register(src, typeof(string), "second");

        Assert.True(ctx.TryGet(src, typeof(string), out var found));
        Assert.Equal("second", found);
    }

    [Fact]
    public void TryGet_AfterMultipleRegisters_FindsAll()
    {
        var ctx = new MappingContext();
        var sources = new object[5];
        for (int i = 0; i < 5; i++)
        {
            sources[i] = new object();
            ctx.Register(sources[i], typeof(int), i);
        }
        for (int i = 0; i < 5; i++)
        {
            Assert.True(ctx.TryGet(sources[i], typeof(int), out var found));
            Assert.Equal(i, found);
        }
    }

    private sealed class ValueEqPerson
    {
        public int Id { get; set; }
        public override bool Equals(object? obj) => obj is ValueEqPerson p && p.Id == Id;
        public override int GetHashCode() => Id;
    }
}
