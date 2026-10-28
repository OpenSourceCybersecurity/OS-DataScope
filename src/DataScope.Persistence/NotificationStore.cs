using DataScope.Core.Governance;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Persistence;

/// <summary>A notice to put in the outbox.</summary>
public sealed record OutgoingNotice(
    string Key, NotificationKind Kind, Guid? ReviewId, string RecipientSid, string RecipientName, string? RecipientEmail, string Subject, string Body);

/// <summary>
/// The email outbox. Enqueuing is idempotent by key, so schedulers can run as often as they like. Messages are claimed
/// with a short lease so two workers never send the same one, and retried with growing delays before giving up.
/// </summary>
public sealed class NotificationStore(DataScopeDbContext db, TimeProvider clock)
{
    public const int MaxAttempts = 5;
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2)];

    /// <summary>Adds the notice unless one with the same key exists. Returns true when it was added.</summary>
    public async Task<bool> EnqueueAsync(OutgoingNotice notice, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var noAddress = string.IsNullOrWhiteSpace(notice.RecipientEmail);
        var state = (noAddress ? OutboxState.Skipped : OutboxState.Pending).ToString();
        var kind = notice.Kind.ToString();
        var error = noAddress ? "The recipient has no email address in Active Directory or in the owner record." : null;
        var email = noAddress ? null : notice.RecipientEmail!.Trim();
        var subject = Clip(notice.Subject, 500);
        var body = Clip(notice.Body, 8000);
        var rows = await db.Database.ExecuteSqlAsync($"""
            INSERT INTO notification_outbox (idempotency_key, kind, review_id, recipient_sid, recipient_name, recipient_email,
                subject, body, state, attempts, next_attempt_at, last_error, created_at)
            VALUES ({notice.Key}, {kind}, {notice.ReviewId}, {notice.RecipientSid}, {Clip(notice.RecipientName, 256)}, {email},
                {subject}, {body}, {state}, 0, {now}, {error}, {now})
            ON CONFLICT (idempotency_key) DO NOTHING
            """, ct);
        return rows == 1;
    }

    /// <summary>Claims up to <paramref name="max"/> messages that are due, hiding them from other workers for a few minutes.</summary>
    public async Task<IReadOnlyList<OutboxMessage>> ClaimDueAsync(int max, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var hidden = now + TimeSpan.FromMinutes(5);
        var pending = nameof(OutboxState.Pending);
        return await db.Outbox.FromSql($"""
            UPDATE notification_outbox SET next_attempt_at = {hidden}
            WHERE id IN (
                SELECT id FROM notification_outbox
                WHERE state = {pending} AND next_attempt_at <= {now}
                ORDER BY id
                FOR UPDATE SKIP LOCKED
                LIMIT {max})
            RETURNING *
            """).AsNoTracking().ToListAsync(ct);
    }

    public Task MarkSentAsync(long id, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return db.Outbox.Where(m => m.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.State, OutboxState.Sent)
            .SetProperty(m => m.SentAt, now)
            .SetProperty(m => m.Attempts, m => m.Attempts + 1)
            .SetProperty(m => m.LastError, (string?)null), ct);
    }

    /// <summary>Records a failed attempt: retried later, or given up after <see cref="MaxAttempts"/>.</summary>
    public async Task MarkFailedAsync(long id, string error, CancellationToken ct)
    {
        var message = await db.Outbox.AsNoTracking().SingleAsync(m => m.Id == id, ct);
        var attempts = message.Attempts + 1;
        var giveUp = attempts >= MaxAttempts;
        var next = clock.GetUtcNow() + Backoff[Math.Min(attempts - 1, Backoff.Length - 1)];
        await db.Outbox.Where(m => m.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.Attempts, attempts)
            .SetProperty(m => m.State, giveUp ? OutboxState.Failed : OutboxState.Pending)
            .SetProperty(m => m.NextAttemptAt, next)
            .SetProperty(m => m.LastError, Clip(error, 1000)), ct);
    }

    /// <summary>Marks a message as not sent on purpose, for example because email is not set up.</summary>
    public Task SkipAsync(long id, string reason, CancellationToken ct) =>
        db.Outbox.Where(m => m.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.State, OutboxState.Skipped)
            .SetProperty(m => m.LastError, Clip(reason, 1000)), ct);

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max];
}
