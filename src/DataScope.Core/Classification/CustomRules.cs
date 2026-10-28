using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DataScope.Core.Classification;

/// <summary>How a custom rule finds text: a list of words and phrases, or a pattern (a .NET regular expression).</summary>
public enum CustomRuleKind
{
    Keywords,
    Pattern,
}

/// <summary>What a file with a match becomes (D133). <see cref="ListOnly"/> lists the match without changing the label.</summary>
public enum CustomRuleEffect
{
    ListOnly,
    Confidential,
    Restricted,
}

/// <summary>
/// An administrator's own detection rule (D133). Only what changes matching is here; the name and the reason live
/// with the stored rule. <see cref="Keywords"/> is used for <see cref="CustomRuleKind.Keywords"/> rules and
/// <see cref="Pattern"/> for <see cref="CustomRuleKind.Pattern"/> rules.
/// </summary>
/// <param name="NearWords">When set, a match counts only with one of these words or phrases within 80 characters.</param>
/// <param name="MinMatches">How many different matching values a file needs before the rule reports it.</param>
public sealed record CustomRuleDefinition(
    int Id,
    CustomRuleKind Kind,
    string? Pattern,
    IReadOnlyList<string> Keywords,
    bool CaseSensitive,
    IReadOnlyList<string> NearWords,
    int MinMatches,
    CustomRuleEffect Effect)
{
    public string RuleId => CustomRules.RuleId(Id);
}

/// <summary>A starting point for a rule, offered when an administrator adds one. Every template passes <see cref="CustomRules.Problems"/>.</summary>
/// <param name="Example">Made-up text that the template matches, for the test box.</param>
public sealed record CustomRuleTemplate(
    string Id,
    string Name,
    string Description,
    CustomRuleKind Kind,
    string? Pattern,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<string> NearWords,
    int MinMatches,
    CustomRuleEffect Effect,
    string Example);

/// <summary>
/// Administrators' own detection rules (D133): checking them, compiling them into a <see cref="CustomRuleSet"/>, and
/// the policy version they give. Every rule runs on .NET's non-backtracking engine over bounded pieces of text, so no
/// rule an administrator can write takes more than linear time in the file's length.
/// </summary>
public static class CustomRules
{
    public const string RulePrefix = "DS-CUSTOM-";
    public const int MaxRules = 100;
    public const int MaxEnabled = 25;
    public const int MaxNameChars = 80;
    public const int MaxDescriptionChars = 300;
    public const int MaxPatternChars = 500;
    public const int MaxKeywords = 100;
    public const int MaxKeywordChars = 80;
    public const int MaxKeywordTotalChars = 3_000;
    public const int MaxNearWords = 30;
    public const int MaxMinMatches = 1_000;

    /// <summary>Text pasted into the test box is cut to this length.</summary>
    public const int MaxSampleChars = 20_000;

    /// <summary>A match longer than this is cut short: values worth finding are short, and long matches are slow (D133).</summary>
    public const int MaxMatchChars = 256;

    /// <summary>How far from a match a nearby word may be, in characters, as for the built-in keyword rules.</summary>
    public const int NearWindow = 80;

    internal const RegexOptions Options = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;

    public static string RuleId(int id) => RulePrefix + id.ToString(CultureInfo.InvariantCulture);

    public static bool IsCustom(string ruleId) => ruleId.StartsWith(RulePrefix, StringComparison.Ordinal);

    public static Confidence ConfidenceOf(CustomRuleEffect effect) => effect switch
    {
        CustomRuleEffect.Restricted => Confidence.High,
        CustomRuleEffect.Confidential => Confidence.Medium,
        _ => Confidence.Low,
    };

    /// <summary>Ready-made rules to start from. The examples are made up.</summary>
    public static IReadOnlyList<CustomRuleTemplate> Templates { get; } =
    [
        new("ipv4", "IP addresses",
            "Four numbers from 0 to 255 separated by dots, such as 10.20.30.40. Version numbers such as 1.2.3.4 match too; add nearby words such as \"IP\" or \"server\" to leave those out.",
            CustomRuleKind.Pattern,
            @"\b(?:25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])(?:\.(?:25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])){3}\b",
            [], [], 1, CustomRuleEffect.Confidential,
            "Firewall change: allow 10.20.30.40 and 192.168.1.15 to reach the payroll server."),
        new("email-list", "Lists of email addresses",
            "Ten or more different email addresses in one file, such as a contact list or an export. A single address in a letter does not count.",
            CustomRuleKind.Pattern,
            @"\b[a-z0-9._%+-]{1,64}@[a-z0-9-]{1,63}(?:\.[a-z0-9-]{1,63})*\.[a-z]{2,24}\b",
            [], [], 10, CustomRuleEffect.Confidential,
            "alex.doe@example.com, sam.lee@example.com, kim.ray@example.org, pat.fox@example.net, lee.cho@example.com, " +
            "jo.ann@example.com, max.wu@example.org, ana.li@example.net, tom.ng@example.com, eve.ko@example.org"),
        new("phone-list", "Lists of US phone numbers",
            "Ten or more different US phone numbers written with separators, such as (555) 201-3344 or 555-201-3344.",
            CustomRuleKind.Pattern,
            @"\(?\b[2-9][0-9]{2}\)?[ .-]?[2-9][0-9]{2}[ .-][0-9]{4}\b",
            [], [], 10, CustomRuleEffect.Confidential,
            "555-201-3344, 555-201-3345, 555-201-3346, 555-201-3347, 555-201-3348, 555-201-3349, (555) 301-1100, (555) 301-1101, " +
            "(555) 301-1102, (555) 301-1103"),
        new("mac", "Network card (MAC) addresses",
            "Six pairs of hexadecimal digits separated by colons or dashes, such as 00:1A:2B:3C:4D:5E.",
            CustomRuleKind.Pattern,
            @"\b[0-9a-f]{2}(?:[:-][0-9a-f]{2}){5}\b",
            [], [], 1, CustomRuleEffect.ListOnly,
            "Badge reader 3 has MAC 00:1A:2B:3C:4D:5E on VLAN 20."),
        new("markings", "Confidentiality markings",
            "Words people put on documents that must not be shared. Add your organisation's own markings.",
            CustomRuleKind.Keywords, null,
            ["attorney-client privileged", "privileged and confidential", "confidential - do not distribute", "do not forward", "internal use only"],
            [], 1, CustomRuleEffect.Confidential,
            "PRIVILEGED AND CONFIDENTIAL. Draft settlement terms, do not forward."),
        new("employee-id", "Employee ID (example format)",
            "An example of a number format of your own: EMP followed by six digits, such as EMP-204518. Change the pattern to your organisation's format.",
            CustomRuleKind.Pattern, @"\bEMP-?[0-9]{6}\b",
            [], [], 1, CustomRuleEffect.Confidential,
            "Leave request for EMP-204518, approved by EMP-100342."),
    ];

    /// <summary>What is wrong with a rule, by field, in words an administrator can act on. Empty when it can be used.</summary>
    public static Dictionary<string, string[]> Problems(CustomRuleDefinition rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var errors = new Dictionary<string, string[]>();
        if (!Enum.IsDefined(rule.Kind))
        {
            errors["kind"] = ["Use Keywords or Pattern."];
        }
        if (!Enum.IsDefined(rule.Effect))
        {
            errors["effect"] = ["Use Restricted, Confidential or ListOnly."];
        }
        if (rule.MinMatches is < 1 or > MaxMinMatches)
        {
            errors["minMatches"] = [$"Use a number from 1 to {MaxMinMatches:N0}."];
        }
        if (rule.Kind == CustomRuleKind.Pattern)
        {
            if (PatternProblem(rule.Pattern, rule.CaseSensitive) is { } problem)
            {
                errors["pattern"] = [problem];
            }
        }
        else if (rule.Kind == CustomRuleKind.Keywords && WordsProblem(rule.Keywords, MaxKeywords, "keyword", rule.CaseSensitive, required: true) is { } problem)
        {
            errors["keywords"] = [problem];
        }
        if (WordsProblem(rule.NearWords, MaxNearWords, "nearby word", rule.CaseSensitive, required: false) is { } nearProblem)
        {
            errors["nearWords"] = [nearProblem];
        }
        return errors;
    }

    public const int MinReasonLength = 3;
    public const int MaxReasonLength = 500;

    /// <summary>Why a reason can't be used, or null. Every change says why, for the audit log.</summary>
    public static string? ReasonProblem(string? reason)
    {
        var text = reason?.Trim() ?? "";
        if (text.Length < MinReasonLength)
        {
            return "Say why, so others know later. For example: our server addresses are confidential.";
        }
        return text.Length > MaxReasonLength || text.Any(c => char.IsControl(c) && c is not '\n' and not '\r')
            ? $"Use at most {MaxReasonLength} characters of plain text."
            : null;
    }

    /// <summary>Why a name can't be used, or null.</summary>
    public static string? NameProblem(string? name)
    {
        var n = name?.Trim() ?? "";
        return n.Length == 0 ? "Give the rule a name people will recognise in results."
            : n.Length > MaxNameChars ? $"Use at most {MaxNameChars} characters."
            : n.Any(char.IsControl) ? "Use letters, digits, spaces and punctuation only."
            : null;
    }

    /// <summary>Compiles enabled rules for a scan. Rules that no longer compile are left out rather than stopping the scan.</summary>
    public static CustomRuleSet Compile(IEnumerable<CustomRuleDefinition> enabled)
    {
        ArgumentNullException.ThrowIfNull(enabled);
        var rules = enabled.OrderBy(r => r.Id).ToList();
        var compiled = new List<CompiledCustomRule>(rules.Count);
        foreach (var rule in rules)
        {
            if (Problems(rule).Count > 0)
            {
                continue;
            }
            compiled.Add(new CompiledCustomRule(
                rule,
                rule.Kind == CustomRuleKind.Pattern ? new Regex(rule.Pattern!, OptionsFor(rule.CaseSensitive)) : WordsRegex(rule.Keywords, rule.CaseSensitive)!,
                WordsRegex(rule.NearWords, rule.CaseSensitive)));
        }
        return new CustomRuleSet(compiled, compiled.Count == 0 ? ClassificationPolicy.CurrentVersion : $"{ClassificationPolicy.CurrentVersion}+c{Fingerprint(compiled.Select(c => c.Definition))}");
    }

    /// <summary>
    /// Eight hex digits that change whenever what the enabled rules match or do changes (not their names), so files are
    /// read again with the new rules on the next scan.
    /// </summary>
    public static string Fingerprint(IEnumerable<CustomRuleDefinition> enabled)
    {
        var canonical = JsonSerializer.Serialize(enabled.OrderBy(r => r.Id).Select(r => new object?[]
        {
            r.Id, r.Kind.ToString(), r.Kind == CustomRuleKind.Pattern ? r.Pattern : null, r.Kind == CustomRuleKind.Keywords ? Clean(r.Keywords) : null,
            r.CaseSensitive, Clean(r.NearWords), r.MinMatches, r.Effect.ToString(),
        }));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..8];
    }

    /// <summary>Keywords as stored: trimmed, blanks dropped, duplicates (ignoring case) removed, order kept.</summary>
    public static List<string> Clean(IEnumerable<string>? words) =>
        (words ?? []).Select(w => Regex.Replace(w ?? "", @"\s+", " ").Trim()).Where(w => w.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static RegexOptions OptionsFor(bool caseSensitive) => caseSensitive ? Options : Options | RegexOptions.IgnoreCase;

    private static string? PatternProblem(string? pattern, bool caseSensitive)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return "Enter a pattern, or start from one of the examples.";
        }
        if (pattern.Length > MaxPatternChars)
        {
            return $"Use at most {MaxPatternChars} characters.";
        }
        Regex regex;
        try
        {
            regex = new Regex(pattern, OptionsFor(caseSensitive));
        }
        catch (RegexParseException ex)
        {
            return $"This isn't a valid pattern: {ParseMessage(ex)}";
        }
        catch (NotSupportedException ex) when (ex.Message.Contains("automata", StringComparison.OrdinalIgnoreCase))
        {
            return "This pattern is too complex to search for safely. Use smaller repeat counts, such as {1,20} instead of {1,1000}.";
        }
        catch (NotSupportedException)
        {
            return "Look-ahead, look-behind, back-references, atomic groups and conditionals aren't supported, so that every pattern runs in time proportional to the file's length.";
        }
        if (regex.IsMatch("") || MatchesNothing(regex))
        {
            return "This pattern can match empty text, so it would match everywhere. Make sure it needs at least one character, for example + instead of *.";
        }
        return null;
    }

    /// <summary>A pattern such as \b matches between characters without matching any of them.</summary>
    private static bool MatchesNothing(Regex regex)
    {
        foreach (var m in regex.EnumerateMatches("a 1 _-.\n"))
        {
            if (m.Length == 0)
            {
                return true;
            }
        }
        return false;
    }

    private static string ParseMessage(RegexParseException ex)
    {
        // "Invalid pattern '[' at offset 1. Unterminated [] set." becomes "Unterminated [] set (at character 1)."
        var message = ex.Message;
        var cut = message.IndexOf(". ", message.IndexOf(" at offset ", StringComparison.Ordinal) is var at and >= 0 ? at : 0, StringComparison.Ordinal);
        var what = cut >= 0 ? message[(cut + 2)..].TrimEnd('.') : ex.Error.ToString();
        return $"{what} (at character {ex.Offset.ToString(CultureInfo.InvariantCulture)}).";
    }

    private static string? WordsProblem(IReadOnlyList<string>? words, int max, string what, bool caseSensitive, bool required)
    {
        var clean = Clean(words);
        if (clean.Count == 0)
        {
            return required ? $"Add at least one {what}." : null;
        }
        if (clean.Count > max)
        {
            return $"Use at most {max} {what}s; split the rest into another rule.";
        }
        if (clean.FirstOrDefault(w => w.Length > MaxKeywordChars) is { } longWord)
        {
            return $"\"{longWord[..20]}…\" is too long; use at most {MaxKeywordChars} characters per {what}.";
        }
        if (clean.FirstOrDefault(w => w.Any(char.IsControl)) is not null)
        {
            return $"Use letters, digits, spaces and punctuation only in each {what}.";
        }
        if (clean.Sum(w => w.Length) > MaxKeywordTotalChars)
        {
            return $"Use at most {MaxKeywordTotalChars:N0} characters of {what}s in one rule; split the rest into another rule.";
        }
        try
        {
            _ = WordsRegex(clean, caseSensitive);
        }
        catch (NotSupportedException)
        {
            return $"These {what}s are too many or too long to search for together; split them into two rules.";
        }
        return null;
    }

    /// <summary>
    /// One pattern for a list of words and phrases: any spacing or line break between the words of a phrase, and a
    /// whole-word match wherever a phrase starts or ends with a letter or digit ("pay" doesn't match "payroll").
    /// </summary>
    private static Regex? WordsRegex(IReadOnlyList<string> words, bool caseSensitive)
    {
        var clean = Clean(words);
        if (clean.Count == 0)
        {
            return null;
        }
        var parts = clean.Select(w =>
            (char.IsLetterOrDigit(w[0]) || w[0] == '_' ? @"\b" : "")
            + string.Join(@"\s+", w.Split(' ').Select(Regex.Escape))
            + (char.IsLetterOrDigit(w[^1]) || w[^1] == '_' ? @"\b" : ""));
        return new Regex("(?:" + string.Join("|", parts) + ")", OptionsFor(caseSensitive));
    }
}

/// <summary>A rule ready to run: its definition, what it matches, and the words a match must be near (if any).</summary>
internal sealed record CompiledCustomRule(CustomRuleDefinition Definition, Regex Match, Regex? Near);

/// <summary>
/// The enabled custom rules, compiled once per scan, and the policy version stamped on files classified with them:
/// <see cref="ClassificationPolicy.CurrentVersion"/> when there are none, otherwise that version plus "+c" and the
/// rules' fingerprint (D133).
/// </summary>
public sealed class CustomRuleSet
{
    internal CustomRuleSet(IReadOnlyList<CompiledCustomRule> rules, string policyVersion)
    {
        Rules = rules;
        PolicyVersion = policyVersion;
    }

    public static CustomRuleSet Empty { get; } = new([], ClassificationPolicy.CurrentVersion);

    internal IReadOnlyList<CompiledCustomRule> Rules { get; }

    public int Count => Rules.Count;

    public string PolicyVersion { get; }
}
