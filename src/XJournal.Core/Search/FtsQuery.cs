using System.Text;
using System.Text.RegularExpressions;

namespace XJournal.Core.Search;

/// <summary>
/// Turns free text typed by the user into a safe FTS5 query: every word must match (as a prefix),
/// and "quoted phrases" must match exactly. FTS5 operators typed by the user are treated as text.
/// </summary>
public static partial class FtsQuery
{
    [GeneratedRegex("\"([^\"]*)\"|(\\S+)")]
    private static partial Regex TokenRegex();

    /// <returns>The FTS5 MATCH expression, or null if the text contains nothing searchable.</returns>
    public static string? Build(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = new List<string>();
        foreach (Match match in TokenRegex().Matches(text))
        {
            var isPhrase = match.Groups[1].Success;
            var token = (isPhrase ? match.Groups[1].Value : match.Groups[2].Value).Trim();
            if (!token.Any(char.IsLetterOrDigit)) continue;
            var quoted = "\"" + token.Replace("\"", "\"\"") + "\"";
            parts.Add(isPhrase ? quoted : quoted + "*");
        }
        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    /// <summary>A plain preview of the start of a body, used when there is no text query.</summary>
    public static string Preview(string summary, string body, int maxLength = 180)
    {
        var source = string.IsNullOrWhiteSpace(body) ? summary : body;
        var sb = new StringBuilder(maxLength + 1);
        var lastWasSpace = false;
        foreach (var c in source)
        {
            var isSpace = char.IsWhiteSpace(c);
            if (isSpace && (lastWasSpace || sb.Length == 0)) continue;
            sb.Append(isSpace ? ' ' : c);
            lastWasSpace = isSpace;
            if (sb.Length >= maxLength) { sb.Append('…'); break; }
        }
        return sb.ToString().TrimEnd();
    }
}
