using DataScope.Core.Classification;

namespace DataScope.Core.Tests.Classification;

public class ClassificationPolicyTests
{
    private static Finding F(FindingCategory category, Confidence confidence) => new("X", category, confidence, 1, []);

    [Fact]
    public void Maps_findings_to_labels()
    {
        Assert.Equal(Sensitivity.Internal, ClassificationPolicy.Classify([]));
        Assert.Equal(Sensitivity.Public, ClassificationPolicy.Classify([], intendedPublic: true));
        Assert.Equal(Sensitivity.Restricted, ClassificationPolicy.Classify(TextScanner.Scan("SSN 512-48-3307").Findings));
        Assert.Equal(Sensitivity.Confidential, ClassificationPolicy.Classify(TextScanner.Scan("Part# 411-22-3333").Findings));
    }

    [Theory]
    [InlineData(FindingCategory.DateOfBirth, Confidence.High, Sensitivity.Confidential)]
    [InlineData(FindingCategory.PaymentCard, Confidence.High, Sensitivity.Restricted)]
    [InlineData(FindingCategory.PaymentCard, Confidence.Medium, Sensitivity.Confidential)]
    [InlineData(FindingCategory.BankAccount, Confidence.Low, Sensitivity.Internal)]
    [InlineData(FindingCategory.Credentials, Confidence.Medium, Sensitivity.Restricted)]
    [InlineData(FindingCategory.Credentials, Confidence.Low, Sensitivity.Internal)]
    [InlineData(FindingCategory.HealthInformation, Confidence.Medium, Sensitivity.Restricted)]
    [InlineData(FindingCategory.HealthInformation, Confidence.Low, Sensitivity.Internal)]
    public void Applies_the_category_rules(FindingCategory category, Confidence confidence, Sensitivity expected) =>
        Assert.Equal(expected, ClassificationPolicy.Classify([F(category, confidence)]));

    [Fact]
    public void A_low_finding_does_not_lift_a_public_share() =>
        Assert.Equal(Sensitivity.Public, ClassificationPolicy.Classify([F(FindingCategory.BankAccount, Confidence.Low)], intendedPublic: true));

    [Fact]
    public void Every_rule_has_a_unique_id_and_a_label()
    {
        Assert.Equal(ClassificationPolicy.Rules.Count, ClassificationPolicy.Rules.Select(r => r.Id).Distinct().Count());
        foreach (var rule in ClassificationPolicy.Rules)
        {
            Assert.False(string.IsNullOrWhiteSpace(rule.Category.Label()));
            Assert.True(FindingCategoryText.TryParseLabel(rule.Category.Label(), out var parsed));
            Assert.Equal(rule.Category, parsed);
        }
        Assert.False(FindingCategoryText.TryParseLabel("Nuclear codes", out _));
    }
}
