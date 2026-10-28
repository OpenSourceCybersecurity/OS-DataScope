using System.Text.RegularExpressions;

namespace DataScope.Core.Classification;

// Driver's licence and passport numbers, IBANs, and names next to an address or a date of birth (D137). Licence and
// passport numbers have no checksum and many formats, so they are only counted next to a label or in a column whose
// header names them; IBANs carry a checksum and a fixed length per country, so they count on their own.
public static partial class TextScanner
{
    private const string IdLabel = @"(?:[ \t]*(?:number|no\.?|num|#|id))?[ \t]*[:#=.-]?[ \t]*";

    /// <summary>A licence number right after its label: letters, then digits, then digit groups ("D1234567", "123 456 789", "A123-456-78-901-0").</summary>
    private static readonly Regex DlLabelled = new(
        @"(?:\bdriver[’']?s?[’']? ?licen[cs]e\b|\bdrivers? lic\b\.?|\bdriving licen[cs]e\b|\boperator[’']?s? licen[cs]e\b|\bdln\b|\bdl ?(?:#|:|no\b|num\b|number\b))"
        + IdLabel + @"([A-Z]{0,5}[0-9][0-9A-Z]*(?:[ -][0-9][0-9A-Z]*){0,4})\b",
        FastIgnoreCase);

    private static readonly Regex DlHeader = new(
        @"^(?:driver[’']?s?[’']? ?licen[cs]e(?: ?(?:no\.?|number|num|#|id))?|driving licen[cs]e(?: ?(?:no\.?|number|#))?|dl(?: ?(?:no\.?|number|num|#))?|dln|licen[cs]e ?(?:no\.?|number|#))$",
        FastIgnoreCase);

    private static readonly Regex DlCell = new(@"^[A-Z]{0,5}[0-9][0-9A-Z]*(?:[ -][0-9][0-9A-Z]*){0,4}$", FastIgnoreCase);

    /// <summary>US passports are 9 digits or a letter and 8 digits; many others are 1 or 2 letters and 6 to 9 digits.</summary>
    private static readonly Regex PassportLabelled = new(@"\bpassport\b" + IdLabel + @"([A-Z]{0,2}[0-9]{6,9})\b", FastIgnoreCase);

    private static readonly Regex PassportKeyword = new(@"\bpassport\b", FastIgnoreCase);
    private static readonly Regex PassportCell = new(@"^[A-Z]{0,2}[0-9]{6,9}$", FastIgnoreCase);

    /// <summary>A country code, two check digits, then 11 to 30 letters and digits, optionally in groups of four.</summary>
    private static readonly Regex Iban = new(@"\b[A-Z]{2}[0-9]{2}(?: ?[A-Z0-9]){11,30}\b", Fast);
    private static readonly Regex IbanContext = new(@"\b(?:iban|bic|swift|bank|account|acct|transfer|payee|beneficiary)\b", FastIgnoreCase);

    /// <summary>The IBAN length for each country in the SWIFT IBAN registry.</summary>
    private static readonly Dictionary<string, int> IbanLengths = new(StringComparer.Ordinal)
    {
        ["AD"] = 24,
        ["AE"] = 23,
        ["AL"] = 28,
        ["AT"] = 20,
        ["AZ"] = 28,
        ["BA"] = 20,
        ["BE"] = 16,
        ["BG"] = 22,
        ["BH"] = 22,
        ["BR"] = 29,
        ["BY"] = 28,
        ["CH"] = 21,
        ["CR"] = 22,
        ["CY"] = 28,
        ["CZ"] = 24,
        ["DE"] = 22,
        ["DK"] = 18,
        ["DO"] = 28,
        ["EE"] = 20,
        ["EG"] = 29,
        ["ES"] = 24,
        ["FI"] = 18,
        ["FO"] = 18,
        ["FR"] = 27,
        ["GB"] = 22,
        ["GE"] = 22,
        ["GI"] = 23,
        ["GL"] = 18,
        ["GR"] = 27,
        ["GT"] = 28,
        ["HR"] = 21,
        ["HU"] = 28,
        ["IE"] = 22,
        ["IL"] = 23,
        ["IQ"] = 23,
        ["IS"] = 26,
        ["IT"] = 27,
        ["JO"] = 30,
        ["KW"] = 30,
        ["KZ"] = 20,
        ["LB"] = 28,
        ["LC"] = 32,
        ["LI"] = 21,
        ["LT"] = 20,
        ["LU"] = 20,
        ["LV"] = 21,
        ["MC"] = 27,
        ["MD"] = 24,
        ["ME"] = 22,
        ["MK"] = 19,
        ["MR"] = 27,
        ["MT"] = 31,
        ["MU"] = 30,
        ["NL"] = 18,
        ["NO"] = 15,
        ["PK"] = 24,
        ["PL"] = 28,
        ["PS"] = 29,
        ["PT"] = 25,
        ["QA"] = 29,
        ["RO"] = 24,
        ["RS"] = 22,
        ["SA"] = 24,
        ["SC"] = 31,
        ["SE"] = 24,
        ["SI"] = 19,
        ["SK"] = 24,
        ["SM"] = 27,
        ["ST"] = 25,
        ["SV"] = 28,
        ["TL"] = 23,
        ["TN"] = 24,
        ["TR"] = 26,
        ["UA"] = 29,
        ["VA"] = 22,
        ["VG"] = 24,
        ["XK"] = 20,
    };

    /// <summary>A labelled personal name: "Name: Casey Byrne", "Patient name - Dana Shah".</summary>
    private static readonly Regex NameLabelled = new(
        @"\b(?i:full name|(?:patient|employee|customer|client|member|resident|student|applicant|tenant|account holder)(?: name)?|name)\b[ \t]*[:=-][ \t]*([A-Z][a-z'’-]+(?: (?:[A-Z]\.|[A-Z][a-z'’-]+)){1,3})",
        Fast);

    /// <summary>
    /// A two or three word name alone at the start of a line just before an address, as on a mailing label or an
    /// envelope ("Casey Byrne" then "12 Oak Street" on the next line or after a comma).
    /// </summary>
    private static readonly Regex NameAtLineStart = new(@"(?:^|\n)[ \t]*([A-Z][a-z'’-]+(?: [A-Z]\.)?(?: [A-Z][a-z'’-]+){1,2})[ \t]*(?:,[ \t]*|\r?\n[ \t]*)$", Fast);

    /// <summary>A US street address ("12 Oak Street", "4021 N Elm Ave.") or a PO box.</summary>
    private static readonly Regex StreetAddress = new(
        @"\b[0-9]{1,6}(?: [A-Z][A-Za-z.'’-]*){1,4} (?:St|Street|Ave|Avenue|Rd|Road|Blvd|Boulevard|Dr|Drive|Ln|Lane|Ct|Court|Way|Pl|Place|Pkwy|Parkway|Hwy|Highway|Cir|Circle|Ter|Terrace|Trl|Trail|Sq|Square)\b\.?|\bP\.? ?O\.? Box [0-9]{1,6}\b",
        Fast);

    /// <summary>A two-letter state and a ZIP code, which make an address complete.</summary>
    private static readonly Regex StateZip = new(@"\b[A-Z]{2},? [0-9]{5}(?:-[0-9]{4})?\b", Fast);

    private static readonly Regex BornOn = new(@"\b(?i:born(?: on)?)[ \t]+[0-9]{1,2}[/-][0-9]{1,2}[/-][0-9]{2,4}\b", Fast);

    private static readonly Regex NameHeader = new(
        @"^(?:(?:full|first|last|given|sur|family|employee|customer|client|patient|member|tenant|student) ?name|name|employee|customer|client|patient|surname)$",
        FastIgnoreCase);
    private static readonly Regex AddressHeader = new(@"^(?:(?:home|street|mailing|postal|residential) )?(?:address(?: ?1| line 1)?|street)$", FastIgnoreCase);
    private static readonly Regex DobHeader = new(@"^(?:dob|d\.o\.b\.?|date of birth|birth ?date|birthday)$", FastIgnoreCase);
    private static readonly Regex DateCell = new(@"^[0-9]{1,4}[/.-][0-9]{1,2}[/.-][0-9]{1,4}$", Fast);

    /// <summary>Characters after a name label that are searched for the address or date of birth that goes with it.</summary>
    private const int RecordWindow = 200;

    /// <summary>A file listing at least this many people with their addresses or dates of birth is reported at High.</summary>
    public const int ManyPeople = 5;

    private static void DetectDriversLicenses(string text, Hits hits)
    {
        var found = new SortedDictionary<int, (Confidence Confidence, int Length, string Value)>();
        foreach (Match m in DlLabelled.Matches(text))
        {
            var g = m.Groups[1];
            if (LooksLikeId(g.Value, 5, 16))
            {
                found[g.Index] = (Confidence.Medium, g.Length, g.Value);
            }
        }
        foreach (var (index, length, value) in ColumnCells(text, DlHeader, DlCell))
        {
            if (LooksLikeId(value, 5, 16))
            {
                found[index] = (Confidence.High, length, value);
            }
        }
        foreach (var (index, value) in found)
        {
            hits.Add("DS-DL-01", value.Confidence, index, value.Length, "DL ****" + value.Value[^2..]);
        }
    }

    private static void DetectPassports(string text, Hits hits)
    {
        var found = new SortedDictionary<int, (Confidence Confidence, int Length, string Value)>();
        foreach (Match m in PassportLabelled.Matches(text))
        {
            var g = m.Groups[1];
            if (LooksLikeId(g.Value, 6, 9))
            {
                found[g.Index] = (Confidence.Medium, g.Length, g.Value);
            }
        }
        foreach (var (index, length, value) in ColumnCells(text, PassportKeyword, PassportCell))
        {
            if (LooksLikeId(value, 6, 9))
            {
                found[index] = (Confidence.High, length, value);
            }
        }
        foreach (var (index, value) in found)
        {
            hits.Add("DS-PASS-01", value.Confidence, index, value.Length, "Passport ****" + value.Value[^2..]);
        }
    }

    /// <summary>Between <paramref name="min"/> and <paramref name="max"/> digits, and not one digit repeated or a run like 123456.</summary>
    private static bool LooksLikeId(string value, int min, int max)
    {
        var digits = string.Concat(value.Where(char.IsAsciiDigit));
        if (digits.Length < min || digits.Length > max || digits.All(c => c == digits[0]))
        {
            return false;
        }
        return !"01234567890123456789".Contains(digits, StringComparison.Ordinal) && !"98765432109876543210".Contains(digits, StringComparison.Ordinal);
    }

    private static void DetectIbans(string text, Hits hits)
    {
        foreach (Match m in Iban.Matches(text))
        {
            var country = m.Value[..2];
            if (!IbanLengths.TryGetValue(country, out var length))
            {
                continue;
            }
            // The match may run on into the next word ("... 00 BIC COBADEFF"); the IBAN is its first `length` characters.
            var end = 0;
            for (var seen = 0; end < m.Length && seen < length; end++)
            {
                if (m.Value[end] != ' ')
                {
                    seen++;
                }
            }
            var compact = m.Value[..end].Replace(" ", "", StringComparison.Ordinal);
            if (compact.Length != length || (end < m.Length && m.Value[end] != ' ') || !IsValidIban(compact))
            {
                continue;
            }
            var confidence = HasNearby(text, m.Index, IbanContext) ? Confidence.High : Confidence.Medium;
            hits.Add("DS-BANK-02", confidence, m.Index, end, $"{country}** **** **** {compact[^4..]}");
        }
    }

    /// <summary>The ISO 13616 check: move the first four characters to the end, letters as 10 to 35, and the number mod 97 is 1.</summary>
    public static bool IsValidIban(string iban)
    {
        ArgumentNullException.ThrowIfNull(iban);
        if (iban.Length < 15 || iban.Length > 34 || !IbanLengths.TryGetValue(iban[..2], out var length) || iban.Length != length
            || !char.IsAsciiDigit(iban[2]) || !char.IsAsciiDigit(iban[3]))
        {
            return false;
        }
        var remainder = 0;
        foreach (var c in iban[4..] + iban[..4])
        {
            if (char.IsAsciiDigit(c))
            {
                remainder = ((remainder * 10) + (c - '0')) % 97;
            }
            else if (c is >= 'A' and <= 'Z')
            {
                remainder = ((remainder * 100) + (c - 'A' + 10)) % 97;
            }
            else
            {
                return false;
            }
        }
        return remainder == 1;
    }

    /// <summary>
    /// People's names next to their address or date of birth: a labelled name ("Name: ...") followed within 200
    /// characters by a street address or a date of birth, a name on its own line just above an address with a state
    /// and ZIP code (a mailing label), and each row of a table with a name column and an address or date of birth
    /// column. Each person is counted once.
    /// </summary>
    private static void DetectPersonalDetails(string text, Hits hits)
    {
        var candidates = new List<(int Start, int End, string Masked)>();
        void Record(int start, int end, string masked) => candidates.Add((start, end, masked));

        foreach (Match m in NameLabelled.Matches(text))
        {
            var from = m.Index + m.Length;
            var window = Math.Min(RecordWindow, text.Length - from);
            var address = StreetAddress.Match(text, from, window);
            var dob = Dob.Match(text, from, window);
            if (!dob.Success)
            {
                dob = BornOn.Match(text, from, window);
            }
            var next = address.Success && (!dob.Success || address.Index < dob.Index) ? address : dob.Success ? dob : null;
            if (next is null || IsOrganisation(m.Groups[1].Value) || NameLabelled.Match(text, from, next.Index - from).Success)
            {
                continue; // nothing after this name, a company's name, or it belongs to the next name
            }
            Record(m.Index, next.Index + next.Length, next == address ? "[name] + [street address]" : "[name] + [date of birth]");
        }

        foreach (Match a in StreetAddress.Matches(text))
        {
            var before = Math.Max(0, a.Index - 60);
            var name = NameAtLineStart.Match(text, before, a.Index - before);
            var tail = Math.Min(60, text.Length - a.Index - a.Length);
            var zip = StateZip.Match(text, a.Index + a.Length, tail);
            if (name.Success && zip.Success && !IsOrganisation(name.Groups[1].Value))
            {
                Record(name.Groups[1].Index, zip.Index + zip.Length, "[name] + [street address]");
            }
        }

        foreach (var (start, end, dob) in PersonRows(text))
        {
            Record(start, end, dob ? "[name] + [date of birth]" : "[name] + [street address]");
        }

        // Each person once: a record that overlaps one already kept (a labelled name on a mailing label, say) is the same person.
        var records = new List<(int Start, int End, string Masked)>();
        foreach (var c in candidates.OrderBy(c => c.Start).ThenByDescending(c => c.End))
        {
            if (records.Count == 0 || c.Start >= records[^1].End)
            {
                records.Add(c);
            }
        }
        // Other people's names and addresses next to one in its evidence are hidden too.
        var confidence = records.Count >= ManyPeople ? Confidence.High : Confidence.Medium;
        var hidden = records.ConvertAll(r => (r.Start, r.End));
        foreach (var (start, end, masked) in records)
        {
            hits.Add("DS-PII-01", confidence, start, end - start, masked, hidden);
        }
    }

    private static readonly HashSet<string> OrganisationWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Inc", "LLC", "LLP", "Ltd", "Limited", "Corp", "Corporation", "Company", "Co", "Group", "Associates", "Partners",
        "Bank", "Clinic", "Hospital", "School", "University", "Services", "Solutions",
    };

    /// <summary>"Acme Corp" or "Springfield Clinic": a business's name and address, not a person's.</summary>
    public static bool IsOrganisation(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var last = name.TrimEnd('.', ' ');
        return OrganisationWords.Contains(last[(last.LastIndexOf(' ') + 1)..]);
    }

    /// <summary>
    /// Rows of a table whose header has a name column and an address or date of birth column, as the span from the
    /// first of those cells to the end of the last. Quoted cells may hold the delimiter ("12 Oak St, Springfield").
    /// </summary>
    private static List<(int Start, int End, bool Dob)> PersonRows(string text)
    {
        var rows = new List<(int, int, bool)>();
        int[]? names = null;
        int[]? details = null;
        var dobColumns = false;
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
                names = details = null;
                continue;
            }
            var lineDelimiter = names is not null && line.Contains(delimiter) ? delimiter : Delimiter(line);
            if (lineDelimiter == '\0')
            {
                continue;
            }
            var cells = SplitCells(line, lineDelimiter);
            var nameCols = Columns(cells, line, NameHeader);
            var addressCols = Columns(cells, line, AddressHeader);
            var dobCols = Columns(cells, line, DobHeader);
            if (nameCols.Length > 0 && (addressCols.Length > 0 || dobCols.Length > 0))
            {
                (names, delimiter) = (nameCols, lineDelimiter);
                dobColumns = addressCols.Length == 0;
                details = dobColumns ? dobCols : addressCols;
                continue;
            }
            if (names is null || details is null || lineDelimiter != delimiter)
            {
                continue;
            }
            (int From, int To) name = default, detail = default;
            foreach (var c in names.Where(c => c < cells.Count))
            {
                if (line[cells[c].From..cells[c].To].ContainsAny(Letters))
                {
                    name = cells[c];
                    break;
                }
            }
            foreach (var c in details.Where(c => c < cells.Count))
            {
                var cell = line[cells[c].From..cells[c].To];
                if (dobColumns ? DateCell.IsMatch(cell) : IsAddressCell(cell))
                {
                    detail = cells[c];
                    break;
                }
            }
            if (name.To > name.From && detail.To > detail.From)
            {
                rows.Add((lineStart + Math.Min(name.From, detail.From), lineStart + Math.Max(name.To, detail.To), dobColumns));
            }
        }
        return rows;
    }

    private static readonly System.Buffers.SearchValues<char> Letters =
        System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");

    /// <summary>An address cell: a house number or PO box followed by words.</summary>
    private static bool IsAddressCell(ReadOnlySpan<char> cell) =>
        cell.Length >= 5 && (char.IsAsciiDigit(cell[0]) || cell.StartsWith("PO", StringComparison.OrdinalIgnoreCase) || cell.StartsWith("P.O", StringComparison.OrdinalIgnoreCase))
        && cell.ContainsAny(Letters) && cell.Contains(' ');

    private static int[] Columns(List<(int From, int To)> cells, ReadOnlySpan<char> line, Regex header)
    {
        var found = new List<int>();
        for (var i = 0; i < cells.Count; i++)
        {
            var cell = line[cells[i].From..cells[i].To];
            if (cell.Length <= MaxHeaderChars && header.IsMatch(cell))
            {
                found.Add(i);
            }
        }
        return [.. found];
    }

    /// <summary>The cells of one row, trimmed of spaces and quotes; a delimiter inside double quotes stays in the cell.</summary>
    private static List<(int From, int To)> SplitCells(ReadOnlySpan<char> line, char delimiter)
    {
        var cells = new List<(int, int)>();
        var cellStart = 0;
        var quoted = false;
        for (var i = 0; i <= line.Length && cells.Count < MaxColumns; i++)
        {
            if (i < line.Length)
            {
                if (line[i] == '"')
                {
                    quoted = !quoted;
                }
                if (quoted || line[i] != delimiter)
                {
                    continue;
                }
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
            cells.Add((from, to));
            cellStart = i + 1;
        }
        return cells;
    }
}
