using DataScope.Core.Access;

namespace DataScope.Api.Access;

/// <summary>Who a permission entry names, in terms a reader can act on.</summary>
/// <param name="Kind">User, Group, Computer, ForeignSecurityPrincipal, LocalGroup, WellKnown, Deleted or Unknown.</param>
/// <param name="Broad">Reaches (almost) every account, such as Everyone or Domain Users.</param>
/// <param name="Administrative">Expected administrative access, such as SYSTEM or Administrators.</param>
public sealed record TrusteeDto(string Sid, string Name, string Kind, bool Broad, bool Administrative, bool? Enabled);

/// <param name="Index">Position in the permission list, in the order Windows applies it.</param>
/// <param name="Effect">Allow, Deny, Conditional allow, Conditional deny or Other.</param>
/// <param name="MembersCertain">Users certainly reached by this trustee; null for entries that name no one.</param>
public sealed record AceDto(
    int Index, string Effect, TrusteeDto? Trustee, string Rights, string Mask, string AppliesTo, bool Inherited,
    bool AppliesToFolder, bool AppliesToFiles, int? MembersCertain, int? MembersPossible);

/// <param name="State">Read, AccessDenied, NotRead or Unreadable.</param>
public sealed record PermissionSetDto(
    string State, DateTimeOffset? ReadAt, TrusteeDto? Owner, bool? Protected, bool? Canonical, bool? NoList, IReadOnlyList<AceDto> Entries);

public sealed record NoteDto(string Code, string Text);

/// <summary>A range of access: <c>Rights</c> is what Data Scope can show, <c>UpTo</c> what is possible.</summary>
public sealed record RangeDto(string Rights, string UpTo, string Certainty, IReadOnlyList<NoteDto> Notes);

public sealed record UserAccessDto(string Sid, string Name, bool Enabled, string Rights, string UpTo, string Certainty, string? Via);

/// <param name="Source">Share or NTFS.</param>
/// <param name="Match">Yes when the account certainly holds the trustee, Maybe when membership could not be resolved.</param>
/// <param name="Chain">How the account reaches the trustee, account first.</param>
public sealed record ReasonDto(
    string Source, int Index, string Effect, TrusteeDto? Trustee, string Match, IReadOnlyList<string> Chain, string Rights,
    bool Inherited, string AppliesTo);

public sealed record ExplainDto(
    UserAccessDto User, string Target, string? ShareRights, string? FolderRights, IReadOnlyList<ReasonDto> Reasons, IReadOnlyList<NoteDto> Notes);

/// <summary>Maps access engine results to API shapes.</summary>
internal static class AccessViews
{
    public const string AnyUserToken = "any";

    public static string Label(Rights rights) => AccessMask.Label(rights);

    public static TrusteeDto? Trustee(DirectoryGraph graph, string? sid)
    {
        if (sid is null)
        {
            return null;
        }
        var t = TrusteeInfo.Describe(graph, sid);
        return new TrusteeDto(t.Sid, t.Name, t.Kind.ToString(), t.Broad, t.Administrative, t.Enabled);
    }

    public static string Effect(Ace ace) => ace.Kind switch
    {
        AceKind.Allow or AceKind.ObjectAllow => "Allow",
        AceKind.Deny or AceKind.ObjectDeny => "Deny",
        AceKind.ConditionalAllow => "Conditional allow",
        AceKind.ConditionalDeny => "Conditional deny",
        _ => "Other",
    };

    public static PermissionSetDto Permissions(DirectoryGraph graph, PermissionState state, DateTimeOffset? readAt, SecurityDescriptor? sd, bool share, bool countMembers)
    {
        if (state != PermissionState.Read || sd is null)
        {
            return new PermissionSetDto(sd is null && state == PermissionState.Read ? nameof(PermissionState.Unreadable) : state.ToString(), readAt, null, null, null, null, []);
        }
        var entries = (sd.Dacl ?? []).Select((ace, i) =>
        {
            (int Certain, int Possible)? members = countMembers && ace.Sid is { } s && !WellKnownSid.IsCreatorPlaceholder(s) ? graph.CountMembers(s) : null;
            return new AceDto(
                i, Effect(ace), Trustee(graph, ace.Sid),
                share ? AccessMask.ShareLabel(ace.Mask) : Label(AccessMask.ToRights(ace.Mask)),
                $"0x{ace.Mask:X8}",
                share ? "This share" : ace.AppliesTo,
                ace.IsInherited,
                share || ace.AppliesToObject,
                share || ace.AppliesToChildFiles,
                members?.Certain, members?.Possible);
        }).ToList();
        return new PermissionSetDto(
            nameof(PermissionState.Read), readAt, share ? null : Trustee(graph, sd.Owner),
            sd.IsProtected, sd.IsCanonical, sd.Dacl is null, entries);
    }

    public static RangeDto Range(AccessResult result) => new(
        Label(result.Rights), Label(result.UpTo), result.Certainty.ToString(), Notes(result));

    public static IReadOnlyList<NoteDto> Notes(AccessResult result) =>
        result.Notes.Select(n => new NoteDto(n.Code.ToString(), n.Text)).ToList();

    public static UserAccessDto User(DirectoryGraph graph, AccessResult result)
    {
        var entry = graph.Find(result.UserSid);
        var name = result.UserSid == graph.AnyDomainUserSid ? "Any domain user" : graph.NameOf(result.UserSid);
        return new UserAccessDto(result.UserSid, name, entry?.Enabled ?? true, Label(result.Rights), Label(result.UpTo), result.Certainty.ToString(), Via(graph, result));
    }

    /// <summary>The trustees of the NTFS allow entries that gave the account its rights, for a one-line "via".</summary>
    private static string? Via(DirectoryGraph graph, AccessResult result)
    {
        var names = result.Reasons
            .Where(r => r.Source != AccessTarget.Share && r.Ace.IsAllow && r.Ace.Sid is not null)
            .OrderByDescending(r => r.Rights)
            .Select(r => r.Ace.Sid == result.UserSid ? "direct entry" : graph.NameOf(r.Ace.Sid!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();
        return names.Count == 0 ? null : string.Join(", ", names);
    }

    public static ExplainDto Explain(DirectoryGraph graph, AccessResult result, AccessTarget target) => new(
        User(graph, result),
        target.ToString(),
        result.ShareRights is { } s ? Label(s) : null,
        result.FolderRights is { } f ? Label(f) : null,
        result.Reasons.Select(r => new ReasonDto(
            r.Source == AccessTarget.Share ? "Share" : "NTFS",
            r.Index,
            Effect(r.Ace),
            Trustee(graph, r.Ace.Sid),
            r.Match.ToString(),
            r.Chain.Select(sid => sid == graph.AnyDomainUserSid ? "Any domain user" : graph.NameOf(sid)).ToList(),
            Label(r.Rights),
            r.Ace.IsInherited,
            r.Source == AccessTarget.Share ? "This share" : r.Ace.AppliesTo)).ToList(),
        Notes(result));

    public static bool TryTarget(string? text, out AccessTarget target)
    {
        target = AccessTarget.Files;
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }
        if (Enum.TryParse(text, ignoreCase: true, out AccessTarget parsed) && parsed != AccessTarget.Share && Enum.IsDefined(parsed))
        {
            target = parsed;
            return true;
        }
        return false;
    }
}
