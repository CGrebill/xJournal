using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using XJournal.Core.Models;

namespace XJournal.Core.Questions;

/// <summary>The JSON format used to import (and export) a set of check-in questions.</summary>
public sealed class QuestionFile
{
    public const int CurrentFormatVersion = 2;

    /// <summary>The agree/disagree labels used by rating questions that don't list their own.</summary>
    public static readonly IReadOnlyList<string> DefaultLabels =
    [
        "Entirely disagree",
        "Somewhat disagree",
        "Neither agree nor disagree",
        "Somewhat agree",
        "Entirely agree",
    ];

    [JsonPropertyName("version")] public int Version { get; set; } = CurrentFormatVersion;

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

/// <summary>One question in a file. Which fields apply depends on <see cref="Type"/>.</summary>
public sealed class QuestionFileItem
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }

    /// <summary>rating: step labels, lowest first (default: agree/disagree).</summary>
    [JsonPropertyName("labels")] public List<string>? Labels { get; set; }

    /// <summary>scale: range (default 1–10) and optional words for each end.</summary>
    [JsonPropertyName("min")] public int? Min { get; set; }
    [JsonPropertyName("max")] public int? Max { get; set; }
    [JsonPropertyName("low")] public string? Low { get; set; }
    [JsonPropertyName("high")] public string? High { get; set; }

    /// <summary>choice: the options, and whether several may be picked.</summary>
    [JsonPropertyName("options")] public List<string>? Options { get; set; }
    [JsonPropertyName("multiple")] public bool? Multiple { get; set; }

    /// <summary>number: optional unit.</summary>
    [JsonPropertyName("unit")] public string? Unit { get; set; }

    /// <summary>Old (version 1) per-question labels; only read to give a helpful error.</summary>
    [JsonPropertyName("scale")] public JsonElement? Scale { get; set; }
}

/// <summary>A question from a file after validation, with its settings filled in.</summary>
public sealed record ValidQuestion(string Id, string Text, QuestionType Type, QuestionConfig Config);

public sealed record QuestionFileParseResult(IReadOnlyList<ValidQuestion> Questions, IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public static partial class QuestionFileParser
{
    public const int MaxTextLength = 500;
    public const int MaxLabelLength = 60;
    public const int MaxUnitLength = 20;
    public const int MinLabels = 2, MaxLabels = 7;
    public const int MinOptions = 2, MaxOptions = 20;
    public const int ScaleLowest = 0, ScaleHighest = 10;

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
        if (file.Version == 1)
        {
            return Fail("This is an older (version 1) question file. Version 2 needs a \"type\" on every question " +
                        $"({QuestionTypes.AllKeys}). Save the sample question file to see how each one looks.");
        }

        var errors = new List<string>();
        if (file.Version != QuestionFile.CurrentFormatVersion)
            errors.Add($"Unsupported \"version\": {file.Version} (expected {QuestionFile.CurrentFormatVersion}).");

        if (file.Questions is null || file.Questions.Count == 0)
            errors.Add("The file has no questions.");

        var result = new List<ValidQuestion>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < (file.Questions?.Count ?? 0); i++)
        {
            var item = file.Questions![i];
            if (item is null) { errors.Add($"Question #{i + 1} is empty."); continue; }

            var id = item.Id?.Trim();
            var label = string.IsNullOrEmpty(id) ? $"Question #{i + 1}" : $"Question #{i + 1} (\"{id}\")";
            var before = errors.Count;

            if (string.IsNullOrEmpty(id))
                errors.Add($"{label}: missing \"id\".");
            else if (!IdRegex().IsMatch(id))
                errors.Add($"{label}: \"id\" may only contain letters, digits, '_', '-' and '.', must start with a letter or digit, and be at most 64 characters.");
            else if (!seen.Add(id))
                errors.Add($"{label}: duplicate \"id\".");

            var text = NormalizeText(item.Text);
            if (text.Length == 0) errors.Add($"{label}: missing \"text\".");
            else if (text.Length > MaxTextLength) errors.Add($"{label}: \"text\" is longer than {MaxTextLength} characters.");

            if (item.Scale is not null)
                errors.Add($"{label}: \"scale\" is from version 1 files; a rating question lists its \"labels\" instead.");

            QuestionConfig? config = null;
            if (string.IsNullOrWhiteSpace(item.Type))
                errors.Add($"{label}: missing \"type\" (one of {QuestionTypes.AllKeys}).");
            else if (!QuestionTypes.TryParse(item.Type, out var type))
                errors.Add($"{label}: unknown \"type\" \"{item.Type}\" (use one of {QuestionTypes.AllKeys}).");
            else
                config = ValidateConfig(item, type, label, errors);

            if (errors.Count == before && config is not null && QuestionTypes.TryParse(item.Type, out var valid))
                result.Add(new ValidQuestion(id!, text, valid, config));
        }

        return new QuestionFileParseResult(errors.Count == 0 ? result : [], errors);
    }

    /// <summary>Checks the fields for a question's type and returns its settings with defaults filled in.</summary>
    private static QuestionConfig ValidateConfig(QuestionFileItem item, QuestionType type, string label, List<string> errors)
    {
        var typeName = type.ToKey();
        void NotFor(string field, bool present)
        {
            if (present) errors.Add($"{label}: \"{field}\" doesn't apply to a \"{typeName}\" question.");
        }

        NotFor("labels", item.Labels is not null && type != QuestionType.Rating);
        NotFor("min", item.Min is not null && type != QuestionType.Scale);
        NotFor("max", item.Max is not null && type != QuestionType.Scale);
        NotFor("low", item.Low is not null && type != QuestionType.Scale);
        NotFor("high", item.High is not null && type != QuestionType.Scale);
        NotFor("options", item.Options is not null && type != QuestionType.Choice);
        NotFor("multiple", item.Multiple is not null && type != QuestionType.Choice);
        NotFor("unit", item.Unit is not null && type != QuestionType.Number);

        switch (type)
        {
            case QuestionType.Rating:
                var labels = item.Labels is null ? QuestionFile.DefaultLabels : Clean(item.Labels);
                if (item.Labels is not null)
                    CheckList(item.Labels, "labels", MinLabels, MaxLabels, label, errors);
                return new QuestionConfig { Labels = labels };

            case QuestionType.Scale:
                var min = item.Min ?? 1;
                var max = item.Max ?? 10;
                if (min < ScaleLowest || max > ScaleHighest || min >= max)
                    errors.Add($"{label}: the scale must go from a lower to a higher number, within {ScaleLowest}–{ScaleHighest} (it's {min}–{max}).");
                var low = CheckLabel(item.Low, "low", label, errors);
                var high = CheckLabel(item.High, "high", label, errors);
                return new QuestionConfig { Min = min, Max = max, Low = low, High = high };

            case QuestionType.Choice:
                if (item.Options is null)
                {
                    errors.Add($"{label}: a choice question needs \"options\" ({MinOptions}–{MaxOptions} of them).");
                    return new QuestionConfig();
                }
                CheckList(item.Options, "options", MinOptions, MaxOptions, label, errors);
                return new QuestionConfig { Options = Clean(item.Options), Multiple = item.Multiple == true ? true : null };

            case QuestionType.Number:
                var unit = item.Unit is null ? null : NormalizeText(item.Unit);
                if (unit is not null && unit.Length > MaxUnitLength)
                    errors.Add($"{label}: \"unit\" is longer than {MaxUnitLength} characters.");
                return new QuestionConfig { Unit = string.IsNullOrEmpty(unit) ? null : unit };

            default:
                return new QuestionConfig();
        }
    }

    private static string? CheckLabel(string? value, string field, string label, List<string> errors)
    {
        if (value is null) return null;
        var text = NormalizeText(value);
        if (text.Length > MaxLabelLength) errors.Add($"{label}: \"{field}\" is longer than {MaxLabelLength} characters.");
        return text.Length == 0 ? null : text;
    }

    private static void CheckList(List<string> items, string field, int min, int max, string label, List<string> errors)
    {
        if (items.Count < min || items.Count > max)
            errors.Add($"{label}: \"{field}\" must have {min}–{max} entries, but has {items.Count}.");
        if (items.Any(string.IsNullOrWhiteSpace))
            errors.Add($"{label}: \"{field}\" has an empty entry.");
        else if (items.Any(l => NormalizeText(l).Length > MaxLabelLength))
            errors.Add($"{label}: \"{field}\" has an entry longer than {MaxLabelLength} characters.");
        else if (items.Select(l => NormalizeText(l)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != items.Count)
            errors.Add($"{label}: \"{field}\" lists the same entry twice.");
    }

    internal static string NormalizeText(string? text) =>
        (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim().Normalize();

    private static string[] Clean(List<string> items) => items.Select(l => NormalizeText(l)).ToArray();

    private static QuestionFileParseResult Fail(string error) => new([], [error]);
}
