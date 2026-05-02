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
}
