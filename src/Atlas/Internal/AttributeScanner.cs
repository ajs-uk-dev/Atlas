using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Atlas.Internal;

/// <summary>
/// Discovers <see cref="MapAttribute"/>-decorated types in scanned assemblies and registers
/// each into the configuration via the same fluent calls a hand-written profile would make.
/// See <c>docs/Atlas-Design-AttributeConfig.md</c> §4.1 / §5.
/// </summary>
internal static class AttributeScanner
{
    private static readonly MethodInfo CreateMapOpenMethodInfo =
        typeof(MapperConfigurationExpression)
            .GetMethods()
            .Single(m => m.Name == nameof(MapperConfigurationExpression.CreateMap)
                      && m.IsGenericMethodDefinition
                      && m.GetParameters().Length == 1
                      && m.GetParameters()[0].ParameterType == typeof(MemberList));

    private const string IgnoreMethodName =
        nameof(Atlas.Configuration.IMemberConfigurationExpression<object, object, object>.Ignore);

    private const string MapFromMethodName =
        nameof(Atlas.Configuration.IMemberConfigurationExpression<object, object, object>.MapFrom);

    private const string NullSubstituteMethodName =
        nameof(Atlas.Configuration.IMemberConfigurationExpression<object, object, object>.NullSubstitute);

    /// <summary>
    /// Top-level entry point. Enumerates public top-level non-abstract decorated types and
    /// processes each. Errors are accumulated; a fatal duplicate-pair throws immediately.
    /// </summary>
    public static void Discover(Assembly assembly, MapperConfigurationExpression cfg)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(cfg);

        var errors = new List<ConfigurationError>();
        foreach (var type in assembly.GetTypes())
        {
            if (!IsAttributeMapCandidate(type))
                continue;

            ProcessMapType(type, cfg, errors);
        }

        if (errors.Count > 0)
            throw new AtlasConfigurationException(errors);
    }

    /// <summary>
    /// True when <paramref name="t"/> is a top-level public non-abstract non-interface
    /// non-nested non-enum class decorated with <see cref="MapAttribute"/>.
    /// Static classes (encoded as <c>IsAbstract &amp;&amp; IsSealed</c>) are excluded.
    /// </summary>
    public static bool IsAttributeMapCandidate(Type t)
    {
        return t.IsClass
            && t.IsPublic
            && !t.IsAbstract
            && !t.IsNested
            && t.GetCustomAttribute<MapAttribute>(inherit: false) is not null;
    }

    /// <summary>
    /// Translates one [Map]-decorated type into fluent calls. Validation (Task 3) is
    /// applied first; invalid registrations accumulate errors and return early.
    /// CreateMap + member attribute application + class-level flags land in Tasks 5/6/7/8.
    /// </summary>
    private static void ProcessMapType(Type decoratedType, MapperConfigurationExpression cfg, List<ConfigurationError> errors)
    {
        var attr = decoratedType.GetCustomAttribute<MapAttribute>(inherit: false)!;
        if (!ValidateMapTarget(decoratedType, attr, errors))
            return;

        var srcType = attr.SourceType;
        var attributeOrigin = $"[Map(typeof({srcType.Name}))] on {decoratedType.Name}";

        // Check for duplicate before calling CreateMap so the conflict error names the
        // attribute as the incoming (new) registration origin — not a synthesised CreateMap<> call.
        // Accumulate (not immediate throw) so all other types in the scan complete first.
        var existing = cfg.GetTypeMaps()
            .FirstOrDefault(t => t.SourceType == srcType && t.DestinationType == decoratedType);
        if (existing is not null)
        {
            errors.Add(new(srcType, decoratedType, "(register)",
                $"Type pair ({srcType.Name}, {decoratedType.Name}) is registered twice: " +
                $"{existing.RegistrationOrigin} and {attributeOrigin}. " +
                $"Pick one — every (TSource, TDestination) pair must have a single registration."));
            return;
        }

        var mappingExpression = InvokeCreateMap(cfg, srcType, decoratedType, attr.MemberList);
        SetRegistrationOrigin(cfg, srcType, decoratedType, attributeOrigin);

        ApplyMemberAttributes(mappingExpression, srcType, decoratedType, errors);

        ApplyClassLevelFlags(mappingExpression, srcType, decoratedType, attr);
    }

    private static object InvokeCreateMap(MapperConfigurationExpression cfg, Type srcType, Type dstType, MemberList memberList)
    {
        var createMapClosed = CreateMapOpenMethodInfo.MakeGenericMethod(srcType, dstType);
        return InvokeAndUnwrap(createMapClosed, cfg, [memberList])!;
    }

    /// <summary>
    /// Invokes <paramref name="method"/> via reflection and unwraps any
    /// <see cref="TargetInvocationException"/> that wraps an <see cref="AtlasConfigurationException"/>,
    /// re-throwing the inner exception with its original stack trace preserved.
    /// All four reflection-invoke sites in this class route through this helper so that
    /// callers always see a clean <see cref="AtlasConfigurationException"/> rather than a
    /// <see cref="TargetInvocationException"/> wrapper.
    /// </summary>
    private static object? InvokeAndUnwrap(MethodInfo method, object target, object?[]? args)
    {
        try
        {
            return method.Invoke(target, args);
        }
        catch (TargetInvocationException tie) when (tie.InnerException is AtlasConfigurationException acex)
        {
            ExceptionDispatchInfo.Capture(acex).Throw();
            throw; // unreachable
        }
    }

    /// <summary>
    /// Sets <see cref="TypeMap.RegistrationOrigin"/> on the just-created TypeMap so error messages
    /// for duplicate-pair conflicts cite the attribute source rather than a synthesized fluent call.
    /// </summary>
    private static void SetRegistrationOrigin(MapperConfigurationExpression cfg, Type srcType, Type dstType, string origin)
    {
        var tm = cfg.GetTypeMaps().FirstOrDefault(t => t.SourceType == srcType && t.DestinationType == dstType);
        if (tm is not null)
        {
            tm.RegistrationOrigin = origin;
        }
    }

    /// <summary>
    /// Iterates destination properties and applies [Skip] / [From] / [DefaultWhenNull]
    /// per-property via reflection-built ForMember invocations. See design §5.4.
    /// Task 6: handles [Skip] only. [From] lands in Task 7; [DefaultWhenNull] in Task 8.
    /// </summary>
    private static void ApplyMemberAttributes(object mappingExpression, Type srcType, Type dstType, List<ConfigurationError> errors)
    {
        var imappingExprClosed = typeof(Atlas.Configuration.IMappingExpression<,>).MakeGenericType(srcType, dstType);
        var forMemberOpen = imappingExprClosed.GetMethods()
            .Single(m => m.Name == nameof(Atlas.Configuration.IMappingExpression<object, object>.ForMember)
                      && m.IsGenericMethodDefinition);

        foreach (var prop in dstType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var ignore = prop.GetCustomAttribute<SkipAttribute>(inherit: false);
            var sourceMember = prop.GetCustomAttribute<FromAttribute>(inherit: false);
            var nullSubstitute = prop.GetCustomAttribute<DefaultWhenNullAttribute>(inherit: false);

            if (ignore is null && sourceMember is null && nullSubstitute is null)
                continue;

            var memberType = prop.PropertyType;
            var imemberConfigClosed = typeof(Atlas.Configuration.IMemberConfigurationExpression<,,>)
                .MakeGenericType(srcType, dstType, memberType);
            var optParam = Expression.Parameter(imemberConfigClosed, "opt");

            var statements = new List<Expression>();

            if (ignore is not null)
            {
                // [Skip] short-circuits — emit only Ignore() and ignore other attributes on this property.
                var ignoreMethod = imemberConfigClosed.GetMethod(IgnoreMethodName, Type.EmptyTypes)!;
                statements.Add(Expression.Call(optParam, ignoreMethod));
            }
            else
            {
                Type? sourceMemberType = null;
                if (sourceMember is not null)
                {
                    var sourceLambda = BuildSourcePathExpression(srcType, sourceMember.MemberName,
                        prop.Name, dstType, errors, out sourceMemberType);

                    if (sourceLambda is not null && sourceMemberType is not null)
                    {
                        var mapFromOpen = imemberConfigClosed.GetMethods()
                            .Single(m => m.Name == MapFromMethodName
                                      && m.IsGenericMethodDefinition
                                      && m.GetParameters().Length == 1
                                      && m.GetParameters()[0].ParameterType.IsGenericType
                                      && m.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == typeof(Expression<>));
                        var mapFromClosed = mapFromOpen.MakeGenericMethod(sourceMemberType);
                        statements.Add(Expression.Call(optParam, mapFromClosed,
                            Expression.Constant(sourceLambda, sourceLambda.GetType())));
                    }
                }

                if (nullSubstitute is not null)
                {
                    // Resolve TSourceMember: use SourceMember leaf if present, else convention-resolve.
                    Type? resolvedSourceType = sourceMemberType;
                    LambdaExpression? conventionSourceLambda = null;
                    if (resolvedSourceType is null)
                    {
                        conventionSourceLambda = BuildConventionSourcePathExpression(srcType, prop);
                        resolvedSourceType = conventionSourceLambda?.ReturnType;
                    }

                    if (resolvedSourceType is not null
                        && ValidateNullSubstituteCompatibility(resolvedSourceType, nullSubstitute.ConstantValue,
                                                               srcType, dstType, prop.Name, errors))
                    {
                        // If no [From] was present, emit MapFrom with the convention-resolved path
                        // so the property map has a source expression. Without this, the convention engine
                        // would skip the property (already in existingNames) leaving SourcePath = null,
                        // and ExecutionPlanBuilder would skip the assignment entirely.
                        if (conventionSourceLambda is not null)
                        {
                            var mapFromOpen = imemberConfigClosed.GetMethods()
                                .Single(m => m.Name == MapFromMethodName
                                          && m.IsGenericMethodDefinition
                                          && m.GetParameters().Length == 1
                                          && m.GetParameters()[0].ParameterType.IsGenericType
                                          && m.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == typeof(Expression<>));
                            var mapFromClosed = mapFromOpen.MakeGenericMethod(resolvedSourceType);
                            statements.Add(Expression.Call(optParam, mapFromClosed,
                                Expression.Constant(conventionSourceLambda, conventionSourceLambda.GetType())));
                        }

                        var constantOverloadOpen = imemberConfigClosed.GetMethods()
                            .Single(m => m.Name == NullSubstituteMethodName
                                      && m.IsGenericMethodDefinition
                                      && m.GetParameters().Length == 1
                                      && !m.GetParameters()[0].ParameterType.IsGenericType);
                        var constantOverloadClosed = constantOverloadOpen.MakeGenericMethod(resolvedSourceType);

                        // Convert the boxed attribute constant to the resolved source type for type-correct emit.
                        var convertedConstant = ConvertAttributeConstant(nullSubstitute.ConstantValue, resolvedSourceType);
                        statements.Add(Expression.Call(optParam, constantOverloadClosed,
                            Expression.Constant(convertedConstant, resolvedSourceType)));
                    }
                }
            }

            if (statements.Count == 0)
                continue;

            var body = statements.Count == 1 ? statements[0] : (Expression)Expression.Block(statements);
            var actionType = typeof(Action<>).MakeGenericType(imemberConfigClosed);
            var optionsCallback = Expression.Lambda(actionType, body, optParam).Compile();

            // Build d => d.X selector
            var dstParam = Expression.Parameter(dstType, "d");
            var memberAccess = Expression.Property(dstParam, prop);
            var funcType = typeof(Func<,>).MakeGenericType(dstType, memberType);
            var selector = Expression.Lambda(funcType, memberAccess, dstParam);

            var forMemberClosed = forMemberOpen.MakeGenericMethod(memberType);
            InvokeAndUnwrap(forMemberClosed, mappingExpression, [selector, optionsCallback]);
        }
    }

    /// <summary>
    /// Builds a <c>Func&lt;TSrc, TMember&gt;</c> lambda for the convention-matched source member
    /// (simple name-equality, no path walking). Returns <c>null</c> if no member is found.
    /// </summary>
    private static LambdaExpression? BuildConventionSourcePathExpression(Type srcType, PropertyInfo destProp)
    {
        var srcParam = Expression.Parameter(srcType, "s");

        var srcProp = srcType.GetProperty(destProp.Name, BindingFlags.Public | BindingFlags.Instance);
        if (srcProp is not null && srcProp.CanRead)
        {
            var memberAccess = Expression.Property(srcParam, srcProp);
            var funcType = typeof(Func<,>).MakeGenericType(srcType, srcProp.PropertyType);
            return Expression.Lambda(funcType, memberAccess, srcParam);
        }

        var srcField = srcType.GetField(destProp.Name, BindingFlags.Public | BindingFlags.Instance);
        if (srcField is not null)
        {
            var memberAccess = Expression.Field(srcParam, srcField);
            var funcType = typeof(Func<,>).MakeGenericType(srcType, srcField.FieldType);
            return Expression.Lambda(funcType, memberAccess, srcParam);
        }

        return null;
    }

    /// <summary>
    /// Eager validator for [DefaultWhenNull] per design §6 rules 5 &amp; 6. Returns true
    /// if the substitute is compatible; appends a structured error and returns false
    /// otherwise.
    /// </summary>
    private static bool ValidateNullSubstituteCompatibility(
        Type sourceMemberType, object constantValue,
        Type srcType, Type dstType, string destMemberName,
        List<ConfigurationError> errors)
    {
        // Rule 6: source must be reference type or Nullable<T>.
        var underlying = Nullable.GetUnderlyingType(sourceMemberType);
        var isReferenceType = !sourceMemberType.IsValueType;
        var isNullable = underlying is not null;

        if (!isReferenceType && !isNullable)
        {
            errors.Add(new(srcType, dstType, destMemberName,
                $"[DefaultWhenNull({FormatConstant(constantValue)})] on '{dstType.Name}.{destMemberName}' — " +
                $"source-member type '{sourceMemberType.Name}' is non-nullable; the substitute is unreachable. " +
                $"Use a different default mechanism or remove the attribute."));
            return false;
        }

        // Rule 5: substitute must be assignable to source-member type (or its underlying type for Nullable<T>).
        var targetType = underlying ?? sourceMemberType;
        var constantType = constantValue.GetType();

        if (!targetType.IsAssignableFrom(constantType))
        {
            // Allow numeric coercion (matches existing fluent NullSubstitute behavior).
            if (!IsNumericallyCoercible(constantType, targetType))
            {
                errors.Add(new(srcType, dstType, destMemberName,
                    $"[DefaultWhenNull({FormatConstant(constantValue)})] on '{dstType.Name}.{destMemberName}' — " +
                    $"substitute type '{constantType.Name}' is not assignable to source-member type " +
                    $"'{(isNullable ? $"Nullable<{targetType.Name}>" : targetType.Name)}'."));
                return false;
            }
        }

        return true;
    }

    private static bool IsNumericallyCoercible(Type from, Type to)
    {
        return Atlas.Internal.NumericConversions.HasImplicitConversion(from, to);
    }

    private static object ConvertAttributeConstant(object value, Type targetType)
    {
        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (underlying.IsAssignableFrom(value.GetType())) return value;
        return Convert.ChangeType(value, underlying);
    }

    private static string FormatConstant(object? value)
    {
        return value switch
        {
            string s => $"\"{s}\"",
            char c => $"'{c}'",
            null => "null",
            _ => value.ToString() ?? "?"
        };
    }

    private static void ApplyClassLevelFlags(object mappingExpression, Type srcType, Type dstType, MapAttribute attr)
    {
        var imappingExprClosed = typeof(Atlas.Configuration.IMappingExpression<,>).MakeGenericType(srcType, dstType);

        if (attr.PreserveReferences)
        {
            var method = imappingExprClosed.GetMethod(
                nameof(Atlas.Configuration.IMappingExpression<object, object>.PreserveReferences),
                Type.EmptyTypes)!;
            InvokeAndUnwrap(method, mappingExpression, null);
        }

        if (attr.ReverseMap)
        {
            var method = imappingExprClosed.GetMethod(
                nameof(Atlas.Configuration.IMappingExpression<object, object>.ReverseMap),
                [typeof(MemberList)])!;
            InvokeAndUnwrap(method, mappingExpression, [MemberList.None]);
        }
    }

    /// <summary>
    /// Validates the [Map] decorated type and its source type against §6 rules 1-3.
    /// Adds a <see cref="ConfigurationError"/> for each violation and returns false if any
    /// violation is found. Returns true when the pair is valid.
    /// </summary>
    private static bool ValidateMapTarget(Type decoratedType, MapAttribute attr, List<ConfigurationError> errors)
    {
        var srcType = attr.SourceType;
        var dstType = decoratedType;

        // Rule: dest is open-generic
        if (dstType.IsGenericTypeDefinition || dstType.ContainsGenericParameters)
        {
            errors.Add(new(srcType, dstType, "(register)",
                $"[Map] applied to open-generic type '{FormatTypeName(dstType)}'. " +
                $"Use cfg.CreateMap(typeof(Source<>), typeof(Dest<>)) for open-generic registrations."));
            return false;
        }

        // Rule: dest is enum (defense in depth; AttributeUsage(Class) usually catches at compile)
        if (dstType.IsEnum)
        {
            errors.Add(new(srcType, dstType, "(register)",
                $"[Map] applied to enum '{dstType.Name}'. Use cfg.CreateMap<TSrcEnum, {dstType.Name}>().MapByName() (or similar) for enum-to-enum mappings."));
            return false;
        }

        // Rule: dest is interface (filter usually catches; defense in depth)
        if (dstType.IsInterface)
        {
            errors.Add(new(srcType, dstType, "(register)",
                $"[Map] applied to interface '{dstType.Name}'. Atlas cannot instantiate interfaces; use a concrete destination type."));
            return false;
        }

        // Rule: dest is static (encoded as IsAbstract && IsSealed in CLR)
        if (dstType is { IsAbstract: true, IsSealed: true })
        {
            errors.Add(new(srcType, dstType, "(register)",
                $"[Map] applied to static type '{dstType.Name}'. Static types cannot be mapping destinations."));
            return false;
        }

        // Rule: dest is abstract (filter usually catches; defense in depth — IsAbstract without IsSealed)
        if (dstType.IsAbstract)
        {
            errors.Add(new(srcType, dstType, "(register)",
                $"[Map] applied to abstract type '{dstType.Name}'. Atlas cannot instantiate abstract destinations."));
            return false;
        }

        // Rule: source is open-generic
        if (srcType.IsGenericTypeDefinition)
        {
            errors.Add(new(srcType, dstType, "(register)",
                $"[Map] on '{dstType.Name}' specifies open-generic source type '{FormatTypeName(srcType)}'. " +
                $"Open generics use cfg.CreateMap(typeof(Source<>), typeof(Dest<>)) — " +
                $"attribute syntax is not supported for open generics."));
            return false;
        }

        // Rule: source is a recognized dynamic shape
        if (DynamicShape.IsDynamicShape(srcType))
        {
            errors.Add(new(srcType, dstType, "(register)",
                $"[Map] on '{dstType.Name}' specifies a recognized dynamic shape ('{FormatTypeName(srcType)}'). " +
                $"Dynamic mapping is convention-only and requires no registration — remove the attribute and call mapper.Map<{dstType.Name}>(dictInstance) directly. " +
                $"To explicitly register a non-dynamic mapping for this pair, use cfg.CreateMap<{FormatTypeName(srcType)}, {dstType.Name}>() in a profile."));
            return false;
        }

        return true;
    }

    private static string FormatTypeName(Type t)
    {
        var keyword = CSharpKeywordFor(t);
        if (keyword is not null) return keyword;
        if (!t.IsGenericType) return t.Name;
        var name = t.Name;
        var tickIdx = name.IndexOf('`');
        if (tickIdx >= 0) name = name[..tickIdx];
        if (t.IsGenericTypeDefinition) return $"{name}<>";
        var args = string.Join(", ", t.GetGenericArguments().Select(FormatTypeName));
        return $"{name}<{args}>";
    }

    private static string? CSharpKeywordFor(Type t)
    {
        if (t == typeof(string)) return "string";
        if (t == typeof(object)) return "object";
        if (t == typeof(bool)) return "bool";
        if (t == typeof(byte)) return "byte";
        if (t == typeof(sbyte)) return "sbyte";
        if (t == typeof(short)) return "short";
        if (t == typeof(ushort)) return "ushort";
        if (t == typeof(int)) return "int";
        if (t == typeof(uint)) return "uint";
        if (t == typeof(long)) return "long";
        if (t == typeof(ulong)) return "ulong";
        if (t == typeof(float)) return "float";
        if (t == typeof(double)) return "double";
        if (t == typeof(decimal)) return "decimal";
        if (t == typeof(char)) return "char";
        return null;
    }

    /// <summary>
    /// Walks <paramref name="dottedPath"/> against <paramref name="srcType"/>, building a
    /// chained MemberExpression. Each segment must resolve to a public readable property or
    /// a public field. Errors are appended to <paramref name="errors"/> and the method returns
    /// <c>null</c> on failure.
    /// </summary>
    internal static LambdaExpression? BuildSourcePathExpression(
        Type srcType, string dottedPath, string destMemberName, Type decoratedType,
        List<ConfigurationError> errors, out Type? leafType)
    {
        leafType = null;
        var segments = dottedPath.Split('.');
        var srcParam = Expression.Parameter(srcType, "s");
        Expression current = srcParam;
        Type currentType = srcType;

        for (int i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];

            var prop = currentType.GetProperty(segment, BindingFlags.Public | BindingFlags.Instance);
            FieldInfo? field = null;
            if (prop is null)
                field = currentType.GetField(segment, BindingFlags.Public | BindingFlags.Instance);

            if (prop is null && field is null)
            {
                errors.Add(new(srcType, decoratedType, destMemberName,
                    $"[From(\"{dottedPath}\")] on '{decoratedType.Name}.{destMemberName}' — " +
                    $"segment '{segment}' not found on '{currentType.Name}'."));
                return null;
            }

            if (prop is { CanRead: false })
            {
                errors.Add(new(srcType, decoratedType, destMemberName,
                    $"[From(\"{dottedPath}\")] on '{decoratedType.Name}.{destMemberName}' — " +
                    $"segment '{segment}' on '{currentType.Name}' has no public getter."));
                return null;
            }

            if (prop is not null)
            {
                current = Expression.Property(current, prop);
                currentType = prop.PropertyType;
            }
            else
            {
                current = Expression.Field(current, field!);
                currentType = field!.FieldType;
            }
        }

        leafType = currentType;
        var funcType = typeof(Func<,>).MakeGenericType(srcType, leafType);
        return Expression.Lambda(funcType, current, srcParam);
    }

    // Test-only entry point (visible via InternalsVisibleTo to Atlas.Tests)
    internal static LambdaExpression? BuildSourcePathExpressionForTest(
        Type srcType, string dottedPath, string destMemberName, Type decoratedType,
        List<ConfigurationError> errors, out Type? leafType) =>
        BuildSourcePathExpression(srcType, dottedPath, destMemberName, decoratedType, errors, out leafType);
}
