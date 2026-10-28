using DataScope.Core.Access;

namespace DataScope.Core.Tests.Access;

public class SidTests
{
    [Theory]
    [InlineData("S-1-1-0")]
    [InlineData("S-1-5-32-544")]
    [InlineData("S-1-5-21-3623811015-3361044348-30300820-1013")]
    [InlineData("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464")]
    [InlineData("S-1-0x123456789ABC-1")]
    public void Round_trips_through_binary(string sid)
    {
        var binary = Sid.ToBinary(sid);
        Assert.Equal(sid, Sid.FromBinary(binary));
        Assert.True(Sid.IsValid(sid));
    }

    [Fact]
    public void Reads_the_documented_binary_layout()
    {
        // S-1-5-32-544: revision 1, 2 sub-authorities, authority 5 (big-endian), 32 and 544 (little-endian).
        byte[] bytes = [1, 2, 0, 0, 0, 0, 0, 5, 32, 0, 0, 0, 0x20, 0x02, 0, 0];
        Assert.Equal("S-1-5-32-544", Sid.FromBinary(bytes));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("S-1")]
    [InlineData("S-2-5-32")]
    [InlineData("S-1-5--1")]
    [InlineData("S-1-5-32-abc")]
    [InlineData("S-1-5-4294967296")]
    [InlineData("S-1-5-+1")]
    [InlineData("S-1-5-1-2-3-4-5-6-7-8-9-10-11-12-13-14-15-16")]
    [InlineData("s-1-5-32-544")]
    public void Rejects_malformed_strings(string? text) => Assert.False(Sid.IsValid(text));

    [Fact]
    public void Rejects_truncated_or_invalid_binary()
    {
        Assert.False(Sid.TryRead([1, 2, 0, 0, 0, 0, 0, 5, 32, 0, 0, 0], out _, out _));
        Assert.False(Sid.TryRead([2, 0, 0, 0, 0, 0, 0, 5], out _, out _));
        Assert.False(Sid.TryRead([1, 16, 0, 0, 0, 0, 0, 5], out _, out _));
        Assert.Throws<FormatException>(() => Sid.FromBinary([1, 1, 0, 0, 0, 0, 0, 5, 1, 0, 0, 0, 99]));
    }

    [Fact]
    public void Splits_domain_and_rid()
    {
        Assert.Equal("S-1-5-21-1-2-3", Sid.AccountDomain("S-1-5-21-1-2-3-513"));
        Assert.Null(Sid.AccountDomain("S-1-5-32-544"));
        Assert.Null(Sid.AccountDomain("S-1-5-21-1-2-3"));
        Assert.Equal(513u, Sid.Rid("S-1-5-21-1-2-3-513"));
    }

    [Theory]
    [InlineData("S-1-1-0", true)]
    [InlineData("S-1-5-11", true)]
    [InlineData("S-1-5-32-545", true)]
    [InlineData("S-1-5-21-1-2-3-513", true)]
    [InlineData("S-1-5-21-1-2-3-512", false)]
    [InlineData("S-1-5-21-1-2-3-1105", false)]
    [InlineData("S-1-5-32-544", false)]
    public void Knows_broad_groups(string sid, bool broad) => Assert.Equal(broad, WellKnownSid.IsBroad(sid));

    [Fact]
    public void Names_well_known_groups()
    {
        Assert.Equal("Everyone", WellKnownSid.NameOf(WellKnownSid.Everyone));
        Assert.Equal(@"BUILTIN\Users", WellKnownSid.NameOf(WellKnownSid.Users));
        Assert.Null(WellKnownSid.NameOf("S-1-5-21-1-2-3-1105"));
        Assert.True(WellKnownSid.IsAdministrative("S-1-5-21-1-2-3-512"));
        Assert.True(WellKnownSid.IsAdministrative(WellKnownSid.LocalSystem));
    }
}
