namespace Atlas.Internal;

/// <summary>
/// Single source of truth for "is there an implicit numeric conversion from src to dst?"
/// Replaces five duplicated copies of this switch (in ConventionEngine, ConfigurationValidator,
/// ExecutionPlanBuilder, and the projections package's validator + builder).
/// </summary>
internal static class NumericConversions
{
    /// <summary>
    /// True if C# would implicitly convert from <paramref name="src"/> to <paramref name="dst"/>
    /// at compile time. Handles Nullable&lt;T&gt; symmetrically: int? -&gt; long? is valid iff int -&gt; long is valid.
    /// </summary>
    public static bool HasImplicitConversion(Type src, Type dst)
    {
        // Unwrap Nullable<T> on both sides: int? -> long? is valid if int -> long is valid.
        var srcUnderlying = Nullable.GetUnderlyingType(src);
        var dstUnderlying = Nullable.GetUnderlyingType(dst);
        if (srcUnderlying is not null || dstUnderlying is not null)
        {
            if (srcUnderlying is null || dstUnderlying is null) return false;
            return HasImplicitConversion(srcUnderlying, dstUnderlying);
        }

        return (src, dst) switch
        {
            _ when src == typeof(sbyte) => dst == typeof(short) || dst == typeof(int) || dst == typeof(long) || dst == typeof(float) || dst == typeof(double) || dst == typeof(decimal),
            _ when src == typeof(byte) => dst == typeof(short) || dst == typeof(ushort) || dst == typeof(int) || dst == typeof(uint) || dst == typeof(long) || dst == typeof(ulong) || dst == typeof(float) || dst == typeof(double) || dst == typeof(decimal),
            _ when src == typeof(short) => dst == typeof(int) || dst == typeof(long) || dst == typeof(float) || dst == typeof(double) || dst == typeof(decimal),
            _ when src == typeof(ushort) => dst == typeof(int) || dst == typeof(uint) || dst == typeof(long) || dst == typeof(ulong) || dst == typeof(float) || dst == typeof(double) || dst == typeof(decimal),
            _ when src == typeof(int) => dst == typeof(long) || dst == typeof(float) || dst == typeof(double) || dst == typeof(decimal),
            _ when src == typeof(uint) => dst == typeof(long) || dst == typeof(ulong) || dst == typeof(float) || dst == typeof(double) || dst == typeof(decimal),
            _ when src == typeof(long) => dst == typeof(float) || dst == typeof(double) || dst == typeof(decimal),
            _ when src == typeof(ulong) => dst == typeof(float) || dst == typeof(double) || dst == typeof(decimal),
            _ when src == typeof(float) => dst == typeof(double),
            _ when src == typeof(char) => dst == typeof(ushort) || dst == typeof(int) || dst == typeof(uint) || dst == typeof(long) || dst == typeof(ulong) || dst == typeof(float) || dst == typeof(double) || dst == typeof(decimal),
            _ => false,
        };
    }
}
