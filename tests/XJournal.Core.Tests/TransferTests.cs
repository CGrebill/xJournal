using XJournal.Core.Models;
using System.Text;
using XJournal.Core.Backup;
using XJournal.Core.Questions;
using XJournal.Core.Sessions;

namespace XJournal.Core.Tests;

public class TransferTests
{
    /// <summary>Fills a journal with entries, two question versions and answers.</summary>
    private static void Populate(Journal journal, FakeClock clock)
    {
        var importer = journal.QuestionImporter;
        importer.Apply(importer.Plan(QuestionFileParser.Parse("""{"questions":[{"id":"mood","type":"rating","text":"Good mood"},{"id":"old","type":"rating","text":"Old one"}]}""").Questions));
        var v1 = journal.Questions.Get("mood")!.Current.Id;

        var session = new EntrySession(journal);
        session.SetText("Day one", "SECRETWORD first entry ✨");
        session.Save();
        journal.Answers.SaveForDay(clock.Today(), session.Current.Id, new Dictionary<long, AnswerValue> { [v1] = AnswerValue.Of(4) }, clock.Now);
        session.Leave();

        clock.Advance(TimeSpan.FromDays(1));
        importer.Apply(importer.Plan(QuestionFileParser.Parse("""{"questions":[{"id":"mood","type":"rating","text":"I was in a good mood"}]}""").Questions));
        var v2 = journal.Questions.Get("mood")!.Current.Id;
        journal.Answers.SaveForDay(clock.Today(), null, new Dictionary<long, AnswerValue> { [v2] = AnswerValue.Of(2) }, clock.Now);
        var second = new EntrySession(journal);
        second.SetText("", "Second entry");
        second.Leave();
        journal.Settings.ResumeWindow = TimeSpan.FromMinutes(45);
    }

    private static void AssertSameContent(Journal expected, Journal actual)
    {
        Assert.Equal(
            Normalize(JournalTransfer.BuildExport(expected)),
            Normalize(JournalTransfer.BuildExport(actual)));

        static string Normalize(JournalExport e)
        {
            e.ExportedAt = default;
            return System.Text.Json.JsonSerializer.Serialize(e, JournalExport.SerializerOptions);
        }
    }

    /// <summary>One question of every type, each answered today.</summary>
    internal static void PopulateAllTypes(Journal journal, FakeClock clock)
    {
        journal.QuestionImporter.Apply(journal.QuestionImporter.Plan(QuestionFileParser.Parse(QuestionTests.AllTypes).Questions, archiveMissing: false));
        var q = journal.Questions.GetAll().ToDictionary(x => x.Id, x => x.Current.Id);
        journal.Answers.SaveForDay(clock.Today(), null, new Dictionary<long, AnswerValue>
        {
            [q["day"]] = AnswerValue.Of(8),
            [q["gym"]] = AnswerValue.OfBool(false),
            [q["did"]] = AnswerValue.OfChoices(["Gym", "Rest"]),
            [q["hours"]] = AnswerValue.Of(6.25),
            [q["good"]] = AnswerValue.OfText("Finished the \"big\" puzzle ✨"),
        }, clock.Now);
    }

    [Fact]
    public void Every_question_type_survives_json_export_and_encrypted_backup()
    {
        using var home = new TestEnv();
        using var laptop = new TestEnv();
        using var journal = home.CreateJournal();
        PopulateAllTypes(journal, home.Clock);

        var json = home.TempFile("journal.json");
        Directory.CreateDirectory(Path.GetDirectoryName(json)!);
        JournalTransfer.ExportJson(journal, json);
        Assert.Contains("\"type\": \"choice\"", File.ReadAllText(json));
        using (var fromJson = new JournalTransfer(laptop.Accounts).ImportJson(json, "jordan", "new password", false))
            AssertSameContent(journal, fromJson);

        var backup = home.TempFile("journal.xjbackup");
        JournalTransfer.ExportEncrypted(journal, backup);
        using var fromBackup = new JournalTransfer(laptop.Accounts).ImportBackup(backup, TestEnv.Password, "sam", null, false);
        AssertSameContent(journal, fromBackup);
        Assert.Equal(["Gym", "Rest"], fromBackup.Answers.GetHistory("did").Single().Value.Choices);
    }

    [Fact]
    public void Version_1_json_exports_still_import()
    {
        using var env = new TestEnv();
        var path = env.TempFile("old.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            { "format": "xjournal-export", "formatVersion": 1, "exportedAt": "2026-11-26T10:00:00-05:00",
              "entries": [],
              "questions": [{"id": "mood", "sortOrder": 0, "archived": false, "currentVersion": 3}],
              "questionVersions": [{"key": 3, "questionId": "mood", "text": "Good mood",
                "scale": ["Entirely disagree", "Somewhat disagree", "Neither agree nor disagree", "Somewhat agree", "Entirely agree"],
                "createdAt": "2026-11-20T10:00:00-05:00"}],
              "answerSets": [{"day": "2026-11-25", "answeredAt": "2026-11-25T20:00:00-05:00",
                "answers": [{"questionId": "mood", "version": 3, "value": 4}]}] }
            """);

        using var journal = new JournalTransfer(env.Accounts).ImportJson(path, "jordan", TestEnv.Password, false);
        var mood = journal.Questions.Get("mood")!.Current;
        Assert.Equal(QuestionType.Rating, mood.Type);
        Assert.Equal("Somewhat agree", mood.Labels[3]);
        Assert.Equal(AnswerValue.Of(4), journal.Answers.GetHistory("mood").Single().Value);
    }

    [Fact]
    public void Encrypted_backup_moves_everything_to_another_computer()
    {
        using var home = new TestEnv();
        using var laptop = new TestEnv();
        using var journal = home.CreateJournal();
        Populate(journal, home.Clock);
        var backup = home.TempFile("journal.xjbackup");
        JournalTransfer.ExportEncrypted(journal, backup);

        Assert.DoesNotContain("SECRETWORD", Encoding.Latin1.GetString(File.ReadAllBytes(backup)));
        var summary = JournalTransfer.InspectBackup(backup, TestEnv.Password);
        Assert.Equal(2, summary.EntryCount);
        Assert.Equal(2, summary.AnsweredDays);

        using var imported = new JournalTransfer(laptop.Accounts)
            .ImportBackup(backup, TestEnv.Password, "jordan", newPassword: null, replaceExisting: false);
        AssertSameContent(journal, imported);
        Assert.Equal("Good mood", imported.Questions.GetVersion(imported.Answers.GetHistory("mood")[0].VersionId)!.Text);
    }

    [Fact]
    public void Backup_can_use_its_own_password_and_rejects_wrong_ones()
    {
        using var home = new TestEnv();
        using var laptop = new TestEnv();
        using var journal = home.CreateJournal();
        Populate(journal, home.Clock);
        var backup = home.TempFile("journal.xjbackup");
        JournalTransfer.ExportEncrypted(journal, backup, "backup password");

        Assert.Throws<InvalidPasswordException>(() => JournalTransfer.InspectBackup(backup, TestEnv.Password));
        var transfer = new JournalTransfer(laptop.Accounts);
        Assert.Throws<InvalidPasswordException>(() => transfer.ImportBackup(backup, TestEnv.Password, "jordan", null, false));
        Assert.Empty(laptop.Accounts.ListAccounts());

        using (var imported = transfer.ImportBackup(backup, "backup password", "jordan", "laptop password", false))
            AssertSameContent(journal, imported);
        laptop.Accounts.SignIn("jordan", "laptop password").Dispose();
    }

    [Fact]
    public void Replacing_an_account_keeps_a_safety_copy()
    {
        using var env = new TestEnv();
        string backup;
        using (var journal = env.CreateJournal("source"))
        {
            Populate(journal, env.Clock);
            backup = env.TempFile("b.xjbackup");
            JournalTransfer.ExportEncrypted(journal, backup);
        }
        env.CreateJournal("jordan").Dispose();

        var transfer = new JournalTransfer(env.Accounts);
        Assert.Throws<AccountException>(() => transfer.ImportBackup(backup, TestEnv.Password, "jordan", null, false));
        using var imported = transfer.ImportBackup(backup, TestEnv.Password, "jordan", null, replaceExisting: true);

        Assert.Equal(2, imported.Entries.Count());
        Assert.Single(Directory.GetFiles(env.Accounts.SafetyBackupsDirectory));
    }

    [Fact]
    public void Json_export_roundtrips()
    {
        using var home = new TestEnv();
        using var laptop = new TestEnv();
        using var journal = home.CreateJournal();
        Populate(journal, home.Clock);
        var path = home.TempFile("journal.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        JournalTransfer.ExportJson(journal, path);

        var json = File.ReadAllText(path);
        Assert.Contains("SECRETWORD first entry ✨", json);
        Assert.Contains("\"text\": \"Good mood\"", json);
        Assert.Equal(2, JournalTransfer.InspectJson(path).EntryCount);

        using var imported = new JournalTransfer(laptop.Accounts).ImportJson(path, "jordan", "new password", false);
        AssertSameContent(journal, imported);
        Assert.Single(imported.Entries.Search(new("secretword")));
    }

    [Fact]
    public void Broken_json_export_is_rejected_with_reasons()
    {
        using var env = new TestEnv();
        var path = env.TempFile("bad.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            { "format": "xjournal-export", "formatVersion": 1,
              "entries": [], "questions": [{"id": "q", "currentVersion": 7}], "questionVersions": [],
              "answerSets": [{"day": "nope", "answers": [{"version": 3, "value": 9}]}] }
            """);
        var ex = Assert.Throws<JournalException>(() => JournalTransfer.InspectJson(path));
        Assert.Contains("invalid current version", ex.Message);
        Assert.Contains("unknown question version", ex.Message);

        File.WriteAllText(path, """{ "hello": 1 }""");
        Assert.Throws<NotAJournalException>(() => JournalTransfer.InspectJson(path));
    }

    [Fact]
    public void Automatic_backups_keep_only_the_newest()
    {
        using var env = new TestEnv();
        using var journal = env.CreateJournal();
        Assert.Null(JournalTransfer.RunAutoBackup(journal));

        var folder = env.TempFile("cloud");
        journal.Settings.AutoBackupFolder = folder;
        journal.Settings.AutoBackupKeep = 3;
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(folder).FullName, "unrelated.txt"), "keep me");
        for (var i = 0; i < 5; i++)
        {
            Assert.NotNull(JournalTransfer.RunAutoBackup(journal));
            env.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        var backups = Directory.GetFiles(folder, "*.xjbackup").Select(Path.GetFileName).Order().ToList();
        Assert.Equal(3, backups.Count);
        Assert.EndsWith("143215.xjbackup", backups[0]); // the two oldest (14:30, 14:31) were pruned
        Assert.True(File.Exists(Path.Combine(folder, "unrelated.txt")));
        Assert.Equal(0, JournalTransfer.InspectBackup(Path.Combine(folder, backups[^1]!), TestEnv.Password).EntryCount);
    }
}
