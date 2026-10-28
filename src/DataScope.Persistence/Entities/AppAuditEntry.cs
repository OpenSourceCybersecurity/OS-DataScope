namespace DataScope.Persistence.Entities;

/// <summary>Append-only admin audit log row. See <see cref="Core.Audit.AuditChain"/>.</summary>
public sealed class AppAuditEntry
{
    public long Sequence { get; set; }
    public DateTimeOffset At { get; set; }
    public required string ActorSid { get; set; }
    public required string ActorName { get; set; }
    public required string Action { get; set; }
    public required string Target { get; set; }

    /// <summary>Redacted JSON details. Never contains secrets or file contents.</summary>
    public required string Details { get; set; }

    public required string PreviousHash { get; set; }
    public required string Hash { get; set; }
}
