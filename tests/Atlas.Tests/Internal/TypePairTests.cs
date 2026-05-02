using Atlas.Internal;

namespace Atlas.Tests.Internal;

public class TypePairTests
{
    [Fact]
    public void Equals_SameTypes_ReturnsTrue()
    {
        var a = new TypePair(typeof(int), typeof(string));
        var b = new TypePair(typeof(int), typeof(string));

        Assert.True(a.Equals(b));
        Assert.True(a == b);
    }

    [Fact]
    public void Equals_DifferentSource_ReturnsFalse()
    {
        var a = new TypePair(typeof(int), typeof(string));
        var b = new TypePair(typeof(long), typeof(string));

        Assert.False(a.Equals(b));
        Assert.True(a != b);
    }

    [Fact]
    public void Equals_DifferentDestination_ReturnsFalse()
    {
        var a = new TypePair(typeof(int), typeof(string));
        var b = new TypePair(typeof(int), typeof(object));

        Assert.False(a.Equals(b));
    }

    [Fact]
    public void GetHashCode_SamePair_IsStable()
    {
        var a = new TypePair(typeof(int), typeof(string));
        var b = new TypePair(typeof(int), typeof(string));

        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void GetHashCode_DifferentPairs_DifferProbabilistically()
    {
        // Generate 100 distinct TypePairs from a pool of common types and require >=95% distinct hashes.
        Type[] types =
        [
            typeof(int), typeof(long), typeof(string), typeof(double), typeof(decimal),
            typeof(byte), typeof(short), typeof(bool), typeof(float), typeof(object),
            typeof(Guid), typeof(DateTime), typeof(TimeSpan), typeof(Uri), typeof(Version),
        ];

        var pairs = new HashSet<TypePair>();
        var hashes = new HashSet<int>();

        for (int i = 0; i < types.Length && pairs.Count < 100; i++)
        {
            for (int j = 0; j < types.Length && pairs.Count < 100; j++)
            {
                var pair = new TypePair(types[i], types[j]);
                if (pairs.Add(pair))
                {
                    hashes.Add(pair.GetHashCode());
                }
            }
        }

        // hashes.Count / pairs.Count should be >= 0.95
        Assert.True(hashes.Count * 100 >= pairs.Count * 95,
            $"Hash distribution too poor: {hashes.Count} unique hashes for {pairs.Count} unique pairs");
    }

    [Fact]
    public void Pair_IsValueType()
    {
        // Sanity guard against accidental refactor to class. Allocation budget depends on this.
        Assert.True(typeof(TypePair).IsValueType);
    }
}
