using Qbitflow.Engine.Scheduling;
using Xunit;

namespace Qbitflow.Tests.Engine;

public class CronDescriptionServiceTests
{
    [Theory]
    [InlineData("*/15 * * * *", "15")]
    [InlineData("0 3 * * *", "3:00 AM")]
    public void Describe_ProducesHumanReadableText_ContainingTheKeyDetail(string cron, string expectedFragment)
    {
        var description = CronDescriptionService.Describe(cron);
        Assert.Contains(expectedFragment, description);
    }

    [Fact]
    public void Describe_DoesNotThrow_OnInvalidExpression()
    {
        var description = CronDescriptionService.Describe("garbage");
        Assert.False(string.IsNullOrWhiteSpace(description));
    }

    [Fact]
    public void CommonSchedulePresets_AreAllValidCronExpressions()
    {
        foreach (var (_, cron) in CommonSchedulePresets.Presets)
        {
            var result = CronValidator.Validate(cron, "UTC");
            Assert.True(result.IsValid, $"Preset '{cron}' failed validation: {result.ErrorMessage}");
        }
    }
}
