namespace Atlas.Internal;

/// <summary>
/// Holds the type-map definitions and the cache of compiled mapping delegates.
/// Built once during configuration; read-only afterward except for lazy delegate caching.
/// </summary>
internal sealed class MapperRegistry
{
    private readonly Dictionary<TypePair, TypeMap> _typeMaps;
    private readonly Dictionary<TypePair, Delegate> _delegates = new();
    private readonly Dictionary<TypePair, Delegate> _updateDelegates = new();
    private readonly Dictionary<TypePair, int> _compileCounts = new();
    private readonly Lock _lock = new();

    public StringToEnumCache StringToEnumCache { get; }

    public MapperRegistry(IEnumerable<TypeMap> typeMaps, StringToEnumCache? stringToEnumCache = null)
    {
        _typeMaps = typeMaps.ToDictionary(t => t.Pair);
        StringToEnumCache = stringToEnumCache ?? new StringToEnumCache();
    }

    public TypeMap? GetTypeMap(TypePair pair) =>
        _typeMaps.TryGetValue(pair, out var m) ? m : null;

    public IReadOnlyCollection<TypeMap> AllTypeMaps => _typeMaps.Values;

    public bool TryGetDelegate(TypePair pair, out Delegate? del)
    {
        lock (_lock)
        {
            return _delegates.TryGetValue(pair, out del);
        }
    }

    public bool HasDelegate(TypePair pair)
    {
        lock (_lock) { return _delegates.ContainsKey(pair); }
    }

    public int CompileCountFor(TypePair pair)
    {
        lock (_lock)
        {
            return _compileCounts.TryGetValue(pair, out var c) ? c : 0;
        }
    }

    /// <summary>
    /// Atomically returns the compiled delegate for <paramref name="pair"/>, building and caching it
    /// (via <paramref name="compile"/>) on first request. The build call is held under the registry lock,
    /// so concurrent callers see a single compile per type-pair.
    /// </summary>
    public Delegate GetOrCompile(TypePair pair, Func<TypePair, Delegate> compile)
    {
        lock (_lock)
        {
            if (_delegates.TryGetValue(pair, out var existing)) return existing;
            var fresh = compile(pair);
            _delegates[pair] = fresh;
            _compileCounts[pair] = (_compileCounts.TryGetValue(pair, out var c) ? c : 0) + 1;
            return fresh;
        }
    }

    public void Register(TypePair pair, Delegate del)
    {
        lock (_lock)
        {
            if (_delegates.ContainsKey(pair)) return;   // already compiled — keep idempotent
            _delegates[pair] = del;
            _compileCounts[pair] = (_compileCounts.TryGetValue(pair, out var c) ? c : 0) + 1;
        }
    }

    // ---- Update-in-place delegates ----

    public bool TryGetUpdateDelegate(TypePair pair, out Delegate? del)
    {
        lock (_lock)
        {
            return _updateDelegates.TryGetValue(pair, out del);
        }
    }

    public Delegate GetOrCompileUpdate(TypePair pair, Func<TypePair, Delegate> compile)
    {
        lock (_lock)
        {
            if (_updateDelegates.TryGetValue(pair, out var existing)) return existing;
            var fresh = compile(pair);
            _updateDelegates[pair] = fresh;
            return fresh;
        }
    }
}
