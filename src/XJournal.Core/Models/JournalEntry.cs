namespace XJournal.Core.Models;

/// <summary>A single journal entry. <see cref="Id"/> is 0 until the entry is first saved.</summary>
public sealed class JournalEntry
{
    public long Id { get; internal set; }

    /// <summary>Globally unique id; survives export/import and allows merging journals later.</summary>
    public string Uid { get; internal set; } = Guid.NewGuid().ToString("N");

    /// <summary>When writing of this entry started (local offset preserved, millisecond precision).</summary>
    public DateTimeOffset CreatedAt { get; internal set; }

    public DateTimeOffset UpdatedAt { get; internal set; }

    /// <summary>Optional one-line tagline/summary.</summary>
    public string Summary { get; internal set; } = "";

    public string Body { get; internal set; } = "";

    public bool IsPersisted => Id != 0;

    public bool IsEmpty => string.IsNullOrWhiteSpace(Summary) && string.IsNullOrWhiteSpace(Body);

    internal JournalEntry Clone() => (JournalEntry)MemberwiseClone();
}

/// <summary>Search filter. <see cref="From"/> and <see cref="To"/> are inclusive local days.</summary>
public sealed record EntryQuery(string? Text = null, DateOnly? From = null, DateOnly? To = null, int Limit = 500);

/// <summary>
/// A search hit. <see cref="Snippet"/> marks matched text with <see cref="HighlightStart"/> /
/// <see cref="HighlightEnd"/> control characters.
/// </summary>
public sealed record EntrySearchResult(JournalEntry Entry, string Snippet)
{
    public const char HighlightStart = '\u0002';
    public const char HighlightEnd = '\u0003';
}
