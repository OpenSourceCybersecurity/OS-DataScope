namespace DataScope.Core.Activity;

/// <summary>
/// Paths on a file server's own disks as audit events write them (C:\Shares\Finance\a.xlsx, \??\C:\Shares\Finance).
/// Comparisons ignore case, as Windows does.
/// </summary>
public static class LocalPath
{
    /// <summary>
    /// Removes the NT prefixes (\??\, \\?\, \\.\), uses backslashes, and drops a trailing backslash except after a
    /// drive. Device paths (\Device\HarddiskVolume3\...) are kept as they are; they match a share only if its
    /// local path is written the same way.
    /// </summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "-")
        {
            return null;
        }
        var p = path.Trim().Replace('/', '\\');
        foreach (var prefix in (string[])[@"\??\", @"\\?\", @"\\.\"])
        {
            if (p.StartsWith(prefix, StringComparison.Ordinal))
            {
                p = p[prefix.Length..];
                if (p.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase))
                {
                    p = @"\\" + p[4..];
                }
                break;
            }
        }
        while (p.Length > 1 && p.EndsWith('\\') && !IsDriveRoot(p))
        {
            p = p[..^1];
        }
        return p.Length == 0 ? null : p;
    }

    private static bool IsDriveRoot(string p) => p.Length == 3 && p[1] == ':' && p[2] == '\\';

    /// <summary>A share-relative path without leading or trailing backslashes; empty for the share itself.</summary>
    public static string TrimRelative(string relative) => relative.Replace('/', '\\').Trim('\\');

    public static string Combine(string root, string relative) =>
        relative.Length == 0 ? root : root.TrimEnd('\\') + "\\" + relative;

    /// <summary>The path below <paramref name="root"/>, empty for the root itself, or null when it is not inside it.</summary>
    public static string? RelativeTo(string root, string path)
    {
        var r = root.TrimEnd('\\');
        if (path.Length == r.Length || (path.Length == r.Length + 1 && path.EndsWith('\\')))
        {
            return string.Equals(path.TrimEnd('\\'), r, StringComparison.OrdinalIgnoreCase) ? "" : null;
        }
        return path.Length > r.Length + 1
            && path[r.Length] == '\\'
            && path.StartsWith(r, StringComparison.OrdinalIgnoreCase)
            ? TrimRelative(path[(r.Length + 1)..])
            : null;
    }
}

/// <summary>Finds which share a local path is in: the share whose folder is the longest match.</summary>
public sealed class SharePathMap
{
    private readonly List<(Guid ShareId, string Root)> _roots;

    public SharePathMap(IEnumerable<(Guid ShareId, string LocalPath)> shares)
    {
        _roots = shares
            .Select(s => (s.ShareId, Root: LocalPath.Normalize(s.LocalPath)))
            .Where(s => s.Root is not null)
            .Select(s => (s.ShareId, s.Root!))
            .OrderByDescending(s => s.Item2.Length)
            .ToList();
    }

    public int Count => _roots.Count;

    public (Guid ShareId, string RelativePath)? Match(string? path)
    {
        if (LocalPath.Normalize(path) is not { } p)
        {
            return null;
        }
        foreach (var (shareId, root) in _roots)
        {
            if (LocalPath.RelativeTo(root, p) is { } relative)
            {
                return (shareId, relative);
            }
        }
        return null;
    }
}
