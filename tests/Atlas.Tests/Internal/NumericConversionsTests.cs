using Atlas.Internal;

namespace Atlas.Tests.Internal;

public class NumericConversionsTests
{
    // ---- Supported conversions ----

    [Theory]
    [InlineData(typeof(int),    typeof(long),    true)]
    [InlineData(typeof(int),    typeof(float),   true)]
    [InlineData(typeof(int),    typeof(double),  true)]
    [InlineData(typeof(int),    typeof(decimal), true)]
    [InlineData(typeof(byte),   typeof(int),     true)]
    [InlineData(typeof(byte),   typeof(long),    true)]
    [InlineData(typeof(byte),   typeof(ulong),   true)]
    [InlineData(typeof(short),  typeof(long),    true)]
    [InlineData(typeof(ushort), typeof(uint),    true)]
    [InlineData(typeof(char),   typeof(int),     true)]
    [InlineData(typeof(float),  typeof(double),  true)]
    [InlineData(typeof(sbyte),  typeof(short),   true)]
    [InlineData(typeof(uint),   typeof(ulong),   true)]

    // ---- Unsupported conversions ----

    [InlineData(typeof(long),   typeof(int),     false)]
    [InlineData(typeof(double), typeof(float),   false)]
    [InlineData(typeof(string), typeof(int),     false)]
    [InlineData(typeof(int),    typeof(int),     false)] // identity — caller handles this upstream
    [InlineData(typeof(double), typeof(decimal), false)]

    // ---- Nullable cases ----

    [InlineData(typeof(int?),  typeof(long?), true)]   // int? -> long? valid
    [InlineData(typeof(int),   typeof(long?), false)]  // non-nullable src, nullable dst
    [InlineData(typeof(int?),  typeof(long),  false)]  // nullable src, non-nullable dst
    [InlineData(typeof(long?), typeof(int?),  false)]  // wrong direction (long -> int not valid)
    public void HasImplicitConversion_ReturnsExpected(Type src, Type dst, bool expected)
    {
        Assert.Equal(expected, NumericConversions.HasImplicitConversion(src, dst));
    }
}
