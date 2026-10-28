using DataScope.Core.Access;

namespace DataScope.Core.Activity;

/// <summary>What one event says, in Data Scope's terms, before it is matched to a share (D97).</summary>
/// <param name="ObjectPath">The object's path on the file server as the event gives it, normalized (C:\Shares\Finance\a.xlsx).</param>
/// <param name="ShareName">For 5140 and 5145: the share used, without the \\*\ prefix.</param>
/// <param name="ShareLocalPath">For 5140 and 5145: the share's folder on the file server.</param>
/// <param name="ShareRelativePath">For 5145: the path within the share, empty for the share itself.</param>
public sealed record InterpretedEvent(
    ActivityOperation Operation,
    ActivityCertainty Certainty,
    ActivityOutcome Outcome,
    string? PrincipalSid,
    string? PrincipalName,
    string? LogonId,
    long? ProcessId,
    string? ProcessName,
    long? HandleId,
    string? ClientAddress,
    string? ObjectPath,
    string? ShareName,
    string? ShareLocalPath,
    string? ShareRelativePath,
    uint? AccessMask,
    string? OldSecurity,
    string? NewSecurity);

/// <summary>
/// Turns Windows Security events about files into activity, saying no more than each event proves: 4663, 4660 and
/// 4670 are operations Windows recorded; 4656 is a request for a handle; 5145 is a share access check and 5140 a share
/// connection, neither of which shows that a file was opened or read.
/// </summary>
public static class ActivityInterpreter
{
    public const string SecurityProvider = "Microsoft-Windows-Security-Auditing";
    public const string EventLogProvider = "Microsoft-Windows-Eventlog";

    /// <summary>The Security event ids Data Scope collects.</summary>
    public static readonly IReadOnlyList<int> CollectedEventIds = [4656, 4660, 4663, 4670, 5140, 5145, 1102];

    /// <summary>Longest security descriptor text kept from a 4670.</summary>
    public const int MaxSecurityText = 8_192;

    /// <summary>
    /// Interprets one event, or returns null when it is not file activity (registry, kernel objects, other event ids,
    /// or another provider writing a look-alike id).
    /// </summary>
    public static InterpretedEvent? Interpret(SecurityEvent ev)
    {
        if (ev.EventId == 1102)
        {
            return ev.Provider == EventLogProvider ? Build(ev, ActivityOperation.LogCleared, ActivityCertainty.Performed) : null;
        }
        if (ev.Provider != SecurityProvider)
        {
            return null;
        }
        switch (ev.EventId)
        {
            case 4663:
                return IsFile(ev) ? Build(ev, Mask(ev) is { } m ? FileAccessMask.Operation(m) : ActivityOperation.Other, ActivityCertainty.Performed, path: ev.Get("ObjectName")) : null;
            case 4656:
                return IsFile(ev) ? Build(ev, ActivityOperation.HandleRequest, ActivityCertainty.Requested, path: ev.Get("ObjectName")) : null;
            case 4670:
                return IsFile(ev)
                    ? Build(ev, ActivityOperation.PermissionChange, ActivityCertainty.Performed, path: ev.Get("ObjectName"),
                        oldSd: Cap(ev.Get("OldSd")), newSd: Cap(ev.Get("NewSd")))
                    : null;
            case 4660:
                // No object type or name: whether this was a file is only known once correlated with its handle (D100).
                return ev.Get("ObjectServer") is null or "Security" ? Build(ev, ActivityOperation.Deleted, ActivityCertainty.Performed) : null;
            case 5140:
            case 5145:
                if (!IsFile(ev, missingIsFile: true))
                {
                    return null;
                }
                var shareName = ShareName(ev.Get("ShareName"));
                var root = LocalPath.Normalize(ev.Get("ShareLocalPath"));
                if (ev.EventId == 5140)
                {
                    return Build(ev, ActivityOperation.ShareConnect, ActivityCertainty.ShareSession, path: root, shareName: shareName, shareRoot: root, relative: "");
                }
                var relative = LocalPath.TrimRelative(ev.Get("RelativeTargetName") ?? "");
                return Build(ev, ActivityOperation.ShareAccessCheck, ActivityCertainty.AccessCheck,
                    path: root is null ? null : LocalPath.Combine(root, relative), shareName: shareName, shareRoot: root, relative: relative);
            default:
                return null;
        }
    }

    private static bool IsFile(SecurityEvent ev, bool missingIsFile = false) =>
        ev.Get("ObjectType") is { } type ? string.Equals(type, "File", StringComparison.OrdinalIgnoreCase) : missingIsFile;

    private static uint? Mask(SecurityEvent ev) =>
        SecurityEventXml.TryParseHex(ev.Get("AccessMask"), out var mask) && mask <= uint.MaxValue ? (uint)mask : null;

    private static InterpretedEvent Build(
        SecurityEvent ev, ActivityOperation op, ActivityCertainty certainty, string? path = null,
        string? shareName = null, string? shareRoot = null, string? relative = null, string? oldSd = null, string? newSd = null)
    {
        var outcome = (ev.Keywords & SecurityEvent.AuditFailureKeyword) != 0 ? ActivityOutcome.Failure : ActivityOutcome.Success;
        var sid = Value(ev.Get("SubjectUserSid"));
        var user = Value(ev.Get("SubjectUserName"));
        var domain = Value(ev.Get("SubjectDomainName"));
        return new InterpretedEvent(
            op,
            certainty,
            outcome,
            Sid.IsValid(sid) ? sid : null,
            user is null ? null : Cap(domain is null ? user : $"{domain}\\{user}", 256),
            Value(ev.Get("SubjectLogonId"))?.ToLowerInvariant() is { Length: <= 20 } logon ? logon : null,
            SecurityEventXml.TryParseHex(ev.Get("ProcessId"), out var pid) ? pid : null,
            Cap(Value(ev.Get("ProcessName")), 520),
            SecurityEventXml.TryParseHex(ev.Get("HandleId"), out var handle) ? handle : null,
            System.Net.IPAddress.TryParse(Value(ev.Get("IpAddress")), out var ip) ? ip.ToString() : null,
            path is null ? null : LocalPath.Normalize(path),
            shareName,
            shareRoot,
            relative,
            Mask(ev),
            oldSd,
            newSd);
    }

    /// <summary>Windows writes "-" for an empty field.</summary>
    private static string? Value(string? value) => value is null or "-" ? null : value;

    private static string? Cap(string? value, int max = MaxSecurityText) => value is null || value.Length <= max ? value : value[..max];

    /// <summary>"\\*\Finance" or "\\FS01\Finance" to "Finance".</summary>
    public static string? ShareName(string? value)
    {
        if (Value(value) is not { } name)
        {
            return null;
        }
        var last = name.TrimEnd('\\').LastIndexOf('\\');
        var result = last < 0 ? name : name[(last + 1)..].TrimEnd('\\');
        return result.Length is > 0 and <= 80 ? result : null;
    }
}
