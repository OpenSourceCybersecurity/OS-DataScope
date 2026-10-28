using DataScope.Core.Inventory;
using DataScope.Scanner.Content;

namespace DataScope.Scanner.Connectors;

/// <summary>
/// The on-prem connector (D141): Windows file shares listed with <see cref="IShareEnumerator"/> and walked folder by
/// folder with <see cref="IDirectorySource"/>, never following junctions or symbolic links. It makes no network
/// connection other than SMB to the file server it scans.
/// </summary>
public sealed class FileShareConnector(IShareEnumerator shares, IDirectorySource directories) : IScanConnector
{
    public SourceKind Kind => SourceKind.FileServer;

    public Task<IReadOnlyList<ShareInfo>> ListLocationsAsync(string source, CancellationToken ct) => shares.ListAsync(source, ct);

    public Task<DirectoryListing> ListFolderAsync(string root, string folderPath, int maxEntries, CancellationToken ct) =>
        Task.FromResult(directories.List(FullPath(root, folderPath), maxEntries, ct));

    public Task<ContentExtraction> ReadItemAsync(IContentReader reader, string root, string folderPath, string name, CancellationToken ct) =>
        Task.FromResult(reader.Read(FullPath(root, PathText.Child(folderPath, name)), ct));

    /// <summary>The path the OS opens: the share's access path joined with a share-relative path.</summary>
    public static string FullPath(string root, string relative) =>
        relative.Length == 0 ? root : Path.Join(root, relative.Replace('\\', Path.DirectorySeparatorChar));
}
