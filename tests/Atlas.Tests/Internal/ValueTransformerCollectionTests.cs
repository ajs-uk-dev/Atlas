using System.Linq.Expressions;

namespace Atlas.Tests.Internal;

public class ValueTransformerCollectionTests
{
    [Fact]
    public void Add_ReturnsThis_ForChaining()
    {
        var collection = new ValueTransformerCollection();
        Expression<Func<string, string>> transformer = s => s;

        var returned = collection.Add(transformer);

        Assert.Same(collection, returned);
    }

    [Fact]
    public void Add_MultipleSameType_AppendsInFifoOrder()
    {
        var collection = new ValueTransformerCollection();
        Expression<Func<string, string>> first = s => s + "1";
        Expression<Func<string, string>> second = s => s + "2";
        Expression<Func<string, string>> third = s => s + "3";

        collection.Add(first);
        collection.Add(second);
        collection.Add(third);

        var all = collection.AllTransformers;
        var stringEntries = all[typeof(string)];
        Assert.Equal(3, stringEntries.Count);
        Assert.Same(first, stringEntries[0]);
        Assert.Same(second, stringEntries[1]);
        Assert.Same(third, stringEntries[2]);
    }

    [Fact]
    public void Add_MultipleDifferentTypes_StoredSeparately()
    {
        var collection = new ValueTransformerCollection();
        Expression<Func<string, string>> stringT = s => s;
        Expression<Func<int, int>> intT = i => i + 1;

        collection.Add(stringT);
        collection.Add(intT);

        var all = collection.AllTransformers;
        Assert.Equal(2, all.Count);
        Assert.Single(all[typeof(string)]);
        Assert.Single(all[typeof(int)]);
        Assert.Same(stringT, all[typeof(string)][0]);
        Assert.Same(intT, all[typeof(int)][0]);
    }

    [Fact]
    public void Add_NullTransformer_Throws()
    {
        var collection = new ValueTransformerCollection();
        Assert.Throws<ArgumentNullException>(() => collection.Add<string>(null!));
    }
}
