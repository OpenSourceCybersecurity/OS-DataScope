using System.Text;
using DataScope.Core.Classification;

namespace DataScope.Core.Tests.Classification;

/// <summary>Text longer than one scan, such as an Outlook mailbox's messages, scanned in pieces (D134). All synthetic.</summary>
public class TextScannerLongTests
{
    private static string Filler(int chars)
    {
        var b = new StringBuilder(chars + 32);
        while (b.Length < chars)
        {
            b.Append("Lorem ipsum dolor sit amet.\n");
        }
        return b.ToString(0, chars);
    }

    [Fact]
    public void Text_that_fits_in_one_scan_is_scanned_as_one()
    {
        const string text = "Employee SSN 512-48-3307 and card 4111 1111 1111 1111 exp 12/29";
        Assert.Equivalent(TextScanner.Scan(text), TextScanner.ScanLong(text), strict: true);
    }

    [Fact]
    public void Findings_in_every_piece_are_merged_into_one_per_rule()
    {
        var text = new StringBuilder();
        for (var piece = 0; piece < 4; piece++)
        {
            text.Append(Filler(400_000)).Append("\n\nEmployee SSN 512-48-33").Append(piece).Append("1\n\n");
            text.Append(Filler(400_000)).Append("\n\nEmployee SSN 512-48-33").Append(piece).Append("2\n\n");
        }
        Assert.True(text.Length > 3 * TextScanner.MaxScanChars);
        var result = TextScanner.ScanLong(text.ToString());
        Assert.False(result.Truncated);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("DS-SSN-01", finding.RuleId);
        Assert.Equal(8, finding.Count);
        Assert.Equal(Confidence.High, finding.Confidence);
        Assert.Equal(TextScanner.MaxEvidence, finding.Evidence.Count);
    }

    [Fact]
    public void The_highest_confidence_of_any_piece_is_kept()
    {
        // A bare number next to the keyword is a medium match; a formatted one is high.
        var text = "SSN 512483307\n\n" + Filler(TextScanner.MaxScanChars) + "\n\nSSN 512-48-3308";
        var finding = Assert.Single(TextScanner.ScanLong(text).Findings);
        Assert.Equal(Confidence.High, finding.Confidence);
        Assert.Equal(2, finding.Count);
    }

    [Fact]
    public void Pieces_end_at_a_blank_line_so_a_message_is_not_cut_in_two()
    {
        // Cut at exactly the piece size, the number would be split between two pieces and missed.
        var before = Filler(700_000) + "\n\n" + Filler(299_987) + "\n";
        var text = before + "SSN 512-48-3307\n" + Filler(500_000);
        Assert.InRange(before.Length, TextScanner.MaxScanChars - 20, TextScanner.MaxScanChars - 5);
        var finding = Assert.Single(TextScanner.ScanLong(text).Findings);
        Assert.Equal(1, finding.Count);
    }

    [Fact]
    public void Text_with_no_blank_lines_is_still_scanned_whole()
    {
        var text = new string('a', TextScanner.MaxScanChars + 10) + " SSN 512-48-3307";
        var result = TextScanner.ScanLong(text);
        Assert.False(result.Truncated);
        Assert.Single(result.Findings);
    }

    [Fact]
    public void Text_beyond_the_long_cap_is_not_scanned_and_says_so()
    {
        var text = new string('a', TextScanner.MaxLongScanChars) + " SSN 512-48-3307";
        var result = TextScanner.ScanLong(text);
        Assert.True(result.Truncated);
        Assert.Empty(result.Findings);
    }
}
