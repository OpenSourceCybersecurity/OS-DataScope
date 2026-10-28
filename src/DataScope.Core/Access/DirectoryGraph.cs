using System.Collections.Concurrent;

namespace DataScope.Core.Access;

public enum PrincipalKind
{
    User,
    Group,
    Computer,

    /// <summary>A placeholder for an account from another domain or forest. Its members are not visible here.</summary>
    ForeignSecurityPrincipal,
}

public enum GroupScope
{
    DomainLocal,
    Global,
    Universal,
}

/// <summary>An account or group from one directory snapshot.</summary>
/// <param name="PrimaryGroupSid">Users' primary group (normally Domain Users), which AD does not list in <c>member</c>.</param>
/// <param name="MembersIncomplete">Some members could not be resolved, so anyone might be inside.</param>
public sealed record DirectoryEntry(
    string Sid,
    PrincipalKind Kind,
    string Name,
    bool Enabled = true,
    bool IsSecurityGroup = true,
    string? PrimaryGroupSid = null,
    bool MembersIncomplete = false,
    IReadOnlyList<string>? SidHistory = null,
    string? ManagerSid = null);

/// <summary>A direct membership: <paramref name="MemberSid"/> is listed in <paramref name="GroupSid"/>.</summary>
public sealed record MembershipEdge(string GroupSid, string MemberSid);

public enum LocalGroupsState
{
    /// <summary>The file server's local groups were read.</summary>
    Read,

    /// <summary>Not attempted yet (no scan since this feature, or the lab has none).</summary>
    NotRead,

    /// <summary>The scan account was refused. Windows Server 2016 and later restrict this to administrators by default.</summary>
    AccessDenied,

    Failed,
}

public enum LocalMemberKind
{
    User,
    Group,
    WellKnownGroup,

    /// <summary>A SID Windows could not name: usually a deleted account, sometimes an account from an unreachable domain.</summary>
    Unknown,
}

public sealed record LocalMember(string Sid, LocalMemberKind Kind);

/// <summary>A group local to one file server, such as BUILTIN\Users on that server.</summary>
/// <param name="MembersIncomplete">The member list itself could not be read completely.</param>
public sealed record LocalGroup(string Sid, string Name, IReadOnlyList<LocalMember> Members, bool MembersIncomplete = false);

public sealed record LocalGroupSet(LocalGroupsState State, IReadOnlyList<LocalGroup> Groups)
{
    public static readonly LocalGroupSet Unknown = new(LocalGroupsState.NotRead, []);
}

/// <summary>Whether a user's sign-in token would contain a SID.</summary>
public enum Membership
{
    No,

    /// <summary>Data Scope cannot tell: membership depends on something it could not read.</summary>
    Maybe,

    Yes,
}

/// <summary>
/// Group membership for one domain snapshot, as seen by one file server: nested domain groups, primary groups,
/// SID history, the well-known groups every network sign-in gets, and that server's local groups.
/// Cycles are walked once and reported, never looped on. Anything unresolved makes membership "Maybe", never "No".
/// Thread-safe; tokens are computed on first use and cached.
/// </summary>
public sealed class DirectoryGraph
{
    private readonly Dictionary<string, DirectoryEntry> _principals;
    private readonly Dictionary<string, List<string>> _parents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _members = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LocalGroup> _localGroups;
    private readonly HashSet<string> _domainMaybe = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, UserToken> _tokens = new(StringComparer.Ordinal);

    public DirectoryGraph(IEnumerable<DirectoryEntry> principals, IEnumerable<MembershipEdge> edges, LocalGroupSet? localGroups = null, string? domainSid = null)
    {
        _principals = principals.GroupBy(p => p.Sid, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        DomainSid = domainSid;
        LocalGroups = localGroups ?? LocalGroupSet.Unknown;
        _localGroups = LocalGroups.State == LocalGroupsState.Read
            ? LocalGroups.Groups.GroupBy(g => g.Sid, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal)
            : new Dictionary<string, LocalGroup>(StringComparer.Ordinal);

        foreach (var edge in edges)
        {
            // Distribution groups never appear in sign-in tokens, so they grant nothing.
            if (_principals.TryGetValue(edge.GroupSid, out var group) && group.Kind == PrincipalKind.Group && !group.IsSecurityGroup)
            {
                continue;
            }
            Add(_parents, edge.MemberSid, edge.GroupSid);
            Add(_members, edge.GroupSid, edge.MemberSid);
        }
        foreach (var user in _principals.Values.Where(p => p.PrimaryGroupSid is not null))
        {
            Add(_parents, user.Sid, user.PrimaryGroupSid!);
            Add(_members, user.PrimaryGroupSid!, user.Sid);
        }

        // A group with members Data Scope could not resolve might contain anyone, and so might every group it is nested in.
        var queue = new Queue<string>(_principals.Values
            .Where(p => p.Kind == PrincipalKind.Group && p.IsSecurityGroup && (p.MembersIncomplete || HasUnknownMember(p.Sid)))
            .Select(p => p.Sid));
        while (queue.TryDequeue(out var sid))
        {
            if (!_domainMaybe.Add(sid))
            {
                continue;
            }
            foreach (var parent in _parents.GetValueOrDefault(sid) ?? [])
            {
                queue.Enqueue(parent);
            }
        }
        Cycles = FindCycles();
    }

    public string? DomainSid { get; }
    public LocalGroupSet LocalGroups { get; }
    public IReadOnlyCollection<DirectoryEntry> Principals => _principals.Values;

    /// <summary>Group nesting loops, each listed once (as the groups in the loop).</summary>
    public IReadOnlyList<IReadOnlyList<string>> Cycles { get; }

    /// <summary>Groups whose membership is uncertain because they contain, directly or nested, something unresolved.</summary>
    public IReadOnlyCollection<string> UncertainGroups => _domainMaybe;

    public IEnumerable<DirectoryEntry> Users => _principals.Values.Where(p => p.Kind == PrincipalKind.User);

    public DirectoryEntry? Find(string sid) => _principals.GetValueOrDefault(sid);

    public LocalGroup? FindLocalGroup(string sid) => _localGroups.GetValueOrDefault(sid);

    public IReadOnlyList<string> DirectParents(string sid) => _parents.GetValueOrDefault(sid) ?? [];

    public IReadOnlyList<string> DirectMembers(string sid) =>
        _localGroups.TryGetValue(sid, out var local) ? local.Members.Select(m => m.Sid).ToList() : _members.GetValueOrDefault(sid) ?? [];

    /// <summary>Display name for any SID: directory, local group, well-known, or the SID itself.</summary>
    public string NameOf(string sid) =>
        _principals.GetValueOrDefault(sid)?.Name
        ?? _localGroups.GetValueOrDefault(sid)?.Name
        ?? WellKnownSid.NameOf(sid)
        ?? (DomainSid is not null && Sid.AccountDomain(sid) == DomainSid ? $"Deleted account ({sid})" : sid);

    /// <summary>
    /// Whether a SID names something Data Scope can identify. Unknown SIDs in permissions are usually deleted accounts
    /// (orphaned entries) or accounts from another domain.
    /// </summary>
    public bool IsKnown(string sid) =>
        _principals.ContainsKey(sid) || _localGroups.ContainsKey(sid) || WellKnownSid.NameOf(sid) is not null;

    public UserToken TokenFor(string userSid) => _tokens.GetOrAdd(userSid, sid => new UserToken(this, sid, null));

    /// <summary>
    /// A stand-in for an ordinary enabled domain user with no extra group memberships: what every account in the
    /// domain gets (Everyone, Authenticated Users, Domain Users, and local groups holding those).
    /// </summary>
    public UserToken AnyDomainUser => _anyUser ??= new UserToken(this, AnyDomainUserSid,
        new DirectoryEntry(AnyDomainUserSid, PrincipalKind.User, "Any domain user", PrimaryGroupSid: DomainSid is null ? null : $"{DomainSid}-{WellKnownSid.DomainUsersRid}"));

    /// <summary>The pseudo-SID of <see cref="AnyDomainUser"/>. RID 0 is never assigned to a real account.</summary>
    public string AnyDomainUserSid => (DomainSid ?? "S-1-5-21-0-0-0") + "-0";

    private UserToken? _anyUser;

    /// <summary>Users whose token certainly or possibly contains <paramref name="sid"/>.</summary>
    public (int Certain, int Possible) CountMembers(string sid)
    {
        int certain = 0, possible = 0;
        foreach (var user in Users)
        {
            switch (TokenFor(user.Sid).Contains(sid))
            {
                case Membership.Yes:
                    certain++;
                    break;
                case Membership.Maybe:
                    possible++;
                    break;
            }
        }
        return (certain, possible);
    }

    private bool HasUnknownMember(string groupSid) =>
        (_members.GetValueOrDefault(groupSid) ?? []).Any(m =>
            WellKnownSid.NameOf(m) is null
            && (!_principals.TryGetValue(m, out var p) || p.Kind == PrincipalKind.ForeignSecurityPrincipal));

    internal bool IsDomainMaybe(string sid) => _domainMaybe.Contains(sid);

    internal IEnumerable<LocalGroup> AllLocalGroups => _localGroups.Values;

    private static void Add(Dictionary<string, List<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out var list))
        {
            map[key] = list = [];
        }
        if (!list.Contains(value, StringComparer.Ordinal))
        {
            list.Add(value);
        }
    }

    /// <summary>Strongly connected components of the group graph with more than one group (or a self-loop).</summary>
    private List<IReadOnlyList<string>> FindCycles()
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var low = new Dictionary<string, int>(StringComparer.Ordinal);
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var result = new List<IReadOnlyList<string>>();
        var next = 0;
        var groups = _members.Keys.Where(k => _principals.TryGetValue(k, out var p) && p.Kind == PrincipalKind.Group).ToList();

        // Iterative Tarjan, so a deeply nested directory cannot overflow the stack.
        foreach (var start in groups)
        {
            if (index.ContainsKey(start))
            {
                continue;
            }
            var work = new Stack<(string Node, int Child)>();
            work.Push((start, 0));
            index[start] = low[start] = next++;
            stack.Push(start);
            onStack.Add(start);
            while (work.Count > 0)
            {
                var (node, child) = work.Pop();
                var children = (_members.GetValueOrDefault(node) ?? []).Where(m => _members.ContainsKey(m)).ToList();
                if (child < children.Count)
                {
                    work.Push((node, child + 1));
                    var m = children[child];
                    if (!index.TryGetValue(m, out var seen))
                    {
                        index[m] = low[m] = next++;
                        stack.Push(m);
                        onStack.Add(m);
                        work.Push((m, 0));
                    }
                    else if (onStack.Contains(m))
                    {
                        low[node] = Math.Min(low[node], seen);
                    }
                    continue;
                }
                if (work.Count > 0)
                {
                    var parent = work.Peek().Node;
                    low[parent] = Math.Min(low[parent], low[node]);
                }
                if (low[node] == index[node])
                {
                    var component = new List<string>();
                    string popped;
                    do
                    {
                        popped = stack.Pop();
                        onStack.Remove(popped);
                        component.Add(popped);
                    }
                    while (popped != node);
                    if (component.Count > 1 || (_members.GetValueOrDefault(node)?.Contains(node, StringComparer.Ordinal) ?? false))
                    {
                        component.Sort(StringComparer.Ordinal);
                        result.Add(component);
                    }
                }
            }
        }
        return result;
    }
}

/// <summary>
/// The SIDs a user's network sign-in token would contain on one file server, each with the shortest membership chain
/// that puts it there (user first, the SID last).
/// </summary>
public sealed class UserToken
{
    private readonly DirectoryGraph _graph;
    private readonly Dictionary<string, string[]> _certain = new(StringComparer.Ordinal);
    private readonly HashSet<string> _localMaybe = new(StringComparer.Ordinal);

    internal UserToken(DirectoryGraph graph, string userSid, DirectoryEntry? pseudo)
    {
        _graph = graph;
        UserSid = userSid;
        var user = pseudo ?? graph.Find(userSid);
        Resolved = user is not null;

        var queue = new Queue<string>();
        void Reach(string sid, string[] chain)
        {
            if (_certain.TryAdd(sid, chain))
            {
                queue.Enqueue(sid);
            }
        }

        Reach(userSid, [userSid]);
        foreach (var old in user?.SidHistory ?? [])
        {
            Reach(old, [userSid, old]);
        }
        foreach (var implicitSid in WellKnownSid.ImplicitForNetworkLogon)
        {
            Reach(implicitSid, [userSid, implicitSid]);
        }
        if (pseudo?.PrimaryGroupSid is { } primary)
        {
            Reach(primary, [userSid, primary]);
        }
        while (queue.TryDequeue(out var current))
        {
            foreach (var parent in graph.DirectParents(current))
            {
                Reach(parent, [.. _certain[current], parent]);
            }
        }

        // Local groups on the file server can hold domain accounts, domain groups and well-known groups, but not other local groups.
        foreach (var local in graph.AllLocalGroups)
        {
            string[]? via = null;
            foreach (var member in local.Members)
            {
                if (_certain.TryGetValue(member.Sid, out via))
                {
                    break;
                }
            }
            if (via is not null)
            {
                _certain.TryAdd(local.Sid, [.. via, local.Sid]);
            }
            else if (local.MembersIncomplete || local.Members.Any(m => MightContainUser(graph, m)))
            {
                _localMaybe.Add(local.Sid);
            }
        }
    }

    public string UserSid { get; }

    /// <summary>False when the user is not in the snapshot; such a token holds only the implicit groups.</summary>
    public bool Resolved { get; }

    public IReadOnlyCollection<string> Sids => _certain.Keys;

    public Membership Contains(string sid)
    {
        if (_certain.ContainsKey(sid))
        {
            return Membership.Yes;
        }
        if (_graph.IsDomainMaybe(sid) || _localMaybe.Contains(sid))
        {
            return Membership.Maybe;
        }
        if (_graph.LocalGroups.State != LocalGroupsState.Read && MightBeLocalGroup(sid))
        {
            return Membership.Maybe;
        }
        return Membership.No;
    }

    /// <summary>Chain from the user to <paramref name="sid"/>, or empty when the user is not (certainly) a member.</summary>
    public IReadOnlyList<string> ChainTo(string sid) => _certain.GetValueOrDefault(sid) ?? [];

    /// <summary>A local group member that could hold this user without Data Scope seeing it: an unexpandable group.</summary>
    private static bool MightContainUser(DirectoryGraph graph, LocalMember member) => member.Kind switch
    {
        LocalMemberKind.Group => graph.IsDomainMaybe(member.Sid) || graph.Find(member.Sid) is null or { Kind: PrincipalKind.ForeignSecurityPrincipal },
        LocalMemberKind.Unknown => Sid.AccountDomain(member.Sid) != graph.DomainSid,
        _ => false,
    };

    /// <summary>
    /// Without the server's local groups, a BUILTIN group or an unknown SID from a non-domain authority could be a
    /// local group with anyone in it.
    /// </summary>
    private bool MightBeLocalGroup(string sid) =>
        sid.StartsWith(WellKnownSid.BuiltinPrefix, StringComparison.Ordinal)
        || (Sid.AccountDomain(sid) is { } domain && domain != _graph.DomainSid && !_graph.IsKnown(sid));
}
