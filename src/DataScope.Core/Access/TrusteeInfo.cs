namespace DataScope.Core.Access;

/// <summary>What kind of thing a SID in a permission list names.</summary>
public enum TrusteeKind
{
    User,
    Group,
    Computer,
    ForeignSecurityPrincipal,

    /// <summary>A group local to the file server.</summary>
    LocalGroup,

    /// <summary>A built-in identity such as Everyone, SYSTEM or BUILTIN\Administrators.</summary>
    WellKnown,

    /// <summary>A SID from this domain that is not in the directory: almost always a deleted account.</summary>
    Deleted,

    /// <summary>A SID Data Scope cannot place, usually from another domain or forest.</summary>
    Unknown,
}

/// <summary>A permission trustee described for people.</summary>
/// <param name="Broad">Reaches (almost) every account, such as Everyone or Domain Users.</param>
/// <param name="Administrative">Expected administrative access, such as SYSTEM or Administrators.</param>
/// <param name="Enabled">For user accounts, whether the account is enabled; null for anything else.</param>
public sealed record TrusteeInfo(string Sid, string Name, TrusteeKind Kind, bool Broad, bool Administrative, bool? Enabled)
{
    public static TrusteeInfo Describe(DirectoryGraph graph, string sid)
    {
        var entry = graph.Find(sid);
        TrusteeKind kind;
        if (entry is not null)
        {
            kind = entry.Kind switch
            {
                PrincipalKind.User => TrusteeKind.User,
                PrincipalKind.Group => TrusteeKind.Group,
                PrincipalKind.Computer => TrusteeKind.Computer,
                _ => TrusteeKind.ForeignSecurityPrincipal,
            };
        }
        else if (graph.FindLocalGroup(sid) is not null)
        {
            kind = TrusteeKind.LocalGroup;
        }
        else if (WellKnownSid.NameOf(sid) is not null)
        {
            kind = TrusteeKind.WellKnown;
        }
        else if (graph.DomainSid is not null && Access.Sid.AccountDomain(sid) == graph.DomainSid)
        {
            kind = TrusteeKind.Deleted;
        }
        else
        {
            kind = TrusteeKind.Unknown;
        }
        var name = sid == graph.AnyDomainUserSid ? "Any domain user" : graph.NameOf(sid);
        return new TrusteeInfo(sid, name, kind, WellKnownSid.IsBroad(sid), WellKnownSid.IsAdministrative(sid),
            entry?.Kind == PrincipalKind.User ? entry.Enabled : null);
    }
}
