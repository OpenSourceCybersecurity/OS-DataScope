using DataScope.Installer;

namespace DataScope.Installer.Tests;

public class WindowsCommandLineTests
{
    [Theory]
    [InlineData("simple", "simple")]
    [InlineData("YOURDOMAIN\\svc-datascope$", "YOURDOMAIN\\svc-datascope$")]
    [InlineData("", "\"\"")]
    [InlineData("C:\\Program Files\\Data Scope", "\"C:\\Program Files\\Data Scope\"")]
    [InlineData("C:\\Temp Folder\\", "\"C:\\Temp Folder\\\\\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("back\\\"quote", "\"back\\\\\\\"quote\"")]
    public void Quotes_arguments_so_windows_splits_them_back_unchanged(string argument, string expected)
    {
        Assert.Equal(expected, WindowsCommandLine.Quote(argument));
    }

    [Fact]
    public void Joined_arguments_split_back_into_the_same_list()
    {
        string[] arguments = ["-File", "C:\\Users\\a b\\Install-DataScope.ps1", "-HostName", "", "x\\", "q\"uote", "tab\there"];
        Assert.Equal(arguments, Split(WindowsCommandLine.Join(arguments)));
    }

    // CommandLineToArgvW's rules, for checking the quoting from the other side.
    private static List<string> Split(string commandLine)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        var any = false;
        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (c == '\\')
            {
                var count = 0;
                while (i < commandLine.Length && commandLine[i] == '\\')
                {
                    count++;
                    i++;
                }
                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    current.Append('\\', count / 2);
                    if (count % 2 == 1)
                    {
                        current.Append('"');
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else
                {
                    current.Append('\\', count);
                    i--;
                }
                any = true;
            }
            else if (c == '"')
            {
                inQuotes = !inQuotes;
                any = true;
            }
            else if (!inQuotes && (c == ' ' || c == '\t'))
            {
                if (any)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    any = false;
                }
            }
            else
            {
                current.Append(c);
                any = true;
            }
        }
        if (any)
        {
            result.Add(current.ToString());
        }
        return result;
    }
}
