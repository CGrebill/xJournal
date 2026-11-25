using XJournal.Core.Models;
using XJournal.Core.Search;
using XJournal.Core.Sessions;

namespace XJournal.Core.Tests;

public class SearchTests
{
    private static void Write(Journal journal, string summary, string body)
    {
        var session = new EntrySession(journal);
        session.SetText(summary, body);
        session.Leave();
    }

    [Fact]
    public void Finds_entries_by_word_prefix_with_highlighted_snippet()
    {
        using var env = new TestEnv();
        using var journal = env.CreateJournal();
        Write(journal, "Walk", "Went hiking in the mountains today.");
        Write(journal, "Work", "Long meeting about budgets.");

        var hit = Assert.Single(journal.Entries.Search(new("mount")));
        Assert.Contains("mountains", hit.Entry.Body);
        Assert.Contains($"{EntrySearchResult.HighlightStart}mountains{EntrySearchResult.HighlightEnd}", hit.Snippet);
    }

    [Fact]
    public void Searches_summary_and_ignores_case_and_accents()
    {
        using var env = new TestEnv();
        using var journal = env.CreateJournal();
        Write(journal, "Café visit", "Nothing else.");
        Assert.Single(journal.Entries.Search(new("CAFE")));
    }

    [Fact]
    public void All_words_must_match_and_phrases_are_exact()
    {
        using var env = new TestEnv();
        using var journal = env.CreateJournal();
        Write(journal, "", "the quick brown fox");
        Write(journal, "", "brown bears are quick");

        Assert.Equal(2, journal.Entries.Search(new("quick brown")).Count);
        Assert.Single(journal.Entries.Search(new("\"quick brown\"")));
    }

    [Theory]
    [InlineData("AND")]
    [InlineData("OR NOT (")]
    [InlineData("\"unbalanced")]
    [InlineData("col:value ^start *star NEAR(a b)")]
    [InlineData("- + ! ?")]
    public void Odd_user_input_never_throws(string text)
    {
        using var env = new TestEnv();
        using var journal = env.CreateJournal();
        Write(journal, "", "AND OR NOT value start");
        _ = journal.Entries.Search(new(text));
    }

    [Fact]
    public void Punctuation_only_query_lists_everything()
    {
        Assert.Null(FtsQuery.Build("  -- !! "));
    }

    [Fact]
    public void Filters_by_inclusive_local_date_range_newest_first()
    {
        using var env = new TestEnv(new FakeClock(new DateTimeOffset(2026, 11, 1, 23, 30, 0, TimeSpan.FromHours(-5))));
        using var journal = env.CreateJournal();
        Write(journal, "", "Nov 1 late evening");           // 2026-11-02 04:30 UTC, but Nov 1 locally
        env.Clock.Advance(TimeSpan.FromHours(1));
        Write(journal, "", "Nov 2 just after midnight");
        env.Clock.Advance(TimeSpan.FromDays(5));
        Write(journal, "", "Nov 7");

        var nov1 = journal.Entries.Search(new(null, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 1)));
        Assert.Equal("Nov 1 late evening", Assert.Single(nov1).Entry.Body);

        var range = journal.Entries.Search(new(null, new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 7)));
        Assert.Equal(["Nov 7", "Nov 2 just after midnight"], range.Select(r => r.Entry.Body));

        var textAndRange = journal.Entries.Search(new("nov", new DateOnly(2026, 11, 7), null));
        Assert.Equal("Nov 7", Assert.Single(textAndRange).Entry.Body);
    }
}
