using System.Text.Json;
using System.Text.Json.Serialization;
using DataScope.Core.Access;

namespace DataScope.ActiveDirectory;

/// <summary>
/// Development stand-in for Active Directory: reads a synthetic domain from a JSON file written by
/// <c>scripts/make-lab.mjs</c>. Never registered in production.
/// </summary>
public sealed class LabDirectoryReader(string path) : IDirectoryReader
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        MaxDepth = 16,
    };

    public async Task<DirectoryReadResult> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            throw new DirectoryReadException("No domain controller answered (the lab directory file is missing; run scripts/make-lab.mjs).");
        }
        LabDomain? lab;
        await using (var stream = File.OpenRead(path))
        {
            lab = await JsonSerializer.DeserializeAsync<LabDomain>(stream, Json, ct);
        }
        if (lab is null || !Sid.IsValid(lab.DomainSid) || string.IsNullOrWhiteSpace(lab.Domain))
        {
            throw new DirectoryReadException("The lab directory file is not valid.");
        }
        var nc = string.Join(',', lab.Domain.Split('.').Select(p => $"DC={p}"));
        // An optional OU per principal, so a lab directory can mirror real OUs. Members and
        // managers are listed by SID and point at the DN their object was given.
        var valid = lab.Principals.Where(p => Sid.IsValid(p.Sid)).ToList();
        var dnBySid = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in valid)
        {
            dnBySid.TryAdd(p.Sid, string.IsNullOrEmpty(p.Ou) ? $"CN={p.Sid},{nc}" : $"CN={p.Sid},{p.Ou},{nc}");
        }
        string DnOf(string sid) => dnBySid.GetValueOrDefault(sid) ?? $"CN={sid},{nc}";
        var objects = new List<DirectoryObject>();
        var memberDns = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var p in valid)
        {
            objects.Add(new DirectoryObject(
                p.Sid,
                p.Kind,
                p.Name,
                DnOf(p.Sid),
                DisplayName: p.DisplayName,
                Enabled: p.Enabled,
                IsSecurityGroup: p.Security,
                Scope: p.Kind == PrincipalKind.Group ? GroupScope.Global : null,
                PrimaryGroupSid: p.Kind is PrincipalKind.User or PrincipalKind.Computer ? p.PrimaryGroup ?? $"{lab.DomainSid}-513" : null,
                Mail: p.Mail,
                Department: p.Department,
                Title: p.Title,
                ManagerDn: p.Manager is { } manager && Sid.IsValid(manager) ? DnOf(manager) : null));
            if (p.Members.Count > 0)
            {
                memberDns[p.Sid] = [.. p.Members.Select(DnOf)];
            }
        }
        var (resolved, edges, unresolved) = Memberships.Resolve(objects, memberDns);
        List<string> warnings = unresolved > 0 ? [$"{unresolved} group members could not be matched to an account in this domain."] : [];
        return new DirectoryReadResult(lab.Domain, lab.DomainSid, nc, "lab-dc01." + lab.Domain, resolved, edges, warnings, unresolved, false);
    }

    private sealed class LabDomain
    {
        public string Domain { get; set; } = "";
        public string DomainSid { get; set; } = "";
        public List<LabPrincipal> Principals { get; set; } = [];
    }

    private sealed class LabPrincipal
    {
        public string Sid { get; set; } = "";
        public PrincipalKind Kind { get; set; }
        public string Name { get; set; } = "";
        public string? DisplayName { get; set; }
        public bool Enabled { get; set; } = true;
        public bool Security { get; set; } = true;
        public string? PrimaryGroup { get; set; }
        public string? Mail { get; set; }
        public string? Department { get; set; }
        public string? Title { get; set; }
        public string? Manager { get; set; }
        public string? Ou { get; set; }
        public List<string> Members { get; set; } = [];
    }
}
