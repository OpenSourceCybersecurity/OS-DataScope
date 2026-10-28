using DataScope.Core.Access;
using DataScope.Core.Classification;
using DataScope.Core.Inventory;

namespace DataScope.Persistence.Entities;

public enum ShareState
{
    /// <summary>Found but not inventoried yet.</summary>
    NotScanned,

    /// <summary>Last scan listed every folder.</summary>
    Scanned,

    /// <summary>Last scan finished with gaps: some folders were not listed.</summary>
    Partial,

    /// <summary>Last scan could not open the share root.</summary>
    Unavailable,

    /// <summary>The server no longer reports this share. Kept for history.</summary>
    Removed,
}

/// <summary>Where a share's local folder came from (D99).</summary>
public enum ShareLocalPathSource
{
    /// <summary>A 5140 or 5145 event named the share and its folder.</summary>
    Event,

    /// <summary>An administrator entered it.</summary>
    Admin,
}

public sealed class Share
{
    public Guid Id { get; set; }
    public Guid ServerId { get; set; }
    public ManagedServer? Server { get; set; }

    public required string Name { get; set; }

    /// <summary>Upper-cased name: share names are case-insensitive.</summary>
    public required string NameKey { get; set; }

    public required string UncPath { get; set; }
    public string? Remark { get; set; }
    public ShareKind Kind { get; set; }

    /// <summary>Whether scans include this share. Disk shares default to true; special shares can never be included.</summary>
    public bool Included { get; set; }

    public ShareState State { get; set; } = ShareState.NotScanned;
    public Guid? LastScanJobId { get; set; }
    public DateTimeOffset? LastScannedAt { get; set; }
    public long FolderCount { get; set; }
    public long FileCount { get; set; }
    public long TotalBytes { get; set; }
    public int GapCount { get; set; }

    /// <summary>The share permission (Advanced Sharing) as read by the last scan.</summary>
    public long? SecurityId { get; set; }

    /// <summary>Reading share permissions needs administrator rights on the file server; without them this stays AccessDenied.</summary>
    public PermissionState SecurityState { get; set; }

    public DateTimeOffset? SecurityReadAt { get; set; }

    /// <summary>The share's folder on the file server (C:\Shares\Finance), used to match file activity to it (D99).</summary>
    public string? LocalPath { get; set; }

    public ShareLocalPathSource? LocalPathSource { get; set; }
    public DateTimeOffset? LocalPathSetAt { get; set; }

    /// <summary>Why an administrator left the share out of scans, and who and when (D124). Cleared when it is included again.</summary>
    public string? ExclusionReason { get; set; }

    public DateTimeOffset? ExcludedAt { get; set; }
    public string? ExcludedBySid { get; set; }
    public string? ExcludedByName { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public uint RowVersion { get; set; }
}

public sealed class Folder
{
    public Guid Id { get; set; }
    public Guid ShareId { get; set; }
    public Guid? ParentId { get; set; }

    /// <summary>Share-relative path with backslashes; empty for the share root.</summary>
    public required string Path { get; set; }

    /// <summary>SHA-256 of the upper-cased path. Unique per share; paths can exceed index size limits.</summary>
    public required byte[] PathHash { get; set; }

    public int Depth { get; set; }
    public DateTimeOffset? ModifiedAtSource { get; set; }
    public int Attributes { get; set; }
    public Guid FirstSeenJobId { get; set; }
    public Guid LastSeenJobId { get; set; }

    /// <summary>Last job that listed this folder's contents completely. Null when it has never been listed.</summary>
    public Guid? LastWalkedJobId { get; set; }

    public DateTimeOffset? RemovedAt { get; set; }

    /// <summary>The folder's NTFS security descriptor (owner and DACL) as read by the last scan.</summary>
    public long? SecurityId { get; set; }

    public PermissionState SecurityState { get; set; }
    public DateTimeOffset? SecurityReadAt { get; set; }
}

public sealed class InventoryFile
{
    public Guid Id { get; set; }
    public Guid ShareId { get; set; }
    public Guid FolderId { get; set; }
    public required string Name { get; set; }
    public required string NameKey { get; set; }
    public required string Extension { get; set; }
    public long Size { get; set; }
    public DateTimeOffset? CreatedAtSource { get; set; }
    public DateTimeOffset? ModifiedAtSource { get; set; }
    public int Attributes { get; set; }

    public Guid FirstSeenJobId { get; set; }
    public Guid LastSeenJobId { get; set; }
    public DateTimeOffset? RemovedAt { get; set; }

    /// <summary>Label from the classification policy. Unknown until the contents have been read.</summary>
    public Sensitivity Sensitivity { get; set; }

    /// <summary>Label set by a reviewer, which wins over <see cref="Sensitivity"/>. History is in classification_overrides.</summary>
    public Sensitivity? OverrideSensitivity { get; set; }

    public ContentState ContentState { get; set; }

    /// <summary>Why the contents were not fully read. Fixed text; never file contents.</summary>
    public string? ContentNote { get; set; }

    public DateTimeOffset? ClassifiedAt { get; set; }
    public Guid? ClassifiedJobId { get; set; }
    public string? ClassifiedPolicy { get; set; }

    /// <summary>Size and modified time when last classified. A file whose values still match is not read again.</summary>
    public long? ClassifiedSize { get; set; }

    public DateTimeOffset? ClassifiedModifiedAt { get; set; }
}

/// <summary>
/// A share's totals as one finished scan left them. The inventory keeps only the current state; these rows are what
/// trends over time are drawn from. Counts only, never names or contents.
/// </summary>
public sealed class ShareScanHistory
{
    public long Id { get; set; }
    public Guid ShareId { get; set; }
    public Guid JobId { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public ShareState State { get; set; }
    public long Files { get; set; }
    public long Bytes { get; set; }
    public long RestrictedFiles { get; set; }
    public long ConfidentialFiles { get; set; }

    /// <summary>Files whose contents were not read (skipped, unreadable, too large, not yet reached).</summary>
    public long FilesNotRead { get; set; }

    public int Gaps { get; set; }
}

/// <summary>A part of a share a scan could not cover, shown to users as missing coverage.</summary>
public sealed class ScanGap
{
    public long Id { get; set; }
    public Guid JobId { get; set; }
    public Guid? ShareId { get; set; }

    /// <summary>Share-relative path, or empty for the share itself.</summary>
    public required string Path { get; set; }

    public GapKind Kind { get; set; }
    public required string Message { get; set; }
    public DateTimeOffset At { get; set; }
}

/// <summary>Folders found by a running job but not listed yet. Lives only while the job is unfinished.</summary>
public sealed class ScanFrontierItem
{
    public long Seq { get; set; }
    public Guid JobId { get; set; }
    public Guid FolderId { get; set; }
    public Guid ShareId { get; set; }
    public int Depth { get; set; }
}
