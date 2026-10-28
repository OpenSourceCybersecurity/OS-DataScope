namespace DataScope.Core.Activity;

/// <summary>One event as read from a log: its record id and the XML Windows rendered for it.</summary>
public sealed record RawSecurityEvent(long RecordId, string Xml);

/// <summary>
/// What a Security log holds right now: the record ids of its oldest and newest records (all events, not only the
/// collected ones), how many there are, and how big it is. Any value Windows would not give is null.
/// </summary>
public sealed record SecurityLogInfo(
    long? OldestRecordId,
    long? NewestRecordId,
    long? RecordCount,
    DateTimeOffset? OldestEventAt,
    long? SizeBytes,
    long? MaxSizeBytes,
    bool? IsFull)
{
    public static readonly SecurityLogInfo Empty = new(null, null, 0, null, null, null, null);
}

/// <summary>
/// Reads file activity events from a file server's Security log, oldest first. Implementations read only; they never
/// change audit policy, clear a log or touch the server in any other way (D96).
/// </summary>
public interface ISecurityEventSource
{
    /// <summary>
    /// Up to <paramref name="max"/> collected events (<see cref="ActivityInterpreter.CollectedEventIds"/>) with a
    /// record id above <paramref name="afterRecordId"/>, or from the oldest when it is null.
    /// </summary>
    /// <exception cref="SecurityEventReadException">The log could not be read; the message says why and what to do.</exception>
    Task<IReadOnlyList<RawSecurityEvent>> ReadAsync(string hostname, long? afterRecordId, int max, CancellationToken ct);

    /// <summary>The log's oldest and newest record ids and size, read before each pass to find gaps (D102).</summary>
    /// <exception cref="SecurityEventReadException">The log could not be read; the message says why and what to do.</exception>
    Task<SecurityLogInfo> GetLogInfoAsync(string hostname, CancellationToken ct);
}

/// <summary>The log could not be read (unreachable server, access denied, no such log). Safe to show to an admin.</summary>
public sealed class SecurityEventReadException : Exception
{
    public SecurityEventReadException() : base("The event log could not be read.") { }

    public SecurityEventReadException(string message) : base(message) { }

    public SecurityEventReadException(string message, Exception inner) : base(message, inner) { }
}
