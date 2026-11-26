namespace XJournal.Core.Insights;

public sealed record WeekTotal(DateOnly WeekStart, int Entries, int Words);

/// <summary>Summary of writing habits, all based on local calendar days.</summary>
public sealed record WritingStats(
    int TotalEntries,
    int TotalWords,
    int CurrentStreak,
    int LongestStreak,
    int EntriesThisWeek,
    int WordsThisWeek,
    int EntriesThisMonth,
    int WordsThisMonth,
    IReadOnlyList<WeekTotal> RecentWeeks)
{
    public int AverageWords => TotalEntries == 0 ? 0 : (int)Math.Round(TotalWords / (double)TotalEntries);

    /// <param name="weeks">How many weeks (ending with the current one) to include in <see cref="RecentWeeks"/>.</param>
    public static WritingStats Compute(Journal journal, DayOfWeek firstDayOfWeek, int weeks = 12) =>
        Compute(journal.Entries.GetDayActivity(), journal.Clock.Today(), firstDayOfWeek, weeks);

    public static WritingStats Compute(IReadOnlyDictionary<DateOnly, Models.DayActivity> days, DateOnly today,
        DayOfWeek firstDayOfWeek, int weeks = 12)
    {
        // Current streak: consecutive days with an entry, ending today (or yesterday, if today isn't written yet).
        var current = 0;
        var cursor = days.ContainsKey(today) ? today : today.AddDays(-1);
        while (days.ContainsKey(cursor))
        {
            current++;
            cursor = cursor.AddDays(-1);
        }

        var longest = 0;
        var run = 0;
        DateOnly? previous = null;
        foreach (var day in days.Keys.Order())
        {
            run = previous is { } p && p.AddDays(1) == day ? run + 1 : 1;
            longest = Math.Max(longest, run);
            previous = day;
        }

        var weekStart = today.AddDays(-(((int)today.DayOfWeek - (int)firstDayOfWeek + 7) % 7));
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var recent = Enumerable.Range(0, weeks)
            .Select(i => weekStart.AddDays(-7 * (weeks - 1 - i)))
            .Select(start =>
            {
                var inWeek = days.Values.Where(d => d.Day >= start && d.Day < start.AddDays(7)).ToList();
                return new WeekTotal(start, inWeek.Sum(d => d.Entries), inWeek.Sum(d => d.Words));
            })
            .ToList();

        var thisWeek = days.Values.Where(d => d.Day >= weekStart && d.Day <= today).ToList();
        var thisMonth = days.Values.Where(d => d.Day >= monthStart && d.Day <= today).ToList();
        return new WritingStats(
            days.Values.Sum(d => d.Entries),
            days.Values.Sum(d => d.Words),
            current,
            longest,
            thisWeek.Sum(d => d.Entries),
            thisWeek.Sum(d => d.Words),
            thisMonth.Sum(d => d.Entries),
            thisMonth.Sum(d => d.Words),
            recent);
    }
}
