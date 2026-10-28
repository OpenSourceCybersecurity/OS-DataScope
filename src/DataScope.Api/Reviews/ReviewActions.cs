using DataScope.Api.Auth;
using DataScope.Core.Access;
using DataScope.Core.Governance;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Reviews;

public sealed record DecisionInput(long ItemId, string? Decision, string? Justification, DateOnly? ExceptionExpiresOn);

public sealed record DecisionsRequest(IReadOnlyList<DecisionInput>? Decisions);

/// <param name="Confirm">The reviewer confirms the decisions are theirs and final.</param>
public sealed record SubmitRequest(bool Confirm);

/// <param name="DelegateSid">The person to hand the review to; null or empty takes it back.</param>
public sealed record DelegateRequest(string? DelegateSid);

/// <summary>
/// Recording decisions, submitting and delegating a review. Every change locks the review row first, so two people
/// working on the same review cannot interleave, and nothing can be decided after submission. Nothing here touches
/// Active Directory or file permissions: a decision to remove access is recorded for IT to act on in Windows.
/// </summary>
internal sealed class ReviewActions(DataScopeDbContext db, DirectoryStore directory, ReviewReminders reminders, AuditLogWriter audit, TimeProvider clock, IConfiguration configuration)
{
    public const int MaxDecisionsPerRequest = 500;

    /// <summary>The address review emails point to: the owner portal, which needs a Windows sign-in.</summary>
    public string ReviewLink
    {
        get
        {
            var url = configuration["Notifications:PublicUrl"];
            if (string.IsNullOrWhiteSpace(url))
            {
                url = $"https://{System.Net.Dns.GetHostName().ToLowerInvariant()}:{configuration.GetValue("Https:Port", 443)}/";
            }
            return url.TrimEnd('/') + "/#my-reviews";
        }
    }

    public async Task<IResult> DecideAsync(Guid reviewId, DecisionsRequest request, Actor actor, CancellationToken ct)
    {
        var inputs = request.Decisions ?? [];
        if (inputs.Count == 0 || inputs.Count > MaxDecisionsPerRequest)
        {
            return Invalid("decisions", $"Send between 1 and {MaxDecisionsPerRequest} decisions.");
        }
        if (inputs.Select(i => i.ItemId).Distinct().Count() != inputs.Count)
        {
            return Invalid("decisions", "Each entry can be decided once per request.");
        }
        var now = CampaignBuilder.Microseconds(clock.GetUtcNow());
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (review, problem) = await LockOpenAsync(reviewId, actor, ct);
        if (review is null)
        {
            return problem!;
        }
        var role = ReviewViews.RoleOf(review, actor.Sid)!;
        var ids = inputs.Select(i => i.ItemId).ToList();
        var items = await db.ReviewItems.AsNoTracking().Where(i => i.ReviewId == reviewId && ids.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, ct);
        var latest = await ReviewViews.LatestDecisionsAsync(db, [reviewId], ct);
        var errors = new Dictionary<string, string[]>();
        var added = new List<ReviewDecision>();
        for (var n = 0; n < inputs.Count; n++)
        {
            var input = inputs[n];
            var field = $"decisions[{n}]";
            if (!items.TryGetValue(input.ItemId, out var item))
            {
                errors[field] = ["That entry is not part of this review."];
                continue;
            }
            if (!item.RequiresDecision)
            {
                errors[field] = [$"{item.TrusteeName} does not need a decision: it is an administrative or deny entry."];
                continue;
            }
            if (!Enum.TryParse<DecisionKind>(input.Decision, true, out var kind) || !Enum.IsDefined(kind) || int.TryParse(input.Decision, out _))
            {
                errors[field] = ["Use Keep, Remove, Investigate or Exception."];
                continue;
            }
            if (DecisionRules.Validate(kind, input.Justification, input.ExceptionExpiresOn, today) is { } message)
            {
                errors[field] = [$"{item.TrusteeName}: {message}"];
                continue;
            }
            var justification = string.IsNullOrWhiteSpace(input.Justification) ? null : input.Justification.Trim();
            if (latest.TryGetValue(item.Id, out var current) && current.Latest.Decision == kind
                && current.Latest.Justification == justification && current.Latest.ExceptionExpiresOn == input.ExceptionExpiresOn)
            {
                continue; // Unchanged; no new revision.
            }
            added.Add(new ReviewDecision
            {
                ReviewId = reviewId,
                ItemId = item.Id,
                Decision = kind,
                Justification = justification,
                ExceptionExpiresOn = kind == DecisionKind.Exception ? input.ExceptionExpiresOn : null,
                DecidedBySid = actor.Sid,
                DecidedByName = actor.Name,
                DecidedAs = role,
                DecidedAt = now,
            });
        }
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }
        if (added.Count > 0)
        {
            db.ReviewDecisions.AddRange(added);
            review.State = ReviewState.InProgress;
            await db.SaveChangesAsync(ct);
            await audit.AppendAsync(actor.Sid, actor.Name, "review.decide", review.Resource, new
            {
                ReviewId = reviewId,
                As = role,
                Keep = added.Count(d => d.Decision == DecisionKind.Keep),
                Remove = added.Count(d => d.Decision == DecisionKind.Remove),
                Investigate = added.Count(d => d.Decision == DecisionKind.Investigate),
                Exception = added.Count(d => d.Decision == DecisionKind.Exception),
            }, ct);
        }
        await tx.CommitAsync(ct);
        return Results.Ok(await ReviewViews.DetailAsync(db, reviewId, actor.Sid, viewerIsReviewer: true, clock.GetUtcNow(), ct));
    }

    public async Task<IResult> SubmitAsync(Guid reviewId, SubmitRequest request, Actor actor, CancellationToken ct)
    {
        if (!request.Confirm)
        {
            return Invalid("confirm", "Confirm that these decisions are yours and final.");
        }
        var now = CampaignBuilder.Microseconds(clock.GetUtcNow());
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (review, problem) = await LockOpenAsync(reviewId, actor, ct);
        if (review is null)
        {
            return problem!;
        }
        var role = ReviewViews.RoleOf(review, actor.Sid)!;
        var items = await db.ReviewItems.AsNoTracking().Where(i => i.ReviewId == reviewId && i.RequiresDecision).OrderBy(i => i.Ordinal).ToListAsync(ct);
        var latest = await ReviewViews.LatestDecisionsAsync(db, [reviewId], ct);
        var undecided = items.Count(i => !latest.ContainsKey(i.Id));
        if (undecided > 0)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Some entries have no decision",
                detail: $"{undecided} of {items.Count} entries still need a decision before the review can be submitted.");
        }
        review.State = ReviewState.Submitted;
        review.SubmittedAt = now;
        review.SubmittedBySid = actor.Sid;
        review.SubmittedByName = actor.Name;
        var decisions = items.Select(i => latest[i.Id].Latest.Decision).ToList();
        var detail = $"Submitted as {role.ToLowerInvariant()}: {decisions.Count(d => d == DecisionKind.Keep)} keep, "
            + $"{decisions.Count(d => d == DecisionKind.Remove)} remove, {decisions.Count(d => d == DecisionKind.Investigate)} investigate, "
            + $"{decisions.Count(d => d == DecisionKind.Exception)} exception.";
        db.ReviewEvents.Add(new ReviewEvent { ReviewId = reviewId, Kind = "Submitted", ActorSid = actor.Sid, ActorName = actor.Name, At = now, Detail = detail });
        await db.SaveChangesAsync(ct);
        await audit.AppendAsync(actor.Sid, actor.Name, "review.submit", review.Resource, new
        {
            ReviewId = reviewId,
            As = role,
            Decisions = decisions.Count,
            review.EvidenceHash,
        }, ct);
        await tx.CommitAsync(ct);
        return Results.Ok(await ReviewViews.DetailAsync(db, reviewId, actor.Sid, viewerIsReviewer: true, clock.GetUtcNow(), ct));
    }

    /// <param name="asAdministrator">An administrator reassigning the review rather than its owner or escalated backup.</param>
    public async Task<IResult> DelegateAsync(Guid reviewId, DelegateRequest request, Actor actor, bool asAdministrator, CancellationToken ct)
    {
        var now = CampaignBuilder.Microseconds(clock.GetUtcNow());
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var review = await LockAsync(reviewId, ct);
        if (review is null || (!asAdministrator && ReviewViews.RoleOf(review, actor.Sid) is null))
        {
            return Results.NotFound();
        }
        if (!asAdministrator && ReviewViews.RoleOf(review, actor.Sid) is not ("Owner" or "Backup"))
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Only the owner can hand this review to someone else.");
        }
        var campaign = await db.ReviewCampaigns.AsNoTracking().SingleAsync(c => c.Id == review.CampaignId, ct);
        if (campaign.State != CampaignState.Open || review.State == ReviewState.Submitted)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "This review is finished and cannot be handed over.");
        }
        string? sid = null, name = null, email = null;
        if (!string.IsNullOrWhiteSpace(request.DelegateSid))
        {
            var snapshot = await directory.LatestCompletedAsync(ct);
            if (snapshot is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Sync Active Directory first", detail: "The person is chosen from Active Directory.");
            }
            var wanted = request.DelegateSid.Trim().ToUpperInvariant();
            var person = await db.DirectoryPrincipals.AsNoTracking().Where(p => p.SnapshotId == snapshot.Id && p.Sid == wanted)
                .Select(p => new { p.Sid, p.Kind, p.Name, p.DisplayName, p.Enabled, p.Mail }).SingleOrDefaultAsync(ct);
            if (person is null || person.Kind != PrincipalKind.User || !person.Enabled)
            {
                return Invalid("delegateSid", "Choose an enabled user from the latest Active Directory sync.");
            }
            if (string.Equals(person.Sid, review.OwnerSid, StringComparison.OrdinalIgnoreCase) || string.Equals(person.Sid, actor.Sid, StringComparison.OrdinalIgnoreCase))
            {
                return Invalid("delegateSid", "Choose someone other than yourself and the owner.");
            }
            sid = person.Sid;
            name = person.DisplayName is { Length: > 0 } display ? $"{display} ({person.Name})" : person.Name;
            email = person.Mail;
        }
        if (string.Equals(sid, review.DelegateSid, StringComparison.OrdinalIgnoreCase))
        {
            await tx.CommitAsync(ct);
            return Results.Ok(await ReviewViews.DetailAsync(db, reviewId, actor.Sid, !asAdministrator, clock.GetUtcNow(), ct));
        }
        var previous = review.DelegateName;
        review.DelegateSid = sid;
        review.DelegateName = name;
        review.DelegateEmail = email;
        db.ReviewEvents.Add(new ReviewEvent
        {
            ReviewId = reviewId,
            Kind = sid is null ? "DelegationEnded" : "Delegated",
            ActorSid = actor.Sid,
            ActorName = actor.Name,
            At = now,
            Detail = sid is null ? $"Taken back from {previous}." : $"Handed to {name}{(asAdministrator ? " by an administrator" : "")}. The owner can still decide.",
        });
        await db.SaveChangesAsync(ct);
        await audit.AppendAsync(actor.Sid, actor.Name, "review.delegate", review.Resource,
            new { ReviewId = reviewId, Delegate = sid, Previous = previous, AsAdministrator = asAdministrator }, ct);
        if (sid is not null)
        {
            await reminders.NotifyDelegateAsync(review, campaign.Name, campaign.DueOn, ReviewLink, ct);
        }
        await tx.CommitAsync(ct);
        return Results.Ok(await ReviewViews.DetailAsync(db, reviewId, actor.Sid, !asAdministrator, clock.GetUtcNow(), ct));
    }

    /// <summary>Locks a review the actor may decide on; a review that is not theirs is reported as not found.</summary>
    private async Task<(AccessReview? Review, IResult? Problem)> LockOpenAsync(Guid reviewId, Actor actor, CancellationToken ct)
    {
        var review = await LockAsync(reviewId, ct);
        if (review is null || ReviewViews.RoleOf(review, actor.Sid) is null)
        {
            return (null, Results.NotFound());
        }
        var campaign = await db.ReviewCampaigns.AsNoTracking().Where(c => c.Id == review.CampaignId).Select(c => c.State).SingleAsync(ct);
        if (campaign != CampaignState.Open)
        {
            return (null, Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The campaign is closed. Nothing more can be decided."));
        }
        if (review.State == ReviewState.Submitted)
        {
            return (null, Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "This review was already submitted. Its decisions are final."));
        }
        return (review, null);
    }

    private Task<AccessReview?> LockAsync(Guid reviewId, CancellationToken ct) =>
        db.AccessReviews.FromSql($"SELECT *, xmin FROM access_reviews WHERE id = {reviewId} FOR UPDATE").SingleOrDefaultAsync(ct);

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
