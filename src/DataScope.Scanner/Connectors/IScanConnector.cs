using DataScope.Core.Inventory;
using DataScope.Scanner.Content;

namespace DataScope.Scanner.Connectors;

/// <summary>
/// Where one source's items live (D141): a file server's shares, folders and files. A connector lists locations, lists one folder at a time and reads one item through the
/// bounded content readers; the walk, gaps, limits, classification and findings around it are the same for every
/// source. Implementations must be read-only: nothing at the source is created, changed, moved or deleted.
/// </summary>
public interface IScanConnector
{
    /// <summary>The kind of server this connector scans.</summary>
    SourceKind Kind { get; }

    /// <summary>The source's top-level locations (shares). Throws <see cref="ShareEnumerationException"/> when the whole source is unreachable.</summary>
    Task<IReadOnlyList<ShareInfo>> ListLocationsAsync(string source, CancellationToken ct);

    /// <summary>Lists one folder without recursing. Failures come back as a gap on the listing, never as an exception.</summary>
    /// <param name="root">The location's <see cref="ShareInfo.AccessPath"/>.</param>
    /// <param name="folderPath">Location-relative path with backslashes; empty for the root.</param>
    Task<DirectoryListing> ListFolderAsync(string root, string folderPath, int maxEntries, CancellationToken ct);

    /// <summary>Reads one item's contents through <paramref name="reader"/>, which enforces the size, time and depth limits.</summary>
    Task<ContentExtraction> ReadItemAsync(IContentReader reader, string root, string folderPath, string name, CancellationToken ct);
}
