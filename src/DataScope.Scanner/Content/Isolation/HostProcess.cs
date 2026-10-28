using System.ComponentModel;
using System.Diagnostics;

namespace DataScope.Scanner.Content.Isolation;

/// <summary>How one exchange with a reader process ended.</summary>
internal enum HostResult
{
    Answered,

    /// <summary>No answer by the deadline: the process was stopped.</summary>
    TimedOut,

    /// <summary>The process stopped or sent something unreadable: it crashed, ran out of memory or misbehaved.</summary>
    Stopped,

    /// <summary>The scan was cancelled or paused: the process was stopped.</summary>
    Cancelled,
}

/// <summary>One running reader process (D127), started and spoken to only by <see cref="IsolatedContentReader"/>.</summary>
internal sealed class HostProcess : IDisposable
{
    /// <summary>Memory the runtime itself needs beyond the managed heap: code, stacks and native buffers.</summary>
    internal const long NativeAllowanceBytes = 512L * 1024 * 1024;

    private readonly Process process;
    private readonly WindowsJob? job;
    private readonly Stream input;
    private readonly Stream output;
    private readonly int maxResponseBytes;
    private volatile bool killed;

    private HostProcess(Process process, WindowsJob? job, ContentLimits limits)
    {
        this.process = process;
        this.job = job;
        input = process.StandardInput.BaseStream;
        output = process.StandardOutput.BaseStream;
        maxResponseBytes = HostProtocol.MaxResponseBytes(limits);
    }

    /// <summary>Files this process has read.</summary>
    public int Reads { get; private set; }

    /// <summary>
    /// False once this side stopped the process or saw it stop. Checked without asking the system, which is slow; a
    /// process that died while idle is noticed by the next exchange, and the file is tried again in a fresh one.
    /// </summary>
    public bool IsAlive => !killed;

    /// <summary>Starts a reader process, gives it the limits and waits until it answers.</summary>
    /// <exception cref="InvalidOperationException">It could not start or did not answer in time.</exception>
    /// <exception cref="OperationCanceledException">The scan was paused or stopped while it started; it was killed.</exception>
    public static HostProcess Start(ProcessStartInfo command, ContentLimits limits, TimeSpan startTimeout, CancellationToken ct)
    {
        command.UseShellExecute = false;
        command.RedirectStandardInput = true;
        command.RedirectStandardOutput = true;
        command.RedirectStandardError = true;
        command.CreateNoWindow = true;
        // The managed heap is capped on every platform; on Windows the job object also caps everything else.
        command.Environment["DOTNET_GCHeapHardLimit"] = "0x" + limits.ReaderMemoryBytes.ToString("X", System.Globalization.CultureInfo.InvariantCulture);

        Process process;
        try
        {
            process = Process.Start(command) ?? throw new InvalidOperationException("The file reader did not start.");
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException("The file reader did not start.", ex);
        }
        WindowsJob? job = null;
        HostProcess? host = null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Assigned before the limits are sent: until then the process only waits, and reads no file.
                job = WindowsJob.Create(limits.ReaderMemoryBytes + NativeAllowanceBytes);
                job.Assign(process);
            }
            process.ErrorDataReceived += static (_, _) => { }; // Drained so it can never fill up and block the process.
            process.BeginErrorReadLine();
            host = new HostProcess(process, job, limits);
            HostProtocol.Write(host.input, limits);
            var answer = host.Exchange(new HostRequest(null), startTimeout, ct, out _);
            if (answer == HostResult.Cancelled)
            {
                // A pause doesn't wait for a slow start: the process is killed now.
                host.Dispose();
                throw new OperationCanceledException(ct);
            }
            if (answer != HostResult.Answered)
            {
                throw new InvalidOperationException("The file reader did not answer.");
            }
            host.Reads = 0;
            return host;
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException)
        {
            if (host is not null)
            {
                host.Dispose();
            }
            else
            {
                Kill(process);
                process.Dispose();
                job?.Dispose();
            }
            throw ex as InvalidOperationException ?? new InvalidOperationException("The file reader did not start.", ex);
        }
    }

    /// <summary>
    /// Sends one request and waits for the answer until <paramref name="deadline"/>. A process that doesn't answer in
    /// time, or that the scan stops waiting for, is killed, which also ends the wait.
    /// </summary>
    public HostResult Exchange(HostRequest request, TimeSpan deadline, CancellationToken ct, out HostResponse? response)
    {
        response = null;
        if (!IsAlive)
        {
            return HostResult.Stopped;
        }
        var timedOut = false;
        using var timer = new CancellationTokenSource(deadline);
        using var onTimeout = timer.Token.Register(() =>
        {
            timedOut = true;
            Stop();
        });
        using var onCancel = ct.Register(Stop);
        try
        {
            HostProtocol.Write(input, request);
            response = HostProtocol.Read<HostResponse>(output, maxResponseBytes);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException)
        {
            response = null;
        }
        if (ct.IsCancellationRequested)
        {
            Stop();
            return HostResult.Cancelled;
        }
        if (timedOut)
        {
            response = null;
            return HostResult.TimedOut;
        }
        if (response is null)
        {
            Stop();
            return HostResult.Stopped;
        }
        Reads++;
        return HostResult.Answered;
    }

    public void Dispose()
    {
        Stop();
        process.Dispose();
        job?.Dispose();
    }

    private void Stop()
    {
        killed = true;
        Kill(process);
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone.
        }
    }
}
