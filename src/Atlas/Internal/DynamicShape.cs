using System.Collections.Generic;
using System.Dynamic;
using System.Reflection;

namespace Atlas.Internal;

/// <summary>
/// Gating predicates and lazy-materialization factory for Atlas v2 #10 dynamic mapping.
/// Detects when a TypePair has exactly one side as a recognized dynamic shape
/// (<see cref="IDictionary{TKey, TValue}"/> with TKey=string TValue=object,
/// <see cref="ExpandoObject"/>, or <see cref="Dictionary{TKey, TValue}"/> with TKey=string TValue=object)
/// and the other side as a POCO. See docs/Atlas-Design-DynamicMapping.md §4.3.
/// </summary>
internal static class DynamicShape
{
    private static readonly Type[] _shapes =
    {
        typeof(IDictionary<string, object>),
        typeof(ExpandoObject),
        typeof(Dictionary<string, object>),
    };

    /// <summary>True if <paramref name="t"/> is one of the three recognized dynamic shapes.</summary>
    internal static bool IsDynamicShape(Type t) => Array.IndexOf(_shapes, t) >= 0;

    /// <summary>
    /// True iff exactly one side of the pair is a recognized dynamic shape (XOR), OR
    /// both sides are collections (array / List&lt;T&gt;) where the source element type is a
    /// recognized dynamic shape and the destination element type is a POCO.
    /// Self-pairs (both dynamic) and non-pairs (neither dynamic) return false.
    /// </summary>
    internal static bool IsDynamicPair(TypePair pair)
    {
        if (IsDynamicShape(pair.Source) ^ IsDynamicShape(pair.Destination))
            return true;

        // Collection-of-dynamic-shape pairs (both directions), e.g.
        //   List<IDictionary<string,object>> → List<SimplePoco>   (dict→POCO direction)
        //   List<SimplePoco> → List<ExpandoObject>                 (POCO→dict direction)
        var srcEl = GetCollectionElementType(pair.Source);
        var dstEl = GetCollectionElementType(pair.Destination);
        if (srcEl is not null && dstEl is not null && (IsDynamicShape(srcEl) || IsDynamicShape(dstEl)))
            return true;

        return false;
    }

    /// <summary>
    /// Returns the element type if <paramref name="t"/> is a recognized collection type
    /// (T[], List&lt;T&gt;, IEnumerable&lt;T&gt;, IList&lt;T&gt;, ICollection&lt;T&gt;,
    /// IReadOnlyList&lt;T&gt;, or IReadOnlyCollection&lt;T&gt;); otherwise null.
    /// Used by both <see cref="DynamicPlanBuilder"/> and <see cref="IsDynamicPair"/>.
    /// </summary>
    internal static Type? GetCollectionElementType(Type t)
    {
        if (t.IsArray) return t.GetElementType();
        if (t.IsGenericType)
        {
            var def = t.GetGenericTypeDefinition();
            if (def == typeof(List<>) || def == typeof(IList<>) || def == typeof(IEnumerable<>)
                || def == typeof(ICollection<>) || def == typeof(IReadOnlyList<>)
                || def == typeof(IReadOnlyCollection<>))
                return t.GetGenericArguments()[0];
        }
        return null;
    }

    /// <summary>
    /// Returns true for POCO-like types: non-primitive, non-scalar, non-enum, non-dynamic,
    /// and non-collection. Used by <see cref="DynamicPlanBuilder"/> (codegen classification)
    /// and <see cref="MappingInvoker"/> (runtime recursive dict→POCO dispatch).
    /// </summary>
    internal static bool IsPocoLike(Type t)
        => !t.IsPrimitive
        && t != typeof(string)
        && t != typeof(object)
        && t != typeof(Guid)
        && t != typeof(DateTime)
        && t != typeof(DateTimeOffset)
        && t != typeof(TimeSpan)
        && t != typeof(decimal)
        && !t.IsEnum
        && !t.IsArray
        && !IsDynamicShape(t)
        && !(t.IsGenericType && (
               t.GetGenericTypeDefinition() == typeof(List<>)
            || t.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            || t.GetGenericTypeDefinition() == typeof(ICollection<>)
            || t.GetGenericTypeDefinition() == typeof(IList<>)
            || t.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)
            || t.GetGenericTypeDefinition() == typeof(IReadOnlyCollection<>)));

    /// <summary>
    /// Materializes a dynamic TypeMap on demand. Called by MapperRegistry.GetTypeMap when the
    /// closed-pair cache and open-generic template scan both miss and IsDynamicPair returns true.
    /// Synthesizes one PropertyMap per public writable POCO member (dict→POCO direction) or
    /// one per public readable POCO member (POCO→dict direction). For collection-of-dynamic
    /// pairs (e.g. List&lt;IDictionary&lt;string,object&gt;&gt; → List&lt;TPoco&gt;), returns a minimal
    /// TypeMap that the collection branch of ExecutionPlanBuilder will pick up.
    /// </summary>
    internal static TypeMap MaterializeTypeMap(
        TypePair pair,
        ValueTransformerCollection globalTransformers,
        ConventionOptions conventions)
    {
        // Collection-of-dynamic-shape (both directions): both sides are collections and at least
        // one element type is a recognized dynamic shape.
        var srcEl = GetCollectionElementType(pair.Source);
        var dstEl = GetCollectionElementType(pair.Destination);
        if (srcEl is not null && dstEl is not null && (IsDynamicShape(srcEl) || IsDynamicShape(dstEl)))
            return BuildCollectionDynamicTypeMap(pair, globalTransformers);

        if (IsDynamicShape(pair.Source))
            return BuildDictToPocoTypeMap(pair, globalTransformers, conventions);
        else
            return BuildPocoToDictTypeMap(pair, globalTransformers, conventions);
    }

    private static TypeMap BuildCollectionDynamicTypeMap(
        TypePair pair,
        ValueTransformerCollection globalTransformers)
    {
        // Produce a minimal, non-dynamic TypeMap. ExecutionPlanBuilder sees IsDynamic=false and
        // IsCollection(src) && IsCollection(dst), so it routes to BuildCollectionLambda which
        // calls InvokeToList/InvokeToArray. Each element invoke then hits the dynamic detector
        // for (srcElement, dstElement) and recurses naturally.
        var tm = new TypeMap(pair.Source, pair.Destination, MemberList.None);
        tm.IsDynamic = false;
        tm.OriginatingProfile = null;
        tm.RegistrationOrigin = "<dynamic-collection>";
        // No property maps needed — collection routing doesn't use them.
        TransformerResolver.Resolve(new[] { tm }, globalTransformers);
        tm.Seal();
        return tm;
    }

    private static TypeMap BuildDictToPocoTypeMap(
        TypePair pair,
        ValueTransformerCollection globalTransformers,
        ConventionOptions conventions)
    {
        var pocoType = pair.Destination;
        var tm = new TypeMap(pair.Source, pair.Destination, MemberList.None);
        tm.IsDynamic = true;
        tm.OriginatingProfile = null;
        tm.RegistrationOrigin = "<dynamic>";

        foreach (var prop in pocoType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanWrite) continue;
            if (prop.GetIndexParameters().Length > 0) continue;
            tm.PropertyMaps.Add(PropertyMap.ForDictKey(prop, prop.Name));
        }

        TransformerResolver.Resolve(new[] { tm }, globalTransformers);

        tm.Seal();
        return tm;
    }

    private static TypeMap BuildPocoToDictTypeMap(
        TypePair pair,
        ValueTransformerCollection globalTransformers,
        ConventionOptions conventions)
    {
        var pocoType = pair.Source;
        var tm = new TypeMap(pair.Source, pair.Destination, MemberList.None);
        tm.IsDynamic = true;
        tm.OriginatingProfile = null;
        tm.RegistrationOrigin = "<dynamic>";

        foreach (var prop in pocoType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead) continue;
            if (prop.GetIndexParameters().Length > 0) continue;
            tm.PropertyMaps.Add(PropertyMap.ForPocoSource(prop, prop.Name));
        }

        TransformerResolver.Resolve(new[] { tm }, globalTransformers);

        tm.Seal();
        return tm;
    }
}
