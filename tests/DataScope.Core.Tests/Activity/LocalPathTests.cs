using DataScope.Core.Activity;

namespace DataScope.Core.Tests.Activity;

public class LocalPathTests
{
    [Theory]
    [InlineData(@"\??\C:\Shares\Finance", @"C:\Shares\Finance")]
    [InlineData(@"\\?\C:\Shares\Finance\", @"C:\Shares\Finance")]
    [InlineData(@"C:/Shares/Finance/", @"C:\Shares\Finance")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"\??\UNC\nas\data", @"\\nas\data")]
    [InlineData(@"\Device\HarddiskVolume3\Shares", @"\Device\HarddiskVolume3\Shares")]
    [InlineData("-", null)]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void Normalizes_paths_from_events(string? path, string? expected) => Assert.Equal(expected, LocalPath.Normalize(path));

    [Theory]
    [InlineData(@"C:\Shares\Finance", @"C:\Shares\Finance\Budget\a.xlsx", @"Budget\a.xlsx")]
    [InlineData(@"C:\Shares\Finance", @"c:\shares\FINANCE\a.xlsx", "a.xlsx")]
    [InlineData(@"C:\Shares\Finance", @"C:\Shares\Finance", "")]
    [InlineData(@"C:\Shares\Finance\", @"C:\Shares\Finance\", "")]
    [InlineData(@"C:\Shares\Fin", @"C:\Shares\Finance\a.xlsx", null)]
    [InlineData(@"C:\Shares\Finance", @"C:\Shares", null)]
    [InlineData(@"C:\", @"C:\Shares\a.txt", @"Shares\a.txt")]
    [InlineData(@"D:\Data", @"C:\Data\a.txt", null)]
    public void Finds_the_path_inside_a_folder(string root, string path, string? expected) => Assert.Equal(expected, LocalPath.RelativeTo(root, path));

    [Fact]
    public void The_most_specific_share_wins()
    {
        var finance = Guid.NewGuid();
        var audit = Guid.NewGuid();
        var whole = Guid.NewGuid();
        var map = new SharePathMap([(whole, @"D:\"), (finance, @"D:\Shares\Finance"), (audit, @"\??\D:\Shares\Finance\Audit")]);

        Assert.Equal((audit, "2026.xlsx"), map.Match(@"D:\Shares\Finance\Audit\2026.xlsx"));
        Assert.Equal((finance, @"AP\a.pdf"), map.Match(@"D:\Shares\Finance\AP\a.pdf"));
        Assert.Equal((whole, @"Other\b.txt"), map.Match(@"D:\Other\b.txt"));
        Assert.Null(map.Match(@"C:\Windows\system.ini"));
        Assert.Null(map.Match(null));
    }
}
