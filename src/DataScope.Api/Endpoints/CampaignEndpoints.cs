using System.Security.Claims;
using System.Text.Json;
using DataScope.Api.Auth;
using DataScope.Api.Reviews;
using DataScope.Core.Governance;
using DataScope.Core.Security;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Endpoints;

/// <summary>
/// Access review campaigns for administrators and auditors: create (freeze), follow, close, verify and export. Owners
/// work on their own reviews through <see cref="MyReviewEndpoints"/>.
/// </summary>
internal static class CampaignEndpoints
{
    public sealed record CampaignDetailDto(CampaignDto Campaign, IReadOnlyList<ReviewSummaryDto> Reviews);

    public sealed record CloseRequest(string? Note);

    /// <param name="Mismatched">Reviews whose stored contents no longer match the hash sealed when they were frozen.</param>
    public sealed record VerifyDto(Guid CampaignId, int Reviews, int Verified, IReadOnlyList<VerifyMismatchDto> Mismatched, DateTimeOffset CheckedAt);

    public sealed record VerifyMismatchDto(Guid ReviewId, string Resource, string Expected, string Actual);

    private static readonly JsonSerializerOptions EvidenceJson = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public static void MapCampaigns(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/campaigns").RequireAuthorization(Policies.ReadReviews);

        group.MapGet("/", async (DataScopeDbContext db, TimeProvider clock, CancellationToken ct) =>
            Results.Ok(await CampaignsAsync(db, db.ReviewCampaigns.OrderByDescending(c => c.CreatedAt), clock.GetUtcNow(), ct)));

        group.MapPost("/", async (CampaignRequest request, ClaimsPrincipal user, CampaignBuilder builder, DataScopeDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            try
            {
                var campaign = await builder.CreateAsync(request, Actor.From(user), ct);
                var dto = (await CampaignsAsync(db, db.ReviewCampaigns.Where(c => c.Id == campaign.Id), clock.GetUtcNow(), ct)).Single();
                return Results.Created($"/api/v1/campaigns/{campaign.Id}", dto);
            }
            catch (CampaignException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { [ex.Field] = [ex.Message] });
            }
        }).RequireAuthorization(Policies.ManageReviews);

        group.MapGet("/{id:guid}", async (Guid id, DataScopeDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var now = clock.GetUtcNow();
            var campaign = (await CampaignsAsync(db, db.ReviewCampaigns.Where(c => c.Id == id), now, ct)).SingleOrDefault();
            if (campaign is null)
            {
                return Results.NotFound();
            }
            var reviews = await ReviewViews.SummariesAsync(db, db.AccessReviews.Where(r => r.CampaignId == id), null, now, ct);
            return Results.Ok(new CampaignDetailDto(campaign, reviews));
        });

        group.MapPost("/{id:guid}/close", async (Guid id, CloseRequest request, ClaimsPrincipal user, DataScopeDbContext db, AuditLogWriter audit, TimeProvider clock, CancellationToken ct) =>
        {
            var note = request.Note?.Trim();
            if (note is { Length: > 1000 })
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["note"] = ["Keep the note under 1,000 characters."] });
            }
            var actor = Actor.From(user);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var campaign = await db.ReviewCampaigns.FromSql($"SELECT *, xmin FROM review_campaigns WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(ct);
            if (campaign is null)
            {
                return Results.NotFound();
            }
            if (campaign.State == CampaignState.Closed)
            {
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The campaign is already closed.");
            }
            var now = CampaignBuilder.Microseconds(clock.GetUtcNow());
            var unfinished = await db.AccessReviews.Where(r => r.CampaignId == id && r.State != ReviewState.Submitted).Select(r => r.Id).ToListAsync(ct);
            campaign.State = CampaignState.Closed;
            campaign.ClosedAt = now;
            campaign.ClosedBySid = actor.Sid;
            campaign.ClosedByName = actor.Name;
            db.ReviewEvents.AddRange(unfinished.Select(r => new ReviewEvent
            {
                ReviewId = r,
                Kind = "ClosedUnsubmitted",
                ActorSid = actor.Sid,
                ActorName = actor.Name,
                At = now,
                Detail = "The campaign was closed before this review was submitted. Its decisions so far are kept but no tasks were created."
                    + (string.IsNullOrEmpty(note) ? "" : $" Note: {note}"),
            }));
            await db.SaveChangesAsync(ct);
            await audit.AppendAsync(actor.Sid, actor.Name, "campaign.close", campaign.Name, new { CampaignId = id, Unsubmitted = unfinished.Count, Note = note }, ct);
            await tx.CommitAsync(ct);
            return Results.Ok((await CampaignsAsync(db, db.ReviewCampaigns.Where(c => c.Id == id), clock.GetUtcNow(), ct)).Single());
        }).RequireAuthorization(Policies.ManageReviews);

        group.MapGet("/{id:guid}/verify", async (Guid id, DataScopeDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            if (!await db.ReviewCampaigns.AnyAsync(c => c.Id == id, ct))
            {
                return Results.NotFound();
            }
            var reviews = await db.AccessReviews.AsNoTracking().Where(r => r.CampaignId == id).OrderBy(r => r.Resource)
                .Select(r => new { r.Id, r.Resource, r.EvidenceHash }).ToListAsync(ct);
            var mismatched = new List<VerifyMismatchDto>();
            foreach (var r in reviews)
            {
                var actual = (await ReviewViews.FrozenAsync(db, r.Id, ct))!.Hash();
                if (!string.Equals(actual, r.EvidenceHash.Trim(), StringComparison.Ordinal))
                {
                    mismatched.Add(new VerifyMismatchDto(r.Id, r.Resource, r.EvidenceHash, actual));
                }
            }
            return Results.Ok(new VerifyDto(id, reviews.Count, reviews.Count - mismatched.Count, mismatched, clock.GetUtcNow()));
        });

        group.MapGet("/{id:guid}/evidence.csv", async (Guid id, ClaimsPrincipal user, DataScopeDbContext db, AuditLogWriter audit, CancellationToken ct) =>
        {
            var campaign = await db.ReviewCampaigns.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct);
            if (campaign is null)
            {
                return Results.NotFound();
            }
            var actor = Actor.From(user);
            await audit.AppendAsync(actor.Sid, actor.Name, "campaign.export", campaign.Name, new { CampaignId = id, Format = "csv" }, ct);
            return Results.Stream(stream => WriteCsvAsync(db, campaign, stream, ct), "text/csv; charset=utf-8", FileName(campaign, "csv"));
        });

        group.MapGet("/{id:guid}/evidence.json", async (Guid id, ClaimsPrincipal user, DataScopeDbContext db, AuditLogWriter audit, TimeProvider clock, CancellationToken ct) =>
        {
            var campaign = await db.ReviewCampaigns.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct);
            if (campaign is null)
            {
                return Results.NotFound();
            }
            var actor = Actor.From(user);
            await audit.AppendAsync(actor.Sid, actor.Name, "campaign.export", campaign.Name, new { CampaignId = id, Format = "json" }, ct);
            return Results.Stream(stream => WriteJsonAsync(db, campaign, actor, clock.GetUtcNow(), stream, ct), "application/json", FileName(campaign, "json"));
        });

        // Administrators and auditors can open any review read-only; only the owner side can decide.
        var reviews = api.MapGroup("/reviews").RequireAuthorization(Policies.ReadReviews);

        reviews.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, DataScopeDbContext db, TimeProvider clock, CancellationToken ct) =>
            await ReviewViews.DetailAsync(db, id, Actor.From(user).Sid, viewerIsReviewer: false, clock.GetUtcNow(), ct) is { } detail
                ? Results.Ok(detail) : Results.NotFound());

        reviews.MapGet("/{id:guid}/items/{itemId:long}/users", async (Guid id, long itemId, string? search, int? limit, int? offset, DataScopeDbContext db, CancellationToken ct) =>
        {
            var (l, o) = Paging.Clamp(limit, offset);
            return Results.Ok(await ReviewViews.ItemUsersAsync(db, id, itemId, search, l, o, ct));
        });

        reviews.MapPost("/{id:guid}/delegate", (Guid id, DelegateRequest request, ClaimsPrincipal user, ReviewActions actions, CancellationToken ct) =>
            actions.DelegateAsync(id, request, Actor.From(user), asAdministrator: true, ct)).RequireAuthorization(Policies.ManageReviews);
    }

    public static async Task<List<CampaignDto>> CampaignsAsync(DataScopeDbContext db, IQueryable<ReviewCampaign> campaigns, DateTimeOffset now, CancellationToken ct)
    {
        var rows = await campaigns.AsNoTracking().ToListAsync(ct);
        var ids = rows.Select(c => c.Id).ToList();
        var counts = await db.AccessReviews.AsNoTracking().Where(r => ids.Contains(r.CampaignId))
            .GroupBy(r => r.CampaignId)
            .Select(g => new
            {
                g.Key,
                Reviews = g.Count(),
                Submitted = g.Count(r => r.State == ReviewState.Submitted),
                InProgress = g.Count(r => r.State == ReviewState.InProgress),
                Pending = g.Count(r => r.State == ReviewState.Pending),
                Required = g.Sum(r => r.DecisionsRequired),
            })
            .ToDictionaryAsync(x => x.Key, ct);
        var decided = await (from d in db.ReviewDecisions
                             join i in db.ReviewItems on d.ItemId equals i.Id
                             join r in db.AccessReviews on d.ReviewId equals r.Id
                             where i.RequiresDecision && ids.Contains(r.CampaignId)
                             select new { r.CampaignId, d.ItemId }).Distinct()
                            .GroupBy(x => x.CampaignId).Select(g => new { g.Key, Count = g.Count() })
                            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return rows.Select(c =>
        {
            var n = counts.GetValueOrDefault(c.Id);
            var skipped = JsonSerializer.Deserialize<List<SkippedDto>>(c.Skipped) ?? [];
            return new CampaignDto(
                c.Id, c.Name, c.Description, c.DueOn, c.State.ToString(),
                c.State == CampaignState.Open && (n?.Submitted ?? 0) < (n?.Reviews ?? 0) && ReviewSchedule.IsOverdue(c.DueOn, now),
                c.CreatedAt, c.CreatedByName, c.ClosedAt, c.ClosedByName, c.DirectorySyncedAt, c.Domain,
                n?.Reviews ?? 0, n?.Submitted ?? 0, n?.InProgress ?? 0, n?.Pending ?? 0, n?.Required ?? 0, decided.GetValueOrDefault(c.Id), skipped);
        }).ToList();
    }

    private static string FileName(ReviewCampaign campaign, string extension)
    {
        var safe = new string(campaign.Name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
        return $"access-review-{(safe.Length > 60 ? safe[..60] : safe)}-{campaign.CreatedAt:yyyyMMdd}.{extension}";
    }

    /// <summary>One row per frozen entry with the decision in force.</summary>
    private static async Task WriteCsvAsync(DataScopeDbContext db, ReviewCampaign campaign, Stream stream, CancellationToken ct)
    {
        await using var writer = new StreamWriter(stream, Csv.Utf8WithBom, bufferSize: 64 * 1024, leaveOpen: true);
        await writer.WriteAsync(Csv.Line([
            "Campaign", "Campaign due", "Campaign state", "Directory synced", "Review id", "Resource", "Owner", "Owner SID", "Backup owner",
            "Delegate", "Review state", "Submitted", "Submitted by", "Frozen", "Evidence hash (SHA-256)", "Hash verified", "Entry",
            "Trustee", "Trustee SID", "Trustee kind", "Effect", "Rights", "Applies to", "Inherited", "Needs decision",
            "Users (certain)", "Users (possible)", "Users disabled", "Users with other access", "Flags", "Decision", "Justification",
            "Exception ends", "Decided by", "Decided as", "Decided", "Revisions",
        ]));
        var reviews = await db.AccessReviews.AsNoTracking().Where(r => r.CampaignId == campaign.Id).OrderBy(r => r.Resource).ToListAsync(ct);
        foreach (var r in reviews)
        {
            var verified = (await ReviewViews.FrozenAsync(db, r.Id, ct))!.Hash() == r.EvidenceHash.Trim();
            var items = await db.ReviewItems.AsNoTracking().Where(i => i.ReviewId == r.Id).OrderBy(i => i.Ordinal).ToListAsync(ct);
            var latest = await ReviewViews.LatestDecisionsAsync(db, [r.Id], ct);
            foreach (var i in items)
            {
                var d = latest.TryGetValue(i.Id, out var x) ? x : ((ReviewDecision Latest, int Revisions)?)null;
                await writer.WriteAsync(Csv.Line([
                    campaign.Name, campaign.DueOn, campaign.State, campaign.DirectorySyncedAt, r.Id, r.Resource, r.OwnerName, r.OwnerSid, r.BackupName,
                    r.DelegateName, r.State, r.SubmittedAt, r.SubmittedByName, r.FrozenAt, r.EvidenceHash.Trim(), verified, i.Ordinal + 1,
                    i.TrusteeName, i.TrusteeSid, i.TrusteeKind, i.Effect, i.Rights, i.AppliesTo, i.Inherited, i.RequiresDecision,
                    i.UsersCertain, i.UsersPossible, i.UsersDisabled, i.UsersWithOtherAccess, string.Join(" ", i.Flags),
                    d?.Latest.Decision, d?.Latest.Justification, d?.Latest.ExceptionExpiresOn, d?.Latest.DecidedByName, d?.Latest.DecidedAs,
                    d?.Latest.DecidedAt, d?.Revisions,
                ]));
            }
            db.ChangeTracker.Clear();
        }
        await writer.FlushAsync(ct);
    }

    /// <summary>
    /// The complete evidence: the campaign, and for every review its frozen entries with the people they reached, every
    /// decision revision and its history. Written one review at a time.
    /// </summary>
    private static async Task WriteJsonAsync(DataScopeDbContext db, ReviewCampaign campaign, Actor exportedBy, DateTimeOffset now, Stream stream, CancellationToken ct)
    {
        // The response stream only accepts asynchronous writes, so each review is written to a buffer and then copied.
        using var buffer = new MemoryStream();
        await using var json = new Utf8JsonWriter(buffer);
        async Task DrainAsync()
        {
            await json.FlushAsync(ct);
            buffer.Position = 0;
            await buffer.CopyToAsync(stream, ct);
            buffer.SetLength(0);
        }
        json.WriteStartObject();
        json.WriteString("format", "datascope.access-review-evidence/1");
        json.WriteString("exportedAt", now);
        json.WriteString("exportedBy", exportedBy.Name);
        json.WritePropertyName("campaign");
        JsonSerializer.Serialize(json, new
        {
            campaign.Id,
            campaign.Name,
            campaign.Description,
            campaign.DueOn,
            State = campaign.State.ToString(),
            Scope = JsonDocument.Parse(campaign.Scope).RootElement,
            Skipped = JsonDocument.Parse(campaign.Skipped).RootElement,
            campaign.DirectorySnapshotId,
            campaign.DirectorySyncedAt,
            campaign.Domain,
            campaign.CreatedByName,
            campaign.CreatedBySid,
            campaign.CreatedAt,
            campaign.ClosedByName,
            campaign.ClosedAt,
        }, EvidenceJson);
        json.WriteStartArray("reviews");
        var ids = await db.AccessReviews.AsNoTracking().Where(r => r.CampaignId == campaign.Id).OrderBy(r => r.Resource).Select(r => r.Id).ToListAsync(ct);
        foreach (var id in ids)
        {
            var r = await db.AccessReviews.AsNoTracking().SingleAsync(x => x.Id == id, ct);
            var frozen = (await ReviewViews.FrozenAsync(db, id, ct))!;
            var itemIds = await db.ReviewItems.AsNoTracking().Where(i => i.ReviewId == id).OrderBy(i => i.Ordinal).Select(i => new { i.Id, i.Ordinal }).ToListAsync(ct);
            var ordinalOf = itemIds.ToDictionary(i => i.Id, i => i.Ordinal);
            var decisions = await db.ReviewDecisions.AsNoTracking().Where(d => d.ReviewId == id).OrderBy(d => d.Id).ToListAsync(ct);
            var events = await db.ReviewEvents.AsNoTracking().Where(e => e.ReviewId == id).OrderBy(e => e.Id).ToListAsync(ct);
            JsonSerializer.Serialize(json, new
            {
                r.Id,
                r.Resource,
                r.FolderId,
                r.OwnerName,
                r.OwnerSid,
                r.BackupName,
                r.BackupSid,
                r.DelegateName,
                r.DelegateSid,
                State = r.State.ToString(),
                r.SubmittedAt,
                r.SubmittedByName,
                r.SubmittedBySid,
                r.EscalatedAt,
                EvidenceHash = r.EvidenceHash.Trim(),
                HashVerified = frozen.Hash() == r.EvidenceHash.Trim(),
                Frozen = frozen,
                Decisions = decisions.Select(d => new
                {
                    Entry = ordinalOf.GetValueOrDefault(d.ItemId),
                    Decision = d.Decision.ToString(),
                    d.Justification,
                    d.ExceptionExpiresOn,
                    d.DecidedByName,
                    d.DecidedBySid,
                    d.DecidedAs,
                    d.DecidedAt,
                }),
                Events = events.Select(e => new { e.Kind, e.ActorName, e.ActorSid, e.Detail, e.At }),
            }, EvidenceJson);
            db.ChangeTracker.Clear();
            await DrainAsync();
        }
        json.WriteEndArray();
        json.WriteEndObject();
        await DrainAsync();
    }
}
