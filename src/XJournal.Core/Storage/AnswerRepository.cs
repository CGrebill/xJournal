using Dapper;
using XJournal.Core.Models;

namespace XJournal.Core.Storage;

public sealed class AnswerRepository(JournalDatabase db)
{
    public AnswerSet? GetForDay(DateOnly day)
    {
        var set = db.Connection.QuerySingleOrDefault<SetRow>(
            "SELECT id, day, entry_id, answered_utc, answered_offset FROM answer_sets WHERE day = $day",
            new { day = DbTime.Day(day) });
        if (set is null) return null;
        var values = db.Connection.Query<(long VersionId, long Value)>(
                "SELECT question_version_id, value FROM answers WHERE answer_set_id = $id", new { id = set.Id })
            .ToDictionary(a => a.VersionId, a => (int)a.Value);
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
    /// Saves (creating or replacing) the answers for a day. The set stays linked to the entry it was
    /// first linked to; <paramref name="entryId"/> only fills the link if there is none yet.
    /// </summary>
    public void SaveForDay(DateOnly day, long? entryId, IReadOnlyDictionary<long, int> values, DateTimeOffset answeredAt)
    {
        foreach (var (versionId, value) in values)
        {
            if (value is < 1 or > 5)
                throw new ArgumentOutOfRangeException(nameof(values), $"Answer for version {versionId} must be 1–5.");
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
        {
            connection.Execute(
                "INSERT INTO answers (answer_set_id, question_version_id, value) VALUES ($setId, $versionId, $value)",
                new { setId, versionId, value }, transaction);
        }
        transaction.Commit();
    }

    /// <summary>Links a day's answers to an entry if they are not linked to one yet.</summary>
    public void AttachEntryIfMissing(DateOnly day, long entryId) =>
        db.Connection.Execute(
            "UPDATE answer_sets SET entry_id = $entryId WHERE day = $day AND entry_id IS NULL",
            new { day = DbTime.Day(day), entryId });

    /// <summary>All answers to any version of a question, oldest first, optionally limited to a day range (inclusive).</summary>
    public IReadOnlyList<AnswerPoint> GetHistory(string questionId, DateOnly? from = null, DateOnly? to = null) =>
        db.Connection.Query<PointRow>(
                """
                SELECT s.day, a.value, a.question_version_id AS version_id, s.entry_id
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
            .Select(r => new AnswerPoint(DbTime.ParseDay(r.Day), (int)r.Value, r.VersionId, r.EntryId))
            .ToList();

    /// <summary>Every answer set, oldest first (used for export).</summary>
    public IReadOnlyList<AnswerSet> GetAll()
    {
        var sets = db.Connection.Query<SetRow>(
            "SELECT id, day, entry_id, answered_utc, answered_offset FROM answer_sets ORDER BY day").ToList();
        var values = db.Connection.Query<(long SetId, long VersionId, long Value)>(
                "SELECT answer_set_id, question_version_id, value FROM answers")
            .GroupBy(a => a.SetId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(a => a.VersionId, a => (int)a.Value));
        return sets.Select(s => s.ToAnswerSet(values.GetValueOrDefault(s.Id) ?? [])).ToList();
    }

    private sealed class SetRow
    {
        public long Id { get; set; }
        public string Day { get; set; } = "";
        public long? EntryId { get; set; }
        public long AnsweredUtc { get; set; }
        public long AnsweredOffset { get; set; }

        public AnswerSet ToAnswerSet(IReadOnlyDictionary<long, int> values) =>
            new(Id, DbTime.ParseDay(Day), EntryId, DbTime.From(AnsweredUtc, AnsweredOffset), values);
    }

    private sealed class PointRow
    {
        public string Day { get; set; } = "";
        public long Value { get; set; }
        public long VersionId { get; set; }
        public long? EntryId { get; set; }
    }
}
