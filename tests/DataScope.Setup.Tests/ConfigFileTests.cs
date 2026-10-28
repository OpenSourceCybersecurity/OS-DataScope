namespace DataScope.Setup.Tests;

public sealed class ConfigFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ds-config-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string PathFor(string name = "datascope.json") => Path.Combine(_dir, name);

    [Fact]
    public void Missing_file_starts_empty_and_saves_nested_values()
    {
        var config = ConfigFile.Load(PathFor());
        Assert.False(config.Exists);
        config.Set("Host=db", "ConnectionStrings", "DataScope");
        config.Set(8443, "Https", "Port");
        config.Save();

        var again = ConfigFile.Load(PathFor());
        Assert.Equal("Host=db", again.GetString("ConnectionStrings", "DataScope"));
        Assert.Equal(8443, again.GetInt("Https", "Port"));
        Assert.Null(again.GetString("Https", "Missing"));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Keeps_settings_it_does_not_touch()
    {
        File.WriteAllText(PathFor(), """
            {
              // added by an administrator
              "Scanner": { "MaxDepth": 12 },
              "Https": { "Port": 443, },
            }
            """);
        var config = ConfigFile.Load(PathFor());
        config.Set(9443, "Https", "Port");
        config.Save();

        var text = File.ReadAllText(PathFor());
        Assert.Contains("\"MaxDepth\": 12", text, StringComparison.Ordinal);
        Assert.Equal(9443, ConfigFile.Load(PathFor()).GetInt("Https", "Port"));
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    public void Refuses_a_file_that_is_not_an_object(string json)
    {
        File.WriteAllText(PathFor(), json);
        var ex = Assert.Throws<SetupException>(() => ConfigFile.Load(PathFor()));
        Assert.Contains("not a JSON object", ex.Message, StringComparison.Ordinal);
    }
}
