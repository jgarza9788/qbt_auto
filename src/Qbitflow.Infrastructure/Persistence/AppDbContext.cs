using Microsoft.EntityFrameworkCore;
using Qbitflow.Core.Domain;

namespace Qbitflow.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Instance> Instances => Set<Instance>();
    public DbSet<StoragePathConfig> StoragePaths => Set<StoragePathConfig>();
    public DbSet<Rule> Rules => Set<Rule>();
    public DbSet<RunRecord> RunRecords => Set<RunRecord>();
    public DbSet<AppSettings> AppSettings => Set<AppSettings>();
    public DbSet<User> Users => Set<User>();
    public DbSet<PathMappingRule> PathMappingRules => Set<PathMappingRule>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Instance>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.SourceType).HasConversion<string>();
        });

        modelBuilder.Entity<StoragePathConfig>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<Rule>(e =>
        {
            e.HasIndex(x => x.Priority);
        });

        modelBuilder.Entity<RunRecord>(e =>
        {
            e.HasIndex(x => x.RuleId);
            e.HasIndex(x => x.StartedAt);
            e.Property(x => x.Outcome).HasConversion<string>();
        });

        modelBuilder.Entity<AppSettings>(e =>
        {
            e.ToTable("AppSettings");
            e.HasData(new AppSettings { Id = 1 });
        });

        modelBuilder.Entity<User>(e =>
        {
            e.HasIndex(x => x.Username).IsUnique();
        });
    }
}
