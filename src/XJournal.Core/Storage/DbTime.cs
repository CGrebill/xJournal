namespace XJournal.Core.Storage;

/// <summary>Instants are stored as (unix ms UTC, offset minutes) so they sort correctly and keep local time.</summary>
internal static class DbTime
{
    public static long Utc(DateTimeOffset value) => value.ToUnixTimeMilliseconds();

    public static int Offset(DateTimeOffset value) => (int)value.Offset.TotalMinutes;

    public static DateTimeOffset From(long utcMs, long offsetMinutes) =>
        DateTimeOffset.FromUnixTimeMilliseconds(utcMs).ToOffset(TimeSpan.FromMinutes(offsetMinutes));

    /// <summary>Truncates to millisecond precision so values round-trip through the database exactly.</summary>
    public static DateTimeOffset Truncate(DateTimeOffset value) => From(Utc(value), Offset(value));

    public static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public static DateOnly ParseDay(string day) =>
        DateOnly.ParseExact(day, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
