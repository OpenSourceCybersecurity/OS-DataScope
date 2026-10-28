using System.Globalization;

namespace DataScope.Core.Activity;

/// <summary>What an activity alert is about (D105).</summary>
public enum ActivityAlertRule
{
    /// <summary>One person deleted many files or folders in a short time.</summary>
    MassDelete,

    /// <summary>One person was refused access many times in a short time.</summary>
    ManyDenied,

    /// <summary>Permissions or the owner of a file or folder were changed.</summary>
    PermissionChange,

    /// <summary>Someone cleared a file server's Security log.</summary>
    LogCleared,

    /// <summary>Events were gone from a Security log before Data Scope read them.</summary>
    EventsMissed,

    /// <summary>One person touched far more files and folders on a file server than on their busy days (D166).</summary>
    UnusualVolume,

    /// <summary>One person was active on a file server in hours they hadn't worked in before.</summary>
    UnusualHours,

    /// <summary>One person used folders holding sensitive data on a file server that they hadn't used before.</summary>
    UnusualPlaces,
}

public enum ActivityAlertSeverity
{
    High,
    Medium,
}

public enum ActivityAlertState
{
    Open,
    Acknowledged,
}

/// <summary>Alert thresholds (section <c>Activity:Alerts</c>). Counts are events by one person on one server.</summary>
public sealed class ActivityAlertOptions
{
    public const string Section = "Activity:Alerts";

    public bool Enabled { get; set; } = true;
    public int MassDeleteCount { get; set; } = 50;
    public int MassDeleteMinutes { get; set; } = 10;
    public int DeniedCount { get; set; } = 20;
    public int DeniedMinutes { get; set; } = 10;

    /// <summary>Permission changes by the same person within this many minutes of each other are one alert.</summary>
    public int PermissionChangeMinutes { get; set; } = 60;

    // Unusual behaviour (D166): each person against their own past, on each file server.
    public bool UnusualEnabled { get; set; } = true;

    /// <summary>Days before today that each person's normal activity is learned from.</summary>
    public int UnusualLearnDays { get; set; } = 28;

    /// <summary>Days with activity a person needs in that time before anything about them is called unusual.</summary>
    public int UnusualMinActiveDays { get; set; } = 5;

    /// <summary>A day is unusually busy at this many times their busy day, and at least <see cref="UnusualVolumeMinimum"/>.</summary>
    public int UnusualVolumeFactor { get; set; } = 4;

    public int UnusualVolumeMinimum { get; set; } = 200;

    /// <summary>Files and folders touched in hours a person never worked in before.</summary>
    public int UnusualHoursMinimum { get; set; } = 20;

    /// <summary>Folders holding sensitive data a person used for the first time today.</summary>
    public int UnusualPlacesCount { get; set; } = 3;

    /// <summary>Addresses emailed for new alerts, through the SMTP server under <c>Notifications</c>. Empty: no email.</summary>
    public string[] EmailTo { get; set; } = [];

    /// <summary>Also email Medium alerts (permission changes, refused access, missed events); High alerts are always emailed.</summary>
    public bool EmailMedium { get; set; }

    public void Validate()
    {
        foreach (var (name, value, min, max) in new[]
        {
            (nameof(MassDeleteCount), MassDeleteCount, 2, 1_000_000), (nameof(MassDeleteMinutes), MassDeleteMinutes, 1, 1440),
            (nameof(DeniedCount), DeniedCount, 2, 1_000_000), (nameof(DeniedMinutes), DeniedMinutes, 1, 1440),
            (nameof(PermissionChangeMinutes), PermissionChangeMinutes, 1, 1440),
            (nameof(UnusualLearnDays), UnusualLearnDays, 7, 90), (nameof(UnusualMinActiveDays), UnusualMinActiveDays, 2, 90),
            (nameof(UnusualVolumeFactor), UnusualVolumeFactor, 2, 100), (nameof(UnusualVolumeMinimum), UnusualVolumeMinimum, 10, 1_000_000),
            (nameof(UnusualHoursMinimum), UnusualHoursMinimum, 1, 1_000_000), (nameof(UnusualPlacesCount), UnusualPlacesCount, 1, 1_000),
        })
        {
            if (value < min || value > max)
            {
                throw new InvalidOperationException($"{Section}:{name} must be from {min} to {max}.");
            }
        }
        if (UnusualMinActiveDays > UnusualLearnDays)
        {
            throw new InvalidOperationException($"{Section}:{nameof(UnusualMinActiveDays)} can't be more than {nameof(UnusualLearnDays)}.");
        }
        if (EmailTo.Any(a => string.IsNullOrWhiteSpace(a) || a.Length > 254 || !a.Contains('@', StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"{Section}:{nameof(EmailTo)} must hold email addresses.");
        }
    }

    /// <summary>The rules that look for bursts of events, with their thresholds.</summary>
    public IReadOnlyList<BurstRule> BurstRules() =>
    [
        new(ActivityAlertRule.MassDelete, MassDeleteCount, TimeSpan.FromMinutes(MassDeleteMinutes)),
        new(ActivityAlertRule.ManyDenied, DeniedCount, TimeSpan.FromMinutes(DeniedMinutes)),
        new(ActivityAlertRule.PermissionChange, 1, TimeSpan.FromMinutes(PermissionChangeMinutes)),
        new(ActivityAlertRule.LogCleared, 1, TimeSpan.FromMinutes(1)),
    ];
}

/// <summary>
/// At least <c>Count</c> matching events by one person, each within <c>Window</c> of the first of them. With
/// <c>Distinct</c>, events are counted by their <see cref="AlertEvent.Key"/> instead: <c>Count</c> different things.
/// </summary>
public sealed record BurstRule(ActivityAlertRule Rule, int Count, TimeSpan Window, bool Distinct = false)
{
    public ActivityAlertSeverity Severity => ActivityAlertText.Severity(Rule);
}

/// <summary>One event as the alert rules see it. <c>Key</c> is what a distinct rule counts, such as the folder opened.</summary>
public sealed record AlertEvent(long Id, DateTimeOffset Time, string? PrincipalSid, string? PrincipalName, string? Key = null);

/// <summary>
/// A run of events by one person that reached a rule's threshold. <c>NewEvents</c> counts the ones not seen by an
/// earlier pass, so an alert that is already open can be extended without counting anything twice.
/// </summary>
public sealed record Burst(string? PrincipalSid, string? PrincipalName, DateTimeOffset First, DateTimeOffset Last, int Count, int NewEvents, long FirstEventId, long LastEventId);

/// <summary>
/// Finds bursts. Events are grouped by person; a burst starts when <c>Count</c> events fall within <c>Window</c>, and
/// carries on while each next event comes within <c>Window</c> of the one before. Only bursts with at least one new
/// event are returned. <c>earlier</c> holds events already seen, from at least one window before the first new one.
/// </summary>
public static class BurstDetector
{
    public static IReadOnlyList<Burst> Find(IEnumerable<AlertEvent> earlier, IEnumerable<AlertEvent> fresh, BurstRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var newIds = fresh.Select(e => e.Id).ToHashSet();
        var all = earlier.Concat(fresh).DistinctBy(e => e.Id);
        var bursts = new List<Burst>();
        foreach (var person in all.GroupBy(e => e.PrincipalSid ?? ""))
        {
            var events = person.OrderBy(e => e.Time).ThenBy(e => e.Id).ToList();
            var start = 0; // sliding window [start, i]
            var burstStart = -1; // index of a burst's first event, while one runs
            var keys = new Dictionary<string, int>(StringComparer.Ordinal); // for a distinct rule: events per key in the window
            for (var i = 0; i < events.Count; i++)
            {
                if (burstStart >= 0 && events[i].Time - events[i - 1].Time > rule.Window)
                {
                    Close(events, burstStart, i - 1, newIds, rule, bursts);
                    burstStart = -1;
                    start = i;
                    keys.Clear();
                }
                Count(keys, events[i], +1);
                while (events[i].Time - events[start].Time > rule.Window)
                {
                    Count(keys, events[start], -1);
                    start++;
                }
                if (burstStart < 0 && (rule.Distinct ? keys.Count : i - start + 1) >= rule.Count)
                {
                    burstStart = start;
                }
            }
            if (burstStart >= 0)
            {
                Close(events, burstStart, events.Count - 1, newIds, rule, bursts);
            }
        }
        return bursts.OrderBy(b => b.First).ToList();
    }

    private static void Count(Dictionary<string, int> keys, AlertEvent e, int by)
    {
        var key = e.Key ?? "";
        var n = keys.GetValueOrDefault(key) + by;
        if (n > 0)
        {
            keys[key] = n;
        }
        else
        {
            keys.Remove(key);
        }
    }

    private static void Close(List<AlertEvent> events, int from, int to, HashSet<long> newIds, BurstRule rule, List<Burst> bursts)
    {
        var run = events.GetRange(from, to - from + 1);
        if (!run.Any(e => newIds.Contains(e.Id)))
        {
            return;
        }
        // A distinct rule counts things, and only the ones the run first reached through a new event are new.
        var count = rule.Distinct ? run.Select(e => e.Key ?? "").Distinct(StringComparer.Ordinal).Count() : run.Count;
        var added = rule.Distinct
            ? run.GroupBy(e => e.Key ?? "", StringComparer.Ordinal).Count(g => newIds.Contains(g.First().Id))
            : run.Count(e => newIds.Contains(e.Id));
        var name = run.Select(e => e.PrincipalName).LastOrDefault(n => n is not null);
        bursts.Add(new Burst(run[0].PrincipalSid, name, run[0].Time, run[^1].Time, count, added, run.Min(e => e.Id), run.Max(e => e.Id)));
    }
}

/// <summary>Severity and plain words for each alert.</summary>
public static class ActivityAlertText
{
    /// <summary>Raised by comparing a person with their own past (D166) rather than by a fixed threshold.</summary>
    public static bool IsUnusual(ActivityAlertRule rule) => rule is ActivityAlertRule.UnusualVolume or ActivityAlertRule.UnusualHours
        or ActivityAlertRule.UnusualPlaces;

    /// <summary>The rule for an unusual finding on a file server.</summary>
    public static ActivityAlertRule Rule(UnusualKind kind) => kind switch
    {
        UnusualKind.Volume => ActivityAlertRule.UnusualVolume,
        UnusualKind.Hours => ActivityAlertRule.UnusualHours,
        _ => ActivityAlertRule.UnusualPlaces,
    };

    public static ActivityAlertSeverity Severity(ActivityAlertRule rule) => rule switch
    {
        ActivityAlertRule.MassDelete or ActivityAlertRule.LogCleared => ActivityAlertSeverity.High,
        _ => ActivityAlertSeverity.Medium,
    };

    public static string Title(ActivityAlertRule rule) => rule switch
    {
        ActivityAlertRule.MassDelete => "Many files deleted",
        ActivityAlertRule.ManyDenied => "Many refused attempts",
        ActivityAlertRule.PermissionChange => "Permissions changed",
        ActivityAlertRule.LogCleared => "Security log cleared",
        ActivityAlertRule.EventsMissed => "Events missed",
        ActivityAlertRule.UnusualVolume => "Unusually busy day",
        ActivityAlertRule.UnusualHours => "Active at unusual hours",
        ActivityAlertRule.UnusualPlaces => "Sensitive folders not used before",
        _ => rule.ToString(),
    };

    /// <summary>
    /// One sentence: who, what, how many, where and over how long. Never more than the events show. For unusual
    /// behaviour (D166), <paramref name="usual"/> and <paramref name="detail"/> say what was usual for them and which
    /// hours or folders were not.
    /// </summary>
    public static string Describe(ActivityAlertRule rule, string? who, int count, string hostname, DateTimeOffset first, DateTimeOffset last,
        int? usual = null, string? detail = null)
    {
        var person = string.IsNullOrWhiteSpace(who) ? "Someone" : who;
        var span = Span(last - first);
        return rule switch
        {
            ActivityAlertRule.MassDelete => $"{person} deleted {Plural(count, "file or folder", "files or folders")} on {hostname}{span}.",
            ActivityAlertRule.ManyDenied => $"{person} was refused access {Plural(count, "time", "times")} on {hostname}{span}.",
            ActivityAlertRule.PermissionChange => count == 1
                ? $"{person} changed permissions or the owner of something on {hostname}."
                : $"{person} changed permissions or owners {Plural(count, "time", "times")} on {hostname}{span}.",
            ActivityAlertRule.LogCleared => $"{person} cleared the Security log on {hostname}. Events from before the clear may be missing.",
            ActivityAlertRule.EventsMissed => count > 0
                ? $"{Plural(count, "log record", "log records")} left the Security log on {hostname} before Data Scope read them. Some may have been file events."
                : $"The Security log on {hostname} started again; events not yet read were lost.",
            ActivityAlertRule.UnusualVolume =>
                $"{person} touched {Plural(count, "file or folder", "files and folders")} on {hostname} in one day, far more than on their usual busy day (about {Number(usual ?? 0)}).",
            ActivityAlertRule.UnusualHours =>
                $"{person} touched {Plural(count, "file or folder", "files and folders")} on {hostname} from {detail}, hours they hadn't worked in before.",
            ActivityAlertRule.UnusualPlaces =>
                $"{person} used {Plural(count, "folder", "folders")} holding sensitive data on {hostname} that they hadn't used before: {detail}.",
            _ => $"{person}: {count} events on {hostname}.",
        };
    }

    private static string Span(TimeSpan span) => span switch
    {
        _ when span < TimeSpan.FromMinutes(1) => span <= TimeSpan.Zero ? "" : " within a minute",
        _ when span < TimeSpan.FromHours(2) => string.Create(CultureInfo.InvariantCulture, $" in {Math.Ceiling(span.TotalMinutes):0} minutes"),
        _ => string.Create(CultureInfo.InvariantCulture, $" over {Math.Round(span.TotalHours):0} hours"),
    };

    private static string Number(int n) => n.ToString("N0", CultureInfo.InvariantCulture);

    private static string Plural(int n, string one, string many) =>
        n == 1 ? $"1 {one}" : string.Create(CultureInfo.InvariantCulture, $"{n:N0} {many}");
}
