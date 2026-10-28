using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace DataScope.Installer;

/// <summary>Unzips the package carried by the setup program, and finds the folder with Install-DataScope.ps1 in it.</summary>
public static class PackageExtractor
{
    public const string InstallScript = "Install-DataScope.ps1";

    /// <summary>
    /// Unzips into <paramref name="destination"/>, refusing any entry that would land outside it. Returns the package
    /// folder. <paramref name="progress"/> gets the fraction done, from 0 to 1.
    /// </summary>
    public static string Extract(Stream zip, string destination, Action<double>? progress = null)
    {
        var root = Path.GetFullPath(destination);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            ? root
            : root + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true))
        {
            var total = Math.Max(1, archive.Entries.Count);
            var done = 0;
            foreach (var entry in archive.Entries)
            {
                var target = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"The package has a file that would be written outside its folder: {entry.FullName}");
                }
                // Windows PowerShell's Compress-Archive (used when a package is signed) writes \ between folder names.
                if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || entry.FullName.EndsWith("\\", StringComparison.Ordinal))
                {
                    Directory.CreateDirectory(target);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using var input = entry.Open();
                    using var output = File.Create(target);
                    input.CopyTo(output);
                }
                progress?.Invoke(++done / (double)total);
            }
        }
        return FindPackageFolder(root)
            ?? throw new InvalidDataException($"The package has no {InstallScript}.");
    }

    /// <summary>The folder itself, or its one subfolder (a zip holds DataScope-&lt;version&gt;\), with the install script.</summary>
    public static string? FindPackageFolder(string folder)
    {
        if (File.Exists(Path.Combine(folder, InstallScript)))
        {
            return folder;
        }
        return Directory.GetDirectories(folder).FirstOrDefault(d => File.Exists(Path.Combine(d, InstallScript)));
    }
}
