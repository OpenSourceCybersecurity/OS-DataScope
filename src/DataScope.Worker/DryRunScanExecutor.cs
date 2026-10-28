using System.Text.Json;
using DataScope.Core.ScanJobs;

namespace DataScope.Worker;

/// <summary>
/// M0 placeholder: walks a fixed number of steps without touching any file
/// server, so the job lifecycle (claim, checkpoint, pause, cancel, resume after
/// a crash) can be exercised end to end. Replaced by the SMB scanner in M1.
/// </summary>
public sealed class DryRunScanExecutor(IConfiguration config) : IScanExecutor
{
    private sealed record Checkpoint(int Step);

    public async Task<ScanControl> RunAsync(ScanContext context, CancellationToken ct)
    {
        var steps = config.GetValue("Scanner:DryRunSteps", 20);
        var delay = TimeSpan.FromMilliseconds(config.GetValue("Scanner:DryRunStepMilliseconds", 250));
        var start = context.Checkpoint is null ? 0 : JsonSerializer.Deserialize<Checkpoint>(context.Checkpoint)?.Step ?? 0;
        for (var step = start + 1; step <= steps; step++)
        {
            await Task.Delay(delay, ct);
            var control = await context.Report(step, steps, JsonSerializer.Serialize(new Checkpoint(step)), ct);
            if (control != ScanControl.Continue)
            {
                return control;
            }
        }
        return ScanControl.Continue;
    }
}
