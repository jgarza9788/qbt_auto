namespace Qbitflow.Engine.Scheduling;

/// <summary>
/// The "human-friendly picker" -- a fixed, round-trippable set of label/cron pairs.
/// Arbitrary cron -> English is one-way (CronDescriptionService); going the other way
/// (English -> cron) only makes sense for a known, closed list like this one, not for
/// parsing free-form text.
/// </summary>
public static class CommonSchedulePresets
{
    public static readonly IReadOnlyList<(string Label, string CronExpression)> Presets =
    [
        ("Every 15 minutes", "*/15 * * * *"),
        ("Every 30 minutes", "*/30 * * * *"),
        ("Hourly", "0 * * * *"),
        ("Every 6 hours", "0 */6 * * *"),
        ("Daily at 3:00 AM", "0 3 * * *"),
        ("Weekly on Sunday at 3:00 AM", "0 3 * * 0")
    ];
}
