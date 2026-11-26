using Dapper;
using XJournal.Core.Models;
using XJournal.Core.Search;

namespace XJournal.Core.Storage;

public sealed class EntryRepository(JournalDatabase db, IClock clock)
{
    private const string Columns =
        "e.id, e.uid, e.created_utc, e.created_offset, e.updated_utc, e.updated_offset, e.summary, e.body";

    public JournalEntry? Get(long id) =>
        db.Connection.QuerySingleOrDefault<EntryRow>($"SELECT {Columns} FROM entries e WHERE e.id = $id", new { id })?.ToEntry();

    /// <summary>The most recently edited entry.</summary>
    public JournalEntry? GetLatest() =>
        db.Connection.QueryFirstOrDefault<EntryRow>(
            $"SELECT {Columns} FROM entries e ORDER BY e.updated_utc DESC, e.id DESC LIMIT 1")?.ToEntry();

    public int Count() => db.Connection.ExecuteScalar<int>("SELECT count(*) FROM entries");

    /// <summary>All entries, oldest first (used for export).</summary>
    public IReadOnlyList<JournalEntry> GetAll() =>
        db.Connection.Query<EntryRow>($"SELECT {Columns} FROM entries e ORDER BY e.created_utc, e.id")
            .Select(r => r.ToEntry()).ToList();

    public void Insert(JournalEntry entry)
    {
        if (entry.IsPersisted) throw new InvalidOperationException("Entry is already saved.");
        entry.CreatedAt = DbTime.Truncate(entry.CreatedAt);
        entry.UpdatedAt = DbTime.Truncate(entry.UpdatedAt);
        entry.Id = db.Connection.ExecuteScalar<long>(
            """
            INSERT INTO entries (uid, created_utc, created_offset, updated_utc, updated_offset, summary, body)
            VALUES ($Uid, $CreatedUtc, $CreatedOffset, $UpdatedUtc, $UpdatedOffset, $Summary, $Body)
            RETURNING id
            """,
            Params(entry));
    }

    public void Update(JournalEntry entry)
    {
        if (!entry.IsPersisted) throw new InvalidOperationException("Entry has not been saved yet.");
        entry.UpdatedAt = DbTime.Truncate(entry.UpdatedAt);
        var rows = db.Connection.Execute(
            """
            UPDATE entries SET updated_utc = $UpdatedUtc, updated_offset = $UpdatedOffset,
                               summary = $Summary, body = $Body
            WHERE id = $Id
            """,
            Params(entry));
        if (rows != 1) throw new InvalidOperationException($"Entry {entry.Id} no longer exists.");
    }

    public void Delete(long id) => db.Connection.Execute("DELETE FROM entries WHERE id = $id", new { id });

    /// <summary>Full-text and/or date-range search, newest first.</summary>
    public IReadOnlyList<EntrySearchResult> Search(EntryQuery query)
    {
        var fromUtc = query.From is { } from ? DbTime.Utc(clock.StartOfDay(from)) : long.MinValue;
        var toUtc = query.To is { } to ? DbTime.Utc(clock.StartOfDay(to.AddDays(1))) : long.MaxValue;
        var match = FtsQuery.Build(query.Text);
        var args = new { fromUtc, toUtc, match, limit = query.Limit };

        if (match is null)
        {
            return db.Connection.Query<EntryRow>(
                    $"""
                     SELECT {Columns} FROM entries e
                     WHERE e.created_utc >= $fromUtc AND e.created_utc < $toUtc
                     ORDER BY e.created_utc DESC LIMIT $limit
                     """, args)
                .Select(r => new EntrySearchResult(r.ToEntry(), FtsQuery.Preview(r.Summary, r.Body)))
                .ToList();
        }

        return db.Connection.Query<SearchRow>(
                $"""
                 SELECT {Columns}, snippet(entries_fts, -1, char(2), char(3), '…', 24) AS snippet
                 FROM entries_fts JOIN entries e ON e.id = entries_fts.rowid
                 WHERE entries_fts MATCH $match AND e.created_utc >= $fromUtc AND e.created_utc < $toUtc
                 ORDER BY e.created_utc DESC LIMIT $limit
                 """, args)
            .Select(r => new EntrySearchResult(r.ToEntry(), r.Snippet))
            .ToList();
    }

    // ------------------------------------------------------------------ previous versions

    private const string RevisionColumns =
        "id, entry_id, summary, body, updated_utc, updated_offset, saved_utc, saved_offset, reason";

    /// <summary>Previous versions of an entry, most recently replaced first.</summary>
    public IReadOnlyList<EntryRevision> GetRevisions(long entryId) =>
        db.Connection.Query<RevisionRow>(
                $"SELECT {RevisionColumns} FROM entry_revisions WHERE entry_id = $entryId ORDER BY saved_utc DESC, id DESC",
                new { entryId })
            .Select(r => r.ToRevision()).ToList();

    public EntryRevision? GetRevision(long id) =>
        db.Connection.QuerySingleOrDefault<RevisionRow>(
            $"SELECT {RevisionColumns} FROM entry_revisions WHERE id = $id", new { id })?.ToRevision();

    public int RevisionCount(long entryId) =>
        db.Connection.ExecuteScalar<int>("SELECT count(*) FROM entry_revisions WHERE entry_id = $entryId", new { entryId });

    /// <summary>All previous versions of all entries, oldest first (used for export and merging).</summary>
    public IReadOnlyList<EntryRevision> GetAllRevisions() =>
        db.Connection.Query<RevisionRow>($"SELECT {RevisionColumns} FROM entry_revisions ORDER BY saved_utc, id")
            .Select(r => r.ToRevision()).ToList();

    /// <summary>
    /// Keeps a previous version of an entry. Does nothing if an identical previous version
    /// (same text and edit time) is already kept, so repeated merges don't pile up copies.
    /// </summary>
    /// <returns>True if a revision was added.</returns>
    public bool AddRevision(long entryId, string summary, string body, DateTimeOffset updatedAt, DateTimeOffset savedAt,
        string reason, Microsoft.Data.Sqlite.SqliteTransaction? transaction = null)
    {
        var exists = db.Connection.ExecuteScalar<bool>(
            """
            SELECT EXISTS (SELECT 1 FROM entry_revisions
                           WHERE entry_id = $entryId AND summary = $summary AND body = $body AND updated_utc = $updatedUtc)
            """,
            new { entryId, summary, body, updatedUtc = DbTime.Utc(updatedAt) }, transaction);
        if (exists) return false;

        db.Connection.Execute(
            """
            INSERT INTO entry_revisions (entry_id, summary, body, updated_utc, updated_offset, saved_utc, saved_offset, reason)
            VALUES ($entryId, $summary, $body, $updatedUtc, $updatedOffset, $savedUtc, $savedOffset, $reason)
            """,
            new
            {
                entryId, summary, body, reason,
                updatedUtc = DbTime.Utc(updatedAt), updatedOffset = DbTime.Offset(updatedAt),
                savedUtc = DbTime.Utc(savedAt), savedOffset = DbTime.Offset(savedAt),
            },
            transaction);
        return true;
    }

    private sealed class RevisionRow
    {
        public long Id { get; set; }
        public long EntryId { get; set; }
        public string Summary { get; set; } = "";
        public string Body { get; set; } = "";
        public long UpdatedUtc { get; set; }
        public long UpdatedOffset { get; set; }
        public long SavedUtc { get; set; }
        public long SavedOffset { get; set; }
        public string Reason { get; set; } = "";

        public EntryRevision ToRevision() => new(
            Id, EntryId, Summary, Body,
            DbTime.From(UpdatedUtc, UpdatedOffset),
            DbTime.From(SavedUtc, SavedOffset),
            Reason);
    }

    private static object Params(JournalEntry e) => new
    {
        e.Id,
        e.Uid,
        CreatedUtc = DbTime.Utc(e.CreatedAt),
        CreatedOffset = DbTime.Offset(e.CreatedAt),
        UpdatedUtc = DbTime.Utc(e.UpdatedAt),
        UpdatedOffset = DbTime.Offset(e.UpdatedAt),
        e.Summary,
        e.Body,
    };

    private class EntryRow
    {
        public long Id { get; set; }
        public string Uid { get; set; } = "";
        public long CreatedUtc { get; set; }
        public long CreatedOffset { get; set; }
        public long UpdatedUtc { get; set; }
        public long UpdatedOffset { get; set; }
        public string Summary { get; set; } = "";
        public string Body { get; set; } = "";

        public JournalEntry ToEntry() => new()
        {
            Id = Id,
            Uid = Uid,
            CreatedAt = DbTime.From(CreatedUtc, CreatedOffset),
            UpdatedAt = DbTime.From(UpdatedUtc, UpdatedOffset),
            Summary = Summary,
            Body = Body,
        };
    }

    private sealed class SearchRow : EntryRow
    {
        public string Snippet { get; set; } = "";
    }
}
