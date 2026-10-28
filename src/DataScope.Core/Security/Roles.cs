namespace DataScope.Core.Security;

/// <summary>Application roles from the PRD. Users get them through AD group mappings.</summary>
public static class Roles
{
    public const string PlatformAdmin = "PlatformAdmin";
    public const string SecurityAnalyst = "SecurityAnalyst";
    public const string Auditor = "Auditor";
    public const string DataOwner = "DataOwner";
    public const string Viewer = "Viewer";

    public static readonly IReadOnlyList<string> All = [PlatformAdmin, SecurityAnalyst, Auditor, DataOwner, Viewer];
}

/// <summary>Authorization policy names used by API endpoints.</summary>
public static class Policies
{
    /// <summary>Change configuration: servers, scans, policies, RBAC.</summary>
    public const string ManagePlatform = nameof(ManagePlatform);

    /// <summary>Read inventory, findings and posture.</summary>
    public const string ReadPosture = nameof(ReadPosture);

    /// <summary>Read the Data Scope admin audit log.</summary>
    public const string ReadAuditLog = nameof(ReadAuditLog);

    /// <summary>See redacted evidence snippets behind findings. Viewers see categories and counts only.</summary>
    public const string ReadEvidence = nameof(ReadEvidence);

    /// <summary>Override a file's sensitivity label, for example to mark a false positive.</summary>
    public const string ReviewClassification = nameof(ReviewClassification);

    /// <summary>Assign owners and run access review campaigns.</summary>
    public const string ManageReviews = nameof(ManageReviews);

    /// <summary>Read every campaign, review, decision and the evidence export.</summary>
    public const string ReadReviews = nameof(ReadReviews);

    /// <summary>Read file activity: who did what to which file, with the raw events (D101).</summary>
    public const string ReadActivity = nameof(ReadActivity);

    /// <summary>Acknowledge or reopen file activity alerts, with a note (D105).</summary>
    public const string TriageActivity = nameof(TriageActivity);

    /// <summary>Accept, snooze or reopen a risk (D115).</summary>
    public const string TriageRisks = nameof(TriageRisks);
}
