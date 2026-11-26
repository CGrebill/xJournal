using System.Text.Json;
using System.Text.Json.Serialization;

namespace XJournal.Core.Models;

/// <summary>How a question is answered. The names in the question file are given by <see cref="QuestionTypes"/>.</summary>
public enum QuestionType
{
    /// <summary>Labeled steps, e.g. "Entirely disagree" … "Entirely agree".</summary>
    Rating,

    /// <summary>A numbered scale, 1–10 by default, with optional words for each end.</summary>
    Scale,

    YesNo,

    /// <summary>One (or, if allowed, several) of the question's own options.</summary>
    Choice,

    /// <summary>Any number, with an optional unit.</summary>
    Number,

    /// <summary>A short sentence.</summary>
    Text,
}

public static class QuestionTypes
{
    private static readonly Dictionary<QuestionType, string> Keys = new()
    {
        [QuestionType.Rating] = "rating",
        [QuestionType.Scale] = "scale",
        [QuestionType.YesNo] = "yesno",
        [QuestionType.Choice] = "choice",
        [QuestionType.Number] = "number",
        [QuestionType.Text] = "text",
    };

    /// <summary>The name used in question files, exports and the database.</summary>
    public static string ToKey(this QuestionType type) => Keys[type];

    public static bool TryParse(string? key, out QuestionType type)
    {
        foreach (var (t, k) in Keys)
        {
            if (string.Equals(k, key?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                type = t;
                return true;
            }
        }
        type = default;
        return false;
    }

    public static string AllKeys => string.Join(", ", Keys.Values.Select(k => $"\"{k}\""));

    /// <summary>A short description for people, e.g. "1–10 scale", "choice: Work / Gym", "number (hours)".</summary>
    public static string Describe(QuestionType type, QuestionConfig config) => type switch
    {
        QuestionType.Rating => "rating: " + string.Join(" / ", config.Labels ?? []),
        QuestionType.Scale => $"{config.Min ?? 1}–{config.Max ?? 10} scale" +
                              (config.Low is not null || config.High is not null ? $" ({config.Low ?? "…"} to {config.High ?? "…"})" : ""),
        QuestionType.YesNo => "yes / no",
        QuestionType.Choice => (config.AllowsMultiple ? "pick any: " : "pick one: ") + string.Join(" / ", config.Options ?? []),
        QuestionType.Number => string.IsNullOrEmpty(config.Unit) ? "number" : $"number ({config.Unit})",
        _ => "short text",
    };

    /// <summary>Whether answers can be drawn as a line over time.</summary>
    public static bool IsNumeric(this QuestionType type) => type is QuestionType.Rating or QuestionType.Scale or QuestionType.Number;
}

/// <summary>
/// The settings that belong to a question's type. Only the fields for that type are set; the rest are null.
/// Part of a question version, so changing any of them creates a new version.
/// </summary>
public sealed record QuestionConfig
{
    /// <summary>Rating: the step labels, lowest first.</summary>
    public IReadOnlyList<string>? Labels { get; init; }

    /// <summary>Scale: the lowest and highest numbers.</summary>
    public int? Min { get; init; }
    public int? Max { get; init; }

    /// <summary>Scale: optional words for the lowest and highest ends.</summary>
    public string? Low { get; init; }
    public string? High { get; init; }

    /// <summary>Choice: the options, in display order.</summary>
    public IReadOnlyList<string>? Options { get; init; }

    /// <summary>Choice: true if several options may be picked (null means one).</summary>
    public bool? Multiple { get; init; }

    /// <summary>Number: optional unit, e.g. "hours".</summary>
    public string? Unit { get; init; }

    public bool AllowsMultiple => Multiple == true;

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static QuestionConfig FromJson(string? json) =>
        string.IsNullOrWhiteSpace(json) ? new QuestionConfig() : JsonSerializer.Deserialize<QuestionConfig>(json, JsonOptions) ?? new QuestionConfig();

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>
/// An immutable snapshot of a question's wording, type and settings. Answers always point at the
/// version that was shown, so later re-wording never changes what a past answer meant.
/// </summary>
public sealed record QuestionVersion(
    long Id,
    string QuestionId,
    string Text,
    QuestionType Type,
    QuestionConfig Config,
    string ContentHash,
    DateTimeOffset CreatedAt)
{
    public IReadOnlyList<string> Labels => Config.Labels ?? [];
    public IReadOnlyList<string> Options => Config.Options ?? [];
}

/// <summary>A logical question, identified by the stable id from the import file.</summary>
public sealed record Question(string Id, int SortOrder, bool Archived, QuestionVersion Current);

/// <summary>
/// One answer. Numeric types (rating, scale, number, and yes/no as 1/0) use <see cref="Number"/>;
/// text uses <see cref="Text"/>, and choice stores the picked options as a JSON array in <see cref="Text"/>.
/// </summary>
public sealed record AnswerValue
{
    public double? Number { get; init; }
    public string? Text { get; init; }

    public static AnswerValue Of(double number) => new() { Number = number };
    public static AnswerValue OfText(string text) => new() { Text = text };
    public static AnswerValue OfBool(bool yes) => Of(yes ? 1 : 0);
    public static AnswerValue OfChoices(IEnumerable<string> options) => OfText(JsonSerializer.Serialize(options.ToArray(), QuestionConfig.JsonOptions));

    /// <summary>Yes/no answers: true for yes.</summary>
    public bool? Bool => Number is { } n ? n != 0 : null;

    /// <summary>Choice answers: the picked options (empty if this isn't a valid choice answer).</summary>
    public IReadOnlyList<string> Choices
    {
        get
        {
            if (Text is null) return [];
            try
            {
                return JsonSerializer.Deserialize<string[]>(Text) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }
}

/// <summary>One day's answers. <see cref="Values"/> maps question version id → answer.</summary>
public sealed record AnswerSet(
    long Id,
    DateOnly Day,
    long? EntryId,
    DateTimeOffset AnsweredAt,
    IReadOnlyDictionary<long, AnswerValue> Values);

/// <summary>A single historical answer to a question, for trends.</summary>
public sealed record AnswerPoint(DateOnly Day, AnswerValue Value, long VersionId, long? EntryId);
