using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using XJournal.Core.Models;
using XJournal.Core.Questions;
using XJournal.Core.Storage;

namespace XJournal.Core.Backup;

/// <summary>What a merge will do (or did).</summary>
public sealed record MergeSummary(
    int NewEntries,
    int UpdatedEntries,
    int KeptEntries,
    int UnchangedEntries,
    int NewRevisions,
    int NewQuestions,
    int NewWordings,
    int NewAnswerDays,
    int MergedAnswerDays)
{
    public bool HasChanges =>
        NewEntries + UpdatedEntries + KeptEntries + NewRevisions + NewQuestions + NewWordings + NewAnswerDays + MergedAnswerDays > 0;
}

/// <summary>A merge, analysed against the local journal and ready to apply.</summary>
public sealed class MergePlan
{
    internal MergePlan(JournalExport incoming, MergeSummary summary)
    {
        Incoming = incoming;
        Summary = summary;
    }

    public JournalExport Incoming { get; }
    public MergeSummary Summary { get; }
}

/// <summary>
/// Combines a journal from another computer into this one. Merging only ever adds:
/// <list type="bullet">
/// <item>Entries are matched by uid. When both sides changed an entry, the most recently edited text
/// wins and the other text is kept as a previous version.</item>
/// <item>Question wordings are matched by content, so answers stay tied to the exact wording.
/// The newest wording becomes current; local archive state and order are kept.</item>
/// <item>Answers are merged per day and per question; where both sides answered the same question
/// on the same day, the more recently saved answer wins.</item>
/// <item>Local settings are kept. Nothing is deleted.</item>
/// </list>
/// </summary>
public static class JournalMerger
{
    public static MergePlan Plan(Journal local, JournalExport incoming) =>
        new(incoming, Run(local, incoming, transaction: null));

    /// <summary>
    /// Applies a plan atomically. When <paramref name="safetyCopyPath"/> is given, an encrypted copy of
    /// the local journal is written there first.
    /// </summary>
    public static MergeSummary Apply(Journal local, MergePlan plan, string? safetyCopyPath = null)
    {
        if (safetyCopyPath is not null) local.Database.BackupTo(safetyCopyPath);
        using var transaction = local.Database.Connection.BeginTransaction();
        var result = Run(local, plan.Incoming, transaction);
        transaction.Commit();
        return result;
    }

    /// <summary>Default location for the safety copy taken before merging.</summary>
    public static string? DefaultSafetyCopyPath(Journal local) =>
        local.SafetyBackupsDirectory is { } folder
            ? Path.Combine(folder, $"{local.Username}-before-merge-{local.Clock.LocalNow():yyyyMMdd-HHmmss}.xjbackup")
            : null;

    /// <summary>Analyses the merge, and performs it when a transaction is given.</summary>
    private static MergeSummary Run(Journal local, JournalExport incoming, SqliteTransaction? transaction)
    {
        var write = transaction is not null;
        var connection = local.Database.Connection;
        var now = local.Clock.LocalNow();

        // Read everything up front: inside a transaction every command must carry it.
        var localEntries = ReadEntries(connection, transaction).ToDictionary(e => e.Uid);
        var revisionKeys = ReadRevisionKeys(connection, transaction);
        var localQuestions = ReadQuestions(connection, transaction).ToDictionary(q => q.Id);
        var localVersions = ReadVersions(connection, transaction);
        var versionIdByHash = localVersions.ToDictionary(v => (v.QuestionId, v.ContentHash), v => v.Id);
        var versionById = localVersions.ToDictionary(v => v.Id);
        var localSets = ReadAnswerSets(connection, transaction).ToDictionary(s => s.Day);

        int newEntries = 0, updated = 0, kept = 0, unchanged = 0, newRevisions = 0;
        int newQuestions = 0, newWordings = 0, newDays = 0, mergedDays = 0;

        // ---- entries -------------------------------------------------------------------
        var entryIdByUid = localEntries.ToDictionary(e => e.Key, e => e.Value.Id);
        foreach (var e in incoming.Entries)
        {
            if (!localEntries.TryGetValue(e.Uid, out var mine))
            {
                newEntries++;
                if (write)
                {
                    var id = connection.ExecuteScalar<long>(
                        """
                        INSERT INTO entries (uid, created_utc, created_offset, updated_utc, updated_offset, summary, body)
                        VALUES ($Uid, $cu, $co, $uu, $uo, $Summary, $Body) RETURNING id
                        """,
                        new
                        {
                            e.Uid, e.Summary, e.Body,
                            cu = DbTime.Utc(e.CreatedAt), co = DbTime.Offset(e.CreatedAt),
                            uu = DbTime.Utc(e.UpdatedAt), uo = DbTime.Offset(e.UpdatedAt),
                        }, transaction);
                    entryIdByUid[e.Uid] = id;
                    JournalTransfer.InsertRevisions(connection, id, e.Revisions, transaction!);
                }
                continue;
            }

            var current = (Summary: mine.Summary, Body: mine.Body);
            if (e.Summary == mine.Summary && e.Body == mine.Body)
            {
                unchanged++;
            }
            else if (e.UpdatedAt > mine.UpdatedAt)
            {
                // Theirs is newer: it becomes the text, ours is kept as a previous version.
                updated++;
                AddRevision(mine.Id, mine.Summary, mine.Body, mine.UpdatedAt, EntryRevision.MergeReason, now);
                current = (e.Summary, e.Body);
                if (write)
                {
                    connection.Execute(
                        "UPDATE entries SET summary = $Summary, body = $Body, updated_utc = $uu, updated_offset = $uo WHERE id = $id",
                        new { e.Summary, e.Body, uu = DbTime.Utc(e.UpdatedAt), uo = DbTime.Offset(e.UpdatedAt), id = mine.Id },
                        transaction);
                }
            }
            else if (AddRevision(mine.Id, e.Summary, e.Body, e.UpdatedAt, EntryRevision.MergeReason, now))
            {
                // Ours is newer: keep it, and keep theirs as a previous version.
                kept++;
            }
            else
            {
                unchanged++; // already merged before
            }

            foreach (var r in e.Revisions ?? [])
            {
                if (r.Summary == current.Summary && r.Body == current.Body) continue;
                if (AddRevision(mine.Id, r.Summary, r.Body, r.UpdatedAt, r.Reason, r.SavedAt)) newRevisions++;
            }
        }

        // ---- questions and wordings ------------------------------------------------------
        var incomingVersions = incoming.QuestionVersions.ToDictionary(v => v.Key);
        var localVersionForKey = new Dictionary<long, long?>();
        var nextOrder = localQuestions.Count == 0 ? 0 : localQuestions.Values.Max(q => q.SortOrder) + 1;
        foreach (var q in incoming.Questions.OrderBy(q => q.SortOrder))
        {
            var isNew = !localQuestions.ContainsKey(q.Id);
            if (isNew)
            {
                newQuestions++;
                if (write)
                {
                    connection.Execute("INSERT INTO questions (id, sort_order, archived) VALUES ($Id, $order, $Archived)",
                        new { q.Id, order = nextOrder++, q.Archived }, transaction);
                }
            }

            foreach (var v in incoming.QuestionVersions.Where(v => v.QuestionId == q.Id))
            {
                var hash = QuestionImporter.ContentHash(v.Text, v.Scale);
                if (versionIdByHash.TryGetValue((q.Id, hash), out var existing))
                {
                    localVersionForKey[v.Key] = existing;
                    continue;
                }
                if (!isNew) newWordings++;
                if (write)
                {
                    var id = local.QuestionImporter.GetOrAddVersion(q.Id, v.Text, v.Scale, v.CreatedAt, transaction!);
                    versionIdByHash[(q.Id, hash)] = id;
                    versionById[id] = new VersionRow { Id = id, QuestionId = q.Id, ContentHash = hash, CreatedUtc = DbTime.Utc(v.CreatedAt) };
                    localVersionForKey[v.Key] = id;
                }
                else
                {
                    localVersionForKey[v.Key] = null; // would be created
                }
            }

            // The newest wording becomes current.
            if (!write || !incomingVersions.TryGetValue(q.CurrentVersion, out var theirCurrent)) continue;
            var theirCurrentId = localVersionForKey[theirCurrent.Key]!.Value;
            var ourCurrentId = isNew ? (long?)null : localQuestions[q.Id].CurrentVersionId;
            if (ourCurrentId is null ||
                (ourCurrentId != theirCurrentId && versionById[ourCurrentId.Value].CreatedUtc < DbTime.Utc(theirCurrent.CreatedAt)))
            {
                connection.Execute("UPDATE questions SET current_version_id = $v WHERE id = $id",
                    new { v = theirCurrentId, id = q.Id }, transaction);
            }
        }

        // ---- answers ----------------------------------------------------------------------
        foreach (var s in incoming.AnswerSets)
        {
            long? entryId = s.EntryUid is { } uid && entryIdByUid.TryGetValue(uid, out var eid) ? eid : null;
            if (!localSets.TryGetValue(s.Day, out var mine))
            {
                newDays++;
                if (write)
                {
                    var setId = connection.ExecuteScalar<long>(
                        "INSERT INTO answer_sets (day, entry_id, answered_utc, answered_offset) VALUES ($Day, $entryId, $u, $o) RETURNING id",
                        new { s.Day, entryId, u = DbTime.Utc(s.AnsweredAt), o = DbTime.Offset(s.AnsweredAt) }, transaction);
                    foreach (var a in s.Answers)
                        InsertAnswer(setId, localVersionForKey[a.Version]!.Value, a.Value);
                }
                continue;
            }

            var theirsNewer = DbTime.Utc(s.AnsweredAt) > mine.AnsweredUtc;
            var changed = false;
            var values = new Dictionary<long, int>(mine.Values);
            foreach (var a in s.Answers)
            {
                var versionId = localVersionForKey[a.Version];
                if (versionId is null) { changed = true; continue; } // dry run: a new wording
                if (!values.TryGetValue(versionId.Value, out var ours))
                {
                    values[versionId.Value] = a.Value;
                    changed = true;
                }
                else if (ours != a.Value && theirsNewer)
                {
                    values[versionId.Value] = a.Value;
                    changed = true;
                }
            }
            if (!changed) continue;

            mergedDays++;
            if (!write) continue;
            connection.Execute("DELETE FROM answers WHERE answer_set_id = $id", new { id = mine.Id }, transaction);
            foreach (var (versionId, value) in values) InsertAnswer(mine.Id, versionId, value);
            connection.Execute(
                """
                UPDATE answer_sets SET entry_id = coalesce(entry_id, $entryId),
                    answered_utc = max(answered_utc, $u),
                    answered_offset = CASE WHEN $u > answered_utc THEN $o ELSE answered_offset END
                WHERE id = $id
                """,
                new { id = mine.Id, entryId, u = DbTime.Utc(s.AnsweredAt), o = DbTime.Offset(s.AnsweredAt) }, transaction);
        }

        return new MergeSummary(newEntries, updated, kept, unchanged, newRevisions, newQuestions, newWordings, newDays, mergedDays);

        bool AddRevision(long entryId, string summary, string body, DateTimeOffset updatedAt, string reason, DateTimeOffset savedAt)
        {
            if (!revisionKeys.Add((entryId, summary, body, DbTime.Utc(updatedAt)))) return false;
            if (write) local.Entries.AddRevision(entryId, summary, body, updatedAt, savedAt, reason, transaction);
            return true;
        }

        void InsertAnswer(long setId, long versionId, int value) =>
            connection.Execute(
                "INSERT INTO answers (answer_set_id, question_version_id, value) VALUES ($setId, $versionId, $value)",
                new { setId, versionId, value }, transaction);
    }

    // ---- snapshot reads (transaction-aware) -------------------------------------------------

    private static IEnumerable<EntryRow> ReadEntries(SqliteConnection c, SqliteTransaction? t) =>
        c.Query<EntryRow>("SELECT id, uid, summary, body, updated_utc, updated_offset FROM entries", transaction: t);

    private static HashSet<(long, string, string, long)> ReadRevisionKeys(SqliteConnection c, SqliteTransaction? t) =>
        c.Query<(long, string, string, long)>("SELECT entry_id, summary, body, updated_utc FROM entry_revisions", transaction: t)
            .ToHashSet();

    private static IEnumerable<QuestionRow> ReadQuestions(SqliteConnection c, SqliteTransaction? t) =>
        c.Query<QuestionRow>("SELECT id, sort_order, current_version_id FROM questions", transaction: t);

    private static List<VersionRow> ReadVersions(SqliteConnection c, SqliteTransaction? t) =>
        c.Query<VersionRow>("SELECT id, question_id, content_hash, created_utc FROM question_versions", transaction: t).ToList();

    private static List<SetRow> ReadAnswerSets(SqliteConnection c, SqliteTransaction? t)
    {
        var sets = c.Query<SetRow>("SELECT id, day, answered_utc FROM answer_sets", transaction: t).ToList();
        var answers = c.Query<(long SetId, long VersionId, long Value)>(
                "SELECT answer_set_id, question_version_id, value FROM answers", transaction: t)
            .ToLookup(a => a.SetId);
        foreach (var s in sets) s.Values = answers[s.Id].ToDictionary(a => a.VersionId, a => (int)a.Value);
        return sets;
    }

    private sealed class EntryRow
    {
        public long Id { get; set; }
        public string Uid { get; set; } = "";
        public string Summary { get; set; } = "";
        public string Body { get; set; } = "";
        public long UpdatedUtc { get; set; }
        public long UpdatedOffset { get; set; }
        public DateTimeOffset UpdatedAt => DbTime.From(UpdatedUtc, UpdatedOffset);
    }

    private sealed class QuestionRow
    {
        public string Id { get; set; } = "";
        public long SortOrder { get; set; }
        public long? CurrentVersionId { get; set; }
    }

    private sealed class VersionRow
    {
        public long Id { get; set; }
        public string QuestionId { get; set; } = "";
        public string ContentHash { get; set; } = "";
        public long CreatedUtc { get; set; }
    }

    private sealed class SetRow
    {
        public long Id { get; set; }
        public string Day { get; set; } = "";
        public long AnsweredUtc { get; set; }
        public Dictionary<long, int> Values { get; set; } = [];
    }
}
