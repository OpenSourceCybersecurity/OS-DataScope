using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace DataScope.Scanner.Content.Isolation;

/// <summary>
/// A Windows job object holding one reader process (D127): it may commit no more than a set amount of memory, may not
/// start other processes, dies on an unhandled error without showing a dialog, and is killed when the worker closes
/// the job, including when the worker itself stops unexpectedly.
/// </summary>
internal sealed partial class WindowsJob : IDisposable
{
    private const uint LimitActiveProcess = 0x0000_0008;
    private const uint LimitProcessMemory = 0x0000_0100;
    private const uint LimitDieOnUnhandledException = 0x0000_0400;
    private const uint LimitKillOnJobClose = 0x0000_2000;
    private const int ExtendedLimitInformation = 9;

    private readonly SafeJobHandle handle;

    private WindowsJob(SafeJobHandle handle) => this.handle = handle;

    [SupportedOSPlatform("windows")]
    public static WindowsJob Create(long memoryBytes)
    {
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        var info = new ExtendedLimits
        {
            Basic = new BasicLimits
            {
                LimitFlags = LimitActiveProcess | LimitProcessMemory | LimitDieOnUnhandledException | LimitKillOnJobClose,
                ActiveProcessLimit = 1,
            },
            ProcessMemoryLimit = (nuint)memoryBytes,
        };
        if (!SetInformationJobObject(handle, ExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<ExtendedLimits>()))
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error);
        }
        return new WindowsJob(handle);
    }

    [SupportedOSPlatform("windows")]
    public void Assign(Process process)
    {
        if (!AssignProcessToJobObject(handle, process.SafeHandle))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    public void Dispose() => handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    private sealed class SafeJobHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeJobHandle CreateJobObject(IntPtr attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(SafeJobHandle job, int infoClass, ref ExtendedLimits info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
