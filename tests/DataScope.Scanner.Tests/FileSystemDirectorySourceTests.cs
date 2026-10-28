using DataScope.Core.Inventory;

namespace DataScope.Scanner.Tests;

public sealed class FileSystemDirectorySourceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ds-fs-").FullName;
    private readonly FileSystemDirectorySource _source = new();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Lists_files_and_folders_with_metadata_without_recursing()
    {
        Directory.CreateDirectory(Path.Join(_root, "HR", "Payroll"));
        File.WriteAllText(Path.Join(_root, "readme.txt"), "hello");
        var modified = new DateTime(2024, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Join(_root, "readme.txt"), modified);

        var listing = _source.List(_root, 100, CancellationToken.None);

        Assert.Null(listing.Gap);
        Assert.Equal(["HR", "readme.txt"], listing.Entries.Select(e => e.Name).Order(StringComparer.Ordinal));
        var file = listing.Entries.Single(e => e.Name == "readme.txt");
        Assert.False(file.IsDirectory);
        Assert.Equal(5, file.Length);
        Assert.Equal(modified, file.ModifiedUtc.UtcDateTime);
        Assert.True(listing.Entries.Single(e => e.Name == "HR").IsDirectory);
    }

    [Fact]
    public void Symbolic_links_to_folders_are_flagged_as_reparse_points()
    {
        Directory.CreateDirectory(Path.Join(_root, "real"));
        Directory.CreateSymbolicLink(Path.Join(_root, "loop"), _root);

        var listing = _source.List(_root, 100, CancellationToken.None);

        var link = listing.Entries.Single(e => e.Name == "loop");
        Assert.True(link.IsReparsePoint);
        Assert.False(listing.Entries.Single(e => e.Name == "real").IsReparsePoint);
    }

    [Fact]
    public void Missing_folder_is_a_not_found_gap()
    {
        var listing = _source.List(Path.Join(_root, "gone"), 100, CancellationToken.None);
        Assert.Equal(GapKind.NotFound, listing.Gap);
        Assert.Empty(listing.Entries);
    }

    [Fact]
    public void Stops_at_the_per_folder_entry_limit_and_reports_it()
    {
        for (var i = 0; i < 10; i++)
        {
            File.WriteAllText(Path.Join(_root, $"f{i}.txt"), "");
        }
        var listing = _source.List(_root, 4, CancellationToken.None);
        Assert.Equal(4, listing.Entries.Count);
        Assert.Equal(GapKind.FolderEntryLimit, listing.Gap);
    }

    [Fact]
    public void Listing_does_not_change_files()
    {
        var path = Path.Join(_root, "keep.txt");
        File.WriteAllText(path, "original");
        var before = (File.GetLastWriteTimeUtc(path), File.GetCreationTimeUtc(path), File.ReadAllText(path));

        _source.List(_root, 100, CancellationToken.None);

        Assert.Equal(before, (File.GetLastWriteTimeUtc(path), File.GetCreationTimeUtc(path), File.ReadAllText(path)));
    }

    [Fact]
    public void Honours_cancellation()
    {
        File.WriteAllText(Path.Join(_root, "a.txt"), "");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => _source.List(_root, 100, cts.Token));
    }
}
