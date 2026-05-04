using Atlas.Internal;

namespace Atlas;

/// <summary>
/// Root of a built mapper. Construct once per process from a fluent expression or callback,
/// hand the singleton to your DI container, and resolve <see cref="IMapper"/> wherever needed.
/// </summary>
public sealed class MapperConfiguration
{
    private readonly MapperRegistry _registry;
    private readonly ConventionOptions _conventionOptions;
    private readonly bool _enumValidationEnabled;
    private readonly StringToEnumCache _stringToEnumCache = new();
    internal StringToEnumCache Internal_StringToEnumCache => _stringToEnumCache;
    private readonly IServiceProvider? _serviceProvider;

    public MapperConfiguration(Action<MapperConfigurationExpression> configure, IServiceProvider serviceProvider)
        : this(BuildExpression(configure), serviceProvider)
    {
    }

    public MapperConfiguration(MapperConfigurationExpression expression, IServiceProvider serviceProvider)
        : this(expression)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        _serviceProvider = serviceProvider;
        _registry = new MapperRegistry(_registry.AllTypeMaps.ToList(), _stringToEnumCache, serviceProvider);
    }

    public MapperConfiguration(Action<MapperConfigurationExpression> configure)
        : this(BuildExpression(configure))
    {
    }

    public MapperConfiguration(MapperConfigurationExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);

        _conventionOptions = new ConventionOptions(
            expression.SourceMemberNamingConvention,
            expression.DestinationMemberNamingConvention,
            expression.CaseSensitive);

        _enumValidationEnabled = expression.EnumValidationEnabled;

        var typeMaps = expression.GetTypeMaps().ToList();
        var pairIndex = typeMaps.ToDictionary(t => t.Pair);
        bool HasRegisteredMap(Type s, Type d) => pairIndex.ContainsKey(new TypePair(s, d));

        // NEW: resolve inheritance before convention so derived maps see inherited
        // explicit config as already-attached (and convention then fills gaps).
        InheritanceMerger.Resolve(typeMaps, pairIndex);

        foreach (var tm in typeMaps)
            ConventionEngine.ResolveMissingMembers(tm, _conventionOptions, HasRegisteredMap);

        ReverseMapMirror.Mirror(typeMaps);

        TransformerResolver.Resolve(typeMaps, expression.ValueTransformers);

        foreach (var tm in typeMaps)
            tm.Seal();

        expression.MarkBuilt();
        _registry = new MapperRegistry(typeMaps, _stringToEnumCache);
    }

    private static MapperConfigurationExpression BuildExpression(Action<MapperConfigurationExpression> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var expr = new MapperConfigurationExpression();
        configure(expr);
        return expr;
    }

    /// <summary>Eagerly compile every registered type-map. Idempotent.</summary>
    public void CompileMappings()
    {
        foreach (var tm in _registry.AllTypeMaps)
        {
            if (_registry.HasDelegate(tm.Pair)) continue;
            _registry.GetOrCompile(tm.Pair, p =>
            {
                var lambda = ExecutionPlanBuilder.Build(tm, _registry);
                return lambda.Compile();
            });
        }
    }

    /// <summary>
    /// Validate the configured maps. Throws <see cref="AtlasConfigurationException"/> aggregating
    /// every problem found, or returns silently. Algorithm: see design §8.
    /// </summary>
    public void AssertConfigurationIsValid() =>
        ConfigurationValidator.Validate(_registry, _enumValidationEnabled, _serviceProvider);

    /// <summary>Create a fresh <see cref="IMapper"/> bound to this configuration.</summary>
    public IMapper CreateMapper() => new Mapper(this, _registry);

    // ---- Internal accessors for tests ----

    internal bool Internal_HasDelegate(TypePair pair) => _registry.HasDelegate(pair);
    internal int Internal_CompileCountFor(TypePair pair) => _registry.CompileCountFor(pair);
    internal MapperRegistry Internal_Registry => _registry;
    internal ConventionOptions Internal_ConventionOptions => _conventionOptions;
}
