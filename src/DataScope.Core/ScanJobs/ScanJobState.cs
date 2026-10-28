namespace DataScope.Core.ScanJobs;

/// <summary>Lifecycle of a scan job. Terminal states never change again.</summary>
public enum ScanJobState
{
    Queued,
    Running,
    Paused,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>Allowed scan job transitions. Every state change goes through here.</summary>
public static class ScanJobStateMachine
{
    private static readonly Dictionary<ScanJobState, ScanJobState[]> Allowed = new()
    {
        [ScanJobState.Queued] = [ScanJobState.Running, ScanJobState.Cancelled],
        // Running -> Queued happens when a worker's lease expires and the job is retried.
        [ScanJobState.Running] = [ScanJobState.Paused, ScanJobState.Completed, ScanJobState.Failed, ScanJobState.Cancelled, ScanJobState.Queued],
        [ScanJobState.Paused] = [ScanJobState.Queued, ScanJobState.Cancelled],
        [ScanJobState.Completed] = [],
        [ScanJobState.Failed] = [],
        [ScanJobState.Cancelled] = [],
    };

    public static bool CanTransition(ScanJobState from, ScanJobState to) => Allowed[from].Contains(to);

    public static bool IsTerminal(ScanJobState state) => Allowed[state].Length == 0;

    public static void EnsureTransition(ScanJobState from, ScanJobState to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidScanJobTransitionException(from, to);
        }
    }
}

public sealed class InvalidScanJobTransitionException(ScanJobState from, ScanJobState to)
    : InvalidOperationException($"A scan job cannot move from {from} to {to}.")
{
    public ScanJobState From { get; } = from;
    public ScanJobState To { get; } = to;
}
