using System.Linq.Expressions;

namespace Atlas;

/// <summary>
/// A registry of value transformers keyed by destination type. Used at global
/// (<c>MapperConfigurationExpression.ValueTransformers</c>) and profile
/// (<c>MapperProfile.ValueTransformers</c>) scopes.
/// </summary>
/// <remarks>
/// Type matching is exact: <c>Add&lt;string&gt;</c> matches destination properties of type
/// <c>string</c> (and <c>string?</c>, which is the same runtime type after nullable
/// reference type erasure). It does NOT match <c>object</c> or other assignable types.
/// For value types, <c>Add&lt;int&gt;</c> matches <c>int</c> only — register
/// <c>Add&lt;int?&gt;</c> separately if needed for nullable int destinations.
/// <para>
/// Multiple <c>Add&lt;T&gt;</c> calls for the same type append in
/// FIFO order. When composed with other scopes (broad-first: global → profile → type-map),
/// the entire FIFO list of this scope appears in order in the effective pipeline.
/// </para>
/// </remarks>
public sealed class ValueTransformerCollection
{
    private readonly Dictionary<Type, List<LambdaExpression>> _byType = new();

    /// <summary>
    /// Registers a transformer for destination type <typeparamref name="T"/>. Multiple
    /// calls for the same <typeparamref name="T"/> append in FIFO order.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// Thrown if <paramref name="transformer"/> is null.
    /// </exception>
    public ValueTransformerCollection Add<T>(Expression<Func<T, T>> transformer)
    {
        ArgumentNullException.ThrowIfNull(transformer);
        var key = typeof(T);
        if (!_byType.TryGetValue(key, out var list))
        {
            list = new List<LambdaExpression>();
            _byType[key] = list;
        }
        list.Add(transformer);
        return this;
    }

    /// <summary>
    /// Internal accessor used by <c>TransformerResolver</c> at config-build time.
    /// Exposes the underlying per-type lists as read-only views.
    /// </summary>
    internal IReadOnlyDictionary<Type, IReadOnlyList<LambdaExpression>> AllTransformers
    {
        get
        {
            return _byType.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<LambdaExpression>)kv.Value);
        }
    }
}
