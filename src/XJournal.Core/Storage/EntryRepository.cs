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

    /// <summary>Inserts an entry exactly as given (used by import); keeps uid and timestamps.</summary>
    internal void InsertRaw(JournalEntry entry) => Insert(entry);

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
