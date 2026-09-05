using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Qbitflow.Core.Domain;
using Qbitflow.Infrastructure.Persistence;

namespace Qbitflow.Web.Pages;

public class IndexModel(AppDbContext db) : PageModel
{
    public AppSettings Settings { get; private set; } = null!;
    public int EnabledInstanceCount { get; private set; }
    public int TotalInstanceCount { get; private set; }
    public int EnabledRuleCount { get; private set; }
    public int TotalRuleCount { get; private set; }
    public List<Instance> Instances { get; private set; } = [];
    public List<RunRecord> RecentRuns { get; private set; } = [];
    public Dictionary<int, string> RuleNamesById { get; private set; } = [];
    public int MatchedLast24h { get; private set; }
    public int FailedLast24h { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        Settings = await db.AppSettings.AsNoTracking().SingleAsync(s => s.Id == 1, ct);

        Instances = await db.Instances.AsNoTracking().OrderBy(i => i.Name).ToListAsync(ct);
        EnabledInstanceCount = Instances.Count(i => i.Enabled);
        TotalInstanceCount = Instances.Count;

        var rules = await db.Rules.AsNoTracking().ToListAsync(ct);
        EnabledRuleCount = rules.Count(r => r.Enabled);
        TotalRuleCount = rules.Count;
        RuleNamesById = rules.ToDictionary(r => r.Id, r => r.Name);

        // SQLite's EF Core provider can't translate ORDER BY on DateTimeOffset columns;
        // Id is an auto-increment PK assigned in real-time insertion order, so ordering
        // by it descending is equivalent here and does translate.
        RecentRuns = await db.RunRecords.AsNoTracking()
            .OrderByDescending(r => r.Id)
            .Take(10)
            .ToListAsync(ct);

        // EF Core's SQLite provider can't translate DateTimeOffset comparisons in WHERE
        // either -- fetch a bounded recent set by Id and filter the time window client-side.
        var since = DateTimeOffset.UtcNow.AddHours(-24);
        var recentWindow = (await db.RunRecords.AsNoTracking()
                .OrderByDescending(r => r.Id)
                .Take(1000)
                .ToListAsync(ct))
            .Where(r => r.StartedAt >= since)
            .ToList();
        MatchedLast24h = recentWindow.Sum(r => r.MatchedCount);
        FailedLast24h = recentWindow.Count(r => r.Outcome == RunOutcome.Failed || r.Outcome == RunOutcome.PartialFailure);
    }
}
