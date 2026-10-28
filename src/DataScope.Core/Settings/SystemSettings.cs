using System.Globalization;
using DataScope.Core.ScanJobs;

namespace DataScope.Core.Settings;

/// <summary>What a setting holds, which decides how it is checked and shown.</summary>
public enum SettingKind
{
    /// <summary>A whole number between a minimum and a maximum; 0 may also be allowed with its own meaning.</summary>
    Whole,
    Toggle,
    Text,

    /// <summary>An https:// address.</summary>
    Url,
    Email,
    EmailList,

    /// <summary>One of a fixed list of values.</summary>
    Choice,
}

public sealed record SettingChoice(string Value, string Label);

public sealed record SettingGroup(string Id, string Title, string Description);

/// <summary>
/// One setting an administrator may change on the System settings screen (D123). <see cref="Key"/> is the configuration
/// path the services already read, so a value saved here overrides the same key in datascope.json and appsettings.json.
/// </summary>
public sealed record SettingDefinition
{
    public required string Key { get; init; }
    public required string Group { get; init; }
    public required string Label { get; init; }
    public required string Help { get; init; }
    public required SettingKind Kind { get; init; }

    /// <summary>The built-in value, as configuration text; null when the built-in is "not set".</summary>
    public string? Default { get; init; }

    public IReadOnlyList<string> DefaultList { get; init; } = [];
    public long Min { get; init; }
    public long Max { get; init; } = long.MaxValue;

    /// <summary>For whole numbers: what 0 means when it is allowed outside <see cref="Min"/>, such as "No limit".</summary>
    public string? ZeroLabel { get; init; }

    /// <summary>The value may be left empty.</summary>
    public bool Optional { get; init; }

    public string? Unit { get; init; }
    public IReadOnlyList<SettingChoice> Choices { get; init; } = [];

    /// <summary>When a change takes effect, in plain words.</summary>
    public required string Applies { get; init; }
}

/// <summary>
/// The settings administrators manage from the web UI, with their limits (D123). Security-sensitive and internal
/// configuration stays out on purpose: who has which role, the database connection, the HTTPS certificate, SMTP
/// sign-in and TLS, the content readers' safety limits, and scanner internals stay in datascope.json and the setup tool.
/// </summary>
public static class SystemSettings
{
    public const int MaxEmailRecipients = 50;
    public const int MaxTextLength = 253;

    /// <summary>The key cross-field problems with the schedule for new servers are reported under.</summary>
    public const string NewServerSchedule = "Schedule:NewServer";

    private const string Next = "Applies from the next scan or the next time a scan resumes.";
    private const string Minute = "Applies within a minute.";
    private const string Now = "Applies straight away.";

    public static readonly IReadOnlyList<SettingGroup> Groups =
    [
        new("scanning", "Scanning", "How hard scans may work a file server. Leave at no limit unless a server slows down during scans."),
        new("new-servers", "New servers", "The schedule a file server gets when it is added. Change an existing server's schedule on Servers & scans."),
        new("activity", "File activity and alerts", "How often Security logs are read and what raises an alert."),
        new("email", "Email", "How review emails, reminders and alerts are sent, through your own mail server. Nothing is sent anywhere else."),
        new("cleanup", "Stale and duplicate data", "What the Stale & duplicates screen, its reports and the old sensitive files risk count."),
        new("retention", "History kept", "How long history Data Scope no longer shows as current is kept. The audit log, access reviews, decisions and classification overrides are evidence and are always kept."),
        new("directory", "Active Directory", "How often users and groups are read from Active Directory."),
    ];

    private static readonly SettingChoice[] Weekdays =
        [.. new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday }
            .Select(d => new SettingChoice(d.ToString(), d.ToString()))];

    private static string Days(int days) => days.ToString(CultureInfo.InvariantCulture);

    public static readonly IReadOnlyList<SettingDefinition> All =
    [
        // Scanning (D94).
        new()
        {
            Key = "Scanner:MaxFoldersPerSecond", Group = "scanning", Kind = SettingKind.Whole, Default = "0", Min = 1, Max = 100_000, ZeroLabel = "No limit",
            Unit = "folders a second", Label = "Folders listed per second",
            Help = "Slows the folder walk on a busy file server. Each scan lists at most this many folders a second.", Applies = Next,
        },
        new()
        {
            Key = "Scanner:MaxFilesReadPerSecond", Group = "scanning", Kind = SettingKind.Whole, Default = "0", Min = 1, Max = 10_000, ZeroLabel = "No limit",
            Unit = "files a second", Label = "Files read per second",
            Help = "Slows the reading of new and changed files for sensitive data. Each scan opens at most this many files a second.", Applies = Next,
        },

        // The schedule for new servers (D116, D121).
        new()
        {
            Key = "Schedule:NewServer:Frequency", Group = "new-servers", Kind = SettingKind.Choice, Default = nameof(ScanFrequency.Daily),
            Choices = [new(nameof(ScanFrequency.Hourly), "Hourly"), new(nameof(ScanFrequency.Daily), "Daily"), new(nameof(ScanFrequency.Weekly), "Weekly"), new(nameof(ScanFrequency.Off), "By hand")],
            Label = "How often", Help = "How often a newly added server is scanned.", Applies = Now,
        },
        new()
        {
            Key = "Schedule:NewServer:StartHour", Group = "new-servers", Kind = SettingKind.Whole, Default = "1", Min = 0, Max = 23,
            Label = "Start hour", Help = "The hour scans start, in the Data Scope server's time zone.", Applies = Now,
        },
        new()
        {
            Key = "Schedule:NewServer:StartMinute", Group = "new-servers", Kind = SettingKind.Whole, Default = "0", Min = 0, Max = 59,
            Label = "Start minute", Help = "The minute past the hour scans start.", Applies = Now,
        },
        new()
        {
            Key = "Schedule:NewServer:EveryHours", Group = "new-servers", Kind = SettingKind.Whole, Optional = true, Min = 1, Max = 12,
            Label = "Every", Unit = "hours", Help = "For hourly scans: 1, 2, 3, 4, 6, 8 or 12 hours apart.", Applies = Now,
        },
        new()
        {
            Key = "Schedule:NewServer:Day", Group = "new-servers", Kind = SettingKind.Choice, Optional = true, Choices = Weekdays,
            Label = "Day", Help = "For weekly scans: the day.", Applies = Now,
        },
        new()
        {
            Key = "Schedule:NewServer:StopHour", Group = "new-servers", Kind = SettingKind.Whole, Default = "7", Optional = true, Min = 0, Max = 23,
            Label = "Pause at", Help = "A scheduled scan still running at this hour pauses and carries on at the next scheduled time. Empty lets it run to the end.",
            Applies = Now,
        },
        new()
        {
            Key = "Schedule:NewServer:FullScan", Group = "new-servers", Kind = SettingKind.Choice, Default = nameof(FullScanFrequency.Weekly),
            Choices = [new(nameof(FullScanFrequency.Weekly), "Weekly"), new(nameof(FullScanFrequency.Monthly), "Monthly"), new(nameof(FullScanFrequency.Off), "By hand")],
            Label = "Full scans", Help = "How often a newly added server gets a full scan, which reads every file again with more time per file (D130).", Applies = Now,
        },
        new()
        {
            Key = "Schedule:NewServer:FullScanDay", Group = "new-servers", Kind = SettingKind.Choice, Default = nameof(DayOfWeek.Saturday), Optional = true, Choices = Weekdays,
            Label = "Full scan day", Help = "The day of the full scan: each week, or the first one of each month. It starts at the scan start time.", Applies = Now,
        },

        // File activity (D96) and alerts (D105).
        new()
        {
            Key = "Activity:PollSeconds", Group = "activity", Kind = SettingKind.Whole, Default = "60", Min = 15, Max = 3600, Unit = "seconds",
            Label = "Read Security logs every", Help = "How often each server's Security log is read for new file events.", Applies = Minute,
        },
        new()
        {
            Key = "Activity:Alerts:Enabled", Group = "activity", Kind = SettingKind.Toggle, Default = "true",
            Label = "Raise alerts", Help = "Look for mass deletes, many refused attempts and permission changes in collected events.", Applies = Minute,
        },
        new()
        {
            Key = "Activity:Alerts:MassDeleteCount", Group = "activity", Kind = SettingKind.Whole, Default = "50", Min = 2, Max = 1_000_000, Unit = "files",
            Label = "Mass delete alert: files", Help = "A High alert when one person deletes this many files within the time below.", Applies = Minute,
        },
        new()
        {
            Key = "Activity:Alerts:MassDeleteMinutes", Group = "activity", Kind = SettingKind.Whole, Default = "10", Min = 1, Max = 1440, Unit = "minutes",
            Label = "Mass delete alert: within", Help = "The time the deletes must fall within.", Applies = Minute,
        },
        new()
        {
            Key = "Activity:Alerts:DeniedCount", Group = "activity", Kind = SettingKind.Whole, Default = "20", Min = 2, Max = 1_000_000, Unit = "attempts",
            Label = "Refused access alert: attempts", Help = "A Medium alert when one person is refused access this many times within the time below.", Applies = Minute,
        },
        new()
        {
            Key = "Activity:Alerts:DeniedMinutes", Group = "activity", Kind = SettingKind.Whole, Default = "10", Min = 1, Max = 1440, Unit = "minutes",
            Label = "Refused access alert: within", Help = "The time the refused attempts must fall within.", Applies = Minute,
        },
        new()
        {
            Key = "Activity:Alerts:PermissionChangeMinutes", Group = "activity", Kind = SettingKind.Whole, Default = "60", Min = 1, Max = 1440, Unit = "minutes",
            Label = "Group permission changes within", Help = "Permission changes by the same person this close together make one alert.", Applies = Minute,
        },
        // Unusual behaviour (D166).
        new()
        {
            Key = "Activity:Alerts:UnusualEnabled", Group = "activity", Kind = SettingKind.Toggle, Default = "true",
            Label = "Spot unusual behaviour",
            Help = "Learn each person's normal file activity on each file server, and raise a Medium alert for a day far busier than usual, work at hours they never work, or folders holding sensitive data they never used. Each person is compared only with their own past.",
            Applies = Minute,
        },
        new()
        {
            Key = "Activity:Alerts:UnusualLearnDays", Group = "activity", Kind = SettingKind.Whole, Default = "28", Min = 7, Max = 90, Unit = "days",
            Label = "Unusual behaviour: learn from", Help = "The days before today that each person's normal activity is learned from, again each day.", Applies = "Applies from tomorrow's learning.",
        },
        new()
        {
            Key = "Activity:Alerts:UnusualMinActiveDays", Group = "activity", Kind = SettingKind.Whole, Default = "5", Min = 2, Max = 90, Unit = "days",
            Label = "Unusual behaviour: days needed", Help = "Days with activity a person needs in that time before anything they do is called unusual. Until then they are still being learned.", Applies = "Applies from tomorrow's learning.",
        },
        new()
        {
            Key = "Activity:Alerts:UnusualVolumeFactor", Group = "activity", Kind = SettingKind.Whole, Default = "4", Min = 2, Max = 100, Unit = "times",
            Label = "Busy day alert: times usual", Help = "A day is unusually busy when a person touches this many times the files and folders of their busy days (nine in ten of their days hold fewer).", Applies = Minute,
        },
        new()
        {
            Key = "Activity:Alerts:UnusualVolumeMinimum", Group = "activity", Kind = SettingKind.Whole, Default = "200", Min = 10, Max = 1_000_000, Unit = "files",
            Label = "Busy day alert: at least", Help = "And at least this many files and folders, so someone who normally touches a handful isn't flagged for a few more.", Applies = Minute,
        },
        new()
        {
            Key = "Activity:Alerts:UnusualHoursMinimum", Group = "activity", Kind = SettingKind.Whole, Default = "20", Min = 1, Max = 1_000_000, Unit = "files",
            Label = "Unusual hours alert: files", Help = "Files and folders a person touches in hours of the day they never worked in while being learned. Someone active in 20 or more hours of the day isn't judged on hours.", Applies = Minute,
        },
        new()
        {
            Key = "Activity:Alerts:UnusualPlacesCount", Group = "activity", Kind = SettingKind.Whole, Default = "3", Min = 1, Max = 1_000, Unit = "folders",
            Label = "New sensitive folders alert: folders", Help = "Top-level folders holding Restricted or Confidential files that a person uses in one day without having used them while being learned.", Applies = Minute,
        },
        new()
        {
            Key = "Activity:Alerts:EmailTo", Group = "activity", Kind = SettingKind.EmailList, Optional = true,
            Label = "Email alerts to", Help = "Addresses told about new High alerts, through the mail server below. Leave empty for no alert emails.", Applies = Minute,
        },
        new()
        {
            Key = "Activity:Alerts:EmailMedium", Group = "activity", Kind = SettingKind.Toggle, Default = "false",
            Label = "Also email Medium alerts", Help = "Permission changes, refused access, missed events and unusual behaviour, as well as High alerts.", Applies = Minute,
        },

        // Email.
        new()
        {
            Key = "Notifications:PublicUrl", Group = "email", Kind = SettingKind.Url, Optional = true,
            Label = "Data Scope's address", Help = "The https:// address people open Data Scope at, put in emails. Empty uses https://this-server/.", Applies = Minute,
        },
        new()
        {
            Key = "Notifications:Smtp:Host", Group = "email", Kind = SettingKind.Text, Optional = true, Max = MaxTextLength,
            Label = "Mail server", Help = "Your SMTP relay's name, for example mail.yourdomain.local. Empty turns email off; reviews still wait in Data Scope.", Applies = Minute,
        },
        new()
        {
            Key = "Notifications:Smtp:Port", Group = "email", Kind = SettingKind.Whole, Default = "587", Min = 1, Max = 65_535,
            Label = "Port", Help = "Usually 587 or 25. The connection always uses STARTTLS.", Applies = Minute,
        },
        new()
        {
            Key = "Notifications:Smtp:From", Group = "email", Kind = SettingKind.Email, Optional = true,
            Label = "From address", Help = "The sender on Data Scope's emails, for example datascope@yourdomain.local.", Applies = Minute,
        },
        new()
        {
            Key = "Notifications:ReminderMinutes", Group = "email", Kind = SettingKind.Whole, Default = "15", Min = 5, Max = 1440, Unit = "minutes",
            Label = "Check for review reminders every", Help = "How often due, overdue and escalated review reminders are worked out.", Applies = Minute,
        },

        // Stale and duplicate data (D119, D122).
        new()
        {
            Key = "Cleanup:StaleAfterYears", Group = "cleanup", Kind = SettingKind.Whole, Default = "3", Min = 1, Max = 30, Unit = "years",
            Label = "Stale after", Help = "Files nobody has changed for this long are stale. The screen can still look at other thresholds; this is its default, and what reports and the risk use.",
            Applies = Now,
        },
        new()
        {
            Key = "Cleanup:MinDuplicateBytes", Group = "cleanup", Kind = SettingKind.Choice, Default = "1024",
            Choices = [new("1024", "1 KB"), new("65536", "64 KB"), new("1048576", "1 MB"), new("10485760", "10 MB"), new("104857600", "100 MB")],
            Label = "Smallest file counted as a copy", Help = "Smaller copies cost almost nothing and are mostly shortcuts and settings.", Applies = Now,
        },

        // History kept.
        Retention("Retention:RemovedItemsDays", 365, "Files and folders removed from a share", "Their last known details, after they disappear."),
        Retention("Retention:PermissionChangesDays", 730, "Permission changes", "The record of each permission change seen on a folder."),
        Retention("Retention:OldScanGapsDays", 365, "Old scan gaps", "What older scans could not read, once a newer scan replaced them."),
        Retention("Retention:NotificationsDays", 180, "Sent emails", "The record of sent, skipped and failed emails."),
        Retention("Retention:ScanHistoryDays", 1095, "Scan history behind trends", "Per-scan totals behind the trend charts. Each share's latest is always kept."),
        Retention("Retention:ActivityDays", 365, "File activity", "Collected file events and the record of missed events. Alerts are kept."),
        new()
        {
            Key = "Retention:IntervalHours", Group = "retention", Kind = SettingKind.Whole, Default = "24", Min = 1, Max = 168, Unit = "hours",
            Label = "Clear old history every", Help = "How often old history is deleted, in small batches.", Applies = "Applies after the next clear-up.",
        },

        // Active Directory.
        new()
        {
            Key = "Directory:SyncHours", Group = "directory", Kind = SettingKind.Whole, Default = "24", Min = 1, Max = 168, Unit = "hours",
            Label = "Read Active Directory every", Help = "How often users, groups and memberships are read. Sync now on Servers & scans reads it at once.",
            Applies = "Applies after the next sync.",
        },
    ];

    private static SettingDefinition Retention(string key, int days, string label, string help) => new()
    {
        Key = key,
        Group = "retention",
        Kind = SettingKind.Whole,
        Default = Days(days),
        Min = 30,
        Max = 36_500,
        ZeroLabel = "Keep forever",
        Unit = "days",
        Label = label,
        Help = help,
        Applies = "Applies at the next clear-up.",
    };

    private static readonly Dictionary<string, SettingDefinition> ByKey = All.ToDictionary(d => d.Key, StringComparer.OrdinalIgnoreCase);

    public static SettingDefinition? Find(string key) => ByKey.GetValueOrDefault(key);

    /// <summary>
    /// Why a value can't be saved, or null when it can. A scalar is configuration text (null or empty clears an optional
    /// setting); a list is every entry.
    /// </summary>
    public static string? Problem(SettingDefinition d, string? text, IReadOnlyList<string>? list = null)
    {
        if (d.Kind == SettingKind.EmailList)
        {
            var items = list ?? [];
            if (items.Count > MaxEmailRecipients)
            {
                return $"Add at most {MaxEmailRecipients} addresses.";
            }
            if (items.FirstOrDefault(a => EmailProblem(a) is not null) is { } bad)
            {
                return $"\"{Clip(bad)}\" is not an email address.";
            }
            return items.Distinct(StringComparer.OrdinalIgnoreCase).Count() == items.Count ? null : "Each address only once.";
        }
        if (string.IsNullOrEmpty(text))
        {
            return d.Optional ? null : "Fill this in.";
        }
        if (text.Length > MaxTextLength || text.Any(char.IsControl) || text != text.Trim())
        {
            return "Use plain text without line breaks or spaces at either end.";
        }
        switch (d.Kind)
        {
            case SettingKind.Whole:
                if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                {
                    return "Enter a whole number.";
                }
                if (n == 0 && d.ZeroLabel is not null)
                {
                    return null;
                }
                return n < d.Min || n > d.Max
                    ? $"Enter {d.Min.ToString("N0", CultureInfo.InvariantCulture)} to {d.Max.ToString("N0", CultureInfo.InvariantCulture)}{(d.ZeroLabel is null ? "" : $", or 0 for {d.ZeroLabel.ToLowerInvariant()}")}."
                    : null;
            case SettingKind.Toggle:
                return text is "true" or "false" ? null : "Use on or off.";
            case SettingKind.Choice:
                return d.Choices.Any(c => c.Value == text) ? null : "Choose one of the options.";
            case SettingKind.Email:
                return EmailProblem(text);
            case SettingKind.Url:
                return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo)
                    && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
                    ? null
                    : "Enter an https:// address, for example https://ds01.yourdomain.local/.";
            case SettingKind.Text:
                return text.Any(c => char.IsWhiteSpace(c) || c is '/' or '\\' or '@' or ':') ? "Enter a server name, for example mail.yourdomain.local." : null;
            default:
                return "This setting can't be changed here.";
        }
    }

    /// <summary>
    /// Problems between settings, given every setting's effective value (null when not set, so the default applies).
    /// Keyed by the setting, or by <see cref="NewServerSchedule"/> for the schedule as a whole.
    /// </summary>
    public static Dictionary<string, string> CrossProblems(Func<string, string?> effective)
    {
        var problems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if ((ScanSchedule.Problem(NewServerScheduleFrom(effective)) ?? ScanSchedule.Problem(NewServerFullScanFrom(effective))) is { } schedule)
        {
            problems[NewServerSchedule] = schedule;
        }
        if (!string.IsNullOrEmpty(effective("Notifications:Smtp:Host")) && string.IsNullOrEmpty(effective("Notifications:Smtp:From")))
        {
            problems["Notifications:Smtp:From"] = "Add the address Data Scope's emails come from, or empty the mail server to turn email off.";
        }
        int Whole(string key, int fallback) => int.TryParse(effective(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;
        if (Whole("Activity:Alerts:UnusualMinActiveDays", 5) > Whole("Activity:Alerts:UnusualLearnDays", 28))
        {
            problems["Activity:Alerts:UnusualMinActiveDays"] = "Choose no more days than unusual behaviour learns from.";
        }
        return problems;
    }

    /// <summary>
    /// The schedule a newly added server gets, from the <c>Schedule:NewServer</c> settings. A key that is not set uses
    /// its default; an empty optional key means none.
    /// </summary>
    public static ScanScheduleSettings NewServerScheduleFrom(Func<string, string?> get)
    {
        string? Value(string name)
        {
            var key = $"{NewServerSchedule}:{name}";
            return get(key) ?? Find(key)!.Default;
        }
        int? Number(string name) => int.TryParse(Value(name), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
        var frequency = Enum.TryParse<ScanFrequency>(Value("Frequency"), out var f) && Enum.IsDefined(f) ? f : (ScanFrequency)(-1);
        var day = Enum.TryParse<DayOfWeek>(Value("Day"), out var dd) && Enum.IsDefined(dd) ? dd : (DayOfWeek?)null;
        return new ScanScheduleSettings(frequency, Number("StartHour") ?? -1, frequency == ScanFrequency.Weekly ? day : null, Number("StopHour"),
            Number("StartMinute") ?? -1, frequency == ScanFrequency.Hourly ? Number("EveryHours") : null);
    }

    /// <summary>The full scans a newly added server gets (D130), from the <c>Schedule:NewServer</c> settings.</summary>
    public static FullScanSettings NewServerFullScanFrom(Func<string, string?> get)
    {
        string? Value(string name)
        {
            var key = $"{NewServerSchedule}:{name}";
            return get(key) ?? Find(key)!.Default;
        }
        var frequency = Enum.TryParse<FullScanFrequency>(Value("FullScan"), out var f) && Enum.IsDefined(f) ? f : (FullScanFrequency)(-1);
        var day = Enum.TryParse<DayOfWeek>(Value("FullScanDay"), out var d) && Enum.IsDefined(d) ? d : (DayOfWeek?)null;
        return new FullScanSettings(frequency, frequency == FullScanFrequency.Off ? null : day);
    }

    /// <summary>One plain email address, as the email settings accept it.</summary>
    public static bool IsMailAddress(string? address) => EmailProblem(address) is null;

    private static string? EmailProblem(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || address.Length > 254 || address != address.Trim() || address.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '<' or '>' or ',' or ';' or '"'))
        {
            return "Enter an email address, for example datascope@yourdomain.local.";
        }
        var at = address.IndexOf('@', StringComparison.Ordinal);
        return at > 0 && at == address.LastIndexOf('@') && at < address.Length - 1 && address[(at + 1)..].Contains('.', StringComparison.Ordinal)
            ? null
            : "Enter an email address, for example datascope@yourdomain.local.";
    }

    private static string Clip(string s) => s.Length <= 60 ? s : s[..60] + "…";
}
