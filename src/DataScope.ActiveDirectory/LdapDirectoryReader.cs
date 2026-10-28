using System.Globalization;
using System.DirectoryServices.Protocols;
using System.Net;
using DataScope.Core.Access;

namespace DataScope.ActiveDirectory;

/// <summary>Where and how to read Active Directory. Nothing here is required on a domain-joined server.</summary>
public sealed class LdapDirectoryOptions
{
    /// <summary>DNS name of the domain, such as northwind.test. Defaults to the computer's own domain.</summary>
    public string? Domain { get; set; }

    /// <summary>A specific domain controller. By default Windows picks one for <see cref="Domain"/>.</summary>
    public string? Server { get; set; }

    /// <summary>0 means 389, or 636 with <see cref="UseLdaps"/>.</summary>
    public int Port { get; set; }

    /// <summary>LDAP over TLS. Without it, Windows still signs and encrypts the session with Kerberos.</summary>
    public bool UseLdaps { get; set; }

    /// <summary>
    /// Optional account to read with. Leave empty to use the service's own identity (recommended: a gMSA).
    /// A password is only accepted over LDAPS, or with <see cref="AllowUnencryptedPassword"/> in a test lab.
    /// </summary>
    public string? UserName { get; set; }

    public string? Password { get; set; }

    /// <summary>Test labs only: send a simple-bind password without TLS. The worker refuses this outside Development.</summary>
    public bool AllowUnencryptedPassword { get; set; }

    public int PageSize { get; set; } = 500;

    /// <summary>Stop (and mark the snapshot incomplete) after this many objects.</summary>
    public int MaxObjects { get; set; } = 500_000;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);

    public void Validate()
    {
        if (PageSize is < 10 or > 5000 || MaxObjects < 1 || Timeout <= TimeSpan.Zero || Port is < 0 or > 65535)
        {
            throw new InvalidOperationException("Directory: PageSize must be 10 to 5000, MaxObjects and Timeout must be positive, and Port must be a valid port.");
        }
        if (string.IsNullOrEmpty(UserName) != string.IsNullOrEmpty(Password) && !OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("Directory: set both UserName and Password, or neither.");
        }
        if (!string.IsNullOrEmpty(Password) && !UseLdaps && !AllowUnencryptedPassword && !OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("Directory: a password can only be sent over LDAPS. Set UseLdaps, or use the service identity.");
        }
    }
}

/// <summary>
/// Reads users, groups, computers, foreign security principals and group memberships from one AD domain over LDAP,
/// with paged searches and ranged retrieval for large groups. Read-only: it only sends bind and search requests.
/// </summary>
public sealed class LdapDirectoryReader(LdapDirectoryOptions options) : IDirectoryReader
{
    private const uint UacDisabled = 0x2;
    private const uint GroupTypeSecurity = 0x8000_0000;

    private static readonly string[] Attributes =
    [
        "objectSid", "objectClass", "sAMAccountName", "displayName", "name", "distinguishedName", "userAccountControl",
        "primaryGroupID", "groupType", "member", "mail", "department", "title", "userPrincipalName",
        "lastLogonTimestamp", "whenChanged", "sIDHistory", "manager",
    ];

    public Task<DirectoryReadResult> ReadAsync(CancellationToken ct) => Task.Run(() => Read(ct), ct);

    private DirectoryReadResult Read(CancellationToken ct)
    {
        options.Validate();
        using var connection = Connect();
        var rootDse = Search(connection, null, "(objectClass=*)", SearchScope.Base, ["defaultNamingContext", "dnsHostName"]).SingleOrDefault()
            ?? throw new DirectoryReadException("The domain controller did not answer the directory query.");
        var namingContext = Text(rootDse, "defaultNamingContext") ?? throw new DirectoryReadException("The domain controller did not report its domain.");
        var server = Text(rootDse, "dnsHostName");
        var domainEntry = Search(connection, namingContext, "(objectClass=*)", SearchScope.Base, ["objectSid"]).SingleOrDefault();
        var domainSid = domainEntry is null ? null : SidOf(domainEntry);
        if (domainSid is null)
        {
            throw new DirectoryReadException("The domain object has no SID; this does not look like Active Directory.");
        }

        var objects = new List<DirectoryObject>();
        var memberDns = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var ignoredDns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        var truncated = false;
        var skippedNoSid = 0;

        var request = new SearchRequest(namingContext, "(|(objectClass=user)(objectClass=group)(objectClass=foreignSecurityPrincipal)(objectClass=contact))", SearchScope.Subtree, Attributes);
        var paging = new PageResultRequestControl(options.PageSize) { IsCritical = true };
        request.Controls.Add(paging);
        request.Controls.Add(new SearchOptionsControl(System.DirectoryServices.Protocols.SearchOption.DomainScope));
        request.TimeLimit = options.Timeout;
        while (!truncated)
        {
            ct.ThrowIfCancellationRequested();
            var response = Send(connection, request);
            foreach (SearchResultEntry entry in response.Entries)
            {
                var classes = Texts(entry, "objectClass");
                if (classes.Contains("contact", StringComparer.OrdinalIgnoreCase))
                {
                    // Contacts can be group members but have no SID, so they never grant access.
                    ignoredDns.Add(entry.DistinguishedName);
                    continue;
                }
                var sid = SidOf(entry);
                if (sid is null)
                {
                    skippedNoSid++;
                    continue;
                }
                if (sid.StartsWith(WellKnownSid.BuiltinPrefix, StringComparison.Ordinal))
                {
                    // The domain's BUILTIN groups belong to the domain controllers. File servers have their own,
                    // which the scan reads from each server.
                    ignoredDns.Add(entry.DistinguishedName);
                    continue;
                }
                if (objects.Count >= options.MaxObjects)
                {
                    truncated = true;
                    warnings.Add($"Stopped after {options.MaxObjects:N0} directory objects; the rest were not read.");
                    break;
                }
                var obj = ToObject(entry, classes, sid, domainSid);
                objects.Add(obj);
                if (obj.Kind == PrincipalKind.Group)
                {
                    memberDns[sid] = ReadMembers(connection, entry, ct);
                }
            }
            var cookie = response.Controls.OfType<PageResultResponseControl>().FirstOrDefault()?.Cookie;
            if (cookie is null || cookie.Length == 0)
            {
                break;
            }
            paging.Cookie = cookie;
        }
        if (skippedNoSid > 0)
        {
            warnings.Add($"{skippedNoSid} objects without a SID were skipped.");
        }

        var filtered = memberDns.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<string>)kv.Value.Where(dn => !ignoredDns.Contains(dn)).ToList(),
            StringComparer.Ordinal);
        var (resolved, edges, unresolved) = Memberships.Resolve(objects, filtered);
        if (unresolved > 0)
        {
            warnings.Add($"{unresolved} group members could not be matched to an account in this domain. Access through those groups is shown as uncertain.");
        }
        return new DirectoryReadResult(DomainName(namingContext), domainSid, namingContext, server, resolved, edges, warnings, unresolved, truncated);
    }

    private LdapConnection Connect()
    {
        var port = options.Port != 0 ? options.Port : options.UseLdaps ? 636 : 389;
        var host = options.Server ?? options.Domain;
        var identifier = host is null ? new LdapDirectoryIdentifier((string?)null, port) : new LdapDirectoryIdentifier(host, port);
        var connection = new LdapConnection(identifier) { Timeout = options.Timeout };
        connection.SessionOptions.ProtocolVersion = 3;
        connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
        if (options.UseLdaps)
        {
            connection.SessionOptions.SecureSocketLayer = true;
        }
        try
        {
            if (OperatingSystem.IsWindows() && !(options.AllowUnencryptedPassword && !string.IsNullOrEmpty(options.Password)))
            {
                // Kerberos (or NTLM) with signing and sealing: the session is integrity-protected and encrypted.
                connection.AuthType = AuthType.Negotiate;
                if (!options.UseLdaps)
                {
                    connection.SessionOptions.Signing = true;
                    connection.SessionOptions.Sealing = true;
                }
                if (!string.IsNullOrEmpty(options.UserName))
                {
                    connection.Credential = new NetworkCredential(options.UserName, options.Password);
                }
                connection.Bind();
            }
            else if (!string.IsNullOrEmpty(options.UserName))
            {
                connection.AuthType = AuthType.Basic;
                connection.Bind(new NetworkCredential(options.UserName, options.Password));
            }
            else
            {
                throw new DirectoryReadException("Reading the directory from a non-Windows host needs a user name and password over LDAPS.");
            }
        }
        catch (LdapException ex)
        {
            connection.Dispose();
            throw new DirectoryReadException(ex.ErrorCode switch
            {
                49 => "The directory refused the sign-in for the account Data Scope uses.",
                81 => $"No domain controller answered on {host ?? "the computer's domain"} port {port}.",
                _ => $"Signing in to the directory failed (LDAP error {ex.ErrorCode}).",
            }, ex);
        }
        catch (DirectoryOperationException ex)
        {
            connection.Dispose();
            throw new DirectoryReadException("Signing in to the directory failed: the domain controller requires a more secure connection.", ex);
        }
        return connection;
    }

    private List<SearchResultEntry> Search(LdapConnection connection, string? baseDn, string filter, SearchScope scope, string[] attributes)
    {
        var request = new SearchRequest(baseDn, filter, scope, attributes) { TimeLimit = options.Timeout };
        return Send(connection, request).Entries.Cast<SearchResultEntry>().ToList();
    }

    private SearchResponse Send(LdapConnection connection, SearchRequest request)
    {
        try
        {
            return (SearchResponse)connection.SendRequest(request, options.Timeout);
        }
        catch (LdapException ex)
        {
            throw new DirectoryReadException($"The directory query failed (LDAP error {ex.ErrorCode}).", ex);
        }
        catch (DirectoryOperationException ex)
        {
            throw new DirectoryReadException($"The directory query was refused ({ex.Response?.ResultCode.ToString() ?? "unknown reason"}).", ex);
        }
    }

    /// <summary>
    /// AD returns at most 1,500 values of <c>member</c> at a time, under a name like <c>member;range=0-1499</c>.
    /// The rest are fetched range by range until the range ends with "*".
    /// </summary>
    private List<string> ReadMembers(LdapConnection connection, SearchResultEntry entry, CancellationToken ct)
    {
        var members = new List<string>(Texts(entry, "member"));
        var rangeName = entry.Attributes.AttributeNames.Cast<string>()
            .FirstOrDefault(n => n.StartsWith("member;range=", StringComparison.OrdinalIgnoreCase));
        while (rangeName is not null)
        {
            ct.ThrowIfCancellationRequested();
            members.AddRange(Texts(entry, rangeName));
            var range = rangeName["member;range=".Length..];
            var end = range[(range.IndexOf('-', StringComparison.Ordinal) + 1)..];
            if (end == "*" || !int.TryParse(end, NumberStyles.None, CultureInfo.InvariantCulture, out var last))
            {
                break;
            }
            var next = Search(connection, entry.DistinguishedName, "(objectClass=*)", SearchScope.Base, [$"member;range={last + 1}-*"]).SingleOrDefault();
            if (next is null)
            {
                break;
            }
            entry = next;
            rangeName = entry.Attributes.AttributeNames.Cast<string>()
                .FirstOrDefault(n => n.StartsWith("member;range=", StringComparison.OrdinalIgnoreCase));
        }
        return members;
    }

    private static DirectoryObject ToObject(SearchResultEntry entry, IReadOnlyList<string> classes, string sid, string domainSid)
    {
        var kind = classes.Contains("foreignSecurityPrincipal", StringComparer.OrdinalIgnoreCase) ? PrincipalKind.ForeignSecurityPrincipal
            : classes.Contains("group", StringComparer.OrdinalIgnoreCase) ? PrincipalKind.Group
            : classes.Contains("computer", StringComparer.OrdinalIgnoreCase) ? PrincipalKind.Computer
            : PrincipalKind.User;
        var uac = Number(entry, "userAccountControl");
        var groupType = Number(entry, "groupType");
        var primary = Number(entry, "primaryGroupID");
        var name = Text(entry, "sAMAccountName") ?? Text(entry, "name") ?? sid;
        GroupScope? scope = kind != PrincipalKind.Group ? null
            : (groupType & 0x4) != 0 ? GroupScope.DomainLocal
            : (groupType & 0x8) != 0 ? GroupScope.Universal
            : GroupScope.Global;
        return new DirectoryObject(
            sid,
            kind,
            name,
            entry.DistinguishedName,
            DisplayName: Text(entry, "displayName") ?? Text(entry, "name"),
            Enabled: kind is not (PrincipalKind.User or PrincipalKind.Computer) || (uac & UacDisabled) == 0,
            IsSecurityGroup: kind != PrincipalKind.Group || (groupType & GroupTypeSecurity) != 0,
            Scope: scope,
            PrimaryGroupSid: primary > 0 && kind is PrincipalKind.User or PrincipalKind.Computer ? $"{domainSid}-{primary.ToString(CultureInfo.InvariantCulture)}" : null,
            UserPrincipalName: Text(entry, "userPrincipalName"),
            Mail: Text(entry, "mail"),
            Department: Text(entry, "department"),
            Title: Text(entry, "title"),
            LastLogon: FileTime(Text(entry, "lastLogonTimestamp")),
            WhenChanged: GeneralizedTime(Text(entry, "whenChanged")),
            ManagerDn: kind == PrincipalKind.User ? Text(entry, "manager") : null,
            SidHistory: Binaries(entry, "sIDHistory").Select(b => Sid.TryRead(b, out var s, out _) ? s : null).OfType<string>().ToList());
    }

    private static string? SidOf(SearchResultEntry entry) =>
        Binaries(entry, "objectSid") is [var bytes, ..] && Sid.TryRead(bytes, out var sid, out var length) && length == bytes.Length ? sid : null;

    private static string? Text(SearchResultEntry entry, string name) => Texts(entry, name) is [var first, ..] ? first : null;

    private static List<string> Texts(SearchResultEntry entry, string name) =>
        entry.Attributes.Contains(name) ? entry.Attributes[name].GetValues(typeof(string)).Cast<string>().ToList() : [];

    private static List<byte[]> Binaries(SearchResultEntry entry, string name) =>
        entry.Attributes.Contains(name) ? entry.Attributes[name].GetValues(typeof(byte[])).Cast<byte[]>().ToList() : [];

    private static long Number(SearchResultEntry entry, string name) =>
        long.TryParse(Text(entry, name), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? (uint)n : 0;

    internal static DateTimeOffset? FileTime(string? text) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var ft) && ft > 0 && ft < DateTime.MaxValue.ToFileTimeUtc()
            ? new DateTimeOffset(DateTime.FromFileTimeUtc(ft))
            : null;

    internal static DateTimeOffset? GeneralizedTime(string? text) =>
        text is not null && DateTimeOffset.TryParseExact(text, ["yyyyMMddHHmmss.0'Z'", "yyyyMMddHHmmss'Z'"], CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : null;

    internal static string DomainName(string namingContext) =>
        string.Join('.', namingContext.Split(',')
            .Select(p => p.Trim())
            .Where(p => p.StartsWith("DC=", StringComparison.OrdinalIgnoreCase))
            .Select(p => p[3..].ToLowerInvariant()));
}
