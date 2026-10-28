using DataScope.Core.Inventory;

namespace DataScope.Core.Tests;

public sealed class InventoryNamingTests
{
    [Theory]
    [InlineData("HR", true)]
    [InlineData("Finance Shared", true)]
    [InlineData("C$", true)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("a\\b", false)]
    [InlineData("a/b", false)]
    [InlineData("x:y", false)]
    [InlineData("..", false)]
    [InlineData("bad\u0001name", false)]
    public void Share_names_are_validated(string name, bool valid) => Assert.Equal(valid, ShareName.IsValid(name));

    [Fact]
    public void Overlong_share_names_are_rejected() => Assert.False(ShareName.IsValid(new string('a', 81)));

    [Fact]
    public void Share_unc_path_is_built_from_parts() => Assert.Equal(@"\\nw-fs01\HR", ShareName.Unc("nw-fs01", "HR"));

    [Fact]
    public void Valid_text_is_returned_unchanged()
    {
        Assert.Equal("Résumé 😀.docx", PathText.Sanitize("Résumé 😀.docx", out var lossy));
        Assert.False(lossy);
    }

    [Fact]
    public void Unpaired_surrogates_are_replaced_and_reported()
    {
        var result = PathText.Sanitize("a\uD800b\uDC00c", out var lossy);
        Assert.True(lossy);
        Assert.Equal("a\uFFFDb\uFFFDc", result);
    }

    [Theory]
    [InlineData("report.PDF", "pdf")]
    [InlineData("archive.tar.gz", "gz")]
    [InlineData("noext", "")]
    [InlineData(".hidden", "")]
    [InlineData("trailing.", "")]
    public void Extensions_are_lower_case_without_dot(string name, string ext) => Assert.Equal(ext, PathText.Extension(name));

    [Fact]
    public void Child_paths_use_backslashes() => Assert.Equal(@"HR\Payroll", PathText.Child("HR", "Payroll"));
}
