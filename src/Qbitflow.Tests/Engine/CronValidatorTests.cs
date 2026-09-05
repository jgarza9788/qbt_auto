using Qbitflow.Engine.Scheduling;
using Xunit;

namespace Qbitflow.Tests.Engine;

public class CronValidatorTests
{
    [Fact]
    public void Validate_EveryFiveMinutes_IsAllowed()
    {
        var result = CronValidator.Validate("*/5 * * * *", "UTC");
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_DailySchedule_IsAllowed()
    {
        var result = CronValidator.Validate("0 3 * * *", "UTC");
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_EveryMinute_IsRejected_FasterThanMinimum()
    {
        var result = CronValidator.Validate("*/1 * * * *", "UTC");
        Assert.False(result.IsValid);
        Assert.Contains("5-minute minimum", result.ErrorMessage);
    }

    [Fact]
    public void Validate_EveryTwoMinutes_IsRejected()
    {
        var result = CronValidator.Validate("*/2 * * * *", "UTC");
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_InvalidSyntax_IsRejectedWithExplanation()
    {
        var result = CronValidator.Validate("not a cron expression", "UTC");
        Assert.False(result.IsValid);
        Assert.Contains("Invalid cron expression", result.ErrorMessage);
    }

    [Fact]
    public void Validate_UnknownTimeZone_IsRejected()
    {
        var result = CronValidator.Validate("0 3 * * *", "Not/ARealZone");
        Assert.False(result.IsValid);
        Assert.Contains("timezone", result.ErrorMessage);
    }

    [Fact]
    public void GetNextOccurrences_ReturnsRequestedCount_InAscendingOrder()
    {
        var cron = Cronos.CronExpression.Parse("0 3 * * *");
        var tz = TimeZoneInfo.Utc;

        var occurrences = CronValidator.GetNextOccurrences(cron, tz, DateTimeOffset.UtcNow, 3);

        Assert.Equal(3, occurrences.Count);
        Assert.True(occurrences[0] < occurrences[1]);
        Assert.True(occurrences[1] < occurrences[2]);
        Assert.All(occurrences, o => Assert.Equal(3, o.Hour));
    }
}
