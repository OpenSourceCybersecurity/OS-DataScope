using DataScope.Core.Classification;

namespace DataScope.Core.Tests.Classification;

// Every value below is synthetic. Card numbers are published test numbers; SSNs are random numbers in valid ranges.
public class TextScannerTests
{
    private static Finding Single(string text) => Assert.Single(TextScanner.Scan(text).Findings);

    private static Finding? ByRule(string text, string ruleId) =>
        TextScanner.Scan(text).Findings.SingleOrDefault(f => f.RuleId == ruleId);

    [Fact]
    public void Finds_a_formatted_ssn_next_to_a_keyword_with_high_confidence_and_masks_it()
    {
        var f = Single("Employee SSN: 512-48-3307 on file");
        Assert.Equal("DS-SSN-01", f.RuleId);
        Assert.Equal(FindingCategory.UsSsn, f.Category);
        Assert.Equal(Confidence.High, f.Confidence);
        Assert.Contains("***-**-3307", f.Evidence[0], StringComparison.Ordinal);
        Assert.DoesNotContain("512", string.Join("|", f.Evidence), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("000", "12", "3456")]
    [InlineData("666", "12", "3456")]
    [InlineData("912", "12", "3456")]
    [InlineData("512", "00", "3456")]
    [InlineData("512", "12", "0000")]
    [InlineData("078", "05", "1120")]
    [InlineData("219", "09", "9999")]
    public void Rejects_invalid_ssa_ranges(string area, string group, string serial) =>
        Assert.False(TextScanner.IsValidSsn(area, group, serial));

    [Fact]
    public void Ignores_bare_nine_digit_numbers_without_ssn_context() =>
        Assert.Empty(TextScanner.Scan("Invoice 482913374 paid. Order 7781203345 shipped.").Findings);

    [Fact]
    public void Counts_a_bare_nine_digit_number_next_to_an_ssn_keyword_at_medium()
    {
        var f = Single("Taxpayer ID 512483307");
        Assert.Equal(Confidence.Medium, f.Confidence);
        Assert.Contains("*****3307", f.Evidence[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SSN 512 48 3307", Confidence.High)]
    [InlineData("Ref 512 48 3307", Confidence.Medium)]
    [InlineData("SS#: 512-48-3307", Confidence.High)]
    [InlineData("SS No. 512-48-3307", Confidence.High)]
    public void Finds_spaced_ssns_and_more_keyword_spellings(string text, Confidence expected)
    {
        var f = Single(text);
        Assert.Equal(expected, f.Confidence);
        Assert.Contains("***-**-3307", f.Evidence[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData('\t')]
    [InlineData(',')]
    [InlineData(';')]
    [InlineData('|')]
    public void Every_row_under_an_ssn_column_header_is_high_confidence(char d)
    {
        // Long enough that most rows are far outside the keyword window around the header.
        var rows = Enumerable.Range(0, 40).Select(i => $"Person {i}{d}Payroll{d}{512483300 + i}{d}Full time");
        var text = $"Name{d}Department{d}Social Security Number{d}Status\n" + string.Join("\n", rows);
        var f = Single(text);
        Assert.Equal(Confidence.High, f.Confidence);
        Assert.Equal(40, f.Count);
        Assert.Contains("*****3300", f.Evidence[0], StringComparison.Ordinal);
        Assert.DoesNotContain("5124833", string.Join("|", f.Evidence), StringComparison.Ordinal);
    }

    [Fact]
    public void A_spreadsheet_ssn_column_counts_numbers_that_lost_their_leading_zero_and_formatted_values()
    {
        var text = "Name\tSSN\t\n" + string.Join("\n", Enumerable.Range(0, 30).Select(i => $"Person {i}\t{12483300 + i}\t")) + "\nLast\t\"045 12 9876\"\t\n";
        var f = Single(text);
        Assert.Equal(Confidence.High, f.Confidence);
        Assert.Equal(31, f.Count);
        Assert.Contains("*****3300", f.Evidence[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_one_column_list_under_an_ssn_heading_is_found()
    {
        var text = "SSN\r\n" + string.Join("\r\n", Enumerable.Range(0, 20).Select(i => $"{512483300 + i}"));
        Assert.Equal(20, Single(text).Count);
    }

    [Fact]
    public void Nine_digit_columns_without_an_ssn_header_are_not_flagged()
    {
        var text = "Invoice,Customer,Amount\n" + string.Join("\n", Enumerable.Range(0, 30).Select(i => $"{512483300 + i},Northwind,{100 + i}.00"));
        Assert.Empty(TextScanner.Scan(text).Findings);
    }

    [Fact]
    public void A_blank_line_ends_an_ssn_table_and_invalid_numbers_under_the_header_are_skipped()
    {
        var text = "Name,SSN\nAvery,512483307\nJordan,987654321\nRiley,000123456\n\n" +
            string.Join("\n", Enumerable.Range(0, 10).Select(i => new string('x', 100) + $",{512483400 + i}"));
        var f = Single(text);
        Assert.Equal(1, f.Count);
    }

    [Fact]
    public void A_value_found_by_two_rules_is_counted_once_at_the_higher_confidence()
    {
        // The keyword rule and the column rule both see the first row.
        var f = Single("SSN\n512-48-3307\n512-48-3308");
        Assert.Equal(2, f.Count);
        Assert.Equal(Confidence.High, f.Confidence);
    }

    [Theory]
    [InlineData("900", "77", "2227", true)]
    [InlineData("912", "50", "1234", true)]
    [InlineData("999", "99", "0001", true)]
    [InlineData("950", "92", "4400", true)]
    [InlineData("900", "12", "3456", false)] // middle digits outside the IRS ranges
    [InlineData("900", "66", "3456", false)]
    [InlineData("900", "89", "3456", false)]
    [InlineData("900", "93", "3456", false)]
    [InlineData("900", "77", "0000", false)]
    [InlineData("987", "65", "4321", false)] // reserved for advertising
    [InlineData("512", "77", "2227", false)] // an SSN, not an ITIN
    public void Recognises_itin_ranges(string area, string group, string serial, bool valid) =>
        Assert.Equal(valid, TextScanner.IsValidItin(area, group, serial));

    [Fact]
    public void Finds_itins_in_an_ssn_column_as_itins_not_ssns()
    {
        // Spreadsheets often keep ITINs in the SSN column; numbers starting with 9 are never SSNs.
        var rows = Enumerable.Range(0, 25).Select(i => $"Person {i}\t900-77-{2200 + i}\tFull time");
        var f = Single("Name\tSSN\tStatus\n" + string.Join("\n", rows));
        Assert.Equal(("DS-ITIN-01", FindingCategory.UsItin, Confidence.High, 25), (f.RuleId, f.Category, f.Confidence, f.Count));
        Assert.Contains("***-**-2200", f.Evidence[0], StringComparison.Ordinal);
        Assert.DoesNotContain("900-77", string.Join("|", f.Evidence), StringComparison.Ordinal);
        Assert.Equal(Sensitivity.Restricted, ClassificationPolicy.Classify([f]));
    }

    [Theory]
    [InlineData("ITIN: 912-70-1234", Confidence.High)]
    [InlineData("Individual taxpayer number 912 70 1234", Confidence.High)]
    [InlineData("Ref 912-70-1234", Confidence.Medium)]
    public void Finds_formatted_itins_with_and_without_a_keyword(string text, Confidence expected)
    {
        var f = Single(text);
        Assert.Equal(("DS-ITIN-01", expected), (f.RuleId, f.Confidence));
        Assert.Contains("***-**-1234", f.Evidence[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Ssns_and_itins_in_one_file_are_reported_by_their_own_rules()
    {
        var found = TextScanner.Scan("Name,SSN\nAvery,512-48-3307\nJordan,912-70-1234\nRiley,900-12-3456").Findings;
        Assert.Equal(1, Assert.Single(found, x => x.RuleId == "DS-SSN-01").Count);
        Assert.Equal(1, Assert.Single(found, x => x.RuleId == "DS-ITIN-01").Count);
        Assert.Equal(2, found.Count); // 900-12-3456 is neither
    }

    [Fact]
    public void Nine_hundred_numbers_outside_the_itin_ranges_are_not_flagged_even_under_an_ssn_header() =>
        Assert.Empty(TextScanner.Scan("SSN\n900-12-3456\n955-40-1111\n987-65-4321").Findings);

    [Fact]
    public void Reports_formatted_lookalikes_without_context_at_medium_a_known_false_positive() =>
        Assert.Equal(Confidence.Medium, Single("Part# 411-22-3333 conveyor bearing").Confidence);

    [Theory]
    [InlineData("4111111111111111", true)]
    [InlineData("4111111111111112", false)]
    [InlineData("", false)]
    [InlineData("41x1", false)]
    public void Validates_luhn(string digits, bool expected) => Assert.Equal(expected, TextScanner.Luhn(digits));

    [Fact]
    public void Finds_test_card_numbers_and_masks_all_but_last_four()
    {
        var f = Single("Visa 4111 1111 1111 1111 exp 04/28; Amex 3782 822463 10005");
        Assert.Equal(FindingCategory.PaymentCard, f.Category);
        Assert.Equal(2, f.Count);
        Assert.Equal(Confidence.High, f.Confidence);
        var evidence = string.Join("|", f.Evidence);
        Assert.DoesNotContain("4111 1111", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("822463", evidence, StringComparison.Ordinal);
        Assert.Contains("**** **** **** 0005", evidence, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Tracking 9999 1111 1111 1111")]
    [InlineData("Ref 4444444444444444 card")]
    [InlineData("Call 555-867-5309 or 1-800-555-0199")]
    public void Ignores_digit_runs_that_are_not_cards(string text) => Assert.Empty(TextScanner.Scan(text).Findings);

    [Theory]
    [InlineData("5555555555554444")]
    [InlineData("2223003122003222")]
    [InlineData("6011111111111117")]
    [InlineData("378282246310005")]
    public void Recognizes_other_issuers(string number) =>
        Assert.Equal(Confidence.Medium, Single("Number " + number).Confidence);

    [Fact]
    public void Requires_a_checksum_valid_routing_number()
    {
        Assert.True(TextScanner.IsValidAba("011000015"));
        Assert.False(TextScanner.IsValidAba("011000016"));
        Assert.False(TextScanner.IsValidAba("000000000"));
        Assert.False(TextScanner.IsValidAba("01100001"));
        var f = Single("Routing number: 011000015\nAccount number: 44001239");
        Assert.Equal(FindingCategory.BankAccount, f.Category);
        Assert.Equal(Confidence.High, f.Confidence);
        Assert.Contains("account ****1239", f.Evidence[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_routing_number_with_no_account_nearby_is_low() =>
        Assert.Equal(Confidence.Low, Single("ABA 011000015").Confidence);

    [Fact]
    public void Finds_private_keys_and_passwords_skipping_placeholders()
    {
        const string text = "-----BEGIN RSA PRIVATE KEY-----\nabc\npassword = changeme\npwd: Winter2026!";
        Assert.Equal(Confidence.High, ByRule(text, "DS-CRED-01")?.Confidence);
        var secret = ByRule(text, "DS-CRED-02");
        Assert.Equal(1, secret?.Count);
        Assert.DoesNotContain("Winter2026", string.Join("|", secret!.Evidence), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("password = ********")]
    [InlineData("password: <your password here>")]
    [InlineData("api_key = {{API_KEY}}")]
    [InlineData("secret=xxxxxxxx")]
    [InlineData("pwd: abc")]
    [InlineData("password=${DB_PASSWORD}")]
    [InlineData("password = %DBPASS%")]
    public void Skips_password_placeholders(string text) => Assert.Null(ByRule(text, "DS-CRED-02"));

    [Fact]
    public void Aws_access_key_ids_are_high_and_masked()
    {
        var f = Single("aws key AKIAIOSFODNN7EXAMPLE in config");
        Assert.Equal(Confidence.High, f.Confidence);
        Assert.DoesNotContain("IOSFODNN7", string.Join("|", f.Evidence), StringComparison.Ordinal);
    }

    [Fact]
    public void Needs_at_least_two_health_signals()
    {
        Assert.Empty(TextScanner.Scan("The patient portal is down").Findings);
        var f = ByRule("Employee SSN 512-48-3307. Physician statement: diagnosis M54.5, treatment plan attached.", "DS-PHI-01");
        Assert.Equal(Confidence.High, f?.Confidence);
    }

    [Fact]
    public void Health_terms_without_an_identifier_are_low() =>
        Assert.Equal(Confidence.Low, Single("Clinic newsletter: our physician team is growing.").Confidence);

    [Theory]
    [InlineData("DOB: 04/12/1987")]
    [InlineData("Date of birth 4-12-87")]
    [InlineData("D.O.B. 04/12/1987")]
    public void Finds_dates_of_birth(string text)
    {
        var f = Single(text);
        Assert.Equal(FindingCategory.DateOfBirth, f.Category);
        Assert.DoesNotContain("04/12", f.Evidence[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Ignores_dates_without_a_birth_keyword() =>
        Assert.Empty(TextScanner.Scan("Meeting on 04/12/1987 about the budget").Findings);

    [Fact]
    public void Caps_input_size_and_reports_truncation()
    {
        Assert.True(TextScanner.Scan(new string('a', TextScanner.MaxScanChars + 1)).Truncated);
        Assert.False(TextScanner.Scan(new string('a', TextScanner.MaxScanChars)).Truncated);
    }

    [Fact]
    public void Text_beyond_the_cap_is_not_scanned()
    {
        var text = new string(' ', TextScanner.MaxScanChars) + "SSN 512-48-3307";
        var result = TextScanner.Scan(text);
        Assert.True(result.Truncated);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void Keeps_at_most_three_evidence_items_but_counts_every_match()
    {
        var text = string.Join("\n", Enumerable.Range(0, 10).Select(i => $"SSN 512-48-{3300 + i}"));
        var f = Single(text);
        Assert.Equal(10, f.Count);
        Assert.Equal(TextScanner.MaxEvidence, f.Evidence.Count);
    }

    [Fact]
    public void Evidence_never_contains_a_neighbouring_number_or_password()
    {
        const string text = "SSN 512-48-3307 card 4111111111111111 password=Hunter2Hunter2 acct 99887766";
        var all = string.Join("|", TextScanner.Scan(text).Findings.SelectMany(f => f.Evidence));
        Assert.DoesNotContain("4111111111111111", all, StringComparison.Ordinal);
        Assert.DoesNotContain("512-48", all, StringComparison.Ordinal);
        Assert.DoesNotContain("Hunter2", all, StringComparison.Ordinal);
        Assert.DoesNotContain("99887766", all, StringComparison.Ordinal);
    }

    [Fact]
    public void Evidence_is_single_line_and_bounded()
    {
        var text = "SSN:\r\n\t512-48-3307\u0000\u0007" + new string('z', 500);
        var evidence = Single(text).Evidence[0];
        Assert.DoesNotContain(evidence, char.IsControl);
        Assert.True(evidence.Length < 100, evidence);
    }
}

/// <summary>
/// Runs on its own, after the rest of this assembly, and counts the processor time this test process used rather than
/// time on the clock, so other test assemblies sharing a busy build machine don't make it fail.
/// </summary>
[Collection(nameof(TextScannerTimingTests))]
public class TextScannerTimingTests
{
    [Fact]
    public void Hostile_input_finishes_quickly()
    {
        // Patterns that make backtracking engines blow up: long digit runs with separators and repeated keywords.
        var text = string.Concat(Enumerable.Repeat("1-2 3-4 5 6 7 8 9 0 password=password=", 25_000));
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var before = process.TotalProcessorTime;
        TextScanner.Scan(text);
        process.Refresh();
        var used = process.TotalProcessorTime - before;
        Assert.True(used < TimeSpan.FromSeconds(10), used.ToString());
    }
}

[CollectionDefinition(nameof(TextScannerTimingTests), DisableParallelization = true)]
public sealed class TextScannerTimingGroup;
