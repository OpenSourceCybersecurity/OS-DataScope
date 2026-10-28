using DataScope.Core.Access;

namespace DataScope.Core.Tests.Access;

public class SecurityDescriptorTests
{
    private const AceInheritance Both = AceInheritance.ObjectInherit | AceInheritance.ContainerInherit;

    private static SecurityDescriptor Sample() => new(
        SecurityDescriptor.DaclProtected,
        "S-1-5-32-544",
        "S-1-5-21-1-2-3-513",
        [
            new Ace(AceKind.Deny, 1, AceInheritance.None, AccessMask.Delete, "S-1-5-21-1-2-3-1105"),
            new Ace(AceKind.Allow, 0, Both, AccessMask.FileAllAccess, "S-1-5-18"),
            new Ace(AceKind.ConditionalAllow, 9, AceInheritance.ObjectInherit | AceInheritance.InheritOnly, AccessMask.GenericRead, "S-1-1-0"),
            new Ace(AceKind.Allow, 0, Both | AceInheritance.Inherited, AccessMask.FileGenericRead, "S-1-5-11"),
        ]);

    [Fact]
    public void Round_trips()
    {
        var sd = Sample();
        var parsed = SecurityDescriptorCodec.Parse(SecurityDescriptorCodec.Build(sd));
        Assert.Equal(sd.Owner, parsed.Owner);
        Assert.Equal(sd.Group, parsed.Group);
        Assert.True(parsed.IsProtected);
        Assert.True(parsed.HasExplicitEntries);
        Assert.Equal(sd.Dacl!, parsed.Dacl!);
    }

    [Fact]
    public void Reads_a_hand_encoded_descriptor()
    {
        // Self-relative, DACL present, no owner or group, one ACE: allow 0x001200A9 to Everyone (S-1-1-0), OI|CI.
        byte[] bytes =
        [
            1, 0, 0x04, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 20, 0, 0, 0,
            2, 0, 28, 0, 1, 0, 0, 0,
            0, 3, 20, 0, 0xA9, 0x00, 0x12, 0x00, 1, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0,
        ];
        var sd = SecurityDescriptorCodec.Parse(bytes);
        var ace = Assert.Single(sd.Dacl!);
        Assert.Equal(AceKind.Allow, ace.Kind);
        Assert.Equal(WellKnownSid.Everyone, ace.Sid);
        Assert.Equal(AccessMask.ShareRead, ace.Mask);
        Assert.Equal("This folder, subfolders and files", ace.AppliesTo);
        Assert.Null(sd.Owner);
    }

    [Fact]
    public void Distinguishes_no_dacl_from_an_empty_dacl()
    {
        var none = SecurityDescriptorCodec.Parse(SecurityDescriptorCodec.Build(new SecurityDescriptor(0, null, null, null)));
        var empty = SecurityDescriptorCodec.Parse(SecurityDescriptorCodec.Build(new SecurityDescriptor(0, null, null, [])));
        Assert.Null(none.Dacl);
        Assert.NotNull(empty.Dacl);
        Assert.Empty(empty.Dacl);
    }

    [Fact]
    public void Reads_object_entries_and_keeps_unknown_types()
    {
        var guid = new byte[16];
        var sid = Sid.ToBinary("S-1-5-11");
        // Object allow (type 5) with an object-type GUID present (flag 1).
        var objectAce = new List<byte> { 5, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 };
        objectAce.AddRange(guid);
        objectAce.AddRange(sid);
        objectAce[2] = (byte)objectAce.Count;
        // Mandatory label (type 0x11): ignored for access, kept as evidence.
        var label = new List<byte> { 0x11, 0, 0, 0, 1, 0, 0, 0 };
        label.AddRange(Sid.ToBinary("S-1-16-8192"));
        label[2] = (byte)label.Count;
        var body = objectAce.Concat(label).ToArray();
        var acl = new List<byte> { 2, 0, (byte)(8 + body.Length), 0, 2, 0, 0, 0 };
        acl.AddRange(body);
        var bytes = new List<byte> { 1, 0, 0x04, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 20, 0, 0, 0 };
        bytes.AddRange(acl);

        var sd = SecurityDescriptorCodec.Parse(bytes.ToArray());
        Assert.Equal(AceKind.ObjectAllow, sd.Dacl![0].Kind);
        Assert.Equal("S-1-5-11", sd.Dacl[0].Sid);
        Assert.Equal(AceKind.Other, sd.Dacl[1].Kind);
        Assert.Equal(0x11, sd.Dacl[1].RawType);
    }

    public static TheoryData<string, byte[]> Malformed() => new()
    {
        { "empty", [] },
        { "short header", [1, 0, 4, 0x80] },
        { "revision 2", [2, 0, 0x04, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0] },
        { "absolute form", [1, 0, 0x04, 0x00, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 20, 0, 0, 0, 2, 0, 8, 0, 0, 0, 0, 0] },
        { "owner past end", [1, 0, 0x04, 0x80, 200, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0] },
        { "dacl past end", [1, 0, 0x04, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0, 0] },
        { "acl size too big", [1, 0, 0x04, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 20, 0, 0, 0, 2, 0, 0xFF, 0, 0, 0, 0, 0] },
        { "ace count too big", [1, 0, 0x04, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 20, 0, 0, 0, 2, 0, 8, 0, 5, 0, 0, 0] },
        {
            "ace size zero",
            [1, 0, 0x04, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 20, 0, 0, 0, 2, 0, 16, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]
        },
        {
            "ace runs past acl",
            [1, 0, 0x04, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 20, 0, 0, 0, 2, 0, 16, 0, 1, 0, 0, 0, 0, 0, 64, 0, 0, 0, 0, 0]
        },
        {
            "ace sid truncated",
            [1, 0, 0x04, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 20, 0, 0, 0, 2, 0, 20, 0, 1, 0, 0, 0, 0, 0, 12, 0, 1, 0, 0, 0, 1, 5, 0, 0]
        },
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public void Rejects_malformed_descriptors(string name, byte[] bytes)
    {
        Assert.False(SecurityDescriptorCodec.TryParse(bytes, out _), name);
        Assert.Throws<MalformedSecurityDescriptorException>(() => SecurityDescriptorCodec.Parse(bytes));
    }

    [Fact]
    public void Measures_a_descriptor_inside_a_larger_buffer()
    {
        var bytes = SecurityDescriptorCodec.Build(Sample());
        var padded = new byte[bytes.Length + 4000];
        bytes.CopyTo(padded, 0);

        Assert.Equal(bytes.Length, SecurityDescriptorCodec.SelfRelativeLength(padded));
        Assert.Equal(bytes.Length, SecurityDescriptorCodec.SelfRelativeLength(bytes));
        Assert.Equal(20, SecurityDescriptorCodec.SelfRelativeLength(SecurityDescriptorCodec.Build(new SecurityDescriptor(0, null, null, null))));
    }

    [Fact]
    public void Measuring_a_truncated_or_foreign_buffer_returns_zero()
    {
        var bytes = SecurityDescriptorCodec.Build(Sample());
        Assert.Equal(0, SecurityDescriptorCodec.SelfRelativeLength(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.Equal(0, SecurityDescriptorCodec.SelfRelativeLength(new byte[4096]));
        Assert.Equal(0, SecurityDescriptorCodec.SelfRelativeLength([1, 0, 4]));
    }

    [Fact]
    public void Rejects_oversized_input() =>
        Assert.Throws<MalformedSecurityDescriptorException>(() => SecurityDescriptorCodec.Parse(new byte[SecurityDescriptorCodec.MaxBytes + 1]));

    [Fact]
    public void Random_corruption_never_escapes_as_another_exception()
    {
        var valid = SecurityDescriptorCodec.Build(Sample());
        var random = new Random(20261009);
        for (var i = 0; i < 5000; i++)
        {
            var bytes = (byte[])valid.Clone();
            var flips = 1 + random.Next(6);
            for (var f = 0; f < flips; f++)
            {
                bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
            }
            var cut = random.Next(4) == 0 ? bytes[..random.Next(bytes.Length)] : bytes;
            // Either parses or reports malformed input; never IndexOutOfRange, Overflow or similar.
            _ = SecurityDescriptorCodec.TryParse(cut, out _);
        }
    }

    [Fact]
    public void Detects_non_canonical_order()
    {
        Assert.True(Sample().IsCanonical);
        var allowThenDeny = new SecurityDescriptor(0, null, null,
        [
            new Ace(AceKind.Allow, 0, Both, AccessMask.FileAllAccess, "S-1-1-0"),
            new Ace(AceKind.Deny, 1, Both, AccessMask.WriteData, "S-1-1-0"),
        ]);
        var explicitAfterInherited = new SecurityDescriptor(0, null, null,
        [
            new Ace(AceKind.Allow, 0, Both | AceInheritance.Inherited, AccessMask.FileAllAccess, "S-1-1-0"),
            new Ace(AceKind.Allow, 0, Both, AccessMask.FileAllAccess, "S-1-5-11"),
        ]);
        Assert.False(allowThenDeny.IsCanonical);
        Assert.False(explicitAfterInherited.IsCanonical);
    }

    [Theory]
    [InlineData(AceInheritance.None, "This folder only")]
    [InlineData(AceInheritance.ContainerInherit, "This folder and subfolders")]
    [InlineData(AceInheritance.ObjectInherit, "This folder and files")]
    [InlineData(AceInheritance.ObjectInherit | AceInheritance.InheritOnly, "Files only")]
    [InlineData(AceInheritance.ContainerInherit | AceInheritance.ObjectInherit | AceInheritance.InheritOnly, "Subfolders and files only")]
    [InlineData(AceInheritance.ContainerInherit | AceInheritance.NoPropagateInherit, "This folder and subfolders (one level)")]
    public void Describes_scope_like_explorer(AceInheritance flags, string expected) =>
        Assert.Equal(expected, new Ace(AceKind.Allow, 0, flags, AccessMask.FileGenericRead, "S-1-1-0").AppliesTo);
}
