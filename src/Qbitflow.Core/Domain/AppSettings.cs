namespace Qbitflow.Core.Domain;

/// <summary>Singleton row (Id is always 1) holding global application settings.</summary>
public class AppSettings
{
    public int Id { get; set; } = 1;
    public ParallelismLevel ParallelismLevel { get; set; } = ParallelismLevel.Medium;
    public bool GlobalDryRun { get; set; }
    public bool GlobalKillSwitch { get; set; }
    public string Theme { get; set; } = "system";
    public string LogLevel { get; set; } = "Information";
    public string TimeZoneId { get; set; } = "UTC";
    public bool SetupCompleted { get; set; }
}
