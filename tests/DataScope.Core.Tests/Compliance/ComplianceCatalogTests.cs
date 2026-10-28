using DataScope.Core.Classification;
using DataScope.Core.Compliance;

namespace DataScope.Core.Tests.Compliance;

public class ComplianceCatalogTests
{
    private static readonly FindingCategory[] Cards = [FindingCategory.PaymentCard];

    [Fact]
    public void Every_standard_has_a_unique_id_data_it_covers_limits_and_requirements()
    {
        var ids = ComplianceCatalog.Standards.Select(s => s.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(ComplianceCatalog.Standards, s =>
        {
            Assert.Matches("^[a-z][a-z-]{1,31}$", s.Id);
            Assert.NotEmpty(s.DefaultCategories);
            Assert.NotEmpty(s.NotDetected);
            Assert.NotEmpty(s.Requirements);
            // Data Scope shows evidence for a requirement; it never claims one is met.
            Assert.All(s.Requirements, r => Assert.DoesNotContain("complian", r.Helps, StringComparison.OrdinalIgnoreCase));
        });
    }

    [Theory]
    [InlineData("pci-dss", "PCI DSS")]
    [InlineData(" HIPAA ", "HIPAA")]
    public void Standards_are_found_by_id_in_any_case(string id, string name) =>
        Assert.Equal(name, ComplianceCatalog.Find(id)!.Name);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sox")]
    public void Unknown_standards_are_not_found(string? id) => Assert.Null(ComplianceCatalog.Find(id));

    [Fact]
    public void A_finding_in_a_covered_category_at_the_minimum_confidence_counts()
    {
        Assert.True(ComplianceCatalog.Covers(Cards, Confidence.Medium, [(FindingCategory.PaymentCard, Confidence.Medium)], null));
        Assert.True(ComplianceCatalog.Covers(Cards, Confidence.Medium, [(FindingCategory.UsSsn, Confidence.High), (FindingCategory.PaymentCard, Confidence.High)], null));
    }

    [Fact]
    public void Weaker_matches_and_other_categories_do_not_count()
    {
        Assert.False(ComplianceCatalog.Covers(Cards, Confidence.High, [(FindingCategory.PaymentCard, Confidence.Medium)], null));
        Assert.False(ComplianceCatalog.Covers(Cards, Confidence.Medium, [(FindingCategory.PaymentCard, Confidence.Low)], null));
        Assert.False(ComplianceCatalog.Covers(Cards, Confidence.Medium, [(FindingCategory.UsSsn, Confidence.High)], null));
        Assert.False(ComplianceCatalog.Covers(Cards, Confidence.Medium, [], null));
    }

    [Theory]
    [InlineData(Sensitivity.Public, false)]
    [InlineData(Sensitivity.Internal, false)]
    [InlineData(Sensitivity.Confidential, true)]
    [InlineData(Sensitivity.Restricted, true)]
    public void A_reviewer_marking_a_file_not_sensitive_takes_it_out_of_every_standard(Sensitivity label, bool covered) =>
        Assert.Equal(covered, ComplianceCatalog.Covers(Cards, Confidence.Medium, [(FindingCategory.PaymentCard, Confidence.High)], label));

    [Theory]
    [InlineData(null, false)]
    [InlineData("  ", false)]
    [InlineData("ok", false)]
    [InlineData("We take cards by phone.", true)]
    [InlineData("Line one\nline two", true)]
    [InlineData("bell\u0007", false)]
    public void Reasons_must_say_something_in_plain_text(string? reason, bool ok) =>
        Assert.Equal(ok, ComplianceCatalog.ReasonProblem(reason) is null);

    [Fact]
    public void Reasons_have_a_length_limit() =>
        Assert.NotNull(ComplianceCatalog.ReasonProblem(new string('x', ComplianceCatalog.MaxReasonLength + 1)));
}
