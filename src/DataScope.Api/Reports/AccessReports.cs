using DataScope.Api.Risks;
using DataScope.Api.Access;
using DataScope.Api.Endpoints;
using DataScope.Core.Access;
using DataScope.Core.Classification;
using DataScope.Core.Security;
using DataScope.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Reports;

/// <summary>Places where permissions are set and the entries set there, with how many people each entry reaches.</summary>
internal sealed class GroupAccessReport : IReport
{
    /// <summary>Permission boundaries considered by one report.</summary>
    internal const int MaxPlaces = 50_000;

    public ReportInfo Info { get; } = new("group-access", "Group access matrix",
        "Every group and account named in share or folder permissions, where, with which rights, and how many people it reaches.",
        Policies.ReadPosture);

    public async Task<ReportDocument> BuildAsync(ReportContext ctx, DirectoryStore directory, CancellationToken ct)
    {
        var places = await AccessEndpoints.BoundaryPlacesAsync(ctx.Db, ctx.Scope.ServerId, ctx.Scope.ShareId, MaxPlaces, ct);
        var placesTruncated = places.Count > MaxPlaces;
        var rows = new List<object?[]>();
        foreach (var p in places.Take(MaxPlaces))
        {
            if (rows.Count > ctx.RowLimit)
            {
                break;
            }
            var (graph, _) = await ctx.Access.GraphAsync(p.ServerId, ct);
            var place = await ctx.Access.PermissionsAsync(p.ShareState, p.ShareSecurityId, p.FolderState, p.FolderSecurityId, ct);
            var where = AccessEndpoints.UncOf(p.UncPath, p.Path);
            object?[] Row(string list, Ace? ace, string? trustee, string? kind, string? rights, bool? isProtected)
            {
                (int Certain, int Possible)? members = ace?.Sid is { } sid && !WellKnownSid.IsCreatorPlaceholder(sid) ? graph.CountMembers(sid) : null;
                return
                [
                    p.Hostname, p.UncPath, where, list, trustee, kind, ace is null ? null : AccessViews.Effect(ace), rights,
                    ace is null ? null : list == "Share" ? "This share" : ace.AppliesTo, ace?.IsInherited, isProtected,
                    members?.Certain, members?.Possible,
                ];
            }

            if (p.Depth == 0)
            {
                var share = place.Permissions.Share;
                if (place.Permissions.ShareState != PermissionState.Read || share is null)
                {
                    rows.Add(Row("Share", null, $"Not read: {Unread(place.Permissions.ShareState)}", null, null, null));
                }
                else if (share.Dacl is null)
                {
                    rows.Add(Row("Share", null, "No permission list: everyone has full control", null, "Full control", null));
                }
                else
                {
                    rows.AddRange(share.Dacl.Select(ace =>
                    {
                        var t = AccessViews.Trustee(graph, ace.Sid);
                        return Row("Share", ace, t?.Name, Kind(t?.Kind), AccessMask.ShareLabel(ace.Mask), null);
                    }));
                }
            }

            var folder = place.Permissions.Folder;
            if (place.Permissions.FolderState != PermissionState.Read || folder is null)
            {
                rows.Add(Row("NTFS", null, $"Not read: {Unread(place.Permissions.FolderState)}", null, null, null));
            }
            else if (folder.Dacl is null)
            {
                rows.Add(Row("NTFS", null, "No permission list: everyone has full control", null, "Full control", folder.IsProtected));
            }
            else
            {
                // Below the share root, inherited entries are already listed at the folder they come from.
                rows.AddRange(folder.Dacl.Where(ace => p.Depth == 0 || !ace.IsInherited).Select(ace =>
                {
                    var t = AccessViews.Trustee(graph, ace.Sid);
                    return Row("NTFS", ace, t?.Name, Kind(t?.Kind), AccessViews.Label(AccessMask.ToRights(ace.Mask)), folder.IsProtected);
                }));
            }
        }

        var facts = await ctx.CommonFactsAsync(ct);
        facts.Add(new("Places listed", $"{Math.Min(places.Count, MaxPlaces):N0} share roots and folders where permissions are set" +
            (placesTruncated ? $" (stopped at {MaxPlaces:N0}; narrow the scope to see the rest)" : "")));
        return ctx.Document(Info, facts,
        [
            "Lists the permissions the latest scan read. At each share root every entry is shown; below it, only the entries a folder sets itself, because inherited ones appear at the folder they come from.",
            "Member counts come from the latest directory sync and include nested groups. \"Possible\" counts people Data Scope cannot rule in or out, for example members of a group it could not read.",
            "A deleted or unknown account means the entry names a SID that is not in the directory. Data Scope never changes these entries.",
        ],
        ["Server", "Share", "Folder", "Permission list", "Group or account", "Type", "Effect", "Rights", "Applies to", "Inherited",
         "Inheritance turned off here", "People reached", "People possibly reached"],
        rows);
    }

    public static string? Kind(string? kind) => kind switch
    {
        nameof(TrusteeKind.WellKnown) => "Built-in",
        nameof(TrusteeKind.LocalGroup) => "Local group on the file server",
        nameof(TrusteeKind.ForeignSecurityPrincipal) => "Account from another domain",
        nameof(TrusteeKind.Deleted) => "Deleted account",
        nameof(TrusteeKind.Unknown) => "Unknown account",
        _ => kind,
    };

    public static string Unread(PermissionState state) => state switch
    {
        PermissionState.AccessDenied => "the scan account was refused",
        PermissionState.Unreadable => "the stored permissions could not be parsed",
        _ => "not read by a scan yet",
    };
}

/// <summary>
/// Permission problems with a stated severity and the reasons for it. Severity comes from fixed rules over sensitivity,
/// exposure and certainty, never from an opaque score.
/// </summary>
internal sealed class RiskyPermissionsReport : IReport
{
    public ReportInfo Info { get; } = new("risky-permissions", "Risky permissions",
        "Sensitive data open to everyone in the domain, folders anyone can change, and permission entries that need tidying, each with its reason and a suggested fix.",
        Policies.ReadPosture);

    public async Task<ReportDocument> BuildAsync(ReportContext ctx, DirectoryStore directory, CancellationToken ct)
    {
        // The same rules as the Risks screen, limited to permissions, so the two always agree.
        var scan = await new RiskEngine(ctx.Db, ctx.Access, directory).CheckAsync(ctx.Scope.ServerId, ctx.Scope.ShareId, permissionsOnly: true, ct);
        var findings = scan.Items.Where(r => RiskRules.Permission.Contains(r.Rule)).ToList();

        var rows = findings
            .Select(f => new object?[] { f.Severity.ToString(), f.Title, f.Where, f.Restricted, f.Confidential, f.Who, f.Access, f.Certainty, f.Why, f.Fix })
            .ToList();

        var facts = await ctx.CommonFactsAsync(ct);
        facts.Add(new("Findings", $"{findings.Count(f => f.Severity == RiskSeverity.High):N0} high, {findings.Count(f => f.Severity == RiskSeverity.Medium):N0} medium, {findings.Count(f => f.Severity == RiskSeverity.Low):N0} low" +
            string.Concat(scan.Limits.Select(l => ". " + l.TrimEnd('.')))));
        return ctx.Document(Info, facts,
        [
            "Severity follows fixed rules. High: Restricted files any domain user can read. Medium: Confidential files any domain user can read, Restricted files they might read, or folders any domain user can change. Low: everything else listed.",
            "Access is computed from the permissions the latest scan read and the latest directory sync, as a range with a certainty. It is not a Windows access check and not evidence that anyone opened a file.",
            "Sensitive file counts include only files whose contents were read and that are directly in that folder.",
            "Data Scope does not change permissions. Each fix is a suggestion for IT to make in Windows; check who needs the access first.",
        ],
        ["Severity", "Finding", "Where", "Restricted files", "Confidential files", "Who", "Access", "Certainty", "Why", "Suggested fix"],
        rows);
    }
}
