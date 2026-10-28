using DataScope.Core.ScanJobs;

namespace DataScope.Core.Tests;

public class ScanJobStateMachineTests
{
    [Theory]
    [InlineData(ScanJobState.Queued, ScanJobState.Running)]
    [InlineData(ScanJobState.Running, ScanJobState.Paused)]
    [InlineData(ScanJobState.Running, ScanJobState.Queued)]
    [InlineData(ScanJobState.Paused, ScanJobState.Queued)]
    [InlineData(ScanJobState.Paused, ScanJobState.Cancelled)]
    public void Allows_valid_transitions(ScanJobState from, ScanJobState to) =>
        Assert.True(ScanJobStateMachine.CanTransition(from, to));

    [Theory]
    [InlineData(ScanJobState.Queued, ScanJobState.Completed)]
    [InlineData(ScanJobState.Queued, ScanJobState.Paused)]
    [InlineData(ScanJobState.Paused, ScanJobState.Running)]
    [InlineData(ScanJobState.Completed, ScanJobState.Queued)]
    [InlineData(ScanJobState.Cancelled, ScanJobState.Queued)]
    [InlineData(ScanJobState.Failed, ScanJobState.Running)]
    public void Rejects_invalid_transitions(ScanJobState from, ScanJobState to)
    {
        Assert.False(ScanJobStateMachine.CanTransition(from, to));
        var ex = Assert.Throws<InvalidScanJobTransitionException>(() => ScanJobStateMachine.EnsureTransition(from, to));
        Assert.Equal(from, ex.From);
    }

    [Fact]
    public void Terminal_states_have_no_exits()
    {
        foreach (var state in Enum.GetValues<ScanJobState>())
        {
            var terminal = state is ScanJobState.Completed or ScanJobState.Failed or ScanJobState.Cancelled;
            Assert.Equal(terminal, ScanJobStateMachine.IsTerminal(state));
        }
    }
}
