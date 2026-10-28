using System.Text.Json;
using DataScope.Core.Access;

namespace DataScope.Scanner.Security;

/// <summary>
/// Development stand-in for Windows permissions, read from <c>{root}/{host}.security.json</c> (written by
/// <c>scripts/make-lab.mjs</c>). Folder permissions are inherited down the tree the way NTFS propagates them, so the
/// scanner sees full per-folder descriptors just as it would on Windows. Never registered in production.
/// </summary>
public sealed class LabSecurityReader(string root) : ISecurityReader
{
    private const AceInheritance Both = AceInheritance.ObjectInherit | AceInheritance.ContainerInherit;
    private readonly Dictionary<string, LabServer?> _servers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    public SecurityRead ReadShare(string hostname, string shareName)
    {
        var lab = Load(hostname);
        if (lab is null)
        {
            return SecurityRead.Denied("Windows only shows share permissions to administrators of the file server.");
        }
        if (!lab.Shares.TryGetValue(shareName, out var share))
        {
            return new SecurityRead(PermissionState.Read, SecurityDescriptorCodec.Build(new SecurityDescriptor(0, WellKnownSid.Administrators, null,
                [new Ace(AceKind.Allow, 0, AceInheritance.None, AccessMask.ShareRead, WellKnownSid.Everyone)])));
        }
        return share is null
            ? SecurityRead.Denied("Windows only shows share permissions to administrators of the file server.")
            : new SecurityRead(PermissionState.Read, SecurityDescriptorCodec.Build(new SecurityDescriptor(0, WellKnownSid.Administrators, null,
                share.Select(a => a.ToAce(AceInheritance.None, share: true)).ToList())));
    }

    public SecurityRead ReadFolder(string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        var parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] == ".." || !Directory.Exists(fullPath))
        {
            return SecurityRead.Failed("The folder disappeared before its permissions were read.");
        }
        var lab = Load(parts[0]);
        if (lab is null)
        {
            return new SecurityRead(PermissionState.Read, SecurityDescriptorCodec.Build(Default));
        }
        var path = string.Join('/', parts[1..]);
        if (lab.Denied.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            return SecurityRead.Denied("The scan account may not read this folder's permissions.");
        }
        return new SecurityRead(PermissionState.Read, SecurityDescriptorCodec.Build(Compute(lab, parts[1..])));
    }

    public LocalGroupsRead ReadLocalGroups(string hostname)
    {
        var lab = Load(hostname);
        return lab?.LocalGroups is { } groups
            ? new LocalGroupsRead(LocalGroupsState.Read, groups)
            : new LocalGroupsRead(LocalGroupsState.AccessDenied, [], "Windows only lets administrators list the file server's local groups (Restrict remote SAM).");
    }

    private static SecurityDescriptor Default => new(0, WellKnownSid.Administrators, null,
    [
        new Ace(AceKind.Allow, 0, Both, AccessMask.FileAllAccess, WellKnownSid.LocalSystem),
        new Ace(AceKind.Allow, 0, Both, AccessMask.FileAllAccess, WellKnownSid.Administrators),
        new Ace(AceKind.Allow, 0, Both, 0x1200A9, WellKnownSid.Users),
    ]);

    /// <summary>Applies NTFS inheritance from the share root down to the folder.</summary>
    private static SecurityDescriptor Compute(LabServer lab, string[] segments)
    {
        SecurityDescriptor? current = null;
        for (var depth = 1; depth <= segments.Length; depth++)
        {
            var key = string.Join('/', segments[..depth]);
            lab.Folders.TryGetValue(key, out var entry);
            if (current is null)
            {
                var explicitAces = entry?.Aces.Select(a => a.ToAce(a.Flags(), share: false)).ToList() ?? Default.Dacl!.ToList();
                current = new SecurityDescriptor(SecurityDescriptor.DaclProtected, entry?.Owner ?? WellKnownSid.Administrators, null, explicitAces);
                continue;
            }
            var inherited = current.Dacl!.Select(Inherit).OfType<Ace>().ToList();
            if (entry is null)
            {
                current = new SecurityDescriptor(0, current.Owner, null, inherited);
                continue;
            }
            var own = entry.Aces.Select(a => a.ToAce(a.Flags(), share: false)).ToList();
            var ordered = own.Where(a => a.IsDeny).Concat(own.Where(a => !a.IsDeny));
            current = new SecurityDescriptor(entry.Protected ? SecurityDescriptor.DaclProtected : (ushort)0, entry.Owner ?? current.Owner, null,
                entry.Protected ? ordered.ToList() : [.. ordered, .. inherited]);
        }
        return current!;
    }

    /// <summary>What a child folder inherits from one parent entry (MS-DTYP inheritance rules for containers).</summary>
    private static Ace? Inherit(Ace parent)
    {
        var flags = parent.Flags;
        if (flags.HasFlag(AceInheritance.ContainerInherit))
        {
            var kept = flags.HasFlag(AceInheritance.NoPropagateInherit) ? AceInheritance.None : flags & Both;
            return parent with { Flags = kept | AceInheritance.Inherited };
        }
        if (flags.HasFlag(AceInheritance.ObjectInherit) && !flags.HasFlag(AceInheritance.NoPropagateInherit))
        {
            return parent with { Flags = AceInheritance.ObjectInherit | AceInheritance.InheritOnly | AceInheritance.Inherited };
        }
        return null;
    }

    private LabServer? Load(string hostname)
    {
        lock (_lock)
        {
            if (_servers.TryGetValue(hostname, out var cached))
            {
                return cached;
            }
            var file = Path.Join(root, hostname + ".security.json");
            LabServer? server = null;
            if (File.Exists(file))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { MaxDepth = 16 });
                server = LabServer.From(doc.RootElement);
            }
            _servers[hostname] = server;
            return server;
        }
    }

    private sealed class LabServer
    {
        public List<LocalGroup>? LocalGroups { get; private init; }
        public Dictionary<string, List<LabAce>?> Shares { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, LabFolder> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Denied { get; } = [];

        public static LabServer From(JsonElement e)
        {
            var server = new LabServer
            {
                LocalGroups = e.TryGetProperty("localGroups", out var lg) && lg.ValueKind == JsonValueKind.Array
                    ? lg.EnumerateArray().Select(g => new LocalGroup(
                        g.GetProperty("sid").GetString()!,
                        g.GetProperty("name").GetString()!,
                        g.GetProperty("members").EnumerateArray()
                            .Select(m => new LocalMember(m.GetProperty("sid").GetString()!, Enum.Parse<LocalMemberKind>(m.GetProperty("kind").GetString()!)))
                            .ToList())).ToList()
                    : null,
            };
            if (e.TryGetProperty("shares", out var shares))
            {
                foreach (var share in shares.EnumerateObject())
                {
                    server.Shares[share.Name] = share.Value.ValueKind == JsonValueKind.Array ? share.Value.EnumerateArray().Select(LabAce.From).ToList() : null;
                }
            }
            if (e.TryGetProperty("folders", out var folders))
            {
                foreach (var folder in folders.EnumerateObject())
                {
                    server.Folders[folder.Name] = new LabFolder(
                        folder.Value.TryGetProperty("owner", out var o) ? o.GetString() : null,
                        folder.Value.TryGetProperty("protected", out var p) && p.GetBoolean(),
                        folder.Value.TryGetProperty("aces", out var aces) ? aces.EnumerateArray().Select(LabAce.From).ToList() : []);
                }
            }
            if (e.TryGetProperty("denied", out var denied))
            {
                server.Denied.AddRange(denied.EnumerateArray().Select(d => d.GetString()!));
            }
            return server;
        }
    }

    private sealed record LabFolder(string? Owner, bool Protected, List<LabAce> Aces);

    private sealed record LabAce(string Sid, string Rights, bool Deny, string Applies)
    {
        public static LabAce From(JsonElement e) => new(
            e.GetProperty("sid").GetString()!,
            e.TryGetProperty("rights", out var r) ? r.GetString()! : "Read",
            e.TryGetProperty("deny", out var d) && d.GetBoolean(),
            e.TryGetProperty("applies", out var a) ? a.GetString()! : "all");

        public AceInheritance Flags() => Applies switch
        {
            "folder" => AceInheritance.None,
            "files" => AceInheritance.ObjectInherit | AceInheritance.InheritOnly,
            "subfolders" => AceInheritance.ContainerInherit | AceInheritance.InheritOnly,
            "subfolders-and-files" => Both | AceInheritance.InheritOnly,
            _ => Both,
        };

        public Ace ToAce(AceInheritance flags, bool share)
        {
            var mask = Rights switch
            {
                "Full" => AccessMask.FileAllAccess,
                "Modify" or "Change" => share ? AccessMask.ShareChange : 0x1301BFu,
                "Write" => 0x120116u | 0x1200A9u,
                _ => share ? AccessMask.ShareRead : 0x1200A9u,
            };
            return new Ace(Deny ? AceKind.Deny : AceKind.Allow, Deny ? (byte)1 : (byte)0, flags, mask, Sid);
        }
    }
}
