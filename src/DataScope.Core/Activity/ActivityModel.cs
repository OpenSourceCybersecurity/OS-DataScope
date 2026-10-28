namespace DataScope.Core.Activity;

/// <summary>
/// How much an event proves. Kept next to every stored event so no screen or report can present an access check or a
/// request as something that happened to a file (D97).
/// </summary>
public enum ActivityCertainty
{
    /// <summary>Windows recorded the operation on the object (4663, 4660, 4670, 1102).</summary>
    Performed,

    /// <summary>Someone asked Windows for a handle (4656). It shows what they asked for, not what they did with it.</summary>
    Requested,

    /// <summary>Windows checked whether someone may use a file through a share (5145). Not proof it was opened or read.</summary>
    AccessCheck,

    /// <summary>Someone connected to a share (5140). Says nothing about which files they touched.</summary>
    ShareSession,
}

public enum ActivityOutcome
{
    Success,

    /// <summary>Windows refused (an audit failure event).</summary>
    Failure,
}

/// <summary>
/// The main thing an event records. One event can carry several rights (all are kept in the access mask); this is the
/// most consequential one. Read, Write and Execute mean List folder, Create in folder and Traverse on a folder, and
/// Windows' file events do not say which, so the stored object kind decides the wording (<see cref="ActivityText"/>).
/// </summary>
public enum ActivityOperation
{
    Read,
    Write,
    Execute,

    /// <summary>A 4663 for DELETE: the file was opened to be deleted or renamed (moving a file also needs DELETE).</summary>
    DeleteOrRename,

    /// <summary>A 4660: Windows recorded that the object was deleted.</summary>
    Deleted,

    PermissionChange,
    OwnerChange,
    AttributeChange,

    /// <summary>Reading attributes, extended attributes or permissions, which Explorer does constantly; not a read of contents.</summary>
    MetadataRead,

    HandleRequest,
    ShareConnect,
    ShareAccessCheck,

    /// <summary>Event 1102: someone cleared the Security log. Events before it may be missing.</summary>
    LogCleared,

    Other,

    /// <summary>The event could not be read. Its raw text is kept as evidence.</summary>
    Unreadable,
}

public enum ActivityObjectKind
{
    /// <summary>Not matched to a file or folder Data Scope has scanned.</summary>
    Unknown,
    File,
    Folder,
}

/// <summary>Where an event's path came from.</summary>
public enum ActivityPathSource
{
    /// <summary>The event names the object.</summary>
    Event,

    /// <summary>The event has no path (4660); it was taken from the earlier event on the same handle (D100).</summary>
    Correlated,

    /// <summary>No path: the event has none and nothing could be correlated, or it is not about a file.</summary>
    None,
}

/// <summary>Plain-language labels shared by the API, the screens and reports.</summary>
public static class ActivityText
{
    public static string Operation(ActivityOperation op, ActivityObjectKind kind) => (op, kind) switch
    {
        (ActivityOperation.Read, ActivityObjectKind.File) => "Read",
        (ActivityOperation.Read, ActivityObjectKind.Folder) => "List folder",
        (ActivityOperation.Read, _) => "Read or list folder",
        (ActivityOperation.Write, ActivityObjectKind.File) => "Write",
        (ActivityOperation.Write, ActivityObjectKind.Folder) => "Create in folder",
        (ActivityOperation.Write, _) => "Write or create in folder",
        (ActivityOperation.Execute, ActivityObjectKind.File) => "Run",
        (ActivityOperation.Execute, ActivityObjectKind.Folder) => "Traverse folder",
        (ActivityOperation.Execute, _) => "Run or traverse",
        (ActivityOperation.DeleteOrRename, _) => "Delete or rename",
        (ActivityOperation.Deleted, _) => "Deleted",
        (ActivityOperation.PermissionChange, _) => "Permission change",
        (ActivityOperation.OwnerChange, _) => "Owner change",
        (ActivityOperation.AttributeChange, _) => "Attribute change",
        (ActivityOperation.MetadataRead, _) => "Read properties",
        (ActivityOperation.HandleRequest, _) => "Open request",
        (ActivityOperation.ShareConnect, _) => "Connected to share",
        (ActivityOperation.ShareAccessCheck, _) => "Share access check",
        (ActivityOperation.LogCleared, _) => "Security log cleared",
        (ActivityOperation.Unreadable, _) => "Unreadable event",
        _ => "Other access",
    };

    public static string Certainty(ActivityCertainty certainty) => certainty switch
    {
        ActivityCertainty.Performed => "Windows recorded the operation.",
        ActivityCertainty.Requested => "Someone asked to open it with these rights. This does not show what they did with it.",
        ActivityCertainty.AccessCheck => "Windows checked access through the share. This is not proof the file was opened or read.",
        _ => "Someone connected to the share. This does not show which files they used.",
    };
}
