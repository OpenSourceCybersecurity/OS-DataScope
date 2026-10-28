using DataScope.Api.Access;
using DataScope.Api.Auth;
using DataScope.Api.Cleanup;
using DataScope.Api.Endpoints;
using DataScope.Api.Hosting;
using DataScope.Api.Progress;
using DataScope.Api.Reviews;
using DataScope.Api.Risks;
using DataScope.Core.Security;
using DataScope.Persistence;
using DataScope.Persistence.Configuration;
using DataScope.Persistence.Settings;
using System.Security.Authentication;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddDataScopeConfigFile();
builder.Host.UseWindowsServiceIfAvailable();

builder.WebHost.ConfigureKestrel(k => k.AddServerHeader = false);

// On an installed server, HTTPS uses the certificate the installer chose, from LocalMachine\My.
var thumbprint = builder.Configuration["Https:CertificateThumbprint"];
if (!string.IsNullOrWhiteSpace(thumbprint))
{
    if (!OperatingSystem.IsWindows())
    {
        throw new PlatformNotSupportedException("Https:CertificateThumbprint needs the Windows certificate store.");
    }
    var certificate = ServerCertificate.LoadFromMachineStore(thumbprint, DateTimeOffset.UtcNow);
    var port = builder.Configuration.GetValue("Https:Port", 443);
    builder.WebHost.ConfigureKestrel(k => k.ListenAnyIP(port, listen =>
    {
        // Windows sign-in (Negotiate) does not work over HTTP/2.
        listen.Protocols = HttpProtocols.Http1;
        // TLS 1.2 or 1.3 only; Windows picks the cipher suites, which Group Policy can narrow further.
        listen.UseHttps(new HttpsConnectionAdapterOptions { ServerCertificate = certificate, SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 });
    }));
}

builder.Services.AddDataScopePersistence(builder.Configuration);
// Checks in every minute for System health, next to the scanner.
builder.Services.AddServiceHeartbeat(ServiceHeartbeatWriter.Web, ProductVersion.Current);
builder.Services.AddHostedService<DataScope.Api.Health.HeartbeatService>();
// Settings saved on System settings (D123) win over datascope.json; read at start, after each save and every 30 seconds.
builder.Services.AddSystemSettings(builder.Configuration);
builder.Services.AddHostedService<SystemSettingsService>();
builder.Services.AddSingleton<AccessCache>();
builder.Services.AddScoped<AccessService>();
builder.Services.AddScoped<DataScope.Api.People.PersonLookup>();
builder.Services.AddScoped<RiskEngine>();
builder.Services.AddScoped<DataScope.Api.Health.SystemHealth>();
builder.Services.AddScoped<RiskTriage>();
builder.Services.AddOptions<CleanupOptions>().Bind(builder.Configuration.GetSection(CleanupOptions.Section))
    .Validate(o => o.IsValid, "Cleanup:StaleAfterYears must be 1 to 30 and Cleanup:MinDuplicateBytes 1 or more.").ValidateOnStart();
builder.Services.AddScoped<DataCleanup>();
builder.Services.AddScoped<ProgressRecorder>();
builder.Services.AddHostedService<ProgressService>();
builder.Services.AddScoped<CampaignBuilder>();
builder.Services.AddScoped<ReviewActions>();
builder.Services.Configure<DataScopeAuthOptions>(builder.Configuration.GetSection(DataScopeAuthOptions.Section));
builder.Services.AddProblemDetails();
builder.Services.AddDataScopeRateLimits(builder.Configuration);
builder.Services.AddHealthChecks().AddDbContextCheck<DataScopeDbContext>("database");

var authMode = builder.Configuration[$"{DataScopeAuthOptions.Section}:Mode"] ?? "Negotiate";
if (string.Equals(authMode, DevelopmentAuthHandler.SchemeName, StringComparison.OrdinalIgnoreCase))
{
    if (!builder.Environment.IsDevelopment())
    {
        throw new InvalidOperationException("Auth:Mode=Development is only allowed when ASPNETCORE_ENVIRONMENT is Development.");
    }
    builder.Services.AddAuthentication(DevelopmentAuthHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthHandler>(DevelopmentAuthHandler.SchemeName, null);
}
else
{
    builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate();
}
builder.Services.AddTransient<IClaimsTransformation, RoleMappingClaimsTransformation>();

builder.Services.AddAuthorizationBuilder()
    // Default deny: every endpoint needs a signed-in user unless it opts out explicitly.
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
    .AddPolicy(Policies.ManagePlatform, p => p.RequireRole(Roles.PlatformAdmin))
    .AddPolicy(Policies.ReadPosture, p => p.RequireRole(Roles.PlatformAdmin, Roles.SecurityAnalyst, Roles.Auditor, Roles.Viewer))
    .AddPolicy(Policies.ReadAuditLog, p => p.RequireRole(Roles.PlatformAdmin, Roles.Auditor))
    .AddPolicy(Policies.ReadEvidence, p => p.RequireRole(Roles.PlatformAdmin, Roles.SecurityAnalyst, Roles.Auditor))
    .AddPolicy(Policies.ReviewClassification, p => p.RequireRole(Roles.PlatformAdmin, Roles.SecurityAnalyst))
    .AddPolicy(Policies.ManageReviews, p => p.RequireRole(Roles.PlatformAdmin))
    .AddPolicy(Policies.ReadReviews, p => p.RequireRole(Roles.PlatformAdmin, Roles.SecurityAnalyst, Roles.Auditor))
    .AddPolicy(Policies.TriageRisks, p => p.RequireRole(Roles.PlatformAdmin, Roles.SecurityAnalyst))
    .AddPolicy(Policies.ReadActivity, p => p.RequireRole(Roles.PlatformAdmin, Roles.SecurityAnalyst, Roles.Auditor))
    .AddPolicy(Policies.TriageActivity, p => p.RequireRole(Roles.PlatformAdmin, Roles.SecurityAnalyst));

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseDataScopeSecurityHeaders();

// Serve the built web UI (web/dist) when configured. The bundle holds no data;
// every API call behind it is authenticated and authorized.
var webRoot = app.Configuration["Web:RootPath"];
if (!string.IsNullOrWhiteSpace(webRoot))
{
    var full = Path.GetFullPath(webRoot, app.Environment.ContentRootPath);
    if (Directory.Exists(full))
    {
        var files = new PhysicalFileProvider(full);
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
    }
    else
    {
        app.Logger.LogWarning("Web:RootPath {Path} does not exist; the UI will not be served.", full);
    }
}

app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.UseDataScopeAntiForgeryHeader();

app.MapHealthChecks("/health").AllowAnonymous();

var api = app.MapGroup("/api/v1");
api.MapMe();
api.MapServers();
api.MapScanJobs();
api.MapAuditLog();
api.MapInventory();
api.MapClassification();
api.MapDirectory();
api.MapAccess();
api.MapOwners();
api.MapRisks();
api.MapProgress();
api.MapCampaigns();
api.MapMyReviews();
api.MapReviewExemptions();
api.MapReports();
api.MapActivity();
api.MapCleanup();
api.MapSettings();
DataScope.Api.Health.HealthEndpoints.MapHealth(api);
api.MapScanScope();
api.MapExposureMap();
api.MapCompliance();
api.MapCustomRules();
api.MapSearch();
api.MapPersonSearch();

app.Run();

/// <summary>Entry point, public for integration tests.</summary>
public partial class Program;
