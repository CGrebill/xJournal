using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace XJournal.Core.Questions;

/// <summary>The JSON format used to import (and export) a set of 1–5 scale questions.</summary>
public sealed class QuestionFile
{
    public const int CurrentFormatVersion = 1;

    /// <summary>The standard agree/disagree labels used when a file doesn't specify its own.</summary>
    public static readonly IReadOnlyList<string> DefaultScale =
    [
        "Entirely disagree",
        "Somewhat disagree",
        "Neither agree nor disagree",
        "Somewhat agree",
        "Entirely agree",
    ];

    [JsonPropertyName("version")] public int Version { get; set; } = CurrentFormatVersion;

    /// <summary>Default labels for every question in the file (exactly 5, lowest first).</summary>
    [JsonPropertyName("scale")] public List<string>? Scale { get; set; }

    [JsonPropertyName("questions")] public List<QuestionFileItem> Questions { get; set; } = [];

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

public sealed class QuestionFileItem
{
    [JsonPropertyName("id")] public string? Id { get; set; }

    [JsonPropertyName("text")] public string? Text { get; set; }

    /// <summary>Optional per-question labels, overriding the file's default scale.</summary>
    [JsonPropertyName("scale")] public List<string>? Scale { get; set; }
}

/// <summary>A question from a file after validation, with its effective scale.</summary>
public sealed record ValidQuestion(string Id, string Text, IReadOnlyList<string> Scale);

public sealed record QuestionFileParseResult(IReadOnlyList<ValidQuestion> Questions, IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public static partial class QuestionFileParser
{
    public const int MaxTextLength = 500;
    public const int MaxLabelLength = 60;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$")]
    private static partial Regex IdRegex();

    public static QuestionFileParseResult ParseFile(string path) => Parse(File.ReadAllText(path));

    /// <summary>Parses and validates a question file, collecting every problem found.</summary>
    public static QuestionFileParseResult Parse(string json)
    {
        QuestionFile? file;
        try
        {
            file = JsonSerializer.Deserialize<QuestionFile>(json, QuestionFile.SerializerOptions);
        }
        catch (JsonException ex)
        {
            var where = ex.LineNumber is { } line ? $" (line {line + 1})" : "";
            return Fail($"The file isn't valid JSON{where}: {ex.Message.Split(" Path:")[0]}");
        }

        if (file is null) return Fail("The file is empty.");
        return Validate(file);
    }

    public static QuestionFileParseResult Validate(QuestionFile file)
    {
        var errors = new List<string>();
        if (file.Version != QuestionFile.CurrentFormatVersion)
            errors.Add($"Unsupported \"version\": {file.Version} (expected {QuestionFile.CurrentFormatVersion}).");

        IReadOnlyList<string> defaultScale = QuestionFile.DefaultScale;
        if (file.Scale is not null)
        {
            if (ValidateScale(file.Scale, "The file's \"scale\"") is { } scaleError) errors.Add(scaleError);
            else defaultScale = Clean(file.Scale);
        }

        if (file.Questions is null || file.Questions.Count == 0)
            errors.Add("The file has no questions.");

        var result = new List<ValidQuestion>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < (file.Questions?.Count ?? 0); i++)
        {
            var item = file.Questions![i];
            var id = item?.Id?.Trim();
            var label = string.IsNullOrEmpty(id) ? $"Question #{i + 1}" : $"Question #{i + 1} (\"{id}\")";
            var ok = true;

            if (item is null) { errors.Add($"Question #{i + 1} is empty."); continue; }
            if (string.IsNullOrEmpty(id))
            {
                errors.Add($"{label}: missing \"id\".");
                ok = false;
            }
            else if (!IdRegex().IsMatch(id))
            {
                errors.Add($"{label}: \"id\" may only contain letters, digits, '_', '-' and '.', must start with a letter or digit, and be at most 64 characters.");
                ok = false;
            }
            else if (!seen.Add(id))
            {
                errors.Add($"{label}: duplicate \"id\".");
                ok = false;
            }

            var text = NormalizeText(item.Text);
            if (text.Length == 0) { errors.Add($"{label}: missing \"text\"."); ok = false; }
            else if (text.Length > MaxTextLength) { errors.Add($"{label}: \"text\" is longer than {MaxTextLength} characters."); ok = false; }

            var scale = defaultScale;
            if (item.Scale is not null)
            {
                if (ValidateScale(item.Scale, $"{label}: \"scale\"") is { } scaleError) { errors.Add(scaleError); ok = false; }
                else scale = Clean(item.Scale);
            }

            if (ok) result.Add(new ValidQuestion(id!, text, scale));
        }

        return new QuestionFileParseResult(errors.Count == 0 ? result : [], errors);
    }

    internal static string NormalizeText(string? text) =>
        (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim().Normalize();

    private static string? ValidateScale(List<string> scale, string what)
    {
        if (scale.Count != 5) return $"{what} must have exactly 5 labels (lowest first), but has {scale.Count}.";
        if (scale.Any(string.IsNullOrWhiteSpace)) return $"{what} has an empty label.";
        if (scale.Any(l => l.Trim().Length > MaxLabelLength)) return $"{what} has a label longer than {MaxLabelLength} characters.";
        return null;
    }

    private static string[] Clean(List<string> scale) => scale.Select(l => NormalizeText(l)).ToArray();

    private static QuestionFileParseResult Fail(string error) => new([], [error]);
}
