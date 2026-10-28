using System.Text.RegularExpressions;

namespace DataScope.Core.Classification;

public static partial class TextScanner
{
    /// <summary>Text is searched in pieces of this many characters, so one long match can't make a pattern slow (D133).</summary>
    private const int PieceChars = 4096;

    /// <summary>Characters before each piece, so \b at a piece's start sees the character before it.</summary>
    private const int PieceLead = 16;

    /// <summary>Different values remembered per rule and file; a file with more still reports the rule.</summary>
    private const int MaxCustomValues = 10_000;

    private static void DetectCustom(string text, Hits hits, CustomRuleSet rules)
    {
        foreach (var rule in rules.Rules)
        {
            var definition = rule.Definition;
            var near = rule.Near is null ? null : Pieces(text, rule.Near);
            var comparer = definition.CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
            var values = new HashSet<string>(comparer);
            var evidence = new List<(int, int, string)>();
            var spans = new List<(int Start, int End)>();
            var pattern = definition.Kind == CustomRuleKind.Pattern;
            foreach (var (index, length) in Pieces(text, rule.Match))
            {
                if (near is not null && !IsNear(near, index, length))
                {
                    continue;
                }
                if (pattern && spans.Count < MaxCustomValues)
                {
                    spans.Add((index, index + length));
                }
                var value = Whitespace.Replace(text.Substring(index, length), " ");
                if (values.Count < MaxCustomValues && values.Add(value) && evidence.Count < MaxEvidence)
                {
                    // Words the administrator chose are shown as found; a pattern's values are masked like card numbers.
                    evidence.Add((index, length, pattern ? MaskCustom(value) : value));
                }
            }
            if (values.Count >= definition.MinMatches)
            {
                hits.AddCounted(definition.RuleId, CustomRules.ConfidenceOf(definition.Effect), values.Count, evidence, pattern ? spans : []);
            }
        }
    }

    /// <summary>
    /// Every match, in order, found piece by piece: each piece starts a little early for context and runs on past
    /// its end by the longest match kept, and a match counts in the piece where it starts. Matches longer than
    /// <see cref="CustomRules.MaxMatchChars"/> are cut short; empty matches are skipped.
    /// </summary>
    private static List<(int Index, int Length)> Pieces(string text, Regex regex)
    {
        var found = new List<(int, int)>();
        for (var start = 0; start < text.Length; start += PieceChars)
        {
            var from = Math.Max(0, start - PieceLead);
            var to = Math.Min(text.Length, start + PieceChars + CustomRules.MaxMatchChars);
            foreach (var m in regex.EnumerateMatches(text.AsSpan(from, to - from)))
            {
                var index = from + m.Index;
                if (m.Length > 0 && index >= start && index < start + PieceChars)
                {
                    found.Add((index, Math.Min(m.Length, CustomRules.MaxMatchChars)));
                }
            }
        }
        return found;
    }

    /// <summary>Whether any of the nearby words (in text order) is within <see cref="CustomRules.NearWindow"/> characters of the match.</summary>
    private static bool IsNear(List<(int Index, int Length)> near, int index, int length)
    {
        var lo = 0;
        var hi = near.Count;
        var earliest = index - CustomRules.NearWindow - CustomRules.MaxMatchChars;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (near[mid].Index < earliest)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        for (var i = lo; i < near.Count && near[i].Index <= index + length + CustomRules.NearWindow; i++)
        {
            if (near[i].Index + near[i].Length >= index - CustomRules.NearWindow)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>All but the last four characters hidden, or all of them when the value is shorter than eight.</summary>
    internal static string MaskCustom(string value) =>
        value.Length >= 8 ? new string('•', value.Length - 4) + value[^4..] : new string('•', value.Length);
}
