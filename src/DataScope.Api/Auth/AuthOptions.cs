namespace DataScope.Api.Auth;

public sealed class DataScopeAuthOptions
{
    public const string Section = "Auth";

    /// <summary>"Negotiate" (Windows Integrated Authentication, the default) or "Development".</summary>
    public string Mode { get; set; } = "Negotiate";

    /// <summary>Identity used when Mode is Development. Refused outside the Development environment.</summary>
    public DevUserOptions DevUser { get; set; } = new();

    /// <summary>App role -> AD group SIDs. SIDs survive group renames, so names are not accepted.</summary>
    public Dictionary<string, string[]> RoleGroupSids { get; set; } = [];
}

public sealed class DevUserOptions
{
    public string Name { get; set; } = @"DEV\developer";
    public string Sid { get; set; } = "S-1-5-21-1000000000-1000000000-1000000000-1001";
    public string[] Roles { get; set; } = [];
}
