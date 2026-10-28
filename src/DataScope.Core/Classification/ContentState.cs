namespace DataScope.Core.Classification;

/// <summary>
/// What happened when the scanner tried to read a file's contents. Anything other than <see cref="Scanned"/> and
/// <see cref="Partial"/> is a coverage gap: the file's sensitivity is unknown, never "clean".
/// </summary>
public enum ContentState
{
    /// <summary>Contents not read yet.</summary>
    NotRead = 0,

    /// <summary>All extractable text was scanned.</summary>
    Scanned,

    /// <summary>Some text was scanned, but a limit stopped the rest (characters, pages or archive entries).</summary>
    Partial,

    /// <summary>A file type Data Scope does not read yet.</summary>
    Unsupported,

    /// <summary>Bigger than the configured limit for its type, so not opened.</summary>
    TooLarge,

    /// <summary>Offline or a cloud placeholder. Never opened, so it is never downloaded or recalled.</summary>
    Offline,

    AccessDenied,

    /// <summary>The file disappeared or was renamed between the walk and the read.</summary>
    NotFound,

    /// <summary>Password-protected or encrypted.</summary>
    Encrypted,

    /// <summary>Damaged, not what its extension says, or tripped a safety limit such as a decompression-bomb check.</summary>
    Unreadable,

    /// <summary>Reading took longer than the per-file time limit.</summary>
    TimedOut,

    /// <summary>Any other I/O error, such as a sharing violation or a network drop.</summary>
    IoError,

    /// <summary>An administrator excluded these contents from reading (D124): listed in the inventory, never opened.</summary>
    Excluded,
}
