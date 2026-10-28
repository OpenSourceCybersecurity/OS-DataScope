using DataScope.Core.Access;
using DataScope.Core.Governance;

namespace DataScope.Persistence.Entities;

/// <summary>
/// Who owns a share or folder. Assignments are never edited: a change ends the current row and adds a new one, so the
/// history of ownership stays. At most one row per folder is current (no <see cref="EndedAt"/>).
/// </summary>
public sealed class OwnerAssignment
{
    public long Id { get; set; }

    /// <summary>The folder owned; a share is owned through its root folder.</summary>
    public Guid FolderId { get; set; }

    public Guid ShareId { get; set; }
    public required string OwnerSid { get; set; }
    public required string OwnerName { get; set; }
    public string? OwnerEmail { get; set; }
    public string? BackupSid { get; set; }
    public string? BackupName { get; set; }
    public string? BackupEmail { get; set; }
    public string? Department { get; set; }
    public string? Purpose { get; set; }
    public ReviewFrequency ReviewFrequency { get; set; }
    public string? RetentionCategory { get; set; }
    public string? Notes { get; set; }
    public required string AssignedBySid { get; set; }
    public required string AssignedByName { get; set; }
    public DateTimeOffset AssignedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string? EndedBySid { get; set; }
    public string? EndReason { get; set; }
}

/// <summary>An access review campaign: a set of reviews frozen at one moment.</summary>
public sealed class ReviewCampaign
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public DateOnly DueOn { get; set; }
    public CampaignState State { get; set; }

    /// <summary>The scope as chosen, kept as JSON for the evidence export.</summary>
    public required string Scope { get; set; }

    /// <summary>Resources in scope that could not be reviewed, with the reason, as JSON.</summary>
    public required string Skipped { get; set; }

    /// <summary>The directory sync the campaign was frozen on. The snapshot itself may later be pruned; the frozen items are kept.</summary>
    public Guid? DirectorySnapshotId { get; set; }

    public DateTimeOffset? DirectorySyncedAt { get; set; }
    public string? Domain { get; set; }
    public required string CreatedBySid { get; set; }
    public required string CreatedByName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public string? ClosedBySid { get; set; }
    public string? ClosedByName { get; set; }
    public uint RowVersion { get; set; }
}

/// <summary>One owner's review of one folder within a campaign.</summary>
public sealed class AccessReview
{
    public Guid Id { get; set; }
    public Guid CampaignId { get; set; }
    public Guid FolderId { get; set; }
    public Guid ShareId { get; set; }
    public Guid ServerId { get; set; }

    /// <summary>The full UNC path at the time, for display and evidence.</summary>
    public required string Resource { get; set; }

    public required string OwnerSid { get; set; }
    public required string OwnerName { get; set; }
    public string? OwnerEmail { get; set; }
    public string? BackupSid { get; set; }
    public string? BackupName { get; set; }
    public string? BackupEmail { get; set; }

    /// <summary>Someone the owner (or an administrator) handed the review to. They can decide; the owner still can too.</summary>
    public string? DelegateSid { get; set; }

    public string? DelegateName { get; set; }
    public string? DelegateEmail { get; set; }

    public ReviewState State { get; set; }
    public PermissionState ShareState { get; set; }
    public PermissionState FolderState { get; set; }
    public long? ShareSecurityId { get; set; }
    public long? FolderSecurityId { get; set; }
    public DateTimeOffset? PermissionsReadAt { get; set; }
    public string[] Notes { get; set; } = [];
    public int ItemCount { get; set; }
    public int DecisionsRequired { get; set; }

    /// <summary>SHA-256 of the frozen review (see <see cref="FrozenReview.Hash"/>).</summary>
    public required string EvidenceHash { get; set; }

    public DateTimeOffset FrozenAt { get; set; }
    public DateTimeOffset? EscalatedAt { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public string? SubmittedBySid { get; set; }
    public string? SubmittedByName { get; set; }
    public uint RowVersion { get; set; }
}

/// <summary>
/// A user or group that needs no decision in access reviews, such as an IT admin group or a backup account. Applied when
/// a campaign is created: their entries are frozen as needing no decision, marked, and not shown to owners. Ending an
/// entry keeps the row, so the list's history stays.
/// </summary>
public sealed class ReviewExemption
{
    public long Id { get; set; }
    public required string Sid { get; set; }
    public required string Name { get; set; }

    /// <summary>User or Group, from the directory sync it was chosen from.</summary>
    public required string Kind { get; set; }

    public required string Reason { get; set; }
    public required string AddedBySid { get; set; }
    public required string AddedByName { get; set; }
    public DateTimeOffset AddedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string? EndedBySid { get; set; }
    public string? EndedByName { get; set; }
}

/// <summary>
/// A risk someone accepted or snoozed (D115). It hides the risk from the Risks screen's default view until the date,
/// and only while the risk is no more severe than when it was decided. Reopening ends the row; it is never deleted.
/// </summary>
public sealed class RiskDecision
{
    public long Id { get; set; }

    /// <summary>The risk's id from the Risks screen: the rule and what it matched.</summary>
    public required string RiskId { get; set; }

    public required string Rule { get; set; }
    public required string Title { get; set; }
    public required string Where { get; set; }

    /// <summary>The severity when decided. If the risk becomes more severe, the decision no longer hides it.</summary>
    public required string Severity { get; set; }

    public RiskDecisionKind Kind { get; set; }
    public string? Note { get; set; }

    /// <summary>The last day the decision applies.</summary>
    public DateOnly Until { get; set; }

    public required string DecidedBySid { get; set; }
    public required string DecidedByName { get; set; }
    public DateTimeOffset DecidedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string? EndedBySid { get; set; }
    public string? EndedByName { get; set; }
}

/// <summary>A trustee frozen into a review. Rows are immutable: a database trigger rejects updates and deletes.</summary>
public sealed class ReviewItem
{
    public long Id { get; set; }
    public Guid ReviewId { get; set; }
    public int Ordinal { get; set; }
    public required string TrusteeSid { get; set; }
    public required string TrusteeName { get; set; }
    public required string TrusteeKind { get; set; }
    public required string Effect { get; set; }
    public required string Rights { get; set; }
    public required string AppliesTo { get; set; }
    public bool Inherited { get; set; }
    public bool Broad { get; set; }
    public bool Administrative { get; set; }
    public bool RequiresDecision { get; set; }
    public int UsersCertain { get; set; }
    public int UsersPossible { get; set; }
    public int UsersDisabled { get; set; }
    public int UsersWithOtherAccess { get; set; }
    public bool UsersTruncated { get; set; }
    public string[] Flags { get; set; } = [];
}

/// <summary>A person reached by a review item, frozen. Immutable like the item.</summary>
public sealed class ReviewItemUser
{
    public long ItemId { get; set; }
    public int Ordinal { get; set; }
    public required string Sid { get; set; }
    public required string Name { get; set; }
    public bool Enabled { get; set; }
    public string? Department { get; set; }
    public string? Title { get; set; }
    public bool Certain { get; set; }
    public string[] Chain { get; set; } = [];
    public required string Rights { get; set; }
    public required string UpTo { get; set; }
    public required string Certainty { get; set; }
    public string? AlsoVia { get; set; }
}

/// <summary>A decision on an item. Append-only: the newest row per item is in force until the review is submitted.</summary>
public sealed class ReviewDecision
{
    public long Id { get; set; }
    public Guid ReviewId { get; set; }
    public long ItemId { get; set; }
    public DecisionKind Decision { get; set; }
    public string? Justification { get; set; }
    public DateOnly? ExceptionExpiresOn { get; set; }
    public required string DecidedBySid { get; set; }
    public required string DecidedByName { get; set; }

    /// <summary>Owner, Backup, Delegate: the capacity the person decided in.</summary>
    public required string DecidedAs { get; set; }

    public DateTimeOffset DecidedAt { get; set; }
}

/// <summary>Things that happened to a review, for its trail: created, notified, delegated, escalated, submitted. Append-only.</summary>
public sealed class ReviewEvent
{
    public long Id { get; set; }
    public Guid ReviewId { get; set; }
    public required string Kind { get; set; }
    public required string ActorSid { get; set; }
    public required string ActorName { get; set; }
    public required string Detail { get; set; }
    public DateTimeOffset At { get; set; }
}

public enum OutboxState
{
    Pending,
    Sent,

    /// <summary>Gave up after repeated failures.</summary>
    Failed,

    /// <summary>Not sent: email is not set up, or the recipient has no address.</summary>
    Skipped,
}

/// <summary>An email waiting to be sent, sent, or given up on. The idempotency key makes each notice unique.</summary>
public sealed class OutboxMessage
{
    public long Id { get; set; }
    public required string IdempotencyKey { get; set; }
    public NotificationKind Kind { get; set; }
    public Guid? ReviewId { get; set; }
    public required string RecipientSid { get; set; }
    public required string RecipientName { get; set; }
    public string? RecipientEmail { get; set; }
    public required string Subject { get; set; }
    public required string Body { get; set; }
    public OutboxState State { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
}
