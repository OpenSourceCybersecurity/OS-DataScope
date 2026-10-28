namespace DataScope.Api.Hosting;

internal static class SecurityHeaders
{
    /// <summary>
    /// The UI's policy: its own scripts, styles and fonts only, no inline code, no plugins, no framing.
    /// The UI ships its own fonts, so nothing needs to leave the network.
    /// </summary>
    public const string ContentPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self' data:; connect-src 'self'; " +
        "object-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";

    /// <summary>Strict headers for an intranet app: no framing, no third-party origins, and no caching of API answers.</summary>
    public static IApplicationBuilder UseDataScopeSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var h = context.Response.Headers;
            h.XContentTypeOptions = "nosniff";
            h.XFrameOptions = "DENY";
            h["Referrer-Policy"] = "no-referrer";
            h["Cross-Origin-Opener-Policy"] = "same-origin";
            h["Cross-Origin-Resource-Policy"] = "same-origin";
            h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=(), clipboard-read=()";
            h.ContentSecurityPolicy = ContentPolicy;
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                // File names, people and permissions: browsers and proxies must not keep a copy.
                h.CacheControl = "no-store";
                h.Pragma = "no-cache";
            }
            await next();
        });
}
