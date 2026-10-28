using System.IO.Compression;
using System.Text;
using DataScope.Core.Classification;
using DataScope.Core.ScanJobs;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using DataScope.Scanner;
using DataScope.Scanner.Content;
using DataScope.Scanner.Content.Isolation;
using DataScope.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataScope.Api.Tests;

/// <summary>The classify phase end to end: synthetic files on disk, the real readers and detectors, and the database.</summary>
[Trait("Category", "Database")]
public sealed class ClassificationScanTests : IClassFixture<TestDatabase>, IDisposable
{
    private const string Ssn = "512-48-3307";

    private readonly TestDatabase _database;
    private readonly string _lab = Directory.CreateTempSubdirectory("ds-classify-").FullName;

    public ClassificationScanTests(TestDatabase database) => _database = database;

    public void Dispose() => Directory.Delete(_lab, recursive: true);

    private string Share(string host, string share)
    {
        var path = Path.Join(_lab, host, share);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Write(string root, string relative, byte[] content)
    {
        var path = Path.Join(root, relative.Replace('\\', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static string Write(string root, string relative, string content) => Write(root, relative, Encoding.UTF8.GetBytes(content));

    private static byte[] Docx(string text)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var s = new StreamWriter(zip.CreateEntry("word/document.xml").Open());
            s.Write($"""<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>{text}</w:t></w:r></w:p></w:body></w:document>""");
        }
        return ms.ToArray();
    }

    /// <summary>HR with an SSN in a CSV, Finance with a card number in a DOCX, plus a clean file and an executable.</summary>
    private void Lab(string host)
    {
        var hr = Share(host, "HR");
        Write(hr, @"Payroll\staff.csv", $"Name,SSN\nAvery Okafor,{Ssn}\n");
        Write(hr, "welcome.txt", "Welcome to the team. The canteen opens at 8.");
        Write(hr, "tool.exe", [0x4D, 0x5A, 0x90, 0x00]);
        var finance = Share(host, "Finance");
        Write(finance, "card-form.docx", Docx("Visa 4111 1111 1111 1111 exp 04/28"));
    }

    private async Task<(Guid ServerId, Guid JobId)> Queue(string host, Guid? serverId = null, ScanKind kind = ScanKind.Quick)
    {
        await using var db = _database.CreateContext();
        await db.ScanJobs.Where(j => j.State == ScanJobState.Queued).ExecuteUpdateAsync(s => s.SetProperty(j => j.State, ScanJobState.Cancelled));
        var now = DateTimeOffset.UtcNow;
        if (serverId is null)
        {
            var server = new ManagedServer { Id = Guid.NewGuid(), Hostname = host, CreatedAt = now, UpdatedAt = now, CreatedBySid = "S-1" };
            db.ManagedServers.Add(server);
            serverId = server.Id;
        }
        var job = new ScanJob { Id = Guid.NewGuid(), ServerId = serverId.Value, Kind = kind, PolicyVersion = ClassificationPolicy.CurrentVersion, RequestedBySid = "S-1", CreatedAt = now, UpdatedAt = now };
        db.ScanJobs.Add(job);
        await db.SaveChangesAsync();
        return (serverId.Value, job.Id);
    }

    private async Task<ScanJob> Run(Guid jobId, IContentReader reader, int batch = 100, IContentReader? full = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DataScope"] = _database.ConnectionString,
            ["Scanner:LeaseSeconds"] = "60",
        }).Build();
        var services = new ServiceCollection();
        services.AddDataScopePersistence(config);
        await using var sp = services.BuildServiceProvider();
        var limits = new ScanLimits(64, 10_000, 1_000_000, TimeSpan.Zero, batch);
        var executor = new InventoryScanExecutor(sp.GetRequiredService<IServiceScopeFactory>(), new LabShareEnumerator(_lab),
            new FileSystemDirectorySource(), limits, NullLogger<InventoryScanExecutor>.Instance, reader,
            fullScanReader: full is null ? null : new FullScanContentReader(full));
        var worker = new ScanWorker(sp.GetRequiredService<IServiceScopeFactory>(), executor, config, NullLogger<ScanWorker>.Instance);
        Assert.True(await worker.RunOneAsync(CancellationToken.None));
        await using var db = _database.CreateContext();
        return await db.ScanJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
    }

    private async Task<Dictionary<string, InventoryFile>> Files(Guid serverId)
    {
        await using var db = _database.CreateContext();
        return await (from f in db.Files
                      join s in db.Shares on f.ShareId equals s.Id
                      where s.ServerId == serverId
                      select f).AsNoTracking().ToDictionaryAsync(f => f.Name);
    }

    private async Task<List<ClassificationFinding>> Findings(Guid fileId)
    {
        await using var db = _database.CreateContext();
        return await db.Findings.AsNoTracking().Where(f => f.FileId == fileId).ToListAsync();
    }

    [Fact]
    public async Task Classifies_files_after_the_walk_and_stores_only_redacted_evidence()
    {
        Lab("cls-basic");
        var (serverId, jobId) = await Queue("cls-basic");

        var job = await Run(jobId, new CountingReader());

        Assert.Equal(ScanJobState.Completed, job.State);
        Assert.Equal("classify", DataScope.Api.Endpoints.ScanJobEndpoints.Phase(job.Checkpoint));
        var files = await Files(serverId);

        var staff = files["staff.csv"];
        Assert.Equal(ContentState.Scanned, staff.ContentState);
        Assert.Equal(Sensitivity.Restricted, staff.Sensitivity);
        Assert.Equal(ClassificationPolicy.CurrentVersion, staff.ClassifiedPolicy);
        Assert.Equal(jobId, staff.ClassifiedJobId);
        var finding = Assert.Single(await Findings(staff.Id));
        Assert.Equal("DS-SSN-01", finding.RuleId);
        Assert.Equal(Confidence.High, finding.Confidence);
        Assert.Contains("***-**-3307", finding.Evidence[0], StringComparison.Ordinal);

        Assert.Equal(Sensitivity.Restricted, files["card-form.docx"].Sensitivity);
        Assert.Equal(Sensitivity.Internal, files["welcome.txt"].Sensitivity);
        Assert.Equal(ContentState.Unsupported, files["tool.exe"].ContentState);
        Assert.Equal(Sensitivity.Unknown, files["tool.exe"].Sensitivity);

        // No raw value reaches the database, in findings or anywhere else on the file rows.
        await using var db = _database.CreateContext();
        var evidence = await db.Findings.Select(f => f.Evidence).ToListAsync();
        Assert.DoesNotContain(evidence.SelectMany(e => e), e => e.Contains(Ssn, StringComparison.Ordinal) || e.Contains("4111 1111", StringComparison.Ordinal));
        Assert.DoesNotContain(files.Values, f => (f.ContentNote ?? "").Contains("3307", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_rescan_reads_only_new_and_changed_files()
    {
        Lab("cls-rescan");
        var (serverId, first) = await Queue("cls-rescan");
        await Run(first, new CountingReader());

        var hr = Path.Join(_lab, "cls-rescan", "HR");
        var welcome = Write(hr, "welcome.txt", $"Updated. SSN {Ssn}");
        File.SetLastWriteTimeUtc(welcome, DateTime.UtcNow.AddMinutes(5));
        Write(hr, "new.txt", "nothing to see");
        var counting = new CountingReader();
        var (_, second) = await Queue("cls-rescan", serverId);
        await Run(second, counting);

        Assert.Equal(["new.txt", "welcome.txt"], counting.Read.Select(Path.GetFileName).Order(StringComparer.Ordinal));
        var files = await Files(serverId);
        Assert.Equal(Sensitivity.Restricted, files["welcome.txt"].Sensitivity);
        Assert.Equal(first, files["staff.csv"].ClassifiedJobId);
    }

    [Fact]
    public async Task A_full_scan_reads_every_file_again_with_the_full_scan_reader()
    {
        Lab("cls-full");
        var (serverId, first) = await Queue("cls-full");
        await Run(first, new CountingReader());

        var quick = new CountingReader();
        var full = new CountingReader();
        var (_, second) = await Queue("cls-full", serverId, ScanKind.Full);
        var job = await Run(second, quick, full: full);

        Assert.Equal((ScanJobState.Completed, ScanKind.Full), (job.State, job.Kind));
        Assert.Empty(quick.Read);
        Assert.Equal(["card-form.docx", "staff.csv", "welcome.txt"], full.Read.Select(Path.GetFileName).Order(StringComparer.Ordinal));
        var files = await Files(serverId);
        Assert.Equal(second, files["staff.csv"].ClassifiedJobId);
        Assert.Equal(Sensitivity.Restricted, files["staff.csv"].Sensitivity);
        Assert.Single(await Findings(files["staff.csv"].Id));

        // A quick scan after it reads nothing: nothing changed.
        var after = new CountingReader();
        var (_, third) = await Queue("cls-full", serverId);
        await Run(third, after, full: new CountingReader());
        Assert.Empty(after.Read);
    }

    [Fact]
    public async Task Without_a_full_scan_reader_a_full_scan_still_reads_every_file()
    {
        Lab("cls-full-plain");
        var (serverId, first) = await Queue("cls-full-plain");
        await Run(first, new CountingReader());

        var reader = new CountingReader();
        var (_, second) = await Queue("cls-full-plain", serverId, ScanKind.Full);
        await Run(second, reader);
        Assert.Equal(3, reader.Read.Count);
    }

    [Fact]
    public async Task A_rescan_in_one_file_windows_steps_past_files_that_need_no_reading()
    {
        // Each batch looks at one file of the whole inventory (other tests' servers included), so most come back
        // with nothing to read; the scan must still walk every window and read exactly the changed file.
        Lab("cls-window");
        var (serverId, first) = await Queue("cls-window");
        await Run(first, new CountingReader());
        var changed = Write(Path.Join(_lab, "cls-window", "Finance"), "later.txt", $"SSN {Ssn}");
        var counting = new CountingReader();
        var (_, second) = await Queue("cls-window", serverId);

        var job = await Run(second, counting, batch: 1);

        Assert.Equal(ScanJobState.Completed, job.State);
        Assert.Equal([Path.GetFileName(changed)], counting.Read.Select(Path.GetFileName));
        Assert.Equal(Sensitivity.Restricted, (await Files(serverId))["later.txt"].Sensitivity);
    }

    [Fact]
    public async Task Access_denied_is_recorded_as_unknown_and_retried_on_the_next_scan()
    {
        Lab("cls-denied");
        var (serverId, first) = await Queue("cls-denied");
        await Run(first, new CountingReader(deny: "staff.csv"));

        var staff = (await Files(serverId))["staff.csv"];
        Assert.Equal(ContentState.AccessDenied, staff.ContentState);
        Assert.Equal(Sensitivity.Unknown, staff.Sensitivity);
        Assert.Empty(await Findings(staff.Id));

        var counting = new CountingReader();
        var (_, second) = await Queue("cls-denied", serverId);
        await Run(second, counting);

        Assert.Equal(["staff.csv"], counting.Read.Select(Path.GetFileName));
        Assert.Equal(Sensitivity.Restricted, (await Files(serverId))["staff.csv"].Sensitivity);
    }

    [Fact]
    public async Task A_failed_reread_keeps_the_previous_result()
    {
        Lab("cls-keep");
        var (serverId, first) = await Queue("cls-keep");
        await Run(first, new CountingReader());
        var staffPath = Path.Join(_lab, "cls-keep", "HR", "Payroll", "staff.csv");
        File.SetLastWriteTimeUtc(staffPath, DateTime.UtcNow.AddMinutes(5));

        var (_, second) = await Queue("cls-keep", serverId);
        await Run(second, new CountingReader(deny: "staff.csv"));

        var staff = (await Files(serverId))["staff.csv"];
        Assert.Equal(ContentState.AccessDenied, staff.ContentState);
        Assert.Equal(Sensitivity.Restricted, staff.Sensitivity);
        Assert.Equal(first, staff.ClassifiedJobId);
        Assert.Single(await Findings(staff.Id));
    }

    [Fact]
    public async Task A_file_not_read_for_now_keeps_its_previous_result_and_is_read_on_the_next_scan()
    {
        // As when text recognition is turned off or its libraries are missing (D129): no verdict on the contents.
        Lab("cls-notread");
        var (serverId, first) = await Queue("cls-notread");
        await Run(first, new CountingReader());
        var staffPath = Path.Join(_lab, "cls-notread", "HR", "Payroll", "staff.csv");
        File.SetLastWriteTimeUtc(staffPath, DateTime.UtcNow.AddMinutes(5));

        var (_, second) = await Queue("cls-notread", serverId);
        await Run(second, new CountingReader(notRead: "staff.csv"));
        var staff = (await Files(serverId))["staff.csv"];
        Assert.Equal(ContentState.NotRead, staff.ContentState);
        Assert.Equal("Text in images is not read on this server.", staff.ContentNote);
        Assert.Equal(Sensitivity.Restricted, staff.Sensitivity);
        Assert.Single(await Findings(staff.Id));

        var counting = new CountingReader();
        var (_, third) = await Queue("cls-notread", serverId);
        await Run(third, counting);
        Assert.Equal(["staff.csv"], counting.Read.Select(Path.GetFileName));
        Assert.Equal(ContentState.Scanned, (await Files(serverId))["staff.csv"].ContentState);
    }

    [Fact]
    public async Task A_policy_change_reclassifies_everything()
    {
        Lab("cls-policy");
        var (serverId, first) = await Queue("cls-policy");
        await Run(first, new CountingReader());
        await using (var db = _database.CreateContext())
        {
            await db.Files.ExecuteUpdateAsync(s => s.SetProperty(f => f.ClassifiedPolicy, "2000.01-r1"));
        }

        var counting = new CountingReader();
        var (_, second) = await Queue("cls-policy", serverId);
        await Run(second, counting);

        Assert.Equal(3, counting.Read.Count); // tool.exe is skipped by type without being opened
    }

    [Fact]
    public async Task Pause_during_classification_resumes_without_rereading_finished_files()
    {
        Lab("cls-pause");
        var (serverId, jobId) = await Queue("cls-pause");
        var first = new CountingReader(onRead: n =>
        {
            if (n == 1)
            {
                using var db = _database.CreateContext();
                db.ScanJobs.Where(j => j.Id == jobId).ExecuteUpdate(s => s.SetProperty(j => j.RequestedState, ScanJobState.Paused));
            }
        });

        var paused = await Run(jobId, first, batch: 1);
        Assert.Equal(ScanJobState.Paused, paused.State);
        Assert.Contains("classify", paused.Checkpoint, StringComparison.Ordinal);
        await using (var db = _database.CreateContext())
        {
            await db.ScanJobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s.SetProperty(j => j.State, ScanJobState.Queued));
        }

        var second = new CountingReader();
        var resumed = await Run(jobId, second, batch: 1);

        Assert.Equal(ScanJobState.Completed, resumed.State);
        Assert.Equal(3, first.Read.Count + second.Read.Count);
        Assert.Empty(first.Read.Intersect(second.Read));
        Assert.DoesNotContain((await Files(serverId)).Values, f => f.ContentState == ContentState.NotRead);
    }

    [Fact]
    public async Task A_reader_crash_marks_the_file_unreadable_and_the_scan_carries_on()
    {
        Lab("cls-crash");
        var (serverId, jobId) = await Queue("cls-crash");

        var job = await Run(jobId, new CountingReader(crash: "staff.csv"));

        Assert.Equal(ScanJobState.Completed, job.State);
        var files = await Files(serverId);
        Assert.Equal(ContentState.Unreadable, files["staff.csv"].ContentState);
        Assert.DoesNotContain("boom", files["staff.csv"].ContentNote, StringComparison.Ordinal);
        Assert.Equal(Sensitivity.Restricted, files["card-form.docx"].Sensitivity);
    }

    [Fact]
    public async Task Classification_never_changes_files()
    {
        Lab("cls-readonly");
        var root = Path.Join(_lab, "cls-readonly");
        var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(p => p, p => (File.ReadAllBytes(p), File.GetLastWriteTimeUtc(p)));
        var (_, jobId) = await Queue("cls-readonly");

        await Run(jobId, new CountingReader());

        foreach (var (path, (bytes, modified)) in before)
        {
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
        }
        Assert.Equal(before.Count, Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task A_file_that_crashes_the_reader_process_costs_only_that_file()
    {
        Lab("cls-isolated");
        Write(Share("cls-isolated", "HR"), "poison.docx", Docx("anything"));
        var (serverId, jobId) = await Queue("cls-isolated");
        using var reader = new IsolatedContentReader(new ContentLimits(), () =>
        {
            var start = ContentHostCommand.For(typeof(InventoryScanExecutor).Assembly.Location);
            start.Environment[ContentHost.CrashOnVariable] = "poison.docx";
            return start;
        });

        var job = await Run(jobId, reader);

        Assert.Equal(ScanJobState.Completed, job.State);
        var files = await Files(serverId);
        Assert.Equal(ContentState.Unreadable, files["poison.docx"].ContentState);
        Assert.Equal(Sensitivity.Unknown, files["poison.docx"].Sensitivity);
        Assert.Equal(Sensitivity.Restricted, files["staff.csv"].Sensitivity);
        Assert.Equal(Sensitivity.Restricted, files["card-form.docx"].Sensitivity);
        Assert.Equal(ContentState.Scanned, files["welcome.txt"].ContentState);
    }

    /// <summary>The real reader, recording which files it opened, with optional denial or crash for one file name.</summary>
    [Fact]
    public async Task Custom_rules_are_applied_in_a_scan_and_a_change_to_them_reads_every_file_again()
    {
        Lab("cls-custom");
        Write(Path.Join(_lab, "cls-custom", "HR"), "network.txt", "Router 192.0.2.10 for the Project Falcon data room.");
        await using (var db = _database.CreateContext())
        {
            var now = DateTimeOffset.UtcNow;
            CustomDetectionRule Rule(string name, CustomRuleKind kind, string? pattern, string[] keywords, CustomRuleEffect effect) => new()
            {
                Name = name,
                Kind = kind,
                Pattern = pattern,
                Keywords = keywords,
                NearWords = [],
                MinMatches = 1,
                Effect = effect,
                Enabled = true,
                Reason = "test",
                CreatedAt = now,
                CreatedByName = "t",
                UpdatedAt = now,
                UpdatedBySid = "S-1",
                UpdatedByName = "t",
            };
            db.CustomRules.AddRange(
                Rule("Falcon", CustomRuleKind.Keywords, null, ["project falcon"], CustomRuleEffect.Restricted),
                Rule("IP addresses", CustomRuleKind.Pattern, CustomRules.Templates.Single(t => t.Id == "ipv4").Pattern, [], CustomRuleEffect.ListOnly));
            await db.SaveChangesAsync();
        }
        try
        {
            var (serverId, first) = await Queue("cls-custom");
            await Run(first, new CountingReader());
            var files = await Files(serverId);
            var network = files["network.txt"];
            Assert.Equal(Sensitivity.Restricted, network.Sensitivity);
            Assert.StartsWith(ClassificationPolicy.CurrentVersion + "+c", network.ClassifiedPolicy, StringComparison.Ordinal);
            var findings = await Findings(network.Id);
            Assert.Equal(2, findings.Count);
            Assert.All(findings, f => Assert.Equal(FindingCategory.CustomRule, f.Category));
            Assert.All(findings, f => Assert.StartsWith("DS-CUSTOM-", f.RuleId, StringComparison.Ordinal));
            // Built-in detection is unchanged, and the address is masked in evidence.
            Assert.Equal(Sensitivity.Restricted, files["staff.csv"].Sensitivity);
            Assert.DoesNotContain(findings.SelectMany(f => f.Evidence), e => e.Contains("192.0.2", StringComparison.Ordinal));

            // Nothing changed: a quick scan reads nothing.
            var unchanged = new CountingReader();
            var (_, second) = await Queue("cls-custom", serverId);
            await Run(second, unchanged);
            Assert.Empty(unchanged.Read);

            // A rule changes what it does: every file is read again with it.
            await using (var db = _database.CreateContext())
            {
                await db.CustomRules.Where(r => r.Name == "Falcon").ExecuteUpdateAsync(s => s.SetProperty(r => r.Effect, CustomRuleEffect.ListOnly));
            }
            var again = new CountingReader();
            var (_, third) = await Queue("cls-custom", serverId);
            await Run(third, again);
            Assert.Equal(["card-form.docx", "network.txt", "staff.csv", "welcome.txt"], again.Read.Select(Path.GetFileName).Order(StringComparer.Ordinal));
            Assert.Equal(Sensitivity.Internal, (await Files(serverId))["network.txt"].Sensitivity);
        }
        finally
        {
            await using var db = _database.CreateContext();
            await db.CustomRules.ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Text_longer_than_one_scan_is_scanned_in_pieces_and_every_match_counted()
    {
        // As an Outlook mailbox gives (D134): messages that add up to more than one scan of text.
        var share = Share("cls-long", "Mail");
        Write(share, "archive.pst", [0x21, 0x42, 0x44, 0x4E]);
        var filler = string.Concat(Enumerable.Repeat("Lunch on Friday is moved to noon.\n", 40_000));
        var text = $"Employee SSN 512-48-3301\n\n{filler}\n\nEmployee SSN 512-48-3302\n\n{filler}\n\nEmployee SSN 512-48-3303";
        Assert.True(text.Length > 2 * TextScanner.MaxScanChars);
        var (serverId, jobId) = await Queue("cls-long");
        await Run(jobId, new FixedReader(new ContentExtraction(ContentState.Scanned, text, "Read all 3 messages in the mailbox.")));
        var mailbox = (await Files(serverId))["archive.pst"];
        Assert.Equal(ContentState.Scanned, mailbox.ContentState);
        Assert.Equal("Read all 3 messages in the mailbox.", mailbox.ContentNote);
        var finding = Assert.Single(await Findings(mailbox.Id));
        Assert.Equal(("DS-SSN-01", 3), (finding.RuleId, finding.MatchCount));
    }

    /// <summary>Gives every file the same extraction, as if the reader had read it.</summary>
    private sealed class FixedReader(ContentExtraction extraction) : IContentReader
    {
        public ContentExtraction? Precheck(string fileName, long size, FileAttributes attributes) => null;

        public ContentExtraction Read(string fullPath, CancellationToken ct) => extraction;
    }

    private sealed class CountingReader(string? deny = null, string? crash = null, Action<int>? onRead = null, string? notRead = null) : IContentReader
    {
        private readonly FileContentReader _inner = new(new ContentLimits());

        public List<string> Read { get; } = [];

        public ContentExtraction? Precheck(string fileName, long size, FileAttributes attributes) => _inner.Precheck(fileName, size, attributes);

        ContentExtraction IContentReader.Read(string fullPath, CancellationToken ct)
        {
            Read.Add(fullPath);
            onRead?.Invoke(Read.Count);
            var name = Path.GetFileName(fullPath);
            if (name == crash)
            {
                throw new InvalidOperationException("boom " + Ssn);
            }
            return name == deny ? ContentExtraction.Of(ContentState.AccessDenied, "Access denied to the scan identity.")
                : name == notRead ? ContentExtraction.Of(ContentState.NotRead, "Text in images is not read on this server.")
                : _inner.Read(fullPath, ct);
        }
    }
}
