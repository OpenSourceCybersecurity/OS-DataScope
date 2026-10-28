using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DataScope.Core.Inventory;

namespace DataScope.Scanner;

/// <summary>
/// Lists shares with NetShareEnum at level 1 (name, type, remark), which any authenticated domain user may call.
/// It does not need local admin on the file server and changes nothing.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsShareEnumerator : IShareEnumerator
{
    private const int NerrSuccess = 0;
    private const int ErrorMoreData = 234;
    private const int ErrorAccessDenied = 5;
    private const int ErrorBadNetPath = 53;
    private const uint MaxPreferredLength = 0xFFFFFFFF;
    private const uint StypeMask = 0xFF;
    private const uint StypeDiskTree = 0;
    private const uint StypeSpecial = 0x80000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct ShareInfo1
    {
        public IntPtr NetName;
        public uint Type;
        public IntPtr Remark;
    }

    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NetShareEnum(string serverName, int level, out IntPtr buffer, uint prefMaxLen, out int entriesRead, out int totalEntries, ref int resumeHandle);

    [LibraryImport("netapi32.dll")]
    private static partial int NetApiBufferFree(IntPtr buffer);

    public Task<IReadOnlyList<ShareInfo>> ListAsync(string hostname, CancellationToken ct) =>
        Task.Run<IReadOnlyList<ShareInfo>>(() => List(hostname), ct);

    private static List<ShareInfo> List(string hostname)
    {
        var result = new List<ShareInfo>();
        var resume = 0;
        int status;
        do
        {
            status = NetShareEnum(@"\\" + hostname, 1, out var buffer, MaxPreferredLength, out var read, out _, ref resume);
            if (status is not NerrSuccess and not ErrorMoreData)
            {
                throw new ShareEnumerationException(status switch
                {
                    ErrorAccessDenied => $"Access denied listing shares on {hostname}.",
                    ErrorBadNetPath => $"{hostname} could not be reached over SMB.",
                    _ => $"Listing shares on {hostname} failed with Windows error {status}.",
                });
            }
            try
            {
                var size = Marshal.SizeOf<ShareInfo1>();
                for (var i = 0; i < read; i++)
                {
                    var info = Marshal.PtrToStructure<ShareInfo1>(buffer + (i * size));
                    var name = Marshal.PtrToStringUni(info.NetName) ?? "";
                    if (!ShareName.IsValid(name))
                    {
                        continue;
                    }
                    var kind = (info.Type & StypeSpecial) != 0 || name.EndsWith('$')
                        ? ShareKind.Special
                        : (info.Type & StypeMask) == StypeDiskTree ? ShareKind.Disk : ShareKind.Other;
                    result.Add(new ShareInfo(name, ShareName.Unc(hostname, name), Marshal.PtrToStringUni(info.Remark), kind));
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
        return result;
    }
}
