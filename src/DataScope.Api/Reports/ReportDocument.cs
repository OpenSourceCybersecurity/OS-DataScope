namespace DataScope.Api.Reports;

/// <summary>A report Data Scope can produce, and the policy a caller needs to produce it.</summary>
/// <param name="Id">Stable identifier used in URLs.</param>
/// <param name="Policy">Authorization policy, one of <see cref="Core.Security.Policies"/>.</param>
/// <param name="Period">The report covers the last <see cref="ReportContext.Days"/> days rather than the latest state.</param>
/// <param name="Standard">The report is about one compliance standard, which the caller chooses (<see cref="ReportContext.Standard"/>).</param>
public sealed record ReportInfo(string Id, string Title, string About, string Policy, bool Period = false, bool Standard = false);

/// <summary>One line of the statement every report starts with: when, what scope, how fresh, how complete.</summary>
public sealed record ReportFact(string Label, string Value);

/// <summary>
/// A finished report: the statement of scope, freshness and limitations required on every report (acceptance test
/// A15), then one table. Writers turn it into CSV or a print-ready page; the content is the same in both.
/// </summary>
public sealed class ReportDocument
{
    public required ReportInfo Info { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
    public required string GeneratedBy { get; init; }
    public required IReadOnlyList<ReportFact> Facts { get; init; }

    /// <summary>What the report cannot show, in plain words. Never empty.</summary>
    public required IReadOnlyList<string> Limitations { get; init; }

    public required IReadOnlyList<string> Columns { get; init; }
    public required IReadOnlyList<object?[]> Rows { get; init; }

    /// <summary>The report stopped at <see cref="RowLimit"/> rows; there were more.</summary>
    public bool Truncated { get; init; }

    public int RowLimit { get; init; }

    /// <summary>What the report is about when one report id covers several subjects, such as a standard's id; added to the file name.</summary>
    public string? Subject { get; init; }
}

/// <summary>Output format. Both are produced locally; nothing leaves the server.</summary>
public enum ReportFormat
{
    Csv,

    /// <summary>A self-contained page with no scripts, laid out for printing or "Save as PDF".</summary>
    Html,
}
