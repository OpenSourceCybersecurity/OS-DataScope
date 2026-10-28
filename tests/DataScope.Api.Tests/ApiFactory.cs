using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataScope.Api.Tests;

public sealed class ApiFactory(TestDatabase database) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:DataScope", database.ConnectionString);
        builder.UseSetting("Web:RootPath", "");
        // Test classes make hundreds of calls as one person within a minute; RateLimitTests sets real limits.
        builder.UseSetting("RateLimits:RequestsPerMinute", "1000000");
        builder.UseSetting("RateLimits:AnonymousRequestsPerMinute", "1000000");
        builder.UseSetting("RateLimits:ReportsPerFiveMinutes", "1000000");
        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);
            // Negotiate needs Kestrel's connection features, which TestServer lacks; hide it behind the test scheme.
            services.AddSingleton<IAuthenticationSchemeProvider, WithoutNegotiateSchemeProvider>();
        });
    }

    /// <summary>
    /// A client signed in as a user with the given roles, or anonymous when roles is null.
    /// Like the web UI, it sends the anti-forgery header unless told not to.
    /// </summary>
    public HttpClient ClientAs(string? roles, string sid = "S-1-5-21-1-2-3-1001", string name = "NW\\tester", bool antiForgeryHeader = true) =>
        Configure(CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }), roles, sid, name, antiForgeryHeader);

    /// <summary>Signs a client in as <see cref="ClientAs"/> does; for clients from a factory derived with WithWebHostBuilder.</summary>
    public static HttpClient Configure(HttpClient client, string? roles, string sid = "S-1-5-21-1-2-3-1001", string name = "NW\\tester", bool antiForgeryHeader = true)
    {
        if (antiForgeryHeader)
        {
            client.DefaultRequestHeaders.Add("X-DataScope-Client", "tests");
        }
        if (roles is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(TestAuthHandler.SchemeName, $"{sid};{name};{roles}");
        }
        return client;
    }
}

/// <summary>Test-only scheme: "Authorization: Test sid;name;Role1,Role2". No header means anonymous.</summary>
internal sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith(SchemeName + " ", StringComparison.Ordinal))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }
        var parts = header[(SchemeName.Length + 1)..].Split(';');
        var claims = new List<Claim> { new(ClaimTypes.PrimarySid, parts[0]), new(ClaimTypes.Name, parts[1]) };
        claims.AddRange(parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(r => new Claim(ClaimTypes.Role, r)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}

/// <summary>Scheme provider that hides Negotiate so production auth wiring stays untouched in tests.</summary>
internal sealed class WithoutNegotiateSchemeProvider(IOptions<AuthenticationOptions> options) : AuthenticationSchemeProvider(options)
{
    public override async Task<IEnumerable<AuthenticationScheme>> GetRequestHandlerSchemesAsync() =>
        (await base.GetRequestHandlerSchemesAsync()).Where(s => s.Name != NegotiateDefaults.AuthenticationScheme);

    public override async Task<IEnumerable<AuthenticationScheme>> GetAllSchemesAsync() =>
        (await base.GetAllSchemesAsync()).Where(s => s.Name != NegotiateDefaults.AuthenticationScheme);

    public override Task<AuthenticationScheme?> GetSchemeAsync(string name) =>
        name == NegotiateDefaults.AuthenticationScheme ? Task.FromResult<AuthenticationScheme?>(null) : base.GetSchemeAsync(name);
}
