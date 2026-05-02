namespace Atlas.Internal;

/// <summary>
/// Identifies a (source, destination) type pair. Used as a dictionary key on the hot path
/// of mapper lookups; equality and hash code are intentionally allocation-free.
/// </summary>
internal readonly record struct TypePair(Type Source, Type Destination);
