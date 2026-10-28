using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DataScope.Core.Classification;

/// <summary>
/// Runs the detection rules over extracted text. Every regex uses the non-backtracking engine, so the time taken
/// is linear in the text length whatever the file contains. Results are possible matches with a confidence and
/// redacted evidence; no unmasked value ever leaves this class.
/// </summary>
public static partial class TextScanner
{
    /// <summary>Text beyond this many characters is not scanned, and the result says so.</summary>
    public const int MaxScanChars = 1_000_000;

    public const int MaxEvidence = 3;

    private const int ContextChars = 30;
    private const int KeywordWindow = 80;

    private const RegexOptions Fast = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;
    private const RegexOptions FastIgnoreCase = Fast | RegexOptions.IgnoreCase;

    private static readonly Regex SsnFormatted = new(@"\b([0-9]{3})-([0-9]{2})-([0-9]{4})\b", Fast);
    private static readonly Regex SsnSpaced = new(@"\b([0-9]{3}) ([0-9]{2}) ([0-9]{4})\b", Fast);
    private static readonly Regex SsnBare = new(@"\b([0-9]{3})([0-9]{2})([0-9]{4})\b", Fast);
    private static readonly Regex SsnContext = new(
        @"\b(?:(?:ssns?|social security|soc\.? ?sec|ss ?(?:no|num|number)|taxpayer id|tin|itins?|individual taxpayer)\b|ss#)", FastIgnoreCase);

    /// <summary>A whole spreadsheet or export cell holding one SSN-like value: formatted, spaced, or 7-9 digits.</summary>
    private static readonly Regex SsnCell = new(@"^(?:[0-9]{3}-[0-9]{2}-[0-9]{4}|[0-9]{3} [0-9]{2} [0-9]{4}|[0-9]{7,9})$", Fast);

    /// <summary>Header cells longer than this are sentences, not column names.</summary>
    private const int MaxHeaderChars = 40;

    /// <summary>Columns looked at in one row; wider rows are rare and their later columns are left to the keyword rule.</summary>
    private const int MaxColumns = 256;

    private static readonly Regex Card = new(@"\b[0-9](?:[ -]?[0-9]){12,18}\b", Fast);
    private static readonly Regex CardContext = new(@"\b(card|visa|mastercard|amex|discover|cc|credit|exp(iry|iration)?|cvv)\b", FastIgnoreCase);

    private static readonly Regex Routing = new(@"\b(?:routing|aba|rtn)\b[^0-9\n]{0,20}([0-9]{9})\b", FastIgnoreCase);
    private static readonly Regex Account = new(@"\b(?:account|acct)\b[^0-9\n]{0,20}([0-9]{6,17})\b", FastIgnoreCase);

    private static readonly Regex PrivateKey = new(@"-----BEGIN (?:RSA |EC |DSA |OPENSSH |ENCRYPTED )?PRIVATE KEY-----", Fast);
    private static readonly Regex Secret = new(
        @"\b(password|passwd|pwd|secret|api[_-]?key)[ \t]*[:=][ \t]*(""[^""\n]{4,200}""|'[^'\n]{4,200}'|[^\s;,""']{4,200})",
        FastIgnoreCase);
    // Masked values, sample values and template or environment references, which are not real secrets.
    private static readonly Regex Placeholder = new(@"^(\*+|<.*|changeme|xxxx+|\{\{.*|\$\{.*|\$\(.*|%[a-z0-9_]+%)$", FastIgnoreCase);
    private static readonly Regex AwsKey = new(@"\bAKIA[0-9A-Z]{16}\b", Fast);

    private static readonly Regex HealthTerms = new(
        @"\b(diagnos[ie]s|prescription|medication|patient|medical record|mrn|treatment plan|icd-10|hipaa|lab result|physician|clinic(al)?|fmla|disability claim)\b",
        FastIgnoreCase);
    private static readonly Regex Icd10 = new(@"\b[A-TV-Z][0-9]{2}\.[0-9]{1,4}\b", Fast);
    private static readonly Regex PersonContext = new(@"\bpatient name\b|\bemployee\b|\bname:", FastIgnoreCase);

    private static readonly Regex Dob = new(
        @"\b(?:dob|d\.o\.b|date of birth|birth ?date)\b\.?[^0-9\n]{0,8}([0-9]{1,2})[/-]([0-9]{1,2})[/-]([0-9]{2,4})\b",
        FastIgnoreCase);

    private static readonly Regex Whitespace = new(@"\s+", Fast);

    /// <param name="custom">The administrator's own rules (D133), run after the built-in ones; none when null.</param>
    public static TextScanResult Scan(string input, CustomRuleSet? custom = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        var truncated = input.Length > MaxScanChars;
        var text = truncated ? input[..CutPoint(input, MaxScanChars)] : input;
        var hits = new Hits(text);

        DetectSsn(text, hits);
        DetectCards(text, hits);
        DetectBank(text, hits);
        DetectCredentials(text, hits);
        DetectDob(text, hits);
        DetectDriversLicenses(text, hits);
        DetectPassports(text, hits);
        DetectIbans(text, hits);
        DetectPersonalDetails(text, hits);
        var identified = hits.Has(FindingCategory.UsSsn) || hits.Has(FindingCategory.UsItin) || hits.Has(FindingCategory.DateOfBirth)
            || hits.Has(FindingCategory.DriversLicense) || hits.Has(FindingCategory.Passport) || hits.Has(FindingCategory.PersonalDetails)
            || PersonContext.IsMatch(text);
        DetectHealth(text, hits, identified);
        if (custom is not null)
        {
            DetectCustom(text, hits, custom);
        }

        return new TextScanResult(hits.ToList(), truncated);
    }

    /// <summary>The most text <see cref="ScanLong"/> scans: an Outlook mailbox's largest budget (D134).</summary>
    public const int MaxLongScanChars = 50_000_000;

    /// <summary>
    /// Scans text of any length up to <see cref="MaxLongScanChars"/>, such as an Outlook mailbox's messages, in pieces
    /// of at most <see cref="MaxScanChars"/>, each ending at a blank line where there is one so a message is rarely cut
    /// in two. Each rule's findings are merged as one scan merges matches: counts added, the highest confidence kept,
    /// the first evidence kept. Text that fits in one piece is scanned exactly as <see cref="Scan"/> scans it.
    /// </summary>
    public static TextScanResult ScanLong(string input, CustomRuleSet? custom = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length <= MaxScanChars)
        {
            return Scan(input, custom);
        }
        var truncated = input.Length > MaxLongScanChars;
        var end = truncated ? CutPoint(input, MaxLongScanChars) : input.Length;
        var merged = new Dictionary<string, Finding>(StringComparer.Ordinal);
        var order = new List<string>();
        for (var start = 0; start < end;)
        {
            var length = Math.Min(MaxScanChars, end - start);
            if (start + length < end)
            {
                var blank = input.LastIndexOf("\n\n", start + length - 1, length, StringComparison.Ordinal);
                length = blank > start + (MaxScanChars / 2) ? blank - start + 2 : CutPoint(input, start + length) - start;
            }
            foreach (var f in Scan(input.Substring(start, length), custom).Findings)
            {
                if (!merged.TryGetValue(f.RuleId, out var seen))
                {
                    merged[f.RuleId] = f;
                    order.Add(f.RuleId);
                    continue;
                }
                merged[f.RuleId] = seen with
                {
                    Confidence = f.Confidence > seen.Confidence ? f.Confidence : seen.Confidence,
                    Count = seen.Count + f.Count,
                    Evidence = seen.Evidence.Count >= MaxEvidence ? seen.Evidence : [.. seen.Evidence.Concat(f.Evidence).Take(MaxEvidence)],
                };
            }
            start += length;
        }
        return new TextScanResult(order.ConvertAll(id => merged[id]), truncated);
    }

    public static bool IsValidSsn(string area, string group, string serial)
    {
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(serial);
        var a = int.Parse(area, CultureInfo.InvariantCulture);
        if (a == 0 || a == 666 || a >= 900 || group == "00" || serial == "0000")
        {
            return false;
        }
        // Well-known advertising and test numbers are never real people.
        var all = area + group + serial;
        return all is not ("078051120" or "219099999");
    }

    /// <summary>
    /// An IRS Individual Taxpayer Identification Number: 9NN-NN-NNNN with the middle two digits in 50-65, 70-88,
    /// 90-92 or 94-99. Numbers starting with 9 are never Social Security numbers.
    /// </summary>
    public static bool IsValidItin(string area, string group, string serial)
    {
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(serial);
        if (area.Length != 3 || area[0] != '9' || serial == "0000")
        {
            return false;
        }
        var g = int.Parse(group, CultureInfo.InvariantCulture);
        if (g is not ((>= 50 and <= 65) or (>= 70 and <= 88) or (>= 90 and <= 92) or (>= 94 and <= 99)))
        {
            return false;
        }
        // 987-65-4320 to 987-65-4329 are reserved for advertising.
        return !(area + group + serial).StartsWith("98765432", StringComparison.Ordinal);
    }

    public static bool Luhn(string digits)
    {
        ArgumentNullException.ThrowIfNull(digits);
        var sum = 0;
        var doubleIt = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i] - '0';
            if (d is < 0 or > 9)
            {
                return false;
            }
            if (doubleIt)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }
            sum += d;
            doubleIt = !doubleIt;
        }
        return digits.Length > 0 && sum % 10 == 0;
    }

    public static bool IsValidAba(string n)
    {
        ArgumentNullException.ThrowIfNull(n);
        if (n.Length != 9 || !n.All(char.IsAsciiDigit) || n == "000000000")
        {
            return false;
        }
        int D(int i) => n[i] - '0';
        var sum = (3 * (D(0) + D(3) + D(6))) + (7 * (D(1) + D(4) + D(7))) + D(2) + D(5) + D(8);
        return sum % 10 == 0;
    }

    private static void DetectSsn(string text, Hits hits)
    {
        // Each value is counted once, at the highest confidence any rule gives it. SSNs and ITINs share the formats,
        // keywords and columns (ITINs are often kept in an "SSN" column); the first digit tells them apart.
        var found = new SortedDictionary<int, (string Rule, Confidence Confidence, int Length, string Masked)>();
        void Found(string area, string group, string serial, int index, int length, Confidence confidence, bool formatted)
        {
            var rule = IsValidSsn(area, group, serial) ? "DS-SSN-01" : IsValidItin(area, group, serial) ? "DS-ITIN-01" : null;
            if (rule is not null && (!found.TryGetValue(index, out var seen) || confidence > seen.Confidence))
            {
                found[index] = (rule, confidence, length, (formatted ? "***-**-" : "*****") + serial);
            }
        }

        foreach (var pattern in (Regex[])[SsnFormatted, SsnSpaced])
        {
            foreach (Match m in pattern.Matches(text))
            {
                Found(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Index, m.Length,
                    HasNearby(text, m.Index, SsnContext) ? Confidence.High : Confidence.Medium, formatted: true);
            }
        }
        foreach (Match m in SsnBare.Matches(text))
        {
            // A lone 9-digit number is an invoice or an order far more often than an SSN.
            if (HasNearby(text, m.Index, SsnContext))
            {
                Found(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Index, m.Length, Confidence.Medium, formatted: false);
            }
        }
        foreach (var (index, length, cell) in ColumnCells(text, SsnContext, SsnCell))
        {
            var digits = string.Concat(cell.Where(char.IsAsciiDigit)).PadLeft(9, '0');
            Found(digits[..3], digits[3..5], digits[5..], index, length, Confidence.High, formatted: false);
        }

        foreach (var (index, value) in found)
        {
            hits.Add(value.Rule, value.Confidence, index, value.Length, value.Masked);
        }
    }

    /// <summary>
    /// Values in a column whose header matches <paramref name="header"/> (an SSN, a driver's licence, a passport), in
    /// spreadsheets, delimited exports (tab, comma, semicolon or pipe) and one-column lists. Rows far below the header
    /// are out of reach of the keyword window; for SSNs, spreadsheets often drop the dashes and the leading zero, so the
    /// caller pads a 7 or 8 digit cell back to nine digits. A blank line ends a table. Quoted cells that contain the
    /// delimiter shift the columns; those rows fall back to the keyword rule.
    /// </summary>
    private static List<(int Index, int Length, string Cell)> ColumnCells(string text, Regex header, Regex value)
    {
        var found = new List<(int, int, string)>();
        var columns = new HashSet<int>();
        var delimiter = '\0';
        var start = 0;
        while (start < text.Length)
        {
            var end = text.IndexOf('\n', start);
            if (end < 0)
            {
                end = text.Length;
            }
            var lineStart = start;
            var line = text.AsSpan(start, end - start);
            start = end + 1;

            if (line.Trim().IsEmpty)
            {
                columns.Clear();
                continue;
            }
            // A row inside a table keeps the table's delimiter even when a name such as "Doe, Jane" adds commas.
            var lineDelimiter = columns.Count > 0 && (delimiter == '\0' || line.Contains(delimiter)) ? delimiter : Delimiter(line);
            var headers = new HashSet<int>();
            var cells = new List<(int, int, string)>();
            var column = 0;
            var cellStart = 0;
            for (var i = 0; i <= line.Length && column < MaxColumns; i++)
            {
                if (i < line.Length && line[i] != lineDelimiter)
                {
                    continue;
                }
                var (from, to) = (cellStart, i);
                while (from < to && (char.IsWhiteSpace(line[from]) || line[from] == '"'))
                {
                    from++;
                }
                while (to > from && (char.IsWhiteSpace(line[to - 1]) || line[to - 1] == '"'))
                {
                    to--;
                }
                var cell = line[from..to];
                if (cell.Length <= MaxHeaderChars && !value.IsMatch(cell) && header.IsMatch(cell))
                {
                    headers.Add(column);
                }
                else if (lineDelimiter == delimiter && columns.Contains(column) && value.IsMatch(cell))
                {
                    cells.Add((lineStart + from, cell.Length, cell.ToString()));
                }
                column++;
                cellStart = i + 1;
            }
            if (headers.Count > 0)
            {
                columns = headers;
                delimiter = lineDelimiter;
            }
            else
            {
                found.AddRange(cells);
            }
        }
        return found;
    }

    /// <summary>Tab when the line has one, otherwise the most frequent of comma, semicolon and pipe; '\0' for none.</summary>
    private static char Delimiter(ReadOnlySpan<char> line)
    {
        if (line.Contains('\t'))
        {
            return '\t';
        }
        var best = '\0';
        var bestCount = 0;
        foreach (var d in (ReadOnlySpan<char>)[',', ';', '|'])
        {
            var count = line.Count(d);
            if (count > bestCount)
            {
                (best, bestCount) = (d, count);
            }
        }
        return best;
    }

    private static void DetectCards(string text, Hits hits)
    {
        foreach (Match m in Card.Matches(text))
        {
            var digits = string.Concat(m.Value.Where(char.IsAsciiDigit));
            if (!KnownIssuer(digits) || !Luhn(digits) || digits.All(c => c == digits[0]))
            {
                continue;
            }
            var confidence = HasNearby(text, m.Index, CardContext) ? Confidence.High : Confidence.Medium;
            hits.Add("DS-PCI-01", confidence, m, "**** **** **** " + digits[^4..]);
        }
    }

    private static bool KnownIssuer(string d)
    {
        int Prefix(int length) => int.Parse(d.AsSpan(0, length), CultureInfo.InvariantCulture);
        var visa = d[0] == '4' && d.Length is 13 or 16 or 19;
        var mastercard = d.Length == 16 && (Prefix(2) is >= 51 and <= 55 || Prefix(4) is >= 2221 and <= 2720);
        var amex = d.Length == 15 && Prefix(2) is 34 or 37;
        var discover = d.Length >= 16 && (d.StartsWith("6011", StringComparison.Ordinal) || Prefix(2) == 65 || Prefix(3) is >= 644 and <= 649);
        return visa || mastercard || amex || discover;
    }

    private static void DetectBank(string text, Hits hits)
    {
        foreach (Match m in Routing.Matches(text))
        {
            var routing = m.Groups[1].Value;
            if (!IsValidAba(routing))
            {
                continue;
            }
            var near = text.Substring(m.Index, Math.Min(160, text.Length - m.Index));
            var account = Account.Match(near);
            var masked = "routing *****" + routing[^4..] + (account.Success ? ", account ****" + account.Groups[1].Value[^4..] : "");
            hits.Add("DS-BANK-01", account.Success ? Confidence.High : Confidence.Low, m, masked);
        }
    }

    private static void DetectCredentials(string text, Hits hits)
    {
        var keys = PrivateKey.Count(text);
        for (var i = 0; i < keys; i++)
        {
            hits.Add("DS-CRED-01", Confidence.High, "-----BEGIN PRIVATE KEY----- [key body redacted]");
        }
        foreach (Match m in Secret.Matches(text))
        {
            var value = m.Groups[2].Value.Replace("\"", "", StringComparison.Ordinal).Replace("'", "", StringComparison.Ordinal);
            if (Placeholder.IsMatch(value))
            {
                continue;
            }
            hits.Add("DS-CRED-02", Confidence.Medium, m, m.Groups[1].Value + "=••••••");
        }
        foreach (Match m in AwsKey.Matches(text))
        {
            hits.Add("DS-CRED-02", Confidence.High, m, "AKIA************" + m.Value[^4..]);
        }
    }

    private static void DetectHealth(string text, Hits hits, bool hasIdentifier)
    {
        var terms = new List<string>();
        foreach (Match m in HealthTerms.Matches(text))
        {
            var term = m.Value.ToLowerInvariant();
            if (!terms.Contains(term))
            {
                terms.Add(term);
            }
        }
        var codes = Icd10.Count(text);
        var signal = terms.Count + Math.Min(codes, 2);
        if (signal < 2)
        {
            return;
        }
        var confidence = hasIdentifier && signal >= 3 ? Confidence.High
            : hasIdentifier || signal >= 3 ? Confidence.Medium
            : Confidence.Low;
        var sample = "Terms: " + string.Join(", ", terms.Take(4)) + (codes > 0 ? $"; {codes} ICD-10 code(s)" : "");
        hits.Add("DS-PHI-01", confidence, sample);
    }

    private static void DetectDob(string text, Hits hits)
    {
        foreach (Match m in Dob.Matches(text))
        {
            hits.Add("DS-DOB-01", Confidence.Medium, m, "DOB **/**/" + m.Groups[3].Value[^2..]);
        }
    }

    private static bool HasNearby(string text, int index, Regex words)
    {
        var start = Math.Max(0, index - KeywordWindow);
        var end = Math.Min(text.Length, index + KeywordWindow);
        return words.IsMatch(text.AsSpan(start, end - start));
    }

    /// <summary>Never split a surrogate pair when truncating.</summary>
    private static int CutPoint(string text, int length) =>
        char.IsHighSurrogate(text[length - 1]) ? length - 1 : length;

    private static readonly Dictionary<string, DetectionRule> RulesById =
        ClassificationPolicy.Rules.ToDictionary(r => r.Id, StringComparer.Ordinal);

    private sealed class Hits(string text)
    {
        private readonly List<Builder> order = [];
        private readonly Dictionary<string, Builder> byRule = new(StringComparer.Ordinal);
        private string? redacted;

        public bool Has(FindingCategory category) => order.Exists(b => b.Category == category);

        public void Add(string ruleId, Confidence confidence, Match match, string masked) =>
            Add(ruleId, confidence, match.Index, match.Length, masked);

        public void Add(string ruleId, Confidence confidence, int index, int length, string masked) =>
            Add(ruleId, confidence, () => Snippet(index, length, masked));

        /// <summary>One match, with other matched values near it hidden in its context.</summary>
        public void Add(string ruleId, Confidence confidence, int index, int length, string masked, IReadOnlyList<(int Start, int End)> hidden) =>
            Add(ruleId, confidence, () => Snippet(index, length, masked, hidden));

        public void Add(string ruleId, Confidence confidence, string evidence) => Add(ruleId, confidence, () => evidence);

        private void Add(string ruleId, Confidence confidence, Func<string> evidence)
        {
            if (!byRule.TryGetValue(ruleId, out var b))
            {
                b = new Builder(ruleId, RulesById[ruleId].Category, confidence);
                byRule.Add(ruleId, b);
                order.Add(b);
            }
            else
            {
                b.Count++;
                if (confidence > b.Confidence)
                {
                    b.Confidence = confidence;
                }
            }
            if (b.Evidence.Count < MaxEvidence)
            {
                b.Evidence.Add(evidence());
            }
        }

        /// <summary>One custom rule's result, counted by the caller. Other values the rule matched are hidden in the context.</summary>
        public void AddCounted(string ruleId, Confidence confidence, int count, IEnumerable<(int Index, int Length, string Masked)> evidence,
            IReadOnlyList<(int Start, int End)> hidden)
        {
            var b = new Builder(ruleId, FindingCategory.CustomRule, confidence) { Count = count };
            foreach (var (index, length, masked) in evidence.Take(MaxEvidence))
            {
                b.Evidence.Add(Snippet(index, length, masked, hidden));
            }
            byRule.Add(ruleId, b);
            order.Add(b);
        }

        public List<Finding> ToList() =>
            order.ConvertAll(b => new Finding(b.RuleId, b.Category, b.Confidence, b.Count, b.Evidence.AsReadOnly()));

        /// <summary>
        /// The masked value with up to 30 characters either side. The surrounding text comes from a redacted copy
        /// of the input, so a second number or a password next to the match is never stored.
        /// </summary>
        private string Snippet(int index, int length, string masked, IReadOnlyList<(int Start, int End)>? hidden = null)
        {
            redacted ??= Redact(text);
            var start = Math.Max(0, index - ContextChars);
            var end = Math.Min(text.Length, index + length + ContextChars);
            var window = redacted.ToCharArray(start, end - start);
            foreach (var (from, to) in hidden ?? [])
            {
                var a = Math.Max(from, start);
                var z = Math.Min(to, end);
                if (a < z)
                {
                    Array.Fill(window, '•', a - start, z - a);
                }
            }
            var before = Clean(new string(window, 0, index - start));
            var after = Clean(new string(window, index + length - start, end - index - length));
            return (start > 0 ? "…" : "") + before + masked + after + (end < text.Length ? "…" : "");
        }

        private static string Clean(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                sb.Append(char.IsControl(c) || char.IsSurrogate(c) ? ' ' : c);
            }
            return Whitespace.Replace(sb.ToString(), " ");
        }
    }

    /// <summary>Same-length copy with every digit replaced by '#' and every secret value replaced by '•'.</summary>
    public static string Redact(string text)
    {
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsDigit(chars[i]))
            {
                chars[i] = '#';
            }
        }
        foreach (Match m in Secret.Matches(text))
        {
            var g = m.Groups[2];
            Array.Fill(chars, '•', g.Index, g.Length);
        }
        foreach (Match m in AwsKey.Matches(text))
        {
            Array.Fill(chars, '•', m.Index, m.Length);
        }
        return new string(chars);
    }

    private sealed class Builder(string ruleId, FindingCategory category, Confidence confidence)
    {
        public string RuleId { get; } = ruleId;
        public FindingCategory Category { get; } = category;
        public Confidence Confidence { get; set; } = confidence;
        public int Count { get; set; } = 1;
        public List<string> Evidence { get; } = [];
    }
}
