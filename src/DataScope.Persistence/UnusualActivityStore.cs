using DataScope.Core.Activity;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace DataScope.Persistence;

/// <summary>
/// Unusual behaviour (D166). Once a day, learns each person's normal activity on a file server
/// from the days before; after each collection, compares what they did so far today with it and raises an alert for a
/// day far busier than their busy days, hours they never worked in, or folders holding sensitive data they never used.
/// Each alert covers one person and one local day and is updated as the day goes on. Only counts, hours and top-level
/// folders are kept; nothing here changes an event.
/// </summary>
public sealed class UnusualActivityStore(DataScopeDbContext db, NotificationStore outbox, TimeProvider clock)
{
    // What counts as using a file or folder. Explorer's attribute reads and plain share connections are left out: they
    // happen constantly and say nothing about files. An access check shows a file was reached, not that it was read.
    private static readonly string[] FileOperations =
    [
        nameof(ActivityOperation.Read), nameof(ActivityOperation.Write), nameof(ActivityOperation.Execute), nameof(ActivityOperation.DeleteOrRename),
        nameof(ActivityOperation.Deleted), nameof(ActivityOperation.PermissionChange), nameof(ActivityOperation.OwnerChange),
        nameof(ActivityOperation.AttributeChange), nameof(ActivityOperation.HandleRequest), nameof(ActivityOperation.ShareAccessCheck),
    ];

    /// <summary>The local system, local service and network service accounts: Windows itself, not a person.</summary>
    private static readonly string[] SystemSids = ["S-1-5-18", "S-1-5-19", "S-1-5-20"];

    private sealed record DayRow(string Person, string? Name, DateOnly Day, int Items, int Hours);

    private sealed record PlaceRow(string Person, string Place, int Items);

    private sealed record HourRow(string Person, string? Name, int Hour, int Items, DateTimeOffset First, DateTimeOffset Last, long FirstId, long LastId);

    private sealed record TotalRow(string Person, int Items);

    private sealed record SensitiveRow(string Place, Guid ShareId, string Folder);

    /// <summary>
    /// Learns the server's people if today's baselines aren't there yet, then judges today so far. Returns the alerts
    /// raised for the first time; alerts already raised today are updated in place.
    /// </summary>
    public async Task<IReadOnlyList<ActivityAlert>> CheckAsync(Guid serverId, ActivityAlertOptions options, TimeZoneInfo zone, string? publicUrl, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(zone);
        if (!options.Enabled || !options.UnusualEnabled)
        {
            // Turned off: what was learned about people isn't kept.
            await db.ActivityBaselines.Where(b => b.ServerId == serverId).ExecuteDeleteAsync(ct);
            return [];
        }
        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var dayStart = Midnight(today, zone);
        var tz = Iana(zone);

        var baselines = await LearnAsync(serverId, options, today, zone, tz, ct);
        if (baselines.Count == 0)
        {
            return [];
        }

        var until = now.AddMinutes(1);
        var hours = (await QueryAsync<HourRow>(Sql.FileHours, serverId, dayStart, until, tz, ct)).ToLookup(r => r.Person);
        var totals = (await QueryAsync<TotalRow>(Sql.FileTotals, serverId, dayStart, until, tz, ct))
            .GroupBy(r => r.Person).ToDictionary(g => g.Key, g => g.Sum(r => r.Items));
        var places = (await QueryAsync<PlaceRow>(Sql.FilePlaces, serverId, dayStart, until, tz, ct))
            .ToLookup(r => r.Person);

        // Only shares where someone judged used enough places new to them are looked up, so a quiet day costs nothing.
        var shares = places.Where(g => baselines.TryGetValue(g.Key, out var b) && b.Baseline.ActiveDays >= options.UnusualMinActiveDays
                && g.Select(p => p.Place).Distinct().Count(p => !b.Baseline.Places.Contains(p)) >= options.UnusualPlacesCount)
            .SelectMany(g => g).Where(p => !baselines[p.Person].Baseline.Places.Contains(p.Place))
            .Select(p => Guid.TryParse(p.Place.Split('|')[0], out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty).Distinct().ToArray();
        var sensitive = await SensitivePlacesAsync(shares, ct);

        var raised = new List<ActivityAlert>();
        foreach (var (person, items) in totals)
        {
            if (!baselines.TryGetValue(person, out var usual))
            {
                continue;
            }
            var byHour = hours[person].GroupBy(h => h.Hour).ToDictionary(g => g.Key, g => g.Sum(h => h.Items));
            var used = places[person].GroupBy(p => p.Place).ToDictionary(g => g.Key, g => g.Sum(p => p.Items));
            var findings = UnusualBehaviour.Judge(usual.Baseline, new ActivitySoFar(items, byHour, used), options, sensitive.ContainsKey);
            foreach (var finding in findings)
            {
                var rows = finding.Kind == UnusualKind.Hours ? hours[person].Where(h => finding.Hours.Contains(h.Hour)).ToList() : hours[person].ToList();
                var name = hours[person].Select(h => h.Name).LastOrDefault(n => n is not null) ?? usual.Name;
                var detail = finding.Kind switch
                {
                    UnusualKind.Hours => UnusualBehaviour.HoursText(finding.Hours),
                    UnusualKind.Places => PlacesText(finding.Places.Select(p => sensitive[p])),
                    _ => null,
                };
                if (await RaiseAsync(serverId, ActivityAlertText.Rule(finding.Kind), person, name, finding, detail, rows, dayStart, now, ct) is { } alert)
                {
                    raised.Add(alert);
                }
            }
        }
        await db.SaveChangesAsync(ct);

        if (options.EmailMedium && raised.Count > 0)
        {
            var hostname = await db.ManagedServers.Where(s => s.Id == serverId).Select(s => s.Hostname).SingleAsync(ct);
            foreach (var alert in raised)
            {
                await ActivityAlertStore.EmailAsync(outbox, alert, hostname, options, publicUrl, ct);
            }
        }
        return raised;
    }

    private sealed record Learned(string? Name, BehaviourBaseline Baseline);

    /// <summary>Today's baselines for the server, learning them first when they were learned on an earlier day.</summary>
    private async Task<Dictionary<string, Learned>> LearnAsync(Guid serverId, ActivityAlertOptions options, DateOnly today, TimeZoneInfo zone, string tz, CancellationToken ct)
    {
        var current = await db.ActivityBaselines.AnyAsync(b => b.ServerId == serverId && b.PersonKey == "" && b.LearnedOn == today, ct);
        if (!current)
        {
            var from = Midnight(today.AddDays(-options.UnusualLearnDays), zone);
            var to = Midnight(today, zone);
            var days = await QueryAsync<DayRow>(Sql.FileDays, serverId, from, to, tz, ct);
            var places = await QueryAsync<PlaceRow>(Sql.FileLearnedPlaces, serverId, from, to, tz, ct);
            var placesByPerson = places.ToLookup(p => p.Person);
            var now = clock.GetUtcNow();
            var rows = days.GroupBy(d => d.Person).Select(g =>
            {
                var merged = g.GroupBy(d => d.Day).Select(d => new ActivityDay(d.Key, d.Sum(x => x.Items), d.Aggregate(0, (m, x) => m | x.Hours)));
                var baseline = UnusualBehaviour.Learn(merged, placesByPerson[g.Key].OrderByDescending(p => p.Items).Select(p => p.Place).Distinct(StringComparer.Ordinal));
                return new ActivityBaseline
                {
                    ServerId = serverId,
                    PersonKey = g.Key,
                    PersonName = g.Select(d => d.Name).LastOrDefault(n => n is not null),
                    LearnedOn = today,
                    ActiveDays = baseline.ActiveDays,
                    BusyDay = baseline.BusyDay,
                    Hours = baseline.Hours,
                    Places = [.. baseline.Places],
                    UpdatedAt = now,
                };
            }).ToList();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.ActivityBaselines.Where(b => b.ServerId == serverId).ExecuteDeleteAsync(ct);
            db.ActivityBaselines.Add(new ActivityBaseline { ServerId = serverId, PersonKey = "", LearnedOn = today, UpdatedAt = now });
            db.ActivityBaselines.AddRange(rows);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            db.ChangeTracker.Clear();
        }
        return await db.ActivityBaselines.AsNoTracking().Where(b => b.ServerId == serverId && b.PersonKey != "")
            .ToDictionaryAsync(b => b.PersonKey, b => new Learned(b.PersonName, new BehaviourBaseline(b.ActiveDays, b.BusyDay, b.Hours, b.Places.ToHashSet(StringComparer.Ordinal))), ct);
    }

    /// <summary>Saves a finding as today's alert for the person, or updates the one already raised today; returns it when new.</summary>
    private async Task<ActivityAlert?> RaiseAsync(Guid serverId, ActivityAlertRule rule, string person, string? name, UnusualFinding finding, string? detail,
        List<HourRow> rows, DateTimeOffset dayStart, DateTimeOffset now, CancellationToken ct)
    {
        var first = rows.Min(r => r.First);
        var last = rows.Max(r => r.Last);
        var existing = await db.ActivityAlerts.FirstOrDefaultAsync(a => a.Rule == rule && a.ServerId == serverId && a.PrincipalSid == person && a.FirstEventAt >= dayStart, ct);
        if (existing is not null)
        {
            // The day goes on: the counts follow it, but an alert someone has looked at stays looked at.
            existing.EventCount = finding.Count;
            existing.Usual = finding.Usual;
            existing.Detail = detail;
            existing.FirstEventAt = first < existing.FirstEventAt ? first : existing.FirstEventAt;
            existing.LastEventAt = last > existing.LastEventAt ? last : existing.LastEventAt;
            existing.LastEventId = Math.Max(existing.LastEventId ?? 0, rows.Max(r => r.LastId));
            existing.PrincipalName = name ?? existing.PrincipalName;
            existing.UpdatedAt = now;
            return null;
        }
        var alert = new ActivityAlert
        {
            Rule = rule,
            Severity = ActivityAlertText.Severity(rule),
            State = ActivityAlertState.Open,
            ServerId = serverId,
            PrincipalSid = person,
            PrincipalName = name,
            FirstEventAt = first,
            LastEventAt = last,
            EventCount = finding.Count,
            FirstEventId = rows.Min(r => r.FirstId),
            LastEventId = rows.Max(r => r.LastId),
            Usual = finding.Usual,
            Detail = detail,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.ActivityAlerts.Add(alert);
        return alert;
    }

    /// <summary>The places on these shares that hold Restricted or Confidential files, with the share and folder they are.</summary>
    private async Task<Dictionary<string, (string Share, string Folder)>> SensitivePlacesAsync(Guid[] shares, CancellationToken ct)
    {
        if (shares.Length == 0)
        {
            return [];
        }
        string[] levels = [nameof(Core.Classification.Sensitivity.Confidential), nameof(Core.Classification.Sensitivity.Restricted)];
        var rows = await db.Database.SqlQuery<SensitiveRow>($"""
            SELECT f.share_id::text || '|' || lower(t.top) AS place, f.share_id, min(t.top) AS folder
            FROM files f JOIN folders d ON d.id = f.folder_id,
                LATERAL (SELECT split_part(translate(d.path, '/', '\'), '\', 1) AS top) t
            WHERE f.share_id = ANY({shares}) AND f.removed_at IS NULL AND coalesce(f.override_sensitivity, f.sensitivity) = ANY({levels})
            GROUP BY 1, 2
            """).ToListAsync(ct);
        var names = await db.Shares.AsNoTracking().Where(s => shares.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        return rows.ToDictionary(r => r.Place, r => (names.GetValueOrDefault(r.ShareId) ?? "a share", r.Folder), StringComparer.Ordinal);
    }

    /// <summary>"Finance\Payroll, HR\Staff and Board (its top level)": up to five, then how many more.</summary>
    private static string PlacesText(IEnumerable<(string Share, string Folder)> places)
    {
        var all = places.Select(p => p.Folder.Length == 0 ? $"{p.Share} (its top level)" : $"{p.Share}\\{p.Folder}").ToList();
        var shown = all.Take(5).ToList();
        var more = all.Count - shown.Count;
        if (more > 0)
        {
            return string.Join(", ", shown) + $" and {more} more";
        }
        return shown.Count == 1 ? shown[0] : string.Join(", ", shown[..^1]) + " and " + shown[^1];
    }

    // The server's activity between two times, one row per event: the person, the file or folder (item) and its
    // share's top-level folder (place). Names are compared without case. Each query below selects from it.
    private const string FileActivity = $"""
        WITH a AS (
            SELECT id, event_time, principal_sid AS person, principal_name AS name,
                coalesce(share_id::text || '|' || lower(relative_path), lower(object_path)) AS item,
                CASE WHEN share_id IS NULL OR relative_path IS NULL THEN NULL
                    WHEN strpos(translate(relative_path, '/', '\'), '\') > 0 THEN share_id::text || '|' || lower(split_part(translate(relative_path, '/', '\'), '\', 1))
                    WHEN object_kind = '{nameof(ActivityObjectKind.Folder)}' THEN share_id::text || '|' || lower(relative_path)
                    ELSE share_id::text || '|' END AS place
            FROM file_audit_events
            WHERE server_id = @server AND event_time >= @from AND event_time < @to AND event_id <> 0 AND outcome = '{nameof(ActivityOutcome.Success)}'
                AND operation = ANY(@kinds) AND principal_sid IS NOT NULL AND principal_sid <> ALL(@system)
                AND coalesce(principal_name, '') NOT LIKE '%$' AND coalesce(relative_path, object_path) IS NOT NULL)

        """;

    private const string DaysSelect = """
        SELECT person, max(name) AS name, (event_time AT TIME ZONE @tz)::date AS day, count(DISTINCT item)::int AS items,
            bit_or(1 << extract(hour FROM event_time AT TIME ZONE @tz)::int)::int AS hours
        FROM a GROUP BY person, 3
        """;

    private const string LearnedPlacesSelect = """
        SELECT person, place, items FROM (
            SELECT person, place, count(*)::int AS items, row_number() OVER (PARTITION BY person ORDER BY count(*) DESC, place) AS n
            FROM a WHERE place IS NOT NULL GROUP BY person, place) p
        WHERE n <= @max
        """;

    private const string HoursSelect = """
        SELECT person, max(name) AS name, extract(hour FROM event_time AT TIME ZONE @tz)::int AS hour, count(DISTINCT item)::int AS items,
            min(event_time) AS first, max(event_time) AS last, min(id) AS first_id, max(id) AS last_id
        FROM a GROUP BY person, 3
        """;

    private const string TotalsSelect = "SELECT person, count(DISTINCT item)::int AS items FROM a GROUP BY person";

    private const string PlacesSelect = "SELECT person, place, count(DISTINCT item)::int AS items FROM a WHERE place IS NOT NULL GROUP BY person, place";

    private static class Sql
    {
        public const string FileDays = FileActivity + DaysSelect;
        public const string FileLearnedPlaces = FileActivity + LearnedPlacesSelect;
        public const string FileHours = FileActivity + HoursSelect;
        public const string FileTotals = FileActivity + TotalsSelect;
        public const string FilePlaces = FileActivity + PlacesSelect;
    }

    /// <summary>Runs one of the <see cref="Sql"/> queries over the server's activity between two times.</summary>
    private Task<List<T>> QueryAsync<T>(string sql, Guid serverId, DateTimeOffset from, DateTimeOffset to, string tz, CancellationToken ct)
    {
        NpgsqlParameter[] parameters =
        [
            new("server", serverId),
            new("from", from),
            new("to", to),
            new("tz", tz),
            new("max", UnusualBehaviour.MaxPlaces),
            new("kinds", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = FileOperations },
            new("system", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = SystemSids },
        ];
        return db.Database.SqlQueryRaw<T>(sql, parameters).ToListAsync(ct);
    }

    /// <summary>The start of a local day, in UTC.</summary>
    public static DateTimeOffset Midnight(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        // A day that starts in a clock change starts at the first time that exists.
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(30);
        }
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }

    /// <summary>The zone's IANA name, which PostgreSQL understands; UTC when Windows has no match for it.</summary>
    public static string Iana(TimeZoneInfo zone) =>
        zone.HasIanaId ? zone.Id : TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : "UTC";
}
