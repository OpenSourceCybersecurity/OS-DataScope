namespace DataScope.Core.Governance;

public enum NotificationKind
{
    /// <summary>A new review was assigned to the owner (or to a delegate).</summary>
    ReviewAssigned,

    /// <summary>The due date is a week away.</summary>
    ReviewDueSoon,

    /// <summary>The review is due tomorrow or today.</summary>
    ReviewDueTomorrow,

    /// <summary>The due date has passed. Sent weekly while the review stays open.</summary>
    ReviewOverdue,

    /// <summary>Sent to the backup owner when the owner has not finished a few days after the due date.</summary>
    ReviewEscalated,

    /// <summary>A new file activity alert, sent to the addresses under <c>Activity:Alerts:EmailTo</c> (D105).</summary>
    ActivityAlert,

    /// <summary>A test sent from System settings to check the mail server settings (D123).</summary>
    TestEmail,
}

/// <summary>A notification that should exist. The key makes enqueuing idempotent: the same key is only ever sent once.</summary>
public sealed record ScheduledNotice(NotificationKind Kind, string Key, bool ToBackup);

/// <summary>When reviewers are reminded. Pure: the caller supplies the clock and stores what was sent.</summary>
public static class ReviewSchedule
{
    public const int DueSoonDays = 7;
    public const int EscalateAfterDays = 3;

    /// <param name="createdAt">When the review was assigned; reminders wait a day so a new review does not arrive with one.</param>
    public static IReadOnlyList<ScheduledNotice> Due(Guid reviewId, DateOnly dueOn, DateTimeOffset createdAt, DateTimeOffset now, bool submitted, bool hasBackup)
    {
        if (submitted)
        {
            return [];
        }
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var notices = new List<ScheduledNotice> { new(NotificationKind.ReviewAssigned, $"review:{reviewId:N}:assigned", false) };
        var settled = now - createdAt >= TimeSpan.FromDays(1);
        if (settled && today >= dueOn.AddDays(-DueSoonDays) && today < dueOn.AddDays(-1))
        {
            notices.Add(new(NotificationKind.ReviewDueSoon, $"review:{reviewId:N}:due-soon", false));
        }
        if (settled && today >= dueOn.AddDays(-1) && today <= dueOn)
        {
            notices.Add(new(NotificationKind.ReviewDueTomorrow, $"review:{reviewId:N}:due-tomorrow", false));
        }
        if (today > dueOn)
        {
            var week = (today.DayNumber - dueOn.DayNumber - 1) / 7;
            notices.Add(new(NotificationKind.ReviewOverdue, $"review:{reviewId:N}:overdue:{week}", false));
            if (hasBackup && today.DayNumber - dueOn.DayNumber > EscalateAfterDays)
            {
                notices.Add(new(NotificationKind.ReviewEscalated, $"review:{reviewId:N}:escalated", true));
            }
        }
        return notices;
    }

    public static bool IsOverdue(DateOnly dueOn, DateTimeOffset now) => DateOnly.FromDateTime(now.UtcDateTime) > dueOn;
}

/// <summary>
/// Review emails. They name the review and link to Data Scope, where the reader signs in with Windows. They carry no
/// token and no decision buttons: an email can never approve anything, and a forwarded email gives the reader nothing.
/// </summary>
public static class ReviewEmail
{
    public static (string Subject, string Body) Compose(NotificationKind kind, string recipientName, string campaignName, string resource, DateOnly dueOn, DateOnly today, string link, string? ownerName = null)
    {
        var due = dueOn.ToString("dddd d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
        var (subject, opening) = kind switch
        {
            NotificationKind.ReviewAssigned => ($"Access review: {resource}",
                $"You have been asked to review who can open the files in {resource}, as part of \"{campaignName}\". It is due on {due}."),
            NotificationKind.ReviewDueSoon => ($"Reminder: access review for {resource} is due in a week",
                $"Your review of who can open the files in {resource} (\"{campaignName}\") is due on {due}."),
            NotificationKind.ReviewDueTomorrow => ($"Reminder: access review for {resource} is due {(today >= dueOn ? "today" : "tomorrow")}",
                $"Your review of who can open the files in {resource} (\"{campaignName}\") is due on {due}."),
            NotificationKind.ReviewOverdue => ($"Overdue: access review for {resource}",
                $"Your review of who can open the files in {resource} (\"{campaignName}\") was due on {due} and is not finished."),
            _ => ($"Escalated: access review for {resource}",
                $"You are the backup owner of {resource}. The review in \"{campaignName}\" assigned to {ownerName ?? "the owner"} was due on {due} and is not finished, so it is now yours to complete as well."),
        };
        var body = string.Join("\r\n", [
            $"Hello {recipientName},",
            "",
            opening,
            "",
            "Open Data Scope to see each group and person with access and decide: Keep, Remove, Investigate or Exception.",
            link,
            "",
            "Data Scope signs you in with your Windows account. This email cannot approve or change anything, and nothing changes in Active Directory or on the file server until IT acts on your decisions.",
            "",
            "Data Scope",
        ]);
        return (subject, body);
    }
}
