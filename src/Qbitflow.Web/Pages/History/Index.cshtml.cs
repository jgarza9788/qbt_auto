using System.Text.Json;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Qbitflow.Core.Domain;
using Qbitflow.Engine.Actions;
using Qbitflow.Infrastructure.Persistence;

namespace Qbitflow.Web.Pages.History;

public class IndexModel(AppDbContext db) : PageModel
{
    [Microsoft.AspNetCore.Mvc.BindProperty(SupportsGet = true)]
    public int? RuleId { get; set; }

    [Microsoft.AspNetCore.Mvc.BindProperty(SupportsGet = true)]
    public RunOutcome? Outcome { get; set; }

    [Microsoft.AspNetCore.Mvc.BindProperty(SupportsGet = true)]
    public DateTime? From { get; set; }

    [Microsoft.AspNetCore.Mvc.BindProperty(SupportsGet = true)]
    public DateTime? To { get; set; }

    [Microsoft.AspNetCore.Mvc.BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    public const int PageSize = 25;

    public List<RunRecord> Runs { get; private set; } = [];
    public Dictionary<int, string> RuleNamesById { get; private set; } = [];
    public List<Rule> AllRules { get; private set; } = [];
    public int TotalCount { get; private set; }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));

    public async Task OnGetAsync(CancellationToken ct)
    {
        AllRules = await db.Rules.AsNoTracking().OrderBy(r => r.Name).ToListAsync(ct);
        RuleNamesById = AllRules.ToDictionary(r => r.Id, r => r.Name);

        var query = db.RunRecords.AsNoTracking().AsQueryable();

        if (RuleId is not null)
        {
            query = query.Where(r => r.RuleId == RuleId);
        }
        if (Outcome is not null)
        {
            query = query.Where(r => r.Outcome == Outcome);
        }
        // SQLite's EF Core provider can't translate DateTimeOffset comparisons (WHERE or
        // ORDER BY) -- push down what does translate (RuleId/Outcome), order by Id (an
        // auto-increment PK assigned in real-time insertion order, equivalent to ordering
        // by StartedAt here), then apply the From/To window and pagination client-side.
        var candidates = await query.OrderByDescending(r => r.Id).Take(2000).ToListAsync(ct);

        if (From is not null)
        {
            var fromOffset = new DateTimeOffset(DateTime.SpecifyKind(From.Value, DateTimeKind.Utc));
            candidates = candidates.Where(r => r.StartedAt >= fromOffset).ToList();
        }
        if (To is not null)
        {
            var toOffset = new DateTimeOffset(DateTime.SpecifyKind(To.Value.AddDays(1), DateTimeKind.Utc));
            candidates = candidates.Where(r => r.StartedAt < toOffset).ToList();
        }

        TotalCount = candidates.Count;

        var page = Math.Max(1, PageNumber);
        Runs = candidates.Skip((page - 1) * PageSize).Take(PageSize).ToList();
    }

    public static List<ActionResult> ParseDetails(string? detailsJson)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<ActionResult>>(detailsJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
