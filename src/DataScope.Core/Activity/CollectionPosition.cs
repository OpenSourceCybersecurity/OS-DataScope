namespace DataScope.Core.Activity;

/// <summary>Why records were lost before Data Scope read them.</summary>
public enum ActivityGapReason
{
    /// <summary>The log no longer holds records Data Scope had not read yet (overwritten when full, or cleared).</summary>
    Overwritten,

    /// <summary>The log's record ids went backwards: it was cleared or replaced, and starts again.</summary>
    LogReset,
}

/// <summary>A range of log records that were gone before Data Scope read them. <c>LastRecordId</c> null: unknown end.</summary>
public sealed record MissedRecords(ActivityGapReason Reason, long FirstRecordId, long? LastRecordId)
{
    public long? Count => LastRecordId is { } last ? last - FirstRecordId + 1 : null;
}

/// <summary>
/// What to do before reading: carry on, record a gap and skip to the oldest record still there, or start a new log
/// generation because the log was reset.
/// </summary>
public sealed record PositionCheck(MissedRecords? Missed, bool NewGeneration, long? ReadAfter);

/// <summary>
/// Compares where collection stopped with what the log holds now (D102). Record ids count every record in the log, so
/// a read that caught up moves the position to the newest record seen even when the last ones were not file events;
/// otherwise records that were never relevant would later look like a gap.
/// </summary>
public static class CollectionPosition
{
    public static PositionCheck Check(long? position, SecurityLogInfo log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (position is not { } last)
        {
            // First read: everything still in the log, oldest first. Nothing before it was ever Data Scope's to miss.
            return new PositionCheck(null, false, null);
        }
        if (log.OldestRecordId is not { } oldest || log.NewestRecordId is not { } newest)
        {
            // An empty log after one that had records was cleared without the cleared event being kept.
            return new PositionCheck(new MissedRecords(ActivityGapReason.LogReset, last + 1, null), true, null);
        }
        if (newest < last)
        {
            return new PositionCheck(new MissedRecords(ActivityGapReason.LogReset, last + 1, null), true, null);
        }
        if (oldest > last + 1)
        {
            return new PositionCheck(new MissedRecords(ActivityGapReason.Overwritten, last + 1, oldest - 1), false, oldest - 1);
        }
        return new PositionCheck(null, false, last);
    }
}
