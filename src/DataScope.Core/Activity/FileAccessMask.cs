using System.Globalization;
using DataScope.Core.Access;

namespace DataScope.Core.Activity;

/// <summary>File and folder access rights as they appear in Windows audit events (AccessMask).</summary>
public static class FileAccessMask
{
    public const uint ReadData = 0x0001;
    public const uint WriteData = 0x0002;
    public const uint AppendData = 0x0004;
    public const uint ReadEa = 0x0008;
    public const uint WriteEa = 0x0010;
    public const uint Execute = 0x0020;
    public const uint DeleteChild = 0x0040;
    public const uint ReadAttributes = 0x0080;
    public const uint WriteAttributes = 0x0100;
    public const uint Delete = AccessMask.Delete;
    public const uint ReadControl = AccessMask.ReadControl;
    public const uint WriteDac = AccessMask.WriteDac;
    public const uint WriteOwner = AccessMask.WriteOwner;
    public const uint Synchronize = 0x0010_0000;
    public const uint AccessSystemSecurity = 0x0100_0000;

    // The names Windows itself shows in the event's Accesses list, so admins can match them to Event Viewer.
    private static readonly (uint Bit, string Name)[] Names =
    [
        (ReadData, "ReadData (or ListDirectory)"),
        (WriteData, "WriteData (or AddFile)"),
        (AppendData, "AppendData (or AddSubdirectory)"),
        (ReadEa, "ReadEA"),
        (WriteEa, "WriteEA"),
        (Execute, "Execute/Traverse"),
        (DeleteChild, "DeleteChild"),
        (ReadAttributes, "ReadAttributes"),
        (WriteAttributes, "WriteAttributes"),
        (Delete, "DELETE"),
        (ReadControl, "READ_CONTROL"),
        (WriteDac, "WRITE_DAC"),
        (WriteOwner, "WRITE_OWNER"),
        (Synchronize, "SYNCHRONIZE"),
        (AccessSystemSecurity, "ACCESS_SYS_SEC"),
        (AccessMask.GenericAll, "GENERIC_ALL"),
        (AccessMask.GenericExecute, "GENERIC_EXECUTE"),
        (AccessMask.GenericWrite, "GENERIC_WRITE"),
        (AccessMask.GenericRead, "GENERIC_READ"),
    ];

    /// <summary>The rights in a mask, in Windows' order, with any unnamed bits as one hexadecimal value.</summary>
    public static IReadOnlyList<string> Describe(uint mask)
    {
        var names = new List<string>();
        var rest = mask;
        foreach (var (bit, name) in Names)
        {
            if ((mask & bit) != 0)
            {
                names.Add(name);
                rest &= ~bit;
            }
        }
        if (rest != 0)
        {
            names.Add("0x" + rest.ToString("X", CultureInfo.InvariantCulture));
        }
        return names;
    }

    /// <summary>The most consequential operation a mask carries (generic rights expanded first).</summary>
    public static ActivityOperation Operation(uint mask)
    {
        var m = AccessMask.Normalize(mask) | (mask & AccessSystemSecurity);
        return m switch
        {
            _ when (m & Delete) != 0 => ActivityOperation.DeleteOrRename,
            _ when (m & WriteDac) != 0 => ActivityOperation.PermissionChange,
            _ when (m & WriteOwner) != 0 => ActivityOperation.OwnerChange,
            _ when (m & (WriteData | AppendData)) != 0 => ActivityOperation.Write,
            _ when (m & ReadData) != 0 => ActivityOperation.Read,
            _ when (m & Execute) != 0 => ActivityOperation.Execute,
            _ when (m & (WriteAttributes | WriteEa)) != 0 => ActivityOperation.AttributeChange,
            _ when (m & (ReadAttributes | ReadEa | ReadControl)) != 0 => ActivityOperation.MetadataRead,
            _ => ActivityOperation.Other,
        };
    }
}
