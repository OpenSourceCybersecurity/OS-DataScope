using DataScope.Core.Inventory;

namespace DataScope.Scanner;

/// <summary>One child of a listed folder. Metadata only: the file itself is never opened.</summary>
public sealed record FsEntry(
    string Name,
    bool IsDirectory,
    bool IsReparsePoint,
    long Length,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ModifiedUtc,
    FileAttributes Attributes);

/// <summary>Result of listing one folder: its children, and a gap if listing failed or stopped early.</summary>
public sealed record DirectoryListing(IReadOnlyList<FsEntry> Entries, GapKind? Gap, string? GapMessage)
{
    public static DirectoryListing Failed(GapKind gap, string message) => new([], gap, message);
}

/// <summary>Lists a single folder without recursing. Implementations must not modify anything.</summary>
public interface IDirectorySource
{
    DirectoryListing List(string fullPath, int maxEntries, CancellationToken ct);
}
