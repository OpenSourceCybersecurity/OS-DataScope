namespace DataScope.Core.Governance;

/// <summary>How often an owner is expected to review who can reach their data.</summary>
public enum ReviewFrequency
{
    Quarterly,
    Semiannual,
    Annual,
}

public enum CampaignState
{
    /// <summary>Owners can review and decide.</summary>
    Open,

    /// <summary>Closed by an administrator. Nothing more can be decided; the evidence stays.</summary>
    Closed,
}

public enum ReviewState
{
    /// <summary>No decision recorded yet.</summary>
    Pending,

    /// <summary>Some decisions recorded; the reviewer has not submitted.</summary>
    InProgress,

    /// <summary>Submitted. Decisions are final and kept as evidence.</summary>
    Submitted,
}

/// <summary>What a reviewer decided about one entry in a folder's permissions.</summary>
public enum DecisionKind
{
    /// <summary>The access is still needed.</summary>
    Keep,

    /// <summary>The access should be removed. Data Scope records the decision and never changes permissions or groups itself.</summary>
    Remove,

    /// <summary>The reviewer does not know; someone should look into it.</summary>
    Investigate,

    /// <summary>The access breaks a rule but is accepted for a stated reason until a date.</summary>
    Exception,
}

/// <summary>What someone decided about a risk on the Risks screen (D115).</summary>
public enum RiskDecisionKind
{
    /// <summary>Known and tolerated for a stated reason until a date, at most a year away.</summary>
    Accepted,

    /// <summary>Hidden for a while, at most 90 days, to deal with later.</summary>
    Snoozed,
}

public static class GovernanceText
{
    public static int Months(this ReviewFrequency frequency) => frequency switch
    {
        ReviewFrequency.Quarterly => 3,
        ReviewFrequency.Semiannual => 6,
        _ => 12,
    };
}

/// <summary>Rules for a single review decision. Returns a message for people, or null when the decision is acceptable.</summary>
public static class DecisionRules
{
    public const int MinJustification = 10;
    public const int MaxText = 1000;
    public const int MaxExceptionDays = 366;

    public static string? Validate(DecisionKind kind, string? justification, DateOnly? exceptionExpires, DateOnly today)
    {
        var text = justification?.Trim() ?? "";
        if (text.Length > MaxText)
        {
            return $"Keep the justification under {MaxText} characters.";
        }
        if (kind != DecisionKind.Keep && text.Length < MinJustification)
        {
            return kind switch
            {
                DecisionKind.Remove => "Say why this access should be removed (at least 10 characters).",
                DecisionKind.Investigate => "Say what should be looked into (at least 10 characters).",
                _ => "Give the business reason for the exception (at least 10 characters). Auditors will read it.",
            };
        }
        if (kind == DecisionKind.Exception)
        {
            if (exceptionExpires is not { } expires)
            {
                return "Choose when the exception ends. It must be reviewed again by then.";
            }
            if (expires <= today || expires > today.AddDays(MaxExceptionDays))
            {
                return "The exception must end after today and within a year.";
            }
        }
        else if (exceptionExpires is not null)
        {
            return "Only an exception has an end date.";
        }
        return null;
    }
}
