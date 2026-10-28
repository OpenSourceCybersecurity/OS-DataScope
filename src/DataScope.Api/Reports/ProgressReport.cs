using System.Globalization;
using DataScope.Persistence;
using DataScope.Core.Security;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Reports;

/// <summary>
/// Progress over time (D165): the posture day by day over a period, with what changed from its first day to its last,
/// for a board pack or an auditor. Counts only; no names, paths or contents.
/// </summary>
internal sealed class ProgressReport : IReport
{
    public ReportInfo Info { get; } = new("progress", "Progress over time",
        "Open risks by severity, sensitive files, how many are open to everyone, files not read and shares with no owner, day by day, with the change over the period.",
        Policies.ReadPosture, Period: true);

    public async Task<ReportDocument> BuildAsync(ReportContext ctx, DirectoryStore directory, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(ctx.Now.UtcDateTime);
        var from = today.AddDays(1 - ctx.Days);
        // Like the Progress page, the report includes today even before the background check has measured it.
        if (ctx.Progress is not null && !await ctx.Db.PostureSnapshots.AnyAsync(s => s.Day == today, ct))
        {
            await ctx.Progress.RecordAsync(ct);
        }
        var days = await ctx.Db.PostureSnapshots.AsNoTracking().Where(s => s.Day >= from).OrderByDescending(s => s.Day).Take(ctx.RowLimit + 1).ToListAsync(ct);
        var keptSince = await ctx.Db.PostureSnapshots.MinAsync(s => (DateOnly?)s.Day, ct);

        // The latest scan's coverage first, as every report states it, then the period.
        var facts = await ctx.CommonFactsAsync(ct);
        facts.Add(new("Period", $"{from:yyyy-MM-dd} to {today:yyyy-MM-dd} ({ctx.Days:N0} days)"));
        facts.Add(new("Kept since", keptSince is { } k ? $"{k:yyyy-MM-dd}: Data Scope keeps one row a day from the day it was upgraded to keep them" : "Nothing kept yet"));
        if (ctx.Scope.ServerId is not null || ctx.Scope.ShareId is not null)
        {
            facts.Add(new("Scope", "Progress is kept for everything Data Scope scans, so this covers every server and share, not only the one chosen."));
        }
        if (days.Count >= 2)
        {
            var last = days[0];
            var first = days[^1];
            var change = string.Join("; ", Changes(first, last));
            facts.Add(new("Change", char.ToUpperInvariant(change[0]) + change[1..]));
        }
        if (days.Any(d => d.RisksLimited))
        {
            facts.Add(new("Risk checks", "On days marked, the risk check stopped early somewhere, so that day's risk counts may be low."));
        }

        var rows = days.Select(d => new object?[]
        {
            d.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), d.TakenAt, d.LastScanAt, d.RisksHigh, d.RisksMedium, d.RisksLow, d.RisksAccepted, d.RisksSnoozed,
            d.RisksLimited ? "Stopped early" : "Complete", d.Restricted, d.Confidential, d.SensitiveOpen,
            d.Files, d.FilesNotRead, d.SharesUnowned,
        }).ToList();
        return ctx.Document(Info, facts,
        [
            "Each day shows the last time Data Scope measured it that day: after the day's last scan, directory sync or change. Days on which Data Scope was not running have no row.",
            "Counts follow what the scans found. A new share scanned for the first time raises them; that is new coverage, not new problems. A file not read is a gap, not clean.",
            "Open risks exclude risks accepted or snoozed that day. Risk rules change with Data Scope versions, so a count can move after an upgrade without anything on the file servers changing.",
            "Open to everyone counts sensitive files on file shares any domain user can open.",
        ],
        ["Day", "Measured", "Newest scan", "Open high risks", "Open medium risks", "Open low risks", "Accepted risks", "Snoozed risks", "Risk check",
         "Restricted files", "Confidential files", "Sensitive files open to everyone", "Files", "Files not read", "Shares with no owner"],
        rows);
    }

    /// <summary>Plain-language changes from the first day to the last, the ones that moved only.</summary>
    internal static List<string> Changes(PostureSnapshot first, PostureSnapshot last)
    {
        var changes = new List<string>();
        void Add(string what, long before, long after)
        {
            if (before != after)
            {
                changes.Add($"{what} {before:N0} to {after:N0}");
            }
        }
        Add("open high risks", first.RisksHigh, last.RisksHigh);
        Add("open medium risks", first.RisksMedium, last.RisksMedium);
        Add("open low risks", first.RisksLow, last.RisksLow);
        Add("sensitive files open to everyone", first.SensitiveOpen, last.SensitiveOpen);
        Add("files not read", first.FilesNotRead, last.FilesNotRead);
        Add("shares with no owner", first.SharesUnowned, last.SharesUnowned);
        return changes.Count == 0 ? ["No change"] : changes;
    }
}
