using DataScope.Core.Access;

namespace DataScope.ActiveDirectory;

/// <summary>Reads users, groups and memberships. Implementations must never write to the directory.</summary>
public interface IDirectoryReader
{
    Task<DirectoryReadResult> ReadAsync(CancellationToken ct);
}

/// <summary>The directory could not be read at all. The message is safe to show and store: no passwords, no stack traces.</summary>
public sealed class DirectoryReadException : Exception
{
    public DirectoryReadException() : base("The directory could not be read.") { }

    public DirectoryReadException(string message) : base(message) { }

    public DirectoryReadException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Turns resolved objects and member DNs into membership edges, the way both readers need.</summary>
internal static class Memberships
{
    public static (List<DirectoryObject> Objects, List<MembershipEdge> Edges, int Unresolved) Resolve(
        IReadOnlyList<DirectoryObject> objects, IReadOnlyDictionary<string, IReadOnlyList<string>> memberDnsBySid)
    {
        var sidByDn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in objects)
        {
            sidByDn.TryAdd(o.DistinguishedName, o.Sid);
        }
        var edges = new List<MembershipEdge>();
        var unresolved = 0;
        var result = new List<DirectoryObject>(objects.Count);
        foreach (var o in objects)
        {
            if (!memberDnsBySid.TryGetValue(o.Sid, out var dns) || dns.Count == 0)
            {
                result.Add(o);
                continue;
            }
            var missing = 0;
            foreach (var dn in dns)
            {
                if (sidByDn.TryGetValue(dn, out var member))
                {
                    edges.Add(new MembershipEdge(o.Sid, member));
                }
                else
                {
                    missing++;
                }
            }
            unresolved += missing;
            result.Add(o with { MembersIncomplete = o.MembersIncomplete || missing > 0, DirectMemberCount = dns.Count });
        }
        return (result, edges, unresolved);
    }
}
