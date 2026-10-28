using DataScope.Core.Access;
using DataScope.Core.Inventory;
using DataScope.Core.ScanJobs;

namespace DataScope.Persistence.Entities;

public enum EnrollmentState
{
    Pending,
    Enrolled,
    Error,
    Retired,
}

public sealed class ManagedServer
{
    public Guid Id { get; set; }

    /// <summary>Normalized (lower-case) DNS or NetBIOS name, validated by <see cref="Core.Inventory.HostName"/>.</summary>
    public required string Hostname { get; set; }

    /// <summary>A file server (D141). Decides which connector scans it.</summary>
    public SourceKind Kind { get; set; } = SourceKind.FileServer;

    public EnrollmentState State { get; set; } = EnrollmentState.Pending;
    public DateTimeOffset? LastSeenAt { get; set; }
    public string? LastError { get; set; }

    /// <summary>Whether the server's local groups (BUILTIN\Users and so on) could be read by the last scan.</summary>
    public LocalGroupsState LocalGroupsState { get; set; } = LocalGroupsState.NotRead;

    public DateTimeOffset? LocalGroupsReadAt { get; set; }

    /// <summary>When the server is scanned on its own (D116, D121). Times are in the Data Scope server's time zone.</summary>
    public ScanFrequency ScanFrequency { get; set; } = ScanFrequency.Off;
    public int ScanStartHour { get; set; } = 1;
    public int ScanStartMinute { get; set; }
    public DayOfWeek? ScanDay { get; set; }
    public int? ScanStopHour { get; set; }

    /// <summary>For scans every few hours, how many hours apart.</summary>
    public int? ScanEveryHours { get; set; }

    /// <summary>The scheduled time last dealt with: a scan started or resumed, or skipped because one was already under way.</summary>
    public DateTimeOffset? LastScheduledAt { get; set; }

    public ScanScheduleSettings Schedule
    {
        get => new(ScanFrequency, ScanStartHour, ScanDay, ScanStopHour, ScanStartMinute, ScanEveryHours);
        set => (ScanFrequency, ScanStartHour, ScanDay, ScanStopHour, ScanStartMinute, ScanEveryHours) =
            (value.Frequency, value.StartHour, value.Day, value.StopHour, value.StartMinute, value.Frequency == ScanFrequency.Hourly ? value.EveryHours : null);
    }

    /// <summary>When the server gets a full scan, which reads every file again (D130).</summary>
    public FullScanFrequency FullScanFrequency { get; set; } = FullScanFrequency.Off;
    public DayOfWeek? FullScanDay { get; set; }

    /// <summary>The full scan time last dealt with: a full scan started, or one already under way.</summary>
    public DateTimeOffset? LastFullScheduledAt { get; set; }

    public FullScanSettings FullScan
    {
        get => new(FullScanFrequency, FullScanDay);
        set => (FullScanFrequency, FullScanDay) = (value.Frequency, value.Frequency == FullScanFrequency.Off ? null : value.Day);
    }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public required string CreatedBySid { get; set; }

    /// <summary>Optimistic concurrency token mapped to PostgreSQL's xmin.</summary>
    public uint RowVersion { get; set; }
}
