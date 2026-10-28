namespace DataScope.Core.ScanJobs;

/// <summary>What the worker tells an executor after each progress report.</summary>
public enum ScanControl
{
    Continue,
    Pause,
    Cancel,

    /// <summary>Another worker took the job over (our lease expired). Stop without writing anything.</summary>
    LeaseLost,
}

public delegate Task<ScanControl> ReportProgress(long done, long total, string? checkpoint, CancellationToken ct);

/// <param name="Checkpoint">Resume point from a previous attempt, or null for a fresh start.</param>
/// <param name="Kind">Quick reads only new and changed files; full reads every file, with more room per file (D130).</param>
public sealed record ScanContext(Guid JobId, string Hostname, string? Checkpoint, ReportProgress Report, ScanKind Kind = ScanKind.Quick);

/// <summary>Performs the scan for one job. Implementations must be read-only against file servers.</summary>
public interface IScanExecutor
{
    /// <returns>The control signal that stopped the run, or <see cref="ScanControl.Continue"/> when it finished.</returns>
    Task<ScanControl> RunAsync(ScanContext context, CancellationToken ct);
}
