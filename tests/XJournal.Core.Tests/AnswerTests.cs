using XJournal.Core.Models;
using XJournal.Core.Questions;

namespace XJournal.Core.Tests;

public class AnswerTests
{
    private static long[] Setup(Journal journal)
    {
        var parsed = QuestionFileParser.Parse("""{"questions":[{"id":"a","type":"rating","text":"A"},{"id":"b","type":"rating","text":"B"}]}""");
        journal.QuestionImporter.Apply(journal.QuestionImporter.Plan(parsed.Questions));
        return journal.Questions.GetAll().Select(q => q.Current.Id).ToArray();
    }

    [Fact]
    public void One_answer_set_per_day_is_replaced_when_edited()
    {
        using var env = new TestEnv();
        using var journal = env.CreateJournal();
        var v = Setup(journal);
        var today = env.Clock.Today();

        Assert.False(journal.Answers.HasAnswered(today));
        journal.Answers.SaveForDay(today, null, new Dictionary<long, AnswerValue> { [v[0]] = AnswerValue.Of(1), [v[1]] = AnswerValue.Of(2) }, env.Clock.Now);
        env.Clock.Advance(TimeSpan.FromHours(2));
        journal.Answers.SaveForDay(today, null, new Dictionary<long, AnswerValue> { [v[0]] = AnswerValue.Of(5) }, env.Clock.Now);

        var set = journal.Answers.GetForDay(today)!;
        Assert.True(journal.Answers.HasAnswered(today));
        Assert.Single(journal.Answers.GetAll());
        Assert.Equal(new Dictionary<long, AnswerValue> { [v[0]] = AnswerValue.Of(5) }, set.Values);
        Assert.Equal(env.Clock.Now, set.AnsweredAt);
    }

    [Fact]
    public void Entry_link_is_kept_once_set()
    {
        using var env = new TestEnv();
        using var journal = env.CreateJournal();
        var v = Setup(journal);
        var session = new Sessions.EntrySession(journal);
        session.SetText("", "one");
        session.StartNew();
        var first = journal.Entries.GetLatest()!.Id;
        journal.Answers.SaveForDay(env.Clock.Today(), first, new Dictionary<long, AnswerValue> { [v[0]] = AnswerValue.Of(3) }, env.Clock.Now);
        journal.Answers.SaveForDay(env.Clock.Today(), 999, new Dictionary<long, AnswerValue> { [v[0]] = AnswerValue.Of(4) }, env.Clock.Now);
        Assert.Equal(first, journal.Answers.GetForDay(env.Clock.Today())!.EntryId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void Values_outside_1_to_5_are_rejected(int value)
    {
        using var env = new TestEnv();
        using var journal = env.CreateJournal();
        var v = Setup(journal);
        Assert.Throws<ArgumentException>(() =>
            journal.Answers.SaveForDay(env.Clock.Today(), null, new Dictionary<long, AnswerValue> { [v[0]] = AnswerValue.Of(value) }, env.Clock.Now));
    }

    private static Dictionary<string, QuestionVersion> SetupAllTypes(Journal journal)
    {
        var parsed = QuestionFileParser.Parse(QuestionTests.AllTypes);
        journal.QuestionImporter.Apply(journal.QuestionImporter.Plan(parsed.Questions));
        return journal.Questions.GetAll().ToDictionary(q => q.Id, q => q.Current);
    }

    [Fact]
    public void Every_type_saves_and_reads_back()
    {
        using var env = new TestEnv();
        using var journal = env.CreateJournal();
        var q = SetupAllTypes(journal);
        var answers = new Dictionary<long, AnswerValue>
        {
            [q["day"].Id] = AnswerValue.Of(7),
            [q["mood"].Id] = AnswerValue.Of(4),
            [q["gym"].Id] = AnswerValue.OfBool(true),
            [q["did"].Id] = AnswerValue.OfChoices(["Work", "Rest"]),
            [q["where"].Id] = AnswerValue.OfChoices(["Home"]),
            [q["hours"].Id] = AnswerValue.Of(7.5),
            [q["good"].Id] = AnswerValue.OfText("Coffee with an old friend"),
        };
        journal.Answers.SaveForDay(env.Clock.Today(), null, answers, env.Clock.Now);

        var saved = journal.Answers.GetForDay(env.Clock.Today())!.Values;
        Assert.Equal(answers, saved);
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        Assert.Equal("7 / 10", AnswerRules.Format(q["day"], saved[q["day"].Id], culture));
        Assert.Equal("4 – Somewhat agree", AnswerRules.Format(q["mood"], saved[q["mood"].Id], culture));
        Assert.Equal("Yes", AnswerRules.Format(q["gym"], saved[q["gym"].Id], culture));
        Assert.Equal("Work, Rest", AnswerRules.Format(q["did"], saved[q["did"].Id], culture));
        Assert.Equal("7.5 hours", AnswerRules.Format(q["hours"], saved[q["hours"].Id], culture));
        Assert.Equal("Coffee with an old friend", AnswerRules.Format(q["good"], saved[q["good"].Id], culture));
    }

    public static TheoryData<string, AnswerValue> InvalidAnswers => new()
    {
        { "day", AnswerValue.Of(11) },
        { "day", AnswerValue.Of(6.5) },
        { "day", AnswerValue.OfText("7") },
        { "gym", AnswerValue.Of(2) },
        { "did", AnswerValue.OfChoices(["Sleep"]) },
        { "did", AnswerValue.OfChoices([]) },
        { "did", AnswerValue.OfChoices(["Work", "Work"]) },
        { "where", AnswerValue.OfChoices(["Home", "Away"]) },
        { "hours", AnswerValue.Of(double.NaN) },
        { "hours", AnswerValue.OfText("lots") },
        { "good", AnswerValue.OfText("   ") },
        { "good", AnswerValue.OfText(new string('x', AnswerRules.MaxTextAnswerLength + 1)) },
        { "good", AnswerValue.Of(3) },
    };

    [Theory]
    [MemberData(nameof(InvalidAnswers))]
    public void Answers_that_dont_fit_the_question_are_rejected(string question, AnswerValue answer)
    {
        using var env = new TestEnv();
        using var journal = env.CreateJournal();
        var q = SetupAllTypes(journal);
        Assert.Throws<ArgumentException>(() =>
            journal.Answers.SaveForDay(env.Clock.Today(), null, new Dictionary<long, AnswerValue> { [q[question].Id] = answer }, env.Clock.Now));
        Assert.False(journal.Answers.HasAnswered(env.Clock.Today()));
    }

    [Fact]
    public void History_is_ordered_and_filterable_by_day()
    {
        using var env = new TestEnv();
        using var journal = env.CreateJournal();
        var v = Setup(journal);
        var start = env.Clock.Today();
        for (var i = 0; i < 5; i++)
        {
            journal.Answers.SaveForDay(start.AddDays(i), null, new Dictionary<long, AnswerValue> { [v[0]] = AnswerValue.Of(i + 1) }, env.Clock.Now);
        }

        Assert.Equal([1.0, 2, 3, 4, 5], journal.Answers.GetHistory("a").Select(p => p.Value.Number!.Value));
        Assert.Equal([2.0, 3], journal.Answers.GetHistory("a", start.AddDays(1), start.AddDays(2)).Select(p => p.Value.Number!.Value));
        Assert.Empty(journal.Answers.GetHistory("b"));
    }
}
