using XJournal.Core.Sessions;

namespace XJournal.Core.Tests;

public class EntrySessionTests
{
    [Fact]
    public void A_new_session_starts_an_unsaved_draft()
    {
        using var env = new TestEnv();
        using var journal = env.CreateJournal();
        var session = new EntrySession(journal);
        session.Begin();
        Assert.False(session.Current.IsPersisted);
        Assert.False(session.Save());
        session.Leave();
        Assert.Equal(0, journal.Entries.Count());
    }

    [Fact]
    public void Entry_is_stamped_when_writing_starts_with_offset_and_milliseconds()
    {
        using var env = new TestEnv();
        using var journal = env.CreateJournal();
        var session = new EntrySession(journal);
        session.Begin();

        env.Clock.Advance(TimeSpan.FromMinutes(7) + TimeSpan.FromMilliseconds(456));
        var startedAt = env.Clock.Now;
        session.SetText("", "First words");
        env.Clock.Advance(TimeSpan.FromSeconds(3));
        Assert.True(session.Save());

        var saved = journal.Entries.Get(session.Current.Id)!;
        Assert.Equal(startedAt, saved.CreatedAt);
        Assert.Equal(TimeSpan.FromHours(-5), saved.CreatedAt.Offset);
        Assert.Equal(579, saved.CreatedAt.Millisecond); // 14:30:15.123 + 7m 0.456s
        Assert.Equal(startedAt + TimeSpan.FromSeconds(3), saved.UpdatedAt);
    }

    [Theory]
    [InlineData(29, true)]
    [InlineData(31, false)]
    public void Recent_entry_is_resumed_on_sign_in(int minutesLater, bool resumes)
    {
        using var env = new TestEnv();
        using var journal = env.CreateJournal();
        var first = new EntrySession(journal);
        first.Begin();
        first.SetText("", "Morning thoughts");
        first.Leave();
        var firstId = first.Current.Id;

        env.Clock.Advance(TimeSpan.FromMinutes(minutesLater));
        var second = new EntrySession(journal);
        second.Begin();

        Assert.Equal(resumes, second.Current.Id == firstId);
        Assert.Equal(resumes, second.Current.IsPersisted);
    }

    [Fact]
    public void Resume_window_is_configurable()
    {
        using var env = new TestEnv();
        using var journal = env.CreateJournal();
        journal.Settings.ResumeWindow = TimeSpan.FromMinutes(90);
        var first = new EntrySession(journal);
        first.SetText("", "x");
        first.Leave();

        env.Clock.Advance(TimeSpan.FromMinutes(60));
        var second = new EntrySession(journal);
        second.Begin();
        Assert.Equal(first.Current.Id, second.Current.Id);
    }

    [Fact]
    public void New_saves_current_and_starts_fresh()
    {
        using var env = new TestEnv();
        using var journal = env.CreateJournal();
        var session = new EntrySession(journal);
        session.Begin();
        session.SetText("", "Entry one");
        var changed = 0;
        session.CurrentChanged += (_, _) => changed++;

        session.StartNew();

        Assert.Equal(1, changed);
        Assert.False(session.Current.IsPersisted);
        Assert.Equal("Entry one", Assert.Single(journal.Entries.GetAll()).Body);
    }

    [Fact]
    public void Clearing_an_entry_deletes_it_when_leaving()
    {
        using var env = new TestEnv();
        using var journal = env.CreateJournal();
        var session = new EntrySession(journal);
        session.SetText("", "Oops");
        session.Save();
        session.SetText("", "   ");
        session.Save();
        Assert.Equal(1, journal.Entries.Count()); // not deleted while still being edited

        session.StartNew();
        Assert.Equal(0, journal.Entries.Count());
    }

    [Fact]
    public void Opening_another_entry_saves_the_current_one()
    {
        using var env = new TestEnv();
        using var journal = env.CreateJournal();
        var session = new EntrySession(journal);
        session.SetText("", "Older");
        session.StartNew();
        var olderId = journal.Entries.GetAll().Single().Id;
        session.SetText("", "Newer");

        session.Open(olderId);

        Assert.Equal("Older", session.Current.Body);
        Assert.Equal(2, journal.Entries.Count());
    }

    [Fact]
    public void Delete_current_removes_entry()
    {
        using var env = new TestEnv();
        using var journal = env.CreateJournal();
        var session = new EntrySession(journal);
        session.SetText("", "Delete me");
        session.Save();
        session.DeleteCurrent();
        Assert.Equal(0, journal.Entries.Count());
        Assert.False(session.Current.IsPersisted);
    }

    [Fact]
    public void Answers_given_before_writing_are_linked_to_the_entry()
    {
        using var env = new TestEnv();
        using var journal = env.CreateJournal();
        var session = new EntrySession(journal);
        session.Begin();
        journal.Answers.SaveForDay(env.Clock.Today(), session.Current.IsPersisted ? session.Current.Id : null,
            new Dictionary<long, int>(), env.Clock.Now);

        session.SetText("", "Now writing");
        session.Save();

        Assert.Equal(session.Current.Id, journal.Answers.GetForDay(env.Clock.Today())!.EntryId);
    }
}
