using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using DataScope.Api.Auth;
using DataScope.Core.Activity;
using DataScope.Core.Inventory;
using DataScope.Core.Security;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Endpoints;

/// <summary>
/// File activity collected from file servers' Security logs. Every event says how much it proves (D97): by default
/// only operations Windows recorded and handle requests are listed; share access checks and share connections are
/// shown only when asked for, and never as reads. Activity names people and what they touched, so it needs the
/// same roles as review evidence, not just a Viewer role (D101).
/// </summary>
internal static partial class ActivityEndpoints
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 200;

    public sealed record ActivityEventDto(
        long Id, Guid ServerId, string Hostname, int EventId, long RecordId, DateTimeOffset Time,
        string? PrincipalSid, string? PrincipalName, string Operation, string Label, string Certainty, string Outcome,
        Guid? ShareId, string? ShareName, string? RelativePath, string? ObjectPath, string ObjectKind, string PathSource,
        long? CorrelatedRecordId, string? ClientAddress);

    public sealed record ActivityEventDetailDto(
        ActivityEventDto Event, string Explanation, IReadOnlyList<string> Rights, long? AccessMask, string? ProcessName, long? ProcessId,
        long? HandleId, string? LogonId, string Channel, int LogGeneration, string? Provider, DateTimeOffset IngestedAt,
        string? OldSecurity, string? NewSecurity, string? ParseError, string RawXml);

    public sealed record ActivityPage(IReadOnlyList<ActivityEventDto> Items, string? Next);

    public sealed record ActivityDay(DateOnly Date, int Reads, int Writes, int Deletes, int PermissionChanges, int Denied);

    public sealed record ActivitySummaryDto(DateTimeOffset From, DateTimeOffset To, int OffsetMinutes, IReadOnlyList<ActivityDay> Days, long Unmatched, DateTimeOffset? LastEventAt, int Gaps);

    public sealed record ShareMappingDto(Guid Id, string Name, string? LocalPath, string? LocalPathSource, DateTimeOffset? LocalPathSetAt);

    public sealed record ActivitySourceDto(
        Guid ServerId, string Hostname, bool Enabled, string State, DateTimeOffset? LastReadAt, DateTimeOffset? LastEventAt,
        DateTimeOffset? NextReadAt, string? LastError, DateTimeOffset? LastErrorAt, long EventsStored, long EventsIgnored,
        long EventsUnreadable, long Unmatched, IReadOnlyList<ShareMappingDto> Shares, SecurityLogDto? Log, IReadOnlyList<ActivityGapDto> Gaps);

    /// <summary>
    /// The server's Security log as last checked (D102). <c>Behind</c> counts log records after the last one read, of
    /// any kind; <c>AtRisk</c> means more than half the log is still to read, so it could wrap before Data Scope gets there.
    /// </summary>
    public sealed record SecurityLogDto(
        long? OldestRecordId, long? NewestRecordId, long? RecordCount, DateTimeOffset? OldestEventAt, long? SizeBytes,
        long? MaxSizeBytes, bool? Full, DateTimeOffset? CheckedAt, long? Behind, bool AtRisk);

    public sealed record ActivityGapDto(long Id, string Reason, long FirstRecordId, long? LastRecordId, long? Records, DateTimeOffset DetectedAt, DateTimeOffset? LogOldestEventAt);

    public sealed record SetSourceRequest(bool? Enabled);

    /// <summary>An alert with its wording (D105). <c>Operation</c> is the event filter that shows the events behind it.</summary>
    public sealed record ActivityAlertDto(
        long Id, string Rule, string Title, string Severity, string State, Guid ServerId, string Hostname, string? PrincipalSid,
        string? PrincipalName, DateTimeOffset FirstEventAt, DateTimeOffset LastEventAt, int EventCount, string Description,
        string? Operation, string? Outcome, DateTimeOffset CreatedAt, string? AcknowledgedByName, DateTimeOffset? AcknowledgedAt, string? Note,
        bool Unusual = false, int? Usual = null, string? Detail = null);

    public sealed record ActivityAlertList(IReadOnlyList<ActivityAlertDto> Items, int Open);

    public sealed record SetAlertRequest(string? State, string? Note);

    /// <summary>
    /// How far unusual behaviour (D166) has learned each file server: when, how many people it has seen, and
    /// how many of them have enough days to be judged. Counts only.
    /// </summary>
    public sealed record UnusualSourceDto(Guid ServerId, string Hostname, DateOnly? LearnedOn, int People, int Judged);

    public sealed record UnusualDto(bool On, int LearnDays, int MinActiveDays, IReadOnlyList<UnusualSourceDto> Sources);

    public sealed record SetLocalPathRequest(string? LocalPath);

    private sealed record DayRow(DateOnly Day, string Operation, string Outcome, int Count);

    // Data Scope only accepts a drive path for a share's local folder, as Windows' own share settings show it.
    [GeneratedRegex(@"^[A-Za-z]:\\[^\x00-\x1f""*?<>|]*$")]
    private static partial Regex DrivePath();

    public static void MapActivity(this RouteGroupBuilder api)
    {
        var activity = api.MapGroup("/activity").RequireAuthorization(Policies.ReadActivity);

        activity.MapGet("/events", async (
            Guid? serverId, Guid? shareId, string? principalSid, string? operation, string? outcome, bool? includeChecks,
            DateTimeOffset? from, DateTimeOffset? to, string? search, string? cursor, int? limit, DataScopeDbContext db, CancellationToken ct) =>
        {
            var l = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
            var query = db.FileAuditEvents.AsNoTracking();
            if (includeChecks != true)
            {
                query = query.Where(e => e.Certainty == ActivityCertainty.Performed || e.Certainty == ActivityCertainty.Requested);
            }
            if (serverId is { } server)
            {
                query = query.Where(e => e.ServerId == server);
            }
            if (shareId is { } share)
            {
                query = query.Where(e => e.ShareId == share);
            }
            if (!string.IsNullOrWhiteSpace(principalSid))
            {
                query = query.Where(e => e.PrincipalSid == principalSid);
            }
            if (!string.IsNullOrWhiteSpace(operation))
            {
                if (!Enum.TryParse<ActivityOperation>(operation, ignoreCase: false, out var op) || !Enum.IsDefined(op))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["operation"] = [$"Use one of: {string.Join(", ", Enum.GetNames<ActivityOperation>())}."] });
                }
                query = query.Where(e => e.Operation == op);
            }
            if (!string.IsNullOrWhiteSpace(outcome))
            {
                if (!Enum.TryParse<ActivityOutcome>(outcome, ignoreCase: false, out var oc) || !Enum.IsDefined(oc))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["outcome"] = ["Use Success or Failure."] });
                }
                query = query.Where(e => e.Outcome == oc);
            }
            if (from is { } f)
            {
                query = query.Where(e => e.EventTime >= f);
            }
            if (to is { } t)
            {
                query = query.Where(e => e.EventTime < t);
            }
            if (!string.IsNullOrWhiteSpace(search))
            {
                if (search.Length > 200)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["search"] = ["Search for at most 200 characters."] });
                }
                // Paths are full of backslashes, LIKE's escape character: escape it and the wildcards.
                var pattern = "%" + search.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
                query = query.Where(e => e.ObjectPath != null && EF.Functions.ILike(e.ObjectPath, pattern, "\\"));
            }
            if (cursor is not null)
            {
                if (!TryReadCursor(cursor, out var at, out var id))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["cursor"] = ["The cursor is not valid. Start again without it."] });
                }
                query = query.Where(e => e.EventTime < at || (e.EventTime == at && e.Id < id));
            }
            var rows = await Project(db, query.OrderByDescending(e => e.EventTime).ThenByDescending(e => e.Id).Take(l + 1)).ToListAsync(ct);
            var items = rows.Take(l).Select(WithLabel).ToList();
            var next = rows.Count > l ? Cursor(items[^1].Time, items[^1].Id) : null;
            return Results.Ok(new ActivityPage(items, next));
        });

        activity.MapGet("/events/{id:long}", async (long id, DataScopeDbContext db, CancellationToken ct) =>
        {
            var e = await db.FileAuditEvents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (e is null)
            {
                return Results.NotFound();
            }
            var dto = WithLabel(await Project(db, db.FileAuditEvents.Where(x => x.Id == id)).SingleAsync(ct));
            return Results.Ok(new ActivityEventDetailDto(
                dto, Explain(e), e.AccessMask is { } m ? FileAccessMask.Describe((uint)m) : [], e.AccessMask, e.ProcessName, e.ProcessId,
                e.HandleId, e.LogonId, e.Channel, e.LogGeneration, e.Provider, e.IngestedAt, e.OldSecurity, e.NewSecurity, e.ParseError, e.RawXml));
        });

        activity.MapGet("/summary", async (int? days, int? offsetMinutes, DataScopeDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var n = Math.Clamp(days ?? 7, 1, 90);
            var offset = Math.Clamp(offsetMinutes ?? 0, -14 * 60, 14 * 60);
            var shift = TimeSpan.FromMinutes(offset);
            var localToday = DateOnly.FromDateTime((clock.GetUtcNow() + shift).UtcDateTime);
            var firstDay = localToday.AddDays(1 - n);
            var from = new DateTimeOffset(firstDay.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) - shift;
            var to = from.AddDays(n);
            var performed = nameof(ActivityCertainty.Performed);
            var requested = nameof(ActivityCertainty.Requested);
            var rows = await db.Database.SqlQuery<DayRow>($"""
                SELECT (event_time AT TIME ZONE 'UTC' + make_interval(mins => {offset}))::date AS day, operation, outcome, count(*)::int AS count
                FROM file_audit_events
                WHERE event_time >= {from} AND event_time < {to} AND certainty IN ({performed}, {requested})
                GROUP BY 1, 2, 3
                """).ToListAsync(ct);
            var list = Enumerable.Range(0, n).Select(i =>
            {
                var day = firstDay.AddDays(i);
                var today = rows.Where(r => r.Day == day).ToList();
                int Count(params ActivityOperation[] ops) => today
                    .Where(r => r.Outcome == nameof(ActivityOutcome.Success) && ops.Any(o => o.ToString() == r.Operation)).Sum(r => r.Count);
                return new ActivityDay(day, Count(ActivityOperation.Read), Count(ActivityOperation.Write), Count(ActivityOperation.Deleted),
                    Count(ActivityOperation.PermissionChange, ActivityOperation.OwnerChange),
                    today.Where(r => r.Outcome == nameof(ActivityOutcome.Failure)).Sum(r => r.Count));
            }).ToList();
            var unmatched = await db.FileAuditEvents.LongCountAsync(e => e.EventTime >= from && e.EventTime < to && e.ShareId == null && e.ObjectPath != null, ct);
            var last = await db.FileAuditEvents.Where(e => e.EventId != 0).MaxAsync(e => (DateTimeOffset?)e.EventTime, ct);
            var gaps = await db.ActivityGaps.CountAsync(g => g.DetectedAt >= from && g.DetectedAt < to, ct);
            return Results.Ok(new ActivitySummaryDto(from, to, offset, list, unmatched, last, gaps));
        });

        activity.MapGet("/sources", async (DataScopeDbContext db, CancellationToken ct) =>
        {
            var servers = await db.ManagedServers.AsNoTracking().Where(s => s.State != EnrollmentState.Retired).OrderBy(s => s.Hostname).ToListAsync(ct);
            var sources = await db.ActivitySources.AsNoTracking().Where(s => s.Channel == ActivityStore.SecurityChannel).ToDictionaryAsync(s => s.ServerId, ct);
            var shares = (await db.Shares.AsNoTracking().Where(s => s.State != ShareState.Removed && s.Kind == Core.Inventory.ShareKind.Disk)
                .OrderBy(s => s.NameKey).ToListAsync(ct)).ToLookup(s => s.ServerId);
            var unmatched = await db.FileAuditEvents.Where(e => e.ShareId == null && e.ObjectPath != null)
                .GroupBy(e => e.ServerId).Select(g => new { g.Key, Count = g.LongCount() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
            var gaps = (await db.ActivityGaps.AsNoTracking().Where(g => g.Channel == ActivityStore.SecurityChannel)
                .OrderByDescending(g => g.Id).Take(200).ToListAsync(ct)).ToLookup(g => g.ServerId);
            return servers.Select(s =>
            {
                sources.TryGetValue(s.Id, out var a);
                return new ActivitySourceDto(
                    s.Id, s.Hostname, a?.Enabled ?? false, a?.State.ToString() ?? "Off", a?.LastReadAt, a?.LastEventAt, a?.NextReadAt,
                    a?.LastError, a?.LastErrorAt, a?.EventsStored ?? 0, a?.EventsIgnored ?? 0, a?.EventsUnreadable ?? 0,
                    unmatched.GetValueOrDefault(s.Id),
                    shares[s.Id].Select(x => new ShareMappingDto(x.Id, x.Name, x.LocalPath, x.LocalPathSource?.ToString(), x.LocalPathSetAt)).ToList(),
                    a?.LogCheckedAt is null ? null : LogHealth(a),
                    gaps[s.Id].Take(10).Select(g => new ActivityGapDto(g.Id, g.Reason.ToString(), g.FirstRecordId, g.LastRecordId,
                        g.LastRecordId - g.FirstRecordId + 1, g.DetectedAt, g.LogOldestEventAt)).ToList());
            }).ToList();
        });

        activity.MapPut("/sources/{serverId:guid}", async (Guid serverId, SetSourceRequest request, ClaimsPrincipal user, DataScopeDbContext db, ActivityStore store, AuditLogWriter audit, CancellationToken ct) =>
        {
            var server = await db.ManagedServers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == serverId && s.State != EnrollmentState.Retired, ct);
            if (server is null)
            {
                return Results.NotFound();
            }
            if (request.Enabled is not { } enabled)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["enabled"] = ["Set enabled to true or false."] });
            }
            var actor = Actor.From(user);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await store.SetEnabledAsync(serverId, enabled, actor.Sid, ct);
            await audit.AppendAsync(actor.Sid, actor.Name, enabled ? "activity.enable" : "activity.disable", server.Hostname, new { serverId }, ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization(Policies.ManagePlatform);

        activity.MapGet("/unusual", async (DataScopeDbContext db, IConfiguration config, CancellationToken ct) =>
        {
            var options = config.GetSection(ActivityAlertOptions.Section).Get<ActivityAlertOptions>() ?? new ActivityAlertOptions();
            var min = options.UnusualMinActiveDays;
            var learned = await db.ActivityBaselines.AsNoTracking().GroupBy(b => b.ServerId).Select(g => new
            {
                ServerId = g.Key,
                LearnedOn = g.Where(b => b.PersonKey == "").Max(b => (DateOnly?)b.LearnedOn),
                People = g.Count(b => b.PersonKey != ""),
                Judged = g.Count(b => b.PersonKey != "" && b.ActiveDays >= min),
            }).ToListAsync(ct);
            var servers = await db.ManagedServers.AsNoTracking().Where(s => learned.Select(l => l.ServerId).Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => s.Hostname, ct);
            var sources = learned.Where(l => servers.ContainsKey(l.ServerId)).Select(l => new UnusualSourceDto(l.ServerId, servers[l.ServerId],
                l.LearnedOn, l.People, l.Judged)).OrderBy(s => s.Hostname).ToList();
            return new UnusualDto(options.Enabled && options.UnusualEnabled, options.UnusualLearnDays, min, sources);
        });

        activity.MapGet("/alerts", async (string? state, int? limit, DataScopeDbContext db, CancellationToken ct) =>
        {
            var query = db.ActivityAlerts.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(state) && state != "All")
            {
                if (!Enum.TryParse<ActivityAlertState>(state, ignoreCase: false, out var s) || !Enum.IsDefined(s))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["state"] = ["Use Open, Acknowledged or All."] });
                }
                query = query.Where(a => a.State == s);
            }
            var rows = await (from a in query
                              join server in db.ManagedServers on a.ServerId equals server.Id
                              orderby a.LastEventAt descending, a.Id descending
                              select new { a, server.Hostname }).Take(Math.Clamp(limit ?? 50, 1, MaxLimit)).ToListAsync(ct);
            var open = await db.ActivityAlerts.CountAsync(a => a.State == ActivityAlertState.Open, ct);
            return Results.Ok(new ActivityAlertList(rows.Select(r => AlertDto(r.a, r.Hostname)).ToList(), open));
        });

        activity.MapPut("/alerts/{id:long}", async (long id, SetAlertRequest request, ClaimsPrincipal user, DataScopeDbContext db, ActivityAlertStore alerts, AuditLogWriter audit, CancellationToken ct) =>
        {
            if (!Enum.TryParse<ActivityAlertState>(request.State, ignoreCase: false, out var state) || !Enum.IsDefined(state))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["state"] = ["Use Open or Acknowledged."] });
            }
            if (request.Note is { Length: > 2000 })
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["note"] = ["Keep the note under 2,000 characters."] });
            }
            var actor = Actor.From(user);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var alert = await alerts.SetStateAsync(id, state, request.Note, actor.Sid, actor.Name, ct);
            if (alert is null)
            {
                return Results.NotFound();
            }
            var hostname = await db.ManagedServers.Where(s => s.Id == alert.ServerId).Select(s => s.Hostname).SingleAsync(ct);
            await audit.AppendAsync(actor.Sid, actor.Name, state == ActivityAlertState.Acknowledged ? "activity.alert.acknowledge" : "activity.alert.reopen",
                hostname, new { alert.Id, Rule = alert.Rule.ToString(), alert.Note }, ct);
            await tx.CommitAsync(ct);
            return Results.Ok(AlertDto(alert, hostname));
        }).RequireAuthorization(Policies.TriageActivity);

        activity.MapPut("/shares/{shareId:guid}/local-path", async (Guid shareId, SetLocalPathRequest request, ClaimsPrincipal user, DataScopeDbContext db, ActivityStore store, AuditLogWriter audit, CancellationToken ct) =>
        {
            var path = string.IsNullOrWhiteSpace(request.LocalPath) ? null : request.LocalPath.Trim();
            if (path is not null && (path.Length > 1024 || !DrivePath().IsMatch(path)))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["localPath"] = [@"Enter the share's folder on the file server as a drive path, for example D:\Shares\Finance."],
                });
            }
            var actor = Actor.From(user);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var share = await store.SetShareLocalPathAsync(shareId, path, ct);
            if (share is null)
            {
                return Results.NotFound();
            }
            await audit.AppendAsync(actor.Sid, actor.Name, "share.local-path", share.UncPath, new { share.Id, share.LocalPath }, ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new ShareMappingDto(share.Id, share.Name, share.LocalPath, share.LocalPathSource?.ToString(), share.LocalPathSetAt));
        }).RequireAuthorization(Policies.ManagePlatform);
    }

    private static IQueryable<ActivityEventDto> Project(DataScopeDbContext db, IQueryable<FileAuditEvent> events) =>
        from e in events
        join server in db.ManagedServers on e.ServerId equals server.Id
        join share in db.Shares on e.ShareId equals share.Id into shares
        from share in shares.DefaultIfEmpty()
        select new ActivityEventDto(
            e.Id, e.ServerId, server.Hostname, e.EventId, e.RecordId, e.EventTime, e.PrincipalSid, e.PrincipalName,
            e.Operation.ToString(), "", e.Certainty.ToString(), e.Outcome.ToString(), e.ShareId, share == null ? null : share.Name,
            e.RelativePath, e.ObjectPath, e.ObjectKind.ToString(), e.PathSource.ToString(), e.CorrelatedRecordId, e.ClientAddress);

    private static ActivityEventDto WithLabel(ActivityEventDto e) =>
        e with { Label = ActivityText.Operation(Enum.Parse<ActivityOperation>(e.Operation), Enum.Parse<ActivityObjectKind>(e.ObjectKind)) };

    private static string Explain(FileAuditEvent e)
    {
        var text = ActivityText.Certainty(e.Certainty);
        if (e.Outcome == ActivityOutcome.Failure)
        {
            text = "Windows refused. " + text;
        }
        if (e.PathSource == ActivityPathSource.Correlated)
        {
            text += $" Windows does not name the object in a delete event; the path comes from event record {e.CorrelatedRecordId} on the same handle.";
        }
        else if (e.Operation == ActivityOperation.Deleted && e.ObjectPath is null)
        {
            text += " Windows does not name the object in a delete event, and no earlier event on the same handle was collected, so what was deleted is unknown.";
        }
        if (e.Operation == ActivityOperation.DeleteOrRename)
        {
            text += " Renaming and moving a file need the same right as deleting it; a separate delete event (4660) confirms a deletion.";
        }
        if (e.ObjectKind == ActivityObjectKind.Unknown && e.Operation is ActivityOperation.Read or ActivityOperation.Write or ActivityOperation.Execute)
        {
            text += " Windows uses the same right for files and folders, and this path is not a file or folder from the last scan, so it may be either.";
        }
        if (e.Operation == ActivityOperation.Unreadable)
        {
            text = "Data Scope could not read this event. It is kept exactly as received.";
        }
        return text;
    }

    internal static string Cursor(DateTimeOffset at, long id) =>
        $"{at.UtcTicks.ToString(CultureInfo.InvariantCulture)}_{id.ToString(CultureInfo.InvariantCulture)}";

    internal static bool TryReadCursor(string cursor, out DateTimeOffset at, out long id)
    {
        at = default;
        id = 0;
        var parts = cursor.Split('_');
        if (parts.Length != 2
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out id)
            || ticks > DateTimeOffset.MaxValue.UtcTicks)
        {
            return false;
        }
        at = new DateTimeOffset(ticks, TimeSpan.Zero);
        return true;
    }

    private static ActivityAlertDto AlertDto(ActivityAlert a, string hostname) => new(
        a.Id, a.Rule.ToString(), ActivityAlertText.Title(a.Rule), a.Severity.ToString(), a.State.ToString(), a.ServerId, hostname, a.PrincipalSid,
        a.PrincipalName, a.FirstEventAt, a.LastEventAt, a.EventCount,
        ActivityAlertText.Describe(a.Rule, a.PrincipalName, a.EventCount, hostname, a.FirstEventAt, a.LastEventAt, a.Usual, a.Detail),
        a.Rule switch
        {
            ActivityAlertRule.MassDelete => nameof(ActivityOperation.Deleted),
            ActivityAlertRule.PermissionChange => nameof(ActivityOperation.PermissionChange),
            ActivityAlertRule.LogCleared => nameof(ActivityOperation.LogCleared),
            _ => null,
        },
        a.Rule == ActivityAlertRule.ManyDenied ? nameof(ActivityOutcome.Failure) : null,
        a.CreatedAt, a.AcknowledgedByName, a.AcknowledgedAt, a.Note,
        ActivityAlertText.IsUnusual(a.Rule), a.Usual, a.Detail);

    private static SecurityLogDto LogHealth(ActivitySource a)
    {
        long? behind = a.LogNewestRecordId is { } newest ? Math.Max(0, newest - (a.LastRecordId ?? (a.LogOldestRecordId ?? 1) - 1)) : null;
        var atRisk = behind is { } b && a.LogRecordCount is > 0 && b * 2 > a.LogRecordCount;
        return new SecurityLogDto(a.LogOldestRecordId, a.LogNewestRecordId, a.LogRecordCount, a.LogOldestEventAt, a.LogSizeBytes,
            a.LogMaxSizeBytes, a.LogFull, a.LogCheckedAt, behind, atRisk);
    }
}
