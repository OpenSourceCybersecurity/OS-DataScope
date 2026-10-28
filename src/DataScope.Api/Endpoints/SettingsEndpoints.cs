using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using DataScope.Api.Auth;
using DataScope.Core.Governance;
using DataScope.Core.Security;
using DataScope.Core.Settings;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using DataScope.Persistence.Settings;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Endpoints;

/// <summary>
/// System settings (D123): the operational settings administrators may change from the web UI, saved in the database
/// and applied by both services without a restart. Auditors can read them. Security-sensitive configuration is not
/// listed and can't be saved here.
/// </summary>
internal static class SettingsEndpoints
{
    public sealed record ChoiceDto(string Value, string Label);

    public sealed record GroupDto(string Id, string Title, string Description);

    /// <param name="Value">The value in effect, as text; null when empty. Lists use <paramref name="Values"/>.</param>
    /// <param name="Source">Default (built in), File (datascope.json or appsettings.json) or Here (saved on this screen).</param>
    /// <param name="OverridesFile">Saved here while datascope.json also sets it.</param>
    public sealed record SettingDto(
        string Key, string Group, string Label, string Help, string Kind, string? Value, IReadOnlyList<string>? Values, string? Default,
        long Min, long Max, string? ZeroLabel, bool Optional, string? Unit, IReadOnlyList<ChoiceDto> Choices, string Applies,
        string Source, bool OverridesFile, DateTimeOffset? UpdatedAt, string? UpdatedByName);

    /// <param name="EmailSignIn">Whether datascope.json holds an SMTP sign-in; it is set with the setup tool, never here.</param>
    public sealed record SettingsDto(IReadOnlyList<GroupDto> Groups, IReadOnlyList<SettingDto> Settings, string TimeZone, bool EmailSignIn);

    /// <summary>Keys to change. null puts a setting back to its file or built-in value; "" empties an optional one.</summary>
    public sealed record SaveRequest(Dictionary<string, JsonElement>? Values);

    public sealed record TestEmailRequest(string? To);

    public sealed record TestEmailDto(long Id, string State, string? Error, DateTimeOffset? SentAt);

    private const int MaxChanges = 100;

    public static void MapSettings(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/settings").RequireAuthorization(Policies.ReadAuditLog);

        group.MapGet("/", async (IConfiguration config, SystemSettingsConfigurationProvider saved, DataScopeDbContext db, CancellationToken ct) =>
            Results.Ok(await DescribeAsync(config, saved, db, ct)));

        group.MapPut("/", async (SaveRequest request, ClaimsPrincipal user, IConfiguration config, SystemSettingsConfigurationProvider saved,
            SystemSettingsRefresher refresher, DataScopeDbContext db, AuditLogWriter audit, TimeProvider clock, CancellationToken ct) =>
        {
            var values = request.Values ?? [];
            if (values.Count is 0 or > MaxChanges)
            {
                return Invalid("values", $"Send 1 to {MaxChanges} settings to change.");
            }
            var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            var changes = new List<(SettingDefinition Definition, bool Reset, string? Text, IReadOnlyList<string>? List)>();
            foreach (var (key, json) in values)
            {
                if (SystemSettings.Find(key) is not { } definition)
                {
                    errors[key] = ["This setting can't be changed here."];
                    continue;
                }
                if (json.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    changes.Add((definition, true, null, null));
                    continue;
                }
                if (!TryRead(definition, json, out var text, out var list))
                {
                    errors[definition.Key] = [definition.Kind == SettingKind.EmailList ? "Send a list of addresses." : "Send text, a number or true or false."];
                    continue;
                }
                if (SystemSettings.Problem(definition, text, list) is { } problem)
                {
                    errors[definition.Key] = [problem];
                    continue;
                }
                changes.Add((definition, false, text, list));
            }
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            // Settings that depend on each other are checked with every other setting as it will be.
            var pending = changes.ToDictionary(c => c.Definition.Key, StringComparer.OrdinalIgnoreCase);
            string? Effective(string key) => pending.TryGetValue(key, out var c)
                ? c.Reset ? FileValue(config, saved, key) : c.Text ?? ""
                : config[key];
            foreach (var (key, problem) in SystemSettings.CrossProblems(Effective))
            {
                errors[key] = [problem];
            }
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var actor = Actor.From(user);
            var now = clock.GetUtcNow();
            var keys = changes.Select(c => c.Definition.Key).ToList();
            var rows = await db.SystemSettings.Where(s => keys.Contains(s.Key)).ToDictionaryAsync(s => s.Key, StringComparer.OrdinalIgnoreCase, ct);
            var audited = new List<object>();
            foreach (var (definition, reset, text, list) in changes)
            {
                rows.TryGetValue(definition.Key, out var row);
                var before = Shown(definition, config);
                if (reset)
                {
                    if (row is null)
                    {
                        continue;
                    }
                    db.SystemSettings.Remove(row);
                    var after = definition.Kind == SettingKind.EmailList ? ShownList(FileList(config, saved, definition.Key)) : ShownText(definition, FileValue(config, saved, definition.Key));
                    audited.Add(new { definition.Key, Before = before, After = after, Reset = true });
                    continue;
                }
                var json = SystemSettingsConfigurationProvider.Encode(definition, text, list);
                if (row is not null && row.Value == json)
                {
                    continue;
                }
                if (row is null)
                {
                    db.SystemSettings.Add(new SystemSetting { Key = definition.Key, Value = json, UpdatedAt = now, UpdatedBySid = actor.Sid, UpdatedByName = actor.Name });
                }
                else
                {
                    row.Value = json;
                    row.UpdatedAt = now;
                    row.UpdatedBySid = actor.Sid;
                    row.UpdatedByName = actor.Name;
                }
                audited.Add(new { definition.Key, Before = before, After = definition.Kind == SettingKind.EmailList ? ShownList(list) : ShownText(definition, text ?? ""), Reset = false });
            }
            if (audited.Count > 0)
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                await db.SaveChangesAsync(ct);
                await audit.AppendAsync(actor.Sid, actor.Name, "settings.change", "system settings", new { Changes = audited }, ct);
                await tx.CommitAsync(ct);
                // This service applies the change now; the scanner service reads it within 30 seconds.
                await refresher.RefreshAsync(ct);
            }
            return Results.Ok(await DescribeAsync(config, saved, db, ct));
        }).RequireAuthorization(Policies.ManagePlatform);

        // Queues one email to check the mail server settings; the scanner service sends it within a minute.
        group.MapPost("/test-email", async (TestEmailRequest request, ClaimsPrincipal user, NotificationStore outbox, DataScopeDbContext db,
            AuditLogWriter audit, CancellationToken ct) =>
        {
            var to = request.To?.Trim() ?? "";
            if (to.Length == 0)
            {
                return Invalid("to", "Enter the address to send the test to.");
            }
            if (SystemSettings.Problem(SystemSettings.Find("Notifications:Smtp:From")!, to) is { } problem)
            {
                return Invalid("to", problem);
            }
            var actor = Actor.From(user);
            var key = $"test-email:{Guid.CreateVersion7():N}";
            await outbox.EnqueueAsync(new OutgoingNotice(key, NotificationKind.TestEmail, null, actor.Sid, actor.Name, to,
                "Data Scope test email",
                $"This is a test email from Data Scope, sent by {actor.Name} from System settings.\n\nIf you can read it, Data Scope can send review emails, reminders and alerts."), ct);
            var id = await db.Outbox.Where(m => m.IdempotencyKey == key).Select(m => m.Id).SingleAsync(ct);
            await audit.AppendAsync(actor.Sid, actor.Name, "settings.test-email", to, new { Id = id }, ct);
            return Results.Accepted($"/api/v1/settings/test-email/{id}", new TestEmailDto(id, nameof(OutboxState.Pending), null, null));
        }).RequireAuthorization(Policies.ManagePlatform);

        group.MapGet("/test-email/{id:long}", async (long id, DataScopeDbContext db, CancellationToken ct) =>
            await db.Outbox.AsNoTracking().Where(m => m.Id == id && m.Kind == NotificationKind.TestEmail)
                .Select(m => new TestEmailDto(m.Id, m.State.ToString(), m.LastError, m.SentAt)).SingleOrDefaultAsync(ct) is { } found
                ? Results.Ok(found)
                : Results.NotFound()).RequireAuthorization(Policies.ManagePlatform);
    }

    private static async Task<SettingsDto> DescribeAsync(IConfiguration config, SystemSettingsConfigurationProvider saved, DataScopeDbContext db, CancellationToken ct)
    {
        var rows = await db.SystemSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, StringComparer.OrdinalIgnoreCase, ct);
        var settings = SystemSettings.All.Select(d =>
        {
            var here = saved.Has(d.Key);
            var inFile = FileProviders(config, saved).Any(p => p.TryGet(d.Key, out _) || p.TryGet($"{d.Key}:0", out _));
            rows.TryGetValue(d.Key, out var row);
            var list = d.Kind == SettingKind.EmailList ? List(config, d.Key) : null;
            var value = d.Kind == SettingKind.EmailList ? null : Text(config, d);
            return new SettingDto(d.Key, d.Group, d.Label, d.Help, d.Kind.ToString(), value, list, d.Default, d.Min, d.Max, d.ZeroLabel, d.Optional, d.Unit,
                [.. d.Choices.Select(c => new ChoiceDto(c.Value, c.Label))], d.Applies,
                here ? "Here" : inFile ? "File" : "Default", here && inFile, here ? row?.UpdatedAt : null, here ? row?.UpdatedByName : null);
        }).ToList();
        var signIn = !string.IsNullOrWhiteSpace(config["Notifications:Smtp:UserName"]);
        return new SettingsDto([.. SystemSettings.Groups.Select(g => new GroupDto(g.Id, g.Title, g.Description))], settings, ServerEndpoints.Zone.DisplayName, signIn);
    }

    /// <summary>The value in effect: configuration, or the built-in value when nothing sets it. Empty is shown as null.</summary>
    private static string? Text(IConfiguration config, SettingDefinition d) => config[d.Key] is { } v ? (v.Length == 0 ? null : v) : d.Default;

    private static List<string> List(IConfiguration config, string key) =>
        [.. config.GetSection(key).GetChildren().OrderBy(c => int.TryParse(c.Key, out var i) ? i : int.MaxValue).Select(c => c.Value).OfType<string>().Where(v => v.Length > 0)];

    private static IEnumerable<IConfigurationProvider> FileProviders(IConfiguration config, SystemSettingsConfigurationProvider saved) =>
        config is IConfigurationRoot root ? root.Providers.Where(p => !ReferenceEquals(p, saved)) : [];

    /// <summary>What the files set for a key, ignoring the UI: what a reset goes back to. Null when nothing sets it.</summary>
    private static string? FileValue(IConfiguration config, SystemSettingsConfigurationProvider saved, string key)
    {
        string? value = null;
        foreach (var p in FileProviders(config, saved))
        {
            if (p.TryGet(key, out var v))
            {
                value = v;
            }
        }
        return value;
    }

    private static List<string> FileList(IConfiguration config, SystemSettingsConfigurationProvider saved, string key)
    {
        var items = new SortedDictionary<int, string>();
        foreach (var p in FileProviders(config, saved))
        {
            foreach (var child in p.GetChildKeys([], key))
            {
                if (int.TryParse(child, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && p.TryGet($"{key}:{child}", out var v) && !string.IsNullOrEmpty(v))
                {
                    items[i] = v;
                }
            }
        }
        return [.. items.Values];
    }

    private static string Shown(SettingDefinition d, IConfiguration config) =>
        d.Kind == SettingKind.EmailList ? ShownList(List(config, d.Key)) : ShownText(d, config[d.Key]);

    /// <summary>A value as the audit log records it: null is not set (the built-in value applies). None of these settings is a secret.</summary>
    private static string ShownText(SettingDefinition d, string? raw) => raw is null ? d.Default ?? "(empty)" : raw.Length == 0 ? "(empty)" : raw;

    private static string ShownList(IReadOnlyList<string>? list) => list is { Count: > 0 } ? string.Join(", ", list) : "(none)";

    private static bool TryRead(SettingDefinition d, JsonElement json, out string? text, out IReadOnlyList<string>? list)
    {
        text = null;
        list = null;
        if (d.Kind == SettingKind.EmailList)
        {
            if (json.ValueKind != JsonValueKind.Array || json.GetArrayLength() > SystemSettings.MaxEmailRecipients * 2
                || json.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String))
            {
                return false;
            }
            list = [.. json.EnumerateArray().Select(e => e.GetString()!.Trim()).Where(e => e.Length > 0)];
            return true;
        }
        switch (json.ValueKind)
        {
            case JsonValueKind.String:
                text = json.GetString()!.Trim();
                return true;
            case JsonValueKind.Number when json.TryGetInt64(out var n):
                text = n.ToString(CultureInfo.InvariantCulture);
                return true;
            case JsonValueKind.True or JsonValueKind.False when d.Kind == SettingKind.Toggle:
                text = json.GetBoolean() ? "true" : "false";
                return true;
            default:
                return false;
        }
    }

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
