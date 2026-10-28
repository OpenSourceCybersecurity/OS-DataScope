using DataScope.Core.Classification;

namespace DataScope.Persistence.Entities;

/// <summary>
/// An administrator's own detection rule (D133). Its findings carry the rule id <c>DS-CUSTOM-{Id}</c>. Kept when turned
/// off; removing it is allowed and audited, and files it matched are read again on the next scan. Every change is in
/// the audit log.
/// </summary>
public sealed class CustomDetectionRule
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public CustomRuleKind Kind { get; set; }

    /// <summary>For a pattern rule: a .NET regular expression, run on the non-backtracking engine.</summary>
    public string? Pattern { get; set; }

    /// <summary>For a keyword rule: words and phrases, cleaned by <see cref="CustomRules.Clean"/>.</summary>
    public required string[] Keywords { get; set; }

    public bool CaseSensitive { get; set; }
    public required string[] NearWords { get; set; }
    public int MinMatches { get; set; }
    public CustomRuleEffect Effect { get; set; }
    public bool Enabled { get; set; }
    public required string Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string CreatedByName { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public required string UpdatedBySid { get; set; }
    public required string UpdatedByName { get; set; }

    public CustomRuleDefinition ToDefinition() => new(Id, Kind, Pattern, Keywords, CaseSensitive, NearWords, MinMatches, Effect);
}
