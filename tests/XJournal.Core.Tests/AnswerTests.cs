using XJournal.Core.Questions;

namespace XJournal.Core.Tests;

public class AnswerTests
{
    private static long[] Setup(Journal journal)
    {
        var parsed = QuestionFileParser.Parse("""{"questions":[{"id":"a","text":"A"},{"id":"b","text":"B"}]}""");
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
        journal.Answers.SaveForDay(today, null, new Dictionary<long, int> { [v[0]] = 1, [v[1]] = 2 }, env.Clock.Now);
        env.Clock.Advance(TimeSpan.FromHours(2));
        journal.Answers.SaveForDay(today, null, new Dictionary<long, int> { [v[0]] = 5 }, env.Clock.Now);

        var set = journal.Answers.GetForDay(today)!;
        Assert.True(journal.Answers.HasAnswered(today));
        Assert.Single(journal.Answers.GetAll());
        Assert.Equal(new Dictionary<long, int> { [v[0]] = 5 }, set.Values);
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
        journal.Answers.SaveForDay(env.Clock.Today(), first, new Dictionary<long, int> { [v[0]] = 3 }, env.Clock.Now);
        journal.Answers.SaveForDay(env.Clock.Today(), 999, new Dictionary<long, int> { [v[0]] = 4 }, env.Clock.Now);
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
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            journal.Answers.SaveForDay(env.Clock.Today(), null, new Dictionary<long, int> { [v[0]] = value }, env.Clock.Now));
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
            journal.Answers.SaveForDay(start.AddDays(i), null, new Dictionary<long, int> { [v[0]] = i + 1 }, env.Clock.Now);
        }

        Assert.Equal([1, 2, 3, 4, 5], journal.Answers.GetHistory("a").Select(p => p.Value));
        Assert.Equal([2, 3], journal.Answers.GetHistory("a", start.AddDays(1), start.AddDays(2)).Select(p => p.Value));
        Assert.Empty(journal.Answers.GetHistory("b"));
    }
}
