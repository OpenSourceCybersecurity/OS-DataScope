using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DataScope.Persistence;

/// <summary>Used only by `dotnet ef` to generate migrations. Needs no real database.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<DataScopeDbContext>
{
    public DataScopeDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<DataScopeDbContext>();
        PersistenceSetup.Configure(options, "Host=localhost;Database=datascope_design");
        return new DataScopeDbContext(options.Options);
    }
}
