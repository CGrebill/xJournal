namespace XJournal.Core;

/// <summary>Source of "now" and the local time zone. Injected so time-based rules can be tested.</summary>
public interface IClock
{
    DateTimeOffset Now { get; }
    TimeZoneInfo TimeZone { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTimeOffset Now => DateTimeOffset.Now;
    public TimeZoneInfo TimeZone => TimeZoneInfo.Local;
}

public static class ClockExtensions
{
    /// <summary>The local calendar day (in the clock's time zone) for "now".</summary>
    public static DateOnly Today(this IClock clock) => clock.LocalDay(clock.Now);

    /// <summary>The local calendar day (in the clock's time zone) of an instant.</summary>
    public static DateOnly LocalDay(this IClock clock, DateTimeOffset instant) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, clock.TimeZone).DateTime);

    /// <summary>"Now" expressed in the clock's local offset.</summary>
    public static DateTimeOffset LocalNow(this IClock clock) => TimeZoneInfo.ConvertTime(clock.Now, clock.TimeZone);

    /// <summary>The UTC instant at which a local calendar day starts.</summary>
    public static DateTimeOffset StartOfDay(this IClock clock, DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        // Midnight can fall into a DST gap in a few zones; step forward until it is a real time.
        while (clock.TimeZone.IsInvalidTime(local)) local = local.AddMinutes(30);
        return new DateTimeOffset(local, clock.TimeZone.GetUtcOffset(local));
    }
}
