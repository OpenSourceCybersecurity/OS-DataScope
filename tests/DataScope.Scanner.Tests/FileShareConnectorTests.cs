using DataScope.Core.Classification;
using DataScope.Core.Inventory;
using DataScope.Scanner.Connectors;
using DataScope.Scanner.Content;

namespace DataScope.Scanner.Tests;

/// <summary>The on-prem connector (D141) lists, walks and reads exactly what the scanner did before connectors.</summary>
public sealed class FileShareConnectorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ds-connector-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private FileShareConnector Connector() => new(new LabShareEnumerator(_root), new FileSystemDirectorySource());

    [Fact]
    public void It_is_the_file_server_connector_and_reads_windows_permissions()
    {
        var connector = Connector();

        Assert.Equal(SourceKind.FileServer, connector.Kind);
    }

    [Fact]
    public async Task Lists_shares_then_one_folder_at_a_time()
    {
        var hr = Path.Join(_root, "fs01", "HR");
        Directory.CreateDirectory(Path.Join(hr, "Payroll", "2024"));
        await File.WriteAllTextAsync(Path.Join(hr, "Payroll", "march.csv"), "synthetic");
        var connector = Connector();

        var shares = await connector.ListLocationsAsync("fs01", CancellationToken.None);
        var root = Assert.Single(shares).AccessPath;
        var top = await connector.ListFolderAsync(root, "", 100, CancellationToken.None);
        var payroll = await connector.ListFolderAsync(root, "Payroll", 100, CancellationToken.None);

        Assert.Equal(["Payroll"], top.Entries.Select(e => e.Name));
        Assert.Equal(["2024", "march.csv"], payroll.Entries.Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.Null(payroll.Gap);
    }

    [Fact]
    public async Task A_missing_folder_is_a_gap_not_an_exception()
    {
        Directory.CreateDirectory(Path.Join(_root, "fs01", "HR"));
        var connector = Connector();
        var root = (await connector.ListLocationsAsync("fs01", CancellationToken.None))[0].AccessPath;

        var listing = await connector.ListFolderAsync(root, @"Gone\Away", 100, CancellationToken.None);

        Assert.Empty(listing.Entries);
        Assert.Equal(GapKind.NotFound, listing.Gap);
    }

    [Fact]
    public async Task Reads_an_item_through_the_given_reader_at_its_full_path()
    {
        var reader = new RecordingReader();
        var root = Path.Join(_root, "fs01", "HR");

        var read = await Connector().ReadItemAsync(reader, root, @"Payroll\2024", "march.csv", CancellationToken.None);

        Assert.Equal(ContentState.Scanned, read.State);
        Assert.Equal(Path.Join(root, "Payroll", "2024", "march.csv"), Assert.Single(reader.Paths));
    }

    [Fact]
    public void Full_paths_join_with_the_platform_separator_and_the_root_stands_for_itself()
    {
        Assert.Equal("root", FileShareConnector.FullPath("root", ""));
        Assert.Equal(Path.Join("root", "a", "b.txt"), FileShareConnector.FullPath("root", @"a\b.txt"));
    }

    private sealed class RecordingReader : IContentReader
    {
        public List<string> Paths { get; } = [];

        public ContentExtraction? Precheck(string fileName, long size, FileAttributes attributes) => null;

        public ContentExtraction Read(string fullPath, CancellationToken ct)
        {
            Paths.Add(fullPath);
            return new ContentExtraction(ContentState.Scanned, "synthetic", null);
        }
    }
}
