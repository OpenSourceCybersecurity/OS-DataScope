using System.Text.Json.Serialization;
using DataScope.Api.Access;
using DataScope.Api.Cleanup;
using DataScope.Api.Compliance;
using DataScope.Api.Endpoints;
using DataScope.Api.Reports;
using DataScope.Core.Access;
using DataScope.Core.Classification;
using DataScope.Core.Governance;
using DataScope.Core.Inventory;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Risks;

[JsonConverter(typeof(JsonStringEnumConverter<RiskSeverity>))]
public enum RiskSeverity
{
    High,
    Medium,
    Low,
}

/// <summary>The rules Data Scope checks, in the order the Risks screen lists them.</summary>
public static class RiskRules
{
    public const string OpenSensitive = "open-sensitive";
    public const string Secrets = "secrets";
    public const string OpenChange = "open-change";
    public const string NoOwner = "no-owner";
    public const string OwnerGone = "owner-gone";
    public const string DisabledAccount = "disabled-account";
    public const string DeletedAccount = "deleted-account";
    public const string DirectUser = "direct-user";
    public const string UnusualOrder = "unusual-order";
    public const string InheritanceOff = "inheritance-off";
    public const string NotCovered = "not-covered";
    public const string NestingLoop = "nesting-loop";
    public const string StaleSensitive = "stale-sensitive";
    public const string ReviewOverdue = "review-overdue";

    public static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [OpenSensitive] = "Sensitive files open to everyone",
        [Secrets] = "Passwords or keys stored in files",
        [OpenChange] = "Anyone can change or delete files",
        [NoOwner] = "Sensitive share with no owner",
        [OwnerGone] = "Owner left or is disabled",
        [DisabledAccount] = "Disabled accounts still on permissions",
        [DeletedAccount] = "Deleted accounts still on permissions",
        [DirectUser] = "Access given directly to people",
        [UnusualOrder] = "Permission entries in an unusual order",
        [InheritanceOff] = "Folders cut off from their parent's permissions",
        [NotCovered] = "Parts of a share could not be scanned",
        [NestingLoop] = "Groups nested in each other",
        [StaleSensitive] = "Sensitive files nobody has changed in years",
        [ReviewOverdue] = "Regulated data with an overdue access review",
    };

    /// <summary>Rules about permission entries, which the Risky permissions report lists.</summary>
    public static readonly IReadOnlySet<string> Permission = new HashSet<string>(StringComparer.Ordinal)
    {
        OpenSensitive, OpenChange, DisabledAccount, DeletedAccount, DirectUser, UnusualOrder,
    };
}

/// <summary>
/// One risk: a rule that matched a place. Severity comes from fixed rules over sensitivity, exposure and certainty,
/// never an opaque score, and every risk says why and what to do.
/// </summary>
/// <param name="Id">Stable while the underlying facts stay the same: the rule and what it matched.</param>
/// <param name="Who">The account or group involved, if one.</param>
/// <param name="Count">How many things the risk covers (files, entries, folders), when that helps.</param>
/// <param name="Status">Open, Accepted or Snoozed (D115). The engine always says Open; the Risks screen adds decisions.</param>
/// <param name="Decision">The current accept or snooze on this risk, if any, even when it no longer applies.</param>
/// <param name="Standards">
/// The turned-on compliance standards whose data the risk involves, in catalog order (D136). Empty for risks that are
/// not about where data is, such as permission entries and groups.
/// </param>
/// <param name="RaisedFrom">The severity before it was raised one step because the risk involves a turned-on standard.</param>
public sealed record Risk(
    string Id, string Rule, RiskSeverity Severity, string Title, string Where, Guid? ServerId, Guid? ShareId, Guid? FolderId,
    int? Restricted, int? Confidential, string? Who, string? WhoSid, string? Access, string? Certainty, int? Count, string Why, string Fix,
    string Status = RiskStatus.Open, RiskDecisionDto? Decision = null,
    IReadOnlyList<string>? Standards = null, RiskSeverity? RaisedFrom = null);

/// <param name="Limits">Where the check stopped early, in plain words. Empty when everything in scope was checked.</param>
public sealed record RiskScan(IReadOnlyList<Risk> Items, IReadOnlyList<string> Limits);

/// <summary>
/// Works out the current risks from the latest scan, classification and directory sync. Read-only: nothing is stored or
/// changed, so the list always reflects the data as it is now.
/// </summary>
internal sealed class RiskEngine(
    DataScopeDbContext db, AccessService access, DirectoryStore directory, DataCleanup? cleanup = null, TimeProvider? clock = null)
{
    // Labels are stored as text, so they are compared by name, never by order.
    private static readonly List<Sensitivity> Sensitive = [Sensitivity.Confidential, Sensitivity.Restricted];

    /// <summary>Places where permissions are set that one check looks at.</summary>
    public const int MaxPlaces = 50_000;

    /// <summary>Folders holding files with passwords or keys that one check lists.</summary>
    public const int MaxSecretFolders = 500;

    /// <summary>Folders holding data under a turned-on standard that one check matches to their owners' reviews.</summary>
    public const int MaxRegulatedFolders = 20_000;

    private const string OpenFix = "Remove Everyone, Authenticated Users or Domain Users from this folder (or the folder it inherits from), after checking who needs the files. Or move the files somewhere restricted.";

    public async Task<RiskScan> CheckAsync(Guid? serverId, Guid? shareId, bool permissionsOnly, CancellationToken ct)
    {
        var risks = new List<Risk>();
        var limits = new List<string>();
        var on = (await ComplianceScope.LoadAsync(db, ct)).Where(s => s.Enabled).ToList();

        var exposure = await AccessEndpoints.ExposureAsync(db, access, Sensitivity.Confidential, serverId, shareId, ct);
        if (exposure.Truncated)
        {
            limits.Add("Only the 20,000 folders with the most sensitive files were checked for exposure.");
        }
        var openFolders = exposure.Items.Where(e => e.Status == "Open").Select(e => e.FolderId).ToHashSet();
        // Folders with the same permissions in one share are one risk: a single fix covers them all.
        var exposedIds = exposure.Items.Select(e => e.FolderId).ToList();
        var permissionsOf = await db.Folders.AsNoTracking().Where(f => exposedIds.Contains(f.Id))
            .Select(f => new { f.Id, f.SecurityId, f.SecurityState })
            .ToDictionaryAsync(f => f.Id, f => f.SecurityId is { } id ? $"sd{id}" : f.SecurityState.ToString(), ct);
        var regulated = await StandardsAsync(on, db.Files.Where(f => exposedIds.Contains(f.FolderId)), byShare: false, ct);
        var groups = exposure.Items.GroupBy(e => (e.ShareId, Permissions: permissionsOf.GetValueOrDefault(e.FolderId, e.FolderId.ToString()), e.Status, AnyRestricted: e.RestrictedFiles > 0));
        foreach (var g in groups)
        {
            var e = g.First();
            var folders = g.Count();
            var notes = string.Join(" ", e.OpenAccess.Notes.Select(n => n.Text));
            var (severity, title) = e.Status switch
            {
                "Open" when g.Key.AnyRestricted => (RiskSeverity.High, "Restricted files open to any domain user"),
                "Open" => (RiskSeverity.Medium, "Confidential files open to any domain user"),
                "PossiblyOpen" => (g.Key.AnyRestricted ? RiskSeverity.Medium : RiskSeverity.Low, "Sensitive files possibly open to any domain user"),
                _ => (RiskSeverity.Low, "Sensitive files where permissions could not be read"),
            };
            var why = e.Status switch
            {
                "Open" => "Any account in the domain can read files here through the share and folder permissions.",
                "PossiblyOpen" => "Data Scope cannot rule out that any domain user can read files here.",
                _ => "The scan could not read the permissions, so exposure is unknown.",
            };
            if (folders > 1)
            {
                title += $" in {folders} folders";
                why = $"These {folders} folders have the same permissions, so one fix covers them all. " + why;
            }
            int restricted = g.Sum(x => x.RestrictedFiles), confidential = g.Sum(x => x.ConfidentialFiles);
            var where = CommonPath(g.Select(x => x.UncPath));
            var folderId = e.FolderId;
            if (folders > 1)
            {
                // Point at the folder they are all in, where the fix usually belongs, when the scan listed it.
                var shareUnc = (await db.Shares.AsNoTracking().Where(x => x.Id == e.ShareId).Select(x => x.UncPath).FirstAsync(ct)).TrimEnd('\\');
                var relative = where.Length > shareUnc.Length && where.StartsWith(shareUnc + "\\", StringComparison.OrdinalIgnoreCase) ? where[(shareUnc.Length + 1)..] : "";
                folderId = await db.Folders.AsNoTracking().Where(f => f.ShareId == e.ShareId && f.Path == relative && f.RemovedAt == null)
                    .Select(f => (Guid?)f.Id).FirstOrDefaultAsync(ct) ?? e.FolderId;
            }
            var id = folders == 1 ? $"{RiskRules.OpenSensitive}:{e.FolderId}" : $"{RiskRules.OpenSensitive}:{g.Key.ShareId}:{g.Key.Permissions}:{e.Status}:{g.Key.AnyRestricted}";
            risks.Add(Regulated(new(id, RiskRules.OpenSensitive, severity, title, where, null, e.ShareId, folderId,
                restricted, confidential, "Any domain user", null, Range(e.OpenAccess.Rights, e.OpenAccess.UpTo), e.OpenAccess.Certainty,
                folders == 1 ? restricted + confidential : folders, (why + " " + notes).Trim(),
                e.Status == "Unknown" ? "Give the scan account Read on this folder's permissions and rescan." : OpenFix),
                on, g.SelectMany(x => regulated.GetValueOrDefault(x.FolderId, []))));
        }

        var places = await AccessEndpoints.BoundaryPlacesAsync(db, serverId, shareId, MaxPlaces, ct);
        if (places.Count > MaxPlaces)
        {
            limits.Add($"Only the first {MaxPlaces:N0} places where permissions are set were checked.");
        }
        foreach (var p in places.Take(MaxPlaces))
        {
            var (graph, key) = await access.GraphAsync(p.ServerId, ct);
            var place = await access.PermissionsAsync(p.ShareState, p.ShareSecurityId, p.FolderState, p.FolderSecurityId, ct);
            var where = AccessEndpoints.UncOf(p.UncPath, p.Path);
            var any = access.AnyDomainUser(graph, key, place, AccessTarget.Files);
            if (any.Certainty != Certainty.Unknown && any.Rights >= Rights.Modify)
            {
                risks.Add(new($"{RiskRules.OpenChange}:{p.FolderId}", RiskRules.OpenChange, RiskSeverity.Medium, "Any domain user can change or delete files",
                    where, p.ServerId, p.ShareId, p.FolderId, null, null, "Any domain user", null,
                    Range(AccessViews.Label(any.Rights), AccessViews.Label(any.UpTo)), any.Certainty.ToString(), null,
                    "A broad group such as Everyone or Domain Users has Modify or more here, so anyone in the domain, or malware running as them, can change or delete these files.",
                    "Give change rights to the group that works with the data, and Read (or nothing) to everyone else."));
            }
            var folder = place.Permissions.Folder;
            if (place.Permissions.FolderState != PermissionState.Read || folder?.Dacl is null)
            {
                continue;
            }
            if (!folder.IsCanonical)
            {
                risks.Add(new($"{RiskRules.UnusualOrder}:{p.FolderId}", RiskRules.UnusualOrder, RiskSeverity.Low, "Permission entries in an unusual order",
                    where, p.ServerId, p.ShareId, p.FolderId, null, null, null, null, null, null, null,
                    "Windows normally lists deny entries before allow entries. Entries out of order are evaluated as stored and are hard to read correctly.",
                    "Open Advanced Security in Windows Explorer; it offers to reorder the entries."));
            }
            if (p.Depth > 0 && folder.IsProtected)
            {
                risks.Add(new($"{RiskRules.InheritanceOff}:{p.FolderId}", RiskRules.InheritanceOff, RiskSeverity.Low, "Folder cut off from its parent's permissions",
                    where, p.ServerId, p.ShareId, p.FolderId, null, null, null, null, null, null, null,
                    "Inheritance is turned off here, so a change on the folder above (adding a team, removing someone who left) never reaches this folder. That is sometimes on purpose, to lock a folder down, and often left over from a one-off fix.",
                    "If it isn't on purpose, turn inheritance back on (Advanced Security, Enable inheritance) after checking what the folder above would add."));
            }
            foreach (var ace in folder.Dacl.Where(a => (p.Depth == 0 || !a.IsInherited) && a.IsAllow && a.Sid is not null))
            {
                var t = TrusteeInfo.Describe(graph, ace.Sid!);
                var rights = AccessViews.Label(AccessMask.ToRights(ace.Mask));
                var (rule, title, why, fix) = t.Kind switch
                {
                    TrusteeKind.Deleted => (RiskRules.DeletedAccount, "Entry for a deleted account",
                        "The entry names an account from this domain that no longer exists. It grants nothing today and makes the permissions harder to read.",
                        "Remove the entry."),
                    TrusteeKind.User when t.Enabled == false => (RiskRules.DisabledAccount, "Entry for a disabled account",
                        "The account is disabled in Active Directory but still named in the permissions; if it is enabled again, it gets this access back.",
                        "Remove the entry if the person has left."),
                    TrusteeKind.User => (RiskRules.DirectUser, "Access given directly to a person",
                        "Access given to people one by one is easy to forget when they change jobs and is not reviewed with group membership.",
                        "Add the person to a group that has this access and remove the direct entry."),
                    _ => (null, "", "", ""),
                };
                if (rule is not null)
                {
                    risks.Add(new($"{rule}:{p.FolderId}:{t.Sid}", rule, RiskSeverity.Low, title, where, p.ServerId, p.ShareId, p.FolderId,
                        null, null, t.Name, t.Sid, rights, null, null, why, fix));
                }
            }
        }

        if (!permissionsOnly)
        {
            await SecretsAsync(risks, limits, openFolders, on, serverId, shareId, ct);
            await OwnersAsync(risks, on, serverId, shareId, ct);
            await CoverageAsync(risks, serverId, shareId, ct);
            await StaleAsync(risks, on, serverId, shareId, ct);
            await ReviewsAsync(risks, limits, on, serverId, shareId, ct);
            if (shareId is null)
            {
                await LoopsAsync(risks, serverId, ct);
            }
        }

        var ordered = risks
            .Select(r => r.Standards is null ? r with { Standards = [] } : r)
            .OrderBy(r => r.Severity)
            .ThenByDescending(r => r.Restricted ?? 0).ThenByDescending(r => r.Confidential ?? 0)
            .ThenBy(r => r.Where, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .ToList();
        return new RiskScan(ordered, limits);
    }

    /// <summary>Folders holding files where the classifier found passwords or private keys.</summary>
    private async Task SecretsAsync(
        List<Risk> risks, List<string> limits, HashSet<Guid> openFolders, List<EffectiveStandard> on, Guid? serverId, Guid? shareId, CancellationToken ct)
    {
        var files = db.Files.AsNoTracking().Where(f => f.RemovedAt == null
            && (shareId == null || f.ShareId == shareId)
            && (serverId == null || db.Shares.Any(s => s.Id == f.ShareId && s.ServerId == serverId))
            && db.Findings.Any(x => x.FileId == f.Id && x.Category == FindingCategory.Credentials && x.Confidence != Confidence.Low));
        var folders = await files.GroupBy(f => f.FolderId)
            .Select(g => new { FolderId = g.Key, Files = g.Count() })
            .OrderByDescending(g => g.Files)
            .Take(MaxSecretFolders + 1)
            .ToListAsync(ct);
        if (folders.Count > MaxSecretFolders)
        {
            folders.RemoveAt(folders.Count - 1);
            limits.Add($"Only the {MaxSecretFolders} folders with the most files holding passwords or keys are listed.");
        }
        var ids = folders.Select(f => f.FolderId).ToList();
        var where = await (from f in db.Folders.AsNoTracking()
                           join s in db.Shares on f.ShareId equals s.Id
                           where ids.Contains(f.Id)
                           select new { f.Id, f.Path, s.UncPath, ShareId = s.Id, s.ServerId })
            .ToDictionaryAsync(x => x.Id, ct);
        var regulated = await StandardsAsync(on, db.Files.Where(f => ids.Contains(f.FolderId)), byShare: false, ct);
        foreach (var f in folders)
        {
            if (!where.TryGetValue(f.FolderId, out var w))
            {
                continue;
            }
            var open = openFolders.Contains(f.FolderId);
            risks.Add(Regulated(new($"{RiskRules.Secrets}:{f.FolderId}", RiskRules.Secrets, open ? RiskSeverity.High : RiskSeverity.Medium,
                f.Files == 1 ? "A file holds a password or key" : $"{f.Files} files hold passwords or keys",
                AccessEndpoints.UncOf(w.UncPath, w.Path), w.ServerId, w.ShareId, f.FolderId, null, null, null, null, null, null, f.Files,
                "The classifier found what look like passwords, connection strings, access keys or private keys. Anyone who can read the file can use them" +
                (open ? ", and any domain user can read files here." : "."),
                "Check the matches on the Sensitive data screen. Move real secrets into a password manager or vault, change any that were exposed, and delete the copies."),
                on, regulated.GetValueOrDefault(f.FolderId, [])));
        }
    }

    /// <summary>Shares with sensitive files and no owner, and owners who left or were disabled.</summary>
    private async Task OwnersAsync(List<Risk> risks, List<EffectiveStandard> on, Guid? serverId, Guid? shareId, CancellationToken ct)
    {
        var shares = await (from f in db.Folders.AsNoTracking()
                            join s in db.Shares on f.ShareId equals s.Id
                            where f.Depth == 0 && f.RemovedAt == null && s.Kind == ShareKind.Disk && s.Included && s.State != ShareState.Removed
                                && (serverId == null || s.ServerId == serverId) && (shareId == null || s.Id == shareId)
                            select new
                            {
                                FolderId = f.Id,
                                ShareId = s.Id,
                                s.ServerId,
                                s.UncPath,
                                Owner = db.OwnerAssignments.Where(a => a.ShareId == s.Id && a.FolderId == f.Id && a.EndedAt == null)
                                    .Select(a => new { a.OwnerSid, a.OwnerName }).FirstOrDefault(),
                                Restricted = db.Files.Count(x => x.ShareId == s.Id && x.RemovedAt == null && (x.OverrideSensitivity ?? x.Sensitivity) == Sensitivity.Restricted),
                                Confidential = db.Files.Count(x => x.ShareId == s.Id && x.RemovedAt == null && (x.OverrideSensitivity ?? x.Sensitivity) == Sensitivity.Confidential),
                            }).ToListAsync(ct);
        var shareIds = shares.Select(s => s.ShareId).ToList();
        var regulated = await StandardsAsync(on, db.Files.Where(f => shareIds.Contains(f.ShareId)), byShare: true, ct);
        foreach (var s in shares.Where(s => s.Owner is null && s.Restricted + s.Confidential > 0))
        {
            risks.Add(Regulated(new($"{RiskRules.NoOwner}:{s.ShareId}", RiskRules.NoOwner, s.Restricted > 0 ? RiskSeverity.Medium : RiskSeverity.Low,
                "Sensitive share with no owner", s.UncPath, s.ServerId, s.ShareId, s.FolderId, s.Restricted, s.Confidential, null, null, null, null,
                s.Restricted + s.Confidential,
                "Nobody answers for who should have access, so nobody reviews it.",
                "Assign an owner from the share's Overview or the Owners screen. Data Scope suggests one from the permissions and reporting lines."),
                on, regulated.GetValueOrDefault(s.ShareId, [])));
        }

        var owned = shares.Where(s => s.Owner is not null).ToList();
        var status = await OwnerEndpoints.StatusAsync(db, directory, owned.Select(s => s.Owner!.OwnerSid), ct);
        foreach (var s in owned)
        {
            var state = status.GetValueOrDefault(s.Owner!.OwnerSid);
            if (state is not ("Disabled" or "Missing"))
            {
                continue;
            }
            risks.Add(Regulated(new($"{RiskRules.OwnerGone}:{s.ShareId}:{s.Owner.OwnerSid}", RiskRules.OwnerGone, s.Restricted > 0 ? RiskSeverity.Medium : RiskSeverity.Low,
                state == "Disabled" ? "The owner's account is disabled" : "The owner is no longer in Active Directory",
                s.UncPath, s.ServerId, s.ShareId, s.FolderId, s.Restricted, s.Confidential, s.Owner.OwnerName, s.Owner.OwnerSid, null, null, null,
                "Reviews of this share go to someone who has left or can't sign in, so access isn't being checked.",
                "Assign a new owner from the share's Overview or the Owners screen."),
                on, regulated.GetValueOrDefault(s.ShareId, [])));
        }
    }

    /// <summary>Shares holding sensitive files nobody has changed or, where file activity is collected, opened in years (D119).</summary>
    private async Task StaleAsync(List<Risk> risks, List<EffectiveStandard> on, Guid? serverId, Guid? shareId, CancellationToken ct)
    {
        if (cleanup is null)
        {
            return;
        }
        var years = cleanup.Options.StaleAfterYears;
        var stale = cleanup.Stale(serverId, shareId, cleanup.Cutoff(years))
            .Where(f => f.Label == Sensitivity.Restricted || f.Label == Sensitivity.Confidential);
        var shares = await stale
            .GroupBy(f => new { f.ShareId, f.ServerId, f.ShareUnc })
            .Select(g => new
            {
                g.Key.ShareId,
                g.Key.ServerId,
                g.Key.ShareUnc,
                Restricted = g.Count(x => x.Label == Sensitivity.Restricted),
                Confidential = g.Count(x => x.Label == Sensitivity.Confidential),
            })
            .ToListAsync(ct);
        var ids = shares.Select(s => s.ShareId).ToList();
        var roots = await db.Folders.AsNoTracking().Where(f => ids.Contains(f.ShareId) && f.Depth == 0 && f.RemovedAt == null)
            .Select(f => new { f.ShareId, f.Id }).ToListAsync(ct);
        var rootOf = roots.GroupBy(r => r.ShareId).ToDictionary(g => g.Key, g => g.First().Id);
        var staleIds = stale.Select(f => f.Id);
        var regulated = await StandardsAsync(on, db.Files.Where(f => staleIds.Contains(f.Id)), byShare: true, ct);
        foreach (var s in shares)
        {
            var files = s.Restricted + s.Confidential;
            risks.Add(Regulated(new($"{RiskRules.StaleSensitive}:{s.ShareId}", RiskRules.StaleSensitive, s.Restricted > 0 ? RiskSeverity.Medium : RiskSeverity.Low,
                files == 1 ? $"A sensitive file unchanged for over {years} years" : $"{files:N0} sensitive files unchanged for over {years} years",
                s.ShareUnc, s.ServerId, s.ShareId, rootOf.GetValueOrDefault(s.ShareId), s.Restricted, s.Confidential, null, null, null, null, files,
                "Sensitive data kept after it is needed can still be stolen or encrypted by ransomware, and HIPAA, PCI DSS and GLBA expect it to be kept only as long as there is a reason to. " +
                "Nobody has changed these files in that time, and file activity, where Data Scope collects it, shows nobody opening them.",
                "Open Stale & duplicates to see the files. Ask the owner whether they are still needed, then archive them to restricted storage or delete them under your retention policy. Data Scope never moves or deletes files."),
                on, regulated.GetValueOrDefault(s.ShareId, [])));
        }
    }

    /// <summary>Shares the last scan could not finish, so risks there may be missing.</summary>
    private async Task CoverageAsync(List<Risk> risks, Guid? serverId, Guid? shareId, CancellationToken ct)
    {
        var shares = await db.Shares.AsNoTracking()
            .Where(s => s.Kind == ShareKind.Disk && s.Included && (s.State == ShareState.Partial || s.State == ShareState.Unavailable)
                && (serverId == null || s.ServerId == serverId) && (shareId == null || s.Id == shareId))
            .Select(s => new { s.Id, s.ServerId, s.UncPath, s.State, s.GapCount })
            .ToListAsync(ct);
        foreach (var s in shares)
        {
            var unavailable = s.State == ShareState.Unavailable;
            risks.Add(new($"{RiskRules.NotCovered}:{s.Id}", RiskRules.NotCovered, unavailable ? RiskSeverity.Medium : RiskSeverity.Low,
                unavailable ? "The last scan could not open this share" : s.GapCount == 1 ? "1 place in this share was not scanned" : $"{s.GapCount:N0} places in this share were not scanned",
                s.UncPath, s.ServerId, s.Id, null, null, null, null, null, null, null, unavailable ? null : s.GapCount,
                "Data Scope can only report on what it could read. Sensitive files or open permissions in the parts it missed are not on this list.",
                "Open the share's \"Not covered\" tab for the reasons: usually missing Read rights for the scan account, or folders an administrator excluded on Scan scope & exclusions. Fix what you can, then rescan."));
        }
    }

    /// <summary>Groups that contain each other.</summary>
    private async Task LoopsAsync(List<Risk> risks, Guid? serverId, CancellationToken ct)
    {
        var (graph, _) = await access.GraphAsync(serverId, ct);
        foreach (var loop in graph.Cycles)
        {
            var names = loop.Select(graph.NameOf).ToList();
            risks.Add(new($"{RiskRules.NestingLoop}:{string.Join(',', loop.Order(StringComparer.Ordinal))}", RiskRules.NestingLoop, RiskSeverity.Low,
                $"{string.Join(", ", names)} contain each other", "Active Directory", null, null, null, null, null, string.Join(", ", names), loop[0],
                null, null, loop.Count,
                "Groups nested in a loop give every member of each group the access of all of them. It is almost always a mistake and makes access hard to follow.",
                "Remove one of the nestings in Active Directory Users and Computers."));
        }
    }

    /// <summary>
    /// Folders holding data under a turned-on standard whose owner's access review is overdue, or was never done more
    /// than one review period after the owner was assigned (D136). Each folder belongs to the nearest folder at or above
    /// it that has an owner, as on the compliance evidence report; folders with no owner are the no-owner rule's.
    /// </summary>
    private async Task ReviewsAsync(List<Risk> risks, List<string> limits, List<EffectiveStandard> on, Guid? serverId, Guid? shareId, CancellationToken ct)
    {
        if (on.Count == 0)
        {
            return;
        }
        var inScope = db.Files.AsNoTracking().Where(f => f.RemovedAt == null
            && (shareId == null || f.ShareId == shareId)
            && db.Shares.Any(s => s.Id == f.ShareId && s.Kind == ShareKind.Disk && s.Included && s.State != ShareState.Removed
                && (serverId == null || s.ServerId == serverId)));
        // A file counts when it falls under any turned-on standard: a category at Medium for the standards that take
        // Medium, or at High for the rest.
        var atMedium = on.Where(s => s.MinConfidence <= Confidence.Medium).SelectMany(s => s.Categories).Distinct().ToList();
        var atHigh = on.SelectMany(s => s.Categories).Distinct().ToList();
        var mediumUp = new List<Confidence> { Confidence.Medium, Confidence.High };
        var counts = await inScope
            .Where(f => (f.OverrideSensitivity == null || f.OverrideSensitivity == Sensitivity.Confidential || f.OverrideSensitivity == Sensitivity.Restricted)
                && db.Findings.Any(x => x.FileId == f.Id
                    && ((atMedium.Contains(x.Category) && mediumUp.Contains(x.Confidence)) || (atHigh.Contains(x.Category) && x.Confidence == Confidence.High))))
            .GroupBy(f => f.FolderId)
            .Select(g => new
            {
                FolderId = g.Key,
                Restricted = g.Count(f => (f.OverrideSensitivity ?? f.Sensitivity) == Sensitivity.Restricted),
                Confidential = g.Count(f => (f.OverrideSensitivity ?? f.Sensitivity) == Sensitivity.Confidential),
            })
            .OrderByDescending(x => x.Restricted).ThenByDescending(x => x.Confidential).ThenBy(x => x.FolderId)
            .Take(MaxRegulatedFolders + 1)
            .ToListAsync(ct);
        if (counts.Count > MaxRegulatedFolders)
        {
            counts.RemoveAt(counts.Count - 1);
            limits.Add($"Only the {MaxRegulatedFolders:N0} folders with the most files under a turned-on standard were checked for overdue access reviews.");
        }
        var ids = counts.Select(c => c.FolderId).ToList();
        var countOf = counts.ToDictionary(c => c.FolderId);
        var regulated = await StandardsAsync(on, db.Files.Where(f => ids.Contains(f.FolderId)), byShare: false, ct);
        var folders = await db.Folders.AsNoTracking().Where(f => ids.Contains(f.Id)).Select(f => new { f.Id, f.ShareId, f.Path }).ToListAsync(ct);
        var shareIds = folders.Select(f => f.ShareId).Distinct().ToList();
        var owners = await (from a in db.OwnerAssignments.AsNoTracking()
                            join f in db.Folders on a.FolderId equals f.Id
                            join s in db.Shares on a.ShareId equals s.Id
                            where a.EndedAt == null && f.RemovedAt == null && shareIds.Contains(a.ShareId)
                            select new { a.ShareId, a.FolderId, f.Path, s.ServerId, s.UncPath, a.OwnerSid, a.OwnerName, a.ReviewFrequency, a.AssignedAt })
            .ToListAsync(ct);
        var reviewed = await OwnerEndpoints.LastReviewedAsync(db, owners.Select(o => o.FolderId).ToList(), ct);
        var today = DateOnly.FromDateTime((clock ?? TimeProvider.System).GetUtcNow().UtcDateTime);

        var byOwner = folders
            .Select(f => (Folder: f, Owner: owners.Where(o => o.ShareId == f.ShareId && ComplianceEvidenceReport.IsAtOrAbove(o.Path, f.Path)).MaxBy(o => o.Path.Length)))
            .Where(x => x.Owner is not null)
            .GroupBy(x => x.Owner!.FolderId);
        foreach (var g in byOwner)
        {
            var o = g.First().Owner!;
            DateOnly? last = reviewed.TryGetValue(o.FolderId, out var d) ? d : null;
            var assigned = DateOnly.FromDateTime(o.AssignedAt.UtcDateTime);
            var due = (last ?? assigned).AddMonths(o.ReviewFrequency.Months());
            var standards = on.Where(s => g.Any(x => regulated.GetValueOrDefault(x.Folder.Id, []).Contains(s.Id))).ToList();
            if (due > today || standards.Count == 0)
            {
                continue;
            }
            var count = g.Count();
            int restricted = g.Sum(x => countOf[x.Folder.Id].Restricted), confidential = g.Sum(x => countOf[x.Folder.Id].Confidential);
            var names = JoinNames(standards.Select(s => s.Name));
            var every = o.ReviewFrequency switch
            {
                ReviewFrequency.Quarterly => "every three months",
                ReviewFrequency.Semiannual => "every six months",
                _ => "every year",
            };
            var where = count == 1 && g.First().Folder.Id == o.FolderId ? "This folder holds" : $"{count:N0} {(count == 1 ? "folder" : "folders")} here {(count == 1 ? "holds" : "hold")}";
            risks.Add(new($"{RiskRules.ReviewOverdue}:{o.FolderId}", RiskRules.ReviewOverdue, restricted > 0 ? RiskSeverity.Medium : RiskSeverity.Low,
                last is null ? "Access never reviewed since the owner was assigned" : $"Access review overdue since {ReportWriters.Text(due)}",
                AccessEndpoints.UncOf(o.UncPath, o.Path), o.ServerId, o.ShareId, o.FolderId, restricted, confidential, o.OwnerName, o.OwnerSid,
                null, null, count,
                $"{where} data that {names} {(standards.Count == 1 ? "covers" : "cover")}. The owner is asked to review who can read it {every}, and " +
                (last is null
                    ? $"no review has been submitted since they became the owner on {ReportWriters.Text(assigned)}."
                    : $"the last review was submitted on {ReportWriters.Text(last.Value)}.") +
                " Regular reviews are how you show that only people who need this data can read it.",
                "Start an access review that includes this folder from Access reviews, or remind the owner to finish the one they have. Data Scope records the owner's decisions; it never changes access itself.",
                Standards: standards.Select(s => s.Id).ToList()));
        }
    }

    /// <summary>
    /// The turned-on standards each folder (or share) holds data for, by the rule <see cref="ComplianceScope.Covered"/>
    /// uses: a finding in one of a standard's categories at or above its confidence, on a file a reviewer did not label
    /// below Confidential.
    /// </summary>
    private async Task<Dictionary<Guid, List<string>>> StandardsAsync(
        List<EffectiveStandard> on, IQueryable<InventoryFile> files, bool byShare, CancellationToken ct)
    {
        if (on.Count == 0)
        {
            return [];
        }
        var categories = on.SelectMany(s => s.Categories).Distinct().ToList();
        var mediumUp = new List<Confidence> { Confidence.Medium, Confidence.High };
        var countable = files.Where(f => f.RemovedAt == null
            && (f.OverrideSensitivity == null || f.OverrideSensitivity == Sensitivity.Confidential || f.OverrideSensitivity == Sensitivity.Restricted));
        var rows = byShare
            ? await (from f in countable
                     from x in db.Findings
                     where x.FileId == f.Id && categories.Contains(x.Category) && mediumUp.Contains(x.Confidence)
                     select new { Key = f.ShareId, x.Category, x.Confidence }).Distinct().ToListAsync(ct)
            : await (from f in countable
                     from x in db.Findings
                     where x.FileId == f.Id && categories.Contains(x.Category) && mediumUp.Contains(x.Confidence)
                     select new { Key = f.FolderId, x.Category, x.Confidence }).Distinct().ToListAsync(ct);
        return rows.GroupBy(r => r.Key).ToDictionary(
            g => g.Key,
            g => on.Where(s => g.Any(r => s.Categories.Contains(r.Category) && r.Confidence >= s.MinConfidence)).Select(s => s.Id).ToList());
    }

    /// <summary>
    /// Names the turned-on standards whose data a risk involves and raises its severity one step for them (D136): data
    /// someone chose to answer for under a standard is worth more attention than the same data without one.
    /// </summary>
    internal static Risk Regulated(Risk risk, IReadOnlyList<EffectiveStandard> on, IEnumerable<string> standardIds)
    {
        var ids = standardIds.ToHashSet(StringComparer.Ordinal);
        var standards = on.Where(s => ids.Contains(s.Id)).ToList();
        if (standards.Count == 0)
        {
            return risk with { Standards = [] };
        }
        var raised = risk.Severity switch
        {
            RiskSeverity.Low => RiskSeverity.Medium,
            _ => RiskSeverity.High,
        };
        var covers = standards.Count == 1
            ? $"{standards[0].Name} covers, a standard you turned on"
            : $"{JoinNames(standards.Select(s => s.Name))} cover, standards you turned on";
        var why = raised == risk.Severity
            ? $"{risk.Why} It involves data {covers}."
            : $"{risk.Why} It involves data {covers}, so it is rated {raised} rather than {risk.Severity}.";
        return risk with
        {
            Severity = raised,
            Why = why,
            Standards = standards.Select(s => s.Id).ToList(),
            RaisedFrom = raised == risk.Severity ? null : risk.Severity,
        };
    }

    /// <summary>"HIPAA", "PCI DSS and GLBA", "HIPAA, PCI DSS and GLBA".</summary>
    internal static string JoinNames(IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.Count <= 1 ? string.Concat(list) : $"{string.Join(", ", list[..^1])} and {list[^1]}";
    }

    /// <summary>The deepest folder every path is in: <c>\\fs\Ops\Projects</c> for its subfolders.</summary>
    internal static string CommonPath(IEnumerable<string> uncPaths)
    {
        string[]? common = null;
        foreach (var parts in uncPaths.Select(p => p.TrimStart('\\').Split('\\')))
        {
            if (common is null)
            {
                common = parts;
                continue;
            }
            var n = 0;
            while (n < common.Length && n < parts.Length && string.Equals(common[n], parts[n], StringComparison.OrdinalIgnoreCase))
            {
                n++;
            }
            common = common[..n];
        }
        return @"\\" + string.Join('\\', common ?? []);
    }

    private static string Range(string rights, string upTo) => rights == upTo ? rights : $"{rights}, up to {upTo}";
}
