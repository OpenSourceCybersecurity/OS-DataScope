using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DataScope.Core.Classification;
using DataScope.Core.Inventory;
using DataScope.Core.ScanJobs;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using Npgsql;

namespace DataScope.Setup.Tests;

public sealed class SupportBundleRedactionTests
{
    [Theory]
    [InlineData(@"Access denied: \\fs01\HR\Payroll\Jo Park 2026.xlsx", "Access denied: <path>")]
    [InlineData(@"Could not read 'D:\Shares\Finance\Avery Okafor.docx' (locked)", "Could not read '<path>' (locked)")]
    [InlineData(@"Sign-in failed for NORTHWIND\jpark at 10:01", "Sign-in failed for <account> at 10:01")]
    [InlineData("Mail to jo.park@northwind.test bounced", "Mail to <email> bounced")]
    [InlineData("Found 512-48-3320 and 4111 1111 1111 1111", "Found <number> and <number>")]
    [InlineData("Scan stopped after 3 attempts (timeout 30s)", "Scan stopped after 3 attempts (timeout 30s)")]
    [InlineData("", "")]
    public void Messages_lose_paths_accounts_addresses_and_long_numbers(string message, string expected) =>
        Assert.Equal(expected, SupportBundle.Redact(message));

    [Fact]
    public void Settings_keep_their_shape_and_lose_every_secret()
    {
        const string json = """
            {
              // Comments are allowed in datascope.json.
              "ConnectionStrings": {
                "DataScope": "Host=db01;Port=5433;Database=datascope;Username=datascope_app;Password=hunter2-app",
                "Legacy": "dpapi:AQAAANCMnd8BFdERjHoAwE"
              },
              "Notifications": { "Smtp": { "Host": "mail.northwind.test", "UserName": "relay", "Password": "hunter2-smtp" } },
              "Directory": { "Password": "dpapi:AAAA" },
              "Backup": { "Key": "dpapi:BBBB", "PgBinPath": "C:\\Program Files\\PostgreSQL\\17\\bin" },
              "Auth": { "RoleGroupSids": { "PlatformAdmin": [ "S-1-5-21-1-2-3-1105" ] } },
              "SomeApiToken": "hunter2-token"
            }
            """;
        var redacted = SupportBundle.RedactSettings(json);
        Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("dpapi:", redacted, StringComparison.Ordinal);
        Assert.Contains("Host=db01;Port=5433;Database=datascope;Username=datascope_app;Password=(removed)", redacted, StringComparison.Ordinal);
        Assert.Contains("mail.northwind.test", redacted, StringComparison.Ordinal);
        Assert.Contains("S-1-5-21-1-2-3-1105", redacted, StringComparison.Ordinal);
        Assert.Contains("PostgreSQL", redacted, StringComparison.Ordinal);
        Assert.Equal("(datascope.json is not valid JSON, so it was left out)", SupportBundle.RedactSettings("{ \"Password\": "));
    }
}

/// <summary>A support bundle from an installation full of synthetic sensitive names holds none of them.</summary>
[Trait("Category", "Database")]
[Collection(nameof(EnvironmentVariableTests))]
public sealed class SupportBundleTests(ScratchPostgres pg) : IClassFixture<ScratchPostgres>, IDisposable
{
    private const string SensitiveServer = "fs-payroll-secret";
    private const string SensitiveName = "Jo Park 512-48-3320 salary.xlsx";
    private const string SmtpPassword = "smtp-hunter2-only-in-tests";
    private readonly string _dir = Directory.CreateTempSubdirectory("ds-bundle-").FullName;
    private string ConfigPath => Path.Combine(_dir, "config", "datascope.json");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(SetupProgram.AdminPasswordVariable, null);
        Environment.SetEnvironmentVariable(SetupProgram.SecretValueVariable, null);
        Directory.Delete(_dir, recursive: true);
    }

    private async Task<(int Code, string Out, string Err)> Run(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await SetupProgram.RunAsync([.. args, "--config", ConfigPath], output, error, CancellationToken.None);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task Holds_counts_and_versions_but_no_names_paths_or_passwords()
    {
        var (db, owner, app) = pg.NewNames();
        var admin = new NpgsqlConnectionStringBuilder(pg.AdminConnection);
        Environment.SetEnvironmentVariable(SetupProgram.AdminPasswordVariable, admin.Password);
        var install = await Run("database", "--host", admin.Host!, "--port", $"{admin.Port}", "--admin-user", admin.Username!,
            "--database", db, "--owner-user", owner, "--app-user", app);
        Assert.True(install.Code == 0, install.Err);
        Environment.SetEnvironmentVariable(SetupProgram.SecretValueVariable, SmtpPassword);
        Assert.Equal(0, (await Run("secret", "--name", "smtp-password")).Code);
        var appConnection = ConfigFile.Load(ConfigPath).GetSecret("ConnectionStrings", "DataScope")!;
        var appPassword = new NpgsqlConnectionStringBuilder(appConnection).Password!;
        var ownerPassword = new NpgsqlConnectionStringBuilder(ConfigFile.Load(OwnerSettings.PathFor(ConfigPath)).GetSecret(OwnerSettings.Key)!).Password!;

        await using (var context = DatabaseProvisioner.CreateContext(appConnection))
        {
            var now = DateTimeOffset.UtcNow;
            var server = new ManagedServer { Id = Guid.NewGuid(), Hostname = SensitiveServer, CreatedBySid = "S-1-5-21-9-9-9-500", CreatedAt = now, UpdatedAt = now };
            var job = new ScanJob
            {
                Id = Guid.NewGuid(),
                ServerId = server.Id,
                PolicyVersion = "test",
                RequestedBySid = "S-1-5-21-9-9-9-500",
                CreatedAt = now,
                UpdatedAt = now,
                State = ScanJobState.Failed,
                LastError = $@"Access denied: \\{SensitiveServer}\Payroll\{SensitiveName}",
            };
            var share = new Share
            {
                Id = Guid.NewGuid(),
                ServerId = server.Id,
                Name = "Payroll",
                NameKey = "PAYROLL",
                UncPath = $@"\\{SensitiveServer}\Payroll",
                Kind = ShareKind.Disk,
                Included = true,
                State = ShareState.Partial,
                CreatedAt = now,
                UpdatedAt = now,
            };
            var root = new Folder { Id = Guid.NewGuid(), ShareId = share.Id, Path = "", PathHash = SHA256.HashData(Encoding.UTF8.GetBytes("")), FirstSeenJobId = job.Id, LastSeenJobId = job.Id };
            context.AddRange(server, job, share, root);
            context.Files.Add(new InventoryFile
            {
                Id = Guid.NewGuid(),
                ShareId = share.Id,
                FolderId = root.Id,
                Name = SensitiveName,
                NameKey = SensitiveName.ToUpperInvariant(),
                Extension = ".xlsx",
                FirstSeenJobId = job.Id,
                LastSeenJobId = job.Id,
                Sensitivity = Sensitivity.Restricted,
                ContentState = ContentState.Scanned,
            });
            context.ScanGaps.Add(new ScanGap { JobId = job.Id, ShareId = share.Id, Path = SensitiveName, Kind = GapKind.AccessDenied, Message = "Synthetic", At = now });
            await context.SaveChangesAsync();
            await new AuditLogWriter(context, TimeProvider.System).AppendAsync("S-1-5-21-9-9-9-500", @"NORTHWIND\jpark", "server.enroll", SensitiveServer);
        }

        var result = await Run("support-bundle", "--out", _dir);
        Assert.True(result.Code == 0, result.Err);
        Assert.Contains("Nothing was sent anywhere", result.Out, StringComparison.Ordinal);
        var zipPath = Assert.Single(Directory.GetFiles(_dir, "datascope-support-*.zip"));
        Assert.Empty(Directory.GetFiles(_dir, "*.partial"));

        var entries = new Dictionary<string, string>();
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in zip.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                entries[entry.FullName] = await reader.ReadToEndAsync();
            }
        }
        Assert.Equal(["README.txt", "check.txt", "database.json", "settings.json", "system.json"], entries.Keys.Order(StringComparer.Ordinal));
        var all = string.Join("\n", entries.Values);
        foreach (var secret in new[] { SensitiveServer, "Jo Park", "512-48", "jpark", SmtpPassword, appPassword, ownerPassword, "dpapi:" })
        {
            Assert.DoesNotContain(secret, all, StringComparison.OrdinalIgnoreCase);
        }

        var stats = entries["database.json"];
        Assert.Contains("\"Restricted / Scanned\": 1", stats, StringComparison.Ordinal);
        Assert.Contains("\"AccessDenied\": 1", stats, StringComparison.Ordinal);
        Assert.Contains("\"Failed\": 1", stats, StringComparison.Ordinal);
        Assert.Contains("Access denied: <path>", stats, StringComparison.Ordinal);
        Assert.Contains("\"AuditChainValid\": true", stats, StringComparison.Ordinal);
        Assert.Contains("\"AuditEntries\": 1", stats, StringComparison.Ordinal);
        Assert.Contains("[Ok] Database", entries["check.txt"], StringComparison.Ordinal);
        Assert.Contains("Password=(removed)", entries["settings.json"], StringComparison.Ordinal);
        Assert.Contains("Data Scope", entries["system.json"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Works_without_a_database_and_says_why()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        await File.WriteAllTextAsync(ConfigPath, """{ "ConnectionStrings": { "DataScope": "Host=127.0.0.1;Port=1;Database=x;Username=y;Password=hunter2-down;Timeout=2" } }""");
        var target = Path.Combine(_dir, "bundle.zip");
        var result = await Run("support-bundle", "--out", target);
        Assert.True(result.Code == 0, result.Err);
        using var zip = ZipFile.OpenRead(target);
        using var reader = new StreamReader(zip.GetEntry("database.json")!.Open());
        var stats = await reader.ReadToEndAsync();
        Assert.Contains("\"Error\"", stats, StringComparison.Ordinal);
        using var settings = new StreamReader(zip.GetEntry("settings.json")!.Open());
        Assert.DoesNotContain("hunter2", await settings.ReadToEndAsync(), StringComparison.Ordinal);
    }
}
