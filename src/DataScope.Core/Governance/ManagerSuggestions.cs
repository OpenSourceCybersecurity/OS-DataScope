using DataScope.Core.Access;

namespace DataScope.Core.Governance;

/// <summary>
/// A suggested owner: the most senior person, by the AD <c>manager</c> chain, among the members of a group that is
/// granted access to the folder.
/// </summary>
/// <param name="Sid">The suggested person.</param>
/// <param name="GroupSid">The group whose members they manage.</param>
/// <param name="Manages">How many of the group's other members report to them, directly or further down.</param>
/// <param name="Members">Enabled people in the group.</param>
/// <param name="Rights">What the group is granted on the folder.</param>
/// <param name="InGroup">False when the person is the shared manager of the group's members but not in it.</param>
public sealed record ManagerSuggestion(string Sid, string GroupSid, int Manages, int Members, Rights Rights, bool InGroup);

/// <summary>
/// Suggests owners from the reporting lines in Active Directory: for each group a folder grants access to, the member
/// who manages the most other members. Only a suggestion: an administrator confirms every owner. Broad and
/// administrative groups are skipped, since their most senior member says nothing about who owns the data.
/// </summary>
public static class ManagerSuggestions
{
    /// <summary>Groups larger than this are skipped: they are department-wide or broader.</summary>
    public const int MaxGroupMembers = 2_000;

    /// <summary>Reporting chains longer than this are cut, so a loop in the data can't run forever.</summary>
    public const int MaxChain = 32;

    public static IReadOnlyList<ManagerSuggestion> For(DirectoryGraph graph, SecurityDescriptor folder, int max = 3)
    {
        var grants = new Dictionary<string, Rights>(StringComparer.Ordinal);
        foreach (var ace in folder.Dacl ?? [])
        {
            if (!ace.IsAllow || ace.IsConditional || !ace.AppliesToObject || ace.Sid is not { } sid
                || WellKnownSid.IsBroad(sid) || WellKnownSid.IsAdministrative(sid))
            {
                continue;
            }
            var isGroup = graph.Find(sid)?.Kind == PrincipalKind.Group || graph.FindLocalGroup(sid) is not null;
            var rights = AccessMask.ToRights(ace.Mask);
            if (isGroup && rights >= Rights.Read)
            {
                grants[sid] = grants.TryGetValue(sid, out var had) && had > rights ? had : rights;
            }
        }
        // Denies are ignored here on purpose: they narrow access, but the people in the group still work with the data.

        var found = new List<ManagerSuggestion>();
        foreach (var (groupSid, rights) in grants)
        {
            var members = EnabledUsersIn(graph, groupSid);
            if (members is null || members.Count < 2)
            {
                continue;
            }
            if (Best(graph, groupSid, members, rights) is { } suggestion)
            {
                found.Add(suggestion);
            }
        }
        return found
            .OrderByDescending(s => s.Manages)
            .ThenByDescending(s => s.Rights)
            .ThenBy(s => graph.NameOf(s.Sid), StringComparer.OrdinalIgnoreCase)
            .DistinctBy(s => s.Sid, StringComparer.Ordinal)
            .Take(max)
            .ToList();
    }

    private static ManagerSuggestion? Best(DirectoryGraph graph, string groupSid, HashSet<string> members, Rights rights)
    {
        var manages = new Dictionary<string, int>(StringComparer.Ordinal);
        var chains = new List<List<string>>();
        foreach (var member in members)
        {
            var chain = Chain(graph, member);
            chains.Add(chain);
            foreach (var boss in chain.Where(members.Contains))
            {
                manages[boss] = manages.GetValueOrDefault(boss) + 1;
            }
        }
        if (manages.Count > 0)
        {
            var top = manages.OrderByDescending(m => m.Value).ThenBy(m => graph.NameOf(m.Key), StringComparer.OrdinalIgnoreCase).First();
            return new ManagerSuggestion(top.Key, groupSid, top.Value, members.Count, rights, InGroup: true);
        }

        // Nobody in the group manages anyone else in it: suggest the nearest manager every member reports to.
        if (chains.Any(c => c.Count == 0))
        {
            return null;
        }
        var shared = chains[0].Where(boss => chains.All(c => c.Contains(boss))).FirstOrDefault();
        return shared is not null && graph.Find(shared) is { Kind: PrincipalKind.User, Enabled: true }
            ? new ManagerSuggestion(shared, groupSid, members.Count, members.Count, rights, InGroup: false)
            : null;
    }

    /// <summary>Everyone above a person, nearest first. Stops at a loop, a missing manager or <see cref="MaxChain"/>.</summary>
    public static List<string> Chain(DirectoryGraph graph, string userSid)
    {
        var chain = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { userSid };
        var current = graph.Find(userSid)?.ManagerSid;
        while (current is not null && chain.Count < MaxChain && seen.Add(current) && graph.Find(current) is { Kind: PrincipalKind.User } boss)
        {
            if (boss.Enabled)
            {
                chain.Add(current);
            }
            current = boss.ManagerSid;
        }
        return chain;
    }

    /// <summary>Enabled users in a group, nested groups included. Null when the group is too large to say anything.</summary>
    private static HashSet<string>? EnabledUsersIn(DirectoryGraph graph, string groupSid)
    {
        var users = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal) { groupSid };
        var queue = new Queue<string>([groupSid]);
        while (queue.TryDequeue(out var sid))
        {
            foreach (var member in graph.DirectMembers(sid))
            {
                if (WellKnownSid.IsBroad(member))
                {
                    return null;
                }
                var entry = graph.Find(member);
                if (entry?.Kind == PrincipalKind.User)
                {
                    if (entry.Enabled)
                    {
                        users.Add(member);
                        if (users.Count > MaxGroupMembers)
                        {
                            return null;
                        }
                    }
                }
                else if ((entry?.Kind == PrincipalKind.Group || graph.FindLocalGroup(member) is not null) && seen.Add(member))
                {
                    queue.Enqueue(member);
                }
            }
        }
        return users;
    }
}
