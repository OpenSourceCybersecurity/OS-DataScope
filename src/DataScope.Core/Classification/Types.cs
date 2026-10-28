namespace DataScope.Core.Classification;

/// <summary>How sure a detector is. A match is always a possible match, never a confirmed fact.</summary>
public enum Confidence
{
    Low = 1,
    Medium = 2,
    High = 3,
}

/// <summary>Sensitivity label for a file. <see cref="Unknown"/> means the contents were never read.</summary>
public enum Sensitivity
{
    Unknown = 0,
    Public = 1,
    Internal = 2,
    Confidential = 3,
    Restricted = 4,
}

public enum FindingCategory
{
    UsSsn,
    PaymentCard,
    BankAccount,
    Credentials,
    HealthInformation,
    DateOfBirth,
    UsItin,

    /// <summary>A match for one of the administrator's own rules (D133); the rule id says which.</summary>
    CustomRule,

    /// <summary>A driver's licence number next to a label or in a licence column (D137).</summary>
    DriversLicense,

    /// <summary>A passport number next to a label or in a passport column (D137).</summary>
    Passport,

    /// <summary>A person's name next to their street address or date of birth (D137).</summary>
    PersonalDetails,
}

/// <summary>
/// One rule's matches in one file. <see cref="Evidence"/> holds redacted snippets only: matched values are masked
/// and every digit and secret value in the surrounding text is replaced, so evidence never carries a usable value.
/// </summary>
public sealed record Finding(string RuleId, FindingCategory Category, Confidence Confidence, int Count, IReadOnlyList<string> Evidence);

public sealed record TextScanResult(IReadOnlyList<Finding> Findings, bool Truncated);

public sealed record DetectionRule(string Id, string Name, FindingCategory Category, string Description);

public static class FindingCategoryText
{
    /// <summary>Labels shown to users. They match the web UI's category names.</summary>
    public static string Label(this FindingCategory category) => category switch
    {
        FindingCategory.UsSsn => "US SSN",
        FindingCategory.PaymentCard => "Payment card",
        FindingCategory.BankAccount => "Bank account",
        FindingCategory.Credentials => "Credentials",
        FindingCategory.HealthInformation => "Health information",
        FindingCategory.DateOfBirth => "Date of birth",
        FindingCategory.UsItin => "US ITIN",
        FindingCategory.CustomRule => "Custom rule",
        FindingCategory.DriversLicense => "Driver's licence",
        FindingCategory.Passport => "Passport",
        FindingCategory.PersonalDetails => "Personal details",
        _ => throw new ArgumentOutOfRangeException(nameof(category)),
    };

    public static bool TryParseLabel(string? label, out FindingCategory category)
    {
        foreach (var value in Enum.GetValues<FindingCategory>())
        {
            if (string.Equals(value.Label(), label, StringComparison.OrdinalIgnoreCase)
                || string.Equals(value.ToString(), label, StringComparison.OrdinalIgnoreCase))
            {
                category = value;
                return true;
            }
        }
        category = default;
        return false;
    }
}
