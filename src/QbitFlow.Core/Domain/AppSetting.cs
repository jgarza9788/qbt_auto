namespace QbitFlow.Core.Domain;

/// <summary>
/// One row of the global key/value settings table. Everything that used to be a per-pipeline column
/// (schedule, dry-run, parallelism, stop-on-first-match) lives here now — see <see cref="EngineDefaults"/>
/// for the fallback each key uses when it has never been written.
/// </summary>
public class AppSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";

    // ---- Well-known keys ----

    public const string ConfigImportHash = "ConfigImportHash";
    public const string AnalyticsWeights = "AnalyticsWeights";
    public const string AnalyticsIntervalMinutes = "AnalyticsIntervalMinutes";
    public const string AnalyticsStaleAfterMinutes = "AnalyticsStaleAfterMinutes";

    /// <summary>
    /// The single rule-engine cadence knob, surfaced in Settings as "Rule check interval". It is both
    /// how often a pass runs and the maximum age of a shared torrent snapshot, so a pass always sees
    /// data no older than one interval. Floor: <see cref="EngineDefaults.MinIntervalSeconds"/>.
    /// </summary>
    public const string QbtFreshnessSeconds = "QbtFreshnessSeconds";

    // Global rule-engine settings (these replaced the old per-pipeline columns).
    public const string EngineEnabled = "EngineEnabled";
    public const string EngineDryRun = "EngineDryRun";
    public const string EngineMaxParallelism = "EngineMaxParallelism";
    public const string EngineStopOnFirstMatch = "EngineStopOnFirstMatch";

    public const string SecretsEncryption = "SecretsEncryption";
    public const string AuthMode = "AuthMode";
    public const string AuthSecretHash = "AuthSecretHash";
    public const string SchemaVersion = "SchemaVersion";
}

/// <summary>
/// Fallbacks for the rule-engine <see cref="AppSetting"/> keys, in one place so the engine loop, the
/// runner, the Settings page and the JSON API can never disagree about what an unset key means.
/// </summary>
public static class EngineDefaults
{
    /// <summary>A fresh install runs; nothing happens until rules exist and are enabled.</summary>
    public const bool Enabled = true;

    /// <summary>Safe by default — evaluate and log, mutate nothing, until the user opts in.</summary>
    public const bool DryRun = true;

    /// <summary>Torrents processed concurrently per pass ("CPU speed" → Medium).</summary>
    public const int MaxParallelism = 8;

    /// <summary>Whether a matching rule short-circuits the rest for that torrent, unless the rule overrides it.</summary>
    public const bool StopOnFirstMatch = false;

    /// <summary>Seconds between passes, and the snapshot max age.</summary>
    public const int IntervalSeconds = 120;

    /// <summary>Hard floor on the interval — protects qBittorrent from being hammered.</summary>
    public const int MinIntervalSeconds = 30;

    /// <summary>Bounds accepted for <see cref="AppSetting.EngineMaxParallelism"/>.</summary>
    public const int MinParallelism = 1;
    public const int MaxParallelismLimit = 64;

    /// <summary>Applies the interval floor to a raw (possibly unset or silly) stored value.</summary>
    public static int ClampInterval(int seconds) => Math.Max(MinIntervalSeconds, seconds);

    /// <summary>Applies the parallelism bounds to a raw stored value.</summary>
    public static int ClampParallelism(int value) => Math.Clamp(value, MinParallelism, MaxParallelismLimit);
}

/// <summary>
/// The rule engine's global configuration, already clamped. Read it in one shot with
/// <c>AppSettingStore.GetEngineSettingsAsync</c> rather than fetching keys individually — that keeps
/// every caller (loop, runner, dashboard, Settings page, JSON API) on the same values and defaults.
/// </summary>
public sealed record EngineSettings(
    bool Enabled,
    bool DryRun,
    int MaxParallelism,
    bool StopOnFirstMatch,
    int IntervalSeconds)
{
    /// <summary>Pass cadence, and the maximum age of a shared torrent snapshot.</summary>
    public TimeSpan Interval => TimeSpan.FromSeconds(IntervalSeconds);

    /// <summary>What a database with no engine settings written yet behaves like.</summary>
    public static EngineSettings Fallback { get; } = new(
        EngineDefaults.Enabled,
        EngineDefaults.DryRun,
        EngineDefaults.MaxParallelism,
        EngineDefaults.StopOnFirstMatch,
        EngineDefaults.IntervalSeconds);
}
