using Microsoft.EntityFrameworkCore;
using Qbitflow.Core.Domain;
using Qbitflow.Infrastructure.Persistence;

namespace Qbitflow.Infrastructure.Config;

public class ConfigPortabilityService(AppDbContext db) : IConfigPortabilityService
{
    public async Task<string> ExportConfigAsync(ConfigFormat format, CancellationToken ct = default)
    {
        var instances = await db.Instances.AsNoTracking().OrderBy(i => i.Name).ToListAsync(ct);
        var storagePaths = await db.StoragePaths.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct);
        var settings = await db.AppSettings.AsNoTracking().SingleAsync(s => s.Id == 1, ct);

        var dto = new ConfigExportDto
        {
            Instances = instances.Select(i => new InstanceDto
            {
                Name = i.Name,
                SourceType = i.SourceType.ToString(),
                BaseUrl = i.BaseUrl,
                Enabled = i.Enabled,
                TimeoutSeconds = i.TimeoutSeconds,
                VerifySsl = i.VerifySsl,
                ExtraConfigJson = i.ExtraConfigJson
            }).ToList(),
            StoragePaths = storagePaths.Select(s => new StoragePathDto
            {
                Name = s.Name,
                Path = s.Path,
                Enabled = s.Enabled,
                FolderSizeScanIntervalMinutes = s.FolderSizeScanIntervalMinutes
            }).ToList(),
            AppSettings = new AppSettingsDto
            {
                ParallelismLevel = settings.ParallelismLevel.ToString(),
                GlobalDryRun = settings.GlobalDryRun,
                GlobalKillSwitch = settings.GlobalKillSwitch,
                Theme = settings.Theme,
                LogLevel = settings.LogLevel,
                TimeZoneId = settings.TimeZoneId
            }
        };

        return ConfigSerializer.Serialize(dto, format);
    }

    public async Task ImportConfigAsync(string content, ConfigFormat format, CancellationToken ct = default)
    {
        var dto = ConfigSerializer.Deserialize<ConfigExportDto>(content, format);
        var now = DateTimeOffset.UtcNow;

        foreach (var i in dto.Instances)
        {
            var sourceType = Enum.Parse<SourceType>(i.SourceType);
            var existing = await db.Instances.SingleOrDefaultAsync(x => x.Name == i.Name, ct);
            if (existing is null)
            {
                db.Instances.Add(new Instance
                {
                    Name = i.Name,
                    SourceType = sourceType,
                    BaseUrl = i.BaseUrl,
                    Enabled = i.Enabled,
                    TimeoutSeconds = i.TimeoutSeconds,
                    VerifySsl = i.VerifySsl,
                    ExtraConfigJson = i.ExtraConfigJson,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
            else
            {
                existing.SourceType = sourceType;
                existing.BaseUrl = i.BaseUrl;
                existing.Enabled = i.Enabled;
                existing.TimeoutSeconds = i.TimeoutSeconds;
                existing.VerifySsl = i.VerifySsl;
                existing.ExtraConfigJson = i.ExtraConfigJson;
                existing.UpdatedAt = now;
            }
        }

        foreach (var s in dto.StoragePaths)
        {
            var existing = await db.StoragePaths.SingleOrDefaultAsync(x => x.Name == s.Name, ct);
            if (existing is null)
            {
                db.StoragePaths.Add(new StoragePathConfig
                {
                    Name = s.Name,
                    Path = s.Path,
                    Enabled = s.Enabled,
                    FolderSizeScanIntervalMinutes = s.FolderSizeScanIntervalMinutes,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
            else
            {
                existing.Path = s.Path;
                existing.Enabled = s.Enabled;
                existing.FolderSizeScanIntervalMinutes = s.FolderSizeScanIntervalMinutes;
                existing.UpdatedAt = now;
            }
        }

        var settings = await db.AppSettings.SingleAsync(x => x.Id == 1, ct);
        settings.ParallelismLevel = Enum.Parse<ParallelismLevel>(dto.AppSettings.ParallelismLevel);
        settings.GlobalDryRun = dto.AppSettings.GlobalDryRun;
        settings.GlobalKillSwitch = dto.AppSettings.GlobalKillSwitch;
        settings.Theme = dto.AppSettings.Theme;
        settings.LogLevel = dto.AppSettings.LogLevel;
        settings.TimeZoneId = dto.AppSettings.TimeZoneId;

        await db.SaveChangesAsync(ct);
    }

    public async Task<string> ExportRulesAsync(ConfigFormat format, CancellationToken ct = default)
    {
        var rules = await db.Rules.AsNoTracking().OrderBy(r => r.Priority).ThenBy(r => r.Name).ToListAsync(ct);

        var dto = new RulesExportDto
        {
            Rules = rules.Select(r => new RuleDto
            {
                Name = r.Name,
                Description = r.Description,
                Enabled = r.Enabled,
                Priority = r.Priority,
                StopOnMatch = r.StopOnMatch,
                DryRun = r.DryRun,
                CronExpression = r.CronExpression,
                TimeZoneId = r.TimeZoneId,
                ConditionTreeJson = r.ConditionTreeJson,
                AdvancedSqlWhere = r.AdvancedSqlWhere,
                UseAdvancedSql = r.UseAdvancedSql,
                ActionsJson = r.ActionsJson,
                TargetInstanceIdsJson = r.TargetInstanceIdsJson
            }).ToList()
        };

        return ConfigSerializer.Serialize(dto, format);
    }

    public async Task ImportRulesAsync(string content, ConfigFormat format, CancellationToken ct = default)
    {
        var dto = ConfigSerializer.Deserialize<RulesExportDto>(content, format);
        var now = DateTimeOffset.UtcNow;

        foreach (var r in dto.Rules)
        {
            var existing = await db.Rules.SingleOrDefaultAsync(x => x.Name == r.Name, ct);
            if (existing is null)
            {
                db.Rules.Add(new Rule
                {
                    Name = r.Name,
                    Description = r.Description,
                    Enabled = r.Enabled,
                    Priority = r.Priority,
                    StopOnMatch = r.StopOnMatch,
                    DryRun = r.DryRun,
                    CronExpression = r.CronExpression,
                    TimeZoneId = r.TimeZoneId,
                    ConditionTreeJson = r.ConditionTreeJson,
                    AdvancedSqlWhere = r.AdvancedSqlWhere,
                    UseAdvancedSql = r.UseAdvancedSql,
                    ActionsJson = r.ActionsJson,
                    TargetInstanceIdsJson = r.TargetInstanceIdsJson,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
            else
            {
                existing.Description = r.Description;
                existing.Enabled = r.Enabled;
                existing.Priority = r.Priority;
                existing.StopOnMatch = r.StopOnMatch;
                existing.DryRun = r.DryRun;
                existing.CronExpression = r.CronExpression;
                existing.TimeZoneId = r.TimeZoneId;
                existing.ConditionTreeJson = r.ConditionTreeJson;
                existing.AdvancedSqlWhere = r.AdvancedSqlWhere;
                existing.UseAdvancedSql = r.UseAdvancedSql;
                existing.ActionsJson = r.ActionsJson;
                existing.TargetInstanceIdsJson = r.TargetInstanceIdsJson;
                existing.UpdatedAt = now;
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
