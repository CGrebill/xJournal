using XJournal.Core.Models;
using Dapper;
using Microsoft.Data.Sqlite;
using XJournal.Core.Questions;

namespace XJournal.Core.Tests;

public class QuestionTests
{
    private const string Sample = """
        {
          // comments and trailing commas are allowed, it's meant to be hand-edited
          "version": 2,
          "questions": [
            { "id": "energy", "type": "rating", "text": "I felt energetic today." },
            { "id": "calm", "type": "rating", "text": "I felt calm.",
              "labels": ["Not at all", "A little", "Somewhat", "Mostly", "Completely"] },
          ]
        }
        """;

    /// <summary>One question of every type.</summary>
    internal const string AllTypes = """
        {
          "version": 2,
          "questions": [
            { "id": "day", "type": "scale", "text": "How was today?", "low": "Awful", "high": "Great" },
            { "id": "mood", "type": "rating", "text": "Good mood." },
            { "id": "gym", "type": "yesno", "text": "Did I exercise?" },
            { "id": "did", "type": "choice", "text": "What did I do?", "options": ["Work", "Gym", "Rest"], "multiple": true },
            { "id": "where", "type": "choice", "text": "Where was I?", "options": ["Home", "Away"] },
            { "id": "hours", "type": "number", "text": "Hours slept", "unit": "hours" },
            { "id": "good", "type": "text", "text": "One good thing" },
          ]
        }
        """;

    private static IReadOnlyList<ValidQuestion> Parse(string json)
    {
        var result = QuestionFileParser.Parse(json);
        Assert.True(result.IsValid, string.Join("\n", result.Errors));
        return result.Questions;
    }

    private static void Import(Journal journal, string json, bool archiveMissing = true) =>
        journal.QuestionImporter.Apply(journal.QuestionImporter.Plan(Parse(json), archiveMissing));

    private static string File(params (string Id, string Text)[] questions) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            version = 2,
            questions = questions.Select(q => new { id = q.Id, type = "rating", text = q.Text }),
        });

    [Fact]
    public void Parses_default_and_custom_rating_labels()
    {
        var questions = Parse(Sample);
        Assert.Equal(2, questions.Count);
        Assert.Equal(QuestionFile.DefaultLabels, questions[0].Config.Labels);
        Assert.Equal("Completely", questions[1].Config.Labels![4]);
    }

    [Fact]
    public void Parses_every_type_with_its_settings()
    {
        var q = Parse(AllTypes).ToDictionary(x => x.Id);
        Assert.Equal(QuestionType.Scale, q["day"].Type);
        Assert.Equal((1, 10, "Awful", "Great"), (q["day"].Config.Min, q["day"].Config.Max, q["day"].Config.Low, q["day"].Config.High));
        Assert.Equal(QuestionType.YesNo, q["gym"].Type);
        Assert.Equal(["Work", "Gym", "Rest"], q["did"].Config.Options!);
        Assert.True(q["did"].Config.AllowsMultiple);
        Assert.False(q["where"].Config.AllowsMultiple);
        Assert.Equal("hours", q["hours"].Config.Unit);
        Assert.Equal(QuestionType.Text, q["good"].Type);
    }

    [Theory]
    [InlineData("{ not json", "valid JSON")]
    [InlineData("""{"version": 2, "questions": []}""", "no questions")]
    [InlineData("""{"questions": [{"id": "a", "type": "yesno"}]}""", "missing \"text\"")]
    [InlineData("""{"questions": [{"type": "yesno", "text": "x"}]}""", "missing \"id\"")]
    [InlineData("""{"questions": [{"id": "a b", "type": "yesno", "text": "x"}]}""", "may only contain")]
    [InlineData("""{"questions": [{"id": "a", "type": "yesno", "text": "x"}, {"id": "A", "type": "yesno", "text": "y"}]}""", "duplicate")]
    [InlineData("""{"questions": [{"id": "a", "text": "x"}]}""", "missing \"type\"")]
    [InlineData("""{"questions": [{"id": "a", "type": "slider", "text": "x"}]}""", "unknown \"type\"")]
    [InlineData("""{"questions": [{"id": "a", "type": "rating", "text": "x", "labels": ["only one"]}]}""", "2–7 entries")]
    [InlineData("""{"questions": [{"id": "a", "type": "scale", "text": "x", "min": 5, "max": 3}]}""", "lower to a higher")]
    [InlineData("""{"questions": [{"id": "a", "type": "scale", "text": "x", "max": 100}]}""", "within 0–10")]
    [InlineData("""{"questions": [{"id": "a", "type": "choice", "text": "x"}]}""", "needs \"options\"")]
    [InlineData("""{"questions": [{"id": "a", "type": "choice", "text": "x", "options": ["A", "a"]}]}""", "same entry twice")]
    [InlineData("""{"questions": [{"id": "a", "type": "yesno", "text": "x", "options": ["A", "B"]}]}""", "doesn't apply")]
    [InlineData("""{"questions": [{"id": "a", "type": "rating", "text": "x", "scale": ["1","2","3","4","5"]}]}""", "version 1")]
    [InlineData("""{"version": 1, "questions": [{"id": "a", "text": "x"}]}""", "older (version 1)")]
    [InlineData("""{"version": 3, "questions": [{"id": "a", "type": "yesno", "text": "x"}]}""", "Unsupported")]
    public void Reports_problems(string json, string expected)
    {
        var result = QuestionFileParser.Parse(json);
        Assert.False(result.IsValid);
        Assert.Empty(result.Questions);
        Assert.Contains(result.Errors, e => e.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Imports_questions_in_file_order()
    {
        using var env = new TestEnv();
        using var journal = env.CreateJournal();
        Import(journal, Sample);

        var questions = journal.Questions.GetAll();
        Assert.Equal(["energy", "calm"], questions.Select(q => q.Id));
        Assert.Equal("I felt calm.", questions[1].Current.Text);
    }

    [Fact]
    public void Reimporting_the_same_file_changes_nothing()
    {
        using var env = new TestEnv();
        using var journal = env.CreateJournal();
        Import(journal, Sample);
        var plan = journal.QuestionImporter.Plan(Parse(Sample));

        Assert.False(plan.HasChanges);
        Assert.Equal(2, plan.UnchangedCount);
    }

    [Fact]
    public void Rewording_adds_a_version_and_old_answers_keep_the_original_wording()
    {
        using var env = new TestEnv();
        using var journal = env.CreateJournal();
        Import(journal, File(("energy", "I felt energetic today.")));
        var original = journal.Questions.Get("energy")!.Current;
        journal.Answers.SaveForDay(env.Clock.Today(), null, new Dictionary<long, AnswerValue> { [original.Id] = AnswerValue.Of(4) }, env.Clock.Now);

        var plan = journal.QuestionImporter.Plan(Parse(File(("energy", "I had plenty of energy."))));
        var planned = Assert.Single(plan.Questions);
        Assert.Equal(QuestionChange.Changed, planned.Change);
        Assert.Equal("I felt energetic today.", planned.Previous!.Text);
        journal.QuestionImporter.Apply(plan);

        Assert.Equal("I had plenty of energy.", journal.Questions.Get("energy")!.Current.Text);
        var answer = Assert.Single(journal.Answers.GetHistory("energy"));
        Assert.Equal(original.Id, answer.VersionId);
        Assert.Equal("I felt energetic today.", journal.Questions.GetVersion(answer.VersionId)!.Text);
        Assert.Equal(2, journal.Questions.GetVersions("energy").Count);
    }

    [Fact]
    public void Reverting_wording_reuses_the_earlier_version()
    {
        using var env = new TestEnv();
        using var journal = env.CreateJournal();
        Import(journal, File(("energy", "A")));
        var first = journal.Questions.Get("energy")!.Current.Id;
        Import(journal, File(("energy", "B")));
        Import(journal, File(("energy", "A")));

        Assert.Equal(first, journal.Questions.Get("energy")!.Current.Id);
        Assert.Equal(2, journal.Questions.GetVersions("energy").Count);
    }

    [Fact]
    public void Questions_left_out_are_archived_with_answers_kept_and_can_return()
    {
        using var env = new TestEnv();
        using var journal = env.CreateJournal();
        Import(journal, Sample);
        var calm = journal.Questions.Get("calm")!.Current;
        journal.Answers.SaveForDay(env.Clock.Today(), null, new Dictionary<long, AnswerValue> { [calm.Id] = AnswerValue.Of(2) }, env.Clock.Now);

        var plan = journal.QuestionImporter.Plan(Parse(File(("energy", "I felt energetic today."))));
        Assert.Equal("calm", Assert.Single(plan.ToArchive).Id);
        journal.QuestionImporter.Apply(plan);

        Assert.Equal(["energy"], journal.Questions.GetAll().Select(q => q.Id));
        Assert.True(journal.Questions.Get("calm")!.Archived);
        Assert.Single(journal.Answers.GetHistory("calm"));

        var restore = journal.QuestionImporter.Plan(Parse(Sample));
        Assert.Equal(1, restore.RestoredCount);
        journal.QuestionImporter.Apply(restore);
        Assert.Equal(["energy", "calm"], journal.Questions.GetAll().Select(q => q.Id));
    }

    [Fact]
    public void Add_mode_keeps_existing_questions_and_appends()
    {
        using var env = new TestEnv();
        using var journal = env.CreateJournal();
        Import(journal, Sample);
        Import(journal, File(("sleep", "I slept well.")), archiveMissing: false);

        Assert.Equal(["energy", "calm", "sleep"], journal.Questions.GetAll().Select(q => q.Id));
    }

    [Fact]
    public void History_cannot_be_modified_or_deleted_even_directly()
    {
        using var env = new TestEnv();
        using var journal = env.CreateJournal();
        Import(journal, Sample);
        var connection = journal.Database.Connection;

        Assert.Throws<SqliteException>(() => connection.Execute("UPDATE question_versions SET text = 'hacked'"));
        Assert.Throws<SqliteException>(() => connection.Execute("DELETE FROM question_versions"));
        Assert.Throws<SqliteException>(() => connection.Execute("DELETE FROM questions"));
        Assert.Equal("I felt energetic today.", journal.Questions.Get("energy")!.Current.Text);
    }

    [Fact]
    public void Export_produces_a_reimportable_file()
    {
        using var env = new TestEnv();
        using var journal = env.CreateJournal();
        Import(journal, Sample);
        var json = journal.QuestionImporter.Export().ToJson();

        var plan = journal.QuestionImporter.Plan(Parse(json));
        Assert.False(plan.HasChanges);
    }

    [Fact]
    public void Every_type_exports_and_reimports_unchanged()
    {
        using var env = new TestEnv();
        using var journal = env.CreateJournal();
        Import(journal, AllTypes);
        var json = journal.QuestionImporter.Export().ToJson();

        Assert.Contains("\"type\": \"choice\"", json);
        var plan = journal.QuestionImporter.Plan(Parse(json));
        Assert.False(plan.HasChanges);
        Assert.Equal(QuestionType.Number, journal.Questions.Get("hours")!.Current.Type);
        Assert.Equal("hours", journal.Questions.Get("hours")!.Current.Config.Unit);
    }

    [Fact]
    public void A_question_cannot_change_type()
    {
        using var env = new TestEnv();
        using var journal = env.CreateJournal();
        Import(journal, AllTypes);

        var plan = journal.QuestionImporter.Plan(Parse("""{"version": 2, "questions": [{"id": "gym", "type": "scale", "text": "Did I exercise?"}]}"""));
        Assert.Contains(plan.Errors, e => e.Contains("can't change type", StringComparison.Ordinal));
        Assert.Throws<JournalException>(() => journal.QuestionImporter.Apply(plan));
        Assert.Equal(QuestionType.YesNo, journal.Questions.Get("gym")!.Current.Type);
    }

    [Fact]
    public void Changing_options_adds_a_version_and_keeps_old_answers()
    {
        using var env = new TestEnv();
        using var journal = env.CreateJournal();
        Import(journal, AllTypes);
        var before = journal.Questions.Get("did")!.Current;
        journal.Answers.SaveForDay(env.Clock.Today(), null,
            new Dictionary<long, AnswerValue> { [before.Id] = AnswerValue.OfChoices(["Work", "Rest"]) }, env.Clock.Now);

        var plan = journal.QuestionImporter.Plan(Parse(
            """{"version": 2, "questions": [{"id": "did", "type": "choice", "text": "What did I do?", "options": ["Work", "Gym", "Rest", "Friends"], "multiple": true}]}"""),
            archiveMissing: false);
        Assert.Equal(QuestionChange.Changed, plan.Questions[0].Change);
        journal.QuestionImporter.Apply(plan);

        var after = journal.Questions.Get("did")!.Current;
        Assert.NotEqual(before.Id, after.Id);
        Assert.Equal(4, after.Options.Count);
        var answer = Assert.Single(journal.Answers.GetHistory("did"));
        Assert.Equal(before.Id, answer.VersionId);
        Assert.Equal(["Work", "Rest"], answer.Value.Choices);
    }

    [Fact]
    public void Old_journals_upgrade_with_their_questions_and_answers_intact()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        XJournal.Core.Storage.Schema.Migrate(connection, 2); // a journal from 1.0.x

        string[] labels = ["Never", "Rarely", "Sometimes", "Often", "Always"];
        // The hash as 1.0.x computed it: text + labels.
        var legacyHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            System.Text.Json.JsonSerializer.Serialize(new object[] { "I slept well.", labels })))).ToLowerInvariant();
        connection.Execute("INSERT INTO questions (id, sort_order, archived) VALUES ('sleep', 0, 0)");
        connection.Execute(
            "INSERT INTO question_versions (question_id, text, scale_json, content_hash, created_utc, created_offset) VALUES ('sleep', 'I slept well.', $scale, $hash, 0, 0)",
            new { scale = System.Text.Json.JsonSerializer.Serialize(labels), hash = legacyHash });
        connection.Execute("UPDATE questions SET current_version_id = 1");
        connection.Execute("INSERT INTO answer_sets (day, answered_utc, answered_offset) VALUES ('2026-11-20', 0, 0)");
        connection.Execute("INSERT INTO answers (answer_set_id, question_version_id, value) VALUES (1, 1, 4)");

        XJournal.Core.Storage.Schema.Migrate(connection);

        var (type, config) = connection.QuerySingle<(string, string)>("SELECT type, config_json FROM question_versions");
        Assert.Equal("rating", type);
        Assert.Equal(labels, QuestionConfig.FromJson(config).Labels);
        Assert.Equal(4.0, connection.ExecuteScalar<double>("SELECT value_num FROM answers"));
        // Re-importing the same question matches the existing version, so no needless new version.
        Assert.Equal(legacyHash, QuestionImporter.ContentHash("I slept well.", QuestionType.Rating, new QuestionConfig { Labels = labels }));
        // Versions are still protected after the upgrade.
        Assert.Throws<SqliteException>(() => connection.Execute("UPDATE question_versions SET text = 'x'"));
    }
}
