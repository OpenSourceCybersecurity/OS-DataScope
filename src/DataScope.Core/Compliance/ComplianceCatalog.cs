using DataScope.Core.Classification;

namespace DataScope.Core.Compliance;

/// <summary>A requirement a standard names that Data Scope's results are relevant to. Never a claim that it is met.</summary>
/// <param name="Ref">The requirement as the standard numbers it, for example "PCI DSS 4.0 req. 12.5.2".</param>
/// <param name="Text">What the requirement asks, in a few words.</param>
/// <param name="Helps">What Data Scope shows that helps with it.</param>
public sealed record ComplianceRequirement(string Ref, string Text, string Helps);

/// <summary>
/// A compliance standard an administrator can turn on (D126). It says which kinds of sensitive data fall under the
/// standard; Data Scope then tags files, folders and shares holding that data. It never says data or an organisation is
/// compliant: <see cref="NotDetected"/> lists what the standard covers that Data Scope can't find.
/// </summary>
/// <param name="Id">Stable lower-case id used in the API and saved settings.</param>
/// <param name="DefaultCategories">The kinds of data that count, until an administrator changes them.</param>
/// <param name="NotDetected">What the standard covers that Data Scope does not detect, shown as a limitation.</param>
/// <param name="EarlierDefaults">
/// What <paramref name="DefaultCategories"/> were in earlier versions. Saved settings hold the categories as they were
/// when the standard was turned on, so a saved list equal to one of these means the administrator never chose, and the
/// current defaults apply (D137).
/// </param>
public sealed record ComplianceStandard(
    string Id,
    string Name,
    string Summary,
    IReadOnlyList<FindingCategory> DefaultCategories,
    IReadOnlyList<string> NotDetected,
    IReadOnlyList<ComplianceRequirement> Requirements,
    IReadOnlyList<IReadOnlyList<FindingCategory>>? EarlierDefaults = null);

/// <summary>The standards Data Scope knows, and the rules for which findings count towards one.</summary>
public static class ComplianceCatalog
{
    /// <summary>Only findings at or above this confidence count. Low findings never make a file even Confidential.</summary>
    public const Confidence DefaultMinConfidence = Confidence.Medium;

    public static IReadOnlyList<Confidence> AllowedMinConfidence { get; } = [Confidence.Medium, Confidence.High];

    public static IReadOnlyList<ComplianceStandard> Standards { get; } =
    [
        new(
            "hipaa",
            "HIPAA",
            "US health privacy law. Applies to protected health information held by healthcare providers, health plans and their business associates.",
            [FindingCategory.HealthInformation],
            [
                "Health information without clinical terms or diagnosis codes, such as a list of patients' names alone.",
                "Medical record, health plan and insurance member numbers.",
                "Handwriting, and text in images that is sideways, faint or in a format the server cannot decode (text in images is read by OCR, which can misread characters).",
            ],
            [
                new("45 CFR 164.308(a)(1)(ii)(A)", "Risk analysis of where electronic PHI is held.", "Where health information was found, and which files were not read."),
                new("45 CFR 164.308(a)(4)", "Information access management.", "Who can read each folder with health information, and how."),
                new("45 CFR 164.502(b)", "Minimum necessary use and disclosure.", "Folders with health information that many people can read."),
                new("45 CFR 164.312(b)", "Audit controls.", "File activity on those folders, where auditing is turned on."),
            ]),
        new(
            "pci-dss",
            "PCI DSS",
            "Payment card industry standard. Applies to anyone who stores, processes or sends payment card numbers.",
            [FindingCategory.PaymentCard],
            [
                "Card numbers split across cells, lines or files, or written with unusual separators.",
                "Card verification codes, PINs and magnetic stripe data on their own.",
                "Handwriting, and text in images that is sideways, faint or in a format the server cannot decode (text in images is read by OCR, which can misread characters).",
            ],
            [
                new("PCI DSS 4.0 req. 12.5.2", "Confirm the scope at least every 12 months, including every place account data is stored.", "Every file share and folder where card numbers were found."),
                new("PCI DSS 4.0 req. 3.2.1", "Keep stored account data to a minimum.", "Card data on file shares, and how old it is."),
                new("PCI DSS 4.0 req. 7.2", "Access is limited to those whose job needs it.", "Who can read each folder with card numbers, and how."),
                new("PCI DSS 4.0 req. 7.2.4", "Review user accounts and access at least every six months.", "Access reviews of the folders that hold card data."),
            ]),
        new(
            "glba",
            "GLBA",
            "US Gramm-Leach-Bliley Act Safeguards Rule. Applies to financial institutions, including tax preparers, lenders and many accountants.",
            [FindingCategory.UsSsn, FindingCategory.UsItin, FindingCategory.BankAccount, FindingCategory.PaymentCard],
            [
                "Account numbers written without a routing number nearby, and loan or policy numbers.",
                "Income, credit and other financial details written in plain prose.",
                "Handwriting, and text in images that is sideways, faint or in a format the server cannot decode (text in images is read by OCR, which can misread characters).",
            ],
            [
                new("16 CFR 314.4(c)(1)", "Access controls, reviewed periodically.", "Who can read customer information, and access reviews."),
                new("16 CFR 314.4(c)(2)", "Identify and manage the data the business holds.", "Where customer financial information was found."),
                new("16 CFR 314.4(c)(6)", "Dispose of customer information within two years of last use.", "Old files holding customer information."),
                new("16 CFR 314.4(c)(8)", "Monitor and log what authorised users do.", "File activity on those folders, where auditing is turned on."),
            ]),
        new(
            "state-breach",
            "US state breach laws",
            "US state data breach and data security laws. Most require notice when certain personal information is exposed; which items count differs by state.",
            [FindingCategory.UsSsn, FindingCategory.UsItin, FindingCategory.PaymentCard, FindingCategory.BankAccount, FindingCategory.HealthInformation,
             FindingCategory.Credentials, FindingCategory.DriversLicense, FindingCategory.Passport],
            [
                "State ID card numbers, and driver's licence and passport numbers written without a label such as \"DL#\" or \"Passport no\" next to them or a column header naming them.",
                "Biometric data.",
                "Whether a person's name appears next to the number, which most of these laws require.",
            ],
            [
                new("Cal. Civ. Code 1798.82", "California breach notification.", "Where personal information that would need notice is stored, and who can reach it."),
                new("201 CMR 17.00", "Massachusetts data security regulation.", "Where personal information is held, and access to it."),
                new("N.Y. Gen. Bus. Law 899-aa and 899-bb", "New York breach notification and SHIELD Act safeguards.", "Where private information is stored, and access to it."),
            ],
            [[FindingCategory.UsSsn, FindingCategory.UsItin, FindingCategory.PaymentCard, FindingCategory.BankAccount, FindingCategory.HealthInformation, FindingCategory.Credentials]]),
        new(
            "secrets",
            "Passwords and keys",
            "Internal security policy, for example SOC 2 or ISO 27001 controls on secrets. Passwords and private keys should not sit in shared files.",
            [FindingCategory.Credentials],
            [
                "API tokens and secrets in formats other than AWS access key IDs.",
                "Passwords in files Data Scope can't read, such as password manager vaults and encrypted documents.",
            ],
            [
                new("ISO/IEC 27001:2022 A.5.17", "Authentication information is managed securely.", "Files with passwords or private keys, and who can read them."),
            ]),
    ];

    public const int MinReasonLength = 3;
    public const int MaxReasonLength = 500;

    /// <summary>Why a reason for turning a standard on or off can't be used, or null when it can.</summary>
    public static string? ReasonProblem(string? reason)
    {
        var text = reason?.Trim() ?? "";
        if (text.Length < MinReasonLength)
        {
            return "Say why, so others know later. For example: we take card payments by phone.";
        }
        return text.Length > MaxReasonLength || text.Any(c => char.IsControl(c) && c is not '\n' and not '\r')
            ? $"Use at most {MaxReasonLength} characters of plain text."
            : null;
    }

    public static ComplianceStandard? Find(string? id) =>
        Standards.FirstOrDefault(s => string.Equals(s.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether a file's findings put it under a standard: a finding in one of the categories at or above the minimum
    /// confidence, unless a reviewer set the file's label below Confidential (a false positive).
    /// </summary>
    public static bool Covers(
        IReadOnlyCollection<FindingCategory> categories, Confidence minConfidence,
        IEnumerable<(FindingCategory Category, Confidence Confidence)> findings, Sensitivity? overrideSensitivity)
    {
        ArgumentNullException.ThrowIfNull(categories);
        ArgumentNullException.ThrowIfNull(findings);
        if (overrideSensitivity is { } o && o < Sensitivity.Confidential)
        {
            return false;
        }
        return findings.Any(f => categories.Contains(f.Category) && f.Confidence >= minConfidence);
    }
}
