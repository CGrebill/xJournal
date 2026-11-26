using XJournal.Core.Models;

namespace XJournal.Core.Insights;

/// <summary>An entry written on today's date in an earlier month or year.</summary>
public sealed record Memory(JournalEntry Entry, DateOnly Day, string Ago);

public static class OnThisDay
{
    /// <summary>
    /// Entries written on the same date one month ago and on the same date in every earlier year,
    /// most recent first. Dates that don't exist that month or year (e.g. the 31st, or 29 February)
    /// are skipped rather than moved, so the same memory never shows on several days.
    /// </summary>
    public static IReadOnlyList<Memory> Find(Journal journal, DateOnly today)
    {
        var memories = new List<Memory>();
        if (SameDate(today.AddMonths(-1), today) is { } lastMonth)
        {
            foreach (var entry in journal.Entries.GetForDay(lastMonth))
                memories.Add(new Memory(entry, lastMonth, "1 month ago"));
        }

        if (journal.Entries.GetFirstDay() is not { } first) return memories;
        for (var years = 1; today.Year - years >= first.Year; years++)
        {
            if (SameDate(today.AddYears(-years), today) is not { } day) continue;
            foreach (var entry in journal.Entries.GetForDay(day))
                memories.Add(new Memory(entry, day, years == 1 ? "1 year ago" : $"{years} years ago"));
        }
        return memories;
    }

    // DateOnly.AddMonths/AddYears clamp (31 Mar - 1 month = 28 Feb); only keep exact date matches.
    private static DateOnly? SameDate(DateOnly candidate, DateOnly today) => candidate.Day == today.Day ? candidate : null;
}
