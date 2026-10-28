using System.Net.Mail;
using System.Security.Claims;
using DataScope.Api.Access;
using DataScope.Api.Auth;
using DataScope.Api.Reviews;
using DataScope.Core.Access;
using DataScope.Core.Governance;
using DataScope.Core.Security;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Endpoints;

/// <summary>
/// The data owner registry: who answers for each share or folder. Owners are confirmed by an administrator; Data Scope
/// only suggests. Changes end the current assignment and add a new one, so ownership history is kept.
/// </summary>
internal static class OwnerEndpoints
{
    /// <param name="OwnerStatus">Active, Disabled (the account is disabled), Missing (not in the latest directory sync) or Unknown (no sync yet).</param>
    public sealed record OwnerDto(
        long Id, Guid FolderId, Guid ShareId, Guid ServerId, string Hostname, string ShareName, string Path, string Resource,
        string OwnerSid, string OwnerName, string? OwnerEmail, string OwnerStatus, string? BackupSid, string? BackupName,
        string? BackupEmail, string? BackupStatus, string? Department, string? Purpose, string ReviewFrequency,
        string? RetentionCategory, string? Notes, string AssignedByName, DateTimeOffset AssignedAt, DateTimeOffset? EndedAt,
        string? EndReason, DateOnly? LastReviewedOn, DateOnly? NextReviewDue);

    public sealed record UnownedDto(Guid FolderId, Guid ShareId, Guid ServerId, string Hostname, string ShareName, string Resource, long FileCount, long TotalBytes, string? MostSensitive);

    public sealed record OwnerSummaryDto(int Owned, int Unowned, int OrphanedOwners, int MissingBackup, int ReviewsDue);

    /// <param name="Basis">Manager (reporting lines in AD), WindowsOwner or DirectRights.</param>
    public sealed record SuggestionDto(string Sid, string Name, string Reason, string Basis, string? Title, string? Department);

    public sealed record FolderOwnerDto(OwnerDto? Current, IReadOnlyList<OwnerDto> History, IReadOnlyList<SuggestionDto> Suggestions);

    public sealed record AssignRequest(
        string? OwnerSid, string? OwnerEmail, string? BackupSid, string? BackupEmail, string? Department, string? Purpose,
        string? ReviewFrequency, string? RetentionCategory, string? Notes);

    public sealed record EndRequest(string? Reason);

    private sealed class Row
    {
        public required OwnerAssignment A { get; init; }
        public required string Hostname { get; init; }
        public required string ShareName { get; init; }
        public required string Path { get; init; }
        public Guid ServerId { get; init; }
    }

    private static IQueryable<Row> Rows(DataScopeDbContext db, IQueryable<OwnerAssignment> assignments) =>
        from a in assignments.AsNoTracking()
        join f in db.Folders on a.FolderId equals f.Id
        join s in db.Shares on a.ShareId equals s.Id
        join m in db.ManagedServers on s.ServerId equals m.Id
        select new Row { A = a, Hostname = m.Hostname, ShareName = s.Name, Path = f.Path, ServerId = m.Id };

    public static void MapOwners(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/owners").RequireAuthorization(Policies.ReadPosture);

        group.MapGet("/", async (string? search, Guid? serverId, bool? orphaned, int? limit, int? offset, DataScopeDbContext db, DirectoryStore directory, TimeProvider clock, CancellationToken ct) =>
        {
            var (l, o) = Paging.Clamp(limit, offset);
            var rows = Rows(db, db.OwnerAssignments.Where(a => a.EndedAt == null));
            if (serverId is { } server)
            {
                rows = rows.Where(r => r.ServerId == server);
            }
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = Like(search);
                rows = rows.Where(r => EF.Functions.ILike(r.A.OwnerName, term) || EF.Functions.ILike(r.ShareName, term)
                    || EF.Functions.ILike(r.Path, term) || (r.A.Department != null && EF.Functions.ILike(r.A.Department, term)));
            }
            var all = await rows.OrderBy(r => r.Hostname).ThenBy(r => r.ShareName).ThenBy(r => r.Path).ToListAsync(ct);
            var status = await StatusAsync(db, directory, all.SelectMany(r => new[] { r.A.OwnerSid, r.A.BackupSid }).OfType<string>(), ct);
            var reviewed = await LastReviewedAsync(db, all.Select(r => r.A.FolderId).ToList(), ct);
            var items = all.Select(r => ToDto(r, status, reviewed)).ToList();
            if (orphaned == true)
            {
                items = items.Where(i => i.OwnerStatus is "Disabled" or "Missing").ToList();
            }
            return Results.Ok(new Page<OwnerDto>(items.Skip(o).Take(l).ToList(), l, o, items.Count));
        });

        group.MapGet("/unowned", async (Guid? serverId, int? limit, int? offset, DataScopeDbContext db, CancellationToken ct) =>
        {
            var (l, o) = Paging.Clamp(limit, offset);
            var query = from f in db.Folders.AsNoTracking()
                        join s in db.Shares on f.ShareId equals s.Id
                        join m in db.ManagedServers on s.ServerId equals m.Id
                        where f.Depth == 0 && f.RemovedAt == null && s.Kind == Core.Inventory.ShareKind.Disk && s.Included && s.State != ShareState.Removed
                            && !db.OwnerAssignments.Any(a => a.ShareId == s.Id && a.EndedAt == null)
                            && (serverId == null || m.Id == serverId)
                        orderby m.Hostname, s.Name
                        select new { f.Id, ShareId = s.Id, ServerId = m.Id, m.Hostname, s.Name, s.FileCount, s.TotalBytes };
            var total = await query.CountAsync(ct);
            var page = await query.Skip(o).Take(l).ToListAsync(ct);
            var shareIds = page.Select(p => p.ShareId).ToList();
            var sensitivity = await db.Files.AsNoTracking().Where(f => shareIds.Contains(f.ShareId) && f.RemovedAt == null)
                .GroupBy(f => f.ShareId)
                .Select(g => new { g.Key, Restricted = g.Any(f => (f.OverrideSensitivity ?? f.Sensitivity) == Core.Classification.Sensitivity.Restricted), Confidential = g.Any(f => (f.OverrideSensitivity ?? f.Sensitivity) == Core.Classification.Sensitivity.Confidential) })
                .ToDictionaryAsync(x => x.Key, ct);
            var items = page.Select(p => new UnownedDto(p.Id, p.ShareId, p.ServerId, p.Hostname, p.Name, CampaignBuilder.Unc(p.Hostname, p.Name, ""), p.FileCount, p.TotalBytes,
                sensitivity.TryGetValue(p.ShareId, out var x) ? x.Restricted ? "Restricted" : x.Confidential ? "Confidential" : null : null)).ToList();
            return Results.Ok(new Page<UnownedDto>(items, l, o, total));
        });

        group.MapGet("/summary", async (DataScopeDbContext db, DirectoryStore directory, TimeProvider clock, CancellationToken ct) =>
        {
            var current = await db.OwnerAssignments.AsNoTracking().Where(a => a.EndedAt == null)
                .Select(a => new { a.FolderId, a.OwnerSid, a.BackupSid, a.ReviewFrequency }).ToListAsync(ct);
            var unowned = await (from f in db.Folders
                                 join s in db.Shares on f.ShareId equals s.Id
                                 where f.Depth == 0 && f.RemovedAt == null && s.Kind == Core.Inventory.ShareKind.Disk && s.Included && s.State != ShareState.Removed
                                     && !db.OwnerAssignments.Any(a => a.ShareId == s.Id && a.EndedAt == null)
                                 select f.Id).CountAsync(ct);
            var status = await StatusAsync(db, directory, current.Select(c => c.OwnerSid), ct);
            var reviewed = await LastReviewedAsync(db, current.Select(c => c.FolderId).ToList(), ct);
            var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            var due = current.Count(c => !reviewed.TryGetValue(c.FolderId, out var last) || last.AddMonths(c.ReviewFrequency.Months()) <= today);
            return new OwnerSummaryDto(current.Count, unowned, current.Count(c => status.GetValueOrDefault(c.OwnerSid) is "Disabled" or "Missing"),
                current.Count(c => c.BackupSid is null), due);
        });

        api.MapGet("/folders/{id:guid}/owner", async (Guid id, DataScopeDbContext db, DirectoryStore directory, AccessService access, CancellationToken ct) =>
        {
            if (!await db.Folders.AnyAsync(f => f.Id == id, ct))
            {
                return Results.NotFound();
            }
            var rows = await Rows(db, db.OwnerAssignments.Where(a => a.FolderId == id)).OrderByDescending(r => r.A.Id).ToListAsync(ct);
            var status = await StatusAsync(db, directory, rows.SelectMany(r => new[] { r.A.OwnerSid, r.A.BackupSid }).OfType<string>(), ct);
            var reviewed = await LastReviewedAsync(db, [id], ct);
            var dtos = rows.Select(r => ToDto(r, status, reviewed)).ToList();
            return Results.Ok(new FolderOwnerDto(dtos.FirstOrDefault(d => d.EndedAt is null), dtos.Where(d => d.EndedAt is not null).ToList(), await SuggestAsync(id, access, db, directory, ct)));
        }).RequireAuthorization(Policies.ReadPosture);

        api.MapPut("/folders/{id:guid}/owner", async (
            Guid id, AssignRequest request, ClaimsPrincipal user, DataScopeDbContext db, DirectoryStore directory, AuditLogWriter audit,
            TimeProvider clock, CancellationToken ct) =>
        {
            var folder = await (from f in db.Folders
                                join s in db.Shares on f.ShareId equals s.Id
                                join m in db.ManagedServers on s.ServerId equals m.Id
                                where f.Id == id && f.RemovedAt == null
                                select new { f.Id, f.ShareId, f.Path, s.Name, m.Hostname }).SingleOrDefaultAsync(ct);
            if (folder is null)
            {
                return Results.NotFound();
            }
            var snapshot = await directory.LatestCompletedAsync(ct);
            if (snapshot is null)
            {
                return Results.Problem(title: "Sync Active Directory first", detail: "Owners are chosen from Active Directory. Run a directory sync on the Servers & scans screen, then try again.", statusCode: StatusCodes.Status409Conflict);
            }
            var errors = new Dictionary<string, string[]>();
            var owner = await PersonAsync(db, snapshot.Id, request.OwnerSid, "ownerSid", required: true, errors, ct);
            var backup = await PersonAsync(db, snapshot.Id, request.BackupSid, "backupSid", required: false, errors, ct);
            if (owner is not null && backup is not null && owner.Sid == backup.Sid)
            {
                errors["backupSid"] = ["The backup owner must be a different person."];
            }
            var ownerEmail = Email(request.OwnerEmail, owner?.Mail, "ownerEmail", errors);
            var backupEmail = backup is null ? null : Email(request.BackupEmail, backup.Mail, "backupEmail", errors);
            if (!Enum.TryParse<ReviewFrequency>(request.ReviewFrequency, true, out var frequency) || !Enum.IsDefined(frequency) || int.TryParse(request.ReviewFrequency, out _))
            {
                errors["reviewFrequency"] = ["Use Quarterly, Semiannual or Annual."];
            }
            var department = Text(request.Department, 128, "department", errors);
            var purpose = Text(request.Purpose, 500, "purpose", errors);
            var retention = Text(request.RetentionCategory, 64, "retentionCategory", errors);
            var notes = Text(request.Notes, 1000, "notes", errors);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var actor = Actor.From(user);
            var now = clock.GetUtcNow();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var current = await db.OwnerAssignments.Where(a => a.FolderId == id && a.EndedAt == null).SingleOrDefaultAsync(ct);
            if (current is not null)
            {
                current.EndedAt = now;
                current.EndedBySid = actor.Sid;
                current.EndReason = current.OwnerSid == owner!.Sid ? "Details updated" : $"Reassigned to {owner.Name}";
                await db.SaveChangesAsync(ct);
            }
            var assignment = new OwnerAssignment
            {
                FolderId = id,
                ShareId = folder.ShareId,
                OwnerSid = owner!.Sid,
                OwnerName = owner.Name,
                OwnerEmail = ownerEmail,
                BackupSid = backup?.Sid,
                BackupName = backup?.Name,
                BackupEmail = backupEmail,
                Department = department ?? owner.Department,
                Purpose = purpose,
                ReviewFrequency = frequency,
                RetentionCategory = retention,
                Notes = notes,
                AssignedBySid = actor.Sid,
                AssignedByName = actor.Name,
                AssignedAt = now,
            };
            db.OwnerAssignments.Add(assignment);
            await db.SaveChangesAsync(ct);
            await audit.AppendAsync(actor.Sid, actor.Name, current is null ? "owner.assign" : "owner.change",
                CampaignBuilder.Unc(folder.Hostname, folder.Name, folder.Path),
                new { FolderId = id, Owner = owner.Sid, OwnerName = owner.Name, Backup = backup?.Sid, Frequency = frequency.ToString(), Previous = current?.OwnerSid }, ct);
            await tx.CommitAsync(ct);
            var row = await Rows(db, db.OwnerAssignments.Where(a => a.Id == assignment.Id)).SingleAsync(ct);
            var status = await StatusAsync(db, directory, [owner.Sid, .. backup is null ? Array.Empty<string>() : [backup.Sid]], ct);
            return Results.Ok(ToDto(row, status, await LastReviewedAsync(db, [id], ct)));
        }).RequireAuthorization(Policies.ManageReviews);

        api.MapPost("/folders/{id:guid}/owner/end", async (
            Guid id, EndRequest request, ClaimsPrincipal user, DataScopeDbContext db, AuditLogWriter audit, TimeProvider clock, CancellationToken ct) =>
        {
            var reason = request.Reason?.Trim();
            if (reason is null || reason.Length is < 3 or > 500)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["reason"] = ["Say why the folder no longer has this owner (3 to 500 characters)."] });
            }
            var current = await db.OwnerAssignments.Where(a => a.FolderId == id && a.EndedAt == null).SingleOrDefaultAsync(ct);
            if (current is null)
            {
                return Results.NotFound();
            }
            var actor = Actor.From(user);
            var target = await Rows(db, db.OwnerAssignments.Where(a => a.Id == current.Id)).SingleAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            current.EndedAt = clock.GetUtcNow();
            current.EndedBySid = actor.Sid;
            current.EndReason = reason;
            await db.SaveChangesAsync(ct);
            await audit.AppendAsync(actor.Sid, actor.Name, "owner.end", CampaignBuilder.Unc(target.Hostname, target.ShareName, target.Path),
                new { FolderId = id, Owner = current.OwnerSid, Reason = reason }, ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization(Policies.ManageReviews);
    }

    private sealed record Person(string Sid, string Name, string? Mail, string? Department);

    private static async Task<Person?> PersonAsync(DataScopeDbContext db, Guid snapshotId, string? sid, string field, bool required, Dictionary<string, string[]> errors, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sid))
        {
            if (required)
            {
                errors[field] = ["Choose a person from Active Directory."];
            }
            return null;
        }
        var canonical = sid.Trim().ToUpperInvariant();
        var p = await db.DirectoryPrincipals.AsNoTracking()
            .Where(x => x.SnapshotId == snapshotId && x.Sid == canonical)
            .Select(x => new { x.Sid, x.Kind, x.Name, x.DisplayName, x.Enabled, x.Mail, x.Department }).SingleOrDefaultAsync(ct);
        if (p is null || p.Kind != PrincipalKind.User)
        {
            errors[field] = ["That account is not a user in the latest Active Directory sync. Owners must be people, not groups."];
            return null;
        }
        if (!p.Enabled)
        {
            errors[field] = ["That account is disabled. Choose someone who can sign in."];
            return null;
        }
        return new Person(p.Sid, p.DisplayName is { Length: > 0 } display ? $"{display} ({p.Name})" : p.Name, p.Mail, p.Department);
    }

    private static string? Email(string? given, string? directory, string field, Dictionary<string, string[]> errors)
    {
        var value = string.IsNullOrWhiteSpace(given) ? directory : given.Trim();
        if (value is null)
        {
            return null;
        }
        if (value.Length > 256 || !MailAddress.TryCreate(value, out var parsed) || parsed.Address != value)
        {
            errors[field] = ["Enter a single email address, like name@example.com."];
            return null;
        }
        return value;
    }

    private static string? Text(string? value, int max, string field, Dictionary<string, string[]> errors)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }
        if (text.Length > max)
        {
            errors[field] = [$"Keep this under {max} characters."];
        }
        return text;
    }

    /// <summary>Whether owners are still usable accounts in the latest directory sync.</summary>
    internal static async Task<Dictionary<string, string>> StatusAsync(DataScopeDbContext db, DirectoryStore directory, IEnumerable<string> sids, CancellationToken ct)
    {
        var wanted = sids.Distinct(StringComparer.Ordinal).ToList();
        var snapshot = await directory.LatestCompletedAsync(ct);
        if (snapshot is null)
        {
            return wanted.ToDictionary(s => s, _ => "Unknown", StringComparer.Ordinal);
        }
        var found = await db.DirectoryPrincipals.AsNoTracking().Where(p => p.SnapshotId == snapshot.Id && wanted.Contains(p.Sid))
            .Select(p => new { p.Sid, p.Enabled }).ToDictionaryAsync(p => p.Sid, p => p.Enabled, StringComparer.Ordinal, ct);
        return wanted.ToDictionary(s => s, s => found.TryGetValue(s, out var enabled) ? enabled ? "Active" : "Disabled" : "Missing", StringComparer.Ordinal);
    }

    /// <summary>The date each folder's most recent review was submitted.</summary>
    internal static async Task<Dictionary<Guid, DateOnly>> LastReviewedAsync(DataScopeDbContext db, List<Guid> folderIds, CancellationToken ct)
    {
        var rows = await db.AccessReviews.AsNoTracking()
            .Where(r => folderIds.Contains(r.FolderId) && r.SubmittedAt != null)
            .GroupBy(r => r.FolderId)
            .Select(g => new { g.Key, Last = g.Max(r => r.SubmittedAt) })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.Key, r => DateOnly.FromDateTime(r.Last!.Value.UtcDateTime));
    }

    private static OwnerDto ToDto(Row r, Dictionary<string, string> status, Dictionary<Guid, DateOnly> reviewed)
    {
        var a = r.A;
        DateOnly? last = reviewed.TryGetValue(a.FolderId, out var d) ? d : null;
        return new OwnerDto(
            a.Id, a.FolderId, a.ShareId, r.ServerId, r.Hostname, r.ShareName, r.Path, CampaignBuilder.Unc(r.Hostname, r.ShareName, r.Path),
            a.OwnerSid, a.OwnerName, a.OwnerEmail, status.GetValueOrDefault(a.OwnerSid, "Unknown"), a.BackupSid, a.BackupName,
            a.BackupEmail, a.BackupSid is null ? null : status.GetValueOrDefault(a.BackupSid, "Unknown"), a.Department, a.Purpose,
            a.ReviewFrequency.ToString(), a.RetentionCategory, a.Notes, a.AssignedByName, a.AssignedAt, a.EndedAt, a.EndReason,
            last, last?.AddMonths(a.ReviewFrequency.Months()));
    }

    /// <summary>
    /// People who might own a folder: its Windows owner when that is a person, and people given Modify or Full control
    /// directly. Suggestions only; an administrator confirms.
    /// </summary>
    private static async Task<List<SuggestionDto>> SuggestAsync(Guid folderId, AccessService access, DataScopeDbContext db, DirectoryStore directory, CancellationToken ct)
    {
        var folder = await access.FolderAsync(folderId, ct);
        if (folder is null || folder.FolderState != PermissionState.Read)
        {
            return [];
        }
        var sd = await access.DescriptorAsync(folder.FolderSecurityId, ct);
        if (sd is null)
        {
            return [];
        }
        var (graph, _) = await access.GraphAsync(folder.ServerId, ct);
        var result = new List<SuggestionDto>();
        foreach (var m in ManagerSuggestions.For(graph, sd))
        {
            var group = graph.NameOf(m.GroupSid);
            var rights = AccessMask.Label(m.Rights);
            var reason = m.InGroup
                ? $"Most senior in {group} ({rights}): {m.Manages} of the {m.Members - 1} other people in it report to them"
                : $"Manager of everyone in {group} ({rights})";
            result.Add(new SuggestionDto(m.Sid, graph.NameOf(m.Sid), reason, "Manager", null, null));
        }
        if (sd.Owner is { } ownerSid && graph.Find(ownerSid) is { Kind: PrincipalKind.User, Enabled: true } owner && result.All(r => r.Sid != owner.Sid))
        {
            result.Add(new SuggestionDto(owner.Sid, owner.Name, "Owner of the folder in Windows", "WindowsOwner", null, null));
        }
        foreach (var ace in (sd.Dacl ?? []).Where(a => a.IsAllow && !a.IsInherited && a.Sid is not null && AccessMask.ToRights(a.Mask) >= Rights.Modify))
        {
            if (graph.Find(ace.Sid!) is { Kind: PrincipalKind.User, Enabled: true } person && result.All(r => r.Sid != person.Sid))
            {
                result.Add(new SuggestionDto(person.Sid, person.Name, $"Has {AccessMask.Label(AccessMask.ToRights(ace.Mask))} on this folder directly", "DirectRights", null, null));
            }
        }
        result = result.Take(5).ToList();
        if (result.Count == 0)
        {
            return result;
        }
        // Title and department help an administrator recognise the person; the graph doesn't carry them.
        var sids = result.Select(r => r.Sid).ToList();
        var snapshotId = await directory.LatestCompletedAsync(ct) is { } snapshot ? snapshot.Id : (Guid?)null;
        var people = await db.DirectoryPrincipals.AsNoTracking()
            .Where(p => p.SnapshotId == snapshotId && sids.Contains(p.Sid))
            .Select(p => new { p.Sid, p.DisplayName, p.Title, p.Department })
            .ToDictionaryAsync(p => p.Sid, StringComparer.Ordinal, ct);
        return result.Select(r => people.TryGetValue(r.Sid, out var p)
            ? r with { Name = p.DisplayName ?? r.Name, Title = p.Title, Department = p.Department }
            : r).ToList();
    }

    private static string Like(string search) => $"%{search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
}
