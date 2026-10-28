namespace DataScope.Core.Classification;

/// <summary>Turns findings into a sensitivity label. Any change to these rules or the detectors needs a new <see cref="CurrentVersion"/>.</summary>
public static class ClassificationPolicy
{
    /// <summary>
    /// Stamped on every classification so results from older rules can be found and rescanned.
    /// r4 is the first server version: r3 (the browser prototype) plus "D.O.B." dates and more password placeholders.
    /// r5 finds SSNs written with spaces, every row under an SSN column header (including numbers that lost their
    /// leading zero in Excel), more keyword spellings such as "SS#", and counts each value once.
    /// r6 finds US ITINs (9NN-NN-NNNN in the IRS ranges), which r5 rightly did not count as SSNs (D117).
    /// r7 reads files that r6 skipped: saved emails, zips, and backup, old and temporary copies (D120).
    /// r8 reads every file by its contents unless its name says it is a program, disk image, video, audio or font: an
    /// unknown extension, no extension, or a document renamed as an image is no longer skipped by its name (D127).
    /// r9 reads the text in images, scanned PDF pages and pictures in zips and emails by OCR (D129).
    /// r10 reads scans that are sideways, upside down or tilted, and pictures pasted into Word, Excel and PowerPoint
    /// files (D131).
    /// r11 reads Word, Excel and PowerPoint 97-2003 files, Outlook .msg files, and 7-Zip and RAR archives (D132).
    /// r12 reads Outlook data files (.pst and .ost) message by message, and scans text longer than one scan in pieces (D134).
    /// r13 finds driver's licence and passport numbers next to a label or in a column, IBANs, and people's names next
    /// to their street address or date of birth (D137).
    /// </summary>
    public const string CurrentVersion = "2026.10-r13";

    public static IReadOnlyList<DetectionRule> Rules { get; } =
    [
        new("DS-SSN-01", "US Social Security number", FindingCategory.UsSsn, "NNN-NN-NNNN or NNN NN NNNN with SSA range checks; unformatted numbers count next to SSN keywords or under an SSN column header."),
        new("DS-ITIN-01", "US Individual Taxpayer ID number", FindingCategory.UsItin, "9NN-NN-NNNN or 9NN NN NNNN with the IRS middle-digit ranges (50-65, 70-88, 90-92, 94-99); same keyword and column rules as SSNs."),
        new("DS-PCI-01", "Payment card number", FindingCategory.PaymentCard, "13-19 digits with a known issuer prefix that pass the Luhn checksum."),
        new("DS-BANK-01", "Bank routing + account", FindingCategory.BankAccount, "ABA routing number (checksum-valid) near an account number."),
        new("DS-CRED-01", "Private key material", FindingCategory.Credentials, "PEM private key blocks."),
        new("DS-CRED-02", "Password or secret in text", FindingCategory.Credentials, "password=, pwd:, connection-string passwords, AWS access key IDs."),
        new("DS-PHI-01", "Health information", FindingCategory.HealthInformation, "Two or more clinical terms or ICD-10 codes, scored higher next to a personal identifier."),
        new("DS-DOB-01", "Date of birth", FindingCategory.DateOfBirth, "A date next to \"DOB\" or \"date of birth\"."),
        new("DS-DL-01", "Driver's licence number", FindingCategory.DriversLicense, "A licence number right after \"driver's license\", \"DL#\" or \"DLN\", or in a column whose header names a licence."),
        new("DS-PASS-01", "Passport number", FindingCategory.Passport, "6 to 9 digits, or 1 or 2 letters and digits, right after \"passport\" or in a passport column."),
        new("DS-BANK-02", "International bank account number (IBAN)", FindingCategory.BankAccount, "A country code, check digits and account, the right length for the country, that passes the ISO 13616 check."),
        new("DS-PII-01", "Name with address or date of birth", FindingCategory.PersonalDetails, "A labelled name followed by a street address or date of birth, a name above an address with a state and ZIP, or rows of a table with name and address or date of birth columns."),
    ];

    /// <summary>
    /// Restricted: any high-confidence finding except a date of birth or a name with an address or date of birth, or
    /// credentials or health information above low.
    /// Confidential: any other finding above low. Otherwise Internal, or Public when the share is intended to be public.
    /// </summary>
    public static Sensitivity Classify(IEnumerable<Finding> findings, bool intendedPublic = false)
    {
        ArgumentNullException.ThrowIfNull(findings);
        var level = intendedPublic ? Sensitivity.Public : Sensitivity.Internal;
        foreach (var f in findings)
        {
            var restricted =
                (f.Confidence == Confidence.High && f.Category is not (FindingCategory.DateOfBirth or FindingCategory.PersonalDetails))
                || (f.Category == FindingCategory.Credentials && f.Confidence != Confidence.Low)
                || (f.Category == FindingCategory.HealthInformation && f.Confidence != Confidence.Low);
            if (restricted)
            {
                return Sensitivity.Restricted;
            }
            if (f.Confidence != Confidence.Low)
            {
                level = Sensitivity.Confidential;
            }
        }
        return level;
    }
}
