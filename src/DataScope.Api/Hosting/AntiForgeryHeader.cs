namespace DataScope.Api.Hosting;

internal static class AntiForgeryHeader
{
    /// <summary>Header the web UI sends on every API call that changes something.</summary>
    public const string Name = "X-DataScope-Client";

    /// <summary>
    /// Browsers send Windows sign-in automatically, so another intranet page could
    /// make a signed-in user's browser POST to the API (cross-site request forgery).
    /// A plain form can't set a custom header, and a cross-origin script that sets
    /// one needs a CORS preflight, which this API never grants. So any state-changing
    /// API request without the header is refused. Runs after authentication, so
    /// anonymous callers still get 401.
    /// </summary>
    public static IApplicationBuilder UseDataScopeAntiForgeryHeader(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var request = context.Request;
            var safe = HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method);
            if (!safe && request.Path.StartsWithSegments("/api") && string.IsNullOrWhiteSpace(request.Headers[Name]))
            {
                await Results.Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Missing request header",
                    detail: $"Requests that change data must include the {Name} header.").ExecuteAsync(context);
                return;
            }
            await next();
        });
}
