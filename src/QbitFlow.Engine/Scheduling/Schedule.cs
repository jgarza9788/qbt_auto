using Cronos;
using QbitFlow.Core.Domain;

namespace QbitFlow.Engine.Scheduling;

/// <summary>Next-run computation with a hard 5-minute floor on every schedule.</summary>
public static class Schedule
{
    public static DateTimeOffset Next(Pipeline pipeline, DateTimeOffset fromUtc)
    {
        var floor = fromUtc.AddSeconds(ScheduleLimits.MinIntervalSeconds);

        if (pipeline.ScheduleKind == ScheduleKind.Cron && !string.IsNullOrWhiteSpace(pipeline.CronExpression))
        {
            try
            {
                var cron = CronExpression.Parse(pipeline.CronExpression, CronFormat.IncludeSeconds);
                var tz = ResolveTimeZone(pipeline.TimeZoneId);
                var next = cron.GetNextOccurrence(fromUtc.UtcDateTime, tz);
                if (next is { } n)
                {
                    var candidate = new DateTimeOffset(n, TimeSpan.Zero);
                    return candidate < floor ? floor : candidate;
                }
            }
            catch
            {
                // fall through to interval
            }
        }

        return fromUtc.AddSeconds(pipeline.EffectiveIntervalSeconds);
    }

    private static TimeZoneInfo ResolveTimeZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { return TimeZoneInfo.Utc; }
    }
}
