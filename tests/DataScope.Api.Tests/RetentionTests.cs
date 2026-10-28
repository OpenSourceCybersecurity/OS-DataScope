using System.Net.Http.Json;
using DataScope.Core.Classification;
using DataScope.Core.Governance;
using DataScope.Core.Inventory;
using DataScope.Core.Security;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Tests;

/// <summary>Scan history, trends and retention over the synthetic governance lab, as the services' limited login.</summary>
[Trait("Category", "Database")]
public sealed class RetentionTests : IClassFixture<RetentionTests.RetentionLab>
{
    private readonly GovernanceApiTests.Lab _lab;

    public RetentionTests(RetentionLab lab) => _lab = lab.Lab;

    public sealed class RetentionLab : IAsyncLifetime
    {
        public GovernanceApiTests.Lab Lab { get; } = new();

        public async Task InitializeAsync()
        {
            await Lab.InitializeAsync();
            Lab.Write(@"Finance\Old\a.txt", "Old.");
            Lab.Write(@"Finance\Old\Deeper\b.txt", "Older.");
            Lab.Write(@"Finance\Recent\c.txt", "Recent.");
            Lab.Write(@"Finance\Labelled\d.txt", "Labelled.");
            Lab.Write(@"Finance\salaries.csv", "Name,SSN\nJo Park,512-48-3320\n");
            await Lab.RescanAsync();
            Lab.RemoveFolder(@"Finance\Old");
            Lab.RemoveFolder(@"Finance\Recent");
            Lab.RemoveFolder(@"Finance\Labelled");
            await Lab.RescanAsync();
        }

        public Task DisposeAsync() => Lab.DisposeAsync();
    }

    private Task<int> Sql(string sql) => Exec(_lab.Database.CreateOwnerContext(), sql);

    private static async Task<int> Exec(DataScopeDbContext db, string sql)
    {
        await using (db)
        {
            return await db.Database.ExecuteSqlRawAsync(sql);
        }
    }

    private sealed record TrendView(DateOnly Day, int Shares, long Files, long Restricted, long Confidential);

    private sealed record DashboardView(List<TrendView> Trend);

    [Fact]
    public async Task Every_finished_scan_leaves_share_totals_for_the_trend()
    {
        await using var db = _lab.Database.CreateContext();
        var finance = _lab.Shares["Finance"];
        var rows = await db.ShareScanHistory.AsNoTracking().Where(h => h.ShareId == finance).OrderBy(h => h.CompletedAt).ToListAsync();
        // One row per scan: the lab's first, then the two here (retention, tested below, may remove the oldest).
        Assert.True(rows.Count >= 2, $"{rows.Count} rows");
        var share = await db.Shares.AsNoTracking().SingleAsync(s => s.Id == finance);
        var last = rows.Single(r => r.JobId == share.LastScanJobId);
        Assert.Equal(share.FileCount, last.Files);
        Assert.Equal(1, last.RestrictedFiles + last.ConfidentialFiles);
        Assert.Equal(ShareState.Scanned, last.State);

        var dashboard = await (await _lab.Factory.ClientAs(Roles.Viewer).GetAsync("/api/v1/dashboard")).Content.ReadFromJsonAsync<DashboardView>();
        var today = Assert.Single(dashboard!.Trend);
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow), today.Day);
        Assert.Equal(_lab.Shares.Count, today.Shares);
        Assert.Equal(await db.Shares.Where(s => s.Included).SumAsync(s => s.FileCount), today.Files);
    }

    [Fact]
    public async Task Trend_has_a_point_per_day_built_from_each_shares_latest_scan()
    {
        await using var db = _lab.Database.CreateContext();
        var shares = _lab.Shares.Values.ToList();
        var hr = _lab.Shares["HR"];
        var now = DateTimeOffset.UtcNow;
        // Earlier scans, added in a transaction that is rolled back: everyone 400 days ago (before the window),
        // HR alone 20 days ago, everyone 10 days ago.
        await using var tx = await db.Database.BeginTransactionAsync();
        foreach (var (share, at, files) in shares.Select(s => (s, now.AddDays(-400), 999L))
            .Append((hr, now.AddDays(-20), 7L))
            .Concat(shares.Select(s => (s, now.AddDays(-10), 1L))))
        {
            var job = new ScanJob { Id = Guid.NewGuid(), ServerId = _lab.ServerId, PolicyVersion = "test", RequestedBySid = "S-1", CreatedAt = at, UpdatedAt = at };
            db.ScanJobs.Add(job);
            db.ShareScanHistory.Add(new ShareScanHistory { ShareId = share, JobId = job.Id, CompletedAt = at, State = ShareState.Scanned, Files = files, Bytes = files });
        }
        await db.SaveChangesAsync();

        var trend = await Endpoints.ReportEndpoints.TrendAsync(db, shares, now, CancellationToken.None);
        await tx.RollbackAsync();

        Assert.Equal(3, trend.Count);
        // 400 days ago is outside the window, but still each share's starting point; HR's newer scan replaced its own.
        Assert.Equal(DateOnly.FromDateTime(now.AddDays(-20).UtcDateTime), trend[0].Day);
        Assert.Equal(shares.Count, trend[0].Shares);
        Assert.Equal(999L * (shares.Count - 1) + 7, trend[0].Files);
        Assert.Equal(shares.Count, trend[1].Files);
        Assert.Equal(DateOnly.FromDateTime(now.UtcDateTime), trend[2].Day);
    }

    [Fact]
    public async Task A_years_daily_scans_are_thinned_to_weekly_points()
    {
        await using var db = _lab.Database.CreateContext();
        var share = _lab.Shares["Ops"];
        var now = DateTimeOffset.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync();
        for (var day = 1; day <= 300; day++)
        {
            var at = now.AddDays(-day);
            var job = new ScanJob { Id = Guid.NewGuid(), ServerId = _lab.ServerId, PolicyVersion = "test", RequestedBySid = "S-1", CreatedAt = at, UpdatedAt = at };
            db.ScanJobs.Add(job);
            db.ShareScanHistory.Add(new ShareScanHistory { ShareId = share, JobId = job.Id, CompletedAt = at, State = ShareState.Scanned, Files = day });
        }
        await db.SaveChangesAsync();

        var trend = await Endpoints.ReportEndpoints.TrendAsync(db, [share], now, CancellationToken.None);
        await tx.RollbackAsync();

        Assert.InRange(trend.Count, 40, Endpoints.ReportEndpoints.MaxTrendPoints);
        Assert.True(trend.Zip(trend.Skip(1)).All(p => p.First.Day < p.Second.Day));
        Assert.Equal(DateOnly.FromDateTime(now.UtcDateTime), trend[^1].Day);
    }

    [Fact]
    public async Task Retention_removes_old_history_and_keeps_evidence()
    {
        var finance = _lab.Shares["Finance"];
        await using (var db = _lab.Database.CreateContext())
        {
            // A reviewer's label on a file that later disappeared: evidence, kept.
            var labelled = await db.Files.SingleAsync(f => f.ShareId == finance && f.Name == "d.txt");
            db.ClassificationOverrides.Add(new ClassificationOverride
            {
                FileId = labelled.Id,
                Sensitivity = Sensitivity.Confidential,
                PolicySensitivity = Sensitivity.Public,
                Reason = "Synthetic test label",
                ReviewerSid = "S-1-5-21-7-7-7-1",
                ReviewerName = "GOV\\reviewer",
                At = DateTimeOffset.UtcNow,
            });
            var jobs = await db.ScanJobs.OrderBy(j => j.CreatedAt).Select(j => j.Id).ToListAsync();
            var latest = await db.Shares.Where(s => s.Id == finance).Select(s => s.LastScanJobId!.Value).SingleAsync();
            var old = DateTimeOffset.UtcNow.AddDays(-400);
            db.ScanGaps.AddRange(
                new ScanGap { JobId = jobs[0], ShareId = finance, Path = "Gone", Kind = GapKind.AccessDenied, Message = "Synthetic old gap.", At = old },
                new ScanGap { JobId = latest, ShareId = finance, Path = "Current", Kind = GapKind.AccessDenied, Message = "Synthetic current gap.", At = old });
            db.Outbox.AddRange(
                Mail("old-sent", OutboxState.Sent, old), Mail("old-pending", OutboxState.Pending, old), Mail("new-sent", OutboxState.Sent, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }
        // Old and Labelled vanished long ago; Recent only just now.
        await Sql($"UPDATE files SET removed_at = now() - interval '400 days' WHERE share_id = '{finance}' AND name IN ('a.txt', 'b.txt', 'd.txt')");
        await Sql($@"UPDATE folders SET removed_at = now() - interval '400 days' WHERE share_id = '{finance}' AND path IN ('Old', 'Old\Deeper', 'Labelled')");
        await Sql($"UPDATE share_scan_history SET completed_at = now() - interval '1200 days' WHERE share_id = '{finance}' AND job_id = (SELECT id FROM scan_jobs ORDER BY created_at LIMIT 1)");

        RetentionResult result;
        await using (var db = _lab.Database.CreateContext())
        {
            var store = new RetentionStore(db, new AuditLogWriter(db, TimeProvider.System), TimeProvider.System);
            result = await store.PurgeAsync(new RetentionOptions(), CancellationToken.None);
        }

        Assert.Equal(2, result.Files);
        Assert.Equal(2, result.Folders);
        Assert.Equal(1, result.ScanGaps);
        Assert.Equal(1, result.Notifications);
        Assert.Equal(1, result.ScanHistory);
        await using (var db = _lab.Database.CreateContext())
        {
            var names = await db.Files.Where(f => f.ShareId == finance).Select(f => f.Name).ToListAsync();
            Assert.DoesNotContain("a.txt", names);
            Assert.DoesNotContain("b.txt", names);
            Assert.Contains("d.txt", names);
            Assert.Contains("c.txt", names);
            var paths = await db.Folders.Where(f => f.ShareId == finance).Select(f => f.Path).ToListAsync();
            Assert.DoesNotContain("Old", paths);
            Assert.Contains("Labelled", paths);
            Assert.Contains("Recent", paths);
            Assert.Equal(["Current"], await db.ScanGaps.Where(g => g.ShareId == finance).Select(g => g.Path).ToListAsync());
            Assert.Equal(["new-sent", "old-pending"], await db.Outbox.Where(m => m.IdempotencyKey.StartsWith("retention-")).OrderBy(m => m.IdempotencyKey)
                .Select(m => m.IdempotencyKey.Substring(10)).ToListAsync());
            var entry = await db.AppAuditLog.Where(a => a.Action == "retention.purge").SingleAsync();
            Assert.Equal(RetentionStore.ActorName, entry.ActorName);
            Assert.Contains("\"Files\":2", entry.Details, StringComparison.Ordinal);

            // Nothing left to do: no second audit entry.
            var again = await new RetentionStore(db, new AuditLogWriter(db, TimeProvider.System), TimeProvider.System).PurgeAsync(new RetentionOptions(), CancellationToken.None);
            Assert.Equal(0, again.Total);
            Assert.Equal(1, await db.AppAuditLog.CountAsync(a => a.Action == "retention.purge"));
        }
    }

    private static OutboxMessage Mail(string key, OutboxState state, DateTimeOffset at) => new()
    {
        IdempotencyKey = $"retention-{key}",
        Kind = NotificationKind.ReviewAssigned,
        RecipientSid = "S-1-5-21-7-7-7-2",
        RecipientName = "GOV\\owner",
        Subject = "Synthetic",
        Body = "Synthetic",
        State = state,
        NextAttemptAt = at,
        CreatedAt = at,
        SentAt = state == OutboxState.Sent ? at : null,
    };

    [Theory]
    [InlineData(10)]
    [InlineData(-1)]
    public void Retention_refuses_periods_shorter_than_a_month(int days)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new RetentionOptions { RemovedItemsDays = days }.Validate());
        Assert.Contains("RemovedItemsDays", ex.Message, StringComparison.Ordinal);
        new RetentionOptions { RemovedItemsDays = 0 }.Validate();
    }
}
