using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QbitFlow.Core.Abstractions;
using QbitFlow.Infrastructure.Data;
using QbitFlow.Infrastructure.Secrets;

namespace QbitFlow.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Registers the qbit-flow database (SQLite via EF Core) as both a pooled factory (for concurrent
    /// use inside a pipeline run) and a scoped <see cref="AppDbContext"/> (for request handlers).
    /// Puts the database into WAL mode with a busy-timeout so the scheduler, request handlers, and a
    /// running pipeline can share it without "database is locked" errors.
    /// </summary>
    public static IServiceCollection AddQbitFlowInfrastructure(
        this IServiceCollection services, string connectionString)
    {
        connectionString = Normalize(connectionString);
        EnsureSqliteDirectory(connectionString);
        EnableWal(connectionString);

        services.AddDbContextFactory<AppDbContext>(options =>
            options.UseSqlite(connectionString, sqlite =>
            {
                sqlite.MigrationsAssembly(typeof(AppDbContext).Assembly.GetName().Name);
                sqlite.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
            }));

        services.AddScoped<AppDbContext>(sp =>
            sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

        services.AddSingleton<ISecretProtector>(_ => SecretProtectorFactory.Create());
        services.AddScoped<IScriptRunMarkerStore, Data.ScriptRunMarkerStore>();
        services.AddScoped<Config.AppSettingStore>();
        services.AddScoped<Config.ConfigImportService>();
        services.AddSingleton<Config.SourceConnectionReader>();

        return services;
    }

    private static string Normalize(string connectionString)
    {
        var b = new SqliteConnectionStringBuilder(connectionString)
        {
            Pooling = true,
            DefaultTimeout = 30,   // maps to sqlite3_busy_timeout
        };
        return b.ToString();
    }

    private static void EnsureSqliteDirectory(string connectionString)
    {
        try
        {
            var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
            if (string.IsNullOrWhiteSpace(dataSource) || dataSource == ":memory:")
                return;

            var dir = Path.GetDirectoryName(Path.GetFullPath(dataSource));
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
        }
        catch
        {
            // Best effort; a bad path surfaces clearly when EF opens the connection.
        }
    }

    private static void EnableWal(string connectionString)
    {
        try
        {
            if (new SqliteConnectionStringBuilder(connectionString).DataSource is ":memory:" or "")
                return;

            using var conn = new SqliteConnection(connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // Non-fatal — the DB still works without WAL, just with coarser locking.
        }
    }
}
