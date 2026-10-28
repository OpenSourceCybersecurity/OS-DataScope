using System.Security.Claims;

namespace DataScope.Api.Auth;

/// <summary>Who is making a request, as recorded in the audit log.</summary>
internal sealed record Actor(string Sid, string Name)
{
    public static Actor From(ClaimsPrincipal user) => new(
        user.FindFirstValue(ClaimTypes.PrimarySid) ?? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown",
        user.Identity?.Name ?? "unknown");
}
