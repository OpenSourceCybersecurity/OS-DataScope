using System.Runtime.Versioning;
using DataScope.Core.Activity;
using DataScope.Scanner.Activity;

namespace DataScope.Scanner.Tests;

/// <summary>
/// The real Windows Security log reader, against the test machine's own log (the CI runner is an administrator). These
/// run in the Windows CI job; on other systems there is no event log and they return early. Reading real file events
/// through an audited share, as a non-admin scan account, is checked by the Windows install test.
/// </summary>
public sealed class WindowsSecurityEventSourceTests
{
    [Fact]
    public async Task Reads_the_range_and_size_of_the_local_Security_log()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var info = await new WindowsSecurityEventSource().GetLogInfoAsync(Environment.MachineName, default);

        Assert.NotNull(info.OldestRecordId);
        Assert.True(info.NewestRecordId >= info.OldestRecordId);
        Assert.True(info.RecordCount > 0);
        Assert.True(info.OldestEventAt < DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.True(info.SizeBytes > 0);
    }

    [Fact]
    public async Task Returns_only_collected_events_after_a_position_oldest_first()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var source = new WindowsSecurityEventSource();
        var events = await source.ReadAsync("localhost", null, 50, default);

        Assert.True(events.Count <= 50);
        Assert.Equal(events.Select(e => e.RecordId).Order(), events.Select(e => e.RecordId));
        Assert.All(events, e =>
        {
            var parsed = SecurityEventXml.Parse(e.Xml);
            Assert.Equal(e.RecordId, parsed.RecordId);
            Assert.Contains(parsed.EventId, ActivityInterpreter.CollectedEventIds);
        });
        if (events.Count > 1)
        {
            var later = await source.ReadAsync("localhost", events[0].RecordId, 50, default);
            Assert.All(later, e => Assert.True(e.RecordId > events[0].RecordId));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("fs01;whoami")]
    [InlineData(@"\\fs01")]
    [InlineData("fs01/../x")]
    [SupportedOSPlatform("windows")] // the check below returns early elsewhere; the attribute covers the lambda
    public async Task Refuses_names_that_are_not_host_names(string host)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var ex = await Assert.ThrowsAsync<SecurityEventReadException>(() => new WindowsSecurityEventSource().GetLogInfoAsync(host, default));
        Assert.Contains("not a valid host name", ex.Message);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task An_unreachable_server_is_a_plain_error_not_a_crash()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var source = new WindowsSecurityEventSource(TimeSpan.FromSeconds(30));
        var ex = await Assert.ThrowsAsync<SecurityEventReadException>(() => source.GetLogInfoAsync("ds-no-such-host.invalid", default));
        Assert.Contains("ds-no-such-host.invalid", ex.Message);
    }
}
