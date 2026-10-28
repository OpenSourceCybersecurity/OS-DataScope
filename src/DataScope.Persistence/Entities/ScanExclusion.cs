using DataScope.Core.Inventory;

namespace DataScope.Persistence.Entities;

/// <summary>
/// A folder or file type an administrator left out of scans, with why (D124). Kept when turned off so the reason and
/// history stay; every change is also in the audit log.
/// </summary>
public sealed class ScanExclusion
{
    public long Id { get; set; }
    public ScanExclusionKind Kind { get; set; }
    public ScanExclusionEffect Effect { get; set; }

    /// <summary>The share a folder is on; for a file type, the one share it applies to, or null for every share.</summary>
    public Guid? ShareId { get; set; }

    /// <summary>For a folder: share-relative, backslash separated, without leading or trailing backslashes.</summary>
    public string? Path { get; set; }

    /// <summary>For a file type: lower-case, without the dot.</summary>
    public string? Extension { get; set; }

    public required string Reason { get; set; }
    public bool Enabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string CreatedBySid { get; set; }
    public required string CreatedByName { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public required string UpdatedBySid { get; set; }
    public required string UpdatedByName { get; set; }
}
