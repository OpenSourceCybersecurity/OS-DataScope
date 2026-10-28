using DataScope.Api.Cleanup;
using DataScope.Core.Security;
using DataScope.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Reports;

/// <summary>Files nobody has changed or, where file activity is collected, opened in years (D119).</summary>
internal sealed class StaleDataReport : IReport
{
    public ReportInfo Info { get; } = new("stale-data", "Stale data",
        "Files nobody has changed in years, largest first, with their sensitivity label: candidates to archive or delete under your retention policy.",
        Policies.ReadPosture);

    public async Task<ReportDocument> BuildAsync(ReportContext ctx, DirectoryStore directory, CancellationToken ct)
    {
        var cleanup = ctx.Cleanup ?? throw new InvalidOperationException("The stale data report needs the cleanup service.");
        var years = cleanup.Options.StaleAfterYears;
        var cutoff = cleanup.Cutoff(years);
        var rows = await cleanup.Stale(ctx.Scope.ServerId, ctx.Scope.ShareId, cutoff)
            .OrderByDescending(f => f.Size).ThenBy(f => f.Changed).ThenBy(f => f.Id)
            .Take(ctx.RowLimit + 1).ToListAsync(ct);
        var hosts = await ctx.Db.ManagedServers.AsNoTracking().ToDictionaryAsync(m => m.Id, m => m.Hostname, ct);
        var table = rows.Select(f => new object?[]
        {
            hosts.GetValueOrDefault(f.ServerId), f.ShareUnc, DataCleanup.UncOf(f), f.Label.ToString(), f.Size, f.Changed, f.Modified,
        }).ToList();

        var facts = await ctx.CommonFactsAsync(ct);
        facts.Add(new("Stale means", $"Not changed or created since {ReportWriters.Text(cutoff)} ({years} years), and no recorded use since then"));
        return ctx.Document(Info, facts,
        [
            $"A file is stale when neither its last-modified nor its created time is newer than {years} years, so a recent copy of an old file is not stale.",
            "Windows' last-access time is not used: it is often turned off and is changed by backups and antivirus. Files only read, never changed, look stale unless file activity is collected for their server and shows someone opening them.",
            "File activity is kept for a limited time (365 days by default), so use older than that can't be seen.",
            "Data Scope never moves or deletes files. Check with the owner and your retention policy before archiving or deleting anything.",
        ],
        ["Server", "Share", "File", "Label", "Size (bytes)", "Last changed or created", "Modified"],
        table);
    }
}

/// <summary>Files with the same name and size in more than one place (D119): likely copies, not confirmed.</summary>
internal sealed class DuplicateFilesReport : IReport
{
    public ReportInfo Info { get; } = new("duplicate-files", "Duplicate files",
        "Files with the same name and size in more than one place, the groups wasting the most space first. Likely copies, not compared byte for byte.",
        Policies.ReadPosture);

    public async Task<ReportDocument> BuildAsync(ReportContext ctx, DirectoryStore directory, CancellationToken ct)
    {
        var cleanup = ctx.Cleanup ?? throw new InvalidOperationException("The duplicate files report needs the cleanup service.");
        var (serverId, shareId) = (ctx.Scope.ServerId, ctx.Scope.ShareId);
        var table = new List<object?[]>();
        var group = 0;
        // Groups in pages, so a report never loads every copy of every group at once.
        while (table.Count <= ctx.RowLimit)
        {
            var page = await cleanup.DuplicateGroups(serverId, shareId)
                .OrderByDescending(g => g.Wasted).ThenByDescending(g => g.Copies).ThenBy(g => g.NameKey).ThenBy(g => g.Size)
                .Skip(group).Take(200).ToListAsync(ct);
            if (page.Count == 0)
            {
                break;
            }
            var copies = await cleanup.CopiesAsync(serverId, shareId, null, page, ct);
            foreach (var g in page)
            {
                group++;
                foreach (var f in copies[(g.NameKey, g.Size)])
                {
                    table.Add([group, f.Name, g.Size, g.Copies, g.Wasted, DataCleanup.UncOf(f), f.Label.ToString(), f.Modified]);
                }
            }
        }

        var facts = await ctx.CommonFactsAsync(ct);
        facts.Add(new("Duplicate means", $"The same file name and size in more than one folder; files under {cleanup.Options.MinDuplicateBytes:N0} bytes and hidden or system files are left out"));
        return ctx.Document(Info, facts,
        [
            "Copies are matched by name and size only. Contents are not compared, so two different files can match, and renamed copies are not found.",
            "Wasted space counts every copy in a group but one.",
            "Data Scope never moves or deletes files. Check with the owners which copy to keep before deleting any.",
        ],
        ["Group", "Name", "Size (bytes)", "Copies", "Wasted (bytes)", "File", "Label", "Modified"],
        table);
    }
}
