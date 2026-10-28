using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DataScope.Installer;

/// <summary>Builds a Windows command line that programs split back into the same arguments (CommandLineToArgvW rules).</summary>
public static class WindowsCommandLine
{
    public static string Join(IEnumerable<string> arguments) => string.Join(" ", arguments.Select(Quote));

    public static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            return argument;
        }
        var quoted = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            // Backslashes count only before a quote: double them there, and escape the quote itself.
            quoted.Append('\\', c == '"' ? (backslashes * 2) + 1 : backslashes);
            quoted.Append(c);
            backslashes = 0;
        }
        // The closing quote follows, so trailing backslashes are doubled too.
        quoted.Append('\\', backslashes * 2);
        return quoted.Append('"').ToString();
    }
}
