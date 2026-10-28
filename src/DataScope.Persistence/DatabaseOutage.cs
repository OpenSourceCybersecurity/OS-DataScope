using Microsoft.EntityFrameworkCore;

namespace DataScope.Persistence;

/// <summary>
/// Recognises a database that is down or unreachable, so background loops back off and try again instead of stopping
/// the service (A16). EF Core reports a dropped or refused connection as an <see cref="InvalidOperationException"/>
/// ("likely due to a transient failure") around the Npgsql error, so the inner exceptions are checked too.
/// </summary>
public static class DatabaseOutage
{
    public static bool Is(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is DbUpdateException or Npgsql.NpgsqlException or TimeoutException)
            {
                return true;
            }
        }
        return false;
    }
}
