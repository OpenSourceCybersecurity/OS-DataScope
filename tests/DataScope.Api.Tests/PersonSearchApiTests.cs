using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using DataScope.Core.Activity;
using DataScope.Core.Inventory;
using DataScope.Core.Security;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Api.Tests;

/// <summary>
/// Find everything about a person (D160) on the synthetic governance lab, after a real scan and directory sync, with
/// a made-up folder owner and file activity added. Every name and number here is made up.
/// </summary>
[Trait("Category", "Database")]
public sealed class PersonSearchApiTests : IClassFixture<PersonSearchApiTests.PersonLab>
{
    private const string Maria = "S-1-5-21-40-50-60-1101";
    private const string Nate = "S-1-5-21-40-50-60-1102";
    private readonly PersonLab _lab;

    public PersonSearchApiTests(PersonLab lab) => _lab = lab;

    public sealed class PersonLab : IAsyncLifetime
    {
        public GovernanceApiTests.Lab Lab { get; } = new();
        public Guid PublicRootId { get; private set; }

        public async Task InitializeAsync()
        {
            await Lab.InitializeAsync();
            Lab.Write(@"HR\Maria Example - appraisal.txt", "Appraisal notes for the year.");
            Lab.Write(@"Public\Interviews\Maria Example\notes.txt", "Interview notes.");
            Lab.Write(@"Finance\expenses.txt", "Expense claim from Maria Example, SSN 512-48-3302, approved.");
            await Lab.RescanAsync();

            await using var db = Lab.Database.CreateContext();
            var now = DateTimeOffset.UtcNow;

            // Maria owns the Public share's top folder and a folder below it: only the top one is listed.
            var sd = new StoredSecurityDescriptor { Hash = RandomNumberGenerator.GetBytes(32), Raw = [1, 0, 4, 128], OwnerSid = Maria };
            db.SecurityDescriptors.Add(sd);
            await db.SaveChangesAsync();
            var pub = Lab.Shares["Public"];
            var owned = await db.Folders.Where(f => f.ShareId == pub && (f.Path == "" || f.Path == "Interviews")).ToListAsync();
            Assert.Equal(2, owned.Count);
            owned.ForEach(f => f.SecurityId = sd.Id);
            PublicRootId = owned.Single(f => f.Path == "").Id;

            FileAuditEvent Audit(long id, string sid, int daysAgo) => new()
            {
                ServerId = Lab.ServerId,
                Channel = "Security",
                RecordId = id,
                EventId = 4663,
                EventTime = now.AddDays(-daysAgo),
                IngestedAt = now,
                PrincipalSid = sid,
                EventShareName = @"\\*\HR",
                ObjectPath = @"D:\Shares\HR\handbook.txt",
                RawXml = "<Event/>",
            };
            db.FileAuditEvents.AddRange(Audit(1, Maria, 3), Audit(2, Maria, 1), Audit(3, Nate, 1));
            await db.SaveChangesAsync();
        }

        public Task DisposeAsync() => Lab.DisposeAsync();
    }

    private HttpClient Admin => _lab.Lab.Factory.ClientAs(Roles.PlatformAdmin, "S-1-5-21-40-50-60-1110", @"GOV\admin");

    private static async Task<JsonElement> Search(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync("/api/v1/people/search", body);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static List<JsonElement> Items(JsonElement result, string why) =>
        [.. result.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("why").GetString() == why)];

    private static string[] Of(params string[] values) => values;

    private static List<string?> Names(IEnumerable<JsonElement> items) => [.. items.Select(i => i.GetProperty("name").GetString())];

    [Fact]
    public async Task A_person_picked_from_Active_Directory_brings_everything_Data_Scope_holds_about_them()
    {
        var result = await Search(Admin, new { sid = Maria, reason = "Subject access request SAR-2026-014" });

        var account = Assert.Single(result.GetProperty("accounts").EnumerateArray());
        Assert.Equal(("maria", "maria@gov.test"), (account.GetProperty("name").GetString(), account.GetProperty("mail").GetString()));
        var searched = result.GetProperty("searched");
        Assert.Contains("Maria Example", searched.GetProperty("names").EnumerateArray().Select(n => n.GetString()));
        Assert.Equal(["maria@gov.test"], searched.GetProperty("addresses").EnumerateArray().Select(a => a.GetString()));

        var named = Items(result, "named");
        Assert.Contains("Maria Example - appraisal.txt", Names(named));
        var folder = Assert.Single(named, i => i.GetProperty("kind").GetString() == "Folder");
        Assert.Equal(("Maria Example", "Interviews"), (folder.GetProperty("name").GetString(), folder.GetProperty("path").GetString()));

        // The scan's evidence around the SSN mentions her name; the SSN itself is never returned.
        var evidence = Assert.Single(Items(result, "evidence"));
        Assert.Equal("expenses.txt", evidence.GetProperty("name").GetString());
        Assert.Contains("Maria Example", evidence.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("512-48", result.GetRawText(), StringComparison.Ordinal);

        var owned = Assert.Single(Items(result, "owner"));
        Assert.Equal(_lab.PublicRootId.ToString(), owned.GetProperty("id").GetString());

        var dataOwner = Assert.Single(Items(result, "dataOwner"));
        Assert.Equal(("Data owner", _lab.Lab.Shares["HR"]), (dataOwner.GetProperty("detail").GetString(), dataOwner.GetProperty("shareId").GetGuid()));

        // Her own events; Nate's are not counted.
        Assert.Equal(2, result.GetProperty("activity").GetProperty("fileEvents").GetInt32());
        Assert.All(result.GetProperty("sections").EnumerateArray(), s => Assert.False(s.GetProperty("timedOut").GetBoolean()));
    }

    [Fact]
    public async Task A_name_that_is_not_in_Active_Directory_is_found_in_names_and_scan_evidence_only()
    {
        var result = await Search(Admin, new { names = Of("  Avery   Okafor "), reason = "Former contractor's request" });

        Assert.Empty(result.GetProperty("accounts").EnumerateArray());
        Assert.Equal(["Avery Okafor"], result.GetProperty("searched").GetProperty("names").EnumerateArray().Select(n => n.GetString()));
        Assert.Equal("pay.csv", Assert.Single(Items(result, "evidence")).GetProperty("name").GetString());
        foreach (var why in Of("named", "owner", "dataOwner"))
        {
            Assert.Empty(Items(result, why));
        }
        Assert.Equal(0, result.GetProperty("activity").GetProperty("fileEvents").GetInt32());
    }

    [Fact]
    public async Task An_address_finds_the_account_and_a_backup_owner_is_marked_as_such()
    {
        var result = await Search(Admin, new { addresses = Of("NATE@gov.test"), reason = "Leaver's data request" });

        Assert.Equal(Nate, Assert.Single(result.GetProperty("accounts").EnumerateArray()).GetProperty("sid").GetString());
        var owned = Items(result, "dataOwner");
        Assert.Contains(owned, o => o.GetProperty("shareId").GetGuid() == _lab.Lab.Shares["HR"] && o.GetProperty("detail").GetString() == "Backup data owner");
        Assert.Contains(owned, o => o.GetProperty("shareId").GetGuid() == _lab.Lab.Shares["Ops"] && o.GetProperty("detail").GetString() == "Data owner");
        Assert.Equal(1, result.GetProperty("activity").GetProperty("fileEvents").GetInt32());
    }

    [Fact]
    public async Task Every_search_is_in_the_audit_log_with_who_was_searched_for_and_why()
    {
        await Search(Admin, new { names = Of("Dana Shah"), addresses = Of("dana@partner.example"), reason = "SAR-2026-020" });

        await using var db = _lab.Lab.Database.CreateContext();
        var entry = await db.AppAuditLog.Where(e => e.Action == "person.search").OrderByDescending(e => e.Sequence).FirstAsync();
        Assert.Equal((@"GOV\admin", "Dana Shah, dana@partner.example"), (entry.ActorName, entry.Target));
        Assert.Contains("SAR-2026-020", entry.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_platform_admins_may_search_and_every_search_needs_a_reason_and_a_narrow_term()
    {
        var body = new { sid = Maria, reason = "SAR" };
        foreach (var role in new[] { Roles.Auditor, Roles.SecurityAnalyst, Roles.Viewer, Roles.DataOwner })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await _lab.Lab.Factory.ClientAs(role).PostAsJsonAsync("/api/v1/people/search", body)).StatusCode);
        }
        async Task<string> Refused(object request)
        {
            var response = await Admin.PostAsJsonAsync("/api/v1/people/search", request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            return await response.Content.ReadAsStringAsync();
        }
        Assert.Contains("\"reason\"", await Refused(new { sid = Maria }), StringComparison.Ordinal);
        Assert.Contains("\"names\"", await Refused(new { reason = "SAR" }), StringComparison.Ordinal);
        Assert.Contains("\"names\"", await Refused(new { names = Of("Al"), reason = "SAR" }), StringComparison.Ordinal);
        Assert.Contains("\"names\"", await Refused(new { names = Of("123"), reason = "SAR" }), StringComparison.Ordinal);
        Assert.Contains("\"addresses\"", await Refused(new { addresses = Of("%@gov.test"), reason = "SAR" }), StringComparison.Ordinal);
        Assert.Contains("\"addresses\"", await Refused(new { addresses = Of("Maria <maria@gov.test>"), reason = "SAR" }), StringComparison.Ordinal);
        Assert.Contains("\"sid\"", await Refused(new { sid = "S-1-5-21-40-50-60-9999", reason = "SAR" }), StringComparison.Ordinal);
        Assert.Contains("\"sid\"", await Refused(new { sid = "not a sid", reason = "SAR" }), StringComparison.Ordinal);

        // Refused searches are not run, so they are not audited either.
        await using var db = _lab.Lab.Database.CreateContext();
        Assert.False(await db.AppAuditLog.AnyAsync(e => e.Action == "person.search" && e.Target.Contains("9999")));
    }
}
