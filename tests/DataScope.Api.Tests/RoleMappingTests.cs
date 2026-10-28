using System.Security.Claims;
using DataScope.Api.Auth;
using DataScope.Core.Security;
using Microsoft.Extensions.Options;

namespace DataScope.Api.Tests;

/// <summary>Roles from Windows group SIDs. Every SID here is made up.</summary>
public class RoleMappingTests
{
    private const string Admins = "S-1-5-21-40-50-60-3001";
    private const string Analysts = "S-1-5-21-40-50-60-3002";
    private const string Person = "S-1-5-21-40-50-60-1107";

    private sealed class Monitor(DataScopeAuthOptions value) : IOptionsMonitor<DataScopeAuthOptions>
    {
        public DataScopeAuthOptions CurrentValue => value;

        public DataScopeAuthOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<DataScopeAuthOptions, string?> listener) => null;
    }

    private static async Task<ClaimsPrincipal> MapAsync(bool signedIn, params string[] groups)
    {
        var options = new DataScopeAuthOptions
        {
            RoleGroupSids = new() { [Roles.PlatformAdmin] = [Admins], [Roles.SecurityAnalyst] = [Analysts.ToLowerInvariant()] },
        };
        var claims = new List<Claim> { new(ClaimTypes.PrimarySid, Person) };
        claims.AddRange(groups.Select(g => new Claim(ClaimTypes.GroupSid, g)));
        var principal = new ClaimsPrincipal(signedIn ? new ClaimsIdentity(claims, "Negotiate") : new ClaimsIdentity(claims));
        return await new RoleMappingClaimsTransformation(new Monitor(options)).TransformAsync(principal);
    }

    [Fact]
    public async Task A_member_of_a_mapped_group_gets_that_role_and_no_other()
    {
        var person = await MapAsync(true, Admins);
        Assert.True(person.IsInRole(Roles.PlatformAdmin));
        Assert.False(person.IsInRole(Roles.SecurityAnalyst));
    }

    [Fact]
    public async Task Group_SIDs_match_whatever_their_case()
    {
        var person = await MapAsync(true, Analysts);
        Assert.True(person.IsInRole(Roles.SecurityAnalyst));
    }

    [Fact]
    public async Task Someone_in_no_mapped_group_gets_no_role()
    {
        var person = await MapAsync(true, "S-1-5-21-40-50-60-9999");
        Assert.DoesNotContain(Roles.All, person.IsInRole);
    }

    [Fact]
    public async Task A_caller_who_is_not_signed_in_gets_no_role()
    {
        var anonymous = await MapAsync(false, Admins);
        Assert.False(anonymous.IsInRole(Roles.PlatformAdmin));
    }
}
