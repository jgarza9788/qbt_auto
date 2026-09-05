using Cronos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Qbitflow.Core.Domain;
using Qbitflow.Infrastructure.Persistence;

namespace Qbitflow.Engine.Scheduling;

/// <summary>
/// Polls enabled rules on a fixed interval, decides which are due (per-rule cron,
/// restart-safe via Rule.LastRunAt), and triggers a run for each -- guarded by
/// RuleRunGate so a rule already running is never started again concurrently.
/// </summary>
public class RuleSchedulerService(IServiceScopeFactory scopeFactory, RuleRunGate runGate, ILogger<RuleSchedulerService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduler tick failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var settings = await db.AppSettings.AsNoTracking().SingleAsync(s => s.Id == 1, ct);
        if (settings.GlobalKillSwitch)
        {
            return;
        }

        var rules = await db.Rules.AsNoTracking().Where(r => r.Enabled).ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;

        foreach (var rule in rules)
        {
            if (!IsDue(rule, now))
            {
                continue;
            }

            if (!runGate.TryEnter(rule.Id))
            {
                logger.LogDebug("Rule {RuleId} is still running from a previous tick; skipping", rule.Id);
                continue;
            }

            _ = RunRuleInBackgroundAsync(rule.Id, ct);
        }
    }

    private async Task RunRuleInBackgroundAsync(int ruleId, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<IRuleRunner>();
            await runner.RunAsync(ruleId, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Rule {RuleId} run threw unexpectedly", ruleId);
        }
        finally
        {
            runGate.Exit(ruleId);
        }
    }

    /// <summary>Internal (not private) so scheduling decisions are directly testable without running the timer loop.</summary>
    internal static bool IsDue(Rule rule, DateTimeOffset now)
    {
        var validation = CronValidator.Validate(rule.CronExpression, rule.TimeZoneId);
        if (!validation.IsValid)
        {
            return false;
        }

        CronExpression cron;
        TimeZoneInfo tz;
        try
        {
            cron = CronExpression.Parse(rule.CronExpression, CronFormat.Standard);
            tz = TimeZoneInfo.FindSystemTimeZoneById(rule.TimeZoneId);
        }
        catch
        {
            return false;
        }

        var lastRun = rule.LastRunAt?.UtcDateTime ?? now.UtcDateTime.AddMinutes(-1);
        var next = cron.GetNextOccurrence(lastRun, tz);
        return next is not null && next.Value <= now.UtcDateTime;
    }
}
