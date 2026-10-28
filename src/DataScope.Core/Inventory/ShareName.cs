namespace DataScope.Core.Inventory;

public enum ShareKind
{
    /// <summary>An ordinary disk share. Eligible for scanning.</summary>
    Disk,

    /// <summary>Administrative or hidden system share (C$, ADMIN$). Never scanned; scan the real share instead.</summary>
    Special,

    /// <summary>Printers, IPC$, devices. Not file data.</summary>
    Other,
}

public static class ShareName
{
    public const int MaxLength = 80;
    private static readonly System.Buffers.SearchValues<char> Forbidden = System.Buffers.SearchValues.Create("\\/:*?\"<>|");

    /// <summary>Checks a share name reported by a server before it is stored or used to build a UNC path.</summary>
    public static bool IsValid(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Length <= MaxLength
        && name.AsSpan().IndexOfAny(Forbidden) < 0
        && !name.Any(char.IsControl)
        && name is not "." and not "..";

    /// <summary>The comparison key: Windows share names are case-insensitive.</summary>
    public static string Key(string name) => name.ToUpperInvariant();

    public static string Unc(string hostname, string share) => $@"\\{hostname}\{share}";
}
