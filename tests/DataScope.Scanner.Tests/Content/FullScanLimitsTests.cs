using DataScope.Core.Classification;
using DataScope.Scanner.Content;

namespace DataScope.Scanner.Tests.Content;

/// <summary>The limits full scans read with (D130): the same protections, with more room per file.</summary>
public sealed class FullScanLimitsTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("ds-full-").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    [Fact]
    public void Full_scans_get_more_time_bigger_files_and_more_pages()
    {
        var quick = new ContentLimits();
        var full = quick.ForFullScan();
        full.Validate();
        Assert.Equal(TimeSpan.FromMinutes(3), full.PerFileTimeout);
        Assert.Equal(TimeSpan.FromMinutes(2), full.OcrAllowed);
        Assert.True(full.MaxPdfPages > quick.MaxPdfPages);
        Assert.True(full.MaxPdfBytes > quick.MaxPdfBytes);
        Assert.True(full.MaxOfficeBytes > quick.MaxOfficeBytes);
        Assert.True(full.MaxArchiveBytes > quick.MaxArchiveBytes);
        Assert.True(full.MaxArchiveEntries > quick.MaxArchiveEntries);
        Assert.True(full.MaxUncompressedBytes > quick.MaxUncompressedBytes);
        Assert.True(full.MaxEmailBytes > quick.MaxEmailBytes);
        Assert.True(full.ReaderMemoryBytes > quick.ReaderMemoryBytes);
    }

    [Fact]
    public void Full_scans_keep_the_protections_that_are_not_about_depth()
    {
        var quick = new ContentLimits { MaxChars = 50_000, MaxImagePixels = 1_000_000, MaxNestingDepth = 2, ReadTextInImages = false, MaxImageBytes = 1024 };
        var full = quick.ForFullScan();
        Assert.Equal(50_000, full.MaxChars);
        Assert.Equal(1_000_000, full.MaxImagePixels);
        Assert.Equal(2, full.MaxNestingDepth);
        Assert.False(full.ReadTextInImages);
        Assert.Equal(1024, full.MaxImageBytes);
    }

    [Fact]
    public void Raising_a_quick_scan_limit_never_makes_full_scans_shallower()
    {
        var quick = new ContentLimits { PerFileTimeout = TimeSpan.FromMinutes(5), OcrTimeout = TimeSpan.FromMinutes(4), MaxPdfPages = 5_000, MaxArchiveBytes = 4L << 30 };
        var full = quick.ForFullScan();
        Assert.Equal(TimeSpan.FromMinutes(5), full.PerFileTimeout);
        Assert.Equal(TimeSpan.FromMinutes(4), full.OcrTimeout);
        Assert.Equal(5_000, full.MaxPdfPages);
        Assert.Equal(4L << 30, full.MaxArchiveBytes);
    }

    [Fact]
    public void A_pdf_a_quick_scan_reads_only_partly_is_read_in_full()
    {
        var path = Path.Combine(dir, "long.pdf");
        File.WriteAllBytes(path, Fixtures.Pdf("first page", "second page", "SSN 512-48-3307 on the third page"));
        var quick = new ContentLimits { MaxPdfPages = 2 };

        var partly = new FileContentReader(quick).Read(path, CancellationToken.None);
        Assert.Equal(ContentState.Partial, partly.State);
        Assert.DoesNotContain("512-48-3307", partly.Text, StringComparison.Ordinal);

        var whole = new FileContentReader(quick.ForFullScan()).Read(path, CancellationToken.None);
        Assert.Equal(ContentState.Scanned, whole.State);
        Assert.Contains("512-48-3307", whole.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Full_scan_limits_that_are_out_of_range_are_refused()
    {
        var full = new ContentLimits().ForFullScan();
        full.PerFileTimeout = TimeSpan.FromMinutes(11);
        Assert.Throws<ArgumentOutOfRangeException>(full.Validate);
    }

    [Fact]
    public void The_full_scan_reader_stops_the_reader_it_holds()
    {
        var inner = new Disposable();
        new FullScanContentReader(inner).Dispose();
        Assert.True(inner.Disposed);
    }

    private sealed class Disposable : IContentReader, IDisposable
    {
        public bool Disposed { get; private set; }

        public ContentExtraction? Precheck(string fileName, long size, FileAttributes attributes) => null;

        public ContentExtraction Read(string fullPath, CancellationToken ct) => throw new NotSupportedException();

        public void Dispose() => Disposed = true;
    }
}
