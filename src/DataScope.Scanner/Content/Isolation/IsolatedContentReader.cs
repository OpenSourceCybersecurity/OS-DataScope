using System.Collections.Concurrent;
using System.Diagnostics;
using DataScope.Core.Classification;

namespace DataScope.Scanner.Content.Isolation;

/// <summary>
/// Reads files in separate, limited reader processes instead of inside the worker (D127). Parsers for Office files,
/// PDFs, zips and emails handle untrusted bytes; if one crashes, runs out of memory or hangs on a hostile or damaged
/// file, only its reader process is lost. The file is tried once more in a fresh process and then reported as
/// unreadable (or timed out), and the scan carries on. Checks that need no file access run here, without a process.
/// </summary>
public sealed class IsolatedContentReader : IContentReader, IDisposable
{
    /// <summary>Time a reader process gets beyond the per-file limit before it is stopped: it enforces that limit itself.</summary>
    internal TimeSpan Grace { get; init; } = TimeSpan.FromSeconds(15);

    internal static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// A reader process is replaced after this many files, so slow leaks or fragmentation never build up. Not much
    /// more often: a fresh process runs slower until its code has warmed up.
    /// </summary>
    internal const int RecycleAfter = 50_000;

    internal const int MaxNoteLength = 1_000;

    private readonly ContentLimits limits;
    private readonly Func<ProcessStartInfo> command;
    private readonly FileContentReader byName;
    private readonly ConcurrentBag<HostProcess> idle = [];
    private volatile bool disposed;

    /// <param name="command">How to start a reader process: see <see cref="ContentHostCommand"/>.</param>
    public IsolatedContentReader(ContentLimits limits, Func<ProcessStartInfo> command)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(command);
        limits.Validate();
        this.limits = limits;
        this.command = command;
        byName = new FileContentReader(limits);
    }

    private int started;

    /// <summary>Reader processes started so far, for tests.</summary>
    internal int Started => Volatile.Read(ref started);

    public ContentExtraction? Precheck(string fileName, long size, FileAttributes attributes) => byName.Precheck(fileName, size, attributes);

    public ContentExtraction Read(string fullPath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        ObjectDisposedException.ThrowIf(disposed, this);
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            HostProcess host;
            try
            {
                host = Take(ct);
            }
            catch (InvalidOperationException)
            {
                // Says nothing about the file, so it is tried again on the next scan.
                return ContentExtraction.Of(ContentState.IoError, "Data Scope's file reader could not start, so this file was not read. It is tried again on the next scan.");
            }

            switch (host.Exchange(new HostRequest(fullPath), limits.PerFileTimeout + Grace, ct, out var response))
            {
                case HostResult.Answered:
                    Return(host);
                    return Checked(response!);
                case HostResult.Cancelled:
                    host.Dispose();
                    throw new OperationCanceledException(ct);
                case HostResult.TimedOut:
                    host.Dispose();
                    return ContentExtraction.Of(ContentState.TimedOut,
                        $"Reading took longer than {limits.PerFileTimeout.TotalSeconds:N0} seconds, so the file reader was stopped.");
                default:
                    host.Dispose();
                    if (attempt < 2)
                    {
                        continue; // Once more in a fresh process, in case something other than this file was to blame.
                    }
                    return ContentExtraction.Of(ContentState.Unreadable,
                        "Reading this file stopped Data Scope's file reader twice (it crashed or ran out of memory). Files are read in a separate, limited process, so the rest of the scan carried on.");
            }
        }
    }

    public void Dispose()
    {
        disposed = true;
        while (idle.TryTake(out var host))
        {
            host.Dispose();
        }
    }

    private HostProcess Take(CancellationToken ct)
    {
        while (idle.TryTake(out var host))
        {
            if (host.IsAlive)
            {
                return host;
            }
            host.Dispose();
        }
        var fresh = HostProcess.Start(command(), limits, StartTimeout, ct);
        Interlocked.Increment(ref started);
        return fresh;
    }

    private void Return(HostProcess host)
    {
        if (disposed || !host.IsAlive || host.Reads >= RecycleAfter)
        {
            host.Dispose();
            return;
        }
        idle.Add(host);
    }

    /// <summary>The reader process is treated like any other untrusted input: what it says is checked before it is used.</summary>
    private ContentExtraction Checked(HostResponse response)
    {
        // NotRead is allowed: it says text recognition can't run in that process, so the file is read on a later scan.
        if (!Enum.IsDefined(response.State) || response.State is ContentState.Excluded
            || (response.Text is { } text && text.Length > limits.MaxCharsPerFile))
        {
            return ContentExtraction.Of(ContentState.Unreadable, "Data Scope's file reader gave an answer it should not have, so the file is treated as unreadable.");
        }
        var note = response.Note is { Length: > MaxNoteLength } longNote ? longNote[..MaxNoteLength] : response.Note;
        return new ContentExtraction(response.State, response.Text, note);
    }
}
