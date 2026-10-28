using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace DataScope.Api.Auth;

/// <summary>Adds Data Scope role claims based on the caller's Windows group SIDs.</summary>
internal sealed class RoleMappingClaimsTransformation(IOptionsMonitor<DataScopeAuthOptions> options) : IClaimsTransformation
{
    private const string MappedMarker = "datascope:roles-mapped";

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true || principal.HasClaim(c => c.Type == MappedMarker))
        {
            return Task.FromResult(principal);
        }
        var groups = principal.FindAll(ClaimTypes.GroupSid).Select(c => c.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var claims = new List<Claim> { new(MappedMarker, "1") };
        foreach (var (role, sids) in options.CurrentValue.RoleGroupSids)
        {
            if (sids.Any(groups.Contains) && !principal.IsInRole(role))
            {
                claims.Add(new Claim(ClaimTypes.Role, role));
            }
        }
        var clone = principal.Clone();
        clone.AddIdentity(new ClaimsIdentity(claims));
        return Task.FromResult(clone);
    }
}
