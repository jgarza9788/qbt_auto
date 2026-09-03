using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QbitFlow.Infrastructure;
using QbitFlow.Infrastructure.Data;

namespace QbitFlow.Tests.Infrastructure;

/// <summary>Spins up a real migrated SQLite database on a throwaway temp file.</summary>
public sealed class SqliteFixture : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"qbitflow-it-{Guid.NewGuid():N}.db");
    public ServiceProvider Services { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQbitFlowInfrastructure($"Data Source={_dbPath}");
        Services = services.BuildServiceProvider();

        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await Services.DisposeAsync();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* best effort */ }
    }
}
