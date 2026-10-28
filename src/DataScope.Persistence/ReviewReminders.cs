using DataScope.Core.Governance;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Persistence;

/// <summary>
/// Puts review emails in the outbox when they fall due (assignment, reminders, overdue notices) and escalates overdue
/// reviews to the backup owner. Safe to run repeatedly: every notice has a fixed key and is only enqueued once.
/// </summary>
public sealed class ReviewReminders(DataScopeDbContext db, NotificationStore outbox, TimeProvider clock)
{
    /// <summary>Returns how many notices were added.</summary>
    public async Task<int> RunAsync(string reviewLink, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var open = await (from r in db.AccessReviews.AsNoTracking()
                          join c in db.ReviewCampaigns on r.CampaignId equals c.Id
                          where c.State == CampaignState.Open && r.State != ReviewState.Submitted
                          select new { Review = r, c.Name, c.DueOn }).ToListAsync(ct);
        var added = 0;
        foreach (var x in open)
        {
            var r = x.Review;
            foreach (var notice in ReviewSchedule.Due(r.Id, x.DueOn, r.FrozenAt, now, submitted: false, hasBackup: r.BackupSid is not null))
            {
                if (notice.ToBackup)
                {
                    if (await SendAsync(notice, r, r.BackupSid!, r.BackupName ?? r.BackupSid!, r.BackupEmail, x.Name, x.DueOn, today, reviewLink, ct))
                    {
                        added++;
                    }
                    if (r.EscalatedAt is null)
                    {
                        await db.AccessReviews.Where(a => a.Id == r.Id && a.EscalatedAt == null)
                            .ExecuteUpdateAsync(s => s.SetProperty(a => a.EscalatedAt, now), ct);
                        db.ReviewEvents.Add(new ReviewEvent
                        {
                            ReviewId = r.Id,
                            Kind = "Escalated",
                            ActorSid = "system",
                            ActorName = "Data Scope",
                            At = now,
                            Detail = $"Overdue; the backup owner {r.BackupName} can now complete the review.",
                        });
                        await db.SaveChangesAsync(ct);
                    }
                    continue;
                }
                if (await SendAsync(notice, r, r.OwnerSid, r.OwnerName, r.OwnerEmail, x.Name, x.DueOn, today, reviewLink, ct))
                {
                    added++;
                }
                // Reminders also reach a delegate; the assignment notice for a delegate is sent when they are named.
                if (r.DelegateSid is not null && notice.Kind != NotificationKind.ReviewAssigned
                    && await SendAsync(notice, r, r.DelegateSid, r.DelegateName ?? r.DelegateSid, r.DelegateEmail, x.Name, x.DueOn, today, reviewLink, ct))
                {
                    added++;
                }
            }
        }
        return added;
    }

    /// <summary>Tells a newly named delegate about the review.</summary>
    public Task<bool> NotifyDelegateAsync(AccessReview review, string campaignName, DateOnly dueOn, string reviewLink, CancellationToken ct) =>
        SendAsync(new ScheduledNotice(NotificationKind.ReviewAssigned, $"review:{review.Id:N}:delegated", false), review,
            review.DelegateSid!, review.DelegateName ?? review.DelegateSid!, review.DelegateEmail, campaignName, dueOn,
            DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime), reviewLink, ct);

    private Task<bool> SendAsync(ScheduledNotice notice, AccessReview review, string sid, string name, string? email, string campaign,
        DateOnly dueOn, DateOnly today, string link, CancellationToken ct)
    {
        var (subject, body) = ReviewEmail.Compose(notice.Kind, name, campaign, review.Resource, dueOn, today, link, review.OwnerName);
        // One notice per person: the recipient is part of the key.
        return outbox.EnqueueAsync(new OutgoingNotice($"{notice.Key}:{sid}", notice.Kind, review.Id, sid, name, email, subject, body), ct);
    }
}
