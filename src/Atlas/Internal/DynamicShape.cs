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
    /// True iff exactly one side of the pair is a recognized dynamic shape (XOR).
    /// Self-pairs (both dynamic) and non-pairs (neither dynamic) return false.
    /// </summary>
    internal static bool IsDynamicPair(TypePair pair) =>
        IsDynamicShape(pair.Source) ^ IsDynamicShape(pair.Destination);

    /// <summary>
    /// Materializes a dynamic TypeMap on demand. Called by MapperRegistry.GetTypeMap when the
    /// closed-pair cache and open-generic template scan both miss and IsDynamicPair returns true.
    /// Synthesizes one PropertyMap per public writable POCO member (dict→POCO direction) or
    /// one per public readable POCO member (POCO→dict direction).
    /// </summary>
    internal static TypeMap MaterializeTypeMap(
        TypePair pair,
        ValueTransformerCollection globalTransformers,
        ConventionOptions conventions)
    {
        if (IsDynamicShape(pair.Source))
            return BuildDictToPocoTypeMap(pair, globalTransformers, conventions);
        else
            return BuildPocoToDictTypeMap(pair, globalTransformers, conventions);
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
