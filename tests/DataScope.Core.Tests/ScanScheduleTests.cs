using DataScope.Core.ScanJobs;

namespace DataScope.Core.Tests;

public class ScanScheduleTests
{
    /// <summary>Five hours behind UTC all year, so local 01:00 is 06:00 UTC.</summary>
    private static readonly TimeZoneInfo Fixed = TimeZoneInfo.CreateCustomTimeZone("Test-5", TimeSpan.FromHours(-5), "Test -5", "Test -5");

    /// <summary>UTC-5 with summer time at UTC-4 from the second Sunday in March to the first Sunday in November, at 2 AM.</summary>
    private static readonly TimeZoneInfo Summer = TimeZoneInfo.CreateCustomTimeZone("Test-Summer", TimeSpan.FromHours(-5), "Test summer", "Test standard",
        "Test summer",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                new DateTime(2000, 1, 1), DateTime.MaxValue.Date, TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday)),
        ]);

    private static readonly ScanScheduleSettings Nightly = new(ScanFrequency.Daily, 1, null, 7);

    private static DateTimeOffset Utc(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void A_daily_scan_is_due_at_its_hour_in_the_servers_time_zone()
    {
        // 2026-10-10 05:59 UTC is 00:59 local: the last start was yesterday at 01:00 local, the next is in a minute.
        Assert.Equal(Utc(10, 9, 6), ScanSchedule.LastDue(Nightly, Utc(10, 10, 5, 59), Fixed));
        Assert.Equal(Utc(10, 10, 6), ScanSchedule.NextDue(Nightly, Utc(10, 10, 5, 59), Fixed));
        Assert.Equal(Utc(10, 10, 6), ScanSchedule.LastDue(Nightly, Utc(10, 10, 6), Fixed));
        Assert.Equal(Utc(10, 11, 6), ScanSchedule.NextDue(Nightly, Utc(10, 10, 6), Fixed));
    }

    [Fact]
    public void A_weekly_scan_is_due_on_its_day()
    {
        var saturday = new ScanScheduleSettings(ScanFrequency.Weekly, 22, DayOfWeek.Saturday, null);
        // Saturday 2026-10-10 22:00 local is Sunday 03:00 UTC.
        Assert.Equal(Utc(10, 11, 3), ScanSchedule.NextDue(saturday, Utc(10, 10, 12), Fixed));
        Assert.Equal(Utc(10, 4, 3), ScanSchedule.LastDue(saturday, Utc(10, 10, 12), Fixed));
        Assert.Equal(Utc(10, 11, 3), ScanSchedule.LastDue(saturday, Utc(10, 17, 2, 59), Fixed));
    }

    [Fact]
    public void Nothing_is_due_when_scans_are_off()
    {
        Assert.Null(ScanSchedule.LastDue(ScanScheduleSettings.Off, Utc(10, 10, 12), Fixed));
        Assert.Null(ScanSchedule.NextDue(ScanScheduleSettings.Off, Utc(10, 10, 12), Fixed));
    }

    [Fact]
    public void Summer_time_changes_neither_skip_nor_double_a_scan()
    {
        // 2026-03-08: 2 AM local doesn't exist, so a 2 AM scan starts at 3 AM summer time (07:00 UTC).
        var two = new ScanScheduleSettings(ScanFrequency.Daily, 2, null, null);
        Assert.Equal(Utc(3, 8, 7), ScanSchedule.NextDue(two, Utc(3, 8, 0), Summer));
        // 2026-11-01: 1 AM local happens twice; the scan starts at the first (UTC-4, 05:00 UTC), once.
        var one = new ScanScheduleSettings(ScanFrequency.Daily, 1, null, null);
        Assert.Equal(Utc(11, 1, 5), ScanSchedule.NextDue(one, Utc(11, 1, 0), Summer));
        Assert.Equal(Utc(11, 2, 6), ScanSchedule.NextDue(one, Utc(11, 1, 5), Summer));
        Assert.Equal(Utc(11, 1, 5), ScanSchedule.LastDue(one, Utc(11, 1, 6, 30), Summer));
    }

    [Theory]
    [InlineData(1, 7, 0, false)]
    [InlineData(1, 7, 1, true)]
    [InlineData(1, 7, 6, true)]
    [InlineData(1, 7, 7, false)]
    [InlineData(22, 6, 23, true)]
    [InlineData(22, 6, 3, true)]
    [InlineData(22, 6, 6, false)]
    [InlineData(22, 6, 12, false)]
    public void A_scheduled_scan_runs_only_between_its_start_and_stop_hours(int start, int stop, int localHour, bool inside)
    {
        var settings = new ScanScheduleSettings(ScanFrequency.Daily, start, null, stop);
        Assert.Equal(inside, ScanSchedule.InWindow(settings, Utc(10, 10, localHour).AddHours(5), Fixed));
    }

    [Fact]
    public void Without_a_stop_hour_a_scan_runs_to_the_end() =>
        Assert.True(ScanSchedule.InWindow(new ScanScheduleSettings(ScanFrequency.Daily, 1, null, null), Utc(10, 10, 17), Fixed));

    [Fact]
    public void A_daily_scan_starts_at_its_minute()
    {
        var halfPast = new ScanScheduleSettings(ScanFrequency.Daily, 1, null, null, StartMinute: 30);
        Assert.Equal(Utc(10, 10, 6, 30), ScanSchedule.NextDue(halfPast, Utc(10, 10, 6), Fixed));
        Assert.Equal(Utc(10, 9, 6, 30), ScanSchedule.LastDue(halfPast, Utc(10, 10, 6, 29), Fixed));
    }

    [Fact]
    public void A_scan_every_hour_is_due_each_hour_at_its_minute()
    {
        var hourly = new ScanScheduleSettings(ScanFrequency.Hourly, 0, null, null, StartMinute: 30, EveryHours: 1);
        Assert.Equal(Utc(10, 10, 13, 30), ScanSchedule.NextDue(hourly, Utc(10, 10, 12, 31), Fixed));
        Assert.Equal(Utc(10, 10, 12, 30), ScanSchedule.LastDue(hourly, Utc(10, 10, 12, 31), Fixed));
        Assert.Equal(Utc(10, 10, 12, 30), ScanSchedule.LastDue(hourly, Utc(10, 10, 12, 30), Fixed));
    }

    [Fact]
    public void A_scan_every_few_hours_counts_from_its_start_and_carries_over_midnight()
    {
        // Every 3 hours from 1 AM local: 1, 4, 7, 10, 13, 16, 19 and 22.
        var everyThree = new ScanScheduleSettings(ScanFrequency.Hourly, 1, null, null, EveryHours: 3);
        // 22:30 local on the 10th: the next is 01:00 local on the 11th (06:00 UTC).
        Assert.Equal(Utc(10, 11, 6), ScanSchedule.NextDue(everyThree, Utc(10, 11, 3, 30), Fixed));
        Assert.Equal(Utc(10, 11, 3), ScanSchedule.LastDue(everyThree, Utc(10, 11, 3, 30), Fixed));
        // 08:00 local: the last was 07:00, the next 10:00.
        Assert.Equal(Utc(10, 10, 12), ScanSchedule.LastDue(everyThree, Utc(10, 10, 13), Fixed));
        Assert.Equal(Utc(10, 10, 15), ScanSchedule.NextDue(everyThree, Utc(10, 10, 13), Fixed));
    }

    [Fact]
    public void A_scan_every_few_hours_starts_only_inside_its_window()
    {
        // Every 4 hours at a quarter past, from 10 PM until 6 AM: only 22:15 and 02:15 local.
        var overnight = new ScanScheduleSettings(ScanFrequency.Hourly, 22, null, 6, StartMinute: 15, EveryHours: 4);
        Assert.Equal(Utc(10, 11, 3, 15), ScanSchedule.NextDue(overnight, Utc(10, 10, 12), Fixed));
        Assert.Equal(Utc(10, 11, 7, 15), ScanSchedule.NextDue(overnight, Utc(10, 11, 3, 15), Fixed));
        Assert.Equal(Utc(10, 12, 3, 15), ScanSchedule.NextDue(overnight, Utc(10, 11, 7, 15), Fixed));
        Assert.Equal(Utc(10, 11, 7, 15), ScanSchedule.LastDue(overnight, Utc(10, 11, 17), Fixed));
        // 06:00 local stops it.
        Assert.False(ScanSchedule.InWindow(overnight, Utc(10, 11, 11), Fixed));
        Assert.True(ScanSchedule.InWindow(overnight, Utc(10, 11, 10, 59), Fixed));
    }

    [Fact]
    public void Summer_time_changes_neither_skip_nor_double_a_scan_every_hour()
    {
        var hourly = new ScanScheduleSettings(ScanFrequency.Hourly, 0, null, null, EveryHours: 1);
        // 2026-03-08: 1 AM local is 06:00 UTC, then the clocks jump from 2 AM to 3 AM summer time (07:00 UTC).
        Assert.Equal(Utc(3, 8, 7), ScanSchedule.NextDue(hourly, Utc(3, 8, 6), Summer));
        Assert.Equal(Utc(3, 8, 8), ScanSchedule.NextDue(hourly, Utc(3, 8, 7), Summer));
        // 2026-11-01: 1 AM local happens twice; one scan at the first (05:00 UTC), the next at 2 AM standard time (07:00 UTC).
        Assert.Equal(Utc(11, 1, 5), ScanSchedule.NextDue(hourly, Utc(11, 1, 4), Summer));
        Assert.Equal(Utc(11, 1, 7), ScanSchedule.NextDue(hourly, Utc(11, 1, 5), Summer));
    }

    [Fact]
    public void Settings_that_cannot_work_are_refused()
    {
        Assert.Null(ScanSchedule.Problem(Nightly));
        Assert.Null(ScanSchedule.Problem(ScanScheduleSettings.NewServer));
        Assert.Null(ScanSchedule.Problem(new ScanScheduleSettings(ScanFrequency.Weekly, 0, DayOfWeek.Sunday, 23)));
        Assert.NotNull(ScanSchedule.Problem(new ScanScheduleSettings(ScanFrequency.Daily, 24, null, null)));
        Assert.NotNull(ScanSchedule.Problem(new ScanScheduleSettings(ScanFrequency.Daily, -1, null, null)));
        Assert.NotNull(ScanSchedule.Problem(new ScanScheduleSettings(ScanFrequency.Daily, 1, null, 1)));
        Assert.NotNull(ScanSchedule.Problem(new ScanScheduleSettings(ScanFrequency.Daily, 1, null, 24)));
        Assert.NotNull(ScanSchedule.Problem(new ScanScheduleSettings(ScanFrequency.Weekly, 1, null, null)));
        Assert.Equal("Use Off, Hourly, Daily or Weekly.", ScanSchedule.Problem(new ScanScheduleSettings((ScanFrequency)9, 1, null, null)));
        Assert.Null(ScanSchedule.Problem(new ScanScheduleSettings(ScanFrequency.Daily, 1, null, null, StartMinute: 59)));
        Assert.NotNull(ScanSchedule.Problem(new ScanScheduleSettings(ScanFrequency.Daily, 1, null, null, StartMinute: 60)));
        Assert.NotNull(ScanSchedule.Problem(new ScanScheduleSettings(ScanFrequency.Daily, 1, null, null, StartMinute: -1)));
        Assert.Null(ScanSchedule.Problem(new ScanScheduleSettings(ScanFrequency.Hourly, 0, null, null, EveryHours: 12)));
        Assert.Null(ScanSchedule.Problem(new ScanScheduleSettings(ScanFrequency.Hourly, 22, null, 6, StartMinute: 15, EveryHours: 4)));
        Assert.NotNull(ScanSchedule.Problem(new ScanScheduleSettings(ScanFrequency.Hourly, 0, null, null)));
        Assert.NotNull(ScanSchedule.Problem(new ScanScheduleSettings(ScanFrequency.Hourly, 0, null, null, EveryHours: 5)));
        Assert.NotNull(ScanSchedule.Problem(new ScanScheduleSettings(ScanFrequency.Hourly, 0, null, null, EveryHours: 0)));
        Assert.NotNull(ScanSchedule.Problem(new ScanScheduleSettings(ScanFrequency.Hourly, 0, null, null, EveryHours: 24)));
    }

    [Fact]
    public void A_weekly_full_scan_is_due_on_its_day_at_the_scan_start_time()
    {
        var saturdays = new FullScanSettings(FullScanFrequency.Weekly, DayOfWeek.Saturday);
        // Saturday 2026-10-10 01:00 local is 06:00 UTC.
        Assert.Equal(Utc(10, 3, 6), ScanSchedule.LastFullDue(Nightly, saturdays, Utc(10, 10, 5, 59), Fixed));
        Assert.Equal(Utc(10, 10, 6), ScanSchedule.NextFullDue(Nightly, saturdays, Utc(10, 10, 5, 59), Fixed));
        Assert.Equal(Utc(10, 10, 6), ScanSchedule.LastFullDue(Nightly, saturdays, Utc(10, 10, 6), Fixed));
        Assert.Equal(Utc(10, 17, 6), ScanSchedule.NextFullDue(Nightly, saturdays, Utc(10, 10, 6), Fixed));
    }

    [Fact]
    public void A_monthly_full_scan_is_due_on_the_first_of_its_weekday()
    {
        var firstSaturday = new FullScanSettings(FullScanFrequency.Monthly, DayOfWeek.Saturday);
        // The first Saturdays are 3 October and 7 November 2026; the Saturdays between don't count.
        Assert.Equal(Utc(10, 3, 6), ScanSchedule.LastFullDue(Nightly, firstSaturday, Utc(10, 24, 12), Fixed));
        Assert.Equal(Utc(11, 7, 6), ScanSchedule.NextFullDue(Nightly, firstSaturday, Utc(10, 3, 6), Fixed));
        var firstSunday = new FullScanSettings(FullScanFrequency.Monthly, DayOfWeek.Sunday);
        Assert.Equal(Utc(11, 1, 6), ScanSchedule.NextFullDue(Nightly, firstSunday, Utc(10, 10, 12), Fixed));
    }

    [Fact]
    public void A_full_scan_starts_at_the_first_start_of_scans_every_few_hours_and_when_quick_scans_are_off()
    {
        var hourly = new ScanScheduleSettings(ScanFrequency.Hourly, 22, null, 6, StartMinute: 15, EveryHours: 4);
        var saturdays = new FullScanSettings(FullScanFrequency.Weekly, DayOfWeek.Saturday);
        // Saturday 22:15 local is Sunday 03:15 UTC.
        Assert.Equal(Utc(10, 11, 3, 15), ScanSchedule.NextFullDue(hourly, saturdays, Utc(10, 10, 12), Fixed));
        var byHand = new ScanScheduleSettings(ScanFrequency.Off, 2, null, null);
        Assert.Equal(Utc(10, 10, 7), ScanSchedule.NextFullDue(byHand, saturdays, Utc(10, 10, 0), Fixed));
    }

    [Fact]
    public void No_full_scan_is_due_when_full_scans_are_off()
    {
        Assert.Null(ScanSchedule.LastFullDue(Nightly, FullScanSettings.Off, Utc(10, 10, 12), Fixed));
        Assert.Null(ScanSchedule.NextFullDue(Nightly, FullScanSettings.Off, Utc(10, 10, 12), Fixed));
        Assert.Null(ScanSchedule.NextFullDue(Nightly, new FullScanSettings(FullScanFrequency.Weekly, null), Utc(10, 10, 12), Fixed));
    }

    [Fact]
    public void Full_scan_settings_need_a_day_unless_off()
    {
        Assert.Null(ScanSchedule.Problem(FullScanSettings.Off));
        Assert.Null(ScanSchedule.Problem(FullScanSettings.NewServer));
        Assert.Null(ScanSchedule.Problem(new FullScanSettings(FullScanFrequency.Monthly, DayOfWeek.Monday)));
        Assert.Equal("Choose the day of the full scan.", ScanSchedule.Problem(new FullScanSettings(FullScanFrequency.Weekly, null)));
        Assert.Equal("Choose the day of the full scan.", ScanSchedule.Problem(new FullScanSettings(FullScanFrequency.Monthly, (DayOfWeek)9)));
        Assert.Equal("Use Off, Weekly or Monthly.", ScanSchedule.Problem(new FullScanSettings((FullScanFrequency)7, DayOfWeek.Monday)));
    }
}
