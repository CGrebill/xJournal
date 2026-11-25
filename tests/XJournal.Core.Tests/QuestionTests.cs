using Dapper;
using Microsoft.Data.Sqlite;
using XJournal.Core.Questions;

namespace XJournal.Core.Tests;

public class QuestionTests
{
    private const string Sample = """
        {
          // comments and trailing commas are allowed, it's meant to be hand-edited
          "version": 1,
          "questions": [
            { "id": "energy", "text": "I felt energetic today." },
            { "id": "calm", "text": "I felt calm.",
              "scale": ["Not at all", "A little", "Somewhat", "Mostly", "Completely"] },
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
            version = 1,
            questions = questions.Select(q => new { id = q.Id, text = q.Text }),
        });

    [Fact]
    public void Parses_default_and_custom_scales()
    {
        var questions = Parse(Sample);
        Assert.Equal(2, questions.Count);
        Assert.Equal(QuestionFile.DefaultScale, questions[0].Scale);
        Assert.Equal("Completely", questions[1].Scale[4]);
    }

    [Theory]
    [InlineData("{ not json", "valid JSON")]
    [InlineData("""{"questions": []}""", "no questions")]
    [InlineData("""{"questions": [{"id": "a"}]}""", "missing \"text\"")]
    [InlineData("""{"questions": [{"text": "x"}]}""", "missing \"id\"")]
    [InlineData("""{"questions": [{"id": "a b", "text": "x"}]}""", "may only contain")]
    [InlineData("""{"questions": [{"id": "a", "text": "x"}, {"id": "A", "text": "y"}]}""", "duplicate")]
    [InlineData("""{"questions": [{"id": "a", "text": "x", "scale": ["1","2","3"]}]}""", "exactly 5")]
    [InlineData("""{"version": 2, "questions": [{"id": "a", "text": "x"}]}""", "Unsupported")]
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
        journal.Answers.SaveForDay(env.Clock.Today(), null, new Dictionary<long, int> { [original.Id] = 4 }, env.Clock.Now);

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
        journal.Answers.SaveForDay(env.Clock.Today(), null, new Dictionary<long, int> { [calm.Id] = 2 }, env.Clock.Now);

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
}
