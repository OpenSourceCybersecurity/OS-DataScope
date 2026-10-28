using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DataScope.Installer;

public enum OutputKind
{
    Plain,

    /// <summary>"==> Copying programs": the step the installer is on.</summary>
    Step,

    /// <summary>"[OK  ] Memory: 8 GB": one result of the checks it runs first.</summary>
    CheckPassed,
    CheckWarning,
    CheckFailed,
}

/// <summary>Reads what Install-DataScope.ps1 prints, to show the step it is on and why it stopped.</summary>
public static class InstallOutput
{
    private static readonly Regex Check = new(@"^\s*\[(OK|WARN|FAIL)\s*\]\s*(.*)$", RegexOptions.CultureInvariant);

    public static (OutputKind Kind, string Text) Classify(string line)
    {
        if (line.StartsWith("==> ", StringComparison.Ordinal))
        {
            return (OutputKind.Step, line.Substring(4).Trim());
        }
        var check = Check.Match(line);
        if (check.Success)
        {
            var kind = check.Groups[1].Value switch
            {
                "OK" => OutputKind.CheckPassed,
                "WARN" => OutputKind.CheckWarning,
                _ => OutputKind.CheckFailed,
            };
            return (kind, check.Groups[2].Value.Trim());
        }
        return (OutputKind.Plain, line);
    }

    /// <summary>
    /// The reason in what Windows PowerShell wrote to its error output when the script stopped, without the script's
    /// path in front ("C:\...\Install-DataScope.ps1 : ") and the position lines after it. Null when there is none.
    /// </summary>
    public static string? Reason(IEnumerable<string> errorLines)
    {
        var first = errorLines.Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (first is null)
        {
            return null;
        }
        var marker = first.IndexOf(".ps1 : ", StringComparison.OrdinalIgnoreCase);
        return marker >= 0 ? first.Substring(marker + ".ps1 : ".Length).Trim() : first;
    }
}
