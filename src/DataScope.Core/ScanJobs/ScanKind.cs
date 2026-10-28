namespace DataScope.Core.ScanJobs;

/// <summary>How deep a scan goes (D130).</summary>
public enum ScanKind
{
    /// <summary>Lists every folder, file and permission, and reads only the files that are new or changed.</summary>
    Quick,

    /// <summary>
    /// Lists everything and reads every file again, changed or not, with more room per file: longer time limits,
    /// bigger files and more pages.
    /// </summary>
    Full,
}
