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

    public MappingExpression(TypeMap typeMap)
    {
        TypeMap = typeMap;
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
