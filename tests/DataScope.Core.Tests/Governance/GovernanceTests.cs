using DataScope.Core.Access;
using DataScope.Core.Governance;
using DataScope.Core.Tests.Access;

namespace DataScope.Core.Tests.Governance;

public class DecisionRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 9);

    [Fact]
    public void Keep_needs_no_justification() =>
        Assert.Null(DecisionRules.Validate(DecisionKind.Keep, null, null, Today));

    [Theory]
    [InlineData(DecisionKind.Remove)]
    [InlineData(DecisionKind.Investigate)]
    public void Remove_and_investigate_need_a_reason(DecisionKind kind)
    {
        Assert.NotNull(DecisionRules.Validate(kind, null, null, Today));
        Assert.NotNull(DecisionRules.Validate(kind, "   too short  ", null, Today));
        Assert.Null(DecisionRules.Validate(kind, "Left the team in March.", null, Today));
    }

    [Fact]
    public void An_exception_needs_a_reason_and_an_end_within_a_year()
    {
        const string reason = "Auditor access until the year-end audit closes.";
        Assert.NotNull(DecisionRules.Validate(DecisionKind.Exception, reason, null, Today));
        Assert.NotNull(DecisionRules.Validate(DecisionKind.Exception, reason, Today, Today));
        Assert.NotNull(DecisionRules.Validate(DecisionKind.Exception, reason, Today.AddDays(367), Today));
        Assert.NotNull(DecisionRules.Validate(DecisionKind.Exception, "short", Today.AddDays(30), Today));
        Assert.Null(DecisionRules.Validate(DecisionKind.Exception, reason, Today.AddDays(366), Today));
    }

    [Fact]
    public void Only_an_exception_has_an_end_date() =>
        Assert.NotNull(DecisionRules.Validate(DecisionKind.Remove, "Not needed any more.", Today.AddDays(30), Today));

    [Fact]
    public void Justifications_are_bounded() =>
        Assert.NotNull(DecisionRules.Validate(DecisionKind.Keep, new string('x', 1001), null, Today));
}

public class ReviewScheduleTests
{
    private static readonly Guid Review = Guid.Parse("0192a000-0000-7000-8000-000000000001");
    private static readonly DateOnly DueOn = new(2026, 11, 30);
    private static readonly DateTimeOffset Created = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private static List<NotificationKind> Kinds(DateTimeOffset now, bool backup = true, DateTimeOffset? created = null) =>
        ReviewSchedule.Due(Review, DueOn, created ?? Created, now, submitted: false, hasBackup: backup).Select(n => n.Kind).ToList();

    private static DateTimeOffset On(DateOnly day) => new(day.ToDateTime(new TimeOnly(10, 0)), TimeSpan.Zero);

    [Fact]
    public void A_new_review_gets_only_the_assignment()
    {
        Assert.Equal([NotificationKind.ReviewAssigned], Kinds(Created.AddHours(1)));
        // Even when created close to the due date, no reminder arrives on the first day.
        Assert.Equal([NotificationKind.ReviewAssigned], Kinds(On(DueOn), created: On(DueOn).AddHours(-2)));
    }

    [Fact]
    public void Reminds_a_week_before_and_the_day_before()
    {
        Assert.Equal([NotificationKind.ReviewAssigned], Kinds(On(DueOn.AddDays(-8))));
        Assert.Contains(NotificationKind.ReviewDueSoon, Kinds(On(DueOn.AddDays(-7))));
        Assert.Contains(NotificationKind.ReviewDueTomorrow, Kinds(On(DueOn.AddDays(-1))));
        Assert.Contains(NotificationKind.ReviewDueTomorrow, Kinds(On(DueOn)));
        Assert.DoesNotContain(NotificationKind.ReviewDueSoon, Kinds(On(DueOn.AddDays(-1))));
    }

    [Fact]
    public void Overdue_notices_repeat_weekly_with_their_own_keys()
    {
        string Key(int daysLate) => ReviewSchedule.Due(Review, DueOn, Created, On(DueOn.AddDays(daysLate)), false, false)
            .Single(n => n.Kind == NotificationKind.ReviewOverdue).Key;
        Assert.Equal(Key(1), Key(7));
        Assert.NotEqual(Key(7), Key(8));
        Assert.EndsWith(":overdue:0", Key(1), StringComparison.Ordinal);
        Assert.EndsWith(":overdue:1", Key(8), StringComparison.Ordinal);
    }

    [Fact]
    public void Escalates_to_the_backup_after_three_days_only_when_there_is_one()
    {
        Assert.DoesNotContain(NotificationKind.ReviewEscalated, Kinds(On(DueOn.AddDays(3))));
        var notices = ReviewSchedule.Due(Review, DueOn, Created, On(DueOn.AddDays(4)), false, true);
        Assert.True(notices.Single(n => n.Kind == NotificationKind.ReviewEscalated).ToBackup);
        Assert.DoesNotContain(NotificationKind.ReviewEscalated, Kinds(On(DueOn.AddDays(30)), backup: false));
    }

    [Fact]
    public void A_submitted_review_gets_nothing() =>
        Assert.Empty(ReviewSchedule.Due(Review, DueOn, Created, On(DueOn.AddDays(10)), submitted: true, hasBackup: true));

    [Fact]
    public void Overdue_means_after_the_due_day()
    {
        Assert.False(ReviewSchedule.IsOverdue(DueOn, On(DueOn).AddHours(13)));
        Assert.True(ReviewSchedule.IsOverdue(DueOn, On(DueOn.AddDays(1))));
    }
}

public class ReviewEmailTests
{
    [Fact]
    public void Emails_link_to_the_portal_and_cannot_approve_anything()
    {
        var (subject, body) = ReviewEmail.Compose(NotificationKind.ReviewAssigned, "Alice Example", "Q4 review", @"\\fs01\HR",
            new DateOnly(2026, 11, 30), new DateOnly(2026, 10, 9), "https://datascope.corp.example:8443/#my-reviews");
        Assert.Equal(@"Access review: \\fs01\HR", subject);
        Assert.Contains("https://datascope.corp.example:8443/#my-reviews", body, StringComparison.Ordinal);
        Assert.Contains("cannot approve or change anything", body, StringComparison.Ordinal);
        Assert.Contains("Monday 30 November 2026", body, StringComparison.Ordinal);
        Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Says_today_or_tomorrow()
    {
        var due = new DateOnly(2026, 11, 30);
        Assert.EndsWith("tomorrow", ReviewEmail.Compose(NotificationKind.ReviewDueTomorrow, "A", "C", "R", due, due.AddDays(-1), "L").Subject, StringComparison.Ordinal);
        Assert.EndsWith("today", ReviewEmail.Compose(NotificationKind.ReviewDueTomorrow, "A", "C", "R", due, due, "L").Subject, StringComparison.Ordinal);
    }

    [Fact]
    public void The_escalation_names_the_owner() =>
        Assert.Contains("assigned to Bob Owner", ReviewEmail.Compose(NotificationKind.ReviewEscalated, "Carol", "C", "R",
            new DateOnly(2026, 11, 30), new DateOnly(2026, 12, 5), "L", "Bob Owner").Body, StringComparison.Ordinal);
}

public class ReviewSnapshotBuilderTests
{
    private const uint Modify = 0x1301BF;
    private static readonly DirectoryGraph Graph = Lab.Graph();

    private static ResourcePermissions Folder(params Ace[] aces) => Lab.Perms(Lab.Sd(aces));

    private static readonly ResourcePermissions HrFolder = Folder(
        Lab.Allow(WellKnownSid.Administrators, AccessMask.FileAllAccess),
        Lab.Allow(WellKnownSid.CreatorOwner, AccessMask.FileAllAccess),
        Lab.Deny(Lab.Eve, AccessMask.FileGenericRead),
        Lab.Allow(Lab.HrRead, AccessMask.FileGenericRead),
        Lab.Allow(Lab.HrWrite, Modify),
        Lab.Allow(Lab.Bob, AccessMask.FileAllAccess),
        Lab.Allow(WellKnownSid.AuthenticatedUsers, AccessMask.FileGenericRead));

    private static IReadOnlyList<FrozenItem> Build(ResourcePermissions perms, int max = 2000) =>
        ReviewSnapshotBuilder.BuildItems(Graph, perms, sid => sid == Lab.Alice ? new PersonInfo("Human Resources", "HR Manager") : null, max);

    [Fact]
    public void Administrators_placeholders_and_denies_need_no_decision()
    {
        var items = Build(HrFolder);
        Assert.False(items.Single(i => i.TrusteeSid == WellKnownSid.Administrators).RequiresDecision);
        Assert.False(items.Single(i => i.TrusteeSid == WellKnownSid.CreatorOwner).RequiresDecision);
        var deny = items.Single(i => i.TrusteeSid == Lab.Eve);
        Assert.Equal("Deny", deny.Effect);
        Assert.False(deny.RequiresDecision);
        Assert.True(items.Single(i => i.TrusteeSid == Lab.HrRead).RequiresDecision);
        // Items needing a decision come first, and ordinals follow the order.
        Assert.True(items.TakeWhile(i => i.RequiresDecision).Count() == items.Count(i => i.RequiresDecision));
        Assert.Equal(Enumerable.Range(0, items.Count), items.Select(i => i.Ordinal));
    }

    [Fact]
    public void Lists_the_people_an_entry_reaches_with_disabled_accounts_and_other_access()
    {
        var hrRead = Build(HrFolder).Single(i => i.TrusteeSid == Lab.HrRead);
        Assert.Equal(["alice", "carol"], hrRead.Users.Select(u => u.Name));
        Assert.Equal(2, hrRead.UsersCertain);
        Assert.Equal(1, hrRead.UsersDisabled);
        Assert.Contains("DisabledAccounts", hrRead.Flags);
        var alice = hrRead.Users.Single(u => u.Sid == Lab.Alice);
        Assert.Equal("Human Resources", alice.Department);
        Assert.Equal(["alice", "HR_Read"], alice.Chain);
        // Alice keeps access through HR_Write and Authenticated Users if HR_Read is removed.
        Assert.NotNull(alice.AlsoVia);
        Assert.Contains("HR_Write", alice.AlsoVia, StringComparison.Ordinal);
        // Carol's account is disabled, so she has no effective access to keep.
        Assert.Null(hrRead.Users.Single(u => u.Sid == Lab.Carol).AlsoVia);
        Assert.Equal(1, hrRead.UsersWithOtherAccess);
    }

    [Fact]
    public void Accounts_on_the_no_decision_list_are_marked_and_need_no_decision()
    {
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Lab.HrWrite, Lab.Eve };
        var items = ReviewSnapshotBuilder.BuildItems(Graph, HrFolder, _ => null, noDecision: listed);
        var hrWrite = items.Single(i => i.TrusteeSid == Lab.HrWrite);
        Assert.False(hrWrite.RequiresDecision);
        Assert.Contains(ReviewSnapshotBuilder.NoDecisionNeeded, hrWrite.Flags);
        Assert.NotEmpty(hrWrite.Users); // The people are still frozen, for the evidence.
        Assert.Contains(ReviewSnapshotBuilder.NoDecisionNeeded, items.Single(i => i.TrusteeSid == Lab.Eve).Flags);
        Assert.True(items.Single(i => i.TrusteeSid == Lab.HrRead).RequiresDecision);
        Assert.DoesNotContain(items.Where(i => !listed.Contains(i.TrusteeSid)), i => i.Flags.Contains(ReviewSnapshotBuilder.NoDecisionNeeded));
        Assert.Equal(Build(HrFolder).Count(i => i.RequiresDecision) - 1, items.Count(i => i.RequiresDecision));
    }

    [Fact]
    public void Flags_broad_direct_and_full_control_entries()
    {
        var items = Build(HrFolder);
        Assert.Contains("Broad", items.Single(i => i.TrusteeSid == WellKnownSid.AuthenticatedUsers).Flags);
        var bob = items.Single(i => i.TrusteeSid == Lab.Bob);
        Assert.Contains("DirectUser", bob.Flags);
        Assert.Contains("CanChangePermissions", bob.Flags);
        Assert.Equal("Full control", bob.Rights);
    }

    [Fact]
    public void Caps_the_people_listed_but_keeps_the_counts()
    {
        var everyone = Build(Folder(Lab.Allow(WellKnownSid.AuthenticatedUsers, AccessMask.FileGenericRead)), max: 2).Single();
        Assert.Equal(2, everyone.Users.Count);
        Assert.True(everyone.UsersTruncated);
        Assert.True(everyone.UsersCertain + everyone.UsersPossible > 2);
    }

    [Fact]
    public void Nothing_is_frozen_when_the_folder_permissions_were_not_read()
    {
        var perms = new ResourcePermissions(PermissionState.Read, Lab.EveryoneFull, PermissionState.AccessDenied, null);
        Assert.Empty(Build(perms));
    }

    [Fact]
    public void The_seal_is_deterministic_and_changes_with_any_frozen_detail()
    {
        FrozenReview Review(IReadOnlyList<FrozenItem> items) => new(
            Guid.Parse("0192a000-0000-7000-8000-000000000010"), Guid.Parse("0192a000-0000-7000-8000-000000000011"),
            Guid.Parse("0192a000-0000-7000-8000-000000000012"), @"\\fs01\HR", Lab.Alice, "alice", null, null, null, "Read", "Read",
            1, 2, new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero), ["note"], items);
        var items = Build(HrFolder);
        var hash = Review(items).Hash();
        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.Equal(hash, Review(Build(HrFolder)).Hash());
        var changed = items.Select(i => i.TrusteeSid == Lab.HrRead ? i with { Users = [.. i.Users.Skip(1)] } : i).ToList();
        Assert.NotEqual(hash, Review(changed).Hash());
    }

    [Fact]
    public void Notes_say_what_limits_certainty_and_that_access_is_not_activity()
    {
        var notes = ReviewSnapshotBuilder.Notes(Graph, HrFolder, directorySynced: false);
        Assert.Contains(notes, n => n.Contains("not been synced", StringComparison.Ordinal));
        Assert.Contains(notes, n => n.Contains("not a record of anyone opening a file", StringComparison.Ordinal));
    }
}
