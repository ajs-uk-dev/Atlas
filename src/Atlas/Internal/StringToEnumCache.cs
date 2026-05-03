namespace Atlas.Internal;

/// <summary>
/// Per-MapperConfiguration cache of <c>(dstEnumType) → Dictionary&lt;string, dstEnumValue&gt;</c>
/// for the auto-conversion <c>string → enum</c> path. Built on demand.
/// </summary>
internal sealed class StringToEnumCache
{
    private readonly Dictionary<Type, Dictionary<string, object>> _maps = new();
    private readonly System.Threading.Lock _lock = new();

    public Dictionary<string, object> GetOrCreateForType(Type dstEnumType)
    {
        lock (_lock)
        {
            if (_maps.TryGetValue(dstEnumType, out var existing)) return existing;
            var built = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var v in Enum.GetValues(dstEnumType))
            {
                var name = Enum.GetName(dstEnumType, v);
                if (name is not null) built[name] = v;
            }
            _maps[dstEnumType] = built;
            return built;
        }
    }
}
