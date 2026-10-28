using System.Text.Json;
using DataScope.Core.Audit;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Persistence;

/// <summary>Appends to the hash-chained admin audit log. Rows are never updated or deleted.</summary>
public sealed class AuditLogWriter(DataScopeDbContext db, TimeProvider clock)
{
    // Arbitrary constant identifying the audit-log advisory lock.
    private const long AdvisoryLockKey = 0x44_53_41_55_44_49_54; // "DSAUDIT"

    /// <summary>
    /// Appends an entry inside the caller's transaction if there is one, so the
    /// audit row commits or rolls back together with the change it describes.
    /// </summary>
    public async Task<AppAuditEntry> AppendAsync(string actorSid, string actorName, string action, string target, object? details = null, CancellationToken ct = default)
    {
        var ownTransaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            // Serialize writers so sequence numbers and hashes stay contiguous.
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({AdvisoryLockKey})", ct);
            var last = await db.AppAuditLog.AsNoTracking().OrderByDescending(x => x.Sequence).FirstOrDefaultAsync(ct);
            var sequence = (last?.Sequence ?? 0) + 1;
            var previous = last?.Hash ?? AuditChain.GenesisHash;
            // Round to microseconds: PostgreSQL timestamptz precision, so the hash verifies after a round trip.
            var now = clock.GetUtcNow();
            var at = new DateTimeOffset(now.Ticks - now.Ticks % 10, TimeSpan.Zero);
            var json = JsonSerializer.Serialize(details ?? new { });
            var entry = new AppAuditEntry
            {
                Sequence = sequence,
                At = at,
                ActorSid = actorSid,
                ActorName = actorName,
                Action = action,
                Target = target,
                Details = json,
                PreviousHash = previous,
                Hash = AuditChain.ComputeHash(sequence, at, actorSid, actorName, action, target, json, previous),
            };
            db.AppAuditLog.Add(entry);
            await db.SaveChangesAsync(ct);
            if (ownTransaction is not null)
            {
                await ownTransaction.CommitAsync(ct);
            }
            return entry;
        }
        finally
        {
            if (ownTransaction is not null)
            {
                await ownTransaction.DisposeAsync();
            }
        }
    }

    public async Task<AuditChain.VerifyResult> VerifyAsync(CancellationToken ct = default)
    {
        var rows = db.AppAuditLog.AsNoTracking().OrderBy(x => x.Sequence).AsAsyncEnumerable();
        var records = new List<AuditRecord>();
        await foreach (var r in rows.WithCancellation(ct))
        {
            records.Add(new AuditRecord(r.Sequence, r.At, r.ActorSid, r.ActorName, r.Action, r.Target, r.Details, r.PreviousHash, r.Hash));
        }
        return AuditChain.Verify(records);
    }
}
