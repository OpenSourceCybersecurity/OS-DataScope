using DataScope.Api.Reviews;
using DataScope.Core.Governance;
using DataScope.Core.Security;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Reports;

/// <summary>Every frozen review entry across campaigns with the decision in force, who made it and why.</summary>
internal sealed class ReviewEvidenceReport : IReport
{
    public ReportInfo Info { get; } = new("review-evidence", "Access certification evidence",
        "Every entry owners reviewed, as frozen when each campaign started, with the decision in force, who made it and the reason.",
        Policies.ReadReviews);

    public async Task<ReportDocument> BuildAsync(ReportContext ctx, DirectoryStore directory, CancellationToken ct)
    {
        var db = ctx.Db;
        var reviews = await (from r in db.AccessReviews.AsNoTracking()
                             join c in db.ReviewCampaigns on r.CampaignId equals c.Id
                             where (ctx.Scope.ShareId == null || r.ShareId == ctx.Scope.ShareId) && (ctx.Scope.ServerId == null || r.ServerId == ctx.Scope.ServerId)
                             orderby c.CreatedAt descending, r.Resource
                             select new { Review = r, c.Name, CampaignState = c.State }).ToListAsync(ct);
        var rows = new List<object?[]>();
        var sealsBroken = 0;
        foreach (var x in reviews)
        {
            if (rows.Count > ctx.RowLimit)
            {
                break;
            }
            var r = x.Review;
            var intact = (await ReviewViews.FrozenAsync(db, r.Id, ct))!.Hash() == r.EvidenceHash.Trim();
            sealsBroken += intact ? 0 : 1;
            var items = await db.ReviewItems.AsNoTracking().Where(i => i.ReviewId == r.Id).OrderBy(i => i.Ordinal).ToListAsync(ct);
            var latest = await ReviewViews.LatestDecisionsAsync(db, [r.Id], ct);
            foreach (var i in items)
            {
                var d = latest.TryGetValue(i.Id, out var found) ? found.Latest : null;
                rows.Add(
                [
                    x.Name, r.FrozenAt, r.Resource, r.OwnerName, r.State.ToString(), r.SubmittedAt, i.Ordinal + 1, i.TrusteeName, i.TrusteeKind,
                    i.Effect, i.Rights, i.UsersCertain, i.UsersPossible, i.RequiresDecision ? d?.Decision.ToString() ?? "Not decided" : ReviewViews.IsListed(i) ? "No decision needed (administrators' list)" : "No decision needed",
                    d?.Justification, d?.ExceptionExpiresOn, d?.DecidedByName, d?.DecidedAt,
                    intact ? "Verified" : "DOES NOT MATCH",
                ]);
            }
            db.ChangeTracker.Clear();
        }

        var facts = await ctx.CommonFactsAsync(ct);
        facts.Add(new("Reviews", $"{reviews.Count:N0} reviews in {reviews.Select(r => r.Name).Distinct().Count():N0} campaigns" +
            (sealsBroken > 0 ? $". WARNING: {sealsBroken} reviews no longer match the hash taken when they were frozen" : ". Every review read so far matches the hash taken when it was frozen")));
        return ctx.Document(Info, facts,
        [
            "Each review shows permissions and group memberships as they were when its campaign started, not as they are now.",
            "Shows the decision in force for each entry. Earlier revisions, the people each entry reached, and the full review history are in the campaign's JSON evidence export.",
            "\"Seal\" compares each review with the SHA-256 hash taken when it was frozen. A mismatch means stored evidence changed and should be investigated.",
            "A Remove decision does not change anything in Windows or Active Directory. It records what the owner wants; IT makes any change in Windows.",
        ],
        ["Campaign", "Frozen", "Resource", "Owner", "Review state", "Submitted", "Entry", "Group or account", "Type", "Effect", "Rights",
         "People reached", "People possibly reached", "Decision", "Reason", "Exception ends", "Decided by", "Decided", "Seal"],
        rows);
    }
}
