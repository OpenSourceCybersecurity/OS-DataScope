namespace DataScope.Core.Inventory;

/// <summary>
/// What a managed server stands for at the top of the location tree (D141): a Windows file server whose shares,
/// folders and files are scanned.
/// </summary>
public enum SourceKind
{
    /// <summary>A Windows file server reached over SMB.</summary>
    FileServer,
}
