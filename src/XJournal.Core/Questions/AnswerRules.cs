using System.Globalization;
using XJournal.Core.Models;

namespace XJournal.Core.Questions;

/// <summary>What a valid answer looks like for each question type, and how answers read as text.</summary>
public static class AnswerRules
{
    public const int MaxTextAnswerLength = 200;

    /// <summary>Largest magnitude accepted for number answers.</summary>
    public const double MaxNumber = 1_000_000_000;

    /// <summary>Returns why <paramref name="answer"/> isn't valid for <paramref name="version"/>, or null if it is.</summary>
    public static string? Validate(QuestionVersion version, AnswerValue answer)
    {
        var config = version.Config;
        switch (version.Type)
        {
            case QuestionType.Rating:
                return WholeNumber(answer, 1, version.Labels.Count);
            case QuestionType.Scale:
                return WholeNumber(answer, config.Min ?? 1, config.Max ?? 10);
            case QuestionType.YesNo:
                return answer is { Text: null, Number: 0d or 1d } ? null : "must be yes or no";
            case QuestionType.Number:
                return answer is { Text: null, Number: { } n } && double.IsFinite(n) && Math.Abs(n) <= MaxNumber
                    ? null
                    : "must be a number";
            case QuestionType.Text:
                if (answer.Number is not null || string.IsNullOrWhiteSpace(answer.Text)) return "must be some text";
                return answer.Text.Length > MaxTextAnswerLength ? $"must be at most {MaxTextAnswerLength} characters" : null;
            case QuestionType.Choice:
                if (answer.Number is not null || answer.Text is null) return "must be one of the options";
                var picked = answer.Choices;
                if (picked.Count == 0) return "must pick at least one option";
                if (!config.AllowsMultiple && picked.Count > 1) return "allows only one option";
                if (picked.Distinct().Count() != picked.Count) return "picks an option twice";
                return picked.All(version.Options.Contains) ? null : "picks an option the question doesn't have";
            default:
                return "has an unknown question type";
        }
    }

    private static string? WholeNumber(AnswerValue answer, int min, int max) =>
        answer is { Text: null, Number: { } n } && n == Math.Floor(n) && n >= min && n <= max
            ? null
            : $"must be a whole number from {min} to {max}";

    /// <summary>The answer as people read it: "4 – Somewhat agree", "7 / 10", "Yes", "Work, Gym", "7.5 hours".</summary>
    public static string Format(QuestionVersion version, AnswerValue answer, IFormatProvider? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        switch (version.Type)
        {
            case QuestionType.Rating when answer.Number is { } n:
                var index = (int)n - 1;
                return index >= 0 && index < version.Labels.Count
                    ? $"{((int)n).ToString(culture)} – {version.Labels[index]}"
                    : ((int)n).ToString(culture);
            case QuestionType.Scale when answer.Number is { } n:
                return $"{n.ToString("0", culture)} / {(version.Config.Max ?? 10).ToString(culture)}";
            case QuestionType.YesNo when answer.Bool is { } yes:
                return yes ? "Yes" : "No";
            case QuestionType.Number when answer.Number is { } n:
                var number = n.ToString("#,0.##", culture);
                return string.IsNullOrEmpty(version.Config.Unit) ? number : $"{number} {version.Config.Unit}";
            case QuestionType.Choice:
                return string.Join(", ", answer.Choices);
            default:
                return answer.Text ?? answer.Number?.ToString(culture) ?? "";
        }
    }

    /// <summary>A short form for chart labels: the rating label or scale end word if there is one, otherwise <see cref="Format"/>.</summary>
    public static string Describe(QuestionVersion version, AnswerValue answer, IFormatProvider? culture = null)
    {
        if (version.Type == QuestionType.Scale && answer.Number is { } n)
        {
            var end = n == (version.Config.Min ?? 1) ? version.Config.Low : n == (version.Config.Max ?? 10) ? version.Config.High : null;
            var text = Format(version, answer, culture);
            return string.IsNullOrEmpty(end) ? text : $"{text} · {end}";
        }
        return Format(version, answer, culture);
    }
}
