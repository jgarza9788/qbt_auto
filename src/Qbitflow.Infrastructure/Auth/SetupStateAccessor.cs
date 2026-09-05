using Microsoft.EntityFrameworkCore;
using Qbitflow.Infrastructure.Persistence;

namespace Qbitflow.Infrastructure.Auth;

public class SetupStateAccessor : ISetupStateAccessor
{
    private volatile bool _complete;

    public async Task<bool> IsSetupCompleteAsync(AppDbContext db, CancellationToken ct = default)
    {
        if (_complete)
        {
            return true;
        }

        var settings = await db.AppSettings.AsNoTracking().SingleAsync(s => s.Id == 1, ct);
        if (settings.SetupCompleted)
        {
            _complete = true;
        }

        return settings.SetupCompleted;
    }

    public void MarkComplete() => _complete = true;
}
