using Dapper;
using XJournal.Core.Backup;
using XJournal.Core.Models;
using XJournal.Core.Questions;
using XJournal.Core.Sessions;

namespace XJournal.Core.Tests;

/// <summary>Two "computers" that start from the same journal, then diverge and are merged.</summary>
public sealed class MergeTests : IDisposable
{
    private readonly TestEnv _home = new();
    private readonly TestEnv _laptop;
    private readonly Journal _homeJournal;
    private readonly Journal _laptopJournal;

    public MergeTests()
    {
        _laptop = new TestEnv(new FakeClock(_home.Clock.Now));
        _homeJournal = _home.CreateJournal();
        Import(_homeJournal, ("mood", "Good mood"), ("sleep", "Slept well"));
        Write(_homeJournal, "Shared", "Written before the laptop copy was made.");
        _homeJournal.Answers.SaveForDay(_home.Clock.Today(), null,
            new Dictionary<long, int> { [V(_homeJournal, "mood")] = 3 }, _home.Clock.Now);

        var backup = _home.TempFile("start.xjbackup");
        JournalTransfer.ExportEncrypted(_homeJournal, backup);
        _laptopJournal = new JournalTransfer(_laptop.Accounts).ImportBackup(backup, TestEnv.Password, "jordan", null, false);
    }

    public void Dispose()
    {
        _homeJournal.Dispose();
        _laptopJournal.Dispose();
        _home.Dispose();
        _laptop.Dispose();
    }

    private static void Import(Journal journal, params (string Id, string Text)[] questions)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new { questions = questions.Select(q => new { id = q.Id, text = q.Text }) });
        journal.QuestionImporter.Apply(journal.QuestionImporter.Plan(QuestionFileParser.Parse(json).Questions, archiveMissing: false));
    }

    private static JournalEntry Write(Journal journal, string summary, string body)
    {
        var session = new EntrySession(journal);
        session.SetText(summary, body);
        session.Leave();
        return session.Current;
    }

    private static void Edit(Journal journal, string uid, string body)
    {
        var session = new EntrySession(journal);
        session.Open(journal.Entries.GetAll().Single(e => e.Uid == uid).Id);
        session.SetText(session.Current.Summary, body);
        session.Leave();
    }

    private static long V(Journal journal, string questionId) => journal.Questions.Get(questionId)!.Current.Id;

    private static JournalEntry Shared(Journal journal) => journal.Entries.GetAll().Single(e => e.Summary == "Shared");

    /// <summary>Merges the laptop's journal into home via an encrypted backup.</summary>
    private MergeSummary MergeLaptopIntoHome(bool apply = true)
    {
        var path = _laptop.TempFile($"laptop-{Guid.NewGuid():N}.xjbackup");
        JournalTransfer.ExportEncrypted(_laptopJournal, path);
        var plan = JournalMerger.Plan(_homeJournal, JournalTransfer.ReadBackup(path, TestEnv.Password));
        return apply ? JournalMerger.Apply(_homeJournal, plan) : plan.Summary;
    }

    [Fact]
    public void Merging_an_unchanged_copy_changes_nothing()
    {
        var summary = MergeLaptopIntoHome();
        Assert.False(summary.HasChanges);
        Assert.Equal(1, summary.UnchangedEntries);
    }

    [Fact]
    public void New_entries_arrive_with_their_uid_and_timestamps()
    {
        _laptop.Clock.Advance(TimeSpan.FromHours(3));
        var written = Write(_laptopJournal, "On the train", "Written on the laptop.");

        var preview = MergeLaptopIntoHome(apply: false);
        Assert.Equal(1, preview.NewEntries);
        Assert.Equal(1, _homeJournal.Entries.Count()); // a plan writes nothing

        Assert.Equal(1, MergeLaptopIntoHome().NewEntries);
        var arrived = _homeJournal.Entries.GetAll().Single(e => e.Uid == written.Uid);
        Assert.Equal(written.CreatedAt, arrived.CreatedAt);
        Assert.Equal(written.UpdatedAt, arrived.UpdatedAt);
        Assert.Single(_homeJournal.Entries.Search(new("train")));
    }

    [Fact]
    public void Newer_edit_elsewhere_wins_and_our_text_is_kept_as_a_previous_version()
    {
        var uid = Shared(_homeJournal).Uid;
        _home.Clock.Advance(TimeSpan.FromHours(1));
        Edit(_homeJournal, uid, "Home edit (older).");
        _laptop.Clock.Advance(TimeSpan.FromHours(2));
        Edit(_laptopJournal, uid, "Laptop edit (newer).");

        var summary = MergeLaptopIntoHome();

        Assert.Equal(1, summary.UpdatedEntries);
        var entry = Shared(_homeJournal);
        Assert.Equal("Laptop edit (newer).", entry.Body);
        var kept = Assert.Single(_homeJournal.Entries.GetRevisions(entry.Id));
        Assert.Equal("Home edit (older).", kept.Body);
        Assert.Equal(EntryRevision.MergeReason, kept.Reason);
        Assert.Single(_homeJournal.Entries.Search(new("laptop")));
        Assert.Empty(_homeJournal.Entries.Search(new("home"))); // previous versions aren't searched
    }

    [Fact]
    public void Newer_edit_here_stays_and_their_text_is_kept_as_a_previous_version()
    {
        var uid = Shared(_homeJournal).Uid;
        _laptop.Clock.Advance(TimeSpan.FromHours(1));
        Edit(_laptopJournal, uid, "Laptop edit (older).");
        _home.Clock.Advance(TimeSpan.FromHours(2));
        Edit(_homeJournal, uid, "Home edit (newer).");

        Assert.Equal(1, MergeLaptopIntoHome().KeptEntries);
        var entry = Shared(_homeJournal);
        Assert.Equal("Home edit (newer).", entry.Body);
        Assert.Equal("Laptop edit (older).", Assert.Single(_homeJournal.Entries.GetRevisions(entry.Id)).Body);
    }

    [Fact]
    public void Merging_the_same_file_twice_is_harmless()
    {
        var uid = Shared(_homeJournal).Uid;
        _laptop.Clock.Advance(TimeSpan.FromHours(1));
        Edit(_laptopJournal, uid, "Laptop edit (older).");
        Write(_laptopJournal, "", "New on laptop");
        _home.Clock.Advance(TimeSpan.FromHours(2));
        Edit(_homeJournal, uid, "Home edit (newer).");

        var path = _laptop.TempFile("once.xjbackup");
        JournalTransfer.ExportEncrypted(_laptopJournal, path);
        var incoming = JournalTransfer.ReadBackup(path, TestEnv.Password);
        Assert.True(JournalMerger.Apply(_homeJournal, JournalMerger.Plan(_homeJournal, incoming)).HasChanges);

        var again = JournalMerger.Plan(_homeJournal, incoming);
        Assert.False(again.Summary.HasChanges);
        JournalMerger.Apply(_homeJournal, again);
        Assert.Equal(2, _homeJournal.Entries.Count());
        Assert.Single(_homeJournal.Entries.GetRevisions(Shared(_homeJournal).Id));
    }

    [Fact]
    public void Two_way_merge_converges()
    {
        var uid = Shared(_homeJournal).Uid;
        _laptop.Clock.Advance(TimeSpan.FromHours(2));
        Edit(_laptopJournal, uid, "Laptop edit (newer).");
        Write(_laptopJournal, "", "Laptop only");
        _home.Clock.Advance(TimeSpan.FromHours(1));
        Write(_homeJournal, "", "Home only");

        MergeLaptopIntoHome();
        var path = _home.TempFile("home.xjbackup");
        JournalTransfer.ExportEncrypted(_homeJournal, path);
        JournalMerger.Apply(_laptopJournal, JournalMerger.Plan(_laptopJournal, JournalTransfer.ReadBackup(path, TestEnv.Password)));

        string Snapshot(Journal j) => string.Join("|", j.Entries.GetAll().OrderBy(e => e.Uid).Select(e => $"{e.Uid}:{e.Body}"));
        Assert.Equal(Snapshot(_homeJournal), Snapshot(_laptopJournal));
        Assert.Equal(3, _laptopJournal.Entries.Count());
        Assert.False(MergeLaptopIntoHome().HasChanges);
    }

    [Fact]
    public void Question_wordings_unify_and_the_newest_becomes_current()
    {
        _laptop.Clock.Advance(TimeSpan.FromDays(1));
        Import(_laptopJournal, ("mood", "I was in a good mood"), ("focus", "I could focus"));
        _laptopJournal.Answers.SaveForDay(_laptop.Clock.Today(), null,
            new Dictionary<long, int> { [V(_laptopJournal, "mood")] = 5, [V(_laptopJournal, "focus")] = 2 }, _laptop.Clock.Now);

        var summary = MergeLaptopIntoHome();

        Assert.Equal(1, summary.NewQuestions);
        Assert.Equal(1, summary.NewWordings);
        Assert.Equal(1, summary.NewAnswerDays);
        Assert.Equal("I was in a good mood", _homeJournal.Questions.Get("mood")!.Current.Text);
        Assert.Equal(2, _homeJournal.Questions.GetVersions("mood").Count); // "Good mood" was not duplicated
        var history = _homeJournal.Answers.GetHistory("mood");
        Assert.Equal(["Good mood", "I was in a good mood"],
            history.Select(p => _homeJournal.Questions.GetVersion(p.VersionId)!.Text));
        Assert.Single(_homeJournal.Answers.GetHistory("focus"));
    }

    [Fact]
    public void Same_day_answers_merge_per_question_and_newest_answer_wins()
    {
        var today = _home.Clock.Today();
        var mood = V(_homeJournal, "mood");
        var sleep = V(_homeJournal, "sleep");
        _home.Clock.Advance(TimeSpan.FromHours(1));
        _homeJournal.Answers.SaveForDay(today, null, new Dictionary<long, int> { [mood] = 1 }, _home.Clock.Now);
        _laptop.Clock.Advance(TimeSpan.FromHours(2));
        _laptopJournal.Answers.SaveForDay(today, null,
            new Dictionary<long, int> { [V(_laptopJournal, "mood")] = 4, [V(_laptopJournal, "sleep")] = 2 }, _laptop.Clock.Now);

        Assert.Equal(1, MergeLaptopIntoHome().MergedAnswerDays);
        var merged = _homeJournal.Answers.GetForDay(today)!;
        Assert.Equal(new Dictionary<long, int> { [mood] = 4, [sleep] = 2 }, merged.Values);
        Assert.Equal(_laptop.Clock.Now, merged.AnsweredAt);
    }

    [Fact]
    public void Older_answers_from_elsewhere_dont_override_but_fill_gaps()
    {
        var today = _home.Clock.Today();
        _laptop.Clock.Advance(TimeSpan.FromHours(1));
        _laptopJournal.Answers.SaveForDay(today, null,
            new Dictionary<long, int> { [V(_laptopJournal, "mood")] = 1, [V(_laptopJournal, "sleep")] = 5 }, _laptop.Clock.Now);
        _home.Clock.Advance(TimeSpan.FromHours(2));
        _homeJournal.Answers.SaveForDay(today, null, new Dictionary<long, int> { [V(_homeJournal, "mood")] = 4 }, _home.Clock.Now);

        MergeLaptopIntoHome();
        var merged = _homeJournal.Answers.GetForDay(today)!;
        Assert.Equal(4, merged.Values[V(_homeJournal, "mood")]);
        Assert.Equal(5, merged.Values[V(_homeJournal, "sleep")]);
    }

    [Fact]
    public void Merging_only_adds_so_a_deleted_entry_returns()
    {
        var session = new EntrySession(_homeJournal);
        session.Open(Shared(_homeJournal).Id);
        session.DeleteCurrent();

        Assert.Equal(1, MergeLaptopIntoHome().NewEntries);
        Assert.Equal("Shared", Shared(_homeJournal).Summary);
    }

    [Fact]
    public void Json_merge_matches_backup_merge_and_a_safety_copy_is_made()
    {
        _laptop.Clock.Advance(TimeSpan.FromHours(1));
        Write(_laptopJournal, "", "From JSON");
        var json = _laptop.TempFile("laptop.json");
        Directory.CreateDirectory(Path.GetDirectoryName(json)!);
        JournalTransfer.ExportJson(_laptopJournal, json);

        var safety = JournalMerger.DefaultSafetyCopyPath(_homeJournal);
        Assert.NotNull(safety);
        var summary = JournalMerger.Apply(_homeJournal, JournalMerger.Plan(_homeJournal, JournalTransfer.ReadJson(json)), safety);

        Assert.Equal(1, summary.NewEntries);
        Assert.True(File.Exists(safety));
        Assert.Equal(1, JournalTransfer.InspectBackup(safety!, TestEnv.Password).EntryCount);
    }

    [Fact]
    public void Wrong_backup_password_is_rejected()
    {
        var path = _laptop.TempFile("laptop.xjbackup");
        JournalTransfer.ExportEncrypted(_laptopJournal, path);
        Assert.Throws<InvalidPasswordException>(() => JournalTransfer.ReadBackup(path, "nope nope"));
    }
}

public class RevisionTests
{
    [Fact]
    public void Restoring_a_previous_version_keeps_the_replaced_text()
    {
        using var env = new TestEnv();
        using var journal = env.CreateJournal();
        var session = new EntrySession(journal);
        session.SetText("", "Version A");
        session.Save();
        journal.Entries.AddRevision(session.Current.Id, "", "Version 0", env.Clock.Now.AddDays(-1), env.Clock.Now, EntryRevision.MergeReason);
        env.Clock.Advance(TimeSpan.FromMinutes(5));
        session.SetText("", "Version B");

        var revision = Assert.Single(journal.Entries.GetRevisions(session.Current.Id));
        session.RestoreRevision(revision.Id);

        Assert.Equal("Version 0", session.Current.Body);
        Assert.Equal("Version 0", journal.Entries.Get(session.Current.Id)!.Body);
        var revisions = journal.Entries.GetRevisions(session.Current.Id);
        Assert.Equal(2, revisions.Count);
        Assert.Equal("Version B", revisions[0].Body); // unsaved edits were saved, then kept
        Assert.Equal(EntryRevision.RestoreReason, revisions[0].Reason);
    }

    [Fact]
    public void Revisions_roundtrip_through_json_and_are_deleted_with_their_entry()
    {
        using var home = new TestEnv();
        using var laptop = new TestEnv();
        using var journal = home.CreateJournal();
        var session = new EntrySession(journal);
        session.SetText("", "Now");
        session.Save();
        journal.Entries.AddRevision(session.Current.Id, "Then", "Before", home.Clock.Now.AddHours(-1), home.Clock.Now, EntryRevision.MergeReason);

        var path = home.TempFile("j.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        JournalTransfer.ExportJson(journal, path);
        Assert.Contains("\"revisions\"", File.ReadAllText(path));
        using var imported = new JournalTransfer(laptop.Accounts).ImportJson(path, "jordan", TestEnv.Password, false);
        var revision = Assert.Single(imported.Entries.GetRevisions(imported.Entries.GetLatest()!.Id));
        Assert.Equal("Before", revision.Body);
        Assert.Equal(home.Clock.Now.AddHours(-1), revision.UpdatedAt);

        session.DeleteCurrent();
        Assert.Empty(journal.Entries.GetAllRevisions());
    }

    [Fact]
    public void A_version_1_journal_is_upgraded_when_opened()
    {
        using var env = new TestEnv();
        using (var journal = env.CreateJournal())
        {
            journal.Database.Connection.Execute("DROP TABLE entry_revisions; PRAGMA user_version = 1;");
        }
        using var reopened = env.Accounts.SignIn("jordan", TestEnv.Password);
        Assert.Equal(2, reopened.Database.SchemaVersion);
        Assert.Empty(reopened.Entries.GetAllRevisions());
    }
}
