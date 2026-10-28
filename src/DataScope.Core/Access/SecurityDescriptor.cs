using System.Buffers.Binary;

namespace DataScope.Core.Access;

public enum AceKind
{
    Allow,
    Deny,

    /// <summary>Allow or deny that only applies when a condition holds (Dynamic Access Control). Data Scope cannot evaluate the condition.</summary>
    ConditionalAllow,
    ConditionalDeny,

    /// <summary>Allow or deny scoped to an object type. Not used by NTFS; evaluated like a plain entry and flagged.</summary>
    ObjectAllow,
    ObjectDeny,

    /// <summary>Audit, alarm, label or unknown entry types. Ignored for access but kept as evidence.</summary>
    Other,
}

[Flags]
public enum AceInheritance : byte
{
    None = 0,
    ObjectInherit = 0x01,
    ContainerInherit = 0x02,
    NoPropagateInherit = 0x04,
    InheritOnly = 0x08,
    Inherited = 0x10,
}

/// <summary>One access control entry, as stored by Windows.</summary>
/// <param name="RawType">The ACE type byte, kept so unusual entries stay interpretable.</param>
/// <param name="Sid">Trustee. Null only for entry types this parser does not decode.</param>
public sealed record Ace(AceKind Kind, byte RawType, AceInheritance Flags, uint Mask, string? Sid)
{
    public bool IsInherited => Flags.HasFlag(AceInheritance.Inherited);
    public bool IsDeny => Kind is AceKind.Deny or AceKind.ConditionalDeny or AceKind.ObjectDeny;
    public bool IsAllow => Kind is AceKind.Allow or AceKind.ConditionalAllow or AceKind.ObjectAllow;
    public bool IsConditional => Kind is AceKind.ConditionalAllow or AceKind.ConditionalDeny;

    /// <summary>Applies to the folder (or file, or share) the ACL is on.</summary>
    public bool AppliesToObject => !Flags.HasFlag(AceInheritance.InheritOnly);

    /// <summary>Passed down to files directly inside a folder.</summary>
    public bool AppliesToChildFiles => Flags.HasFlag(AceInheritance.ObjectInherit);

    /// <summary>Plain-language scope, matching the "Applies to" column in Windows Explorer.</summary>
    public string AppliesTo
    {
        get
        {
            var self = AppliesToObject;
            var folders = Flags.HasFlag(AceInheritance.ContainerInherit);
            var files = Flags.HasFlag(AceInheritance.ObjectInherit);
            var text = (self, folders, files) switch
            {
                (true, true, true) => "This folder, subfolders and files",
                (true, true, false) => "This folder and subfolders",
                (true, false, true) => "This folder and files",
                (true, false, false) => "This folder only",
                (false, true, true) => "Subfolders and files only",
                (false, true, false) => "Subfolders only",
                (false, false, true) => "Files only",
                _ => "Nothing (inherit-only, no targets)",
            };
            return Flags.HasFlag(AceInheritance.NoPropagateInherit) && (folders || files) ? text + " (one level)" : text;
        }
    }
}

/// <summary>
/// A parsed self-relative security descriptor. A null <see cref="Dacl"/> means "no DACL", which Windows treats as
/// full access for everyone; an empty list means nobody has access.
/// </summary>
public sealed record SecurityDescriptor(ushort Control, string? Owner, string? Group, IReadOnlyList<Ace>? Dacl)
{
    public const ushort DaclPresent = 0x0004;
    public const ushort DaclProtected = 0x1000;
    public const ushort SelfRelative = 0x8000;

    /// <summary>Inheritance from the parent folder is turned off.</summary>
    public bool IsProtected => (Control & DaclProtected) != 0;

    public bool HasExplicitEntries => Dacl?.Any(a => !a.IsInherited) == true;

    /// <summary>
    /// Windows evaluates entries in stored order. Tools always write explicit denies, then explicit allows, then
    /// inherited entries; anything else was written by a script or an old tool and can surprise people.
    /// </summary>
    public bool IsCanonical
    {
        get
        {
            if (Dacl is null)
            {
                return true;
            }
            var seenInherited = false;
            var seenExplicitAllow = false;
            foreach (var ace in Dacl.Where(a => a.Kind != AceKind.Other))
            {
                if (ace.IsInherited)
                {
                    seenInherited = true;
                    continue;
                }
                if (seenInherited || (ace.IsDeny && seenExplicitAllow))
                {
                    return false;
                }
                seenExplicitAllow |= ace.IsAllow;
            }
            return true;
        }
    }
}

public sealed class MalformedSecurityDescriptorException(string message) : FormatException(message);

/// <summary>
/// Reads and writes self-relative security descriptors (the binary format Windows returns for files, folders and
/// shares). Every offset and length is checked against the buffer; malformed input throws
/// <see cref="MalformedSecurityDescriptorException"/> and never reads out of bounds.
/// </summary>
public static class SecurityDescriptorCodec
{
    /// <summary>Windows caps an ACL at 64 KB, which bounds the number of entries.</summary>
    public const int MaxBytes = 64 * 1024 * 3;

    private const int HeaderSize = 20;

    public static SecurityDescriptor Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length > MaxBytes)
        {
            throw new MalformedSecurityDescriptorException("Security descriptor is larger than Windows allows.");
        }
        if (data.Length < HeaderSize || data[0] != 1)
        {
            throw new MalformedSecurityDescriptorException("Not a revision 1 security descriptor.");
        }
        var control = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
        if ((control & SecurityDescriptor.SelfRelative) == 0)
        {
            throw new MalformedSecurityDescriptorException("Security descriptor is not in self-relative form.");
        }
        var owner = ReadSidAt(data, BinaryPrimitives.ReadUInt32LittleEndian(data[4..]), "owner");
        var group = ReadSidAt(data, BinaryPrimitives.ReadUInt32LittleEndian(data[8..]), "group");
        var daclOffset = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
        IReadOnlyList<Ace>? dacl = null;
        if ((control & SecurityDescriptor.DaclPresent) != 0 && daclOffset != 0)
        {
            dacl = ParseAcl(data, daclOffset);
        }
        return new SecurityDescriptor(control, owner, group, dacl);
    }

    public static bool TryParse(ReadOnlySpan<byte> data, out SecurityDescriptor? descriptor)
    {
        try
        {
            descriptor = Parse(data);
            return true;
        }
        catch (MalformedSecurityDescriptorException)
        {
            descriptor = null;
            return false;
        }
    }

    /// <summary>
    /// The number of bytes a self-relative descriptor occupies at the start of a larger buffer: the end of its
    /// furthest owner, group, SACL or DACL. Zero when the header or a component is outside the buffer.
    /// </summary>
    public static int SelfRelativeLength(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize || data[0] != 1)
        {
            return 0;
        }
        var end = HeaderSize;
        foreach (var at in new[] { 4, 8 })
        {
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);
            if (offset == 0)
            {
                continue;
            }
            if (offset >= data.Length || !Sid.TryRead(data[(int)offset..], out _, out var length))
            {
                return 0;
            }
            end = Math.Max(end, (int)offset + length);
        }
        foreach (var at in new[] { 12, 16 })
        {
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);
            if (offset == 0)
            {
                continue;
            }
            if (offset > data.Length - 8)
            {
                return 0;
            }
            var size = BinaryPrimitives.ReadUInt16LittleEndian(data[((int)offset + 2)..]);
            if (offset + size > data.Length)
            {
                return 0;
            }
            end = Math.Max(end, (int)offset + size);
        }
        return end;
    }

    private static string? ReadSidAt(ReadOnlySpan<byte> data, uint offset, string what)
    {
        if (offset == 0)
        {
            return null;
        }
        if (offset < HeaderSize || offset >= data.Length || !Sid.TryRead(data[(int)offset..], out var sid, out _))
        {
            throw new MalformedSecurityDescriptorException($"The {what} SID is outside the descriptor or invalid.");
        }
        return sid;
    }

    private static List<Ace> ParseAcl(ReadOnlySpan<byte> data, uint offset)
    {
        if (offset < HeaderSize || offset > data.Length - 8)
        {
            throw new MalformedSecurityDescriptorException("The DACL is outside the descriptor.");
        }
        var acl = data[(int)offset..];
        var revision = acl[0];
        var size = BinaryPrimitives.ReadUInt16LittleEndian(acl[2..]);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(acl[4..]);
        if (revision is not (2 or 4) || size < 8 || size > acl.Length || count > (size - 8) / 8)
        {
            throw new MalformedSecurityDescriptorException("The DACL header is invalid.");
        }
        acl = acl[..size];
        var aces = new List<Ace>(count);
        var position = 8;
        for (var i = 0; i < count; i++)
        {
            if (position > acl.Length - 4)
            {
                throw new MalformedSecurityDescriptorException("An access entry runs past the end of the DACL.");
            }
            var type = acl[position];
            var flags = (AceInheritance)(acl[position + 1] & 0x1F);
            var aceSize = BinaryPrimitives.ReadUInt16LittleEndian(acl[(position + 2)..]);
            if (aceSize < 8 || aceSize % 4 != 0 || position + aceSize > acl.Length)
            {
                throw new MalformedSecurityDescriptorException("An access entry has an invalid size.");
            }
            aces.Add(ParseAce(acl.Slice(position, aceSize), type, flags));
            position += aceSize;
        }
        return aces;
    }

    private static Ace ParseAce(ReadOnlySpan<byte> ace, byte type, AceInheritance flags)
    {
        var mask = BinaryPrimitives.ReadUInt32LittleEndian(ace[4..]);
        var (kind, sidOffset) = type switch
        {
            0x00 => (AceKind.Allow, 8),
            0x01 => (AceKind.Deny, 8),
            0x09 => (AceKind.ConditionalAllow, 8),
            0x0A => (AceKind.ConditionalDeny, 8),
            0x05 or 0x0B => (type == 0x05 ? AceKind.ObjectAllow : AceKind.ConditionalAllow, ObjectSidOffset(ace)),
            0x06 or 0x0C => (type == 0x06 ? AceKind.ObjectDeny : AceKind.ConditionalDeny, ObjectSidOffset(ace)),
            _ => (AceKind.Other, -1),
        };
        if (sidOffset < 0)
        {
            return new Ace(AceKind.Other, type, flags, mask, ace.Length >= 16 && Sid.TryRead(ace[8..], out var maybe, out _) ? maybe : null);
        }
        if (sidOffset >= ace.Length || !Sid.TryRead(ace[sidOffset..], out var sid, out _))
        {
            throw new MalformedSecurityDescriptorException("An access entry has an invalid SID.");
        }
        return new Ace(kind, type, flags, mask, sid);
    }

    private static int ObjectSidOffset(ReadOnlySpan<byte> ace)
    {
        if (ace.Length < 12)
        {
            throw new MalformedSecurityDescriptorException("An object access entry is too short.");
        }
        var objectFlags = BinaryPrimitives.ReadUInt32LittleEndian(ace[8..]);
        return 12 + ((objectFlags & 1) != 0 ? 16 : 0) + ((objectFlags & 2) != 0 ? 16 : 0);
    }

    /// <summary>
    /// Writes a self-relative descriptor with plain, conditional (empty condition) or other entries. Used by the
    /// development lab and by tests; production descriptors always come from Windows.
    /// </summary>
    public static byte[] Build(SecurityDescriptor sd)
    {
        var owner = sd.Owner is null ? [] : Sid.ToBinary(sd.Owner);
        var group = sd.Group is null ? [] : Sid.ToBinary(sd.Group);
        byte[] dacl = [];
        if (sd.Dacl is { } entries)
        {
            var body = new List<byte>();
            foreach (var ace in entries)
            {
                var sid = Sid.ToBinary(ace.Sid ?? throw new ArgumentException("Every entry needs a SID.", nameof(sd)));
                var type = ace.RawType;
                var size = 8 + sid.Length;
                var header = new byte[8];
                header[0] = type;
                header[1] = (byte)ace.Flags;
                BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2), (ushort)size);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), ace.Mask);
                body.AddRange(header);
                body.AddRange(sid);
            }
            dacl = new byte[8 + body.Count];
            dacl[0] = 2;
            BinaryPrimitives.WriteUInt16LittleEndian(dacl.AsSpan(2), (ushort)dacl.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(dacl.AsSpan(4), (ushort)entries.Count);
            body.CopyTo(dacl, 8);
        }
        var result = new byte[HeaderSize + owner.Length + group.Length + dacl.Length];
        result[0] = 1;
        var control = (ushort)(sd.Control | SecurityDescriptor.SelfRelative | (sd.Dacl is null ? 0 : SecurityDescriptor.DaclPresent));
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(2), control);
        var at = HeaderSize;
        if (owner.Length > 0)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)at);
            owner.CopyTo(result, at);
            at += owner.Length;
        }
        if (group.Length > 0)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), (uint)at);
            group.CopyTo(result, at);
            at += group.Length;
        }
        if (dacl.Length > 0)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16), (uint)at);
            dacl.CopyTo(result, at);
        }
        return result;
    }

    /// <summary>Raw ACE type byte for a kind, for building descriptors.</summary>
    public static byte TypeOf(AceKind kind) => kind switch
    {
        AceKind.Allow => 0x00,
        AceKind.Deny => 0x01,
        AceKind.ConditionalAllow => 0x09,
        AceKind.ConditionalDeny => 0x0A,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), "Only plain and conditional entries can be built."),
    };
}
