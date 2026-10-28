namespace DataScope.Setup.Tests;

public sealed class CommandLineTests
{
    private static readonly HashSet<string> Flags = new(["rotate-password"], StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Parses_options_flags_and_repeats()
    {
        var line = CommandLine.Parse(["Configure", "--role", "Viewer=S-1-5-32-545", "--ROLE", "Auditor=S-1-5-32-544", "--rotate-password", "--port", "5433"], Flags);
        Assert.Equal("configure", line.Command);
        Assert.Equal(2, line.All("role").Count);
        Assert.True(line.Flag("rotate-password"));
        Assert.Equal(5433, line.GetInt("port", 5432, 1, 65535));
        Assert.Equal("localhost", line.Get("host", "localhost"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("--host x")]
    [InlineData("database stray")]
    [InlineData("database --host")]
    [InlineData("database --")]
    public void Rejects_malformed_command_lines(string args) =>
        Assert.Throws<UsageException>(() => CommandLine.Parse(args.Split(' ', StringSplitOptions.RemoveEmptyEntries), Flags));

    [Theory]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("-1")]
    [InlineData("12ab")]
    public void Rejects_out_of_range_numbers(string value)
    {
        var line = CommandLine.Parse(["database", "--port", value], Flags);
        Assert.Throws<UsageException>(() => line.GetInt("port", 5432, 1, 65535));
    }

    [Fact]
    public void Rejects_unknown_and_repeated_single_options()
    {
        var line = CommandLine.Parse(["database", "--hots", "x"], Flags);
        Assert.Throws<UsageException>(() => line.RejectUnknown("host"));
        var twice = CommandLine.Parse(["database", "--host", "a", "--host", "b"], Flags);
        Assert.Throws<UsageException>(() => twice.Optional("host"));
    }
}
