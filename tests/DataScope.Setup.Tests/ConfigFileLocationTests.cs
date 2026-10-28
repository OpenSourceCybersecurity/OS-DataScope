using DataScope.Persistence.Configuration;
using Microsoft.Extensions.Configuration;

namespace DataScope.Setup.Tests;

[Collection(nameof(EnvironmentVariableTests))]
public sealed class ConfigFileLocationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ds-location-").FullName;

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(DataScopeConfigFile.PathVariable, null);
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Services_read_the_settings_file_named_by_the_override()
    {
        var path = Path.Combine(_dir, "custom.json");
        File.WriteAllText(path, """{ "Https": { "Port": 9443 } }""");
        Environment.SetEnvironmentVariable(DataScopeConfigFile.PathVariable, path);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Https:Port"] = "443" })
            .AddDataScopeConfigFile()
            .Build();
        Assert.Equal(path, DataScopeConfigFile.ResolvePath());
        Assert.Equal("9443", config["Https:Port"]);
    }

    [Fact]
    public void A_missing_file_is_not_an_error()
    {
        Environment.SetEnvironmentVariable(DataScopeConfigFile.PathVariable, Path.Combine(_dir, "absent.json"));
        var config = new ConfigurationBuilder().AddDataScopeConfigFile().Build();
        Assert.Null(config["Https:Port"]);
    }

    [Fact]
    public void Default_location_is_under_ProgramData_on_Windows_only()
    {
        Environment.SetEnvironmentVariable(DataScopeConfigFile.PathVariable, null);
        var path = DataScopeConfigFile.ResolvePath();
        if (OperatingSystem.IsWindows())
        {
            Assert.EndsWith(Path.Combine("DataScope", "config", "datascope.json"), path, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Null(path);
        }
    }
}
