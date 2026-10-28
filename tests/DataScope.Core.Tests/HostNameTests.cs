using DataScope.Core.Inventory;

namespace DataScope.Core.Tests;

public class HostNameTests
{
    [Theory]
    [InlineData("FS01", "fs01")]
    [InlineData("  nw-fs01.northwind.local ", "nw-fs01.northwind.local")]
    [InlineData("a", "a")]
    public void Accepts_and_normalizes_host_names(string input, string expected)
    {
        Assert.True(HostName.TryNormalize(input, out var normalized, out var error));
        Assert.Equal(expected, normalized);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"\\evil\share")]
    [InlineData("fs01/../../etc")]
    [InlineData("fs01:445")]
    [InlineData("10.20.1.21")]
    [InlineData("::1")]
    [InlineData("fs01 .corp")]
    [InlineData("-fs01")]
    [InlineData("fs01-")]
    [InlineData("fs..corp")]
    [InlineData("<script>")]
    [InlineData("*.corp.local")]
    [InlineData("averyveryverylongnetbiosname")]
    public void Rejects_unsafe_or_invalid_names(string? input)
    {
        Assert.False(HostName.TryNormalize(input, out var normalized, out var error));
        Assert.Null(normalized);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Rejects_names_longer_than_dns_allows()
    {
        var name = string.Join('.', Enumerable.Repeat(new string('a', 63), 5));
        Assert.False(HostName.TryNormalize(name, out _, out _));
    }
}
