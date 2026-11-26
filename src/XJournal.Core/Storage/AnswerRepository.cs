using Dapper;
using XJournal.Core.Models;
using XJournal.Core.Questions;

namespace XJournal.Core.Storage;

public sealed class AnswerRepository(JournalDatabase db, QuestionRepository questions)
{
    public AnswerSet? GetForDay(DateOnly day)
    {
        var set = db.Connection.QuerySingleOrDefault<SetRow>(
            "SELECT id, day, entry_id, answered_utc, answered_offset FROM answer_sets WHERE day = $day",
            new { day = DbTime.Day(day) });
        if (set is null) return null;
        var values = db.Connection.Query<ValueRow>(
                "SELECT answer_set_id, question_version_id, value_num, value_text FROM answers WHERE answer_set_id = $id",
                new { id = set.Id })
            .ToDictionary(a => a.QuestionVersionId, a => a.ToValue());
        return set.ToAnswerSet(values);
    }

    public bool HasAnswered(DateOnly day) =>
        db.Connection.ExecuteScalar<bool>(
            "SELECT EXISTS (SELECT 1 FROM answer_sets s JOIN answers a ON a.answer_set_id = s.id WHERE s.day = $day)",
            new { day = DbTime.Day(day) });

    /// <summary>Days (inclusive range) that have at least one check-in answer.</summary>
    public IReadOnlySet<DateOnly> GetAnsweredDays(DateOnly from, DateOnly to) =>
        db.Connection.Query<string>(
                """
                SELECT DISTINCT s.day FROM answer_sets s JOIN answers a ON a.answer_set_id = s.id
                WHERE s.day >= $from AND s.day <= $to
                """,
                new { from = DbTime.Day(from), to = DbTime.Day(to) })
            .Select(DbTime.ParseDay).ToHashSet();

    /// <summary>
    /// Saves (creating or replacing) the answers for a day. Every answer is checked against the question
    /// version it answers. The set stays linked to the entry it was first linked to; <paramref name="entryId"/>
    /// only fills the link if there is none yet.
    /// </summary>
    public void SaveForDay(DateOnly day, long? entryId, IReadOnlyDictionary<long, AnswerValue> values, DateTimeOffset answeredAt)
    {
        foreach (var (versionId, value) in values)
        {
            var version = questions.GetVersion(versionId)
                          ?? throw new ArgumentException($"Question version {versionId} doesn't exist.", nameof(values));
            if (AnswerRules.Validate(version, value) is { } problem)
                throw new ArgumentException($"The answer to \"{version.Text}\" {problem}.", nameof(values));
        }

        var connection = db.Connection;
        using var transaction = connection.BeginTransaction();
        var setId = connection.ExecuteScalar<long>(
            """
            INSERT INTO answer_sets (day, entry_id, answered_utc, answered_offset)
            VALUES ($day, $entryId, $utc, $offset)
            ON CONFLICT (day) DO UPDATE SET
                entry_id = coalesce(answer_sets.entry_id, excluded.entry_id),
                answered_utc = excluded.answered_utc,
                answered_offset = excluded.answered_offset
            RETURNING id
            """,
            new { day = DbTime.Day(day), entryId, utc = DbTime.Utc(answeredAt), offset = DbTime.Offset(answeredAt) },
            transaction);

        connection.Execute("DELETE FROM answers WHERE answer_set_id = $setId", new { setId }, transaction);
        foreach (var (versionId, value) in values)
            Insert(connection, setId, versionId, value, transaction);
        transaction.Commit();
    }

    /// <summary>Adds one answer row (callers validate first).</summary>
    internal static void Insert(Microsoft.Data.Sqlite.SqliteConnection connection, long setId, long versionId, AnswerValue value,
        Microsoft.Data.Sqlite.SqliteTransaction? transaction) =>
        connection.Execute(
            "INSERT INTO answers (answer_set_id, question_version_id, value_num, value_text) VALUES ($setId, $versionId, $num, $text)",
            new { setId, versionId, num = value.Number, text = value.Text }, transaction);

    /// <summary>Links a day's answers to an entry if they are not linked to one yet.</summary>
    public void AttachEntryIfMissing(DateOnly day, long entryId) =>
        db.Connection.Execute(
            "UPDATE answer_sets SET entry_id = $entryId WHERE day = $day AND entry_id IS NULL",
            new { day = DbTime.Day(day), entryId });

    /// <summary>All answers to any version of a question, oldest first, optionally limited to a day range (inclusive).</summary>
    public IReadOnlyList<AnswerPoint> GetHistory(string questionId, DateOnly? from = null, DateOnly? to = null) =>
        db.Connection.Query<PointRow>(
                """
                SELECT s.day, a.value_num, a.value_text, a.question_version_id AS version_id, s.entry_id
                FROM answers a
                JOIN answer_sets s ON s.id = a.answer_set_id
                JOIN question_versions v ON v.id = a.question_version_id
                WHERE v.question_id = $questionId
                  AND ($from IS NULL OR s.day >= $from)
                  AND ($to IS NULL OR s.day <= $to)
                ORDER BY s.day
                """,
                new
                {
                    questionId,
                    from = from is { } f ? DbTime.Day(f) : null,
                    to = to is { } t ? DbTime.Day(t) : null,
                })
            .Select(r => new AnswerPoint(DbTime.ParseDay(r.Day), new AnswerValue { Number = r.ValueNum, Text = r.ValueText }, r.VersionId, r.EntryId))
            .ToList();

    /// <summary>Every answer set, oldest first (used for export).</summary>
    public IReadOnlyList<AnswerSet> GetAll()
    {
        var sets = db.Connection.Query<SetRow>(
            "SELECT id, day, entry_id, answered_utc, answered_offset FROM answer_sets ORDER BY day").ToList();
        var values = ReadValues(db.Connection, null);
        return sets.Select(s => s.ToAnswerSet(values.GetValueOrDefault(s.Id) ?? [])).ToList();
    }

    /// <summary>All answers grouped by answer set id → version id → value.</summary>
    internal static Dictionary<long, Dictionary<long, AnswerValue>> ReadValues(
        Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction? transaction) =>
        connection.Query<ValueRow>("SELECT answer_set_id, question_version_id, value_num, value_text FROM answers", transaction: transaction)
            .GroupBy(a => a.AnswerSetId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(a => a.QuestionVersionId, a => a.ToValue()));

    private sealed class SetRow
    {
        public long Id { get; set; }
        public string Day { get; set; } = "";
        public long? EntryId { get; set; }
        public long AnsweredUtc { get; set; }
        public long AnsweredOffset { get; set; }

        public AnswerSet ToAnswerSet(IReadOnlyDictionary<long, AnswerValue> values) =>
            new(Id, DbTime.ParseDay(Day), EntryId, DbTime.From(AnsweredUtc, AnsweredOffset), values);
    }

    private sealed class ValueRow
    {
        public long AnswerSetId { get; set; }
        public long QuestionVersionId { get; set; }
        public double? ValueNum { get; set; }
        public string? ValueText { get; set; }

        public AnswerValue ToValue() => new() { Number = ValueNum, Text = ValueText };
    }

    private sealed class PointRow
    {
        public string Day { get; set; } = "";
        public double? ValueNum { get; set; }
        public string? ValueText { get; set; }
        public long VersionId { get; set; }
        public long? EntryId { get; set; }
    }
}
