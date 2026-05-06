using System.Linq.Expressions;
using System.Reflection;
using Atlas.Internal;

namespace Atlas.Configuration;

/// <summary>
/// Concrete implementation of <see cref="IMappingExpression{TSource, TDestination}"/>.
/// Wraps a <see cref="TypeMap"/> and exposes the fluent surface that mutates it.
/// </summary>
internal sealed class MappingExpression<TSource, TDestination> : IMappingExpression<TSource, TDestination>
{
    public TypeMap TypeMap { get; }
    private readonly Action<TypeMap>? _sink;

    public MappingExpression(TypeMap typeMap, Action<TypeMap>? sink = null)
    {
        TypeMap = typeMap;
        _sink = sink;
    }

    public IMappingExpression<TSource, TDestination> ForMember<TMember>(
        Expression<Func<TDestination, TMember>> destinationMember,
        Action<IMemberConfigurationExpression<TSource, TDestination, TMember>> memberOptions)
    {
        TypeMap.EnsureMutable();
        var prop = ExtractProperty(destinationMember);

        // Last call wins for the same member.
        TypeMap.PropertyMaps.RemoveAll(p => p.Name == prop.Name);

        var pm = PropertyMap.ForProperty(prop);
        var member = new MemberConfigurationExpression<TSource, TDestination, TMember>();
        memberOptions(member);
        member.ApplyTo(pm);
        pm.IsExplicit = true;
        TypeMap.PropertyMaps.Add(pm);
        return this;
    }

    public IMappingExpression<TSource, TDestination> ForCtorParam(
        string ctorParamName,
        Action<IMemberConfigurationExpression<TSource, TDestination, object?>> paramOptions)
    {
        TypeMap.EnsureMutable();
        var param = FindCtorParam(ctorParamName);

        TypeMap.PropertyMaps.RemoveAll(p => p.Name == param.Name);

        var pm = PropertyMap.ForCtorParam(param);
        var member = new MemberConfigurationExpression<TSource, TDestination, object?>();
        paramOptions(member);
        member.ApplyTo(pm);
        pm.IsExplicit = true;
        TypeMap.PropertyMaps.Add(pm);
        return this;
    }

    public IMappingExpression<TSource, TDestination> ForPath<TMember>(
        Expression<Func<TDestination, TMember>> destinationPath,
        Action<IMemberConfigurationExpression<TSource, TDestination, TMember>> memberOptions)
    {
        TypeMap.EnsureMutable();
        var path = ExtractPath(destinationPath);

        // Single-level: behave exactly like ForMember (no DestinationPath set).
        if (path.Count == 1)
        {
            TypeMap.PropertyMaps.RemoveAll(p => p.Name == path[0].Name);

            var pmSingle = PropertyMap.ForProperty(path[0]);
            var memberSingle = new MemberConfigurationExpression<TSource, TDestination, TMember>();
            memberOptions(memberSingle);
            memberSingle.ApplyTo(pmSingle);
            pmSingle.IsExplicit = true;
            TypeMap.PropertyMaps.Add(pmSingle);
            return this;
        }

        // Multi-level: store full path; Name = "A.B.C".
        var dottedName = string.Join('.', path.Select(p => p.Name));
        TypeMap.PropertyMaps.RemoveAll(p => p.Name == dottedName);

        var pm = PropertyMap.ForPath(path);
        var member = new MemberConfigurationExpression<TSource, TDestination, TMember>();
        memberOptions(member);
        member.ApplyTo(pm);
        pm.IsExplicit = true;
        TypeMap.PropertyMaps.Add(pm);
        return this;
    }

    public void ConvertUsing<TConverter>() where TConverter : ITypeConverter<TSource, TDestination>, new()
    {
        TypeMap.EnsureMutable();
        var converter = new TConverter();
        Func<TSource, TDestination> del = source => converter.Convert(source, default!);
        TypeMap.CustomConverter = del;
    }

    public void ConvertUsing(Func<TSource, TDestination> converter)
    {
        TypeMap.EnsureMutable();
        TypeMap.CustomConverter = converter;
    }

    public IMappingExpression<TSource, TDestination> Include<TDerivedSource, TDerivedDestination>()
        where TDerivedSource : TSource
        where TDerivedDestination : TDestination
    {
        TypeMap.EnsureMutable();
        var pair = new TypePair(typeof(TDerivedSource), typeof(TDerivedDestination));
        if (!TypeMap.IncludedDerived.Contains(pair))
            TypeMap.IncludedDerived.Add(pair);
        return this;
    }

    public IMappingExpression<TSource, TDestination> IncludeBase<TBaseSource, TBaseDestination>()
    {
        TypeMap.EnsureMutable();
        var pair = new TypePair(typeof(TBaseSource), typeof(TBaseDestination));
        if (!TypeMap.IncludedBases.Contains(pair))
            TypeMap.IncludedBases.Add(pair);
        return this;
    }

    public IMappingExpression<TDestination, TSource> ReverseMap(MemberList memberList = MemberList.None)
    {
        TypeMap.EnsureMutable();

        if (TypeMap.CachedReverseExpression is MappingExpression<TDestination, TSource> existing)
        {
            var existingMemberList = existing.TypeMap.MemberList;
            if (existingMemberList != memberList)
                throw new AtlasConfigurationException(new List<ConfigurationError>
                {
                    new(typeof(TSource), typeof(TDestination), "(ReverseMap)",
                        $"ReverseMap on ({typeof(TSource).Name}, {typeof(TDestination).Name}) was previously " +
                        $"called with MemberList.{existingMemberList}; cannot now call with MemberList.{memberList}.")
                });
            return existing;
        }

        if (_sink is null)
            throw new InvalidOperationException(
                "ReverseMap can only be called on a MappingExpression created via MapperProfile.CreateMap " +
                "or MapperConfigurationExpression.CreateMap (which provide a sink for the reverse TypeMap).");

        var reverseTm = new TypeMap(typeof(TDestination), typeof(TSource), memberList)
        {
            ReverseMapPair = TypeMap.Pair,
            RegistrationOrigin = $"CreateMap<{typeof(TSource).Name}, {typeof(TDestination).Name}>().ReverseMap()",
            OriginatingProfile = TypeMap.OriginatingProfile,
            PreserveReferences = TypeMap.PreserveReferences,
        };
        _sink(reverseTm);

        var reverseExpr = new MappingExpression<TDestination, TSource>(reverseTm, _sink);
        TypeMap.CachedReverseExpression = reverseExpr;
        return reverseExpr;
    }

    // ---- Enum surface ----

    public IMappingExpression<TSource, TDestination> MapByValue()
    {
        TypeMap.EnsureMutable();
        EnsureBothEnums(nameof(MapByValue));
        EnsureEnumConfig().SetStrategy(EnumMappingStrategy.ByValue, ignoreCase: false);
        return this;
    }

    public IMappingExpression<TSource, TDestination> MapByName(bool ignoreCase = false)
    {
        TypeMap.EnsureMutable();
        EnsureBothEnums(nameof(MapByName));
        EnsureEnumConfig().SetStrategy(EnumMappingStrategy.ByName, ignoreCase);
        return this;
    }

    public IMappingExpression<TSource, TDestination> MapValue(TSource sourceValue, TDestination destinationValue)
    {
        TypeMap.EnsureMutable();
        EnsureBothEnums(nameof(MapValue));
        EnsureEnumConfig().AddOverride(sourceValue!, destinationValue!);
        return this;
    }

    public IMappingExpression<TSource, TDestination> Ignore(TSource sourceValue)
    {
        TypeMap.EnsureMutable();
        EnsureSourceEnum(nameof(Ignore));
        EnsureEnumConfig().AddIgnore(sourceValue!);
        return this;
    }

    public IMappingExpression<TSource, TDestination> WithFallback(TDestination fallbackValue)
    {
        TypeMap.EnsureMutable();
        EnsureDestEnum(nameof(WithFallback));
        EnsureEnumConfig().SetFallback(fallbackValue!);
        return this;
    }

    public IMappingExpression<TSource, TDestination> PreserveReferences()
    {
        TypeMap.EnsureMutable();
        TypeMap.PreserveReferences = true;
        return this;
    }

    // ---- Before/After hooks ----

    public IMappingExpression<TSource, TDestination> BeforeMap(Action<TSource, TDestination> hook)
    {
        TypeMap.EnsureMutable();
        ArgumentNullException.ThrowIfNull(hook);
        TypeMap.BeforeHooks.Add(HookEntry.FromLambda(hook));
        return this;
    }

    public IMappingExpression<TSource, TDestination> BeforeMap<TAction>()
        where TAction : IMappingAction<TSource, TDestination>
    {
        TypeMap.EnsureMutable();
        TypeMap.BeforeHooks.Add(HookEntry.FromActionType(typeof(TAction)));
        return this;
    }

    public IMappingExpression<TSource, TDestination> AfterMap(Action<TSource, TDestination> hook)
    {
        TypeMap.EnsureMutable();
        ArgumentNullException.ThrowIfNull(hook);
        TypeMap.AfterHooks.Add(HookEntry.FromLambda(hook));
        return this;
    }

    public IMappingExpression<TSource, TDestination> AfterMap<TAction>()
        where TAction : IMappingAction<TSource, TDestination>
    {
        TypeMap.EnsureMutable();
        TypeMap.AfterHooks.Add(HookEntry.FromActionType(typeof(TAction)));
        return this;
    }

    // ---- Value transformers ----

    public IMappingExpression<TSource, TDestination> AddTransform<T>(Expression<Func<T, T>> transformer)
    {
        TypeMap.EnsureMutable();
        ArgumentNullException.ThrowIfNull(transformer);

        var key = typeof(T);
        if (!TypeMap.TypeMapTransformers.TryGetValue(key, out var list))
        {
            list = new List<LambdaExpression>();
            TypeMap.TypeMapTransformers[key] = list;
        }
        list.Add(transformer);
        return this;
    }

    private EnumMapConfig EnsureEnumConfig() => TypeMap.EnumConfig ??= new EnumMapConfig();

    private static void EnsureBothEnums(string methodName)
    {
        if (!typeof(TSource).IsEnum || !typeof(TDestination).IsEnum)
            throw new InvalidOperationException(
                $"{methodName}() requires both TSource ({typeof(TSource).Name}) and TDestination ({typeof(TDestination).Name}) to be enum types.");
    }

    private static void EnsureSourceEnum(string methodName)
    {
        if (!typeof(TSource).IsEnum)
            throw new InvalidOperationException(
                $"{methodName}(TSource) requires TSource ({typeof(TSource).Name}) to be an enum type.");
    }

    private static void EnsureDestEnum(string methodName)
    {
        if (!typeof(TDestination).IsEnum)
            throw new InvalidOperationException(
                $"{methodName}(TDestination) requires TDestination ({typeof(TDestination).Name}) to be an enum type.");
    }

    private static PropertyInfo ExtractProperty<TMember>(Expression<Func<TDestination, TMember>> selector)
    {
        var body = selector.Body;
        if (body is UnaryExpression { NodeType: ExpressionType.Convert, Operand: var operand })
            body = operand;

        if (body is MemberExpression { Member: PropertyInfo prop })
            return prop;

        throw new ArgumentException(
            "Destination selector must be a single property access expression (e.g., d => d.PropertyName).",
            nameof(selector));
    }

    private static IReadOnlyList<PropertyInfo> ExtractPath<TMember>(Expression<Func<TDestination, TMember>> selector)
    {
        var body = selector.Body;
        if (body is UnaryExpression { NodeType: ExpressionType.Convert, Operand: var operand })
            body = operand;

        var stack = new Stack<PropertyInfo>();
        var current = body;
        while (current is MemberExpression me)
        {
            if (me.Member is not PropertyInfo prop)
                throw new ArgumentException(
                    "Destination selector must be a chain of property accesses (e.g., d => d.Outer.Inner.Property).",
                    nameof(selector));
            stack.Push(prop);
            current = me.Expression!;
        }

        if (current is not ParameterExpression)
            throw new ArgumentException(
                "Destination selector must be a chain of property accesses (e.g., d => d.Outer.Inner.Property).",
                nameof(selector));

        if (stack.Count == 0)
            throw new ArgumentException(
                "Destination selector must reference at least one property.",
                nameof(selector));

        return stack.ToArray();
    }

    private static ParameterInfo FindCtorParam(string ctorParamName)
    {
        foreach (var ctor in typeof(TDestination).GetConstructors())
        {
            foreach (var p in ctor.GetParameters())
            {
                if (string.Equals(p.Name, ctorParamName, StringComparison.OrdinalIgnoreCase))
                    return p;
            }
        }
        throw new ArgumentException(
            $"No constructor parameter named '{ctorParamName}' found on {typeof(TDestination).Name}.",
            nameof(ctorParamName));
    }
}
