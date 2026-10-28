using DataScope.Core.Activity;

namespace DataScope.Core.Tests.Activity;

public sealed class CollectionPositionTests
{
    private static SecurityLogInfo Log(long? oldest, long? newest) =>
        new(oldest, newest, oldest is { } o && newest is { } n ? n - o + 1 : 0, null, null, null, null);

    [Fact]
    public void A_first_read_starts_at_the_oldest_record_with_no_gap() =>
        Assert.Equal(new PositionCheck(null, false, null), CollectionPosition.Check(null, Log(500, 900)));

    [Theory]
    [InlineData(500L)]
    [InlineData(700L)]
    [InlineData(900L)]
    [InlineData(499L)] // the next record to read is the oldest still there
    public void Carries_on_when_the_next_record_is_still_in_the_log(long position) =>
        Assert.Equal(new PositionCheck(null, false, position), CollectionPosition.Check(position, Log(500, 900)));

    [Fact]
    public void Records_overwritten_before_they_were_read_are_a_gap_and_reading_skips_to_the_oldest()
    {
        var check = CollectionPosition.Check(100, Log(500, 900));
        Assert.Equal(new MissedRecords(ActivityGapReason.Overwritten, 101, 499), check.Missed);
        Assert.Equal(399, check.Missed!.Count);
        Assert.False(check.NewGeneration);
        Assert.Equal(499, check.ReadAfter);
    }

    [Fact]
    public void Record_ids_going_backwards_mean_the_log_started_again()
    {
        var check = CollectionPosition.Check(900, Log(1, 40));
        Assert.Equal(new MissedRecords(ActivityGapReason.LogReset, 901, null), check.Missed);
        Assert.Null(check.Missed!.Count);
        Assert.True(check.NewGeneration);
        Assert.Null(check.ReadAfter);
    }

    [Fact]
    public void An_empty_log_after_one_with_records_was_reset()
    {
        var check = CollectionPosition.Check(900, SecurityLogInfo.Empty);
        Assert.True(check.NewGeneration);
        Assert.Equal(ActivityGapReason.LogReset, check.Missed!.Reason);
    }

    [Fact]
    public void An_empty_log_on_the_first_read_is_simply_empty() =>
        Assert.Equal(new PositionCheck(null, false, null), CollectionPosition.Check(null, SecurityLogInfo.Empty));

    [Fact]
    public void The_query_asks_only_for_collected_events_after_a_record()
    {
        Assert.Equal(
            "*[System[(EventID=4656 or EventID=4660 or EventID=4663 or EventID=4670 or EventID=5140 or EventID=5145 or EventID=1102) and EventRecordID>12345]]",
            SecurityEventQuery.For(12345));
        Assert.DoesNotContain("EventRecordID", SecurityEventQuery.For(null), StringComparison.Ordinal);
    }
}
