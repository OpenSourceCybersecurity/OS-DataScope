namespace DataScope.Persistence.Entities;

/// <summary>
/// The posture at the end of one day (D165): risks by severity, sensitive data and how exposed and labelled it is,
/// coverage and follow-up, as Data Scope measured it. Kept so progress can be shown over time; a day's row is measured
/// again whenever a scan, sync or decision changes the picture that day, so it ends as the day's last state.
/// </summary>
public sealed class PostureSnapshot
{
    /// <summary>The day, in UTC.</summary>
    public DateOnly Day { get; set; }

    public DateTimeOffset TakenAt { get; set; }

    /// <summary>When the newest scan behind it finished.</summary>
    public DateTimeOffset? LastScanAt { get; set; }

    /// <summary>Risks needing attention (not accepted or snoozed), by severity.</summary>
    public int RisksHigh { get; set; }

    public int RisksMedium { get; set; }
    public int RisksLow { get; set; }
    public int RisksAccepted { get; set; }
    public int RisksSnoozed { get; set; }

    /// <summary>The risk check stopped early somewhere, so the counts may be low.</summary>
    public bool RisksLimited { get; set; }

    public long Files { get; set; }

    /// <summary>Files whose contents were not read in full or in part: coverage gaps, not clean.</summary>
    public long FilesNotRead { get; set; }

    public long Restricted { get; set; }
    public long Confidential { get; set; }

    /// <summary>Sensitive files on file shares that any domain user can open.</summary>
    public long SensitiveOpen { get; set; }

    /// <summary>Included shares with no owner, as Overview counts them.</summary>
    public int SharesUnowned { get; set; }
}
