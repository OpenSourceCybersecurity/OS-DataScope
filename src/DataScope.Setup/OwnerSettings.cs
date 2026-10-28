using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DataScope.Setup;

/// <summary>
/// The database owner's connection lives in its own file, next to datascope.json in a folder only administrators and
/// SYSTEM can open. The services never read it: they use the runtime login, which cannot change the schema.
/// </summary>
internal static class OwnerSettings
{
    public const string FolderName = "setup-only";
    public const string FileName = "database-owner.json";
    public static readonly string[] Key = ["ConnectionStrings", "DataScopeOwner"];

    public static string PathFor(string configPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath))!, FolderName, FileName);

    public static ConfigFile Load(CommandLine line, ConfigFile config) =>
        ConfigFile.Load(line.Optional("owner-config") ?? PathFor(config.Path));

    /// <summary>Saves the file after (re)locking its folder to administrators and SYSTEM.</summary>
    public static void Save(ConfigFile owner)
    {
        AdminOnlyFolder.Ensure(Path.GetDirectoryName(Path.GetFullPath(owner.Path))!);
        owner.Save();
    }
}

/// <summary>A folder only administrators and SYSTEM (on Windows), or only this user (elsewhere), can open.</summary>
internal static class AdminOnlyFolder
{
    public static void Ensure(string path)
    {
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsWindows())
        {
            LockDown(new DirectoryInfo(path));
        }
        else
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>Replaces the folder's permissions: no inherited entries, full control for Administrators and SYSTEM only.</summary>
    [SupportedOSPlatform("windows")]
    public static void LockDown(DirectoryInfo folder)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in AllowedSids)
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }
        folder.SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<SecurityIdentifier> AllowedSids =>
    [
        new(WellKnownSidType.BuiltinAdministratorsSid, null),
        new(WellKnownSidType.LocalSystemSid, null),
    ];
}
