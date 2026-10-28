using DataScope.Core.Activity;

namespace DataScope.Persistence.Entities;

/// <summary>
/// One Windows Security event about a file, folder or share, with what it proves (D97, D98). The raw event is kept
/// unchanged as evidence; the services' database login may only fill in which share and file it belongs to.
/// Unique on (server, channel, record id, log generation), so collecting the same events again changes nothing.
/// </summary>
public sealed class FileAuditEvent
{
    public long Id { get; set; }
    public Guid ServerId { get; set; }
    public required string Channel { get; set; }
    public long RecordId { get; set; }

    /// <summary>Bumped when the log is cleared or recreated, after which Windows can reuse record ids (M6b).</summary>
    public int LogGeneration { get; set; }

    /// <summary>0 when the event could not be read.</summary>
    public int EventId { get; set; }

    public string? Provider { get; set; }
    public DateTimeOffset EventTime { get; set; }
    public DateTimeOffset IngestedAt { get; set; }

    public string? PrincipalSid { get; set; }
    public string? PrincipalName { get; set; }
    public string? LogonId { get; set; }
    public long? ProcessId { get; set; }
    public string? ProcessName { get; set; }
    public long? HandleId { get; set; }
    public string? ClientAddress { get; set; }

    /// <summary>The path on the file server's own disk, from the event or, for 4660, from the correlated event.</summary>
    public string? ObjectPath { get; set; }

    /// <summary>For 5140 and 5145: the share the event names (without \\*\).</summary>
    public string? EventShareName { get; set; }

    /// <summary>For 5140 and 5145: the share's folder on the file server, as the event gives it.</summary>
    public string? EventShareLocalPath { get; set; }

    public Guid? ShareId { get; set; }

    /// <summary>Path inside the share; empty for the share itself.</summary>
    public string? RelativePath { get; set; }

    public ActivityObjectKind ObjectKind { get; set; }
    public ActivityPathSource PathSource { get; set; }

    /// <summary>For a 4660: the record id of the 4656 or 4663 its path came from.</summary>
    public long? CorrelatedRecordId { get; set; }

    public ActivityOperation Operation { get; set; }
    public ActivityCertainty Certainty { get; set; }
    public ActivityOutcome Outcome { get; set; }
    public long? AccessMask { get; set; }
    public string? OldSecurity { get; set; }
    public string? NewSecurity { get; set; }

    /// <summary>Why the event could not be read, when it could not.</summary>
    public string? ParseError { get; set; }

    /// <summary>The event exactly as Windows rendered it (capped at 64 KB characters).</summary>
    public required string RawXml { get; set; }
}

public enum ActivitySourceState
{
    /// <summary>Turned on, not read yet.</summary>
    Waiting,
    Ok,
    Error,
}

/// <summary>Collection state for one event log on one file server: on or off, how far it has read, and its health.</summary>
public sealed class ActivitySource
{
    public Guid ServerId { get; set; }
    public required string Channel { get; set; }
    public bool Enabled { get; set; }
    public ActivitySourceState State { get; set; }
    public int LogGeneration { get; set; }

    /// <summary>The highest record id stored; the next read starts after it.</summary>
    public long? LastRecordId { get; set; }

    public DateTimeOffset? LastReadAt { get; set; }
    public DateTimeOffset? LastEventAt { get; set; }
    public DateTimeOffset? NextReadAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? LastErrorAt { get; set; }
    public long EventsStored { get; set; }

    /// <summary>Events read but not about files (registry, other object types).</summary>
    public long EventsIgnored { get; set; }

    public long EventsUnreadable { get; set; }

    /// <summary>The log as last seen (D102): oldest and newest record ids, record count, oldest event, size.</summary>
    public long? LogOldestRecordId { get; set; }
    public long? LogNewestRecordId { get; set; }
    public long? LogRecordCount { get; set; }
    public DateTimeOffset? LogOldestEventAt { get; set; }
    public long? LogSizeBytes { get; set; }
    public long? LogMaxSizeBytes { get; set; }
    public bool? LogFull { get; set; }
    public DateTimeOffset? LogCheckedAt { get; set; }

    /// <summary>Alert rules have looked at this server's events up to this event id (D105).</summary>
    public long AlertsCheckedEventId { get; set; }

    /// <summary>When stored events were last matched to scanned files and folders; redone after each scan.</summary>
    public DateTimeOffset? KindsCheckedAt { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public string? ChangedBySid { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public uint RowVersion { get; set; }
}

/// <summary>
/// Log records that were gone before Data Scope read them (D102): overwritten or cleared, or a log that started again.
/// Some of them may not have been file events at all; Windows can't say. Append-only, like the events.
/// </summary>
public sealed class ActivityGap
{
    public long Id { get; set; }
    public Guid ServerId { get; set; }
    public required string Channel { get; set; }

    /// <summary>The log generation the missing records belonged to.</summary>
    public int LogGeneration { get; set; }
    public ActivityGapReason Reason { get; set; }
    public long FirstRecordId { get; set; }

    /// <summary>Null when the end is unknown (the log was reset).</summary>
    public long? LastRecordId { get; set; }
    public DateTimeOffset DetectedAt { get; set; }

    /// <summary>The oldest event still in the log when the gap was found, so the gap ends before it.</summary>
    public DateTimeOffset? LogOldestEventAt { get; set; }
}

/// <summary>
/// Something in the file activity worth a person's attention (D105): a burst of deletes or refused attempts, permission
/// changes, a cleared log or missed events. An open alert is extended while the same person's burst continues.
/// </summary>
public sealed class ActivityAlert
{
    public long Id { get; set; }
    public ActivityAlertRule Rule { get; set; }
    public ActivityAlertSeverity Severity { get; set; }
    public ActivityAlertState State { get; set; }
    public Guid ServerId { get; set; }
    public string? PrincipalSid { get; set; }
    public string? PrincipalName { get; set; }
    public DateTimeOffset FirstEventAt { get; set; }
    public DateTimeOffset LastEventAt { get; set; }

    /// <summary>Events in the alert, or for missed events the number of log records (0 when unknown).</summary>
    public int EventCount { get; set; }
    public long? FirstEventId { get; set; }
    public long? LastEventId { get; set; }
    public long? GapId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? AcknowledgedBySid { get; set; }
    public string? AcknowledgedByName { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public string? Note { get; set; }

    /// <summary>
    /// For unusual behaviour (D166): what was usual for the person (a busy day's files and folders, the hours they work
    /// in, or the places they used), and which hours or folders were not.
    /// </summary>
    public int? Usual { get; set; }

    public string? Detail { get; set; }
    public uint RowVersion { get; set; }
}

/// <summary>
/// What is normal for one person on one file server, learned once a day from the days before (D166). Rebuilt
/// whole each day, so someone who stops appearing drops out. A row with an empty person key records the day the server
/// was last learned, even when nobody was active.
/// </summary>
public sealed class ActivityBaseline
{
    public Guid ServerId { get; set; }

    /// <summary>The person's SID.</summary>
    public required string PersonKey { get; set; }

    public string? PersonName { get; set; }

    /// <summary>The local day it was learned for, from the days before it.</summary>
    public DateOnly LearnedOn { get; set; }

    public int ActiveDays { get; set; }
    public int BusyDay { get; set; }

    /// <summary>A bit for each local hour (0 to 23) the person was active in.</summary>
    public int Hours { get; set; }

    /// <summary>The places they used: a share or library and its top-level folder, as "share id|folder".</summary>
    public List<string> Places { get; set; } = [];

    public DateTimeOffset UpdatedAt { get; set; }
}
