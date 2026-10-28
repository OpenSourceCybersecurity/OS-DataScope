using System.Net.Http.Json;
using DataScope.Api.Compliance;
using DataScope.Core.Classification;
using DataScope.Core.Compliance;
using DataScope.Core.Security;
using DataScope.Persistence.Entities;

namespace DataScope.Api.Tests;

/// <summary>
/// Driver's licence and passport numbers, IBANs and names with addresses found by a real scan of the synthetic
/// governance lab, and counted by the US state breach laws standard (D137). Every name and number here is made up;
/// the IBAN is the published ISO 13616 example.
/// </summary>
[Trait("Category", "Database")]
public sealed class PersonalDataApiTests : IClassFixture<PersonalDataApiTests.PersonalDataLab>
{
    private readonly GovernanceApiTests.Lab _lab;

    public PersonalDataApiTests(PersonalDataLab lab) => _lab = lab.Lab;

    public sealed class PersonalDataLab : IAsyncLifetime
    {
        public GovernanceApiTests.Lab Lab { get; } = new();

        public async Task InitializeAsync()
        {
            await Lab.InitializeAsync();
            Lab.Write(@"Ops\drivers.csv", "Name,DL Number,State\nCasey Byrne,D4821937,CA\nDana Shah,S7046213,WA\n");
            Lab.Write(@"Ops\travel.txt", "Trip to Lisbon for Eli Ward. Passport no: 548120934, returning Friday.");
            Lab.Write(@"Finance\supplier.txt", "Pay the supplier by transfer to IBAN GB82 WEST 1234 5698 7654 32.");
            Lab.Write(@"HR\mailing.csv", "First name,Last name,Home address\nCasey,Byrne,12 Oak St\nDana,Shah,\"4021 N Elm Ave, Apt 4\"\n");
            await Lab.RescanAsync();
            var admin = Lab.Factory.ClientAs(Roles.PlatformAdmin, "S-1-5-21-40-50-60-1110", @"GOV\admin");
            var response = await admin.PutAsJsonAsync("/api/v1/compliance/standards/state-breach", new { enabled = true, reason = "We keep employee and customer records." });
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        }

        public Task DisposeAsync() => Lab.DisposeAsync();
    }

    private sealed record CategoryView(string Category, string Confidence, int Matches);

    private sealed record FileRow(string Name, string Sensitivity, string ClassifiedPolicy, List<CategoryView> Categories, List<string> Standards);

    private sealed record PageDto<T>(List<T> Items, int Total);

    private async Task<List<FileRow>> FilesAsync(string query = "")
    {
        var admin = _lab.Factory.ClientAs(Roles.PlatformAdmin, "S-1-5-21-40-50-60-1110", @"GOV\admin");
        return (await admin.GetFromJsonAsync<PageDto<FileRow>>($"/api/v1/classification/files?serverId={_lab.ServerId}&limit=100{query}"))!.Items;
    }

    [Fact]
    public async Task A_scan_finds_the_new_kinds_and_labels_them()
    {
        var files = (await FilesAsync()).ToDictionary(f => f.Name);
        Assert.All(files.Values, f => Assert.Equal(ClassificationPolicy.CurrentVersion, f.ClassifiedPolicy));

        var drivers = files["drivers.csv"];
        Assert.Contains(drivers.Categories, c => c.Category == "Driver's licence");
        Assert.Equal("Restricted", drivers.Sensitivity); // a licence column is High
        Assert.Contains(files["travel.txt"].Categories, c => c.Category == "Passport");
        Assert.Equal("Confidential", files["travel.txt"].Sensitivity);
        Assert.Contains(files["supplier.txt"].Categories, c => c.Category == "Bank account");
        var mailing = files["mailing.csv"];
        Assert.Contains(mailing.Categories, c => c.Category == "Personal details");
        Assert.Equal("Confidential", mailing.Sensitivity);

        var licences = await FilesAsync("&category=Driver's licence");
        Assert.Equal(["drivers.csv"], licences.Select(f => f.Name));
        Assert.Equal(["mailing.csv"], (await FilesAsync("&category=PersonalDetails")).Select(f => f.Name));
    }

    [Fact]
    public async Task State_breach_laws_count_licence_and_passport_numbers_but_not_names_and_addresses()
    {
        var files = (await FilesAsync()).ToDictionary(f => f.Name);
        Assert.Contains("state-breach", files["drivers.csv"].Standards);
        Assert.Contains("state-breach", files["travel.txt"].Standards);
        Assert.Contains("state-breach", files["supplier.txt"].Standards);
        Assert.DoesNotContain("state-breach", files["mailing.csv"].Standards);
    }

    [Fact]
    public void A_standard_saved_with_an_earlier_versions_defaults_gets_todays()
    {
        var definition = ComplianceCatalog.Find("state-breach")!;
        ComplianceStandardSetting Saved(params FindingCategory[] categories) => new()
        {
            StandardId = definition.Id,
            Enabled = true,
            Categories = [.. categories.Select(c => c.ToString())],
            MinConfidence = Confidence.Medium,
            Reason = "We keep employee records.",
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBySid = "S-1",
            UpdatedByName = "t",
        };

        // Turned on before driver's licence and passport numbers were detected, with the defaults of the time.
        var old = ComplianceScope.From(definition, Saved([.. definition.EarlierDefaults![0].Reverse()]));
        Assert.Equal(definition.DefaultCategories.Order(), old.Categories.Order());
        Assert.Contains(FindingCategory.DriversLicense, old.Categories);

        // A list the administrator chose stays as it is.
        var chosen = ComplianceScope.From(definition, Saved(FindingCategory.UsSsn, FindingCategory.PaymentCard));
        Assert.Equal([FindingCategory.UsSsn, FindingCategory.PaymentCard], chosen.Categories);
        var chosenWithout = ComplianceScope.From(definition, Saved([.. definition.EarlierDefaults[0].Where(c => c != FindingCategory.Credentials)]));
        Assert.DoesNotContain(FindingCategory.DriversLicense, chosenWithout.Categories);
    }
}
