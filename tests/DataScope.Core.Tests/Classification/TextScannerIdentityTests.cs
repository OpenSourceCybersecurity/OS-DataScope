using System.Diagnostics;
using System.Text;
using DataScope.Core.Classification;

namespace DataScope.Core.Tests.Classification;

// Driver's licence and passport numbers, IBANs and names with addresses or dates of birth (D137). Every value is
// synthetic: licence and passport numbers are made up, IBANs are the published ISO 13616 examples, and every name and
// address is invented.
public class TextScannerIdentityTests
{
    private static Finding? ByRule(string text, string ruleId) =>
        TextScanner.Scan(text).Findings.SingleOrDefault(f => f.RuleId == ruleId);

    private static string Evidence(Finding f) => string.Join("|", f.Evidence);

    [Theory]
    [InlineData("Driver's License: D4821937", "37")]
    [InlineData("Drivers license number D4821937 exp 2027", "37")]
    [InlineData("DL#: 482 193 765", "65")]
    [InlineData("DL No. S530-4609-1278", "78")]
    [InlineData("drivers lic. A123-456-78-901-0", "-0")]
    [InlineData("DLN 99887766", "66")]
    [InlineData("Driving licence: MORGA753116SM9IJ", "IJ")]
    public void Finds_a_licence_number_after_its_label_at_medium_and_masks_it(string text, string last)
    {
        var f = ByRule(text, "DS-DL-01")!;
        Assert.Equal(FindingCategory.DriversLicense, f.Category);
        Assert.Equal(Confidence.Medium, f.Confidence);
        Assert.Contains("DL ****" + last, Evidence(f), StringComparison.Ordinal);
        Assert.DoesNotContain("4821", Evidence(f), StringComparison.Ordinal);
        Assert.DoesNotContain("4609", Evidence(f), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("A driver's license is required for this role.")]
    [InlineData("Driver's license: 2024")] // too few digits
    [InlineData("DL: 1111111")] // one digit repeated
    [InlineData("Driver's license: 12345678")] // a run
    [InlineData("Order D1234567 shipped")] // no label
    [InlineData("Driver's license: valid until 2027")]
    public void Ignores_licence_look_alikes(string text) => Assert.Null(ByRule(text, "DS-DL-01"));

    [Fact]
    public void Every_value_in_a_licence_column_counts_at_high()
    {
        var f = ByRule("Name,DL Number,State\nCasey Byrne,D4821937,CA\nDana Shah,S7046213,WA\nEli Ward,,NY\n", "DS-DL-01")!;
        Assert.Equal((Confidence.High, 2), (f.Confidence, f.Count));
        Assert.Equal(Sensitivity.Restricted, ClassificationPolicy.Classify([f]));
        Assert.Contains("DL ****13", Evidence(f), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Passport No: 548120934", "34")]
    [InlineData("passport number C03005988", "88")]
    [InlineData("Passport #: AB4821937", "37")]
    public void Finds_a_passport_number_after_its_label(string text, string last)
    {
        var f = ByRule(text, "DS-PASS-01")!;
        Assert.Equal((FindingCategory.Passport, Confidence.Medium), (f.Category, f.Confidence));
        Assert.Contains("Passport ****" + last, Evidence(f), StringComparison.Ordinal);
        Assert.DoesNotContain("54812", Evidence(f), StringComparison.Ordinal);
        Assert.DoesNotContain("48219", Evidence(f), StringComparison.Ordinal);
        Assert.Equal(Sensitivity.Confidential, ClassificationPolicy.Classify([f]));
    }

    [Theory]
    [InlineData("Bring your passport photo to reception.")]
    [InlineData("Passport expiry: 2027-04-01")]
    [InlineData("Passport: 12345")]
    public void Ignores_passport_look_alikes(string text) => Assert.Null(ByRule(text, "DS-PASS-01"));

    [Fact]
    public void Every_value_in_a_passport_column_counts_at_high()
    {
        var f = ByRule("Traveller\tPassport number\tPassport expiry\nCasey Byrne\t548120934\t2029-01-31\nDana Shah\tC03005988\t2030-06-30\n", "DS-PASS-01")!;
        Assert.Equal((Confidence.High, 2), (f.Confidence, f.Count));
    }

    [Theory]
    [InlineData("GB82WEST12345698765432")]
    [InlineData("DE89370400440532013000")]
    [InlineData("FR1420041010050500013M02606")]
    [InlineData("NO9386011117947")]
    public void Accepts_valid_ibans(string iban) => Assert.True(TextScanner.IsValidIban(iban));

    [Theory]
    [InlineData("GB82WEST12345698765433")] // check digits wrong
    [InlineData("GB82WEST1234569876543")] // too short for GB
    [InlineData("ZZ82WEST12345698765432")] // no such country
    [InlineData("gb82west12345698765432")] // lower case
    [InlineData("")]
    public void Rejects_invalid_ibans(string iban) => Assert.False(TextScanner.IsValidIban(iban));

    [Fact]
    public void Finds_an_iban_in_groups_next_to_a_label_at_high_and_hides_the_bank_code()
    {
        var f = ByRule("Please pay to IBAN: GB82 WEST 1234 5698 7654 32 by Friday.", "DS-BANK-02")!;
        Assert.Equal((FindingCategory.BankAccount, Confidence.High), (f.Category, f.Confidence));
        Assert.Contains("GB** **** **** 5432", Evidence(f), StringComparison.Ordinal);
        Assert.DoesNotContain("WEST", Evidence(f), StringComparison.Ordinal);
        Assert.Contains("by Friday", Evidence(f), StringComparison.Ordinal);
    }

    [Fact]
    public void Finds_an_iban_on_its_own_at_medium_and_stops_at_its_length()
    {
        Assert.Equal(Confidence.Medium, ByRule("Ref DE89370400440532013000 noted", "DS-BANK-02")!.Confidence);
        var f = ByRule("DE89 3704 0044 0532 0130 00 COBADEFFXXX", "DS-BANK-02")!;
        Assert.Contains("DE** **** **** 3000", Evidence(f), StringComparison.Ordinal);
        Assert.Null(ByRule("GB82 WEST 1234 5698 7654 33", "DS-BANK-02"));
        Assert.Null(ByRule("Part GB82WEST123456987654321 in stock", "DS-BANK-02")); // one character too many
    }

    [Theory]
    [InlineData("Name: Casey Byrne\nAddress: 12 Oak Street, Springfield, IL 62704", "[name] + [street address]")]
    [InlineData("Patient name: Dana Shah DOB: 04/12/1980", "[name] + [date of birth]")]
    [InlineData("Employee - Eli Ward, born on 7/30/1975, joined in 2019", "[name] + [date of birth]")]
    [InlineData("Tenant: Frankie J. Lowe, unit at 4021 N Elm Ave.", "[name] + [street address]")]
    [InlineData("Casey Byrne\n12 Oak Street\nSpringfield, IL 62704", "[name] + [street address]")]
    public void Finds_a_name_with_an_address_or_date_of_birth(string text, string masked)
    {
        var f = ByRule(text, "DS-PII-01")!;
        Assert.Equal((FindingCategory.PersonalDetails, Confidence.Medium, 1), (f.Category, f.Confidence, f.Count));
        Assert.Contains(masked, Evidence(f), StringComparison.Ordinal);
        foreach (var part in new[] { "Casey", "Byrne", "Dana", "Eli Ward", "Frankie", "Oak Street", "Elm" })
        {
            Assert.DoesNotContain(part, Evidence(f), StringComparison.Ordinal);
        }
        Assert.Equal(Sensitivity.Confidential, ClassificationPolicy.Classify([f]));
    }

    [Theory]
    [InlineData("Name: Casey Byrne")]
    [InlineData("Ship to 12 Oak Street, Springfield, IL 62704")]
    [InlineData("Customer: Acme Corp, 12 Oak Street")]
    [InlineData("Our office: Springfield Clinic\n12 Oak Street\nSpringfield, IL 62704")]
    [InlineData("Casey Byrne\n12 Oak Street")] // a label with no state and ZIP is too weak
    [InlineData("Name: Casey Byrne\n\nName: Dana Shah, 12 Oak Street")] // the address is Dana's
    public void Ignores_names_or_addresses_on_their_own_and_businesses(string text)
    {
        var f = ByRule(text, "DS-PII-01");
        Assert.True(f is null || f.Count == 1, $"{f?.Count} records");
        if (text.StartsWith("Name: Casey Byrne\n\n", StringComparison.Ordinal))
        {
            Assert.NotNull(f);
        }
        else
        {
            Assert.Null(f);
        }
    }

    [Fact]
    public void Counts_each_row_of_a_people_table_and_a_list_of_five_is_high_but_not_restricted()
    {
        var rows = new StringBuilder("First name,Last name,Home address,City\n");
        foreach (var (first, last, street) in new[] { ("Casey", "Byrne", "12 Oak St"), ("Dana", "Shah", "\"4021 N Elm Ave, Apt 4\""), ("Eli", "Ward", "7 Pine Rd"),
            ("Frankie", "Lowe", "PO Box 118"), ("Gale", "Moss", "55 Birch Ln") })
        {
            rows.Append(first).Append(',').Append(last).Append(',').Append(street).Append(",Springfield\n");
        }
        rows.Append("Total,,,\n");
        var f = ByRule(rows.ToString(), "DS-PII-01")!;
        Assert.Equal((Confidence.High, TextScanner.ManyPeople), (f.Confidence, f.Count));
        Assert.Equal(Sensitivity.Confidential, ClassificationPolicy.Classify([f]));
        Assert.DoesNotContain("Oak", Evidence(f), StringComparison.Ordinal);

        var dob = ByRule("Employee\tDOB\tTeam\nCasey Byrne\t1980-04-12\tOps\nDana Shah\t\tOps\n", "DS-PII-01")!;
        Assert.Equal((Confidence.Medium, 1), (dob.Confidence, dob.Count));
        Assert.Contains("[name] + [date of birth]", Evidence(dob), StringComparison.Ordinal);

        // A table of names alone, or a blank line before the rows, is not a list of people's details.
        Assert.Null(ByRule("Name,Team\nCasey Byrne,Ops\n", "DS-PII-01"));
        Assert.Null(ByRule("Name,Address\n\nCasey Byrne,12 Oak St\n", "DS-PII-01"));
    }

    [Fact]
    public void A_licence_passport_or_personal_details_identify_the_person_in_health_information()
    {
        const string clinical = "diagnosis and prescription renewed";
        Assert.Equal(Confidence.Low, ByRule(clinical, "DS-PHI-01")!.Confidence);
        Assert.Equal(Confidence.Medium, ByRule($"Driver's license D4821937: {clinical}", "DS-PHI-01")!.Confidence);
        Assert.Equal(Confidence.Medium, ByRule($"Passport no 548120934. {clinical}", "DS-PHI-01")!.Confidence);
        Assert.Equal(Confidence.Medium, ByRule($"Name: Casey Byrne DOB: 04/12/1980 {clinical}", "DS-PHI-01")!.Confidence);
    }

    [Fact]
    public void Ssn_columns_still_work_through_the_shared_column_reader()
    {
        var f = ByRule("Name\tSSN\nCasey Byrne\t5124833\n", "DS-SSN-01")!;
        Assert.Equal(Confidence.High, f.Confidence);
        Assert.Contains("*****4833", Evidence(f), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Acme Corp", true)]
    [InlineData("Northwind Traders Inc.", true)]
    [InlineData("Springfield Clinic", true)]
    [InlineData("Casey Byrne", false)]
    [InlineData("Dana Cohen", false)]
    public void Business_names_are_not_people(string name, bool business) => Assert.Equal(business, TextScanner.IsOrganisation(name));

    [Fact]
    public void Long_hostile_text_takes_linear_time()
    {
        var text = new StringBuilder();
        while (text.Length < TextScanner.MaxScanChars)
        {
            text.Append("Name: Casey Byrne 12 Oak Street Name: DL# 1 passport 1 GB82 WEST IBAN ");
        }
        var watch = Stopwatch.StartNew();
        var result = TextScanner.Scan(text.ToString());
        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"{watch.Elapsed}");
        Assert.True(result.Truncated);
    }
}
