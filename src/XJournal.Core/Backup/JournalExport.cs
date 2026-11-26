using System.Text.Json;
using System.Text.Json.Serialization;
using XJournal.Core.Models;

namespace XJournal.Core.Backup;

/// <summary>
/// Plain-JSON export of an entire journal. Human-readable and independent of xJournal, so it also
/// serves as a long-term archive. NOT encrypted.
/// </summary>
public sealed class JournalExport
{
    public const string FormatName = "xjournal-export";
    /// <summary>2 added question types; version 1 files (all 1–5 ratings) still import.</summary>
    public const int CurrentFormatVersion = 2;

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

    /// <summary>The question type ("rating", "scale", …). Missing in version 1 files, where every question is a rating.</summary>
    public string? Type { get; set; }

    /// <summary>The type's settings (labels, range, options, unit).</summary>
    public QuestionConfig? Config { get; set; }

    /// <summary>Version 1 only: the rating labels.</summary>
    public List<string>? Scale { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The type, or null if it isn't a known one.</summary>
    [JsonIgnore]
    public QuestionType? ResolvedType => Type is null ? QuestionType.Rating : QuestionTypes.TryParse(Type, out var t) ? t : null;

    [JsonIgnore]
    public QuestionConfig ResolvedConfig => Config ?? (Scale is null ? new QuestionConfig() : new QuestionConfig { Labels = Scale });

    /// <summary>This version as the database stores it (only valid once <see cref="ResolvedType"/> is known).</summary>
    internal QuestionVersion ToVersion() =>
        new(Key, QuestionId, Text, ResolvedType ?? QuestionType.Rating, ResolvedConfig, "", CreatedAt);
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

    /// <summary>Numeric answers (rating, scale, number; yes/no as 1/0).</summary>
    public double? Number { get; set; }

    /// <summary>Text answers; choice answers as a JSON array of the picked options.</summary>
    public string? Text { get; set; }

    /// <summary>Version 1 only: the 1–5 answer.</summary>
    public double? Value { get; set; }

    [JsonIgnore]
    public AnswerValue Answer => new() { Number = Number ?? (Text is null ? Value : null), Text = Text };
}
