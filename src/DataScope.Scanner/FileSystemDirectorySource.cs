using System.IO.Enumeration;
using DataScope.Core.Inventory;

namespace DataScope.Scanner;

/// <summary>
/// Lists folders through the OS directory APIs (FindFirstFile on Windows). It reads directory entries only,
/// never opens files, and never follows junctions or symbolic links.
/// </summary>
public sealed class FileSystemDirectorySource : IDirectorySource
{
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false,
        AttributesToSkip = 0,
        MatchType = MatchType.Simple,
    };

    public DirectoryListing List(string fullPath, int maxEntries, CancellationToken ct)
    {
        var entries = new List<FsEntry>();
        try
        {
            var enumerable = new FileSystemEnumerable<FsEntry>(fullPath, ToEntry, Options);
            foreach (var entry in enumerable)
            {
                ct.ThrowIfCancellationRequested();
                if (entries.Count >= maxEntries)
                {
                    return new DirectoryListing(entries, GapKind.FolderEntryLimit, $"Stopped after {maxEntries} entries.");
                }
                entries.Add(entry);
            }
            return new DirectoryListing(entries, null, null);
        }
        catch (UnauthorizedAccessException)
        {
            return Partial(entries, GapKind.AccessDenied, "Access denied to the scan identity.");
        }
        catch (DirectoryNotFoundException)
        {
            return Partial(entries, GapKind.NotFound, "Folder not found; it may have been moved or deleted during the scan.");
        }
        catch (PathTooLongException)
        {
            return Partial(entries, GapKind.PathTooLong, "Path is longer than the system allows.");
        }
        catch (IOException ex)
        {
            // HResult only: IOException messages can include full paths.
            return Partial(entries, GapKind.IoError, $"I/O error 0x{ex.HResult:X8}.");
        }
    }

    // Entries read before a failure are still real, but the folder is incomplete, so the gap stays.
    private static DirectoryListing Partial(List<FsEntry> entries, GapKind gap, string message) => new(entries, gap, message);

    private static FsEntry ToEntry(ref FileSystemEntry e)
    {
        var attributes = e.Attributes;
        return new FsEntry(
            e.FileName.ToString(),
            e.IsDirectory,
            (attributes & FileAttributes.ReparsePoint) != 0,
            e.IsDirectory ? 0 : e.Length,
            e.CreationTimeUtc,
            e.LastWriteTimeUtc,
            attributes);
    }
}
