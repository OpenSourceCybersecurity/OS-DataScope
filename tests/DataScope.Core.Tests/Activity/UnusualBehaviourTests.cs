using DataScope.Core.Activity;

namespace DataScope.Core.Tests.Activity;

/// <summary>Learning a person's normal activity and judging a day against it (D166). Every number here is made up.</summary>
public sealed class UnusualBehaviourTests
{
    private static readonly ActivityAlertOptions Options = new();

    // 09:00 to 17:00.
    private const int OfficeHours = 0b0000_0000_0011_1111_1110_0000_0000;

    private static ActivityDay Day(int n, int items, int hours = OfficeHours) => new(new DateOnly(2026, 9, 1).AddDays(n), items, hours);

    private static BehaviourBaseline Usual(int activeDays = 20, int busyDay = 50, int hours = OfficeHours, params string[] places) =>
        new(activeDays, busyDay, hours, places.ToHashSet(StringComparer.Ordinal));

    private static ActivitySoFar Today(int items, Dictionary<int, int>? byHour = null, Dictionary<string, int>? places = null) =>
        new(items, byHour ?? new() { [10] = items }, places ?? []);

    private static bool Sensitive(string place) => place.StartsWith("s|", StringComparison.Ordinal);

    [Fact]
    public void Learning_takes_the_ninetieth_percentile_day_the_hours_worked_and_the_active_days()
    {
        // Ten active days of 10 to 100 and one idle day: nine in ten hold 90 or fewer, so 90 is a busy day.
        var days = Enumerable.Range(1, 10).Select(i => Day(i, i * 10, i == 3 ? 1 << 20 : OfficeHours)).Append(Day(11, 0, 1 << 2)).ToList();
        var usual = UnusualBehaviour.Learn(days, ["b", "a", "b"]);
        Assert.Equal((10, 90), (usual.ActiveDays, usual.BusyDay));
        Assert.True(usual.WorksAt(9) && usual.WorksAt(17) && usual.WorksAt(20));
        // An idle day's hours aren't learned.
        Assert.False(usual.WorksAt(2));
        Assert.Equal(10, usual.ActiveHours);
        Assert.Equal(["a", "b"], usual.Places.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void One_huge_day_in_a_month_does_not_make_every_day_look_normal()
    {
        var days = Enumerable.Range(1, 20).Select(i => Day(i, 40)).Append(Day(21, 5000)).ToList();
        Assert.Equal(40, UnusualBehaviour.Learn(days, []).BusyDay);
    }

    [Fact]
    public void Learning_nothing_gives_an_empty_baseline_and_keeps_only_the_first_places()
    {
        var empty = UnusualBehaviour.Learn([], []);
        Assert.Equal((0, 0, 0), (empty.ActiveDays, empty.BusyDay, empty.Hours));
        var many = UnusualBehaviour.Learn([Day(1, 1)], Enumerable.Range(0, UnusualBehaviour.MaxPlaces + 10).Select(i => $"p{i}"));
        Assert.Equal(UnusualBehaviour.MaxPlaces, many.Places.Count);
        Assert.Contains("p0", many.Places);
        Assert.DoesNotContain($"p{UnusualBehaviour.MaxPlaces}", many.Places);
    }

    [Fact]
    public void A_person_still_being_learned_is_not_judged()
    {
        var today = Today(5000, new() { [3] = 5000 }, new() { ["s|1"] = 1, ["s|2"] = 1, ["s|3"] = 1 });
        Assert.Empty(UnusualBehaviour.Judge(Usual(activeDays: 4), today, Options, Sensitive));
        Assert.Equal(3, UnusualBehaviour.Judge(Usual(activeDays: 5), today, Options, Sensitive).Count);
        Assert.Empty(UnusualBehaviour.Judge(Usual(), Today(0, []), Options, Sensitive));
    }

    [Fact]
    public void A_day_is_unusually_busy_at_four_times_a_busy_day_and_never_below_the_minimum()
    {
        Assert.Equal(200, UnusualBehaviour.VolumeThreshold(Usual(busyDay: 10), Options));
        Assert.Equal(400, UnusualBehaviour.VolumeThreshold(Usual(busyDay: 100), Options));
        Assert.Equal(int.MaxValue, UnusualBehaviour.VolumeThreshold(Usual(busyDay: int.MaxValue / 2), Options));

        Assert.Empty(UnusualBehaviour.Judge(Usual(busyDay: 100), Today(399), Options, Sensitive));
        var finding = Assert.Single(UnusualBehaviour.Judge(Usual(busyDay: 100), Today(400), Options, Sensitive));
        Assert.Equal((UnusualKind.Volume, 400, 100), (finding.Kind, finding.Count, finding.Usual));
        // Someone who barely works on the server isn't flagged for a modest day.
        Assert.Empty(UnusualBehaviour.Judge(Usual(busyDay: 2), Today(150), Options, Sensitive));
    }

    [Fact]
    public void Work_in_hours_never_worked_before_is_flagged_with_those_hours()
    {
        var today = Today(40, new() { [10] = 15, [2] = 12, [3] = 8, [22] = 5 });
        var finding = Assert.Single(UnusualBehaviour.Judge(Usual(), today, Options, Sensitive));
        Assert.Equal((UnusualKind.Hours, 25, 9), (finding.Kind, finding.Count, finding.Usual));
        Assert.Equal([2, 3, 22], finding.Hours);

        // Fewer than the minimum in odd hours is a late email check, not a pattern.
        Assert.Empty(UnusualBehaviour.Judge(Usual(), Today(25, new() { [10] = 15, [23] = 10 }), Options, Sensitive));
    }

    [Fact]
    public void Someone_who_works_around_the_clock_has_no_unusual_hours()
    {
        var almostAllDay = (1 << 20) - 1;
        Assert.Empty(UnusualBehaviour.Judge(Usual(hours: almostAllDay), Today(100, new() { [22] = 100 }), Options, Sensitive));
        var finding = Assert.Single(UnusualBehaviour.Judge(Usual(hours: almostAllDay >> 1), Today(100, new() { [22] = 100 }), Options, Sensitive));
        Assert.Equal(UnusualKind.Hours, finding.Kind);
    }

    [Fact]
    public void Sensitive_folders_not_used_before_are_flagged_most_used_first()
    {
        var places = new Dictionary<string, int> { ["s|hr"] = 3, ["s|finance"] = 9, ["s|board"] = 3, ["s|known"] = 50, ["p|public"] = 80 };
        var finding = Assert.Single(UnusualBehaviour.Judge(Usual(places: ["s|known"]), Today(148, null, places), Options, Sensitive));
        Assert.Equal((UnusualKind.Places, 3, 1), (finding.Kind, finding.Count, finding.Usual));
        Assert.Equal(["s|finance", "s|board", "s|hr"], finding.Places);

        // Two new sensitive folders and any number of ordinary ones are not enough.
        places.Remove("s|hr");
        places["p|other"] = 5;
        Assert.Empty(UnusualBehaviour.Judge(Usual(places: ["s|known"]), Today(148, null, places), Options, Sensitive));
    }

    [Fact]
    public void The_options_change_the_thresholds()
    {
        var strict = new ActivityAlertOptions { UnusualVolumeFactor = 2, UnusualVolumeMinimum = 10, UnusualHoursMinimum = 1, UnusualPlacesCount = 1 };
        var findings = UnusualBehaviour.Judge(Usual(busyDay: 10), Today(20, new() { [10] = 19, [23] = 1 }, new() { ["s|new"] = 1 }), strict, Sensitive);
        Assert.Equal([UnusualKind.Volume, UnusualKind.Hours, UnusualKind.Places], findings.Select(f => f.Kind));
    }

    [Theory]
    [InlineData(new int[0], "")]
    [InlineData(new[] { 2 }, "02:00 to 03:00")]
    [InlineData(new[] { 4, 2, 3, 3 }, "02:00 to 05:00")]
    [InlineData(new[] { 23, 2, 3, 0 }, "00:00 to 01:00, 02:00 to 04:00 and 23:00 to 00:00")]
    public void Hours_read_as_ranges(int[] hours, string text) => Assert.Equal(text, UnusualBehaviour.HoursText(hours));

    [Fact]
    public void Each_kind_has_its_own_rule()
    {
        var rules = Enum.GetValues<UnusualKind>().Select(ActivityAlertText.Rule).ToList();
        Assert.Equal(3, rules.Distinct().Count());
        Assert.All(rules, r => Assert.True(ActivityAlertText.IsUnusual(r)));
        Assert.All(rules, r => Assert.Equal(ActivityAlertSeverity.Medium, ActivityAlertText.Severity(r)));
        Assert.False(ActivityAlertText.IsUnusual(ActivityAlertRule.MassDelete));
    }

    [Fact]
    public void Wording_says_what_was_usual_and_never_claims_a_file_was_read()
    {
        var t0 = new DateTimeOffset(2026, 10, 20, 2, 0, 0, TimeSpan.Zero);
        Assert.Equal("NORTHWIND\\jordan touched 1,200 files and folders on fs01 in one day, far more than on their usual busy day (about 150).",
            ActivityAlertText.Describe(ActivityAlertRule.UnusualVolume, "NORTHWIND\\jordan", 1200, "fs01", t0, t0.AddHours(5), 150));
        Assert.Equal("NORTHWIND\\jordan used 3 folders holding sensitive data on fs01 that they hadn't used before: Finance\\Payroll, HR\\Staff and Board (its top level).",
            ActivityAlertText.Describe(ActivityAlertRule.UnusualPlaces, "NORTHWIND\\jordan", 3, "fs01", t0, t0, 12, "Finance\\Payroll, HR\\Staff and Board (its top level)"));
        Assert.Equal("Active at unusual hours", ActivityAlertText.Title(ActivityAlertRule.UnusualHours));
        Assert.Equal("Sensitive folders not used before", ActivityAlertText.Title(ActivityAlertRule.UnusualPlaces));
        Assert.Equal("Unusually busy day", ActivityAlertText.Title(ActivityAlertRule.UnusualVolume));
    }

    [Theory]
    [InlineData(nameof(ActivityAlertOptions.UnusualLearnDays), 6)]
    [InlineData(nameof(ActivityAlertOptions.UnusualLearnDays), 91)]
    [InlineData(nameof(ActivityAlertOptions.UnusualMinActiveDays), 1)]
    [InlineData(nameof(ActivityAlertOptions.UnusualVolumeFactor), 1)]
    [InlineData(nameof(ActivityAlertOptions.UnusualVolumeMinimum), 9)]
    [InlineData(nameof(ActivityAlertOptions.UnusualHoursMinimum), 0)]
    [InlineData(nameof(ActivityAlertOptions.UnusualPlacesCount), 0)]
    public void Settings_out_of_range_are_refused(string setting, int value)
    {
        var options = new ActivityAlertOptions();
        typeof(ActivityAlertOptions).GetProperty(setting)!.SetValue(options, value);
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Learning_must_cover_the_days_a_person_needs()
    {
        new ActivityAlertOptions().Validate();
        Assert.Throws<InvalidOperationException>(new ActivityAlertOptions { UnusualLearnDays = 10, UnusualMinActiveDays = 11 }.Validate);
    }
}
