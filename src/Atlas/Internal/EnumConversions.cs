using System.Linq.Expressions;

namespace Atlas.Internal;

/// <summary>
/// Property-level enum conversion layer (§7.3 spec). Used by <c>ConventionEngine</c> for
/// compatibility checks and by <c>ExecutionPlanBuilder.ConvertOrMap</c> to emit the
/// expression for a single-property conversion. Does NOT handle registered
/// <c>CreateMap&lt;E1, E2&gt;()</c> typemaps — those go through <c>ExecutionPlanBuilder.Build</c>.
/// </summary>
internal static class EnumConversions
{
    private static readonly System.Reflection.ConstructorInfo AtlasMappingExceptionCtor =
        typeof(AtlasMappingException).GetConstructor(new[] { typeof(string) })!;

    private static readonly EnumMapConfig DefaultEnumConfig = new();

    private static readonly System.Reflection.MethodInfo EnumGetNameMethod =
        typeof(Enum).GetMethod(nameof(Enum.GetName), new[] { typeof(Type), typeof(object) })!;

    private static readonly System.Reflection.MethodInfo DictionaryTryGetValueMethod =
        typeof(Dictionary<string, object>).GetMethod(
            nameof(Dictionary<string, object>.TryGetValue),
            new[] { typeof(string), typeof(object).MakeByRefType() })!;

    private static readonly System.Reflection.MethodInfo StringConcat3Method =
        typeof(string).GetMethod(nameof(string.Concat), new[] { typeof(string), typeof(string), typeof(string) })!;

    public static bool HasImplicitConversion(Type srcType, Type dstType)
    {
        var srcCore = Nullable.GetUnderlyingType(srcType) ?? srcType;
        var dstCore = Nullable.GetUnderlyingType(dstType) ?? dstType;

        if (srcCore.IsEnum && dstCore.IsEnum) return true;
        if (srcCore.IsEnum && dstCore == typeof(string)) return true;
        if (srcCore == typeof(string) && dstCore.IsEnum) return true;
        if (srcCore.IsEnum && dstCore == Enum.GetUnderlyingType(srcCore)) return true;
        if (dstCore.IsEnum && srcCore == Enum.GetUnderlyingType(dstCore)) return true;
        return false;
    }

    public static Expression BuildConversion(
        Expression srcExpr,
        Type dstType,
        StringToEnumCache cache)
    {
        var srcType = srcExpr.Type;
        var srcCore = Nullable.GetUnderlyingType(srcType) ?? srcType;
        var dstCore = Nullable.GetUnderlyingType(dstType) ?? dstType;

        // Both enums (possibly with nullable wrapping) — build a switch like BuildEnumLambda's body.
        if (srcCore.IsEnum && dstCore.IsEnum)
            return BuildEnumToEnum(srcExpr, srcCore, dstType, dstCore);

        if (srcCore.IsEnum && dstCore == typeof(string))
            return BuildEnumToString(srcExpr, srcCore);

        if (srcCore == typeof(string) && dstCore.IsEnum)
            return BuildStringToEnum(srcExpr, dstCore, cache);

        // Underlying-numeric conversions — straight cast.
        return Expression.Convert(srcExpr, dstType);
    }

    private static Expression BuildEnumToEnum(Expression srcExpr, Type srcEnum, Type dstFullType, Type dstEnum)
    {
        // Build a switch expression for all defined source values, default ByValue, no overrides.
        var cfg = DefaultEnumConfig;   // defaults: ByValue, no overrides

        var srcParam = Expression.Parameter(srcEnum, "_src");
        var cases = new List<SwitchCase>();
        foreach (var definedSrc in Enum.GetValues(srcEnum))
        {
            var action = EnumResolver.Resolve(definedSrc, cfg, srcEnum, dstEnum);
            Expression caseBody = action.Kind switch
            {
                EnumResolver.ActionKind.Hit =>
                    Expression.Constant(action.DestValue, dstEnum),
                EnumResolver.ActionKind.Throw =>
                    Expression.Throw(
                        Expression.New(
                            AtlasMappingExceptionCtor,
                            Expression.Constant(action.Reason)),
                        dstEnum),
                _ => throw new InvalidOperationException("Unreachable"),
            };
            cases.Add(Expression.SwitchCase(caseBody, Expression.Constant(definedSrc, srcEnum)));
        }
        var defaultBody = Expression.Throw(
            Expression.New(
                AtlasMappingExceptionCtor,
                Expression.Constant($"Source value is not defined on {srcEnum.Name}.")),
            dstEnum);

        var switchExpr = Expression.Switch(srcParam, defaultBody, cases.ToArray());
        var lambda = Expression.Lambda(switchExpr, srcParam);

        // Handle nullable source: if src.HasValue, convert; else return default(dstFullType).
        var srcIsNullable = Nullable.GetUnderlyingType(srcExpr.Type) is not null;
        if (srcIsNullable)
        {
            var hasValue = Expression.Property(srcExpr, "HasValue");
            var srcValue = Expression.Property(srcExpr, "Value");   // typed as srcEnum
            var converted = Expression.Invoke(lambda, srcValue);
            // Wrap in Nullable<dstEnum> if dstFullType is nullable
            var convertedWrapped = Nullable.GetUnderlyingType(dstFullType) is not null
                ? (Expression)Expression.Convert(converted, dstFullType)
                : converted;
            return Expression.Condition(hasValue, convertedWrapped, Expression.Default(dstFullType));
        }

        var invoked = Expression.Invoke(lambda, srcExpr);
        if (Nullable.GetUnderlyingType(dstFullType) is not null)
            return Expression.Convert(invoked, dstFullType);
        return invoked;
    }

    private static Expression BuildEnumToString(Expression srcExpr, Type srcEnum)
    {
        if (Nullable.GetUnderlyingType(srcExpr.Type) is not null)
        {
            // src.HasValue ? Enum.GetName(srcEnum, src.Value) : null
            var hasValue = Expression.Property(srcExpr, "HasValue");
            var srcValue = Expression.Property(srcExpr, "Value");   // typed as srcEnum
            var getNameCall = Expression.Call(
                EnumGetNameMethod,
                Expression.Constant(srcEnum),
                Expression.Convert(srcValue, typeof(object)));
            return Expression.Condition(hasValue, getNameCall, Expression.Constant(null, typeof(string)));
        }

        return Expression.Call(EnumGetNameMethod, Expression.Constant(srcEnum), Expression.Convert(srcExpr, typeof(object)));
    }

    private static Expression BuildStringToEnum(Expression srcExpr, Type dstEnum, StringToEnumCache cache)
    {
        var dict = cache.GetOrCreateForType(dstEnum);
        var dictConst = Expression.Constant(dict, typeof(Dictionary<string, object>));

        var outVar = Expression.Variable(typeof(object), "v");

        var mismatchThrow = Expression.Throw(
            Expression.New(
                AtlasMappingExceptionCtor,
                Expression.Call(
                    StringConcat3Method,
                    Expression.Constant("String value '"),
                    srcExpr,
                    Expression.Constant($"' does not match any defined name of {dstEnum.Name}."))),
            dstEnum);

        var nullThrow = Expression.Throw(
            Expression.New(
                AtlasMappingExceptionCtor,
                Expression.Constant($"Cannot map null string to enum type {dstEnum.Name}.")),
            dstEnum);

        // Block: null guard → TryGetValue lookup → cast or throw
        return Expression.Block(
            dstEnum,
            new[] { outVar },
            Expression.IfThen(
                Expression.ReferenceEqual(srcExpr, Expression.Constant(null, typeof(string))),
                nullThrow),
            Expression.Condition(
                Expression.Call(dictConst, DictionaryTryGetValueMethod, srcExpr, outVar),
                Expression.Convert(outVar, dstEnum),
                mismatchThrow));
    }
}
