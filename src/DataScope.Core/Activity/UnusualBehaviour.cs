using System.Globalization;
using System.Numerics;

namespace DataScope.Core.Activity;

/// <summary>One person's activity on one local day, as the learning sees it (D166).</summary>
/// <param name="Items">Different files and folders touched that day.</param>
/// <param name="Hours">A bit for each local hour (0 to 23) with any activity.</param>
public sealed record ActivityDay(DateOnly Day, int Items, int Hours);

/// <summary>
/// What is normal for one person on one source, learned from the days before today (D166): on how many days they were
/// active, how much a busy day holds, the hours they work and the places (a share's or library's top-level folders)
/// they use.
/// </summary>
/// <param name="BusyDay">Files and folders touched on a busy day: nine in ten of their active days hold no more.</param>
public sealed record BehaviourBaseline(int ActiveDays, int BusyDay, int Hours, IReadOnlySet<string> Places)
{
    public int ActiveHours => BitOperations.PopCount((uint)Hours);

    public bool WorksAt(int hour) => (Hours & (1 << hour)) != 0;
}

/// <summary>One person's activity so far today (D166).</summary>
/// <param name="Items">Different files and folders touched today.</param>
/// <param name="ItemsByHour">Different files and folders touched in each local hour.</param>
/// <param name="Places">The places used today, with how many files and folders in each.</param>
public sealed record ActivitySoFar(int Items, IReadOnlyDictionary<int, int> ItemsByHour, IReadOnlyDictionary<string, int> Places);

/// <summary>Something unusual for this person today, with what is usual for them.</summary>
/// <param name="Count">Files and folders today (busy day, unusual hours), or new sensitive places.</param>
/// <param name="Usual">A busy day's files and folders, the number of hours they usually work in, or the places they used.</param>
/// <param name="Hours">For unusual hours: the hours, in order.</param>
/// <param name="Places">For new places: the places, most used first.</param>
public sealed record UnusualFinding(UnusualKind Kind, int Count, int Usual, IReadOnlyList<int> Hours, IReadOnlyList<string> Places);

public enum UnusualKind
{
    /// <summary>Far more files and folders than on their busy days.</summary>
    Volume,

    /// <summary>Active in hours they never worked in while Data Scope was learning.</summary>
    Hours,

    /// <summary>Using folders that hold sensitive data, which they hadn't used while Data Scope was learning.</summary>
    Places,
}

/// <summary>
/// Learns each person's normal file activity and judges today against it (D166). Every judgement compares a person with
/// their own past only, needs enough of that past, and says what was usual, so an alert is a lead to check rather than a
/// verdict. Pure, so the arithmetic is tested on its own.
/// </summary>
public static class UnusualBehaviour
{
    /// <summary>Places remembered per person; a person using more is judged on the first ones only.</summary>
    public const int MaxPlaces = 2_000;

    /// <summary>Someone active in at least this many hours of the day keeps no "unusual" hours to judge.</summary>
    public const int MaxHoursJudged = 20;

    public static BehaviourBaseline Learn(IEnumerable<ActivityDay> days, IEnumerable<string> places)
    {
        var active = days.Where(d => d.Items > 0).ToList();
        var sorted = active.Select(d => d.Items).Order().ToList();
        // The nearest rank at the 90th percentile: one unusually big day in a month doesn't make every day look normal.
        var busy = sorted.Count == 0 ? 0 : sorted[Math.Max(0, (int)Math.Ceiling(sorted.Count * 0.9) - 1)];
        var hours = active.Aggregate(0, (mask, d) => mask | d.Hours);
        return new BehaviourBaseline(active.Count, busy, hours, places.Take(MaxPlaces).ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>
    /// What is unusual about today. Nothing for someone with fewer active days than the options ask: Data Scope is
    /// still learning them. <paramref name="sensitive"/> says whether a place holds Restricted or Confidential files.
    /// </summary>
    public static IReadOnlyList<UnusualFinding> Judge(BehaviourBaseline usual, ActivitySoFar today, ActivityAlertOptions options, Func<string, bool> sensitive)
    {
        ArgumentNullException.ThrowIfNull(usual);
        ArgumentNullException.ThrowIfNull(today);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sensitive);
        var found = new List<UnusualFinding>();
        if (usual.ActiveDays < options.UnusualMinActiveDays || today.Items == 0)
        {
            return found;
        }

        if (today.Items >= VolumeThreshold(usual, options))
        {
            found.Add(new UnusualFinding(UnusualKind.Volume, today.Items, usual.BusyDay, [], []));
        }

        if (usual.ActiveHours < MaxHoursJudged)
        {
            var odd = today.ItemsByHour.Where(h => h.Value > 0 && !usual.WorksAt(h.Key)).OrderBy(h => h.Key).ToList();
            var items = odd.Sum(h => h.Value);
            if (items >= options.UnusualHoursMinimum)
            {
                found.Add(new UnusualFinding(UnusualKind.Hours, items, usual.ActiveHours, [.. odd.Select(h => h.Key)], []));
            }
        }

        var fresh = today.Places.Where(p => !usual.Places.Contains(p.Key) && sensitive(p.Key))
            .OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key).ToList();
        if (fresh.Count >= options.UnusualPlacesCount)
        {
            found.Add(new UnusualFinding(UnusualKind.Places, fresh.Count, usual.Places.Count, [], fresh));
        }
        return found;
    }

    /// <summary>How many files and folders make a day unusually busy for this person.</summary>
    public static int VolumeThreshold(BehaviourBaseline usual, ActivityAlertOptions options) =>
        (int)Math.Min(int.MaxValue, Math.Max(options.UnusualVolumeMinimum, (long)usual.BusyDay * options.UnusualVolumeFactor));

    /// <summary>"02:00 to 04:00" style ranges of local hours, each range ending when its last hour does.</summary>
    public static string HoursText(IEnumerable<int> hours)
    {
        var sorted = hours.Distinct().Order().ToList();
        var ranges = new List<string>();
        for (var i = 0; i < sorted.Count; i++)
        {
            var start = sorted[i];
            while (i + 1 < sorted.Count && sorted[i + 1] == sorted[i] + 1)
            {
                i++;
            }
            ranges.Add(string.Create(CultureInfo.InvariantCulture, $"{start:00}:00 to {(sorted[i] + 1) % 24:00}:00"));
        }
        return ranges.Count switch
        {
            0 => "",
            1 => ranges[0],
            _ => string.Join(", ", ranges[..^1]) + " and " + ranges[^1],
        };
    }
}
