using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using DataScope.Api.Auth;
using DataScope.Core.Classification;
using DataScope.Core.Inventory;
using DataScope.Core.Security;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Endpoints;

/// <summary>
/// Scan scope and exclusions (D124): which shares are scanned, and folders or file types left out of scans with why.
/// Built on share inclusion: whole shares are still turned on and off with PATCH /shares/{id}. Every exclusion is a
/// coverage gap, never clean data, and one that hides a lot needs the admin to confirm it.
/// </summary>
internal static class ScanScopeEndpoints
{
    public sealed record ShareScopeDto(
        Guid Id, Guid ServerId, string Hostname, string Name, string UncPath, string Kind, bool Included, string State,
        long FileCount, long TotalBytes, DateTimeOffset? LastScannedAt, string? ExclusionReason, string? ExcludedByName, DateTimeOffset? ExcludedAt);

    /// <param name="Warnings">Why this hides a lot; saving it needs <c>confirm</c>.</param>
    /// <param name="Notes">Things worth knowing that don't need confirming.</param>
    public sealed record ImpactDto(long Files, long Bytes, long Folders, long Restricted, long Confidential, long TotalFiles, bool Large,
        IReadOnlyList<string> Warnings, IReadOnlyList<string> Notes);

    public sealed record ExclusionDto(
        long Id, string Kind, string Effect, Guid? ShareId, string? ShareUnc, string? Path, string? Extension, string Target, string Reason, bool Enabled,
        DateTimeOffset CreatedAt, string CreatedByName, DateTimeOffset UpdatedAt, string UpdatedByName, ImpactDto Impact);

    /// <param name="FilesNotRead">Current files listed but never opened because of an exclusion.</param>
    /// <param name="FoldersNotScanned">Folders the latest scans left out because of an exclusion.</param>
    public sealed record ScopeCoverageDto(int SharesIncluded, int SharesExcluded, int SpecialShares, int FolderExclusions, int ContentExclusions, int Disabled,
        long FilesNotRead, int FoldersNotScanned, long FilesInExcludedShares);

    public sealed record ChangeDto(DateTimeOffset At, string ActorName, string Action, string Target, string? Reason);

    public sealed record ScopeDto(IReadOnlyList<ShareScopeDto> Shares, IReadOnlyList<ExclusionDto> Exclusions, ScopeCoverageDto Coverage, IReadOnlyList<ChangeDto> History);

    public sealed record ExclusionRequest(string? Kind, string? Effect, Guid? ShareId, string? Path, string? Extension, string? Reason, bool Confirm = false);

    public sealed record UpdateExclusionRequest(bool? Enabled, string? Reason, bool Confirm = false);

    private static readonly string[] HistoryActions =
        ["share.include", "share.exclude", "scan-exclusion.add", "scan-exclusion.enable", "scan-exclusion.disable", "scan-exclusion.change", "scan-exclusion.remove"];

    private const int HistoryLimit = 100;
    private const long ManyFiles = 10_000;

    public static void MapScanScope(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/scan-scope").RequireAuthorization(Policies.ReadAuditLog);

        group.MapGet("/", async (ScanScopeStore store, DataScopeDbContext db, CancellationToken ct) =>
        {
            var shares = await (from s in db.Shares.AsNoTracking()
                                join m in db.ManagedServers on s.ServerId equals m.Id
                                where s.State != ShareState.Removed && m.State != EnrollmentState.Retired
                                orderby m.Hostname, s.NameKey
                                select new ShareScopeDto(s.Id, s.ServerId, m.Hostname, s.Name, s.UncPath, s.Kind.ToString(), s.Included, s.State.ToString(),
                                    s.FileCount, s.TotalBytes, s.LastScannedAt, s.ExclusionReason, s.ExcludedByName, s.ExcludedAt)).ToListAsync(ct);
            var rows = await db.ScanExclusions.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct);
            var unc = await ShareUncs(db, rows.Select(r => r.ShareId), ct);
            var exclusions = new List<ExclusionDto>(rows.Count);
            foreach (var x in rows)
            {
                exclusions.Add(ToDto(x, unc, await Impact(store, x.Kind, x.Effect, x.ShareId, x.Path, x.Extension, ct)));
            }

            var disk = shares.Where(s => s.Kind == nameof(ShareKind.Disk)).ToList();
            var coverage = new ScopeCoverageDto(
                disk.Count(s => s.Included), disk.Count(s => !s.Included), shares.Count - disk.Count,
                rows.Count(r => r.Enabled && r.Kind == ScanExclusionKind.Folder && r.Effect == ScanExclusionEffect.Skip),
                rows.Count(r => r.Enabled && r.Effect == ScanExclusionEffect.ListOnly),
                rows.Count(r => !r.Enabled),
                await db.Files.LongCountAsync(f => f.RemovedAt == null && f.ContentState == ContentState.Excluded, ct),
                await db.ScanGaps.CountAsync(g => g.Kind == GapKind.Excluded && db.Shares.Any(s => s.Id == g.ShareId && s.Included && s.LastScanJobId == g.JobId), ct),
                disk.Where(s => !s.Included).Sum(s => s.FileCount));

            var audit = await db.AppAuditLog.AsNoTracking().Where(a => HistoryActions.Contains(a.Action))
                .OrderByDescending(a => a.Sequence).Take(HistoryLimit)
                .Select(a => new { a.At, a.ActorName, a.Action, a.Target, a.Details }).ToListAsync(ct);
            var history = audit.Select(a => new ChangeDto(a.At, a.ActorName, a.Action, a.Target, ReasonFrom(a.Details))).ToList();
            return Results.Ok(new ScopeDto(shares, exclusions, coverage, history));
        });

        // What an exclusion would leave out, before it is saved.
        group.MapGet("/impact", async (string? kind, string? effect, Guid? shareId, string? path, string? extension, ScanScopeStore store, DataScopeDbContext db, CancellationToken ct) =>
        {
            var (target, errors) = await ReadTarget(new ExclusionRequest(kind, effect, shareId, path, extension, null), db, ct);
            return target is null
                ? Results.ValidationProblem(errors)
                : Results.Ok(await Impact(store, target.Kind, target.Effect, target.ShareId, target.Path, target.Extension, ct));
        }).RequireAuthorization(Policies.ManagePlatform);

        group.MapPost("/exclusions", async (ExclusionRequest request, ClaimsPrincipal user, ScanScopeStore store, DataScopeDbContext db, AuditLogWriter audit,
            TimeProvider clock, CancellationToken ct) =>
        {
            var (target, errors) = await ReadTarget(request, db, ct);
            var reason = request.Reason?.Trim() ?? "";
            if (ScanScope.ReasonProblem(reason) is { } reasonProblem)
            {
                errors["reason"] = [reasonProblem];
            }
            if (target is null || errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }
            if (await db.ScanExclusions.CountAsync(ct) >= ScanScope.MaxExclusions)
            {
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: $"There are already {ScanScope.MaxExclusions} exclusions. Remove ones you no longer need first.");
            }
            var upperPath = target.Path?.ToUpperInvariant();
            var existing = await db.ScanExclusions.Where(x => x.Kind == target.Kind && x.ShareId == target.ShareId && x.Extension == target.Extension).ToListAsync(ct);
            if (existing.FirstOrDefault(x => x.Path?.ToUpperInvariant() == upperPath) is { } same)
            {
                return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                    title: same.Enabled ? "This is already excluded." : "This is already on the list, turned off. Turn it on instead.");
            }
            var impact = await Impact(store, target.Kind, target.Effect, target.ShareId, target.Path, target.Extension, ct);
            if (impact.Large && !request.Confirm)
            {
                return NeedsConfirm(impact);
            }

            var actor = Actor.From(user);
            var now = clock.GetUtcNow();
            var row = new ScanExclusion
            {
                Kind = target.Kind,
                Effect = target.Effect,
                ShareId = target.ShareId,
                Path = target.Path,
                Extension = target.Extension,
                Reason = reason,
                Enabled = true,
                CreatedAt = now,
                CreatedBySid = actor.Sid,
                CreatedByName = actor.Name,
                UpdatedAt = now,
                UpdatedBySid = actor.Sid,
                UpdatedByName = actor.Name,
            };
            var unc = await ShareUncs(db, [target.ShareId], ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            db.ScanExclusions.Add(row);
            await db.SaveChangesAsync(ct);
            await audit.AppendAsync(actor.Sid, actor.Name, "scan-exclusion.add", TargetText(row, unc), AuditDetails(row, impact), ct);
            await tx.CommitAsync(ct);
            return Results.Created($"/api/v1/scan-scope/exclusions/{row.Id}", ToDto(row, unc, impact));
        }).RequireAuthorization(Policies.ManagePlatform);

        // Turn an exclusion on or off, or change why. Turning one on that hides a lot needs confirming again.
        group.MapPut("/exclusions/{id:long}", async (long id, UpdateExclusionRequest request, ClaimsPrincipal user, ScanScopeStore store, DataScopeDbContext db,
            AuditLogWriter audit, TimeProvider clock, CancellationToken ct) =>
        {
            var row = await db.ScanExclusions.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (row is null)
            {
                return Results.NotFound();
            }
            if (request.Enabled is null && request.Reason is null)
            {
                return Invalid("enabled", "Send enabled, a new reason, or both.");
            }
            var reason = request.Reason?.Trim();
            if (reason is not null && ScanScope.ReasonProblem(reason) is { } reasonProblem)
            {
                return Invalid("reason", reasonProblem);
            }
            var impact = await Impact(store, row.Kind, row.Effect, row.ShareId, row.Path, row.Extension, ct);
            if (request.Enabled == true && !row.Enabled && impact.Large && !request.Confirm)
            {
                return NeedsConfirm(impact);
            }

            var actor = Actor.From(user);
            var unc = await ShareUncs(db, [row.ShareId], ct);
            var target = TargetText(row, unc);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (reason is not null && reason != row.Reason)
            {
                await audit.AppendAsync(actor.Sid, actor.Name, "scan-exclusion.change", target, new { row.Id, Before = row.Reason, After = reason }, ct);
                row.Reason = reason;
            }
            if (request.Enabled is { } enabled && enabled != row.Enabled)
            {
                row.Enabled = enabled;
                await audit.AppendAsync(actor.Sid, actor.Name, enabled ? "scan-exclusion.enable" : "scan-exclusion.disable", target, AuditDetails(row, impact), ct);
            }
            row.UpdatedAt = clock.GetUtcNow();
            row.UpdatedBySid = actor.Sid;
            row.UpdatedByName = actor.Name;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(ToDto(row, unc, impact));
        }).RequireAuthorization(Policies.ManagePlatform);

        // Removing an exclusion only makes scans cover more; the audit log keeps what it was and why.
        group.MapDelete("/exclusions/{id:long}", async (long id, ClaimsPrincipal user, DataScopeDbContext db, AuditLogWriter audit, CancellationToken ct) =>
        {
            var row = await db.ScanExclusions.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (row is null)
            {
                return Results.NotFound();
            }
            var actor = Actor.From(user);
            var unc = await ShareUncs(db, [row.ShareId], ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            db.ScanExclusions.Remove(row);
            await db.SaveChangesAsync(ct);
            await audit.AppendAsync(actor.Sid, actor.Name, "scan-exclusion.remove", TargetText(row, unc),
                new { row.Id, Kind = row.Kind.ToString(), Effect = row.Effect.ToString(), row.Reason, row.Enabled }, ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization(Policies.ManagePlatform);
    }

    private sealed record Target(ScanExclusionKind Kind, ScanExclusionEffect Effect, Guid? ShareId, string? Path, string? Extension);

    /// <summary>The exclusion a request names, checked, or the reasons it can't be used.</summary>
    private static async Task<(Target?, Dictionary<string, string[]>)> ReadTarget(ExclusionRequest request, DataScopeDbContext db, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (!TryEnum<ScanExclusionKind>(request.Kind, out var kind))
        {
            errors["kind"] = ["Use Folder or FileType."];
            return (null, errors);
        }
        var effect = ScanExclusionEffect.ListOnly;
        if (kind == ScanExclusionKind.Folder && !TryEnum(request.Effect, out effect))
        {
            errors["effect"] = ["Use Skip (not scanned at all) or ListOnly (listed, contents not read)."];
        }
        if (kind == ScanExclusionKind.FileType && request.Effect is not null && (!TryEnum(request.Effect, out effect) || effect != ScanExclusionEffect.ListOnly))
        {
            errors["effect"] = ["File types are always listed; only their contents can be left unread (ListOnly)."];
        }
        if (request.ShareId is { } shareId)
        {
            var share = await db.Shares.AsNoTracking().Where(s => s.Id == shareId).Select(s => new { s.Kind, s.State, Retired = s.Server!.State == EnrollmentState.Retired })
                .FirstOrDefaultAsync(ct);
            if (share is null || share.State == ShareState.Removed || share.Retired)
            {
                errors["shareId"] = ["That share isn't on any server Data Scope scans."];
            }
            else if (share.Kind != ShareKind.Disk)
            {
                errors["shareId"] = ["Administrative and system shares are never scanned, so there is nothing to exclude."];
            }
        }
        else if (kind == ScanExclusionKind.Folder)
        {
            errors["shareId"] = ["Choose the share the folder is on."];
        }
        string? path = null;
        string? extension = null;
        if (kind == ScanExclusionKind.Folder)
        {
            if (!ScanScope.TryNormalizeFolder(request.Path, out var p, out var problem))
            {
                errors["path"] = [problem!];
            }
            path = p;
        }
        else
        {
            if (!ScanScope.TryNormalizeExtension(request.Extension, out var e, out var problem))
            {
                errors["extension"] = [problem!];
            }
            extension = e;
        }
        return errors.Count > 0 ? (null, errors) : (new Target(kind, effect, request.ShareId, path, extension), errors);
    }

    /// <summary>What an exclusion covers, and whether it hides enough that saving it needs confirming.</summary>
    private static async Task<ImpactDto> Impact(ScanScopeStore store, ScanExclusionKind kind, ScanExclusionEffect effect, Guid? shareId, string? path, string? extension, CancellationToken ct)
    {
        var i = await store.ImpactAsync(kind, shareId, path, extension, ct);
        var warnings = new List<string>();
        var notes = new List<string>();
        var where = kind == ScanExclusionKind.Folder ? "this folder" : shareId is null ? "these files on every share" : "these files on this share";
        if (i.Files >= ManyFiles)
        {
            warnings.Add($"The last scans found {N(i.Files)} files in {where}.");
        }
        if (i.TotalFiles >= 20 && i.Files * 4 >= i.TotalFiles)
        {
            warnings.Add($"That is {Math.Round(100.0 * i.Files / i.TotalFiles):0}% of the files {(kind == ScanExclusionKind.Folder || shareId is not null ? "on the share" : "Data Scope scans")}.");
        }
        if (i.Restricted + i.Confidential > 0)
        {
            var levels = string.Join(" and ", new[] { (i.Restricted, "Restricted"), (i.Confidential, "Confidential") }.Where(x => x.Item1 > 0).Select(x => $"{N(x.Item1)} {x.Item2}"));
            warnings.Add($"{levels} {(i.Restricted + i.Confidential == 1 ? "file was" : "files were")} found here. " +
                (effect == ScanExclusionEffect.Skip ? "Changes to them will no longer be seen." : "Their contents will no longer be checked, and they will show as not read."));
        }
        if (kind == ScanExclusionKind.FileType && ScanScope.CommonDataTypes.Contains(extension!))
        {
            warnings.Add($".{extension} files are where sensitive data is most often found.");
        }
        if (kind == ScanExclusionKind.Folder && i.Folders == 0)
        {
            notes.Add("No folder at this path was found by the last scan. Check the spelling; the exclusion still applies if it appears later.");
        }
        if (effect == ScanExclusionEffect.Skip)
        {
            notes.Add("Files already found there stay in Data Scope as last seen. They are no longer updated, and the share shows as partly covered.");
        }
        return new ImpactDto(i.Files, i.Bytes, i.Folders, i.Restricted, i.Confidential, i.TotalFiles, warnings.Count > 0, warnings, notes);
    }

    private static IResult NeedsConfirm(ImpactDto impact) =>
        Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "This hides a lot from scans. Confirm to save it.",
            extensions: new Dictionary<string, object?> { ["impact"] = impact, ["needsConfirm"] = true });

    private static async Task<Dictionary<Guid, string>> ShareUncs(DataScopeDbContext db, IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var list = ids.OfType<Guid>().Distinct().ToList();
        return list.Count == 0 ? [] : await db.Shares.AsNoTracking().Where(s => list.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.UncPath, ct);
    }

    private static string TargetText(ScanExclusion x, IReadOnlyDictionary<Guid, string> unc) => x.Kind == ScanExclusionKind.Folder
        ? $@"{unc.GetValueOrDefault(x.ShareId!.Value, "?")}\{x.Path}"
        : $".{x.Extension} files on {(x.ShareId is { } s ? unc.GetValueOrDefault(s, "?") : "every share")}";

    private static ExclusionDto ToDto(ScanExclusion x, IReadOnlyDictionary<Guid, string> unc, ImpactDto impact) =>
        new(x.Id, x.Kind.ToString(), x.Effect.ToString(), x.ShareId, x.ShareId is { } s ? unc.GetValueOrDefault(s) : null, x.Path, x.Extension, TargetText(x, unc),
            x.Reason, x.Enabled, x.CreatedAt, x.CreatedByName, x.UpdatedAt, x.UpdatedByName, impact);

    private static object AuditDetails(ScanExclusion x, ImpactDto impact) => new
    {
        x.Id,
        Kind = x.Kind.ToString(),
        Effect = x.Effect.ToString(),
        x.ShareId,
        x.Path,
        x.Extension,
        x.Reason,
        impact.Files,
        impact.Restricted,
        impact.Confidential,
        Confirmed = impact.Large,
    };

    /// <summary>The reason recorded with an audit entry: Reason, or After for a changed reason.</summary>
    private static string? ReasonFrom(string details)
    {
        try
        {
            using var doc = JsonDocument.Parse(details);
            foreach (var name in new[] { "Reason", "After", "PreviousReason" })
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                {
                    return v.GetString();
                }
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    private static bool TryEnum<T>(string? text, out T value) where T : struct, Enum =>
        Enum.TryParse(text, ignoreCase: true, out value) && Enum.IsDefined(value) && !int.TryParse(text, out _);

    private static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
