using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace DataScope.Api.Auth;

/// <summary>
/// Signs every request in as a fixed developer identity. Only registered when
/// the host environment is Development, so it cannot be enabled on a server.
/// </summary>
internal sealed class DevelopmentAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<DataScopeAuthOptions> auth)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Development";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = auth.Value.DevUser;
        var claims = new List<Claim> { new(ClaimTypes.Name, user.Name), new(ClaimTypes.PrimarySid, user.Sid) };
        claims.AddRange(user.Roles.Select(r => new Claim(ClaimTypes.Role, r)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
