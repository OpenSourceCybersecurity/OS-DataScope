using DataScope.Core.Classification;

namespace DataScope.Persistence.Entities;

/// <summary>
/// One detection rule's possible matches in one file, from the file's latest classification. Replaced when the file
/// is classified again. <see cref="Evidence"/> holds redacted snippets only, never a matched value.
/// </summary>
public sealed class ClassificationFinding
{
    public long Id { get; set; }
    public Guid FileId { get; set; }
    public Guid JobId { get; set; }
    public required string PolicyVersion { get; set; }
    public required string RuleId { get; set; }
    public FindingCategory Category { get; set; }
    public Confidence Confidence { get; set; }
    public int MatchCount { get; set; }
    public required string[] Evidence { get; set; }
    public DateTimeOffset DiscoveredAt { get; set; }
}

/// <summary>
/// A person's decision about a file's label. Append-only: the latest row for a file is the one in force, and a row
/// with no <see cref="Sensitivity"/> means the override was removed. Every row is also in the audit log.
/// </summary>
public sealed class ClassificationOverride
{
    public long Id { get; set; }
    public Guid FileId { get; set; }

    /// <summary>The label set by the reviewer, or null when the override was removed.</summary>
    public Sensitivity? Sensitivity { get; set; }

    /// <summary>The policy label at the time, so later readers know what was overridden.</summary>
    public Sensitivity PolicySensitivity { get; set; }

    public required string Reason { get; set; }
    public required string ReviewerSid { get; set; }
    public required string ReviewerName { get; set; }
    public DateTimeOffset At { get; set; }
}
