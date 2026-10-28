using DataScope.Core.Inventory;
using DataScope.Core.Security;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Progress;

/// <summary>Progress over time (D165): the posture day by day, from the rows <see cref="ProgressRecorder"/> keeps.</summary>
internal static class ProgressEndpoints
{
    /// <summary>One day's posture.</summary>
    public sealed record PointDto(
        DateOnly Day, DateTimeOffset TakenAt, DateTimeOffset? LastScanAt, int RisksHigh, int RisksMedium, int RisksLow, int RisksAccepted, int RisksSnoozed,
        bool RisksLimited, long Files, long FilesNotRead, long Restricted, long Confidential, long SensitiveOpen,
        int SharesUnowned)
    {
        public static PointDto From(PostureSnapshot s) => new(s.Day, s.TakenAt, s.LastScanAt, s.RisksHigh, s.RisksMedium, s.RisksLow, s.RisksAccepted,
            s.RisksSnoozed, s.RisksLimited, s.Files, s.FilesNotRead, s.Restricted, s.Confidential, s.SensitiveOpen,
            s.SharesUnowned);
    }

    /// <param name="From">The first day the range covers.</param>
    /// <param name="KeptSince">The first day Data Scope kept, so a range starting earlier says why it is short.</param>
    /// <param name="Thinned">More days than <see cref="MaxPoints"/>: each week's last day is shown, with the range's first and last.</param>
    public sealed record ProgressDto(int Days, DateOnly From, DateOnly? KeptSince, IReadOnlyList<PointDto> Points, bool Thinned);

    public const int DefaultDays = 90;
    public const int MaxDays = ProgressRecorder.KeepDays;
    public const int MaxPoints = 180;

    public static void MapProgress(this RouteGroupBuilder api)
    {
        api.MapGet("/progress", async (int? days, ProgressRecorder recorder, DataScopeDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            if (days is < 7 or > MaxDays)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["days"] = [$"Use 7 to {MaxDays} days."] });
            }
            var span = days ?? DefaultDays;
            var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            // The first visit of a day measures it, so the page never waits for the background check to show today.
            if (!await db.PostureSnapshots.AnyAsync(s => s.Day == today, ct))
            {
                await recorder.RecordAsync(ct);
            }
            var from = today.AddDays(1 - span);
            var rows = await db.PostureSnapshots.AsNoTracking().Where(s => s.Day >= from).OrderBy(s => s.Day).ToListAsync(ct);
            var keptSince = await db.PostureSnapshots.MinAsync(s => (DateOnly?)s.Day, ct);
            var thinned = rows.Count > MaxPoints;
            if (thinned)
            {
                rows = Thin(rows);
            }
            return Results.Ok(new ProgressDto(span, from, keptSince, [.. rows.Select(PointDto.From)], thinned));
        }).RequireAuthorization(Policies.ReadPosture);
    }

    /// <summary>Each week's last day, keeping the first day so changes are measured from the start of the range.</summary>
    internal static List<PostureSnapshot> Thin(List<PostureSnapshot> rows)
    {
        var weekly = rows.GroupBy(r => r.Day.DayNumber / 7).Select(g => g.Last()).ToList();
        if (weekly[0].Day != rows[0].Day)
        {
            weekly.Insert(0, rows[0]);
        }
        return weekly;
    }
}
