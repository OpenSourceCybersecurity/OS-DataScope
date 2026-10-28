using System.Globalization;
using System.Text;

namespace DataScope.Api.Reviews;

/// <summary>
/// CSV for spreadsheets. Cells that a spreadsheet would treat as a formula are prefixed with an apostrophe, because
/// names and justifications come from people and from Active Directory.
/// </summary>
internal static class Csv
{
    public static string Line(IEnumerable<object?> cells) => string.Join(",", cells.Select(Cell)) + "\r\n";

    public static string Cell(object? value)
    {
        var text = value switch
        {
            null => "",
            DateTimeOffset t => t.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            bool b => b ? "yes" : "no",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r' or '|' or '%')
        {
            // A negative number is data, not a formula.
            if (!(value is IFormattable && text[0] == '-'))
            {
                text = "'" + text;
            }
        }
        if (text.AsSpan().IndexOfAny("\",\r\n") >= 0)
        {
            text = "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }
        return text;
    }

    public static readonly Encoding Utf8WithBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
}
