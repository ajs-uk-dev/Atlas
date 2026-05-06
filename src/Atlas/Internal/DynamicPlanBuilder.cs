using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;

namespace Atlas.Internal;

/// <summary>
/// Codegen for dynamic TypeMaps (TypeMap.IsDynamic == true).
/// See docs/Atlas-Design-DynamicMapping.md §5 (dict→POCO) and §6 (POCO→dict).
/// </summary>
internal static class DynamicPlanBuilder
{
    public static LambdaExpression Build(TypeMap typeMap, MapperRegistry registry)
    {
        if (DynamicShape.IsDynamicShape(typeMap.SourceType))
            return BuildDictToPocoLambda(typeMap, registry);
        else
            return BuildPocoToDictLambda(typeMap, registry);
    }

    private static LambdaExpression BuildDictToPocoLambda(TypeMap typeMap, MapperRegistry registry)
    {
        if (typeMap.DestinationType.GetConstructor(Type.EmptyTypes) is null)
            throw new AtlasMappingException(
                $"Dynamic dict→POCO mapping for '{typeMap.DestinationType.FullName}' requires a public " +
                "parameterless constructor. Constructor-injection support is planned for Task 6 of the " +
                "Atlas v2 Dynamic Mapping feature.");

        var srcParam = Expression.Parameter(typeMap.SourceType, "src");

        // Coerce src parameter to IDictionary<string, object> for uniform handling.
        var dictType = typeof(IDictionary<string, object>);
        var srcAsDict = typeMap.SourceType == dictType
            ? (Expression)srcParam
            : Expression.Convert(srcParam, dictType);

        var dst = Expression.Variable(typeMap.DestinationType, "dst");
        var body = new List<Expression> { Expression.Assign(dst, Expression.New(typeMap.DestinationType)) };

        var tryGetValue = dictType.GetMethod(
            nameof(IDictionary<string, object>.TryGetValue))!;
        var convertMethodGeneric = typeof(MappingInvoker)
            .GetMethod(nameof(MappingInvoker.ConvertObjectTo), BindingFlags.Public | BindingFlags.Static)!;
        var registryConst = Expression.Constant(registry);

        foreach (var pm in typeMap.PropertyMaps)
        {
            if (pm.DynamicKey is null || pm.DestinationProperty is null) continue;

            var keyExpr = Expression.Constant(pm.DynamicKey, typeof(string));
            var valueVar = Expression.Variable(typeof(object), "v_" + pm.DynamicKey);
            var hasValue = Expression.Variable(typeof(bool), "h_" + pm.DynamicKey);
            var dstProp = pm.DestinationProperty;

            // dst.Prop = MappingInvoker.ConvertObjectTo<TProp>(v, registry, "Prop");
            var convertCall = Expression.Call(
                convertMethodGeneric.MakeGenericMethod(dstProp.PropertyType),
                valueVar, registryConst, keyExpr);

            var assign = Expression.Assign(
                Expression.Property(dst, dstProp),
                convertCall);

            // if (src.TryGetValue(key, out v)) dst.Prop = ...
            body.Add(Expression.Block(
                new[] { valueVar, hasValue },
                Expression.Assign(hasValue, Expression.Call(srcAsDict, tryGetValue, keyExpr, valueVar)),
                Expression.IfThen(hasValue, assign)
            ));
        }

        body.Add(dst);

        var block = Expression.Block(new[] { dst }, body);
        return Expression.Lambda(block, srcParam);
    }

    private static LambdaExpression BuildPocoToDictLambda(TypeMap typeMap, MapperRegistry registry)
        => throw new NotImplementedException(
            "POCO→Dict codegen lands in Task 7; this branch is intentionally unreachable for Tasks 4–6.");
}
