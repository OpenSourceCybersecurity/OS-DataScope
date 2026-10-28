using System.Globalization;
using DataScope.Core.Classification;

namespace DataScope.Core.Tests.Classification;

/// <summary>
/// Measures the detectors on generated documents: files that should be flagged and look-alike files that should not.
/// The thresholds are a regression guard, not a claim about accuracy on real data.
/// </summary>
public class SyntheticCorpusTests
{
    private const int PerKind = 200;

    [Fact]
    public void Flags_nearly_all_sensitive_documents_and_few_lookalikes()
    {
        var random = new Random(20261009);
        var positives = Enumerable.Range(0, PerKind).SelectMany(_ => Positives(random)).ToList();
        var negatives = Enumerable.Range(0, PerKind).SelectMany(_ => Negatives(random)).ToList();

        var missed = positives.Where(p => !Flagged(p.Text, p.Expected)).ToList();
        var falseAlarms = negatives.Where(n => Classify(n) > Sensitivity.Internal).ToList();

        var recall = 1 - ((double)missed.Count / positives.Count);
        var falseAlarmRate = (double)falseAlarms.Count / negatives.Count;
        Assert.True(recall >= 0.98, $"recall {recall:P1}; first miss: {missed.FirstOrDefault().Text}");
        Assert.True(falseAlarmRate <= 0.02, $"false alarms {falseAlarmRate:P1}; first: {falseAlarms.FirstOrDefault()}");
    }

    private static bool Flagged(string text, FindingCategory expected) =>
        TextScanner.Scan(text).Findings.Any(f => f.Category == expected && f.Confidence >= Confidence.Medium);

    private static Sensitivity Classify(string text) => ClassificationPolicy.Classify(TextScanner.Scan(text).Findings);

    private static IEnumerable<(string Text, FindingCategory Expected)> Positives(Random r)
    {
        yield return ($"Name: {Person(r)}\nSSN: {Ssn(r)}\nStart date: 2024-03-01", FindingCategory.UsSsn);
        yield return ($"Payroll export,{Person(r)},social security {Ssn(r)},full time", FindingCategory.UsSsn);
        yield return ($"Customer paid with Visa {Card(r, "4", 16, spaced: true)} exp 09/29", FindingCategory.PaymentCard);
        yield return ($"card on file: {Card(r, "5" + r.Next(1, 6).ToString(CultureInfo.InvariantCulture), 16, spaced: false)}", FindingCategory.PaymentCard);
        yield return ($"Direct deposit form\nRouting number: 011000015\nAccount number: {r.NextInt64(10_000_000, 999_999_999)}", FindingCategory.BankAccount);
        yield return ($"[database]\nhost=sql01\npassword={Word(r)}{r.Next(1000, 9999)}!", FindingCategory.Credentials);
        yield return ($"Patient name: {Person(r)}\nDiagnosis: J45.909, prescription renewed, treatment plan reviewed", FindingCategory.HealthInformation);
        yield return ($"Employee {Person(r)} DOB: {r.Next(1, 13)}/{r.Next(1, 29)}/19{r.Next(50, 99)}", FindingCategory.DateOfBirth);
        yield return ($"Rental agreement for {Person(r)}. Driver's license: {(char)('A' + r.Next(26))}{r.Next(1_000_000, 9_999_999)}", FindingCategory.DriversLicense);
        yield return ($"Travel booking\nTraveller: {Person(r)}\nPassport no: {r.Next(100_000_000, 999_999_999)}", FindingCategory.Passport);
        yield return ($"Supplier payment to IBAN {Iban(r)} reference {r.Next(1000, 9999)}", FindingCategory.BankAccount);
        yield return ($"Name: {Person(r)}\nAddress: {r.Next(1, 9999)} {Word(r)} Street, Springfield, IL 6{r.Next(1000, 9999)}", FindingCategory.PersonalDetails);
    }

    /// <summary>A German IBAN with a random account and the right check digits.</summary>
    private static string Iban(Random r)
    {
        var bban = string.Concat(Enumerable.Range(0, 18).Select(_ => r.Next(10).ToString(CultureInfo.InvariantCulture)));
        for (var check = 2; check < 99; check++)
        {
            var candidate = string.Create(CultureInfo.InvariantCulture, $"DE{check:00}{bban}");
            if (TextScanner.IsValidIban(candidate))
            {
                return candidate;
            }
        }
        throw new InvalidOperationException("no check digits");
    }

    private static IEnumerable<string> Negatives(Random r)
    {
        yield return $"Invoice {r.Next(100_000_000, 999_999_999)} paid on 2024-0{r.Next(1, 9)}-1{r.Next(0, 9)}. Total ${r.Next(100, 9999)}.00";
        yield return $"Order {r.NextInt64(1_000_000_000, 9_999_999_999)} shipped via tracking 9{r.NextInt64(100_000_000_000_000, 999_999_999_999_999)}";
        yield return $"Call the help desk at 555-{r.Next(100, 999)}-{r.Next(1000, 9999)} for a password reset.";
        yield return $"Meeting notes {r.Next(1, 13)}/{r.Next(1, 29)}/2025: budget review, {r.Next(3, 12)} attendees.";
        yield return $"Part numbers: {r.Next(100, 999)}{r.Next(10, 99)}{r.Next(1000, 9999)}, {r.Next(100, 999)}-{r.Next(1000, 9999)}";
        yield return "Our clinic sponsors the 5k run this year. Sign up at the front desk.";
        yield return $"Version {r.Next(1, 9)}.{r.Next(0, 99)}.{r.Next(0, 999)} release notes: fixed login page, set password = <new password> in the template.";
        yield return $"Quarterly revenue {r.Next(1, 999)},{r.Next(100, 999)},{r.Next(100, 999)} and {r.Next(10, 99)}% margin";
        yield return $"Drivers needed: a valid driver's license and {r.Next(2, 9)} years of experience. Apply at {r.Next(1, 999)} Main Street.";
        yield return $"Renew your passport at least {r.Next(3, 9)} months before you travel. Office hours {r.Next(8, 10)}-{r.Next(4, 6)}.";
        yield return $"Ship to Acme Corp, {r.Next(1, 9999)} Oak Street, Springfield, IL 6{r.Next(1000, 9999)}";
        yield return $"Ticket DE{r.Next(10, 99)}{r.NextInt64(100_000_000_000, 999_999_999_999)} closed";
    }

    private static string Ssn(Random r)
    {
        while (true)
        {
            var area = r.Next(1, 900);
            if (area == 666)
            {
                continue;
            }
            return string.Create(CultureInfo.InvariantCulture, $"{area:000}-{r.Next(1, 100):00}-{r.Next(1, 10000):0000}");
        }
    }

    private static string Card(Random r, string prefix, int length, bool spaced)
    {
        var digits = prefix + string.Concat(Enumerable.Range(0, length - prefix.Length - 1).Select(_ => r.Next(10).ToString(CultureInfo.InvariantCulture)));
        for (var check = 0; check < 10; check++)
        {
            var candidate = digits + check.ToString(CultureInfo.InvariantCulture);
            if (TextScanner.Luhn(candidate))
            {
                return spaced ? string.Join(' ', candidate.Chunk(4).Select(c => new string(c))) : candidate;
            }
        }
        throw new InvalidOperationException("unreachable");
    }

    private static readonly string[] First = ["Avery", "Jordan", "Riley", "Morgan", "Casey", "Quinn", "Harper", "Rowan"];
    private static readonly string[] Last = ["Okafor", "Lindqvist", "Tanaka", "Moreau", "Silva", "Novak", "Haddad", "Byrne"];

    private static string Person(Random r) => $"{First[r.Next(First.Length)]} {Last[r.Next(Last.Length)]}";

    private static string Word(Random r) => ((string[])["Winter", "Summer", "Falcon", "Orchid", "Granite"])[r.Next(5)];
}
