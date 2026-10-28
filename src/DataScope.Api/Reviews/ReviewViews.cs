using DataScope.Core.Governance;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Reviews;

public sealed record SkippedDto(Guid FolderId, string Resource, string Reason);

public sealed record CampaignDto(
    Guid Id, string Name, string? Description, DateOnly DueOn, string State, bool Overdue, DateTimeOffset CreatedAt,
    string CreatedByName, DateTimeOffset? ClosedAt, string? ClosedByName, DateTimeOffset? DirectorySyncedAt, string? Domain,
    int Reviews, int Submitted, int InProgress, int Pending, int DecisionsRequired, int Decided, IReadOnlyList<SkippedDto> Skipped);

/// <param name="MyRole">How the signed-in person relates to the review: Owner, Delegate, Backup, or null.</param>
public sealed record ReviewSummaryDto(
    Guid Id, Guid CampaignId, string CampaignName, string CampaignState, DateOnly DueOn, bool Overdue, string Resource,
    Guid FolderId, Guid ShareId, string OwnerSid, string OwnerName, string? BackupName, string? DelegateSid, string? DelegateName,
    string State, int ItemCount, int DecisionsRequired, int Decided, int Remove, int Investigate, int Exception,
    DateTimeOffset? SubmittedAt, string? SubmittedByName, DateTimeOffset? EscalatedAt, string? MyRole);

public sealed record DecisionDto(string Decision, string? Justification, DateOnly? ExceptionExpiresOn, string DecidedByName, string DecidedAs, DateTimeOffset DecidedAt);

public sealed record ItemDto(
    long Id, int Ordinal, string TrusteeSid, string TrusteeName, string TrusteeKind, string Effect, string Rights, string AppliesTo,
    bool Inherited, bool Broad, bool Administrative, bool RequiresDecision, int UsersCertain, int UsersPossible, int UsersDisabled,
    int UsersWithOtherAccess, bool UsersTruncated, IReadOnlyList<string> Flags, DecisionDto? Decision, int Revisions);

public sealed record EventDto(string Kind, string ActorName, string Detail, DateTimeOffset At);

public sealed record ReviewDetailDto(
    ReviewSummaryDto Review, IReadOnlyList<string> Notes, string ShareState, string FolderState, DateTimeOffset? PermissionsReadAt,
    DateTimeOffset FrozenAt, DateTimeOffset? DirectorySyncedAt, string EvidenceHash, bool CanDecide, bool CanDelegate,
    IReadOnlyList<ItemDto> Items, IReadOnlyList<EventDto> Events, int HiddenItems);

public sealed record ItemUserDto(
    string Sid, string Name, bool Enabled, string? Department, string? Title, bool Certain, IReadOnlyList<string> Chain,
    string Rights, string UpTo, string Certainty, string? AlsoVia);

/// <summary>Shared queries and mappings for campaigns and reviews.</summary>
internal static class ReviewViews
{
    /// <summary>The capacity a person acts in for a review, or null when it is not theirs.</summary>
    public static string? RoleOf(AccessReview r, string sid) =>
        string.Equals(sid, r.OwnerSid, StringComparison.OrdinalIgnoreCase) ? "Owner"
        : r.DelegateSid is not null && string.Equals(sid, r.DelegateSid, StringComparison.OrdinalIgnoreCase) ? "Delegate"
        : r.BackupSid is not null && r.EscalatedAt is not null && string.Equals(sid, r.BackupSid, StringComparison.OrdinalIgnoreCase) ? "Backup"
        : null;

    /// <summary>Reviews a person may open: theirs, delegated to them, or escalated to them as backup owner.</summary>
    public static IQueryable<AccessReview> Mine(DataScopeDbContext db, string sid)
    {
        // SIDs are stored in their canonical upper-case form.
        var upper = sid.ToUpperInvariant();
        return db.AccessReviews.Where(r => r.OwnerSid == upper
            || r.DelegateSid == upper
            || (r.BackupSid == upper && r.EscalatedAt != null));
    }

    /// <summary>The newest decision per item of the given reviews.</summary>
    public static async Task<Dictionary<long, (ReviewDecision Latest, int Revisions)>> LatestDecisionsAsync(DataScopeDbContext db, IReadOnlyCollection<Guid> reviewIds, CancellationToken ct)
    {
        var rows = await db.ReviewDecisions.AsNoTracking().Where(d => reviewIds.Contains(d.ReviewId)).ToListAsync(ct);
        return rows.GroupBy(d => d.ItemId).ToDictionary(g => g.Key, g => (g.MaxBy(d => d.Id)!, g.Count()));
    }

    public static async Task<List<ReviewSummaryDto>> SummariesAsync(DataScopeDbContext db, IQueryable<AccessReview> reviews, string? viewerSid, DateTimeOffset now, CancellationToken ct)
    {
        var rows = await (from x in reviews.AsNoTracking()
                          join c in db.ReviewCampaigns on x.CampaignId equals c.Id
                          orderby c.DueOn, x.Resource
                          select new { Review = x, c.Name, c.State, c.DueOn }).ToListAsync(ct);
        var ids = rows.Select(x => x.Review.Id).ToList();
        var latest = await LatestDecisionsAsync(db, ids, ct);
        var required = await db.ReviewItems.AsNoTracking().Where(i => ids.Contains(i.ReviewId) && i.RequiresDecision)
            .Select(i => new { i.Id, i.ReviewId }).ToListAsync(ct);
        var byReview = required.ToLookup(i => i.ReviewId, i => i.Id);
        return rows.Select(x =>
        {
            var decisions = byReview[x.Review.Id].Where(latest.ContainsKey).Select(i => latest[i].Latest.Decision).ToList();
            return Summary(x.Review, x.Name, x.State, x.DueOn, decisions, viewerSid, now);
        }).ToList();
    }

    public static ReviewSummaryDto Summary(AccessReview r, string campaign, CampaignState campaignState, DateOnly dueOn, IReadOnlyList<DecisionKind> decisions, string? viewerSid, DateTimeOffset now) => new(
        r.Id, r.CampaignId, campaign, campaignState.ToString(), dueOn,
        r.State != ReviewState.Submitted && campaignState == CampaignState.Open && ReviewSchedule.IsOverdue(dueOn, now),
        r.Resource, r.FolderId, r.ShareId, r.OwnerSid, r.OwnerName, r.BackupName, r.DelegateSid, r.DelegateName, r.State.ToString(),
        r.ItemCount, r.DecisionsRequired, decisions.Count,
        decisions.Count(d => d == DecisionKind.Remove), decisions.Count(d => d == DecisionKind.Investigate), decisions.Count(d => d == DecisionKind.Exception),
        r.SubmittedAt, r.SubmittedByName, r.EscalatedAt, viewerSid is null ? null : RoleOf(r, viewerSid));

    /// <param name="viewerIsReviewer">
    /// True in the owner portal: entries for accounts on the administrators' no-decision list are left out and only counted.
    /// </param>
    public static async Task<ReviewDetailDto?> DetailAsync(DataScopeDbContext db, Guid id, string? viewerSid, bool viewerIsReviewer, DateTimeOffset now, CancellationToken ct)
    {
        var row = await (from x in db.AccessReviews.AsNoTracking()
                         join c in db.ReviewCampaigns on x.CampaignId equals c.Id
                         where x.Id == id
                         select new { Review = x, Campaign = c }).SingleOrDefaultAsync(ct);
        if (row is null)
        {
            return null;
        }
        var r = row.Review;
        var items = await db.ReviewItems.AsNoTracking().Where(i => i.ReviewId == id).OrderBy(i => i.Ordinal).ToListAsync(ct);
        var shown = viewerIsReviewer ? items.Where(i => !IsListed(i)).ToList() : items;
        var latest = await LatestDecisionsAsync(db, [id], ct);
        var events = await db.ReviewEvents.AsNoTracking().Where(e => e.ReviewId == id).OrderBy(e => e.At).ThenBy(e => e.Id)
            .Select(e => new EventDto(e.Kind, e.ActorName, e.Detail, e.At)).ToListAsync(ct);
        var decided = items.Where(i => i.RequiresDecision && latest.ContainsKey(i.Id)).Select(i => latest[i.Id].Latest.Decision).ToList();
        var role = viewerSid is null ? null : RoleOf(r, viewerSid);
        var open = row.Campaign.State == CampaignState.Open && r.State != ReviewState.Submitted;
        return new ReviewDetailDto(
            Summary(r, row.Campaign.Name, row.Campaign.State, row.Campaign.DueOn, decided, viewerSid, now),
            r.Notes, r.ShareState.ToString(), r.FolderState.ToString(), r.PermissionsReadAt, r.FrozenAt, row.Campaign.DirectorySyncedAt,
            r.EvidenceHash.Trim(),
            viewerIsReviewer && open && role is not null,
            viewerIsReviewer && open && role is "Owner" or "Backup",
            shown.Select(i => ToDto(i, latest.TryGetValue(i.Id, out var d) ? d : null)).ToList(),
            events, items.Count - shown.Count);
    }

    /// <summary>An entry for an account the administrators listed as needing no decision.</summary>
    public static bool IsListed(ReviewItem i) => i.Flags.Contains(ReviewSnapshotBuilder.NoDecisionNeeded);

    public static ItemDto ToDto(ReviewItem i, (ReviewDecision Latest, int Revisions)? decision) => new(
        i.Id, i.Ordinal, i.TrusteeSid, i.TrusteeName, i.TrusteeKind, i.Effect, i.Rights, i.AppliesTo, i.Inherited, i.Broad,
        i.Administrative, i.RequiresDecision, i.UsersCertain, i.UsersPossible, i.UsersDisabled, i.UsersWithOtherAccess,
        i.UsersTruncated, i.Flags,
        decision is { } d ? new DecisionDto(d.Latest.Decision.ToString(), d.Latest.Justification, d.Latest.ExceptionExpiresOn, d.Latest.DecidedByName, d.Latest.DecidedAs, d.Latest.DecidedAt) : null,
        decision?.Revisions ?? 0);

    /// <param name="hideListed">Owner portal: no people for entries on the administrators' no-decision list.</param>
    public static async Task<List<ItemUserDto>> ItemUsersAsync(DataScopeDbContext db, Guid reviewId, long itemId, string? search, int limit, int offset, CancellationToken ct, bool hideListed = false)
    {
        var users = from u in db.ReviewItemUsers.AsNoTracking()
                    join i in db.ReviewItems on u.ItemId equals i.Id
                    where i.ReviewId == reviewId && u.ItemId == itemId && !(hideListed && i.Flags.Contains(ReviewSnapshotBuilder.NoDecisionNeeded))
                    select u;
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = $"%{search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
            users = users.Where(u => EF.Functions.ILike(u.Name, term) || (u.Department != null && EF.Functions.ILike(u.Department, term)));
        }
        return await users.OrderBy(u => u.Ordinal).Skip(offset).Take(limit)
            .Select(u => new ItemUserDto(u.Sid, u.Name, u.Enabled, u.Department, u.Title, u.Certain, u.Chain, u.Rights, u.UpTo, u.Certainty, u.AlsoVia))
            .ToListAsync(ct);
    }

    /// <summary>Rebuilds the frozen review from stored rows, so its hash can be checked against the one sealed at creation.</summary>
    public static async Task<FrozenReview?> FrozenAsync(DataScopeDbContext db, Guid reviewId, CancellationToken ct)
    {
        var row = await (from x in db.AccessReviews.AsNoTracking()
                         join c in db.ReviewCampaigns on x.CampaignId equals c.Id
                         where x.Id == reviewId
                         select new { Review = x, c.DirectorySnapshotId, c.DirectorySyncedAt }).SingleOrDefaultAsync(ct);
        if (row is null)
        {
            return null;
        }
        var r = row.Review;
        var items = await db.ReviewItems.AsNoTracking().Where(i => i.ReviewId == reviewId).OrderBy(i => i.Ordinal).ToListAsync(ct);
        var ids = items.Select(i => i.Id).ToList();
        var users = (await db.ReviewItemUsers.AsNoTracking().Where(u => ids.Contains(u.ItemId)).ToListAsync(ct))
            .ToLookup(u => u.ItemId);
        var frozenItems = items.Select(i => new FrozenItem(
            i.Ordinal, i.TrusteeSid, i.TrusteeName, i.TrusteeKind, i.Effect, i.Rights, i.AppliesTo, i.Inherited, i.Broad, i.Administrative,
            i.RequiresDecision, i.UsersCertain, i.UsersPossible, i.UsersDisabled, i.UsersWithOtherAccess, i.UsersTruncated, i.Flags,
            users[i.Id].OrderBy(u => u.Ordinal).Select(u => new FrozenUser(
                u.Ordinal, u.Sid, u.Name, u.Enabled, u.Department, u.Title, u.Certain, u.Chain, u.Rights, u.UpTo, u.Certainty, u.AlsoVia)).ToList()))
            .ToList();
        return new FrozenReview(
            r.CampaignId, r.Id, r.FolderId, r.Resource, r.OwnerSid, r.OwnerName, r.BackupSid, row.DirectorySnapshotId, row.DirectorySyncedAt,
            r.ShareState.ToString(), r.FolderState.ToString(), r.ShareSecurityId, r.FolderSecurityId, r.FrozenAt, r.Notes, frozenItems);
    }
}
