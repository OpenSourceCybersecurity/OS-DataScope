using DataScope.Core.Access;

namespace DataScope.Core.Tests.Access;

public class AccessMaskTests
{
    [Theory]
    [InlineData(0x001F01FFu, Rights.FullControl)]
    [InlineData(0x001301BFu, Rights.Modify)]
    [InlineData(0x001200A9u, Rights.Read)]
    [InlineData(0x00120089u, Rights.Read)]
    [InlineData(0x00120116u, Rights.Write)]
    [InlineData(0x00100020u, Rights.None)]
    [InlineData(0x00040000u, Rights.FullControl)]
    [InlineData(0x10000000u, Rights.FullControl)]
    [InlineData(0x80000000u, Rights.Read)]
    [InlineData(0x01000000u, Rights.None)]
    [InlineData(0u, Rights.None)]
    public void Summarises_masks(uint mask, Rights expected) => Assert.Equal(expected, AccessMask.ToRights(mask));

    [Fact]
    public void Expands_generic_rights_to_file_rights()
    {
        Assert.Equal(AccessMask.FileAllAccess, AccessMask.Normalize(AccessMask.GenericAll));
        Assert.Equal(AccessMask.FileGenericRead, AccessMask.Normalize(AccessMask.GenericRead));
        Assert.Equal(0u, AccessMask.Normalize(0x0200_0000));
    }

    [Theory]
    [InlineData(0x001F01FFu, "Full Control")]
    [InlineData(0x001301BFu, "Change")]
    [InlineData(0x001200A9u, "Read")]
    [InlineData(0x00000001u, "Special")]
    public void Names_share_permissions(uint mask, string expected) => Assert.Equal(expected, AccessMask.ShareLabel(mask));

    [Fact]
    public void Labels_rights_in_plain_words()
    {
        Assert.Equal("Full control", AccessMask.Label(Rights.FullControl));
        Assert.Equal("No access", AccessMask.Label(Rights.None));
    }
}
