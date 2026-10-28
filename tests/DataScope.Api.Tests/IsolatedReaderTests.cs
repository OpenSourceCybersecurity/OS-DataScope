using System.Diagnostics;
using DataScope.Core.Classification;
using DataScope.Scanner.Content;
using DataScope.Scanner.Content.Isolation;
using DataScope.Worker;

namespace DataScope.Api.Tests;

/// <summary>
/// Files read in real, separate reader processes (D127): the worker started as <c>content-host</c>. A file that
/// crashes, hangs or runs a reader out of memory costs only that process. Every file is synthetic. These need no
/// database, so they run on Windows too, where the reader is also held in a job object.
/// </summary>
public sealed class IsolatedReaderTests : IDisposable
{
    private const string Ssn = "512-48-3307";
    private static readonly string WorkerAssembly = typeof(InventoryScanExecutor).Assembly.Location;
    private readonly string dir = Directory.CreateTempSubdirectory("ds-isolated-").FullName;
    private readonly List<IsolatedContentReader> readers = [];

    public void Dispose()
    {
        readers.ForEach(r => r.Dispose());
        Directory.Delete(dir, recursive: true);
    }

    private string Save(string name, string text)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    private IsolatedContentReader Reader(ContentLimits? limits = null, string? crashOn = null, string? hangOn = null, bool hangAtStart = false, Func<ProcessStartInfo>? command = null)
    {
        var reader = new IsolatedContentReader(limits ?? new ContentLimits(), command ?? (() =>
        {
            var start = ContentHostCommand.For(WorkerAssembly);
            if (crashOn is not null)
            {
                start.Environment[ContentHost.CrashOnVariable] = crashOn;
            }
            if (hangOn is not null)
            {
                start.Environment[ContentHost.HangOnVariable] = hangOn;
            }
            if (hangAtStart)
            {
                start.Environment[ContentHost.HangAtStartVariable] = "1";
            }
            return start;
        }))
        { Grace = TimeSpan.FromSeconds(1) };
        readers.Add(reader);
        return reader;
    }

    [Fact]
    public void Reads_files_in_one_reader_process_reused_between_files()
    {
        var reader = Reader();
        var first = reader.Read(Save("staff.dat", $"Name,SSN\nAvery,{Ssn}\n"), CancellationToken.None);
        var second = reader.Read(Save("welcome.txt", "Welcome."), CancellationToken.None);
        Assert.Equal(ContentState.Scanned, first.State);
        Assert.Contains(Ssn, first.Text, StringComparison.Ordinal);
        Assert.Equal("Welcome.", second.Text);
        Assert.Equal(1, reader.Started);
    }

    [Fact]
    public void Reads_the_text_in_an_image_in_the_reader_process()
    {
        // The reader process finds the bundled text recognition libraries and model next to the worker (D129).
        var path = Path.Combine(dir, "new-hire.png");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "form-scan.png"), path);
        var r = Reader().Read(path, CancellationToken.None);
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Contains(Ssn, r.Text, StringComparison.Ordinal);
        Assert.StartsWith("Text was read from the image by OCR", r.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void An_image_not_read_for_now_comes_back_as_not_read()
    {
        // NotRead is the one state a reader process gives for a file it may read later; it is passed on, not refused.
        var path = Path.Combine(dir, "new-hire.png");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "form-scan.png"), path);
        var r = Reader(new ContentLimits { ReadTextInImages = false }).Read(path, CancellationToken.None);
        Assert.Equal(ContentState.NotRead, r.State);
        Assert.StartsWith("Reading text in images is turned off", r.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Checks_that_need_no_file_access_start_no_process()
    {
        var reader = Reader();
        Assert.Equal(ContentState.Unsupported, reader.Precheck("setup.exe", 10, FileAttributes.Archive)?.State);
        Assert.Null(reader.Precheck("staff.csv", 10, FileAttributes.Archive));
        Assert.Equal(0, reader.Started);
    }

    [Fact]
    public void A_file_that_crashes_the_reader_is_tried_once_more_then_unreadable_and_the_next_file_is_read()
    {
        var reader = Reader(crashOn: "poison.docx");
        var poison = reader.Read(Save("poison.docx", "anything"), CancellationToken.None);
        Assert.Equal(ContentState.Unreadable, poison.State);
        Assert.Contains("stopped Data Scope's file reader twice", poison.Note, StringComparison.Ordinal);

        var next = reader.Read(Save("staff.csv", $"SSN {Ssn}"), CancellationToken.None);
        Assert.Equal(ContentState.Scanned, next.State);
        Assert.Equal(3, reader.Started); // The first, the one that tried the file again, and a fresh one after.
    }

    [Fact]
    public void A_file_that_hangs_the_reader_times_out_and_the_reader_is_replaced()
    {
        var reader = Reader(new ContentLimits { PerFileTimeout = TimeSpan.FromSeconds(1) }, hangOn: "stuck.pdf");
        var clock = Stopwatch.StartNew();
        var stuck = reader.Read(Save("stuck.pdf", "%PDF-1.7"), CancellationToken.None);
        Assert.Equal(ContentState.TimedOut, stuck.State);
        Assert.Contains("file reader was stopped", stuck.Note, StringComparison.Ordinal);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"Took {clock.Elapsed}.");

        Assert.Equal(ContentState.Scanned, reader.Read(Save("ok.txt", "fine"), CancellationToken.None).State);
        Assert.Equal(2, reader.Started);
    }

    [Fact]
    public void Pausing_a_scan_stops_the_reader_at_once()
    {
        var reader = Reader(hangOn: "stuck.xlsx");
        // A reader that is already running, as during a scan; pausing one that is still starting is the next test.
        // Starting a .NET process is the slow part on a busy machine, so it is kept out of the time measured here.
        Assert.Equal(ContentState.Scanned, reader.Read(Save("ok.txt", "fine"), CancellationToken.None).State);
        var path = Save("stuck.xlsx", "x");
        using var pause = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var clock = Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(() => reader.Read(path, pause.Token));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"Took {clock.Elapsed}.");
        Assert.Equal(1, reader.Started); // The running reader was used, then stopped.
    }

    [Fact]
    public void Pausing_while_a_reader_is_still_starting_stops_it_at_once()
    {
        // A real reader that never gives its start-up answer, as a reader that is slow to start would (in the same
        // Windows job object as in production, which allows no other process).
        var reader = Reader(hangAtStart: true);
        using var pause = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var clock = Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(() => reader.Read(Save("staff.csv", $"SSN {Ssn}"), pause.Token));
        // Without the pause it would wait the full minute the reader is given to start.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"Took {clock.Elapsed}.");
        Assert.Equal(0, reader.Started);
    }

    [Fact]
    public void A_file_that_needs_more_memory_than_allowed_is_unreadable_and_the_scan_carries_on()
    {
        // 30 MB of text with a 10 million character limit needs well over 64 MB to decode.
        var limits = new ContentLimits { MaxChars = 10_000_000, ReaderMemoryBytes = 64L * 1024 * 1024 };
        var reader = Reader(limits);
        var big = Path.Combine(dir, "export.csv");
        File.WriteAllText(big, string.Concat(Enumerable.Repeat("Avery Okafor,555-0100,Some Street,Some Town\n", 700_000)));

        var r = reader.Read(big, CancellationToken.None);
        Assert.Equal(ContentState.Unreadable, r.State);
        Assert.Equal(ContentState.Scanned, reader.Read(Save("small.txt", "fine"), CancellationToken.None).State);
    }

    [Fact]
    public void A_reader_that_cannot_start_leaves_the_file_to_the_next_scan()
    {
        var reader = Reader(command: () => new ProcessStartInfo(Path.Combine(dir, "no-such-program")));
        var r = reader.Read(Save("staff.csv", Ssn), CancellationToken.None);
        Assert.Equal(ContentState.IoError, r.State);
        Assert.Contains("could not start", r.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_program_that_does_not_speak_the_protocol_is_not_trusted()
    {
        // Prints its version and exits: no answer to the start check, so it is never sent a file.
        var reader = Reader(command: () =>
        {
            var start = ContentHostCommand.For(WorkerAssembly);
            var dotnet = new ProcessStartInfo(start.FileName);
            dotnet.ArgumentList.Add("--version");
            return dotnet;
        });
        Assert.Equal(ContentState.IoError, reader.Read(Save("staff.csv", Ssn), CancellationToken.None).State);
    }

    [Fact]
    public void Reading_after_dispose_is_refused()
    {
        var reader = Reader();
        reader.Dispose();
        Assert.Throws<ObjectDisposedException>(() => reader.Read(Save("a.txt", "a"), CancellationToken.None));
    }

    [Fact]
    public void The_reader_process_reads_by_contents_too()
    {
        var r = Reader().Read(Save("holiday.jpg", $"Name,SSN\nAvery,{Ssn}\n"), CancellationToken.None);
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Contains(Ssn, r.Text, StringComparison.Ordinal);
    }
}
