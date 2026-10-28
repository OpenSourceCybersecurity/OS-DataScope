namespace DataScope.Core.Inventory;

/// <summary>What a scan exclusion names: a folder on one share, or a file type.</summary>
public enum ScanExclusionKind
{
    Folder,
    FileType,
}

/// <summary>What a scan leaves out for an exclusion (D124).</summary>
public enum ScanExclusionEffect
{
    /// <summary>The folder is not listed or read: nothing under it is inventoried, checked for permissions or read.</summary>
    Skip,

    /// <summary>Files are listed in the inventory with their permissions, but their contents are not read.</summary>
    ListOnly,
}

/// <summary>One enabled exclusion as a scan applies it.</summary>
/// <param name="Path">For a folder: the share-relative path, backslash separated, without leading or trailing backslashes.</param>
/// <param name="Extension">For a file type: lower-case, without the dot.</param>
public sealed record ScanExclusionRule(long Id, ScanExclusionKind Kind, ScanExclusionEffect Effect, Guid? ShareId, string? Path, string? Extension, string Reason);

/// <summary>
/// The exclusions that apply to one share in one scan. Folder paths match the folder itself and everything below it,
/// ignoring letter case as Windows does. Nothing here touches the file server.
/// </summary>
public sealed class ShareScope
{
    public static readonly ShareScope Everything = new([]);

    private readonly List<ScanExclusionRule> _skip;
    private readonly List<ScanExclusionRule> _listOnlyFolders;
    private readonly Dictionary<string, ScanExclusionRule> _listOnlyTypes;

    public ShareScope(IEnumerable<ScanExclusionRule> rules)
    {
        var list = rules.ToList();
        _skip = [.. list.Where(r => r.Kind == ScanExclusionKind.Folder && r.Effect == ScanExclusionEffect.Skip && r.Path is { Length: > 0 })];
        _listOnlyFolders = [.. list.Where(r => r.Kind == ScanExclusionKind.Folder && r.Effect == ScanExclusionEffect.ListOnly && r.Path is { Length: > 0 })];
        _listOnlyTypes = new(StringComparer.OrdinalIgnoreCase);
        foreach (var r in list.Where(r => r.Kind == ScanExclusionKind.FileType && r.Extension is { Length: > 0 }))
        {
            _listOnlyTypes.TryAdd(r.Extension!, r);
        }
    }

    public bool IsEmpty => _skip.Count == 0 && _listOnlyFolders.Count == 0 && _listOnlyTypes.Count == 0;

    /// <summary>The exclusion that keeps this folder out of the scan, when there is one.</summary>
    public ScanExclusionRule? Skipped(string folderPath) => _skip.FirstOrDefault(r => ScanScope.IsAtOrBelow(folderPath, r.Path!));

    /// <summary>The exclusion that stops this file's contents being read, when there is one.</summary>
    public ScanExclusionRule? ContentExcluded(string folderPath, string fileName)
    {
        var ext = PathText.Extension(fileName);
        if (ext.Length > 0 && _listOnlyTypes.TryGetValue(ext, out var type))
        {
            return type;
        }
        return _listOnlyFolders.FirstOrDefault(r => ScanScope.IsAtOrBelow(folderPath, r.Path!))
            ?? _skip.FirstOrDefault(r => ScanScope.IsAtOrBelow(folderPath, r.Path!));
    }
}

/// <summary>Checks and matching for scan exclusions (D124).</summary>
public static class ScanScope
{
    public const int MaxPathLength = 1024;
    public const int MaxReasonLength = 500;
    public const int MinReasonLength = 3;
    public const int MaxExclusions = 500;

    /// <summary>
    /// File types where sensitive data usually lives. Not reading their contents hides most of what Data Scope finds,
    /// so excluding one always needs the admin to confirm.
    /// </summary>
    public static readonly IReadOnlySet<string> CommonDataTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "csv", "doc", "docm", "docx", "eml", "msg", "pdf", "ppt", "pptx", "rtf", "txt", "xls", "xlsb", "xlsm", "xlsx", "zip",
    };

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or inside it, ignoring case.</summary>
    public static bool IsAtOrBelow(string path, string folder) =>
        path.Length >= folder.Length
        && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
        && (path.Length == folder.Length || path[folder.Length] == '\\');

    /// <summary>
    /// A folder path inside a share as typed by an admin ("Archive/2019", "\Archive\2019\") in the stored form
    /// ("Archive\2019"), or why it can't be used.
    /// </summary>
    public static bool TryNormalizeFolder(string? input, out string path, out string? error)
    {
        path = "";
        var text = (input ?? "").Trim().Replace('/', '\\');
        if (text.StartsWith(@"\\", StringComparison.Ordinal))
        {
            error = @"Enter the folder's path inside the share, for example Archive\2019, not a \\server\share path.";
            return false;
        }
        text = text.Trim('\\');
        if (text.Length == 0)
        {
            error = "Enter a folder inside the share. To leave out a whole share, turn the share off instead.";
            return false;
        }
        if (text.Length > MaxPathLength)
        {
            error = $"Use a path of at most {MaxPathLength} characters.";
            return false;
        }
        if (text.Any(c => char.IsControl(c) || c is '*' or '?' or '<' or '>' or '|' or '"' or ':'))
        {
            error = "Enter one folder path without wildcards, drive letters or special characters.";
            return false;
        }
        var parts = text.Split('\\');
        if (parts.Any(p => p.Trim().Length == 0 || p is "." or ".." || p != p.TrimEnd(' ', '.')))
        {
            error = @"Enter a folder path like Archive\2019, without empty, . or .. parts.";
            return false;
        }
        path = string.Join('\\', parts);
        error = null;
        return true;
    }

    /// <summary>A file type as typed (".BAK", "*.bak", "bak") in the stored form ("bak"), or why it can't be used.</summary>
    public static bool TryNormalizeExtension(string? input, out string extension, out string? error)
    {
        extension = (input ?? "").Trim();
        if (extension.StartsWith("*.", StringComparison.Ordinal))
        {
            extension = extension[2..];
        }
        extension = extension.TrimStart('.').ToLowerInvariant();
        if (extension.Length is 0 or > 32 || !extension.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '~' or '$'))
        {
            error = "Enter one file type, for example .bak.";
            extension = "";
            return false;
        }
        error = null;
        return true;
    }

    /// <summary>Why a reason can't be used, or null.</summary>
    public static string? ReasonProblem(string? reason)
    {
        var text = reason?.Trim() ?? "";
        if (text.Length < MinReasonLength)
        {
            return "Say why, so others know later. For example: backups of the accounting system, copied nightly.";
        }
        return text.Length > MaxReasonLength || text.Any(c => char.IsControl(c) && c is not '\n' and not '\r')
            ? $"Use at most {MaxReasonLength} characters of plain text."
            : null;
    }
}
