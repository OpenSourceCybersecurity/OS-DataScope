using System.Globalization;
using System.Security;
using System.Text;

namespace DataScope.Tests.Activity;

/// <summary>
/// Synthetic Windows Security events, written the way <c>wevtutil qe Security /f:xml</c> renders them. Every name,
/// SID and path here is made up.
/// </summary>
public static class EventXml
{
    public const string Alice = "S-1-5-21-1000-2000-3000-1104";
    public const string Bob = "S-1-5-21-1000-2000-3000-1105";
    public static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    public const ulong Success = 0x8020000000000000;
    public const ulong Failure = 0x8010000000000000;

    public static string Event(
        int eventId, long recordId, IEnumerable<(string Name, string Value)> data, DateTimeOffset? time = null,
        ulong keywords = Success, string provider = "Microsoft-Windows-Security-Auditing", string computer = "fs01.northwind.test")
    {
        var sb = new StringBuilder();
        sb.Append("<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System>");
        sb.Append(CultureInfo.InvariantCulture, $"<Provider Name='{provider}' Guid='{{54849625-5478-4994-a5ba-3e3b0328c30d}}'/>");
        sb.Append(CultureInfo.InvariantCulture, $"<EventID>{eventId}</EventID><Version>1</Version><Level>0</Level><Task>12800</Task><Opcode>0</Opcode>");
        sb.Append(CultureInfo.InvariantCulture, $"<Keywords>0x{keywords:x}</Keywords>");
        sb.Append(CultureInfo.InvariantCulture, $"<TimeCreated SystemTime='{(time ?? T0.AddSeconds(recordId)).UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffffff}Z'/>");
        sb.Append(CultureInfo.InvariantCulture, $"<EventRecordID>{recordId}</EventRecordID><Correlation/><Execution ProcessID='4' ThreadID='92'/>");
        sb.Append(CultureInfo.InvariantCulture, $"<Channel>Security</Channel><Computer>{computer}</Computer><Security/></System><EventData>");
        foreach (var (name, value) in data)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<Data Name='{name}'>{SecurityElement.Escape(value)}</Data>");
        }
        sb.Append("</EventData></Event>");
        return sb.ToString();
    }

    private static (string, string)[] Subject(string sid, string user, string logon) =>
        [("SubjectUserSid", sid), ("SubjectUserName", user), ("SubjectDomainName", "NORTHWIND"), ("SubjectLogonId", logon)];

    /// <summary>4663: an operation on a file or folder, as logged for an SMB client (process 4, no process name).</summary>
    public static string ObjectAccess(long recordId, string path, uint mask, string sid = Alice, string user = "alice", string logon = "0x3e7a1", long handle = 0x1ac, DateTimeOffset? time = null) =>
        Event(4663, recordId, [.. Subject(sid, user, logon), ("ObjectServer", "Security"), ("ObjectType", "File"), ("ObjectName", path),
            ("HandleId", $"0x{handle:x}"), ("AccessList", "%%4416"), ("AccessMask", $"0x{mask:x}"), ("ProcessId", "0x4"), ("ProcessName", ""), ("ResourceAttributes", "S:AI")], time);

    /// <summary>4656: a handle request, success or failure.</summary>
    public static string HandleRequest(long recordId, string path, uint mask, bool denied = false, string sid = Alice, string user = "alice", string logon = "0x3e7a1", long handle = 0x1ac) =>
        Event(4656, recordId, [.. Subject(sid, user, logon), ("ObjectServer", "Security"), ("ObjectType", "File"), ("ObjectName", path),
            ("HandleId", denied ? "0x0" : $"0x{handle:x}"), ("TransactionId", "{00000000-0000-0000-0000-000000000000}"), ("AccessList", "%%1537"),
            ("AccessReason", "-"), ("AccessMask", $"0x{mask:x}"), ("PrivilegeList", "-"), ("RestrictedSidCount", "0"), ("ProcessId", "0x4"), ("ProcessName", "")],
            keywords: denied ? Failure : Success);

    /// <summary>4660: an object was deleted. No path; only the handle.</summary>
    public static string Deleted(long recordId, string sid = Alice, string user = "alice", string logon = "0x3e7a1", long handle = 0x1ac, DateTimeOffset? time = null) =>
        Event(4660, recordId, [.. Subject(sid, user, logon), ("ObjectServer", "Security"), ("HandleId", $"0x{handle:x}"), ("ProcessId", "0x4"), ("ProcessName", ""),
            ("TransactionId", "{00000000-0000-0000-0000-000000000000}")], time);

    /// <summary>4670: permissions changed.</summary>
    public static string PermissionsChanged(long recordId, string path, string oldSd, string newSd, string sid = Alice, string user = "alice") =>
        Event(4670, recordId, [.. Subject(sid, user, "0x3e7a1"), ("ObjectServer", "Security"), ("ObjectType", "File"), ("ObjectName", path),
            ("HandleId", "0x2b8"), ("OldSd", oldSd), ("NewSd", newSd), ("ProcessId", "0x4"), ("ProcessName", "")]);

    /// <summary>5140: a share was connected to.</summary>
    public static string ShareConnect(long recordId, string share, string localPath, string sid = Alice, string user = "alice") =>
        Event(5140, recordId, [.. Subject(sid, user, "0x3e7a1"), ("ObjectType", "File"), ("IpAddress", "10.0.0.25"), ("IpPort", "50112"),
            ("ShareName", $@"\\*\{share}"), ("ShareLocalPath", $@"\??\{localPath}"), ("AccessMask", "0x1"), ("AccessList", "%%4416")]);

    /// <summary>5145: a detailed share access check.</summary>
    public static string ShareCheck(long recordId, string share, string localPath, string relative, uint mask, bool denied = false, string sid = Alice, string user = "alice") =>
        Event(5145, recordId, [.. Subject(sid, user, "0x3e7a1"), ("ObjectType", "File"), ("IpAddress", "10.0.0.25"), ("IpPort", "50112"),
            ("ShareName", $@"\\*\{share}"), ("ShareLocalPath", $@"\??\{localPath}"), ("RelativeTargetName", relative),
            ("AccessMask", $"0x{mask:x}"), ("AccessList", "%%4416"), ("AccessReason", "-")], keywords: denied ? Failure : Success);

    /// <summary>1102: the Security log was cleared (UserData, not EventData).</summary>
    public static string LogCleared(long recordId, string sid = Alice) =>
        "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Microsoft-Windows-Eventlog' Guid='{fc65ddd8-d6ef-4962-83d5-6e5cfe9ce148}'/>"
        + $"<EventID>1102</EventID><Version>0</Version><Level>4</Level><Task>104</Task><Opcode>0</Opcode><Keywords>0x4020000000000000</Keywords>"
        + $"<TimeCreated SystemTime='{T0.AddSeconds(recordId).UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffffff}Z'/><EventRecordID>{recordId}</EventRecordID><Correlation/>"
        + "<Execution ProcessID='1060' ThreadID='4520'/><Channel>Security</Channel><Computer>fs01.northwind.test</Computer><Security/></System>"
        + $"<UserData><LogFileCleared xmlns='http://manifests.microsoft.com/win/2004/08/windows/eventlog'><SubjectUserSid>{sid}</SubjectUserSid>"
        + "<SubjectUserName>alice</SubjectUserName><SubjectDomainName>NORTHWIND</SubjectDomainName><SubjectLogonId>0x3e7a1</SubjectLogonId></LogFileCleared></UserData></Event>";
}
