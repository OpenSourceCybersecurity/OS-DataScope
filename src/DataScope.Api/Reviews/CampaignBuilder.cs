using System.Text.Json;
using DataScope.Api.Access;
using DataScope.Api.Auth;
using DataScope.Core.Access;
using DataScope.Core.Governance;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Reviews;

/// <param name="Frequency">Only owned resources with this review frequency; all when null.</param>
/// <param name="ShareIds">Only these shares (any owned folder on them); all when null or empty.</param>
public sealed record CampaignRequest(string? Name, string? Description, DateOnly? DueOn, string? Frequency, IReadOnlyList<Guid>? ShareIds);

public sealed class CampaignException(string field, string message) : Exception(message)
{
    public string Field { get; } = field;
}

/// <summary>
/// Creates a campaign by freezing, for every owned folder in scope, the folder's permissions and the people they reach
/// in the latest directory sync. Nothing frozen ever changes afterwards; each review carries a hash of its contents.
/// </summary>
internal sealed class CampaignBuilder(DataScopeDbContext db, AccessService access, AuditLogWriter audit, TimeProvider clock)
{
    public const int MaxReviewsPerCampaign = 500;

    public async Task<ReviewCampaign> CreateAsync(CampaignRequest request, Actor actor, CancellationToken ct)
    {
        var now = Microseconds(clock.GetUtcNow());
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var name = request.Name?.Trim() ?? "";
        if (name.Length is < 3 or > 200)
        {
            throw new CampaignException("name", "Give the campaign a name of 3 to 200 characters, for example \"Q4 2026 access review\".");
        }
        var description = request.Description?.Trim();
        if (description is { Length: > 2000 })
        {
            throw new CampaignException("description", "Keep the description under 2,000 characters.");
        }
        if (request.DueOn is not { } dueOn || dueOn <= today || dueOn > today.AddDays(366))
        {
            throw new CampaignException("dueOn", "Choose a due date after today and within a year.");
        }
        ReviewFrequency? frequency = null;
        if (!string.IsNullOrWhiteSpace(request.Frequency))
        {
            if (!Enum.TryParse<ReviewFrequency>(request.Frequency, true, out var parsed) || !Enum.IsDefined(parsed) || int.TryParse(request.Frequency, out _))
            {
                throw new CampaignException("frequency", "Use Quarterly, Semiannual or Annual, or leave it empty for every owned share and folder.");
            }
            frequency = parsed;
        }
        var shareIds = request.ShareIds is { Count: > 0 } list ? list.Distinct().ToList() : null;

        var candidates = await (from a in db.OwnerAssignments.AsNoTracking()
                                join f in db.Folders on a.FolderId equals f.Id
                                join s in db.Shares on a.ShareId equals s.Id
                                where a.EndedAt == null && f.RemovedAt == null && s.State != ShareState.Removed
                                    && (frequency == null || a.ReviewFrequency == frequency)
                                    && (shareIds == null || shareIds.Contains(a.ShareId))
                                orderby s.Name, f.Path
                                select a).ToListAsync(ct);
        if (candidates.Count == 0)
        {
            throw new CampaignException("scope", "No owned shares or folders match. Assign owners on the Owners screen first.");
        }
        if (candidates.Count > MaxReviewsPerCampaign)
        {
            throw new CampaignException("scope", $"That is {candidates.Count} reviews; a campaign can hold {MaxReviewsPerCampaign}. Narrow it by frequency or share.");
        }

        var snapshot = await access.SnapshotAsync(ct);
        var people = snapshot is null
            ? new Dictionary<string, PersonInfo>(StringComparer.Ordinal)
            : await db.DirectoryPrincipals.AsNoTracking()
                .Where(p => p.SnapshotId == snapshot.Id && p.Kind == PrincipalKind.User)
                .Select(p => new { p.Sid, p.Department, p.Title })
                .ToDictionaryAsync(p => p.Sid, p => new PersonInfo(p.Department, p.Title), StringComparer.Ordinal, ct);
        var mail = snapshot is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : await db.DirectoryPrincipals.AsNoTracking()
                .Where(p => p.SnapshotId == snapshot.Id && p.Kind == PrincipalKind.User && p.Mail != null)
                .Select(p => new { p.Sid, p.Mail })
                .ToDictionaryAsync(p => p.Sid, p => p.Mail!, StringComparer.Ordinal, ct);

        var noDecision = (await db.ReviewExemptions.AsNoTracking().Where(x => x.EndedAt == null).Select(x => x.Sid).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var campaign = new ReviewCampaign
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            Description = string.IsNullOrEmpty(description) ? null : description,
            DueOn = dueOn,
            State = CampaignState.Open,
            Scope = JsonSerializer.Serialize(new { Frequency = frequency?.ToString(), ShareIds = shareIds }),
            Skipped = "[]",
            DirectorySnapshotId = snapshot?.Id,
            DirectorySyncedAt = snapshot?.CompletedAt,
            Domain = snapshot?.Domain,
            CreatedBySid = actor.Sid,
            CreatedByName = actor.Name,
            CreatedAt = now,
        };
        var skipped = new List<SkippedDto>();
        var frozen = new List<(AccessReview Review, IReadOnlyList<FrozenItem> Items)>();
        foreach (var assignment in candidates)
        {
            var folder = await access.FolderAsync(assignment.FolderId, ct);
            if (folder is null)
            {
                continue;
            }
            var resource = Unc(folder.Hostname, folder.ShareName, folder.Path);
            if (folder.FolderState != PermissionState.Read)
            {
                skipped.Add(new SkippedDto(folder.FolderId, resource, folder.FolderState switch
                {
                    PermissionState.NotRead => "The folder's permissions have not been read yet. Scan the server first.",
                    PermissionState.AccessDenied => "The scan account may not read this folder's permissions.",
                    _ => "The folder's permissions could not be interpreted.",
                }));
                continue;
            }
            var (graph, _) = await access.GraphAsync(folder.ServerId, ct);
            var place = await access.PermissionsAsync(folder.ShareState, folder.ShareSecurityId, folder.FolderState, folder.FolderSecurityId, ct);
            var items = ReviewSnapshotBuilder.BuildItems(graph, place.Permissions, sid => people.GetValueOrDefault(sid), noDecision: noDecision)
                .Select(Fit).ToList();
            var notes = ReviewSnapshotBuilder.Notes(graph, place.Permissions, snapshot is not null).ToList();
            var listed = items.Count(i => i.Flags.Contains(ReviewSnapshotBuilder.NoDecisionNeeded));
            if (listed > 0)
            {
                notes.Insert(0, listed == 1
                    ? "1 entry is for an account your administrators manage, so it needs no decision and is not shown to owners."
                    : $"{listed} entries are for accounts your administrators manage, so they need no decision and are not shown to owners.");
            }
            var review = new AccessReview
            {
                Id = Guid.CreateVersion7(),
                CampaignId = campaign.Id,
                FolderId = folder.FolderId,
                ShareId = folder.ShareId,
                ServerId = folder.ServerId,
                Resource = resource,
                OwnerSid = assignment.OwnerSid,
                OwnerName = assignment.OwnerName,
                OwnerEmail = assignment.OwnerEmail ?? mail.GetValueOrDefault(assignment.OwnerSid),
                BackupSid = assignment.BackupSid,
                BackupName = assignment.BackupName,
                BackupEmail = assignment.BackupSid is null ? null : assignment.BackupEmail ?? mail.GetValueOrDefault(assignment.BackupSid),
                State = ReviewState.Pending,
                ShareState = folder.ShareState,
                FolderState = folder.FolderState,
                ShareSecurityId = folder.ShareSecurityId,
                FolderSecurityId = folder.FolderSecurityId,
                PermissionsReadAt = folder.FolderReadAt,
                Notes = [.. notes],
                ItemCount = items.Count,
                DecisionsRequired = items.Count(i => i.RequiresDecision),
                FrozenAt = now,
                EvidenceHash = "",
            };
            review.EvidenceHash = new FrozenReview(
                campaign.Id, review.Id, review.FolderId, review.Resource, review.OwnerSid, review.OwnerName, review.BackupSid,
                campaign.DirectorySnapshotId, campaign.DirectorySyncedAt, review.ShareState.ToString(), review.FolderState.ToString(),
                review.ShareSecurityId, review.FolderSecurityId, review.FrozenAt, review.Notes, items).Hash();
            frozen.Add((review, items));
        }
        if (frozen.Count == 0)
        {
            throw new CampaignException("scope", "None of the owned folders in scope has readable permissions yet: " + skipped[0].Reason);
        }
        campaign.Skipped = JsonSerializer.Serialize(skipped);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        db.ReviewCampaigns.Add(campaign);
        foreach (var (review, items) in frozen)
        {
            db.AccessReviews.Add(review);
            db.ReviewEvents.Add(new ReviewEvent
            {
                ReviewId = review.Id,
                Kind = "Created",
                ActorSid = actor.Sid,
                ActorName = actor.Name,
                At = now,
                Detail = $"Frozen with {review.ItemCount} permission entries, {review.DecisionsRequired} needing a decision.",
            });
        }
        await db.SaveChangesAsync(ct);
        foreach (var (review, items) in frozen)
        {
            var rows = items.Select(i => (Item: i, Row: new ReviewItem
            {
                ReviewId = review.Id,
                Ordinal = i.Ordinal,
                TrusteeSid = i.TrusteeSid,
                TrusteeName = i.TrusteeName,
                TrusteeKind = i.TrusteeKind,
                Effect = i.Effect,
                Rights = i.Rights,
                AppliesTo = i.AppliesTo,
                Inherited = i.Inherited,
                Broad = i.Broad,
                Administrative = i.Administrative,
                RequiresDecision = i.RequiresDecision,
                UsersCertain = i.UsersCertain,
                UsersPossible = i.UsersPossible,
                UsersDisabled = i.UsersDisabled,
                UsersWithOtherAccess = i.UsersWithOtherAccess,
                UsersTruncated = i.UsersTruncated,
                Flags = [.. i.Flags],
            })).ToList();
            db.ReviewItems.AddRange(rows.Select(r => r.Row));
            await db.SaveChangesAsync(ct);
            foreach (var (item, row) in rows)
            {
                db.ReviewItemUsers.AddRange(item.Users.Select(u => new ReviewItemUser
                {
                    ItemId = row.Id,
                    Ordinal = u.Ordinal,
                    Sid = u.Sid,
                    Name = u.Name,
                    Enabled = u.Enabled,
                    Department = u.Department,
                    Title = u.Title,
                    Certain = u.Certain,
                    Chain = [.. u.Chain],
                    Rights = u.Rights,
                    UpTo = u.UpTo,
                    Certainty = u.Certainty,
                    AlsoVia = u.AlsoVia,
                }));
            }
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }
        db.ChangeTracker.AutoDetectChangesEnabled = true;
        await audit.AppendAsync(actor.Sid, actor.Name, "campaign.create", name, new
        {
            campaign.Id,
            DueOn = dueOn.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            Frequency = frequency?.ToString(),
            ShareIds = shareIds,
            Reviews = frozen.Count,
            Skipped = skipped.Count,
            DirectorySnapshot = snapshot?.Id,
            NoDecisionAccounts = noDecision.Count,
        }, ct);
        await tx.CommitAsync(ct);
        return campaign;
    }

    public static string Unc(string host, string share, string path) =>
        path.Length == 0 ? $@"\\{host}\{share}" : $@"\\{host}\{share}\{path}";

    /// <summary>PostgreSQL keeps microseconds; rounding first lets a frozen review's hash verify after a round trip.</summary>
    public static DateTimeOffset Microseconds(DateTimeOffset t) => new(t.UtcTicks - (t.UtcTicks % 10), TimeSpan.Zero);

    /// <summary>Trims text to the column sizes before the hash is taken, so stored rows match what was sealed.</summary>
    private static FrozenItem Fit(FrozenItem i) => i with
    {
        TrusteeName = Cut(i.TrusteeName, 256)!,
        AppliesTo = Cut(i.AppliesTo, 500)!,
        Users = i.Users.Select(u => u with
        {
            Name = Cut(u.Name, 256)!,
            Department = Cut(u.Department, 128),
            Title = Cut(u.Title, 128),
            AlsoVia = Cut(u.AlsoVia, 800),
            Chain = u.Chain.Select(c => Cut(c, 256)!).ToList(),
        }).ToList(),
    };

    private static string? Cut(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}
