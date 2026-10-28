using DataScope.Core.ScanJobs;

namespace DataScope.Persistence.Entities;

public sealed class ScanJob
{
    public Guid Id { get; set; }
    public Guid ServerId { get; set; }
    public ManagedServer? Server { get; set; }

    public ScanJobState State { get; set; } = ScanJobState.Queued;

    /// <summary>Quick reads only new and changed files; full reads every file with more room per file (D130).</summary>
    public ScanKind Kind { get; set; } = ScanKind.Quick;

    /// <summary>Classification policy version the job runs with, for evidence.</summary>
    public required string PolicyVersion { get; set; }

    public long ItemsTotal { get; set; }
    public long ItemsDone { get; set; }

    /// <summary>Opaque resume point written by the executor (JSON).</summary>
    public string? Checkpoint { get; set; }

    public int Attempts { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public string? LastError { get; set; }

    /// <summary>Set by the API when someone asks to pause or cancel; the worker honours it at the next checkpoint.</summary>
    public ScanJobState? RequestedState { get; set; }

    public required string RequestedBySid { get; set; }

    /// <summary>Started by the server's scan schedule rather than by a person (D116).</summary>
    public bool Scheduled { get; set; }

    /// <summary>
    /// The schedule looks after this job: it pauses at the stop hour and continues at the next scheduled time. Cleared
    /// when someone pauses, resumes or cancels it by hand.
    /// </summary>
    public bool KeepToWindow { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    public uint RowVersion { get; set; }
}
