using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DataScope.Persistence;

public static class PersistenceSetup
{
    public const string ConnectionStringName = "DataScope";
    public const string MigrationsTable = "schema_migrations";

    public static void Configure(DbContextOptionsBuilder options, string connectionString) =>
        options
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(MigrationsTable))
            .UseSnakeCaseNamingConvention();

    /// <summary>
    /// Registers the database. The connection string comes from configuration
    /// (user secrets in development, environment or protected config in
    /// production) and is never committed to the repository.
    /// </summary>
    public static IServiceCollection AddDataScopePersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:{ConnectionStringName} is not set. See docs/DEVELOPMENT.md for how to configure it.");
        }
        services.AddDbContext<DataScopeDbContext>(o => Configure(o, connectionString));
        services.AddScoped<AuditLogWriter>();
        services.AddScoped<ScanJobStore>();
        services.AddScoped<InventoryStore>();
        services.AddScoped<ServerStore>();
        services.AddScoped<ClassificationStore>();
        services.AddScoped<ScanScopeStore>();
        services.AddScoped<DirectoryStore>();
        services.AddScoped<PermissionStore>();
        services.AddScoped<NotificationStore>();
        services.AddScoped<ReviewReminders>();
        services.AddScoped<RetentionStore>();
        services.AddScoped<ActivityStore>();
        services.AddScoped<ActivityAlertStore>();
        services.AddScoped<UnusualActivityStore>();
        services.AddSingleton(TimeProvider.System);
        return services;
    }
}
