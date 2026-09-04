using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using QbitFlow.Engine.Analytics;
using QbitFlow.Infrastructure.Data;

namespace QbitFlow.Web.Pages;

public class AnalyticsModel(AppDbContext db, IAnalyticsService analytics, AnalyticsRefreshService refresher) : PageModel
{
    public record Bucket(string Instance, string Category, int Torrents, int Matched, double AvgWatch);
    public record ScoreRow(string Category, string Hash, double WatchTotal, double Popularity, bool Matched);

    public DateTimeOffset? LastRun { get; private set; }
    public int MediaItems { get; private set; }
    public List<Bucket> Buckets { get; private set; } = [];
    public List<ScoreRow> Hottest { get; private set; } = [];
    public List<ScoreRow> Unmatched { get; private set; } = [];

    public async Task OnGetAsync()
    {
        LastRun = await analytics.LastRunUtcAsync(HttpContext.RequestAborted);
        MediaItems = await db.MediaItems.CountAsync();

        var names = await db.SourceConnections.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name);
        var scores = await db.MediaScoreCache.AsNoTracking().ToListAsync();

        Buckets = scores
            .GroupBy(s => (s.QbtInstanceId, s.Category))
            .Select(g => new Bucket(
                names.GetValueOrDefault(g.Key.QbtInstanceId, "—"), g.Key.Category,
                g.Count(), g.Count(x => x.IsMediaMatched),
                g.Any(x => x.IsMediaMatched) ? g.Where(x => x.IsMediaMatched).Average(x => x.WatchTotal) : 0))
            .OrderBy(b => b.Instance).ThenBy(b => b.Category)
            .ToList();

        Hottest = scores.Where(s => s.IsMediaMatched)
            .OrderByDescending(s => s.WatchPopularity).Take(15)
            .Select(s => new ScoreRow(s.Category, s.TorrentHash, s.WatchTotal, s.WatchPopularity, true)).ToList();

        Unmatched = scores.Where(s => !s.IsMediaMatched).Take(25)
            .Select(s => new ScoreRow(s.Category, s.TorrentHash, 0, s.WatchPopularity, false)).ToList();
    }

    public async Task<IActionResult> OnPostRefreshAsync()
    {
        await refresher.TriggerAsync(HttpContext.RequestAborted);
        TempData["Msg"] = "Analytics refresh triggered.";
        return RedirectToPage();
    }
}
