using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace DataScope.Api.Hosting;

/// <summary>Request limits per signed-in person (section <c>RateLimits</c>). Generous for people, firm for runaway scripts.</summary>
public sealed class RateLimitOptions
{
    public const string Section = "RateLimits";

    /// <summary>API calls per person per minute. The UI makes a few per screen and refreshes once a minute.</summary>
    public int RequestsPerMinute { get; set; } = 600;

    /// <summary>API calls per minute from one address before sign-in completes (Windows sign-in starts with one).</summary>
    public int AnonymousRequestsPerMinute { get; set; } = 300;

    /// <summary>Report exports per person per five minutes; each can read every file record.</summary>
    public int ReportsPerFiveMinutes { get; set; } = 20;

    public void Validate()
    {
        if (RequestsPerMinute < 1 || AnonymousRequestsPerMinute < 1 || ReportsPerFiveMinutes < 1)
        {
            throw new InvalidOperationException($"{Section} values must be 1 or more.");
        }
    }
}

internal static partial class RateLimits
{
    public const string Reports = "reports";

    public static IServiceCollection AddDataScopeRateLimits(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RateLimitOptions>().Bind(configuration.GetSection(RateLimitOptions.Section)).Validate(o =>
        {
            o.Validate();
            return true;
        }).ValidateOnStart();
        return services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = async (context, ct) =>
            {
                var http = context.HttpContext;
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry))
                {
                    http.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }
                LogRejected(http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("DataScope.RateLimits"), Partition(http), http.Request.Path);
                await Results.Problem(
                    statusCode: StatusCodes.Status429TooManyRequests,
                    title: "Too many requests",
                    detail: "Data Scope is limiting how fast one person can make requests. Wait a minute and try again.").ExecuteAsync(http);
            };
            // Only the API is limited; the UI's static files and the health check are not.
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
            {
                if (!http.Request.Path.StartsWithSegments("/api"))
                {
                    return RateLimitPartition.GetNoLimiter("not-api");
                }
                var options = Options(http);
                var key = Partition(http);
                var limit = key.StartsWith("ip:", StringComparison.Ordinal) ? options.AnonymousRequestsPerMinute : options.RequestsPerMinute;
                return RateLimitPartition.GetFixedWindowLimiter($"{key}|{limit}", _ => Window(limit, TimeSpan.FromMinutes(1)));
            });
            o.AddPolicy(Reports, http =>
            {
                var limit = Options(http).ReportsPerFiveMinutes;
                return RateLimitPartition.GetFixedWindowLimiter($"{Partition(http)}|{limit}", _ => Window(limit, TimeSpan.FromMinutes(5)));
            });
        });
    }

    private static RateLimitOptions Options(HttpContext http) => http.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value;

    private static FixedWindowRateLimiterOptions Window(int limit, TimeSpan window) =>
        new() { PermitLimit = limit, Window = window, QueueLimit = 0, AutoReplenishment = true };

    /// <summary>The person (by SID) once signed in; otherwise the client address.</summary>
    private static string Partition(HttpContext http)
    {
        if (http.User.Identity?.IsAuthenticated == true)
        {
            var id = http.User.FindFirstValue(ClaimTypes.PrimarySid) ?? http.User.FindFirstValue(ClaimTypes.PrimaryGroupSid) ?? http.User.Identity.Name;
            if (!string.IsNullOrEmpty(id))
            {
                return $"user:{id}";
            }
        }
        return $"ip:{http.Connection.RemoteIpAddress}";
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rate limit reached for {Partition} on {Path}.")]
    private static partial void LogRejected(ILogger logger, string partition, string path);
}
