namespace DataScope.Core.ScanJobs;

public enum ScanFrequency
{
    Off,
    Daily,
    Weekly,

    /// <summary>Every few hours (D121).</summary>
    Hourly,
}

/// <summary>
/// When a server is scanned on its own (D116, D121). Times are in the Data Scope server's time zone.
/// </summary>
/// <param name="StartHour">The hour of a daily or weekly scan; for scans every few hours, the first of the day.</param>
/// <param name="Day">The day of a weekly scan; null otherwise.</param>
/// <param name="StopHour">
/// When a scheduled scan that is still running pauses, to continue at the next scheduled time. Null lets it run to the end.
/// For scans every few hours it also ends the day's scans: none start from then until the start hour.
/// </param>
/// <param name="StartMinute">The minute past the hour each scan starts.</param>
/// <param name="EveryHours">For scans every few hours, how many hours apart; one of <see cref="ScanSchedule.HourSteps"/>.</param>
public sealed record ScanScheduleSettings(ScanFrequency Frequency, int StartHour, DayOfWeek? Day, int? StopHour, int StartMinute = 0, int? EveryHours = null)
{
    public static readonly ScanScheduleSettings Off = new(ScanFrequency.Off, 1, null, null);

    /// <summary>What a newly added server gets: every night at 1 AM, pausing at 7 AM if it hasn't finished.</summary>
    public static readonly ScanScheduleSettings NewServer = new(ScanFrequency.Daily, 1, null, 7);
}

public enum FullScanFrequency
{
    Off,
    Weekly,

    /// <summary>On the first of the chosen weekday each month.</summary>
    Monthly,
}

/// <summary>
/// When a server gets a full scan (D130): each week on <paramref name="Day"/>, or on the first <paramref name="Day"/> of
/// each month. It starts at the server's scan start time and takes the place of that time's quick scan.
/// </summary>
public sealed record FullScanSettings(FullScanFrequency Frequency, DayOfWeek? Day)
{
    public static readonly FullScanSettings Off = new(FullScanFrequency.Off, null);

    /// <summary>What a newly added server gets: a full scan every Saturday.</summary>
    public static readonly FullScanSettings NewServer = new(FullScanFrequency.Weekly, DayOfWeek.Saturday);
}

public static class ScanSchedule
{
    /// <summary>Recorded as who asked for a scheduled scan.</summary>
    public const string Requester = "scheduler";

    /// <summary>
    /// How late a scheduled scan may still start, for example after the Data Scope server restarts. Later than this it
    /// waits for the next scheduled time rather than starting during the working day.
    /// </summary>
    public static readonly TimeSpan CatchUp = TimeSpan.FromHours(6);

    /// <summary>How far apart scans every few hours may be: the steps that divide a day evenly, so every day is the same.</summary>
    public static readonly IReadOnlyList<int> HourSteps = [1, 2, 3, 4, 6, 8, 12];

    /// <summary>Why the settings can't be saved, or null when they can.</summary>
    public static string? Problem(ScanScheduleSettings s)
    {
        if (!Enum.IsDefined(s.Frequency))
        {
            return "Use Off, Hourly, Daily or Weekly.";
        }
        if (s.StartHour is < 0 or > 23)
        {
            return "Choose a start hour from 0 to 23.";
        }
        if (s.StartMinute is < 0 or > 59)
        {
            return "Choose a start minute from 0 to 59.";
        }
        if (s.Frequency == ScanFrequency.Hourly && (s.EveryHours is not { } every || !HourSteps.Contains(every)))
        {
            return "Scan every 1, 2, 3, 4, 6, 8 or 12 hours.";
        }
        if (s.StopHour is { } stop && (stop is < 0 or > 23 || stop == s.StartHour))
        {
            return "Choose a stop hour from 0 to 23 that differs from the start hour, or let the scan run to the end.";
        }
        if (s.Frequency == ScanFrequency.Weekly && (s.Day is not { } day || !Enum.IsDefined(day)))
        {
            return "Choose the day of a weekly scan.";
        }
        return null;
    }

    /// <summary>Why the full scan settings can't be saved, or null when they can.</summary>
    public static string? Problem(FullScanSettings f)
    {
        if (!Enum.IsDefined(f.Frequency))
        {
            return "Use Off, Weekly or Monthly.";
        }
        if (f.Frequency != FullScanFrequency.Off && (f.Day is not { } day || !Enum.IsDefined(day)))
        {
            return "Choose the day of the full scan.";
        }
        return null;
    }

    /// <summary>The latest full scan start at or before <paramref name="now"/>, or null when full scans are off.</summary>
    public static DateTimeOffset? LastFullDue(ScanScheduleSettings s, FullScanSettings f, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (f.Frequency == FullScanFrequency.Off || f.Day is null)
        {
            return null;
        }
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        for (var back = 0; back <= 36; back++)
        {
            if (FullStart(s, f, today.AddDays(-back), zone) is { } start && start <= now)
            {
                return start;
            }
        }
        return null;
    }

    /// <summary>The next full scan start after <paramref name="now"/>, or null when full scans are off.</summary>
    public static DateTimeOffset? NextFullDue(ScanScheduleSettings s, FullScanSettings f, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (f.Frequency == FullScanFrequency.Off || f.Day is null)
        {
            return null;
        }
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        for (var ahead = 0; ahead <= 36; ahead++)
        {
            if (FullStart(s, f, today.AddDays(ahead), zone) is { } start && start > now)
            {
                return start;
            }
        }
        return null;
    }

    /// <summary>The full scan start on a local date, at the server's scan start time, or null when none is due that day.</summary>
    private static DateTimeOffset? FullStart(ScanScheduleSettings s, FullScanSettings f, DateTime localDate, TimeZoneInfo zone) =>
        localDate.DayOfWeek == f.Day && (f.Frequency == FullScanFrequency.Weekly || localDate.Day <= 7)
            ? Start(localDate, s.StartHour, s.StartMinute, zone)
            : null;

    /// <summary>The latest scheduled start at or before <paramref name="now"/>, or null when scans are off.</summary>
    public static DateTimeOffset? LastDue(ScanScheduleSettings s, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (s.Frequency == ScanFrequency.Off)
        {
            return null;
        }
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        for (var back = 0; back <= 8; back++)
        {
            var starts = Starts(s, today.AddDays(-back), zone).Where(start => start <= now).ToList();
            if (starts.Count > 0)
            {
                return starts[^1];
            }
        }
        return null;
    }

    /// <summary>The next scheduled start after <paramref name="now"/>, or null when scans are off.</summary>
    public static DateTimeOffset? NextDue(ScanScheduleSettings s, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (s.Frequency == ScanFrequency.Off)
        {
            return null;
        }
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        for (var ahead = 0; ahead <= 8; ahead++)
        {
            var starts = Starts(s, today.AddDays(ahead), zone).Where(start => start > now).ToList();
            if (starts.Count > 0)
            {
                return starts[0];
            }
        }
        return null;
    }

    /// <summary>Whether a scheduled scan may run at <paramref name="now"/>: always without a stop hour.</summary>
    public static bool InWindow(ScanScheduleSettings s, DateTimeOffset now, TimeZoneInfo zone) =>
        InWindow(s, TimeZoneInfo.ConvertTime(now, zone).Hour);

    /// <summary>The scheduled starts on a local date, earliest first; none when the schedule doesn't run that day.</summary>
    private static List<DateTimeOffset> Starts(ScanScheduleSettings s, DateTime localDate, TimeZoneInfo zone)
    {
        switch (s.Frequency)
        {
            case ScanFrequency.Weekly when localDate.DayOfWeek != s.Day:
            case ScanFrequency.Off:
                return [];
            case ScanFrequency.Hourly:
                var every = s.EveryHours is { } e && HourSteps.Contains(e) ? e : 24;
                return Enumerable.Range(0, 24 / every)
                    .Select(k => (s.StartHour + k * every) % 24)
                    .Where(hour => InWindow(s, hour))
                    .Select(hour => Start(localDate, hour, s.StartMinute, zone))
                    // The hour the clocks skip in spring starts with the next one, once.
                    .Distinct()
                    .Order()
                    .ToList();
            default:
                return [Start(localDate, s.StartHour, s.StartMinute, zone)];
        }
    }

    /// <summary>Whether a scan every few hours starts at this hour: always without a stop hour.</summary>
    private static bool InWindow(ScanScheduleSettings s, int hour) =>
        s.StopHour is not { } stop || (s.StartHour < stop ? hour >= s.StartHour && hour < stop : hour >= s.StartHour || hour < stop);

    private static DateTimeOffset Start(DateTime localDate, int hour, int minute, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(localDate.AddHours(hour).AddMinutes(minute), DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            // The clocks skip this hour when summer time starts: start an hour later.
            local = local.AddHours(1);
        }
        // An hour that happens twice when summer time ends starts at the first one.
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
