using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QbitFlow.Core.Domain;
using QbitFlow.Infrastructure.Config;

namespace QbitFlow.Engine.RuleEngine;

/// <summary>Why a manual trigger did or did not run a pass.</summary>
public enum TriggerOutcome
{
    /// <summary>A pass ran to completion (or was cancelled part-way).</summary>
    Started,

    /// <summary>Another pass was already in flight; this trigger was ignored.</summary>
    AlreadyRunning,

    /// <summary>The engine is paused (<see cref="AppSetting.EngineEnabled"/> is false).</summary>
    Paused,
}

/// <summary>
/// The engine loop: every <see cref="AppSetting.QbtFreshnessSeconds"/> it runs one rule pass, unless
/// the engine is paused. There is no cron and no per-rule schedule — one shared cadence for the whole
/// rule list, which is what lets a pass fetch each qBittorrent instance exactly once.
/// <para>
/// Passes are single-flighted by <see cref="_gate"/>: a slow pass causes the next tick to be skipped
/// rather than overlapping. A pass's lifetime is tied to host shutdown, never to the HTTP request
/// that triggered it, so navigating away cannot abort it.
/// </para>
/// </summary>
public sealed class RuleEngineService(
    IServiceScopeFactory scopeFactory,
    ILogger<RuleEngineService> log) : BackgroundService
{
    /// <summary>Grace period after startup so migrations and source health checks settle first.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(10);

    /// <summary>Held for the duration of a pass; a zero count therefore means "a pass is running".</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Cancellation for the in-flight pass, exposed through <see cref="CancelRun"/>.</summary>
    private volatile CancellationTokenSource? _current;

    private CancellationToken _hostStopping = CancellationToken.None;

    /// <summary>True while a pass is in flight. Drives the "running" pill on the dashboard.</summary>
    public bool IsRunning => _gate.CurrentCount == 0;

    /// <summary>Cancels the in-flight pass, if any. Returns false when nothing is running.</summary>
    public bool CancelRun()
    {
        var cts = _current;
        if (cts is null) return false;
        try { cts.Cancel(); return true; }
        catch (ObjectDisposedException) { return false; }   // the pass finished between the read and the cancel
    }

    /// <summary>
    /// Runs a pass immediately, bypassing the interval but not the paused flag. Returns why it did or
    /// did not run so callers can report something truthful instead of an unconditional "started".
    /// </summary>
    /// <param name="dryRun">Overrides the stored dry-run default for this pass only; null uses it.</param>
    public async Task<TriggerOutcome> TriggerAsync(bool? dryRun, CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct)) return TriggerOutcome.AlreadyRunning;
        try { return await RunOnceAsync(RunTrigger.Manual, dryRun); }
        finally { _gate.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _hostStopping = stoppingToken;
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            if (await _gate.WaitAsync(0, stoppingToken))
            {
                try { await RunOnceAsync(RunTrigger.Schedule, dryRun: null); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { log.LogError(ex, "Rule engine pass failed"); }
                finally { _gate.Release(); }
            }
            else
            {
                // A manual trigger or an overrunning pass holds the gate — skip, don't queue up.
                log.LogDebug("Rule engine pass still running — tick skipped");
            }

            // Re-read the interval every tick so a Settings change takes effect without a restart.
            var interval = (await ReadSettingsAsync(stoppingToken)).Interval;
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Executes one pass in its own DI scope. The caller must already hold <see cref="_gate"/>.
    /// </summary>
    private async Task<TriggerOutcome> RunOnceAsync(RunTrigger trigger, bool? dryRun)
    {
        // Linked to host shutdown only — never to a request — so a pass survives the caller.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_hostStopping);
        _current = cts;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();

            var settings = scope.ServiceProvider.GetRequiredService<AppSettingStore>();
            if (!(await settings.GetEngineSettingsAsync(cts.Token)).Enabled)
            {
                if (trigger == RunTrigger.Manual)
                    log.LogInformation("Rule engine is paused — manual trigger ignored");
                return TriggerOutcome.Paused;
            }

            await scope.ServiceProvider.GetRequiredService<IRuleEngineRunner>()
                .RunAsync(trigger, dryRun, cts.Token);

            return TriggerOutcome.Started;
        }
        catch (OperationCanceledException)
        {
            return TriggerOutcome.Started;   // it did start; the runner records the cancellation
        }
        finally
        {
            _current = null;
        }
    }

    /// <summary>Reads engine settings in a throwaway scope, falling back to defaults if the DB is unavailable.</summary>
    private async Task<EngineSettings> ReadSettingsAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<AppSettingStore>()
                .GetEngineSettingsAsync(ct);
        }
        catch
        {
            return EngineSettings.Fallback;
        }
    }
}
