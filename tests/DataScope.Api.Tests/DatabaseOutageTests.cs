using DataScope.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DataScope.Api.Tests;

/// <summary>Which errors the background loops treat as the database being away (A16), and so back off from instead of stopping.</summary>
public sealed class DatabaseOutageTests
{
    public static TheoryData<Exception> Outages => new()
    {
        new NpgsqlException("connection refused"),
        new DbUpdateException("save failed", new NpgsqlException("terminated")),
        new TimeoutException(),
        // What EF Core throws when a connection is refused or dropped.
        new InvalidOperationException("An exception has been raised that is likely due to a transient failure.", new NpgsqlException("refused")),
        new InvalidOperationException("outer", new InvalidOperationException("middle", new TimeoutException())),
    };

    public static TheoryData<Exception> NotOutages => new()
    {
        new InvalidOperationException("Sequence contains no elements"),
        new ArgumentException("bad argument"),
        new IOException("disk"),
        new UnauthorizedAccessException(),
    };

    [Theory]
    [MemberData(nameof(Outages))]
    public void A_refused_or_dropped_database_connection_is_an_outage(Exception ex) => Assert.True(DatabaseOutage.Is(ex));

    [Theory]
    [MemberData(nameof(NotOutages))]
    public void Program_errors_are_not_mistaken_for_an_outage(Exception ex) => Assert.False(DatabaseOutage.Is(ex));
}
