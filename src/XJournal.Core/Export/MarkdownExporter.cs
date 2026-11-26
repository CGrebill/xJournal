using System.Globalization;
using System.Text;
using System.Text.Json;
using XJournal.Core.Models;

namespace XJournal.Core.Export;

/// <param name="From">First local day to include (inclusive); null = from the beginning.</param>
/// <param name="To">Last local day to include (inclusive); null = up to now.</param>
/// <param name="OneFilePerEntry">A folder of files, one per entry, instead of a single document.</param>
/// <param name="IncludeCheckIns">Add each day's check-in answers (with the exact wording answered).</param>
/// <param name="Culture">Formats dates in headings; defaults to the current culture.</param>
public sealed record MarkdownOptions(
    DateOnly? From = null,
    DateOnly? To = null,
    bool OneFilePerEntry = false,
    bool IncludeCheckIns = true,
    CultureInfo? Culture = null);

/// <summary>
/// Exports entries as readable Markdown. The output is NOT encrypted.
/// </summary>
public static class MarkdownExporter
{
    // A JSON string is a valid YAML double-quoted scalar; keep non-ASCII text readable.
    private static readonly JsonSerializerOptions YamlString = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Writes the export. For a single document <paramref name="destination"/> is a file path;
    /// for one file per entry it's a folder (created if needed).
    /// </summary>
    /// <returns>How many entries were exported.</returns>
    public static int Export(Journal journal, string destination, MarkdownOptions options)
    {
        var culture = options.Culture ?? CultureInfo.CurrentCulture;
        var entries = journal.Entries.Search(new EntryQuery(null, options.From, options.To, int.MaxValue))
            .Select(r => r.Entry).Reverse().ToList();
        var checkIns = options.IncludeCheckIns ? CheckInsByEntry(journal, entries) : [];

        if (options.OneFilePerEntry)
        {
            Directory.CreateDirectory(destination);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                var name = UniqueName(FileName(entry), used);
                var text = new StringBuilder()
                    .AppendLine("---")
                    .AppendLine(CultureInfo.InvariantCulture, $"created: {Iso(entry.CreatedAt)}")
                    .AppendLine(CultureInfo.InvariantCulture, $"updated: {Iso(entry.UpdatedAt)}")
                    .AppendLine(CultureInfo.InvariantCulture, $"summary: {JsonSerializer.Serialize(entry.Summary.Trim(), YamlString)}")
                    .AppendLine("---")
                    .AppendLine();
                AppendEntry(text, entry, checkIns.GetValueOrDefault(entry.Id), culture, "#");
                File.WriteAllText(Path.Combine(destination, name), text.ToString());
            }
        }
        else
        {
            var text = new StringBuilder()
                .AppendLine(CultureInfo.InvariantCulture, $"# {journal.Username}'s journal")
                .AppendLine()
                .AppendLine(CultureInfo.InvariantCulture,
                    $"_Exported from xJournal on {journal.Clock.LocalNow().ToString("D", culture)} · {entries.Count} {(entries.Count == 1 ? "entry" : "entries")}_")
                .AppendLine();
            foreach (var entry in entries)
            {
                text.AppendLine("---").AppendLine();
                AppendEntry(text, entry, checkIns.GetValueOrDefault(entry.Id), culture, "##");
            }
            var folder = Path.GetDirectoryName(Path.GetFullPath(destination));
            if (folder is not null) Directory.CreateDirectory(folder);
            File.WriteAllText(destination, text.ToString());
        }
        return entries.Count;
    }

    private static void AppendEntry(StringBuilder text, JournalEntry entry, List<string[]>? checkIn, CultureInfo culture, string heading)
    {
        var created = entry.CreatedAt;
        text.AppendLine(CultureInfo.InvariantCulture, $"{heading} {created.ToString("dddd, MMMM d, yyyy", culture)} · {created.ToString("t", culture)}").AppendLine();
        if (!string.IsNullOrWhiteSpace(entry.Summary)) text.AppendLine(CultureInfo.InvariantCulture, $"*{entry.Summary.Trim()}*").AppendLine();
        var body = Body(entry.Body);
        if (body.Length > 0) text.AppendLine(body).AppendLine();

        if (checkIn is { Count: > 0 })
        {
            text.AppendLine("**Check-in**").AppendLine()
                .AppendLine("| Question | Answer |")
                .AppendLine("| --- | --- |");
            foreach (var row in checkIn) text.AppendLine(CultureInfo.InvariantCulture, $"| {Cell(row[0])} | {Cell(row[1])} |");
            text.AppendLine();
        }
    }

    /// <summary>Keeps the writer's line breaks: Markdown would otherwise join lines within a paragraph.</summary>
    internal static string Body(string body)
    {
        var lines = body.Replace("\r\n", "\n").Trim('\n').Split('\n');
        var output = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            output.Append(line);
            if (i == lines.Length - 1) break;
            var nextIsBlank = lines[i + 1].Trim().Length == 0;
            output.Append(line.Length > 0 && !nextIsBlank ? "  \n" : "\n");
        }
        return output.ToString();
    }

    private static string Cell(string value) => value.Replace("|", "\\|").Replace("\n", " ");

    /// <summary>Check-in rows per entry: answers linked to an entry go with it; unlinked ones go with that day's first entry.</summary>
    private static Dictionary<long, List<string[]>> CheckInsByEntry(Journal journal, List<JournalEntry> entries)
    {
        var result = new Dictionary<long, List<string[]>>();
        if (entries.Count == 0) return result;
        var ids = entries.Select(e => e.Id).ToHashSet();
        var firstOfDay = entries.GroupBy(e => journal.Clock.LocalDay(e.CreatedAt)).ToDictionary(g => g.Key, g => g.First().Id);
        var versions = journal.Questions.GetAllVersions().ToDictionary(v => v.Id);
        var order = journal.Questions.GetAll(includeArchived: true).Select((q, i) => (q.Id, i)).ToDictionary(x => x.Id, x => x.i);

        foreach (var set in journal.Answers.GetAll())
        {
            long? target = set.EntryId is { } linked && ids.Contains(linked) ? linked
                : firstOfDay.TryGetValue(set.Day, out var first) ? first : null;
            if (target is not { } id || set.Values.Count == 0) continue;
            var rows = result.TryGetValue(id, out var existing) ? existing : result[id] = [];
            foreach (var (versionId, value) in set.Values.OrderBy(kv => order.GetValueOrDefault(versions[kv.Key].QuestionId, int.MaxValue)))
            {
                var version = versions[versionId];
                var label = value >= 1 && value <= version.Scale.Count ? version.Scale[value - 1] : "";
                rows.Add([version.Text, $"{value} – {label}"]);
            }
        }
        return result;
    }

    internal static string FileName(JournalEntry entry)
    {
        var source = string.IsNullOrWhiteSpace(entry.Summary) ? entry.Body : entry.Summary;
        var slug = new StringBuilder();
        foreach (var c in source.Normalize(NormalizationForm.FormD))
        {
            if (char.IsAsciiLetterOrDigit(c)) slug.Append(char.ToLowerInvariant(c));
            else if ((char.IsWhiteSpace(c) || c is '-' or '_') && slug.Length > 0 && slug[^1] != '-') slug.Append('-');
            if (slug.Length >= 40) break;
        }
        var cleaned = slug.ToString().Trim('-');
        var stamp = entry.CreatedAt.ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture);
        return cleaned.Length > 0 ? $"{stamp}_{cleaned}.md" : $"{stamp}.md";
    }

    private static string UniqueName(string name, HashSet<string> used)
    {
        var candidate = name;
        for (var n = 2; !used.Add(candidate); n++)
            candidate = $"{Path.GetFileNameWithoutExtension(name)}-{n}.md";
        return candidate;
    }

    private static string Iso(DateTimeOffset value) => value.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture);
}
