using Microsoft.EntityFrameworkCore;
using QbitFlow.Core.Domain;

namespace QbitFlow.Infrastructure.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<SourceConnection> SourceConnections => Set<SourceConnection>();

    public DbSet<Rule> Rules => Set<Rule>();
    public DbSet<RuleConditionGroup> RuleConditionGroups => Set<RuleConditionGroup>();
    public DbSet<RuleCondition> RuleConditions => Set<RuleCondition>();
    public DbSet<RuleAction> RuleActions => Set<RuleAction>();

    public DbSet<RunHistory> RunHistory => Set<RunHistory>();
    public DbSet<RunRuleResult> RunRuleResults => Set<RunRuleResult>();
    public DbSet<RunLogEntry> RunLogEntries => Set<RunLogEntry>();
    public DbSet<ScriptRunMarker> ScriptRunMarkers => Set<ScriptRunMarker>();

    public DbSet<MediaItem> MediaItems => Set<MediaItem>();
    public DbSet<MediaFilePath> MediaFilePaths => Set<MediaFilePath>();
    public DbSet<MediaSourceStat> MediaSourceStats => Set<MediaSourceStat>();
    public DbSet<MediaScoreCache> MediaScoreCache => Set<MediaScoreCache>();

    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Store enums as readable text (also lets the flags enum round-trip as "Data, ActionTarget").
        configurationBuilder.Properties<Enum>().HaveConversion<string>();

        // SQLite can't ORDER BY / compare DateTimeOffset — persist as orderable UTC ticks.
        configurationBuilder.Properties<DateTimeOffset>()
            .HaveConversion<DateTimeOffsetToTicksConverter>();
    }

    private sealed class DateTimeOffsetToTicksConverter()
        : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset, long>(
            v => v.UtcTicks,
            v => new DateTimeOffset(v, TimeSpan.Zero));

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<SourceConnection>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Name).HasMaxLength(128);
            e.Property(x => x.OptionsJson).HasDefaultValue("{}");
        });

        b.Entity<Rule>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(128);
            e.HasIndex(x => x.Order);
            e.HasOne(x => x.RootGroup).WithMany().HasForeignKey(x => x.RootGroupId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.Action).WithOne(x => x.Rule!).HasForeignKey<RuleAction>(x => x.RuleId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RuleConditionGroup>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasMany(x => x.Children).WithOne(x => x.ParentGroup!).HasForeignKey(x => x.ParentGroupId).OnDelete(DeleteBehavior.NoAction);
            e.HasMany(x => x.Conditions).WithOne(x => x.Group!).HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RuleCondition>(e => e.HasKey(x => x.Id));
        b.Entity<RuleAction>(e => { e.HasKey(x => x.Id); e.Property(x => x.ParamsJson).HasDefaultValue("{}"); });

        b.Entity<RunHistory>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.StartedUtc);
            e.HasMany(x => x.RuleResults).WithOne(x => x.Run!).HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RunRuleResult>(e => e.HasKey(x => x.Id));

        b.Entity<RunLogEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.RunId, x.Seq });
        });

        b.Entity<ScriptRunMarker>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.RuleId, x.TorrentHash }).IsUnique();
        });

        b.Entity<MediaItem>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.MatchKey);
            e.HasMany(x => x.Files).WithOne(x => x.MediaItem!).HasForeignKey(x => x.MediaItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.SourceStats).WithOne(x => x.MediaItem!).HasForeignKey(x => x.MediaItemId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<MediaFilePath>(e => { e.HasKey(x => x.Id); e.HasIndex(x => x.FileName); });
        b.Entity<MediaSourceStat>(e => { e.HasKey(x => x.Id); e.HasIndex(x => new { x.MediaItemId, x.SourceConnectionId }).IsUnique(); });

        b.Entity<MediaScoreCache>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.QbtInstanceId, x.TorrentHash }).IsUnique();
        });

        b.Entity<AppSetting>(e => e.HasKey(x => x.Key));
    }
}
