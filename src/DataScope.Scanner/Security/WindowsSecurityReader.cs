using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DataScope.Core.Access;

namespace DataScope.Scanner.Security;

/// <summary>
/// Windows permission reads:
/// folders with GetFileSecurityW (needs only READ_CONTROL, which Read permission includes);
/// shares with NetShareGetInfo level 502 (Windows requires administrator rights for this);
/// local groups with NetLocalGroupEnum and NetLocalGroupGetMembers (restricted to administrators on Windows Server
/// 2016 and later unless the server's "Network access: Restrict clients allowed to make remote calls to SAM" policy
/// allows the scan account). Refusals are recorded, and access results then show their uncertainty.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsSecurityReader : ISecurityReader
{
    private const uint OwnerSecurityInformation = 0x1;
    private const uint GroupSecurityInformation = 0x2;
    private const uint DaclSecurityInformation = 0x4;
    private const int ErrorAccessDenied = 5;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorMoreData = 234;
    private const int NerrSuccess = 0;
    private const int NerrNetNameNotFound = 2310;
    private const uint MaxPreferredLength = 0xFFFFFFFF;
    private const uint InitialFolderBuffer = 4096;
    private const ushort SelfRelative = 0x8000;

    [StructLayout(LayoutKind.Sequential)]
    private struct ShareInfo502
    {
        public IntPtr NetName;
        public uint Type;
        public IntPtr Remark;
        public uint Permissions;
        public uint MaxUses;
        public uint CurrentUses;
        public IntPtr Path;
        public IntPtr Password;
        public uint Reserved;
        public IntPtr SecurityDescriptor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LocalGroupMembersInfo2
    {
        public IntPtr Sid;
        public int SidUsage;
        public IntPtr DomainAndName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "GetFileSecurityW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileSecurity(string fileName, uint requested, byte[]? descriptor, uint length, out uint needed);

    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NetShareGetInfo(string serverName, string netName, int level, out IntPtr buffer);

    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NetLocalGroupEnum(string serverName, int level, out IntPtr buffer, uint prefMaxLen, out int entriesRead, out int totalEntries, ref nuint resumeHandle);

    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NetLocalGroupGetMembers(string serverName, string localGroupName, int level, out IntPtr buffer, uint prefMaxLen, out int entriesRead, out int totalEntries, ref nuint resumeHandle);

    [LibraryImport("netapi32.dll")]
    private static partial int NetApiBufferFree(IntPtr buffer);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial uint GetSecurityDescriptorLength(IntPtr descriptor);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSecurityDescriptorControl(IntPtr descriptor, out ushort control, out uint revision);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MakeSelfRelativeSD(IntPtr absolute, byte[]? selfRelative, ref uint length);

    [LibraryImport("advapi32.dll", EntryPoint = "LookupAccountNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LookupAccountName(string systemName, string accountName, byte[]? sid, ref uint sidLength, char[]? domain, ref uint domainLength, out int use);

    public SecurityRead ReadFolder(string fullPath)
    {
        var path = @"\\?\" + (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal) ? fullPath[4..]
            : fullPath.StartsWith(@"\\", StringComparison.Ordinal) ? @"UNC\" + fullPath[2..]
            : fullPath);
        const uint what = OwnerSecurityInformation | GroupSecurityInformation | DaclSecurityInformation;
        // Start with a buffer that fits almost every descriptor rather than probing with an empty one: over SMB the
        // probe can fail with ERROR_MORE_DATA, and a successful call is not guaranteed to report the length it used.
        var size = InitialFolderBuffer;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buffer = new byte[size];
            if (GetFileSecurity(path, what, buffer, size, out var needed))
            {
                var length = SecurityDescriptorCodec.SelfRelativeLength(buffer);
                return length > 0 ? new SecurityRead(PermissionState.Read, buffer[..length]) : SecurityRead.Failed("Windows returned permissions that could not be interpreted.");
            }
            var error = Marshal.GetLastPInvokeError();
            if (error is not (ErrorInsufficientBuffer or ErrorMoreData))
            {
                return FolderError(error);
            }
            if (size >= SecurityDescriptorCodec.MaxBytes)
            {
                return SecurityRead.Failed("The folder's permissions are larger than Windows allows.");
            }
            size = Math.Min(needed > size ? needed : size * 4, (uint)SecurityDescriptorCodec.MaxBytes);
        }
        return SecurityRead.Failed("The folder's permissions kept growing while they were read.");
    }

    private static SecurityRead FolderError(int error) => error switch
    {
        ErrorAccessDenied => SecurityRead.Denied("The scan account may not read this folder's permissions."),
        2 or 3 => SecurityRead.Failed("The folder disappeared before its permissions were read."),
        _ => SecurityRead.Failed($"Reading permissions failed with Windows error {error}."),
    };

    public SecurityRead ReadShare(string hostname, string shareName)
    {
        var status = NetShareGetInfo(@"\\" + hostname, shareName, 502, out var buffer);
        try
        {
            if (status == ErrorAccessDenied)
            {
                return SecurityRead.Denied("Windows only shows share permissions to administrators of the file server.");
            }
            if (status != NerrSuccess)
            {
                return SecurityRead.Failed(status == NerrNetNameNotFound ? "The share was not found." : $"Reading share permissions failed with Windows error {status}.");
            }
            var info = Marshal.PtrToStructure<ShareInfo502>(buffer);
            if (info.SecurityDescriptor == IntPtr.Zero)
            {
                // No descriptor at all: Windows grants everyone full access to the share.
                return new SecurityRead(PermissionState.Read, SecurityDescriptorCodec.Build(new SecurityDescriptor(0, null, null, null)));
            }
            return new SecurityRead(PermissionState.Read, Copy(info.SecurityDescriptor));
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                _ = NetApiBufferFree(buffer);
            }
        }
    }

    private static byte[] Copy(IntPtr descriptor)
    {
        if (GetSecurityDescriptorControl(descriptor, out var control, out _) && (control & SelfRelative) == 0)
        {
            uint length = 0;
            _ = MakeSelfRelativeSD(descriptor, null, ref length);
            var converted = new byte[length];
            return MakeSelfRelativeSD(descriptor, converted, ref length) ? converted : [];
        }
        var size = GetSecurityDescriptorLength(descriptor);
        var bytes = new byte[Math.Min(size, (uint)SecurityDescriptorCodec.MaxBytes)];
        Marshal.Copy(descriptor, bytes, 0, bytes.Length);
        return bytes;
    }

    public LocalGroupsRead ReadLocalGroups(string hostname)
    {
        var server = @"\\" + hostname;
        var names = new List<string>();
        nuint resume = 0;
        int status;
        do
        {
            status = NetLocalGroupEnum(server, 0, out var buffer, MaxPreferredLength, out var read, out _, ref resume);
            try
            {
                if (status is not NerrSuccess and not ErrorMoreData)
                {
                    return Refused(status);
                }
                for (var i = 0; i < read; i++)
                {
                    names.Add(Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, i * IntPtr.Size)) ?? "");
                }
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                {
                    _ = NetApiBufferFree(buffer);
                }
            }
        }
        while (status == ErrorMoreData);

        var groups = new List<LocalGroup>();
        foreach (var name in names.Where(n => n.Length > 0))
        {
            var sid = LookupSid(hostname, name);
            if (sid is null)
            {
                continue;
            }
            var (members, complete, error) = ReadMembers(server, name);
            if (error is not null)
            {
                return Refused(error.Value);
            }
            groups.Add(new LocalGroup(sid, WellKnownSid.NameOf(sid) ?? $@"{hostname.ToUpperInvariant()}\{name}", members, !complete));
        }
        return new LocalGroupsRead(LocalGroupsState.Read, groups);
    }

    private static LocalGroupsRead Refused(int status) => status == ErrorAccessDenied
        ? new LocalGroupsRead(LocalGroupsState.AccessDenied, [], "Windows only lets administrators list the file server's local groups (Restrict remote SAM).")
        : new LocalGroupsRead(LocalGroupsState.Failed, [], $"Listing local groups failed with Windows error {status}.");

    private static (List<LocalMember> Members, bool Complete, int? Error) ReadMembers(string server, string group)
    {
        var members = new List<LocalMember>();
        var complete = true;
        nuint resume = 0;
        int status;
        do
        {
            status = NetLocalGroupGetMembers(server, group, 2, out var buffer, MaxPreferredLength, out var read, out _, ref resume);
            try
            {
                if (status is not NerrSuccess and not ErrorMoreData)
                {
                    return status == ErrorAccessDenied ? (members, false, status) : (members, false, null);
                }
                var size = Marshal.SizeOf<LocalGroupMembersInfo2>();
                for (var i = 0; i < read; i++)
                {
                    var info = Marshal.PtrToStructure<LocalGroupMembersInfo2>(buffer + (i * size));
                    var sid = ReadSid(info.Sid);
                    if (sid is null)
                    {
                        complete = false;
                        continue;
                    }
                    members.Add(new LocalMember(sid, info.SidUsage switch
                    {
                        1 or 9 => LocalMemberKind.User,
                        2 or 4 => LocalMemberKind.Group,
                        5 => LocalMemberKind.WellKnownGroup,
                        _ => LocalMemberKind.Unknown,
                    }));
                }
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                {
                    _ = NetApiBufferFree(buffer);
                }
            }
        }
        while (status == ErrorMoreData);
        return (members, complete, null);
    }

    private static string? ReadSid(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero)
        {
            return null;
        }
        var header = new byte[8];
        Marshal.Copy(pointer, header, 0, 8);
        if (header[1] > Sid.MaxSubAuthorities)
        {
            return null;
        }
        var bytes = new byte[8 + (4 * header[1])];
        Marshal.Copy(pointer, bytes, 0, bytes.Length);
        return Sid.TryRead(bytes, out var sid, out _) ? sid : null;
    }

    private static string? LookupSid(string hostname, string name)
    {
        uint sidLength = 0, domainLength = 0;
        _ = LookupAccountName(hostname, name, null, ref sidLength, null, ref domainLength, out _);
        if (sidLength is 0 or > 68 || domainLength > 1024)
        {
            return null;
        }
        var sid = new byte[sidLength];
        var domain = new char[domainLength];
        return LookupAccountName(hostname, name, sid, ref sidLength, domain, ref domainLength, out _) && Sid.TryRead(sid, out var text, out _) ? text : null;
    }
}
