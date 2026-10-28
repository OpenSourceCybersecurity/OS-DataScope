using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace DataScope.Core.Access;

/// <summary>
/// Windows security identifiers in their string form ("S-1-5-21-…"). Parsing and formatting are done here, in
/// managed code, so they behave the same on every OS and never trust lengths found in the input.
/// </summary>
public static class Sid
{
    public const int MaxSubAuthorities = 15;

    /// <summary>Longest possible string form; also the column width used to store SIDs.</summary>
    public const int MaxStringLength = 184;

    /// <summary>Reads a binary SID from the start of <paramref name="data"/>.</summary>
    public static bool TryRead(ReadOnlySpan<byte> data, out string sid, out int length)
    {
        sid = "";
        length = 0;
        if (data.Length < 8 || data[0] != 1 || data[1] > MaxSubAuthorities)
        {
            return false;
        }
        var count = data[1];
        length = 8 + (4 * count);
        if (data.Length < length)
        {
            length = 0;
            return false;
        }
        ulong authority = 0;
        for (var i = 2; i < 8; i++)
        {
            authority = (authority << 8) | data[i];
        }
        var text = new StringBuilder("S-1-", 64);
        text.Append(authority < (1UL << 32)
            ? authority.ToString(CultureInfo.InvariantCulture)
            : "0x" + authority.ToString("X12", CultureInfo.InvariantCulture));
        for (var i = 0; i < count; i++)
        {
            text.Append('-').Append(BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(8 + (4 * i), 4)).ToString(CultureInfo.InvariantCulture));
        }
        sid = text.ToString();
        return true;
    }

    public static string FromBinary(ReadOnlySpan<byte> data) =>
        TryRead(data, out var sid, out var length) && length == data.Length
            ? sid
            : throw new FormatException("Not a valid binary SID.");

    /// <summary>Parses the string form. Accepts only the canonical "S-1-…" syntax.</summary>
    public static bool TryParse(string? text, out ulong authority, out uint[] subAuthorities)
    {
        authority = 0;
        subAuthorities = [];
        if (text is null || text.Length is < 5 or > MaxStringLength || !text.StartsWith("S-1-", StringComparison.Ordinal))
        {
            return false;
        }
        var parts = text[4..].Split('-');
        if (parts.Length < 1 || parts.Length - 1 > MaxSubAuthorities)
        {
            return false;
        }
        var first = parts[0];
        var parsed = first.StartsWith("0x", StringComparison.Ordinal)
            ? ulong.TryParse(first.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out authority)
            : first.All(char.IsAsciiDigit) && ulong.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out authority);
        if (!parsed || authority >= (1UL << 48))
        {
            return false;
        }
        var subs = new uint[parts.Length - 1];
        for (var i = 1; i < parts.Length; i++)
        {
            if (parts[i].Length == 0 || !parts[i].All(char.IsAsciiDigit) || !uint.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out subs[i - 1]))
            {
                return false;
            }
        }
        subAuthorities = subs;
        return true;
    }

    public static bool IsValid(string? text) => TryParse(text, out _, out _);

    public static byte[] ToBinary(string sid)
    {
        if (!TryParse(sid, out var authority, out var subs))
        {
            throw new FormatException("Not a valid SID string.");
        }
        var bytes = new byte[8 + (4 * subs.Length)];
        bytes[0] = 1;
        bytes[1] = (byte)subs.Length;
        for (var i = 0; i < 6; i++)
        {
            bytes[2 + i] = (byte)(authority >> (8 * (5 - i)));
        }
        for (var i = 0; i < subs.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8 + (4 * i)), subs[i]);
        }
        return bytes;
    }

    /// <summary>The account domain part of a domain or machine account SID (S-1-5-21-a-b-c), or null.</summary>
    public static string? AccountDomain(string sid)
    {
        if (!sid.StartsWith("S-1-5-21-", StringComparison.Ordinal))
        {
            return null;
        }
        var last = sid.LastIndexOf('-');
        var domain = sid[..last];
        return domain.Count(c => c == '-') == 6 ? domain : null;
    }

    /// <summary>The last sub-authority (the relative id) of a SID.</summary>
    public static uint? Rid(string sid)
    {
        var last = sid.LastIndexOf('-');
        return last > 0 && uint.TryParse(sid.AsSpan(last + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var rid) ? rid : null;
    }
}

/// <summary>Well-known SIDs that matter when reading file share permissions.</summary>
public static class WellKnownSid
{
    public const string Everyone = "S-1-1-0";
    public const string CreatorOwner = "S-1-3-0";
    public const string CreatorGroup = "S-1-3-1";
    public const string OwnerRights = "S-1-3-4";
    public const string Network = "S-1-5-2";
    public const string Interactive = "S-1-5-4";
    public const string Anonymous = "S-1-5-7";
    public const string AuthenticatedUsers = "S-1-5-11";
    public const string ThisOrganization = "S-1-5-15";
    public const string LocalSystem = "S-1-5-18";
    public const string LocalService = "S-1-5-19";
    public const string NetworkService = "S-1-5-20";
    public const string BuiltinPrefix = "S-1-5-32-";
    public const string Administrators = "S-1-5-32-544";
    public const string Users = "S-1-5-32-545";
    public const string Guests = "S-1-5-32-546";
    public const string BackupOperators = "S-1-5-32-551";

    public const uint DomainAdminsRid = 512;
    public const uint DomainUsersRid = 513;
    public const uint DomainGuestsRid = 514;
    public const uint DomainComputersRid = 515;

    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        [Everyone] = "Everyone",
        [CreatorOwner] = "CREATOR OWNER",
        [CreatorGroup] = "CREATOR GROUP",
        [OwnerRights] = "OWNER RIGHTS",
        [Network] = "NETWORK",
        [Interactive] = "INTERACTIVE",
        ["S-1-5-3"] = "BATCH",
        ["S-1-5-6"] = "SERVICE",
        [Anonymous] = "ANONYMOUS LOGON",
        ["S-1-5-9"] = "Enterprise Domain Controllers",
        ["S-1-5-10"] = "SELF",
        [AuthenticatedUsers] = "Authenticated Users",
        ["S-1-5-14"] = "Remote Interactive Logon",
        [ThisOrganization] = "This Organization",
        [LocalSystem] = "SYSTEM",
        [LocalService] = "LOCAL SERVICE",
        [NetworkService] = "NETWORK SERVICE",
        [Administrators] = @"BUILTIN\Administrators",
        [Users] = @"BUILTIN\Users",
        [Guests] = @"BUILTIN\Guests",
        ["S-1-5-32-547"] = @"BUILTIN\Power Users",
        ["S-1-5-32-548"] = @"BUILTIN\Account Operators",
        ["S-1-5-32-549"] = @"BUILTIN\Server Operators",
        ["S-1-5-32-550"] = @"BUILTIN\Print Operators",
        [BackupOperators] = @"BUILTIN\Backup Operators",
        ["S-1-5-32-555"] = @"BUILTIN\Remote Desktop Users",
        ["S-1-5-32-559"] = @"BUILTIN\Performance Log Users",
        ["S-1-5-32-568"] = @"BUILTIN\IIS_IUSRS",
        ["S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"] = "TrustedInstaller",
    };

    public static string? NameOf(string sid) => Names.GetValueOrDefault(sid);

    /// <summary>Groups every signed-in network user belongs to, whatever their domain group memberships.</summary>
    public static readonly IReadOnlyList<string> ImplicitForNetworkLogon = [Everyone, Network, AuthenticatedUsers, ThisOrganization];

    /// <summary>
    /// Trustees that reach (almost) every account: Everyone, Authenticated Users, BUILTIN\Users, Domain Users and
    /// similar. Access granted to one of these is open access.
    /// </summary>
    public static bool IsBroad(string sid) =>
        sid is Everyone or Network or AuthenticatedUsers or ThisOrganization or Anonymous or Users or Guests or Interactive
        || (Sid.AccountDomain(sid) is not null && Sid.Rid(sid) is DomainUsersRid or DomainGuestsRid or DomainComputersRid);

    /// <summary>
    /// Trustees whose access is expected for administration (SYSTEM, Administrators, Domain Admins, Backup Operators,
    /// TrustedInstaller). Shown, but not flagged as exposure.
    /// </summary>
    public static bool IsAdministrative(string sid) =>
        sid is LocalSystem or Administrators or BackupOperators or "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"
        || (Sid.AccountDomain(sid) is not null && Sid.Rid(sid) is DomainAdminsRid or 518 or 519);

    /// <summary>Placeholders that only mean something in inheritable entries.</summary>
    public static bool IsCreatorPlaceholder(string sid) => sid is CreatorOwner or CreatorGroup;
}
