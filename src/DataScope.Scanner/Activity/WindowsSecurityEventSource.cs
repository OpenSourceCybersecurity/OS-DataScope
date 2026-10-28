using System.Diagnostics.Eventing.Reader;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using DataScope.Core.Activity;

namespace DataScope.Scanner.Activity;

/// <summary>
/// Reads a file server's Security log remotely with the Windows event log API (MS-EVEN6 over RPC), as the scanner
/// service's account, with no agent on the file server (D96, D102). The account needs to be in the server's
/// <c>Event Log Readers</c> group and the server's firewall must allow Remote Event Log Management. Only events in
/// <see cref="ActivityInterpreter.CollectedEventIds"/> are asked for, so the filtering happens on the file server.
/// This class only reads: it never clears a log, changes its settings or the audit policy.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsSecurityEventSource(TimeSpan? timeout = null) : ISecurityEventSource
{
    private const string LogName = "Security";
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromMinutes(2);

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9.-]{0,252})$")]
    private static partial Regex HostPattern();

    public Task<IReadOnlyList<RawSecurityEvent>> ReadAsync(string hostname, long? afterRecordId, int max, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        return RunAsync(hostname, session =>
        {
            var query = new EventLogQuery(LogName, PathType.LogName, SecurityEventQuery.For(afterRecordId)) { Session = session };
            using var reader = new EventLogReader(query) { BatchSize = Math.Clamp(max, 1, 256) };
            var events = new List<RawSecurityEvent>();
            while (events.Count < max && !ct.IsCancellationRequested)
            {
                using var record = reader.ReadEvent();
                if (record is null)
                {
                    break;
                }
                if (record.RecordId is { } id)
                {
                    events.Add(new RawSecurityEvent(id, record.ToXml()));
                }
            }
            ct.ThrowIfCancellationRequested();
            return (IReadOnlyList<RawSecurityEvent>)events;
        }, ct);
    }

    public Task<SecurityLogInfo> GetLogInfoAsync(string hostname, CancellationToken ct) => RunAsync(hostname, session =>
    {
        var info = session.GetLogInformation(LogName, PathType.LogName);
        var (oldestId, oldestAt) = Edge(session, newestFirst: false);
        var (newestId, _) = Edge(session, newestFirst: true);
        long? maxSize = null;
        try
        {
            using var config = new EventLogConfiguration(LogName, session);
            maxSize = config.MaximumSizeInBytes;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or EventLogException)
        {
            // Reading the log's settings can need more than Event Log Readers; the size limit is only shown as health.
        }
        return new SecurityLogInfo(oldestId ?? info.OldestRecordNumber, newestId, info.RecordCount, oldestAt, info.FileSize, maxSize, info.IsLogFull);
    }, ct);

    private static (long? Id, DateTimeOffset? At) Edge(EventLogSession session, bool newestFirst)
    {
        var query = new EventLogQuery(LogName, PathType.LogName) { Session = session, ReverseDirection = newestFirst };
        using var reader = new EventLogReader(query) { BatchSize = 1 };
        using var record = reader.ReadEvent();
        if (record is null)
        {
            return (null, null);
        }
        DateTimeOffset? at = record.TimeCreated is { } t ? new DateTimeOffset(t.ToUniversalTime(), TimeSpan.Zero) : null;
        return (record.RecordId, at);
    }

    private async Task<T> RunAsync<T>(string hostname, Func<EventLogSession, T> read, CancellationToken ct)
    {
        if (!HostPattern().IsMatch(hostname) || hostname.Contains("..", StringComparison.Ordinal))
        {
            throw new SecurityEventReadException("The server name is not a valid host name.");
        }
        var local = string.Equals(hostname, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(hostname, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
        try
        {
            // The event log API blocks; a server that stops answering must not hold the collector.
            return await Task.Run(() =>
            {
                using var session = local ? new EventLogSession() : new EventLogSession(hostname);
                return read(session);
            }, ct).WaitAsync(_timeout, ct);
        }
        catch (TimeoutException)
        {
            throw new SecurityEventReadException($"Reading the Security log on {hostname} took longer than {_timeout.TotalSeconds:0} seconds.");
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new SecurityEventReadException(
                $"Access denied reading the Security log on {hostname}. Add Data Scope's scan account to the Event Log Readers group on {hostname} " +
                "(Enable-DataScopeAuditing.ps1 -ReaderAccount does this).", ex);
        }
        catch (EventLogNotFoundException ex)
        {
            throw new SecurityEventReadException($"{hostname} has no Security log Data Scope can open.", ex);
        }
        catch (EventLogException ex)
        {
            throw new SecurityEventReadException(Describe(hostname, ex), ex);
        }
    }

    /// <summary>
    /// Plain words for the errors an admin can fix; anything else keeps Windows' own short message. The exception
    /// does not expose the Windows error code, so its message is compared with Windows' message for each code.
    /// </summary>
    public static string Describe(string hostname, EventLogException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        if (Is(ex, 1722, 1727, 1753))
        {
            return $"Could not reach the event log service on {hostname}. Check the server is on and its firewall allows Remote Event Log Management.";
        }
        if (Is(ex, 5))
        {
            return $"Access denied reading the Security log on {hostname}. Add Data Scope's scan account to the Event Log Readers group on {hostname}.";
        }
        return $"Reading the Security log on {hostname} failed: {Short(ex.Message)}";
    }

    private static bool Is(EventLogException ex, params int[] codes) =>
        codes.Any(code => string.Equals(ex.Message, new System.ComponentModel.Win32Exception(code).Message, StringComparison.Ordinal));

    private static string Short(string message)
    {
        var text = message.ReplaceLineEndings(" ").Trim();
        return text.Length > 300 ? text[..300] : text;
    }
}
