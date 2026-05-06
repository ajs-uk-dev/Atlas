using System.Collections.Concurrent;

namespace Atlas.Internal;

/// <summary>
/// Holds the type-map definitions and the cache of compiled mapping delegates.
/// Built once during configuration; read-only afterward except for lazy delegate caching
/// and on-demand materialization of open-generic closed pairs.
/// </summary>
internal sealed class MapperRegistry
{
    private readonly ConcurrentDictionary<TypePair, TypeMap> _typeMaps;
    private readonly Dictionary<TypePair, Delegate> _delegates = new();
    private readonly Dictionary<TypePair, Delegate> _updateDelegates = new();
    private readonly Dictionary<TypePair, int> _compileCounts = new();
    private readonly Lock _lock = new();

    private readonly IReadOnlyList<OpenGenericTypeMap> _openGenericMaps;
    private readonly ValueTransformerCollection _globalTransformers;
    private readonly ConventionOptions _conventionOptions;

    public StringToEnumCache StringToEnumCache { get; }

    /// <summary>
    /// The application's root <see cref="IServiceProvider"/> when Atlas is registered through
    /// <c>Atlas.Extensions.DependencyInjection</c>; otherwise <c>null</c>. Used by
    /// <c>HookResolver</c> to instantiate <see cref="IMappingAction{TSource,TDestination}"/>
    /// implementations via <c>ActivatorUtilities.CreateInstance</c>.
    /// </summary>
    public IServiceProvider? ServiceProvider { get; }

    /// <summary>
    /// Cached <see cref="IMappingAction{TSource, TDestination}"/> instances keyed by action type.
    /// Populated by <c>HookResolver</c> at codegen time; one entry per distinct action type
    /// regardless of how many TypeMaps reference it.
    /// </summary>
    internal Dictionary<Type, object> ActionInstances { get; } = new();

    public MapperRegistry(
        IEnumerable<TypeMap> typeMaps,
        StringToEnumCache? stringToEnumCache = null,
        IServiceProvider? serviceProvider = null,
        IReadOnlyList<OpenGenericTypeMap>? openGenericMaps = null,
        ValueTransformerCollection? globalTransformers = null,
        ConventionOptions? conventionOptions = null)
    {
        _typeMaps = new ConcurrentDictionary<TypePair, TypeMap>(
            typeMaps.ToDictionary(t => t.Pair));
        StringToEnumCache = stringToEnumCache ?? new StringToEnumCache();
        ServiceProvider = serviceProvider;
        _openGenericMaps = openGenericMaps ?? Array.Empty<OpenGenericTypeMap>();
        _globalTransformers = globalTransformers ?? new ValueTransformerCollection();
        _conventionOptions = conventionOptions ?? new ConventionOptions(
            NamingConvention.PascalCase, NamingConvention.PascalCase, CaseSensitive: true);
    }

    public TypeMap? GetTypeMap(TypePair pair)
    {
        // Hot path: exact closed-pair match. ConcurrentDictionary read is lock-free.
        if (_typeMaps.TryGetValue(pair, out var m)) return m;

        // Stage 2: open-generic template scan.
        if (_openGenericMaps.Count > 0)
        {
            var template = FindMatchingOpenGenericTemplate(pair);
            if (template is not null)
            {
                // Materialize the closed pair via GetOrAdd. Under contention, the factory may
                // run more than once but only one TypeMap is stored — materialization is
                // idempotent (deterministic given the same pair + template + convention options).
                return _typeMaps.GetOrAdd(pair, p => MaterializeClosed(template, p));
            }
        }

        // Stage 3: dynamic-shape detector (Atlas v2 #10 — see docs/Atlas-Design-DynamicMapping.md §2.1)
        if (DynamicShape.IsDynamicPair(pair))
            return _typeMaps.GetOrAdd(pair, _ =>
                DynamicShape.MaterializeTypeMap(pair, _globalTransformers, _conventionOptions));

        return null;
    }

    public IReadOnlyCollection<TypeMap> AllTypeMaps => (IReadOnlyCollection<TypeMap>)_typeMaps.Values;

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

    private OpenGenericTypeMap? FindMatchingOpenGenericTemplate(TypePair pair)
    {
        // Linear scan — open-generic registrations are typically a handful per app,
        // not enough to warrant a hashed lookup.
        foreach (var template in _openGenericMaps)
        {
            if (template.Matches(pair)) return template;
        }
        return null;
    }

    private TypeMap MaterializeClosed(OpenGenericTypeMap template, TypePair closedPair)
    {
        var tm = new TypeMap(closedPair.Source, closedPair.Destination, template.MemberList)
        {
            OriginatingProfile = template.OriginatingProfile,
            RegistrationOrigin = $"{template.RegistrationOrigin} " +
                                 $"(closed at runtime as ({closedPair.Source.Name}, {closedPair.Destination.Name}))"
        };

        // HasRegisteredMap probe — the convention engine uses this to decide whether to
        // emit a nested-map invoke for non-primitive property types. Reads the live
        // ConcurrentDictionary so previously-materialized closed pairs are visible.
        bool HasRegisteredMap(Type s, Type d) => _typeMaps.ContainsKey(new TypePair(s, d));
        ConventionEngine.ResolveMissingMembers(tm, _conventionOptions, HasRegisteredMap);

        // Profile/global value transformers via the existing resolver.
        TransformerResolver.Resolve(new[] { tm }, _globalTransformers);

        tm.Seal();
        return tm;
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
