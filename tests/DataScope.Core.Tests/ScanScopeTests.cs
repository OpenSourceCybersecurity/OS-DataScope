using DataScope.Core.Inventory;

namespace DataScope.Core.Tests;

/// <summary>Checks and matching for scan exclusions (D124).</summary>
public class ScanScopeTests
{
    private static readonly Guid Share = Guid.NewGuid();

    private static ScanExclusionRule Folder(string path, ScanExclusionEffect effect) => new(1, ScanExclusionKind.Folder, effect, Share, path, null, "why");

    private static ScanExclusionRule Type(string ext) => new(2, ScanExclusionKind.FileType, ScanExclusionEffect.ListOnly, null, null, ext, "why");

    [Theory]
    [InlineData(@"Archive\2019", @"Archive\2019")]
    [InlineData("/Archive/2019/", @"Archive\2019")]
    [InlineData(@"  \Archive\  ", "Archive")]
    [InlineData("Old Projects", "Old Projects")]
    public void Folder_paths_are_stored_one_way(string input, string expected)
    {
        Assert.True(ScanScope.TryNormalizeFolder(input, out var path, out var error), error);
        Assert.Equal(expected, path);
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"\")]
    [InlineData(@"\\fs01\HR\Archive")]
    [InlineData(@"C:\Shares\HR")]
    [InlineData(@"Archive\*")]
    [InlineData(@"Archive\..\Finance")]
    [InlineData(@"Archive\\2019")]
    [InlineData("Archive\n2019")]
    [InlineData(@"Archive \2019")]
    public void Paths_that_could_hide_more_than_one_folder_or_leave_the_share_are_refused(string input)
    {
        Assert.False(ScanScope.TryNormalizeFolder(input, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void A_path_longer_than_the_limit_is_refused()
    {
        Assert.False(ScanScope.TryNormalizeFolder(new string('a', ScanScope.MaxPathLength + 1), out _, out _));
    }

    [Theory]
    [InlineData(".BAK", "bak")]
    [InlineData("*.tmp", "tmp")]
    [InlineData("old", "old")]
    [InlineData("7z", "7z")]
    public void File_types_are_stored_lower_case_without_the_dot(string input, string expected)
    {
        Assert.True(ScanScope.TryNormalizeExtension(input, out var ext, out _));
        Assert.Equal(expected, ext);
    }

    [Theory]
    [InlineData("")]
    [InlineData("*.*")]
    [InlineData("*")]
    [InlineData(".")]
    [InlineData("tar.gz")]
    [InlineData("b a k")]
    public void Wildcards_and_compound_types_are_refused(string input)
    {
        Assert.False(ScanScope.TryNormalizeExtension(input, out _, out _));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("  ", false)]
    [InlineData("ok", false)]
    [InlineData("Backups of the accounting system", true)]
    public void A_reason_is_required(string? reason, bool ok)
    {
        Assert.Equal(ok, ScanScope.ReasonProblem(reason) is null);
        Assert.NotNull(ScanScope.ReasonProblem(new string('x', ScanScope.MaxReasonLength + 1)));
    }

    [Fact]
    public void A_folder_matches_itself_and_below_ignoring_case_but_not_its_neighbours()
    {
        var scope = new ShareScope([Folder(@"HR\Personal", ScanExclusionEffect.Skip)]);
        Assert.NotNull(scope.Skipped(@"HR\Personal"));
        Assert.NotNull(scope.Skipped(@"hr\personal\2024"));
        Assert.Null(scope.Skipped(@"HR\Personal Files"));
        Assert.Null(scope.Skipped("HR"));
        Assert.Null(scope.Skipped(@"Other\HR\Personal"));
    }

    [Fact]
    public void Contents_are_excluded_by_folder_or_by_type()
    {
        var scope = new ShareScope([Folder(@"Payroll\2019", ScanExclusionEffect.ListOnly), Type("bak")]);
        Assert.Null(scope.Skipped(@"Payroll\2019"));
        Assert.NotNull(scope.ContentExcluded(@"Payroll\2019", "old.csv"));
        Assert.NotNull(scope.ContentExcluded(@"Payroll\2019\Q1", "old.csv"));
        Assert.Null(scope.ContentExcluded("Payroll", "staff.csv"));
        Assert.NotNull(scope.ContentExcluded("Anywhere", "copy.BAK"));
        Assert.Null(scope.ContentExcluded("Anywhere", "copy.bak.txt"));
        Assert.Null(scope.ContentExcluded("Anywhere", "bak"));
        Assert.True(ShareScope.Everything.IsEmpty);
    }
}
