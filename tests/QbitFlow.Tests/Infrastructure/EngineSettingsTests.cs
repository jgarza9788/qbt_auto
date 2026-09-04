using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using QbitFlow.Core.Domain;
using QbitFlow.Infrastructure.Config;

namespace QbitFlow.Tests.Infrastructure;

/// <summary>
/// Each test gets its own migrated database (the fixture is per-test, not per-class) because these
/// assertions turn on which keys have never been written.
/// </summary>
public class EngineSettingsTests : IAsyncLifetime
{
    private readonly SqliteFixture _fx = new();

    public Task InitializeAsync() => _fx.InitializeAsync();
    public Task DisposeAsync() => _fx.DisposeAsync();

    private async Task<EngineSettings> ReadAsync()
    {
        await using var scope = _fx.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppSettingStore>().GetEngineSettingsAsync();
    }

    private async Task WriteAsync(string key, string value)
    {
        await using var scope = _fx.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppSettingStore>().SetAsync(key, value);
    }

    [Fact]
    public async Task Unset_keys_fall_back_to_the_documented_defaults()
    {
        var cfg = await ReadAsync();

        cfg.Enabled.Should().Be(EngineDefaults.Enabled);
        cfg.DryRun.Should().Be(EngineDefaults.DryRun);
        cfg.MaxParallelism.Should().Be(EngineDefaults.MaxParallelism);
        cfg.StopOnFirstMatch.Should().Be(EngineDefaults.StopOnFirstMatch);
        cfg.IntervalSeconds.Should().Be(EngineDefaults.IntervalSeconds);
        cfg.Interval.Should().Be(TimeSpan.FromSeconds(EngineDefaults.IntervalSeconds));
    }

    [Fact]
    public async Task Stored_values_round_trip_and_are_clamped()
    {
        await WriteAsync(AppSetting.EngineEnabled, "false");
        await WriteAsync(AppSetting.EngineDryRun, "false");
        await WriteAsync(AppSetting.EngineStopOnFirstMatch, "true");
        await WriteAsync(AppSetting.QbtFreshnessSeconds, "5");        // below the floor
        await WriteAsync(AppSetting.EngineMaxParallelism, "9999");    // above the ceiling

        var cfg = await ReadAsync();

        cfg.Enabled.Should().BeFalse();
        cfg.DryRun.Should().BeFalse();
        cfg.StopOnFirstMatch.Should().BeTrue();
        cfg.IntervalSeconds.Should().Be(EngineDefaults.MinIntervalSeconds);
        cfg.MaxParallelism.Should().Be(EngineDefaults.MaxParallelismLimit);
    }

    [Fact]
    public async Task Garbage_values_degrade_to_defaults_rather_than_throwing()
    {
        await WriteAsync(AppSetting.EngineEnabled, "yes-please");
        await WriteAsync(AppSetting.QbtFreshnessSeconds, "soon");

        var cfg = await ReadAsync();

        cfg.Enabled.Should().Be(EngineDefaults.Enabled);
        cfg.IntervalSeconds.Should().Be(EngineDefaults.IntervalSeconds);
    }
}
