namespace Qbitflow.Infrastructure.Config;

// DTOs used for config/rule import-export. Deliberately exclude credential fields
// (ApiKey/Password) from InstanceDto so exported files never leak secrets across
// installs -- imported instances come back in "needs credentials" state.

public class InstanceDto
{
    public required string Name { get; set; }
    public required string SourceType { get; set; }
    public required string BaseUrl { get; set; }
    public bool Enabled { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 30;
    public bool VerifySsl { get; set; } = true;
    public string? ExtraConfigJson { get; set; }
}

public class StoragePathDto
{
    public required string Name { get; set; }
    public required string Path { get; set; }
    public bool Enabled { get; set; } = true;
    public int FolderSizeScanIntervalMinutes { get; set; } = 60;
}

public class RuleDto
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; }
    public bool StopOnMatch { get; set; }
    public bool DryRun { get; set; }
    public required string CronExpression { get; set; }
    public string TimeZoneId { get; set; } = "UTC";
    public required string ConditionTreeJson { get; set; }
    public string? AdvancedSqlWhere { get; set; }
    public bool UseAdvancedSql { get; set; }
    public required string ActionsJson { get; set; }
    public required string TargetInstanceIdsJson { get; set; }
}

public class AppSettingsDto
{
    public string ParallelismLevel { get; set; } = "Medium";
    public bool GlobalDryRun { get; set; }
    public bool GlobalKillSwitch { get; set; }
    public string Theme { get; set; } = "system";
    public string LogLevel { get; set; } = "Information";
    public string TimeZoneId { get; set; } = "UTC";
}

public class ConfigExportDto
{
    public List<InstanceDto> Instances { get; set; } = [];
    public List<StoragePathDto> StoragePaths { get; set; } = [];
    public AppSettingsDto AppSettings { get; set; } = new();
}

public class RulesExportDto
{
    public List<RuleDto> Rules { get; set; } = [];
}
