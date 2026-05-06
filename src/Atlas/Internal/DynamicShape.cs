using System.Collections.Generic;
using System.Dynamic;

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
}
