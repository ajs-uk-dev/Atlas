using System.Reflection;

namespace Atlas.Internal;

/// <summary>
/// A resolved source-side member walk: a single direct member, or a chain (flattening).
/// </summary>
internal sealed record SourceMemberPath(IReadOnlyList<PropertyInfo> Members);
