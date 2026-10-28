using System.Security.Cryptography.X509Certificates;
using DataScope.Core.Security;
using DataScope.Persistence.Configuration;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DataScope.Setup;

internal enum CheckLevel { Ok, Warn, Fail }

internal sealed record CheckResult(CheckLevel Level, string Name, string Detail);

/// <summary>Read-only preflight of an installed server's configuration and database.</summary>
internal static class Checks
{
    public static async Task<IReadOnlyList<CheckResult>> RunAsync(
        ConfigFile config, ConfigFile owner, Func<string, X509Certificate2>? loadCertificate, DateTimeOffset now, CancellationToken ct)
    {
        var results = new List<CheckResult>();
        if (!config.Exists)
        {
            results.Add(new(CheckLevel.Fail, "Settings file", $"{config.Path} does not exist. Run the installer or 'DataScope.Setup database' first."));
            return results;
        }
        results.Add(new(CheckLevel.Ok, "Settings file", config.Path));

        if (string.Equals(config.GetString("Auth", "Mode"), "Development", StringComparison.OrdinalIgnoreCase))
        {
            results.Add(new(CheckLevel.Fail, "Sign-in", "Auth:Mode is Development. Remove it; servers must use Windows sign-in."));
        }

        await CheckDatabase(config, owner, results, ct);
        CheckSecrets(config, owner, results);
        CheckRoles(config, results);
        CheckHttps(config, loadCertificate, now, results);
        CheckBackups(config, now, results);
        return results;
    }

    private static async Task CheckDatabase(ConfigFile config, ConfigFile owner, List<CheckResult> results, CancellationToken ct)
    {
        string? cs;
        try
        {
            cs = config.GetSecret("ConnectionStrings", "DataScope");
        }
        catch (SetupException ex)
        {
            results.Add(new(CheckLevel.Fail, "Database", ex.Message));
            return;
        }
        if (string.IsNullOrWhiteSpace(cs))
        {
            results.Add(new(CheckLevel.Fail, "Database", "ConnectionStrings:DataScope is not set. Run 'DataScope.Setup database'."));
            return;
        }
        try
        {
            await using var db = DatabaseProvisioner.CreateContext(cs);
            var known = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
            var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();
            var pending = known.Except(applied).Count();
            var unknown = applied.Where(m => !known.Contains(m)).ToList();
            var target = new NpgsqlConnectionStringBuilder(cs);
            results.Add(new(CheckLevel.Ok, "Database", $"Connected to {target.Database} on {target.Host} as {target.Username}."));

            if (unknown.Count > 0)
            {
                results.Add(new(CheckLevel.Fail, "Schema", $"The database has {unknown.Count} migration(s) this version doesn't know, so it belongs to a newer Data Scope. Don't run an older version against it."));
            }
            else if (pending > 0)
            {
                results.Add(new(CheckLevel.Fail, "Schema", $"{pending} migration(s) not applied. Run 'DataScope.Setup migrate'."));
            }
            else
            {
                results.Add(new(CheckLevel.Ok, "Schema", $"Up to date ({applied.Count} migrations)."));
            }

            await db.Database.OpenConnectionAsync(ct);
            var conn = (NpgsqlConnection)db.Database.GetDbConnection();
            await using var cmd = new NpgsqlCommand("SELECT rolsuper FROM pg_roles WHERE rolname = current_user", conn);
            if (await cmd.ExecuteScalarAsync(ct) is true)
            {
                results.Add(new(CheckLevel.Fail, "Database role", $"{target.Username} is a PostgreSQL superuser. Use a normal role (the installer creates one)."));
            }
            await using var schema = new NpgsqlCommand(
                "SELECT has_schema_privilege(current_user, 'public', 'CREATE') OR pg_get_userbyid(datdba) = current_user FROM pg_database WHERE datname = current_database()", conn);
            var canChangeSchema = await schema.ExecuteScalarAsync(ct) is true;
            var ownerFileExists = owner.Exists && owner.GetString(OwnerSettings.Key) is not null;
            results.Add(!canChangeSchema && ownerFileExists
                ? new(CheckLevel.Ok, "Database logins", $"The services use {target.Username}, which can't change the schema; the owner login is in {owner.Path}.")
                : new(CheckLevel.Warn, "Database logins", $"The services use {target.Username}, which can change the database schema. Run 'DataScope.Setup database' with the PostgreSQL admin password to split the owner and service logins."));
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or ArgumentException)
        {
            results.Add(new(CheckLevel.Fail, "Database", $"Could not connect: {ex.Message}"));
        }
    }

    private static void CheckSecrets(ConfigFile config, ConfigFile owner, List<CheckResult> results)
    {
        var plain = config.PlainSecrets().Concat(owner.Exists ? owner.PlainSecrets() : []).ToList();
        if (plain.Count == 0)
        {
            results.Add(new(CheckLevel.Ok, "Passwords", config.EncryptsSecrets ? "Saved encrypted for this computer (DPAPI)." : "None saved in plain text."));
        }
        else
        {
            results.Add(new(CheckLevel.Warn, "Passwords", config.EncryptsSecrets
                ? $"Saved as plain text: {string.Join(", ", plain)}. Run 'DataScope.Setup protect'."
                : $"Saved as plain text: {string.Join(", ", plain)}. They can only be encrypted on Windows."));
        }
    }

    private static void CheckRoles(ConfigFile config, List<CheckResult> results)
    {
        var admins = config.GetStrings("Auth", "RoleGroupSids", Roles.PlatformAdmin);
        results.Add(admins.Count > 0
            ? new(CheckLevel.Ok, "Administrators", $"{admins.Count} AD group(s) mapped to PlatformAdmin.")
            : new(CheckLevel.Fail, "Administrators", "No AD group is mapped to PlatformAdmin, so nobody could manage Data Scope. Run 'DataScope.Setup configure --role PlatformAdmin=<group SID>'."));
        var others = Roles.All.Where(r => r != Roles.PlatformAdmin && config.GetStrings("Auth", "RoleGroupSids", r).Count > 0).ToList();
        if (others.Count > 0)
        {
            results.Add(new(CheckLevel.Ok, "Other roles", $"Mapped: {string.Join(", ", others)}."));
        }
    }

    private static void CheckBackups(ConfigFile config, DateTimeOffset now, List<CheckResult> results)
    {
        var folder = Backups.DefaultFolder(config.Path);
        FileInfo? newest = null;
        try
        {
            newest = Directory.Exists(folder)
                ? new DirectoryInfo(folder).EnumerateFiles($"{Backups.FilePrefix}2*{BackupFormat.Extension}").MaxBy(f => f.LastWriteTimeUtc)
                : null;
        }
        catch (UnauthorizedAccessException)
        {
            results.Add(new(CheckLevel.Warn, "Backups", $"Could not open {folder}; run the check as an administrator."));
            return;
        }
        if (newest is null)
        {
            results.Add(new(CheckLevel.Warn, "Backups", $"No backup in {folder} yet. The installer schedules one every night; run 'DataScope.Setup backup' for one now."));
            return;
        }
        var age = now - new DateTimeOffset(newest.LastWriteTimeUtc, TimeSpan.Zero);
        results.Add(age > TimeSpan.FromHours(48)
            ? new(CheckLevel.Warn, "Backups", $"The newest backup, {newest.Name}, is {(int)age.TotalDays} days old. Check the \"Data Scope backup\" scheduled task.")
            : new(CheckLevel.Ok, "Backups", $"Newest: {newest.Name} ({newest.Length:N0} bytes)."));
    }

    private static void CheckHttps(ConfigFile config, Func<string, X509Certificate2>? loadCertificate, DateTimeOffset now, List<CheckResult> results)
    {
        var thumbprint = config.GetString("Https", "CertificateThumbprint");
        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            results.Add(new(CheckLevel.Warn, "HTTPS", "No certificate configured. The API will only listen on http://localhost:5000."));
            return;
        }
        var port = config.GetInt("Https", "Port") ?? 443;
        if (loadCertificate is null)
        {
            results.Add(new(CheckLevel.Warn, "HTTPS", $"Port {port}, certificate {thumbprint}. The certificate store can only be checked on Windows."));
            return;
        }
        try
        {
            using var cert = loadCertificate(thumbprint);
            var days = (cert.NotAfter - now.LocalDateTime).TotalDays;
            results.Add(days < 30
                ? new(CheckLevel.Warn, "HTTPS", $"Port {port}, {cert.Subject}. The certificate expires on {cert.NotAfter:yyyy-MM-dd}; renew it soon.")
                : new(CheckLevel.Ok, "HTTPS", $"Port {port}, {cert.Subject}, valid until {cert.NotAfter:yyyy-MM-dd}."));
        }
        catch (Exception ex) when (ex is CertificateUnusableException or System.Security.Cryptography.CryptographicException)
        {
            results.Add(new(CheckLevel.Fail, "HTTPS", ex.Message));
        }
    }
}
