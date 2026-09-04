using System.Globalization;
using Microsoft.EntityFrameworkCore;
using QbitFlow.Core.Domain;
using QbitFlow.Infrastructure.Data;

namespace QbitFlow.Infrastructure.Config;

/// <summary>Typed access to the <see cref="AppSetting"/> key/value table.</summary>
public sealed class AppSettingStore(AppDbContext db)
{
    public async Task<string?> GetAsync(string key, CancellationToken ct = default) =>
        (await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct))?.Value;

    public async Task<int> GetIntAsync(string key, int fallback, CancellationToken ct = default)
    {
        var raw = await GetAsync(key, ct);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    public async Task<bool> GetBoolAsync(string key, bool fallback, CancellationToken ct = default)
    {
        var raw = await GetAsync(key, ct);
        return bool.TryParse(raw, out var v) ? v : fallback;
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        var existing = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (existing is null)
            db.AppSettings.Add(new AppSetting { Key = key, Value = value });
        else
            existing.Value = value;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Reads every rule-engine key in one query and returns them clamped, falling back to
    /// <see cref="EngineDefaults"/> for anything unset or unparseable. Prefer this over four separate
    /// <see cref="GetBoolAsync"/>/<see cref="GetIntAsync"/> calls: it is one round-trip and it keeps
    /// the defaults in a single place.
    /// </summary>
    public async Task<EngineSettings> GetEngineSettingsAsync(CancellationToken ct = default)
    {
        var rows = await db.AppSettings.AsNoTracking()
            .Where(s => s.Key == AppSetting.EngineEnabled
                     || s.Key == AppSetting.EngineDryRun
                     || s.Key == AppSetting.EngineMaxParallelism
                     || s.Key == AppSetting.EngineStopOnFirstMatch
                     || s.Key == AppSetting.QbtFreshnessSeconds)
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);

        bool Bool(string key, bool fallback) =>
            rows.TryGetValue(key, out var v) && bool.TryParse(v, out var b) ? b : fallback;

        int Int(string key, int fallback) =>
            rows.TryGetValue(key, out var v) &&
            int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;

        return new EngineSettings(
            Enabled: Bool(AppSetting.EngineEnabled, EngineDefaults.Enabled),
            DryRun: Bool(AppSetting.EngineDryRun, EngineDefaults.DryRun),
            MaxParallelism: EngineDefaults.ClampParallelism(
                Int(AppSetting.EngineMaxParallelism, EngineDefaults.MaxParallelism)),
            StopOnFirstMatch: Bool(AppSetting.EngineStopOnFirstMatch, EngineDefaults.StopOnFirstMatch),
            IntervalSeconds: EngineDefaults.ClampInterval(
                Int(AppSetting.QbtFreshnessSeconds, EngineDefaults.IntervalSeconds)));
    }
}
