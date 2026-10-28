using System.Net;
using System.Net.Http.Json;
using DataScope.Api.Endpoints;
using DataScope.Core.Access;
using DataScope.Core.Classification;
using DataScope.Core.Inventory;
using DataScope.Core.Security;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Tests;

/// <summary>The search box at the top of every page (D128).</summary>
[Trait("Category", "Database")]
public sealed class SearchApiTests : IClassFixture<TestDatabase>, IDisposable
{
    private readonly TestDatabase _db;
    private readonly ApiFactory _factory;

    public SearchApiTests(TestDatabase db)
    {
        _db = db;
        _factory = new ApiFactory(db);
    }

    public void Dispose() => _factory.Dispose();

    private sealed record Hit(string Id, string Title, string? Detail, string? Label, bool Exact, bool Disabled);
    private sealed record Group(string Kind, List<Hit> Items, bool More, bool TimedOut);
    private sealed record Result(string Query, List<Group> Groups, bool FilesSearched, DateTimeOffset? DirectoryAt)
    {
        public List<Hit> Of(string kind) => Groups.SingleOrDefault(g => g.Kind == kind)?.Items ?? [];
    }

    private sealed record Seeded(Guid Server, Guid Share, Guid Payroll, Guid March, Guid Archive);

    /// <summary>
    /// One server <paramref name="host"/> with share HR: folders Payroll and Payroll\Archive 2019, files march.xlsx
    /// (Restricted) and april.xlsx in Payroll, a removed file and a removed folder; plus a retired server's share.
    /// </summary>
    private async Task<Seeded> Seed(string host)
    {
        await using var db = _db.CreateContext();
        var now = DateTimeOffset.UtcNow;
        ManagedServer Server(string name, EnrollmentState state) => new() { Id = Guid.NewGuid(), Hostname = name, State = state, CreatedAt = now, UpdatedAt = now, CreatedBySid = "S-1" };
        var server = Server(host, EnrollmentState.Enrolled);
        var retired = Server(host + "-old", EnrollmentState.Retired);
        var job = new ScanJob { Id = Guid.NewGuid(), ServerId = server.Id, PolicyVersion = ClassificationPolicy.CurrentVersion, RequestedBySid = "S-1", CreatedAt = now, UpdatedAt = now, State = Core.ScanJobs.ScanJobState.Completed };
        Share ShareOn(ManagedServer s, string name) => new() { Id = Guid.NewGuid(), ServerId = s.Id, Name = name, NameKey = name.ToUpperInvariant(), UncPath = $@"\\{s.Hostname}\{name}", Kind = ShareKind.Disk, Included = true, State = ShareState.Scanned, CreatedAt = now, UpdatedAt = now };
        var hr = ShareOn(server, "HR");
        var oldHr = ShareOn(retired, "HR");
        Folder F(Share share, string path, Guid? parent, bool removed = false) => new()
        {
            Id = Guid.NewGuid(),
            ShareId = share.Id,
            ParentId = parent,
            Path = path,
            PathHash = InventoryStore.PathHash(path),
            Depth = path.Length == 0 ? 0 : path.Count(c => c == '\\') + 1,
            FirstSeenJobId = job.Id,
            LastSeenJobId = job.Id,
            LastWalkedJobId = job.Id,
            RemovedAt = removed ? now : null,
        };
        var root = F(hr, "", null);
        var oldRoot = F(oldHr, "", null);
        var payroll = F(hr, "Payroll", root.Id);
        var oldPayroll = F(oldHr, "Payroll", oldRoot.Id);
        var gone = F(hr, "Payroll Gone", root.Id, removed: true);
        var archive = F(hr, @"Payroll\Archive 2019", payroll.Id);
        InventoryFile File(Folder folder, string name, Sensitivity label = Sensitivity.Unknown, bool removed = false) => new()
        {
            Id = Guid.NewGuid(),
            ShareId = folder.ShareId,
            FolderId = folder.Id,
            Name = name,
            NameKey = name.ToUpperInvariant(),
            Extension = PathText.Extension(name),
            Size = 10,
            FirstSeenJobId = job.Id,
            LastSeenJobId = job.Id,
            Sensitivity = label,
            RemovedAt = removed ? now : null,
        };
        var march = File(payroll, "march.xlsx", Sensitivity.Restricted);
        db.AddRange(server, retired, job, hr, oldHr);
        await db.SaveChangesAsync();
        db.AddRange(root, oldRoot);
        await db.SaveChangesAsync();
        db.AddRange(payroll, oldPayroll, gone);
        await db.SaveChangesAsync();
        db.Add(archive);
        db.AddRange(march, File(payroll, "april.xlsx"), File(payroll, "deleted-march.xlsx", removed: true), File(oldPayroll, "march-old-server.xlsx"),
            File(archive, "2019 summary.docx"));
        await db.SaveChangesAsync();
        return new Seeded(server.Id, hr.Id, payroll.Id, march.Id, archive.Id);
    }

    private async Task SeedDirectory()
    {
        await using var db = _db.CreateContext();
        var now = DateTimeOffset.UtcNow;
        var snapshot = new DirectorySnapshot { Id = Guid.NewGuid(), State = DirectorySyncState.Completed, RequestedAt = now, StartedAt = now, CompletedAt = now };
        DirectoryPrincipal P(string sid, PrincipalKind kind, string name, string? display, bool enabled = true, string? title = null, string? department = null) => new()
        {
            SnapshotId = snapshot.Id,
            Sid = sid,
            Kind = kind,
            Name = name,
            DisplayName = display,
            DistinguishedName = $"CN={name},DC=nw,DC=local",
            Enabled = enabled,
            IsSecurityGroup = kind == PrincipalKind.Group,
            Title = title,
            Department = department,
            UserPrincipalName = kind == PrincipalKind.User ? $"{name}@nw.local" : null,
        };
        db.Add(snapshot);
        await db.SaveChangesAsync();
        db.AddRange(
            P("S-1-5-21-10-20-30-1101", PrincipalKind.User, "jmorales", "Jordan Morales", title: "Payroll lead", department: "Finance"),
            P("S-1-5-21-10-20-30-1102", PrincipalKind.User, "mjordan", "Morgan Jordan", enabled: false),
            P("S-1-5-21-10-20-30-1103", PrincipalKind.User, "akim", "Alex Kim"),
            P("S-1-5-21-10-20-30-2101", PrincipalKind.Group, "Payroll-Readers", null),
            P("S-1-5-21-10-20-30-3101", PrincipalKind.Computer, "PAYROLL-PC$", null));
        await db.SaveChangesAsync();
    }

    private async Task<Result> Search(string q, string? roles = Roles.Viewer)
    {
        var response = await _factory.ClientAs(roles).GetAsync($"/api/v1/search?q={Uri.EscapeDataString(q)}");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<Result>())!;
    }

    [Fact]
    public async Task Finds_people_and_groups_by_name_with_names_starting_with_the_search_first()
    {
        await SeedDirectory();
        var r = await Search("jordan");

        // "jordan" starts Jordan Morales's name; Morgan Jordan's only contains it. Disabled accounts are shown, and say so.
        Assert.Equal(["Jordan Morales (jmorales)", "Morgan Jordan (mjordan)"], r.Of("User").Select(h => h.Title));
        Assert.Equal("Payroll lead · Finance", r.Of("User")[0].Detail);
        Assert.True(r.Of("User")[1].Disabled);
        Assert.NotNull(r.DirectoryAt);

        var groups = await Search("payroll-r");
        Assert.Equal("S-1-5-21-10-20-30-2101", Assert.Single(groups.Of("Group")).Id);
        Assert.Equal("Security group", groups.Of("Group")[0].Detail);
        // Computers are not people or groups.
        Assert.Empty((await Search("payroll-pc")).Groups.Where(g => g.Kind is "User" or "Group").SelectMany(g => g.Items));

        var bySid = await Search("S-1-5-21-10-20-30-1103");
        Assert.Equal("Alex Kim (akim)", Assert.Single(bySid.Of("User")).Title);
        var byUpn = await Search("akim@nw");
        Assert.Single(byUpn.Of("User"));
    }

    [Fact]
    public async Task Finds_shares_servers_folders_and_files_but_not_removed_or_retired_ones()
    {
        var s = await Seed("srch-fs01");

        var shares = await Search("srch-fs01\\h");
        Assert.Equal([@"\\srch-fs01\HR"], shares.Of("Share").Select(h => h.Detail));
        Assert.Equal(["srch-fs01"], (await Search("srch-fs01")).Of("Server").Select(h => h.Title)); // srch-fs01-old is retired.

        var march = await Search("march");
        var file = Assert.Single(march.Of("File"), f => f.Detail!.StartsWith(@"\\srch-fs01\", StringComparison.Ordinal));
        Assert.Equal((s.March.ToString(), "march.xlsx", @"\\srch-fs01\HR\Payroll", "Restricted"), (file.Id, file.Title, file.Detail, file.Label));
        // The removed copy and the file on the retired server are not found.
        Assert.DoesNotContain(march.Of("File"), f => f.Title == "deleted-march.xlsx" || f.Title == "march-old-server.xlsx");
        Assert.True(march.FilesSearched);

        // Folders match on their own name, not their parents': "payroll" finds Payroll, not Payroll\Archive 2019.
        var folders = (await Search("payroll")).Of("Folder").Where(f => f.Detail!.StartsWith(@"\\srch-fs01\", StringComparison.Ordinal)).ToList();
        Assert.Equal([(s.Payroll.ToString(), "Payroll", @"\\srch-fs01\HR\Payroll")], folders.Select(f => (f.Id, f.Title, f.Detail)));
        var archive = Assert.Single((await Search("archive 20")).Of("Folder"), f => f.Detail!.StartsWith(@"\\srch-fs01\", StringComparison.Ordinal));
        Assert.Equal((s.Archive.ToString(), "Archive 2019"), (archive.Id, archive.Title));
        // A search with a backslash matches anywhere in the folder's path.
        Assert.Contains((await Search(@"payroll\arch")).Of("Folder"), f => f.Id == s.Archive.ToString());
    }

    [Fact]
    public async Task Short_searches_skip_files_and_folders()
    {
        await Seed("srch-fs02");
        var r = await Search("hr");
        Assert.False(r.FilesSearched);
        Assert.DoesNotContain(r.Groups, g => g.Kind is "File" or "Folder");
        Assert.Contains(r.Of("Share"), h => h.Detail == @"\\srch-fs02\HR");
    }

    [Fact]
    public async Task A_pasted_path_finds_exactly_what_it_names_first()
    {
        var s = await Seed("srch-fs03");

        var file = await Search(@"\\srch-fs03\HR\Payroll\march.xlsx");
        var hit = file.Of("File")[0];
        Assert.Equal((s.March.ToString(), true, "Restricted"), (hit.Id, hit.Exact, hit.Label));

        // Any case, forward slashes, a trailing backslash.
        var folder = await Search("//SRCH-FS03/hr/payroll/");
        Assert.Equal((s.Payroll.ToString(), true), (folder.Of("Folder")[0].Id, folder.Of("Folder")[0].Exact));
        Assert.Contains(folder.Of("Folder"), f => f.Title == "Archive 2019" && !f.Exact);
        Assert.Equal(["april.xlsx", "march.xlsx"], folder.Of("File").Select(f => f.Title));

        var share = await Search(@"\\srch-fs03\HR");
        Assert.Equal((s.Share.ToString(), true), (share.Of("Share")[0].Id, share.Of("Share")[0].Exact));

        // Typed so far: the file name's start, and a server name's start.
        Assert.Equal(["march.xlsx"], (await Search(@"\\srch-fs03\HR\Payroll\mar")).Of("File").Select(f => f.Title));
        Assert.Equal([@"\\srch-fs03\HR"], (await Search(@"\\srch-fs03")).Of("Share").Select(h => h.Detail));

        // Unknown server or share: nothing, and no error.
        Assert.Empty((await Search(@"\\nowhere\HR\Payroll")).Groups.SelectMany(g => g.Items));
    }

    [Fact]
    public async Task Search_characters_are_literal()
    {
        await Seed("srch-fs04");
        await using (var db = _db.CreateContext())
        {
            var payroll = await db.Folders.SingleAsync(f => f.Path == "Payroll" && db.Shares.Any(x => x.Id == f.ShareId && x.UncPath == @"\\srch-fs04\HR"));
            db.Files.Add(new InventoryFile
            {
                Id = Guid.NewGuid(),
                ShareId = payroll.ShareId,
                FolderId = payroll.Id,
                Name = "100%_final (v2).xlsx",
                NameKey = "100%_FINAL (V2).XLSX",
                Extension = "xlsx",
                FirstSeenJobId = payroll.FirstSeenJobId,
                LastSeenJobId = payroll.LastSeenJobId,
            });
            await db.SaveChangesAsync();
        }
        Assert.Equal(["100%_final (v2).xlsx"], (await Search("0%_f")).Of("File").Select(f => f.Title));
        Assert.Empty((await Search("1_0%")).Of("File"));
        Assert.Equal(["100%_final (v2).xlsx"], (await Search("(v2)")).Of("File").Select(f => f.Title));
        // Regular expression characters in a folder search are literal too.
        Assert.Empty((await Search("pay.oll")).Of("Folder"));
        Assert.Empty((await Search("a[r")).Of("Folder"));
    }

    [Fact]
    public async Task Each_kind_shows_a_few_and_says_there_are_more()
    {
        await Seed("srch-fs05");
        await using (var db = _db.CreateContext())
        {
            var payroll = await db.Folders.SingleAsync(f => f.Path == "Payroll" && db.Shares.Any(x => x.Id == f.ShareId && x.UncPath == @"\\srch-fs05\HR"));
            for (var i = 0; i < 12; i++)
            {
                var name = $"timesheet-{i:00}.csv";
                db.Files.Add(new InventoryFile
                {
                    Id = Guid.NewGuid(),
                    ShareId = payroll.ShareId,
                    FolderId = payroll.Id,
                    Name = name,
                    NameKey = name.ToUpperInvariant(),
                    Extension = "csv",
                    FirstSeenJobId = payroll.FirstSeenJobId,
                    LastSeenJobId = payroll.LastSeenJobId,
                });
            }
            await db.SaveChangesAsync();
        }
        var r = await Search("timesheet");
        var files = r.Groups.Single(g => g.Kind == "File");
        Assert.Equal(SearchEndpoints.DefaultLimit, files.Items.Count);
        Assert.True(files.More);

        var more = await _factory.ClientAs(Roles.Viewer).GetFromJsonAsync<Result>("/api/v1/search?q=timesheet&limit=20");
        Assert.Equal(12, more!.Of("File").Count);
        Assert.False(more.Groups.Single(g => g.Kind == "File").More);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("   x   ")]
    [InlineData("tab\there")]
    public async Task Rejects_searches_that_are_too_short_or_not_printable(string q)
    {
        var response = await _factory.ClientAs(Roles.Viewer).GetAsync($"/api/v1/search?q={Uri.EscapeDataString(q)}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Rejects_searches_that_are_too_long()
    {
        var response = await _factory.ClientAs(Roles.Viewer).GetAsync($"/api/v1/search?q={new string('a', SearchEndpoints.MaxTermLength + 1)}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Only_people_who_can_see_posture_can_search()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientAs(Roles.DataOwner).GetAsync("/api/v1/search?q=payroll")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.ClientAs(null).GetAsync("/api/v1/search?q=payroll")).StatusCode);
        foreach (var role in new[] { Roles.Auditor, Roles.SecurityAnalyst, Roles.PlatformAdmin })
        {
            Assert.Equal(HttpStatusCode.OK, (await _factory.ClientAs(role).GetAsync("/api/v1/search?q=payroll")).StatusCode);
        }
    }

    [Fact]
    public async Task A_slow_kind_is_left_out_and_the_rest_still_answer()
    {
        await Seed("srch-fs06");
        var before = SearchEndpoints.KindTimeout;
        try
        {
            // Every query is cancelled at once: each kind reports it timed out, and the request still succeeds.
            SearchEndpoints.KindTimeout = TimeSpan.Zero;
            var r = await Search("payroll");
            Assert.NotEmpty(r.Groups);
            Assert.All(r.Groups, g => Assert.True(g.TimedOut && g.Items.Count == 0, g.Kind));
        }
        finally
        {
            SearchEndpoints.KindTimeout = before;
        }
        // The connection is still usable afterwards.
        Assert.NotEmpty((await Search("payroll")).Of("Folder"));
    }

    [Fact]
    public async Task File_and_folder_searches_use_the_trigram_indexes()
    {
        await using var db = _db.CreateOwnerContext();
        var indexes = await db.Database.SqlQueryRaw<string>(
            "SELECT indexdef AS \"Value\" FROM pg_indexes WHERE indexname IN ('ix_files_current_name_trgm', 'ix_folders_current_path_trgm') ORDER BY indexname").ToListAsync();
        Assert.Equal(2, indexes.Count);
        Assert.All(indexes, d => Assert.Contains("gin_trgm_ops", d, StringComparison.Ordinal));
        Assert.All(indexes, d => Assert.Contains("removed_at IS NULL", d, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("payroll", "payroll")]
    [InlineData("a.b", @"a\.b")]
    [InlineData(@"(x)[y]{z}^$|?*+", @"\(x\)\[y\]\{z\}\^\$\|\?\*\+")]
    public void Regex_characters_are_escaped(string input, string expected) => Assert.Equal(expected, SearchEndpoints.RegexEscape(input));
}
