using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DataScope.Core.Security;
using DataScope.Persistence.Configuration;
using DataScope.Setup;
using Npgsql;

return await SetupProgram.RunAsync(args, Console.Out, Console.Error, CancellationToken.None);

namespace DataScope.Setup
{
    /// <summary>
    /// Server setup commands, run by Install-DataScope.ps1 and by administrators.
    /// Nothing here touches file shares or Active Directory.
    /// </summary>
    internal static partial class SetupProgram
    {
        public const string AdminPasswordVariable = "DATASCOPE_PG_ADMIN_PASSWORD";
        public const string SecretValueVariable = "DATASCOPE_SECRET_VALUE";

        private static readonly Dictionary<string, string[]> NamedSecrets = new(StringComparer.OrdinalIgnoreCase)
        {
            ["smtp-password"] = ["Notifications", "Smtp", "Password"],
            ["directory-password"] = ["Directory", "Password"],
        };

        private const string Usage = """
            Data Scope server setup

              DataScope.Setup database  [--host localhost] [--port 5432] [--admin-user postgres] [--database datascope]
                                        [--owner-user datascope_owner] [--app-user datascope_app] [--rotate-password]
                                        [--config <file>] [--owner-config <file>]
                  Creates the database and its two logins (if missing), applies migrations and saves the connections.
                  The owner login changes the schema and is saved where only administrators can read it; the services
                  use the app login. A database from an older version is split between the two. The admin password
                  is read from DATASCOPE_PG_ADMIN_PASSWORD or asked for; it is never saved.

              DataScope.Setup migrate   [--config <file>] [--owner-config <file>]
                  Applies pending database migrations as the owner login (used on upgrade).

              DataScope.Setup configure [--https-port 443] [--certificate-thumbprint <thumbprint>]
                                        [--role <Role>=<group SID>]... [--clear-roles] [--pg-bin <dir>] [--config <file>]
                  Sets HTTPS and maps AD groups (by SID) to roles: PlatformAdmin, SecurityAnalyst, Auditor, DataOwner, Viewer.
                  --pg-bin saves the folder with PostgreSQL's pg_dump and pg_restore, for backups.

              DataScope.Setup secret    --name smtp-password|directory-password [--clear] [--config <file>]
                  Saves a password, encrypted for this server. The value is read from DATASCOPE_SECRET_VALUE or asked for.

              DataScope.Setup protect   [--config <file>] [--owner-config <file>]
                  Encrypts any password still saved as plain text (settings from an older version).

              DataScope.Setup backup    [--folder <dir> | --out <file>] [--keep <n>] [--before-upgrade] [--pg-bin <dir>] [--config <file>] [--owner-config <file>]
                  Saves an encrypted backup of the database, by default in %ProgramData%\DataScope\backups, keeping the
                  newest --keep backups there. The first backup creates this server's backup key. --before-upgrade names
                  the file datascope-before-upgrade-*, which --keep leaves alone.

              DataScope.Setup backup-key [--config <file>] [--owner-config <file>]
                  Shows the backup key. Keep a copy somewhere safe away from this server: restoring a backup on
                  another server needs it.

              DataScope.Setup verify-backup --from <file> [--pg-bin <dir>] [--config <file>] [--owner-config <file>]
                  Reads a backup end to end and checks it can be restored. Changes nothing.

              DataScope.Setup restore   --from <file> --confirm [--pg-bin <dir>] [--config <file>] [--owner-config <file>]
                  Replaces the database with the backup. Stop both Data Scope services first. A backup of the current
                  database is taken first, and migrations are applied when the backup is from an older version. The
                  key is read from this server or DATASCOPE_BACKUP_KEY, or asked for.

              DataScope.Setup check     [--config <file>] [--owner-config <file>]
                  Read-only preflight. Exit code 0 when nothing failed.

              DataScope.Setup support-bundle [--out <file or folder>] [--include-event-log] [--config <file>]
                  Saves a zip of diagnostics for support, with no passwords, names, paths or file contents. Nothing is
                  sent anywhere; read it before sending it.

            The settings file defaults to %ProgramData%\DataScope\config\datascope.json. Passwords in it are encrypted
            with Windows DPAPI for this computer.
            """;

        [GeneratedRegex(@"^S-1-[0-9]+(-[0-9]+){1,14}$")]
        private static partial Regex Sid();

        public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken ct)
        {
            try
            {
                if (args.Length == 0 || args[0] is "help" or "--help" or "-h" or "/?")
                {
                    output.WriteLine(Usage);
                    return args.Length == 0 ? 2 : 0;
                }
                var line = CommandLine.Parse(args, new HashSet<string>(["rotate-password", "clear-roles", "clear", "confirm", "before-upgrade", "include-event-log"], StringComparer.OrdinalIgnoreCase));
                return line.Command switch
                {
                    "database" => await Database(line, output, ct),
                    "migrate" => await Migrate(line, output, ct),
                    "configure" => Configure(line, output),
                    "secret" => Secret(line, output),
                    "protect" => Protect(line, output),
                    "backup" => await Backup(line, output, ct),
                    "backup-key" => BackupKey(line, output),
                    "verify-backup" => await VerifyBackup(line, output, ct),
                    "restore" => await Restore(line, output, ct),
                    "check" => await Check(line, output, ct),
                    "support-bundle" => await Bundle(line, output, ct),
                    _ => throw new UsageException($"Unknown command '{line.Command}'."),
                };
            }
            catch (UsageException ex)
            {
                error.WriteLine($"Error: {ex.Message}");
                error.WriteLine("Run 'DataScope.Setup help' for usage.");
                return 2;
            }
            catch (SetupException ex)
            {
                error.WriteLine($"Error: {ex.Message}");
                return 1;
            }
        }

        private static ConfigFile LoadConfig(CommandLine line)
        {
            var path = line.Optional("config") ?? DataScopeConfigFile.ResolvePath()
                ?? throw new UsageException("--config is required on this platform.");
            return ConfigFile.Load(path);
        }

        private static async Task<int> Database(CommandLine line, TextWriter output, CancellationToken ct)
        {
            line.RejectUnknown("host", "port", "admin-user", "database", "owner-user", "app-user", "rotate-password", "config", "owner-config");
            var config = LoadConfig(line);
            var ownerFile = OwnerSettings.Load(line, config);
            var existingApp = config.GetSecret("ConnectionStrings", "DataScope");
            var existingOwner = ownerFile.GetSecret(OwnerSettings.Key);
            // On an existing server, keep pointing at the same PostgreSQL unless told otherwise.
            var current = existingApp is null ? null : new NpgsqlConnectionStringBuilder(existingApp);
            var admin = new NpgsqlConnectionStringBuilder
            {
                Host = line.Get("host", current?.Host ?? "localhost"),
                Port = line.GetInt("port", current?.Port ?? 5432, 1, 65535),
                Username = line.Get("admin-user", "postgres"),
                Password = ReadSecret(AdminPasswordVariable, "PostgreSQL admin password"),
                Database = "postgres",
            };
            var result = await DatabaseProvisioner.ProvisionAsync(
                new ProvisionRequest(
                    admin.ConnectionString,
                    line.Get("database", current?.Database ?? "datascope"),
                    line.Get("owner-user", existingOwner is null ? "datascope_owner" : new NpgsqlConnectionStringBuilder(existingOwner).Username!),
                    line.Get("app-user", current?.Username ?? "datascope_app"),
                    existingOwner, existingApp, line.Flag("rotate-password")),
                ct);
            config.SetSecret(result.AppConnection, "ConnectionStrings", "DataScope");
            config.Save();
            ownerFile.SetSecret(result.OwnerConnection, OwnerSettings.Key);
            OwnerSettings.Save(ownerFile);

            var owner = new NpgsqlConnectionStringBuilder(result.OwnerConnection);
            var app = new NpgsqlConnectionStringBuilder(result.AppConnection);
            output.WriteLine(result.CreatedDatabase ? $"Created database {owner.Database}." : $"Database {owner.Database} already exists; kept it.");
            if (result.SplitExistingDatabase)
            {
                output.WriteLine($"Moved the schema from {app.Username} to the owner login {owner.Username}; {app.Username} can now only read and write rows.");
            }
            foreach (var role in result.CreatedRoles)
            {
                output.WriteLine($"Created login role {role}.");
            }
            foreach (var role in result.PasswordsSet)
            {
                output.WriteLine($"Set a new password for {role}.");
            }
            output.WriteLine(result.AppliedMigrations.Count == 0 ? "Schema already up to date." : $"Applied {result.AppliedMigrations.Count} migration(s).");
            var encrypted = config.EncryptsSecrets ? ", passwords encrypted for this computer" : "";
            output.WriteLine($"Saved the service connection to {config.Path} and the owner connection to {ownerFile.Path}{encrypted}.");
            return 0;
        }

        private static async Task<int> Migrate(CommandLine line, TextWriter output, CancellationToken ct)
        {
            line.RejectUnknown("config", "owner-config");
            var config = LoadConfig(line);
            var app = config.GetSecret("ConnectionStrings", "DataScope")
                ?? throw new SetupException($"{config.Path} has no database connection. Run 'DataScope.Setup database' first.");
            var owner = OwnerSettings.Load(line, config).GetSecret(OwnerSettings.Key);
            try
            {
                var applied = owner is null
                    ? await DatabaseProvisioner.MigrateAsync(app, appUser: null, ct)
                    : await DatabaseProvisioner.MigrateAsync(owner, new NpgsqlConnectionStringBuilder(app).Username, ct);
                output.WriteLine(applied.Count == 0 ? "Schema already up to date." : $"Applied {applied.Count} migration(s): {string.Join(", ", applied)}.");
                if (owner is null)
                {
                    output.WriteLine("Note: this server still uses one database login for everything. Run 'DataScope.Setup database' with the PostgreSQL admin password to give the services a login that can't change the schema.");
                }
                return 0;
            }
            catch (NpgsqlException ex)
            {
                throw new SetupException($"Migration failed: {ex.Message}");
            }
        }

        private static int Secret(CommandLine line, TextWriter output)
        {
            line.RejectUnknown("name", "clear", "config");
            var name = line.Optional("name") ?? throw new UsageException($"--name is required: {string.Join(" or ", NamedSecrets.Keys)}.");
            if (!NamedSecrets.TryGetValue(name, out var keys))
            {
                throw new UsageException($"--name must be {string.Join(" or ", NamedSecrets.Keys)}.");
            }
            var config = LoadConfig(line);
            if (line.Flag("clear"))
            {
                config.Set(null, keys);
                config.Save();
                output.WriteLine($"Removed {string.Join(':', keys)} from {config.Path}.");
                return 0;
            }
            var value = ReadSecret(SecretValueVariable, $"Value for {name}");
            if (value.Length == 0)
            {
                throw new UsageException("The value is empty. Use --clear to remove the setting.");
            }
            config.SetSecret(value, keys);
            config.Save();
            output.WriteLine(config.EncryptsSecrets
                ? $"Saved {string.Join(':', keys)} to {config.Path}, encrypted for this computer. Restart the Data Scope Scanner service to use it."
                : $"Saved {string.Join(':', keys)} to {config.Path} as plain text: this system has no DPAPI. Restart the Data Scope Scanner service to use it.");
            return 0;
        }

        private static int Protect(CommandLine line, TextWriter output)
        {
            line.RejectUnknown("config", "owner-config");
            var config = LoadConfig(line);
            if (!config.EncryptsSecrets)
            {
                throw new SetupException("Passwords can only be encrypted on Windows (DPAPI).");
            }
            var changed = 0;
            foreach (var file in new[] { config, OwnerSettings.Load(line, config) })
            {
                if (file.Exists)
                {
                    var n = file.ProtectSecrets();
                    if (n > 0)
                    {
                        file.Save();
                        changed += n;
                    }
                }
            }
            output.WriteLine(changed == 0 ? "No plain-text passwords found." : $"Encrypted {changed} password setting(s).");
            return 0;
        }

        /// <summary>The owner login's connection, required by the commands that work on the whole database.</summary>
        private static (ConfigFile Config, ConfigFile Owner, string OwnerConnection) OwnerConnection(CommandLine line)
        {
            var config = LoadConfig(line);
            var owner = OwnerSettings.Load(line, config);
            var cs = owner.GetSecret(OwnerSettings.Key)
                ?? throw new SetupException($"{owner.Path} has no owner login. Run 'DataScope.Setup database' (the installer does) to set it up.");
            return (config, owner, cs);
        }

        private static PgTools Tools(CommandLine line, ConfigFile config) => PgTools.Locate(line.Optional("pg-bin"), config.GetString("Backup", "PgBinPath"));

        private static async Task<int> Backup(CommandLine line, TextWriter output, CancellationToken ct)
        {
            line.RejectUnknown("folder", "out", "keep", "before-upgrade", "pg-bin", "config", "owner-config");
            if (line.Optional("folder") is not null && line.Optional("out") is not null)
            {
                throw new UsageException("Use --folder or --out, not both.");
            }
            int? keep = line.Optional("keep") is null ? null : line.GetInt("keep", 14, 1, 1000);
            if (keep is not null && line.Optional("out") is not null)
            {
                throw new UsageException("--keep works with a backup folder, not with --out.");
            }
            var (config, owner, cs) = OwnerConnection(line);
            var tools = Tools(line, config);
            var (key, created) = Backups.EnsureKey(owner);
            var outFile = line.Optional("out");
            var target = outFile ?? line.Optional("folder") ?? Backups.DefaultFolder(config.Path);
            // The installer's copy before an upgrade is named apart, so --keep never removes it to make room for nightly ones.
            var kind = line.Flag("before-upgrade") ? "before-upgrade-" : "";
            var result = await Backups.CreateAsync(tools, cs, key, target, outFile is null, keep, DateTimeOffset.UtcNow, kind, ct);
            output.WriteLine($"Saved {result.Path} ({result.Bytes:N0} bytes, SHA-256 {result.Sha256}).");
            foreach (var old in result.Deleted)
            {
                output.WriteLine($"Removed the older backup {old}.");
            }
            if (created)
            {
                output.WriteLine("Created this server's backup key. Run 'DataScope.Setup backup-key' and keep a copy somewhere safe away from this server:");
                output.WriteLine("without it, these backups can't be restored anywhere else.");
            }
            return 0;
        }

        private static int BackupKey(CommandLine line, TextWriter output)
        {
            line.RejectUnknown("config", "owner-config");
            var config = LoadConfig(line);
            var owner = OwnerSettings.Load(line, config);
            if (owner.GetString(OwnerSettings.Key) is null)
            {
                throw new SetupException($"{owner.Path} has no owner login. Run 'DataScope.Setup database' first.");
            }
            var (key, created) = Backups.EnsureKey(owner);
            output.WriteLine(created ? "Created this server's backup key:" : "This server's backup key:");
            output.WriteLine();
            output.WriteLine($"  {BackupFormat.FormatKey(key)}");
            output.WriteLine();
            output.WriteLine($"Key id {BackupFormat.KeyId(key)} (every backup names the id of the key it needs).");
            output.WriteLine("Store it in your password manager or safe, not on this server. Anyone with the key and a backup file can read the backup.");
            return 0;
        }

        /// <summary>The key a backup needs: this server's if it matches, else DATASCOPE_BACKUP_KEY or typed in.</summary>
        private static async Task<(byte[] Key, BackupHeader Header, bool FromServer)> KeyFor(string path, ConfigFile owner, CancellationToken ct)
        {
            if (!File.Exists(path))
            {
                throw new SetupException($"{path} does not exist.");
            }
            BackupHeader header;
            await using (var file = File.OpenRead(path))
            {
                header = await BackupFormat.ReadHeaderAsync(file, ct);
            }
            if (owner.GetSecret(Backups.KeyName) is { } saved && BackupFormat.KeyId(BackupFormat.ParseKey(saved)) == header.KeyId)
            {
                return (BackupFormat.ParseKey(saved), header, true);
            }
            var key = BackupFormat.ParseKey(ReadSecret(Backups.KeyVariable, $"Backup key {header.KeyId}"));
            if (BackupFormat.KeyId(key) != header.KeyId)
            {
                throw new SetupException($"That is not the key this backup was made with (it needs key {header.KeyId}).");
            }
            return (key, header, false);
        }

        private static async Task<int> VerifyBackup(CommandLine line, TextWriter output, CancellationToken ct)
        {
            line.RejectUnknown("from", "pg-bin", "config", "owner-config");
            var path = line.Optional("from") ?? throw new UsageException("--from <backup file> is required.");
            var config = LoadConfig(line);
            var owner = OwnerSettings.Load(line, config);
            var (key, _, _) = await KeyFor(path, owner, ct);
            var header = await Backups.VerifyAsync(Tools(line, config), path, key, ct);
            output.WriteLine($"{Path.GetFileName(path)} is complete and readable: database {header.Database}, made {header.CreatedAt:yyyy-MM-dd HH:mm} UTC, schema {header.LatestMigration}.");
            return 0;
        }

        private static async Task<int> Restore(CommandLine line, TextWriter output, CancellationToken ct)
        {
            line.RejectUnknown("from", "confirm", "pg-bin", "config", "owner-config");
            var path = line.Optional("from") ?? throw new UsageException("--from <backup file> is required.");
            if (!line.Flag("confirm"))
            {
                throw new UsageException("Restoring replaces everything in the Data Scope database. Stop both services, then run again with --confirm.");
            }
            var (config, owner, cs) = OwnerConnection(line);
            var app = config.GetSecret("ConnectionStrings", "DataScope")
                ?? throw new SetupException($"{config.Path} has no database connection. Run 'DataScope.Setup database' first.");
            var (key, header, fromServer) = await KeyFor(path, owner, ct);
            byte[] serverKey;
            if (!fromServer && owner.GetString(Backups.KeyName) is null)
            {
                // A new server restoring an old one's backup carries on with the same key.
                owner.SetSecret(BackupFormat.FormatKey(key), Backups.KeyName);
                OwnerSettings.Save(owner);
                serverKey = key;
                output.WriteLine($"This server had no backup key; it now uses the one you supplied (id {header.KeyId}).");
            }
            else
            {
                serverKey = Backups.EnsureKey(owner).Key;
                if (!fromServer)
                {
                    output.WriteLine($"Using the key you supplied (id {header.KeyId}); new backups here keep using this server's key (id {BackupFormat.KeyId(serverKey)}).");
                }
            }
            output.WriteLine($"Restoring {Path.GetFileName(path)}, made {header.CreatedAt:yyyy-MM-dd HH:mm} UTC.");
            var result = await Backups.RestoreAsync(Tools(line, config), cs, new NpgsqlConnectionStringBuilder(app).Username!, path, key, serverKey,
                Backups.DefaultFolder(config.Path), DateTimeOffset.UtcNow, ct);
            output.WriteLine($"Saved the database as it was before the restore to {result.SafetyBackup}.");
            if (result.AuditEntriesOnlyInSafetyBackup > 0)
            {
                output.WriteLine($"{result.AuditEntriesOnlyInSafetyBackup} audit log entr{(result.AuditEntriesOnlyInSafetyBackup == 1 ? "y" : "ies")} written after the backup " +
                    "are no longer in the live log. They are kept in that copy; keep it for as long as your audit records must be kept.");
            }
            output.WriteLine(result.AppliedMigrations.Count == 0
                ? "Restored. The schema is current."
                : $"Restored, then applied {result.AppliedMigrations.Count} migration(s) the backup predates: {string.Join(", ", result.AppliedMigrations)}.");
            output.WriteLine("Start the services again: Start-Service DataScopeApi, DataScopeScanner");
            return 0;
        }

        private static int Configure(CommandLine line, TextWriter output)
        {
            line.RejectUnknown("https-port", "certificate-thumbprint", "role", "clear-roles", "pg-bin", "config");
            var config = LoadConfig(line);

            if (line.Optional("pg-bin") is { } pgBin)
            {
                // Checked now, so a typo shows up here rather than as a failed backup at night.
                config.Set(PgTools.Locate(pgBin, null).BinDirectory, "Backup", "PgBinPath");
            }

            if (line.Optional("https-port") is not null)
            {
                config.Set(line.GetInt("https-port", 443, 1, 65535), "Https", "Port");
            }
            if (line.Optional("certificate-thumbprint") is { } thumbprint)
            {
                var clean = ServerCertificate.Normalize(thumbprint);
                if (clean.Length != 40 || !clean.All(Uri.IsHexDigit))
                {
                    throw new UsageException("--certificate-thumbprint must be 40 hexadecimal characters.");
                }
                config.Set(clean, "Https", "CertificateThumbprint");
            }

            var mappings = line.All("role").Select(ParseRole).ToList();
            if (line.Flag("clear-roles"))
            {
                config.Set(new JsonObject(), "Auth", "RoleGroupSids");
            }
            foreach (var group in mappings.GroupBy(m => m.Role))
            {
                var sids = config.GetStrings("Auth", "RoleGroupSids", group.Key)
                    .Concat(group.Select(m => m.Sid))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                config.Set(new JsonArray([.. sids.Select(s => (JsonNode)s)]), "Auth", "RoleGroupSids", group.Key);
            }

            config.Save();
            output.WriteLine($"Saved {config.Path}.");
            return 0;
        }

        private static (string Role, string Sid) ParseRole(string text)
        {
            var parts = text.Split('=', 2, StringSplitOptions.TrimEntries);
            var role = parts.Length == 2 ? Roles.All.FirstOrDefault(r => string.Equals(r, parts[0], StringComparison.OrdinalIgnoreCase)) : null;
            if (role is null)
            {
                throw new UsageException($"--role takes <Role>=<group SID>, where Role is one of {string.Join(", ", Roles.All)}.");
            }
            var sid = parts[1].ToUpperInvariant();
            return Sid().IsMatch(sid) ? (role, sid) : throw new UsageException($"'{parts[1]}' is not a SID. Use the group's SID (S-1-5-21-...), not its name.");
        }

        private static async Task<int> Bundle(CommandLine line, TextWriter output, CancellationToken ct)
        {
            line.RejectUnknown("out", "include-event-log", "config", "owner-config");
            var config = LoadConfig(line);
            var owner = OwnerSettings.Load(line, config);
            Func<string, X509Certificate2>? load = null;
            if (OperatingSystem.IsWindows())
            {
                load = LoadFromMachineStore;
            }
            var path = await SupportBundle.CreateAsync(config, owner, load, line.Optional("out") ?? Directory.GetCurrentDirectory(),
                line.Flag("include-event-log"), DateTimeOffset.UtcNow, ct);
            output.WriteLine($"Saved {path}. Nothing was sent anywhere.");
            output.WriteLine("Open it and read the files before you send it to anyone: README.txt says what is inside.");
            return 0;
        }

        private static async Task<int> Check(CommandLine line, TextWriter output, CancellationToken ct)
        {
            line.RejectUnknown("config", "owner-config");
            var config = LoadConfig(line);
            var owner = OwnerSettings.Load(line, config);
            Func<string, X509Certificate2>? load = null;
            if (OperatingSystem.IsWindows())
            {
                load = LoadFromMachineStore;
            }
            var results = await Checks.RunAsync(config, owner, load, DateTimeOffset.UtcNow, ct);
            foreach (var r in results)
            {
                var tag = r.Level switch { CheckLevel.Ok => "[ OK ]", CheckLevel.Warn => "[WARN]", _ => "[FAIL]" };
                output.WriteLine($"{tag} {r.Name}: {r.Detail}");
            }
            return results.Any(r => r.Level == CheckLevel.Fail) ? 1 : 0;
        }

        [SupportedOSPlatform("windows")]
        private static X509Certificate2 LoadFromMachineStore(string thumbprint) =>
            ServerCertificate.LoadFromMachineStore(thumbprint, DateTimeOffset.UtcNow);

        /// <summary>A password from an environment variable, or typed at a hidden prompt. Never taken from the command line.</summary>
        private static string ReadSecret(string variable, string prompt)
        {
            var fromEnv = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrEmpty(fromEnv))
            {
                return fromEnv;
            }
            if (Console.IsInputRedirected)
            {
                throw new UsageException($"Set {variable} or run interactively to enter the {prompt.ToLowerInvariant()}.");
            }
            Console.Write($"{prompt}: ");
            var chars = new System.Text.StringBuilder();
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                {
                    break;
                }
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (chars.Length > 0)
                    {
                        chars.Length--;
                    }
                    continue;
                }
                chars.Append(key.KeyChar);
            }
            Console.WriteLine();
            return chars.ToString();
        }
    }
}
