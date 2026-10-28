namespace DataScope.Core.Inventory;

/// <summary>
/// Why part of a share was not inventoried. Every gap is shown to users as missing coverage,
/// never as "clean" or "empty".
/// </summary>
public enum GapKind
{
    /// <summary>The share root could not be opened at all.</summary>
    ShareUnavailable,

    /// <summary>The scan identity was refused access to a folder.</summary>
    AccessDenied,

    /// <summary>A folder disappeared between being found and being listed.</summary>
    NotFound,

    PathTooLong,

    /// <summary>A junction, symbolic link or mount point. Never followed, to avoid loops and leaving the share.</summary>
    ReparsePointSkipped,

    /// <summary>Folder is deeper than the configured limit.</summary>
    DepthLimit,

    /// <summary>A single folder holds more entries than the configured limit; the rest were not listed.</summary>
    FolderEntryLimit,

    /// <summary>The job reached its total entry limit; remaining folders were not listed.</summary>
    JobEntryLimit,

    /// <summary>A name contains characters that cannot be stored exactly; it was stored with replacements.</summary>
    UnrepresentableName,

    IoError,

    /// <summary>An administrator excluded this folder from scans (D124). Not scanned is never the same as clean.</summary>
    Excluded,
}
