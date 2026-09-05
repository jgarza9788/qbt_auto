using Microsoft.EntityFrameworkCore;
using Qbitflow.Core.Domain;
using Qbitflow.Core.Interfaces;
using Qbitflow.Infrastructure.Persistence;

namespace Qbitflow.Infrastructure.Settings;

public class ParallelismSettingsProvider(AppDbContext db) : IParallelismSettingsProvider
{
    public async Task<ParallelismLevel> GetLevelAsync(CancellationToken ct = default)
    {
        var settings = await db.AppSettings.AsNoTracking().SingleAsync(s => s.Id == 1, ct);
        return settings.ParallelismLevel;
    }
}
