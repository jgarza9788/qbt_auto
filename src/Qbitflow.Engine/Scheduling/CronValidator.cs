using Cronos;

namespace Qbitflow.Engine.Scheduling;

/// <summary>Parses/validates a rule's cron expression and enforces the 5-minute minimum interval by rejecting (not silently rewriting) anything faster, with an explanation the UI can show directly.</summary>
public static class CronValidator
{
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(5);

    public static CronValidationResult Validate(string cronExpression, string timeZoneId)
    {
        CronExpression parsed;
        try
        {
            parsed = CronExpression.Parse(cronExpression, CronFormat.Standard);
        }
        catch (Exception ex)
        {
            return CronValidationResult.Failure($"Invalid cron expression: {ex.Message}");
        }

        TimeZoneInfo tz;
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception)
        {
            return CronValidationResult.Failure($"Unknown timezone '{timeZoneId}'.");
        }

        var occurrences = GetNextOccurrences(parsed, tz, DateTimeOffset.UtcNow, 3);
        for (var i = 1; i < occurrences.Count; i++)
        {
            var gap = occurrences[i] - occurrences[i - 1];
            if (gap < MinimumInterval)
            {
                return CronValidationResult.Failure(
                    $"This schedule fires more often than the {MinimumInterval.TotalMinutes:0}-minute minimum " +
                    $"(two consecutive runs would be {gap.TotalMinutes:0.#} minutes apart).");
            }
        }

        return CronValidationResult.Ok();
    }

    public static List<DateTimeOffset> GetNextOccurrences(CronExpression cron, TimeZoneInfo timeZone, DateTimeOffset from, int count)
    {
        var results = new List<DateTimeOffset>();
        var cursor = from.UtcDateTime;

        for (var i = 0; i < count; i++)
        {
            var next = cron.GetNextOccurrence(cursor, timeZone);
            if (next is null)
            {
                break;
            }

            results.Add(new DateTimeOffset(DateTime.SpecifyKind(next.Value, DateTimeKind.Utc)));
            cursor = next.Value;
        }

        return results;
    }
}
