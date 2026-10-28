using DataScope.Core.Inventory;

namespace DataScope.Scanner.Tests;

public sealed class LabShareEnumeratorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ds-lab-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Each_folder_under_the_server_is_a_share_and_dollar_shares_are_special()
    {
        Directory.CreateDirectory(Path.Join(_root, "fs01", "HR"));
        Directory.CreateDirectory(Path.Join(_root, "fs01", "Finance"));
        Directory.CreateDirectory(Path.Join(_root, "fs01", "C$"));

        var shares = await new LabShareEnumerator(_root).ListAsync("fs01", CancellationToken.None);

        Assert.Equal(["C$", "Finance", "HR"], shares.Select(s => s.Name));
        Assert.Equal(ShareKind.Special, shares[0].Kind);
        Assert.All(shares.Skip(1), s => Assert.Equal(ShareKind.Disk, s.Kind));
    }

    [Fact]
    public async Task Unknown_server_is_unreachable()
    {
        var ex = await Assert.ThrowsAsync<ShareEnumerationException>(() => new LabShareEnumerator(_root).ListAsync("nope", CancellationToken.None));
        Assert.Contains("could not be reached", ex.Message, StringComparison.Ordinal);
    }
}
