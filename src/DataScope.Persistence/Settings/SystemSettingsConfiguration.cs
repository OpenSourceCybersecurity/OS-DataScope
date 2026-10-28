using System.Text.Json;
using DataScope.Core.Settings;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace DataScope.Persistence.Settings;

/// <summary>
/// Settings saved on the System settings screen, as configuration (D123). Added after datascope.json, so a value saved
/// in the UI wins over the files. Only keys in <see cref="SystemSettings.All"/> with valid values are ever applied, so
/// a row put in the table by other means can't reach configuration the UI does not manage.
/// </summary>
public sealed class SystemSettingsConfigurationProvider : ConfigurationProvider
{
    private readonly Lock _gate = new();
    private IReadOnlyDictionary<string, string?> _applied = new Dictionary<string, string?>();

    /// <summary>Replaces the saved settings. Raises a reload only when something changed.</summary>
    public bool Apply(IReadOnlyDictionary<string, string?> values)
    {
        lock (_gate)
        {
            if (values.Count == _applied.Count && values.All(kv => _applied.TryGetValue(kv.Key, out var v) && v == kv.Value))
            {
                return false;
            }
            _applied = values;
            Data = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);
        }
        OnReload();
        return true;
    }

    /// <summary>Whether the UI saved this key (or, for a list, its entries).</summary>
    public bool Has(string key) => TryGet(key, out _) || TryGet($"{key}:0", out _);

    /// <summary>Turns saved rows into configuration keys, leaving out unknown keys and values that fail their checks.</summary>
    public static Dictionary<string, string?> ToConfiguration(IEnumerable<SystemSetting> rows, Action<string>? skipped = null)
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (SystemSettings.Find(row.Key) is not { } definition || !TryDecode(definition, row.Value, out var text, out var list)
                || SystemSettings.Problem(definition, text, list) is not null)
            {
                skipped?.Invoke(row.Key);
                continue;
            }
            if (definition.Kind == SettingKind.EmailList)
            {
                // Every index up to the limit, so entries from datascope.json beyond the saved list don't show through.
                for (var i = 0; i < SystemSettings.MaxEmailRecipients; i++)
                {
                    data[$"{definition.Key}:{i}"] = i < list!.Count ? list[i] : "";
                }
            }
            else
            {
                // Empty clears an optional setting: the binder reads it as null.
                data[definition.Key] = text ?? "";
            }
        }
        return data;
    }

    /// <summary>The JSON stored for a value: a string, null, or an array of strings.</summary>
    public static string Encode(SettingDefinition definition, string? text, IReadOnlyList<string>? list) =>
        definition.Kind == SettingKind.EmailList ? JsonSerializer.Serialize(list ?? []) : JsonSerializer.Serialize(string.IsNullOrEmpty(text) ? null : text);

    public static bool TryDecode(SettingDefinition definition, string json, out string? text, out IReadOnlyList<string>? list)
    {
        text = null;
        list = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (definition.Kind == SettingKind.EmailList)
            {
                if (root.ValueKind != JsonValueKind.Array || root.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String))
                {
                    return false;
                }
                list = [.. root.EnumerateArray().Select(e => e.GetString()!)];
                return true;
            }
            switch (root.ValueKind)
            {
                case JsonValueKind.Null:
                    return true;
                case JsonValueKind.String:
                    text = root.GetString();
                    return true;
                default:
                    return false;
            }
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

internal sealed class SystemSettingsConfigurationSource(SystemSettingsConfigurationProvider provider) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) => provider;
}

/// <summary>Reads the saved settings from the database into configuration. Each service runs it on start and every 30 seconds.</summary>
public sealed partial class SystemSettingsRefresher(IServiceScopeFactory scopes, SystemSettingsConfigurationProvider provider, ILogger<SystemSettingsRefresher> log)
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <summary>Reads the table now. Returns false when it couldn't, keeping what was applied before.</summary>
    public async Task<bool> RefreshAsync(CancellationToken ct)
    {
        List<SystemSetting> rows;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<DataScopeDbContext>();
            rows = await db.SystemSettings.AsNoTracking().ToListAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            // The database isn't upgraded yet: nothing saved.
            rows = [];
        }
        catch (Exception ex) when (ex is NpgsqlException or DbUpdateException or InvalidOperationException or TimeoutException)
        {
            LogUnavailable(ex.GetType().Name);
            return false;
        }
        if (provider.Apply(SystemSettingsConfiguration(rows)))
        {
            LogApplied(rows.Count);
        }
        return true;
    }

    /// <summary>Refreshes until <paramref name="ct"/> is cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, ct);
                await RefreshAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private Dictionary<string, string?> SystemSettingsConfiguration(List<SystemSetting> rows) =>
        SystemSettingsConfigurationProvider.ToConfiguration(rows, LogSkipped);

    [LoggerMessage(LogLevel.Information, "Applied {Count} settings saved in Data Scope")]
    private partial void LogApplied(int count);

    [LoggerMessage(LogLevel.Warning, "Ignored the saved setting {Key}: it is not one Data Scope manages, or its value is not valid")]
    private partial void LogSkipped(string key);

    [LoggerMessage(LogLevel.Warning, "Could not read saved settings ({ExceptionType}); keeping the current ones")]
    private partial void LogUnavailable(string exceptionType);
}

public static class SystemSettingsSetup
{
    /// <summary>
    /// Adds the saved settings to configuration, after every file, and registers what reads them. Call after the
    /// configuration files are added; the service then runs <see cref="SystemSettingsRefresher"/> on start.
    /// </summary>
    public static IServiceCollection AddSystemSettings(this IServiceCollection services, IConfigurationBuilder configuration)
    {
        var provider = new SystemSettingsConfigurationProvider();
        configuration.Add(new SystemSettingsConfigurationSource(provider));
        services.AddSingleton(provider);
        services.AddSingleton<SystemSettingsRefresher>();
        return services;
    }
}
