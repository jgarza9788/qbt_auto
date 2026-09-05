using Qbitflow.Infrastructure.Persistence;

namespace Qbitflow.Infrastructure.Auth;

/// <summary>
/// Caches whether the first-run setup wizard has completed so the setup gate doesn't
/// hit the database on every request after the app is up and running.
/// </summary>
public interface ISetupStateAccessor
{
    Task<bool> IsSetupCompleteAsync(AppDbContext db, CancellationToken ct = default);
    void MarkComplete();
}
