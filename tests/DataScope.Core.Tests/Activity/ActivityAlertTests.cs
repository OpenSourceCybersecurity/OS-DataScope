using DataScope.Core.Activity;

namespace DataScope.Core.Tests.Activity;

public sealed class ActivityAlertTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);
    private static readonly BurstRule FivePerTen = new(ActivityAlertRule.MassDelete, 5, TimeSpan.FromMinutes(10));

    private static AlertEvent E(long id, double minutes, string sid = "S-1-5-21-1-2-3-1104", string name = "NORTHWIND\\jordan") =>
        new(id, T0.AddMinutes(minutes), sid, name);

    [Fact]
    public void Fewer_events_than_the_threshold_in_the_window_raise_nothing() =>
        Assert.Empty(BurstDetector.Find([], [E(1, 0), E(2, 3), E(3, 6), E(4, 9), E(5, 11)], FivePerTen));

    [Fact]
    public void Reaching_the_threshold_raises_one_burst_that_carries_on_while_events_keep_coming()
    {
        // Five within ten minutes, then more each within ten minutes of the last, then a long pause and a lone one.
        var burst = Assert.Single(BurstDetector.Find([], [E(1, 0), E(2, 1), E(3, 2), E(4, 3), E(5, 4), E(6, 12), E(7, 20), E(8, 45)], FivePerTen));
        Assert.Equal((7, 7, 1L, 7L, T0, T0.AddMinutes(20)), (burst.Count, burst.NewEvents, burst.FirstEventId, burst.LastEventId, burst.First, burst.Last));
        Assert.Equal("NORTHWIND\\jordan", burst.PrincipalName);
    }

    [Fact]
    public void People_are_counted_separately()
    {
        var events = Enumerable.Range(0, 8).Select(i => E(i + 1, i, sid: i % 2 == 0 ? "S-A" : "S-B", name: i % 2 == 0 ? "a" : "b")).ToList();
        Assert.Empty(BurstDetector.Find([], events, FivePerTen));
        events.AddRange([E(20, 8.5, "S-A", "a"), E(21, 9, "S-A", "a")]);
        Assert.Equal(["S-A"], BurstDetector.Find([], events, FivePerTen).Select(b => b.PrincipalSid));
    }

    [Fact]
    public void Events_seen_by_an_earlier_pass_count_towards_the_threshold_but_not_as_new()
    {
        var burst = Assert.Single(BurstDetector.Find([E(1, 0), E(2, 1), E(3, 2)], [E(4, 3), E(5, 4)], FivePerTen));
        Assert.Equal((5, 2), (burst.Count, burst.NewEvents));
        // A burst made only of events already seen is not raised again.
        Assert.Empty(BurstDetector.Find([E(1, 0), E(2, 1), E(3, 2), E(4, 3), E(5, 4)], [E(6, 40)], FivePerTen));
    }

    [Fact]
    public void A_threshold_of_one_groups_events_close_together()
    {
        var rule = new BurstRule(ActivityAlertRule.PermissionChange, 1, TimeSpan.FromMinutes(60));
        var bursts = BurstDetector.Find([], [E(1, 0), E(2, 30), E(3, 200)], rule);
        Assert.Equal([2, 1], bursts.Select(b => b.Count));
        Assert.Equal(ActivityAlertSeverity.Medium, rule.Severity);
    }

    [Fact]
    public void Wording_says_who_what_where_and_how_long_without_claiming_more()
    {
        Assert.Equal("NORTHWIND\\jordan deleted 1,200 files or folders on fs01 in 4 minutes.",
            ActivityAlertText.Describe(ActivityAlertRule.MassDelete, "NORTHWIND\\jordan", 1200, "fs01", T0, T0.AddMinutes(3.5)));
        Assert.Equal("Someone was refused access 25 times on fs01 within a minute.",
            ActivityAlertText.Describe(ActivityAlertRule.ManyDenied, null, 25, "fs01", T0, T0.AddSeconds(40)));
        Assert.Contains("Some may have been file events", ActivityAlertText.Describe(ActivityAlertRule.EventsMissed, null, 50, "fs01", T0, T0), StringComparison.Ordinal);
        Assert.Equal("NORTHWIND\\admin changed permissions or the owner of something on fs01.",
            ActivityAlertText.Describe(ActivityAlertRule.PermissionChange, "NORTHWIND\\admin", 1, "fs01", T0, T0));
        Assert.Equal("NORTHWIND\\admin changed permissions or owners 3 times on fs01 in 25 minutes.",
            ActivityAlertText.Describe(ActivityAlertRule.PermissionChange, "NORTHWIND\\admin", 3, "fs01", T0, T0.AddMinutes(25)));
        Assert.Equal(ActivityAlertSeverity.High, ActivityAlertText.Severity(ActivityAlertRule.LogCleared));
    }

    [Theory]
    [InlineData(1, 10)]
    [InlineData(50, 0)]
    public void Thresholds_that_would_alert_on_everything_or_nothing_are_refused(int count, int minutes)
    {
        var options = new ActivityAlertOptions { MassDeleteCount = count, MassDeleteMinutes = minutes };
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Email_recipients_must_be_addresses()
    {
        Assert.Throws<InvalidOperationException>(new ActivityAlertOptions { EmailTo = ["security team"] }.Validate);
        new ActivityAlertOptions { EmailTo = ["soc@northwind.test"] }.Validate();
    }

    // A distinct rule counts different shares, not events on them.
    private static readonly BurstRule ThreeShares = new(ActivityAlertRule.ManyDenied, 3, TimeSpan.FromMinutes(60), Distinct: true);

    private static AlertEvent M(long id, double minutes, string share) => new(id, T0.AddMinutes(minutes), "BEN@CONTOSO.EXAMPLE", "ben@contoso.example", share);

    [Fact]
    public void A_distinct_rule_counts_different_things_not_events()
    {
        Assert.Empty(BurstDetector.Find([], [M(1, 0, "Finance"), M(2, 1, "Finance"), M(3, 2, "Payroll"), M(4, 3, "Finance"), M(5, 4, "Payroll")], ThreeShares));
        var burst = Assert.Single(BurstDetector.Find([], [M(1, 0, "Finance"), M(2, 1, "Finance"), M(3, 2, "Payroll"), M(4, 30, "Legal")], ThreeShares));
        Assert.Equal((3, 3, T0, T0.AddMinutes(30)), (burst.Count, burst.NewEvents, burst.First, burst.Last));
    }

    [Fact]
    public void A_distinct_rule_forgets_things_that_leave_the_window()
    {
        // Finance at 0 and Payroll at 30 are more than an hour from Legal at 95, so only two shares are ever within the hour.
        Assert.Empty(BurstDetector.Find([], [M(1, 0, "Finance"), M(2, 30, "Payroll"), M(3, 95, "Legal")], ThreeShares));
    }

    [Fact]
    public void A_distinct_rule_counts_as_new_only_things_first_reached_by_new_events()
    {
        var burst = Assert.Single(BurstDetector.Find([M(1, 0, "Finance"), M(2, 1, "Payroll"), M(3, 2, "Legal")], [M(4, 3, "Finance"), M(5, 4, "IT")], ThreeShares));
        Assert.Equal((4, 1), (burst.Count, burst.NewEvents));
    }
}
