using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace QbitFlow.Infrastructure.Data;

/// <summary>
/// Design-time factory so `dotnet ef migrations …` works without booting the web host.
/// Uses the QBITFLOW_DB env var if set, else a throwaway local file.
/// </summary>
public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("QBITFLOW_DB") ?? "Data Source=qbitflow.design.db";

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connectionString, b =>
                b.MigrationsAssembly(typeof(AppDbContextFactory).Assembly.GetName().Name))
            .Options;

        return new AppDbContext(options);
    }
}
