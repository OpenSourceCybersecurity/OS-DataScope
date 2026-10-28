namespace DataScope.Core.Access;

/// <summary>What someone can do, summarised the way Windows Explorer describes it.</summary>
public enum Rights
{
    None = 0,
    Read = 1,
    Write = 2,
    Modify = 3,
    FullControl = 4,
}

/// <summary>NTFS and share access-mask arithmetic.</summary>
public static class AccessMask
{
    public const uint ReadData = 0x0001;
    public const uint WriteData = 0x0002;
    public const uint AppendData = 0x0004;
    public const uint Delete = 0x0001_0000;
    public const uint ReadControl = 0x0002_0000;
    public const uint WriteDac = 0x0004_0000;
    public const uint WriteOwner = 0x0008_0000;

    /// <summary>Every standard and file-specific right. Bits outside this (SACL access, MAXIMUM_ALLOWED) are ignored.</summary>
    public const uint FileAllAccess = 0x001F_01FF;

    public const uint FileGenericRead = 0x0012_0089;
    public const uint FileGenericWrite = 0x0012_0116;
    public const uint FileGenericExecute = 0x0012_00A0;

    public const uint GenericAll = 0x1000_0000;
    public const uint GenericExecute = 0x2000_0000;
    public const uint GenericWrite = 0x4000_0000;
    public const uint GenericRead = 0x8000_0000;

    /// <summary>Share permission masks as set by the Advanced Sharing dialog.</summary>
    public const uint ShareRead = 0x0012_00A9;
    public const uint ShareChange = 0x0013_01BF;
    public const uint ShareFull = FileAllAccess;

    /// <summary>Expands generic rights to file rights and drops bits that do not affect file access.</summary>
    public static uint Normalize(uint mask)
    {
        var result = mask & FileAllAccess;
        if ((mask & GenericAll) != 0)
        {
            result |= FileAllAccess;
        }
        if ((mask & GenericRead) != 0)
        {
            result |= FileGenericRead;
        }
        if ((mask & GenericWrite) != 0)
        {
            result |= FileGenericWrite;
        }
        if ((mask & GenericExecute) != 0)
        {
            result |= FileGenericExecute;
        }
        return result;
    }

    /// <summary>
    /// Summarises a mask. Change permissions or take ownership counts as full control, because the holder can grant
    /// themselves anything else.
    /// </summary>
    public static Rights ToRights(uint mask)
    {
        mask = Normalize(mask);
        if ((mask & (WriteDac | WriteOwner)) != 0)
        {
            return Rights.FullControl;
        }
        var write = (mask & (WriteData | AppendData)) != 0;
        if (write && (mask & Delete) != 0)
        {
            return Rights.Modify;
        }
        if (write)
        {
            return Rights.Write;
        }
        return (mask & ReadData) != 0 ? Rights.Read : Rights.None;
    }

    public static string Label(Rights rights) => rights switch
    {
        Rights.FullControl => "Full control",
        Rights.Modify => "Modify",
        Rights.Write => "Write",
        Rights.Read => "Read",
        _ => "No access",
    };

    /// <summary>Label for a share permission mask, using the names from the Advanced Sharing dialog.</summary>
    public static string ShareLabel(uint mask) => Normalize(mask) switch
    {
        FileAllAccess => "Full Control",
        var m when (m & ShareChange) == ShareChange => "Change",
        var m when (m & ShareRead) == ShareRead => "Read",
        _ => "Special",
    };
}
