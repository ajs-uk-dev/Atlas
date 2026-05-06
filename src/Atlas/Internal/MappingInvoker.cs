using System.Collections;

namespace Atlas.Internal;

/// <summary>
/// Static helpers invoked from inside generated mapping lambdas. Centralizes the lazy-compile
/// fallback so each generated lambda doesn't have to duplicate it.
/// </summary>
internal static class MappingInvoker
{
    public static TDestination Invoke<TSource, TDestination>(MapperRegistry registry, TSource source)
    {
        if (source is null) return default!;

        var pair = new TypePair(typeof(TSource), typeof(TDestination));
        if (registry.TryGetDelegate(pair, out var cached) && cached is Func<TSource, TDestination> typed)
            return typed(source);

        // If the user registered a map for this pair, use it (lazy compile). Registered maps always
        // win over the identity short-circuit so that, e.g., a Dictionary->Dictionary map produces
        // a new dictionary instance rather than aliasing the source.
        if (registry.GetTypeMap(pair) is not null)
        {
            var del = registry.GetOrCompile(pair, p =>
            {
                var typeMap = registry.GetTypeMap(p)!;
                return ExecutionPlanBuilder.Build(typeMap, registry).Compile();
            });
            return ((Func<TSource, TDestination>)del)(source);
        }

        // No map registered. Identity short-circuit covers nested-call sites (typically primitives
        // appearing as collection/dictionary elements where the user didn't register an explicit map).
        // Allocation-free via Unsafe.As<,> for both reference and value types.
        if (typeof(TSource) == typeof(TDestination))
            return System.Runtime.CompilerServices.Unsafe.As<TSource, TDestination>(ref source);

        throw new InvalidOperationException(
            $"No map registered for {typeof(TSource).Name} -> {typeof(TDestination).Name}.");
    }

    public static void InvokeUpdate<TSource, TDestination>(MapperRegistry registry, TSource source, TDestination destination)
    {
        if (source is null) return;
        if (destination is null) throw new ArgumentNullException(nameof(destination));

        var pair = new TypePair(typeof(TSource), typeof(TDestination));
        if (registry.TryGetUpdateDelegate(pair, out var cached) && cached is Action<TSource, TDestination> typed)
        {
            typed(source, destination);
            return;
        }

        var del = registry.GetOrCompileUpdate(pair, p =>
        {
            var typeMap = registry.GetTypeMap(p)
                ?? throw new InvalidOperationException(
                    $"No map registered for {p.Source.Name} -> {p.Destination.Name}.");
            var lambda = ExecutionPlanBuilder.BuildUpdate(typeMap, registry);
            return lambda.Compile();
        });
        ((Action<TSource, TDestination>)del)(source, destination);
    }

    public static List<TDestination> InvokeToList<TSource, TDestination>(MapperRegistry registry, IEnumerable<TSource>? source)
    {
        if (source is null) return new List<TDestination>(0);

        var list = source is ICollection<TSource> coll
            ? new List<TDestination>(coll.Count)
            : new List<TDestination>();

        foreach (var item in source)
            list.Add(Invoke<TSource, TDestination>(registry, item));

        return list;
    }

    public static TDestination[] InvokeToArray<TSource, TDestination>(MapperRegistry registry, IEnumerable<TSource>? source)
    {
        if (source is null) return [];

        if (source is ICollection<TSource> coll)
        {
            var arr = new TDestination[coll.Count];
            var i = 0;
            foreach (var item in source)
                arr[i++] = Invoke<TSource, TDestination>(registry, item);
            return arr;
        }

        var list = new List<TDestination>();
        foreach (var item in source)
            list.Add(Invoke<TSource, TDestination>(registry, item));
        return list.ToArray();
    }

    public static Dictionary<TKDest, TVDest> InvokeToDictionary<TKSrc, TVSrc, TKDest, TVDest>(
        MapperRegistry registry,
        Dictionary<TKSrc, TVSrc>? source)
        where TKSrc : notnull
        where TKDest : notnull
    {
        if (source is null) return new Dictionary<TKDest, TVDest>();
        var dict = new Dictionary<TKDest, TVDest>(source.Count);
        foreach (var kv in source)
        {
            var k = Invoke<TKSrc, TKDest>(registry, kv.Key);
            var v = Invoke<TVSrc, TVDest>(registry, kv.Value);
            dict[k] = v;
        }
        return dict;
    }

    /// <summary>
    /// Per-key value coercion helper for dict→POCO codegen (Atlas v2 #10).
    /// Tries identity → numeric/IConvertible widening → string parsing (Guid/DateTime/TimeSpan/enum)
    /// → recursive dict→POCO → registered TypeMap (srcRuntimeType, dstType).
    /// Throws AtlasMappingException with diagnostic info when no path applies.
    /// Nested map dispatch goes through reflection on MappingInvoker.Invoke&lt;TSrc, TDest&gt; so
    /// no IMapper instance flows through the compiled lambda.
    /// </summary>
    public static T? ConvertObjectTo<T>(object? value, MapperRegistry registry, string keyForDiagnostics)
    {
        if (value is null) return default;

        var dstType = typeof(T);

        if (value is T direct) return direct;

        var srcType = value.GetType();

        // Numeric / IConvertible widening
        if (TryNumericOrConvertible(value, srcType, dstType, out var numericConverted))
            return (T?)numericConverted;

        // String parsing for Guid / DateTime / TimeSpan / enum / numeric-from-string
        if (value is string s && TryParseString(s, dstType, out var parsed))
            return (T?)parsed;

        // Recursive dict→POCO via reflection on MappingInvoker.Invoke<IDictionary<string, object>, T>
        if (value is IDictionary<string, object> sub && IsPocoLike(dstType))
        {
            var invoke = typeof(MappingInvoker)
                .GetMethod(nameof(Invoke))!
                .MakeGenericMethod(typeof(IDictionary<string, object>), dstType);
            return (T?)invoke.Invoke(null, new object?[] { registry, sub });
        }

        // Registered (srcRuntimeType, dstType) TypeMap — dispatch via reflection on Invoke<srcType, T>
        if (registry.GetTypeMap(new TypePair(srcType, dstType)) is not null)
        {
            var invoke = typeof(MappingInvoker)
                .GetMethod(nameof(Invoke))!
                .MakeGenericMethod(srcType, dstType);
            return (T?)invoke.Invoke(null, new object?[] { registry, value });
        }

        throw new AtlasMappingException(
            $"Cannot convert value of type '{srcType}' at key '{keyForDiagnostics}' to '{dstType}'.");
    }

    private static bool TryNumericOrConvertible(object value, Type srcType, Type dstType, out object? converted)
    {
        var underlyingDst = Nullable.GetUnderlyingType(dstType) ?? dstType;
        if (underlyingDst.IsPrimitive || underlyingDst == typeof(decimal) || underlyingDst == typeof(string))
        {
            try
            {
                converted = Convert.ChangeType(value, underlyingDst);
                return true;
            }
            catch { converted = null; return false; }
        }
        converted = null;
        return false;
    }

    private static bool TryParseString(string s, Type dstType, out object? parsed)
    {
        var underlyingDst = Nullable.GetUnderlyingType(dstType) ?? dstType;
        if (underlyingDst == typeof(Guid)) { if (Guid.TryParse(s, out var g)) { parsed = g; return true; } }
        if (underlyingDst == typeof(DateTime)) { if (DateTime.TryParse(s, out var d)) { parsed = d; return true; } }
        if (underlyingDst == typeof(DateTimeOffset)) { if (DateTimeOffset.TryParse(s, out var dt)) { parsed = dt; return true; } }
        if (underlyingDst == typeof(TimeSpan)) { if (TimeSpan.TryParse(s, out var t)) { parsed = t; return true; } }
        if (underlyingDst.IsEnum) { if (Enum.TryParse(underlyingDst, s, ignoreCase: true, out var e)) { parsed = e; return true; } }
        if (underlyingDst.IsPrimitive || underlyingDst == typeof(decimal))
        {
            try { parsed = Convert.ChangeType(s, underlyingDst); return true; } catch { /* fall through */ }
        }
        parsed = null;
        return false;
    }

    private static bool IsPocoLike(Type t)
        => !t.IsPrimitive
        && t != typeof(string)
        && t != typeof(Guid)
        && t != typeof(DateTime)
        && t != typeof(DateTimeOffset)
        && t != typeof(TimeSpan)
        && t != typeof(decimal)
        && !t.IsEnum
        && !DynamicShape.IsDynamicShape(t);

    /// <summary>
    /// Dot-notation fallback: scans <paramref name="src"/> for keys starting with
    /// <paramref name="prefix"/>, strips the prefix, and returns a synthesized nested dict.
    /// Returns null when no matching keys exist. See docs/Atlas-Design-DynamicMapping.md §5.4.
    /// </summary>
    public static IDictionary<string, object>? ScanPrefix(
        IDictionary<string, object> src,
        string prefix,
        StringComparison cmp)
    {
        Dictionary<string, object>? result = null;
        foreach (var kv in src)
        {
            if (kv.Key.StartsWith(prefix, cmp))
            {
                result ??= new Dictionary<string, object>();
                result[kv.Key.Substring(prefix.Length)] = kv.Value;
            }
        }
        return result;
    }

    /// <summary>POCO collection materialization helper for dict→POCO codegen.</summary>
    public static List<T>? ConvertObjectToList<T>(object? value, MapperRegistry registry, string keyForDiagnostics)
    {
        if (value is null) return null;
        if (value is IEnumerable enumerable)
        {
            var list = new List<T>();
            foreach (var item in enumerable)
                list.Add(ConvertObjectTo<T>(item, registry, keyForDiagnostics)!);
            return list;
        }
        throw new AtlasMappingException(
            $"Cannot convert value of type '{value.GetType()}' at key '{keyForDiagnostics}' to 'List<{typeof(T)}>'.");
    }

    public static T[]? ConvertObjectToArray<T>(object? value, MapperRegistry registry, string keyForDiagnostics)
    {
        var list = ConvertObjectToList<T>(value, registry, keyForDiagnostics);
        return list?.ToArray();
    }
}
