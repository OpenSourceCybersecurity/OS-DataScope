namespace DataScope.Persistence.Entities;

/// <summary>
/// When each Data Scope service last checked in (System health). One row per service, replaced every minute; nothing
/// else is kept, so a stopped scanner shows as "not seen since" rather than as a quiet day.
/// </summary>
public sealed class ServiceHeartbeat
{
    /// <summary>Web or Scanner.</summary>
    public required string Service { get; set; }

    public required string Machine { get; set; }
    public required string Version { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset SeenAt { get; set; }
}
