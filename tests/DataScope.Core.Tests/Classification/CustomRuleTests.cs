using System.Diagnostics;
using System.Text;
using DataScope.Core.Classification;

namespace DataScope.Core.Tests.Classification;

// Every value below is made up: documentation IP ranges, example.com addresses and 555 phone numbers.
public class CustomRuleTests
{
    private static CustomRuleDefinition Pattern(string pattern, int id = 1, CustomRuleEffect effect = CustomRuleEffect.Confidential, int minMatches = 1,
        string[]? near = null, bool caseSensitive = false) =>
        new(id, CustomRuleKind.Pattern, pattern, [], caseSensitive, near ?? [], minMatches, effect);

    private static CustomRuleDefinition Words(string[] keywords, int id = 1, CustomRuleEffect effect = CustomRuleEffect.Restricted, int minMatches = 1,
        string[]? near = null, bool caseSensitive = false) =>
        new(id, CustomRuleKind.Keywords, null, keywords, caseSensitive, near ?? [], minMatches, effect);

    private static CustomRuleTemplate Template(string id) => CustomRules.Templates.Single(t => t.Id == id);

    private static CustomRuleDefinition FromTemplate(string id, int ruleId = 1)
    {
        var t = Template(id);
        return new(ruleId, t.Kind, t.Pattern, t.Keywords, false, t.NearWords, t.MinMatches, t.Effect);
    }

    private static Finding? Custom(string text, params CustomRuleDefinition[] rules) =>
        TextScanner.Scan(text, CustomRules.Compile(rules)).Findings.SingleOrDefault(f => f.Category == FindingCategory.CustomRule);

    [Fact]
    public void Every_template_is_a_valid_rule_that_matches_its_own_example()
    {
        Assert.NotEmpty(CustomRules.Templates);
        foreach (var t in CustomRules.Templates)
        {
            var rule = FromTemplate(t.Id);
            Assert.Empty(CustomRules.Problems(rule));
            var f = Custom(t.Example, rule);
            Assert.True(f is not null, $"Template {t.Id} does not match its example.");
            Assert.True(f.Count >= t.MinMatches, $"Template {t.Id} found {f.Count}.");
        }
    }

    [Fact]
    public void Ip_addresses_are_found_with_impossible_ones_left_out_and_each_address_counted_once()
    {
        var f = Custom("Allow 192.0.2.10 and 198.51.100.7, then 192.0.2.10 again. Not 300.1.2.3 or 10.0.0.256.", FromTemplate("ipv4"));
        Assert.NotNull(f);
        Assert.Equal("DS-CUSTOM-1", f.RuleId);
        Assert.Equal(2, f.Count);
        Assert.Equal(Confidence.Medium, f.Confidence);
    }

    [Fact]
    public void A_pattern_match_is_masked_in_evidence_and_other_matches_nearby_are_hidden()
    {
        var f = Custom("hosts: 192.0.2.10, 198.51.100.77 and alpha.example.com", FromTemplate("ipv4"));
        Assert.NotNull(f);
        var all = string.Join("|", f.Evidence);
        Assert.Contains("••••••2.10", all, StringComparison.Ordinal);
        Assert.DoesNotContain("192.0", all, StringComparison.Ordinal);
        Assert.DoesNotContain("198.51", all, StringComparison.Ordinal);
        // An email's letters aren't hidden by the digit redaction, so the context of each match hides the rule's other matches.
        var emails = Custom("Send to alex.doe@example.com or sam.lee@example.com today", Pattern(Template("email-list").Pattern!));
        Assert.NotNull(emails);
        Assert.All(emails.Evidence, e => Assert.DoesNotContain("@", e, StringComparison.Ordinal));
        Assert.DoesNotContain("sam.lee", emails.Evidence[0], StringComparison.Ordinal);
        Assert.DoesNotContain("alex.doe", emails.Evidence[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Short_pattern_matches_are_masked_completely()
    {
        var f = Custom("Code K7Q9 issued", Pattern(@"\bK[0-9][A-Z][0-9]\b"));
        Assert.NotNull(f);
        Assert.Contains("••••", f.Evidence[0], StringComparison.Ordinal);
        Assert.DoesNotContain("K7Q9", f.Evidence[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Keywords_match_whole_words_and_phrases_across_line_breaks_ignoring_case()
    {
        var rule = Words(["Project Falcon", "skunkworks"]);
        var f = Custom("Notes on PROJECT\n  falcon budget. Skunkworks team.", rule);
        Assert.NotNull(f);
        Assert.Equal(2, f.Count);
        Assert.Contains("PROJECT falcon", f.Evidence[0], StringComparison.Ordinal);
        Assert.Null(Custom("Projects Falconry and skunkworkshop are fine.", rule));
    }

    [Fact]
    public void Keywords_that_start_or_end_with_punctuation_still_match()
    {
        var f = Custom("Tagged #redline and C++ build notes.", Words(["#redline", "C++"]));
        Assert.NotNull(f);
        Assert.Equal(2, f.Count);
    }

    [Fact]
    public void Case_sensitive_rules_only_match_the_exact_case()
    {
        var rule = Words(["NOFORN"], caseSensitive: true);
        Assert.NotNull(Custom("Marked NOFORN.", rule));
        Assert.Null(Custom("marked noforn.", rule));
    }

    [Fact]
    public void Nearby_words_limit_matches_to_those_within_80_characters()
    {
        var rule = Pattern(Template("ipv4").Pattern!, near: ["IP", "server"]);
        Assert.NotNull(Custom("The server address is 192.0.2.10.", rule));
        Assert.Null(Custom("Upgrade to version 1.2.3.4 this week.", rule));
        var far = "Server list follows." + new string(' ', 120) + "203.0.113.9";
        Assert.Null(Custom(far, rule));
    }

    [Fact]
    public void A_rule_reports_a_file_only_with_enough_different_values()
    {
        var rule = FromTemplate("email-list");
        var nine = string.Join(", ", Enumerable.Range(1, 9).Select(i => $"person{i}@example.com"));
        Assert.Null(Custom(nine + ", person1@example.com", rule));
        var f = Custom(nine + ", person10@example.com", rule);
        Assert.NotNull(f);
        Assert.Equal(10, f.Count);
        Assert.Equal(3, f.Evidence.Count);
    }

    [Theory]
    [InlineData(CustomRuleEffect.Restricted, Confidence.High, Sensitivity.Restricted)]
    [InlineData(CustomRuleEffect.Confidential, Confidence.Medium, Sensitivity.Confidential)]
    [InlineData(CustomRuleEffect.ListOnly, Confidence.Low, Sensitivity.Internal)]
    public void The_effect_sets_the_confidence_and_so_the_label(CustomRuleEffect effect, Confidence confidence, Sensitivity label)
    {
        var f = Custom("Project Falcon kickoff", Words(["project falcon"], effect: effect));
        Assert.NotNull(f);
        Assert.Equal(confidence, f.Confidence);
        Assert.Equal(label, ClassificationPolicy.Classify([f]));
    }

    [Fact]
    public void Built_in_and_custom_findings_are_both_reported()
    {
        var findings = TextScanner.Scan("Employee SSN: 512-48-3307, project falcon", CustomRules.Compile([Words(["project falcon"])])).Findings;
        Assert.Contains(findings, f => f.RuleId == "DS-SSN-01");
        Assert.Contains(findings, f => f.RuleId == "DS-CUSTOM-1");
        Assert.DoesNotContain(TextScanner.Scan("project falcon").Findings, f => f.Category == FindingCategory.CustomRule);
    }

    [Fact]
    public void Several_rules_report_separately()
    {
        var findings = TextScanner.Scan("Project Falcon on 192.0.2.10",
            CustomRules.Compile([Words(["project falcon"], id: 4), FromTemplate("ipv4", 9)])).Findings;
        Assert.Equal(["DS-CUSTOM-4", "DS-CUSTOM-9"], findings.Select(f => f.RuleId).Order());
    }

    [Fact]
    public void A_value_across_the_boundary_of_two_pieces_is_found_once()
    {
        foreach (var at in new[] { 4090, 4094, 4096, 4100, 8188 })
        {
            var text = new string('x', at) + " 192.0.2.10 " + new string('y', 100);
            var f = Custom(text, FromTemplate("ipv4"));
            Assert.True(f is not null, $"Not found at {at}.");
            Assert.Equal(1, f.Count);
        }
        // A word boundary is honoured at the start of a piece: "x192.0.2.10" is not an address on its own.
        var joined = new string('x', 4095) + "x192.0.2.10";
        Assert.Null(Custom(joined, Pattern(@"\b192\.0\.2\.10\b")));
    }

    [Fact]
    public void Long_matches_are_cut_short()
    {
        var f = Custom("BEGIN" + new string('a', 5000), Pattern("BEGIN[a-z]+", effect: CustomRuleEffect.ListOnly));
        Assert.NotNull(f);
        Assert.True(f.Evidence[0].Length < CustomRules.MaxMatchChars + 100);
    }

    [Theory]
    [InlineData("[a-z]+")]
    [InlineData(@"\p{L}+")]
    [InlineData(".+")]
    [InlineData("(.*a){30}")]
    [InlineData("(a|b)*a(a|b){30}")]
    [InlineData(@"(\w+\s+){40}")]
    public void Patterns_that_are_slow_on_long_runs_finish_quickly_on_a_million_characters(string pattern)
    {
        var rules = CustomRules.Compile([Pattern(pattern, effect: CustomRuleEffect.ListOnly)]);
        Assert.Equal(1, rules.Count);
        var text = new string('a', 500_000) + new StringBuilder().Insert(0, "word ", 100_000);
        var watch = Stopwatch.StartNew();
        _ = TextScanner.Scan(text, rules);
        // A single match of a million characters took over ten seconds before the text was searched in pieces.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Took {watch.Elapsed}.");
    }

    [Theory]
    [InlineData("", "Enter a pattern")]
    [InlineData("[", "isn't a valid pattern")]
    [InlineData("(?=x)abc", "Look-ahead")]
    [InlineData(@"(\w)\1", "back-references")]
    [InlineData("(?>a)b", "atomic")]
    [InlineData("(a{100}){100}", "too complex")]
    [InlineData("a*", "empty text")]
    [InlineData(@"\b", "empty text")]
    [InlineData("x?", "empty text")]
    public void Patterns_that_cant_run_safely_are_refused_with_a_reason(string pattern, string expected)
    {
        var problems = CustomRules.Problems(Pattern(pattern));
        Assert.Contains(expected, Assert.Single(problems["pattern"]), StringComparison.Ordinal);
    }

    [Fact]
    public void An_overlong_pattern_is_refused() =>
        Assert.Contains("500", CustomRules.Problems(Pattern(new string('a', 501)))["pattern"][0], StringComparison.Ordinal);

    [Fact]
    public void Keyword_rules_need_keywords_within_the_limits()
    {
        Assert.Contains("at least one", CustomRules.Problems(Words([" ", ""]))["keywords"][0], StringComparison.Ordinal);
        Assert.Contains("at most 100", CustomRules.Problems(Words([.. Enumerable.Range(0, 101).Select(i => $"word{i}")]))["keywords"][0], StringComparison.Ordinal);
        Assert.Contains("too long", CustomRules.Problems(Words([new string('k', 81)]))["keywords"][0], StringComparison.Ordinal);
        Assert.Contains("only", CustomRules.Problems(Words(["bell\u0007here"]))["keywords"][0], StringComparison.Ordinal);
        Assert.Empty(CustomRules.Problems(Words([.. Enumerable.Range(0, 100).Select(i => $"code name number {i:000}")])));
    }

    [Fact]
    public void Other_settings_are_checked()
    {
        Assert.Contains("minMatches", CustomRules.Problems(Pattern("abc", minMatches: 0)).Keys);
        Assert.Contains("minMatches", CustomRules.Problems(Pattern("abc", minMatches: 1001)).Keys);
        Assert.Contains("effect", CustomRules.Problems(Pattern("abc", effect: (CustomRuleEffect)7)).Keys);
        Assert.Contains("kind", CustomRules.Problems(new CustomRuleDefinition(1, (CustomRuleKind)5, "abc", [], false, [], 1, CustomRuleEffect.ListOnly)).Keys);
        Assert.Contains("nearWords", CustomRules.Problems(Pattern("abc", near: [.. Enumerable.Range(0, 31).Select(i => $"w{i}")])).Keys);
        Assert.Null(CustomRules.NameProblem("IP addresses"));
        Assert.NotNull(CustomRules.NameProblem("  "));
        Assert.NotNull(CustomRules.NameProblem(new string('n', 81)));
    }

    [Fact]
    public void Rules_that_no_longer_compile_are_left_out_of_a_scan_rather_than_failing_it()
    {
        var set = CustomRules.Compile([Pattern("("), Words(["falcon"], id: 2)]);
        Assert.Equal(1, set.Count);
        Assert.NotNull(Custom("falcon", Pattern("(", id: 1), Words(["falcon"], id: 2)));
    }

    [Fact]
    public void The_policy_version_changes_with_what_the_rules_match_but_not_with_their_order()
    {
        Assert.Equal(ClassificationPolicy.CurrentVersion, CustomRules.Compile([]).PolicyVersion);
        Assert.Equal(ClassificationPolicy.CurrentVersion, CustomRuleSet.Empty.PolicyVersion);
        var a = Words(["falcon"], id: 1);
        var b = FromTemplate("ipv4", 2);
        var v = CustomRules.Compile([a, b]).PolicyVersion;
        Assert.StartsWith(ClassificationPolicy.CurrentVersion + "+c", v, StringComparison.Ordinal);
        Assert.True(v.Length <= 64);
        Assert.Equal(v, CustomRules.Compile([b, a]).PolicyVersion);
        Assert.Equal(v, CustomRules.Compile([a with { Keywords = [" falcon "] }, b]).PolicyVersion);
        Assert.NotEqual(v, CustomRules.Compile([a with { Keywords = ["falcon", "osprey"] }, b]).PolicyVersion);
        Assert.NotEqual(v, CustomRules.Compile([a with { Effect = CustomRuleEffect.ListOnly }, b]).PolicyVersion);
        Assert.NotEqual(v, CustomRules.Compile([a with { MinMatches = 2 }, b]).PolicyVersion);
        Assert.NotEqual(v, CustomRules.Compile([a]).PolicyVersion);
    }

    [Fact]
    public void Keywords_are_cleaned_of_blanks_spacing_and_duplicates() =>
        Assert.Equal(["Project Falcon", "osprey"], CustomRules.Clean(["  Project   Falcon ", "", "osprey", "OSPREY", " "]));

    [Fact]
    public void Custom_rule_ids_are_recognised() =>
        Assert.True(CustomRules.IsCustom("DS-CUSTOM-12") && !CustomRules.IsCustom("DS-SSN-01") && CustomRules.RuleId(12) == "DS-CUSTOM-12");
}
