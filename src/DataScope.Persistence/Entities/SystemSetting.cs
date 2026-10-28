namespace DataScope.Persistence.Entities;

/// <summary>
/// A setting an administrator changed on the System settings screen (D123). <see cref="Key"/> is a configuration path
/// from <see cref="Core.Settings.SystemSettings"/>; <see cref="Value"/> is JSON: a string, null for an optional setting
/// left empty, or an array of strings for a list. Deleting the row puts the setting back to the file or built-in value.
/// </summary>
public sealed class SystemSetting
{
    public required string Key { get; set; }
    public required string Value { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public required string UpdatedBySid { get; set; }
    public required string UpdatedByName { get; set; }
}
