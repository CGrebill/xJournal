using System.Text.Json;
using System.Text.Json.Serialization;

namespace XJournal.Core.Backup;

/// <summary>
/// Plain-JSON export of an entire journal. Human-readable and independent of xJournal, so it also
/// serves as a long-term archive. NOT encrypted.
/// </summary>
public sealed class JournalExport
{
    public const string FormatName = "xjournal-export";
    public const int CurrentFormatVersion = 1;

    public string? Format { get; set; }
    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public DateTimeOffset ExportedAt { get; set; }
    public string? AppVersion { get; set; }
    public List<ExportEntry> Entries { get; set; } = [];
    public List<ExportQuestion> Questions { get; set; } = [];

    /// <summary>Every wording ever used for each question; answers refer to these by <c>key</c>.</summary>
    public List<ExportQuestionVersion> QuestionVersions { get; set; } = [];

    public List<ExportAnswerSet> AnswerSets { get; set; } = [];
    public Dictionary<string, string> Settings { get; set; } = [];

    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed class ExportEntry
{
    public string Uid { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string Summary { get; set; } = "";
    public string Body { get; set; } = "";

    /// <summary>Previous versions of this entry (optional; omitted when there are none).</summary>
    public List<ExportRevision>? Revisions { get; set; }
}

public sealed class ExportRevision
{
    public string Summary { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset SavedAt { get; set; }
    public string Reason { get; set; } = "";
}

public sealed class ExportQuestion
{
    public string Id { get; set; } = "";
    public int SortOrder { get; set; }
    public bool Archived { get; set; }
    public long CurrentVersion { get; set; }
}

public sealed class ExportQuestionVersion
{
    public long Key { get; set; }
    public string QuestionId { get; set; } = "";
    public string Text { get; set; } = "";
    public List<string> Scale { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ExportAnswerSet
{
    public string Day { get; set; } = "";
    public string? EntryUid { get; set; }
    public DateTimeOffset AnsweredAt { get; set; }
    public List<ExportAnswer> Answers { get; set; } = [];
}

public sealed class ExportAnswer
{
    public string QuestionId { get; set; } = "";
    public long Version { get; set; }
    public int Value { get; set; }
}
