using System.Net;
using System.Net.Http.Json;
using DataScope.Core.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;

namespace DataScope.Api.Tests;

[Trait("Category", "Database")]
public sealed class RateLimitTests : IClassFixture<TestDatabase>, IDisposable
{
    private readonly ApiFactory _factory;
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _limited;

    public RateLimitTests(TestDatabase db)
    {
        _factory = new ApiFactory(db);
        _limited = _factory.WithWebHostBuilder(b => b
            .UseSetting("RateLimits:RequestsPerMinute", "5")
            .UseSetting("RateLimits:AnonymousRequestsPerMinute", "3")
            .UseSetting("RateLimits:ReportsPerFiveMinutes", "2"));
    }

    public void Dispose()
    {
        _limited.Dispose();
        _factory.Dispose();
    }

    private HttpClient As(string? roles, string sid) => ApiFactory.Configure(_limited.CreateClient(), roles, sid);

    [Fact]
    public async Task One_person_is_slowed_down_without_affecting_others()
    {
        var busy = As(Roles.Viewer, "S-1-5-21-9-9-9-2001");
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await busy.GetAsync("/api/v1/me")).StatusCode);
        }
        var limited = await busy.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(int.Parse(limited.Headers.GetValues("Retry-After").Single(), System.Globalization.CultureInfo.InvariantCulture) >= 1);
        var problem = await limited.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Contains("Wait a minute", problem!.Detail, StringComparison.Ordinal);

        // Someone else, the health check and the UI's files are unaffected.
        Assert.Equal(HttpStatusCode.OK, (await As(Roles.Viewer, "S-1-5-21-9-9-9-2002").GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await busy.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task Requests_before_sign_in_are_limited_by_address()
    {
        var anonymous = As(null, "unused");
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/me")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, (await anonymous.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Report_exports_have_their_own_tighter_limit()
    {
        var auditor = As(Roles.Auditor, "S-1-5-21-9-9-9-2003");
        Assert.Equal(HttpStatusCode.OK, (await auditor.GetAsync("/api/v1/reports/scan-coverage?format=csv")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await auditor.GetAsync("/api/v1/reports/scan-coverage?format=html")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await auditor.GetAsync("/api/v1/reports/scan-coverage?format=csv")).StatusCode);
        // Other API calls still work for the same person.
        Assert.Equal(HttpStatusCode.OK, (await auditor.GetAsync("/api/v1/reports")).StatusCode);
    }
}
