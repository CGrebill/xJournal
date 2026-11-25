namespace XJournal.Core.Models;

/// <summary>
/// An immutable snapshot of a question's wording and scale labels. Answers always point at the
/// version that was shown, so later re-wording never changes what a past answer meant.
/// </summary>
public sealed record QuestionVersion(
    long Id,
    string QuestionId,
    string Text,
    IReadOnlyList<string> Scale,
    string ContentHash,
    DateTimeOffset CreatedAt);

/// <summary>A logical question, identified by the stable id from the import file.</summary>
public sealed record Question(string Id, int SortOrder, bool Archived, QuestionVersion Current);

/// <summary>One day's answers. <see cref="Values"/> maps question version id → value (1–5).</summary>
public sealed record AnswerSet(
    long Id,
    DateOnly Day,
    long? EntryId,
    DateTimeOffset AnsweredAt,
    IReadOnlyDictionary<long, int> Values);

/// <summary>A single historical answer to a question, for trend charts.</summary>
public sealed record AnswerPoint(DateOnly Day, int Value, long VersionId, long? EntryId);
