using FluentAssertions;
using QbitFlow.Core.Domain;
using QbitFlow.Engine.Scheduling;

namespace QbitFlow.Tests.Scheduling;

public class ScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Interval_below_five_minutes_is_clamped()
    {
        var p = new Pipeline { ScheduleKind = ScheduleKind.Interval, IntervalSeconds = 60 };
        Schedule.Next(p, Now).Should().Be(Now.AddSeconds(300));
    }

    [Fact]
    public void Interval_above_the_floor_is_respected()
    {
        var p = new Pipeline { ScheduleKind = ScheduleKind.Interval, IntervalSeconds = 900 };
        Schedule.Next(p, Now).Should().Be(Now.AddSeconds(900));
    }

    [Fact]
    public void Cron_never_returns_sooner_than_five_minutes()
    {
        var p = new Pipeline { ScheduleKind = ScheduleKind.Cron, CronExpression = "* * * * * *", TimeZoneId = "UTC" };
        Schedule.Next(p, Now).Should().BeOnOrAfter(Now.AddSeconds(300));
    }

    [Fact]
    public void Cron_hourly_lands_on_the_next_hour()
    {
        var p = new Pipeline { ScheduleKind = ScheduleKind.Cron, CronExpression = "0 0 * * * *", TimeZoneId = "UTC" };
        Schedule.Next(p, Now).Should().Be(new DateTimeOffset(2026, 1, 1, 13, 0, 0, TimeSpan.Zero));
    }
}
