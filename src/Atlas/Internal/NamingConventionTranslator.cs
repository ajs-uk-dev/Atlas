using System.Text;

namespace Atlas.Internal;

/// <summary>
/// Pure string translation between source/destination naming conventions and the canonical
/// PascalCase form used internally by the convention engine.
/// </summary>
internal static class NamingConventionTranslator
{
    public static string ToCanonical(string memberName, NamingConvention convention) =>
        convention switch
        {
            NamingConvention.PascalCase => memberName,
            NamingConvention.CamelCase => CamelToPascal(memberName),
            NamingConvention.SnakeCase => SnakeToPascal(memberName),
            _ => memberName,
        };

    private static string CamelToPascal(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return char.IsUpper(s[0]) ? s : char.ToUpperInvariant(s[0]) + s[1..];
    }

    private static string SnakeToPascal(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new StringBuilder(s.Length);
        bool capitalizeNext = true;
        foreach (var ch in s)
        {
            if (ch == '_') { capitalizeNext = true; continue; }
            sb.Append(capitalizeNext ? char.ToUpperInvariant(ch) : ch);
            capitalizeNext = false;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Split a PascalCase identifier into its capitalized segments, e.g. "CustomerAddressCity" -> ["Customer", "Address", "City"].
    /// </summary>
    public static IReadOnlyList<string> SplitPascal(string pascal)
    {
        if (string.IsNullOrEmpty(pascal)) return [];
        var segments = new List<string>();
        var sb = new StringBuilder();
        for (int i = 0; i < pascal.Length; i++)
        {
            var ch = pascal[i];
            if (i > 0 && char.IsUpper(ch) && sb.Length > 0)
            {
                segments.Add(sb.ToString());
                sb.Clear();
            }
            sb.Append(ch);
        }
        if (sb.Length > 0) segments.Add(sb.ToString());
        return segments;
    }
}
