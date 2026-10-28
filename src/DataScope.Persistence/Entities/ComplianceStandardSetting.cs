using DataScope.Core.Classification;

namespace DataScope.Persistence.Entities;

/// <summary>
/// An administrator's choice for one compliance standard from <see cref="Core.Compliance.ComplianceCatalog"/> (D126):
/// whether it is on, which kinds of data count and from what confidence, and why. No row means the standard is off
/// with its default categories. Kept when turned off; every change is also in the audit log.
/// </summary>
public sealed class ComplianceStandardSetting
{
    public required string StandardId { get; set; }
    public bool Enabled { get; set; }

    /// <summary><see cref="FindingCategory"/> names that count towards the standard.</summary>
    public required string[] Categories { get; set; }

    public Confidence MinConfidence { get; set; }
    public required string Reason { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public required string UpdatedBySid { get; set; }
    public required string UpdatedByName { get; set; }
}
