using Microsoft.EntityFrameworkCore;
using QbitFlow.Core.Abstractions;
using QbitFlow.Core.Domain;

namespace QbitFlow.Infrastructure.Data;

/// <summary>
/// DB-backed <c>script.run</c> idempotency. Uses a short-lived context per call so it is safe to
/// invoke from the bounded-parallel torrent loop.
/// </summary>
public sealed class ScriptRunMarkerStore(IDbContextFactory<AppDbContext> factory) : IScriptRunMarkerStore
{
    public async Task<bool> ExistsAsync(Guid ruleId, string torrentHash, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ScriptRunMarkers
            .AsNoTracking()
            .AnyAsync(m => m.RuleId == ruleId && m.TorrentHash == torrentHash, ct);
    }

    public async Task AddAsync(Guid ruleId, string torrentHash, string runDir, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.ScriptRunMarkers.Add(new ScriptRunMarker
        {
            RuleId = ruleId,
            TorrentHash = torrentHash,
            RunDir = runDir,
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A concurrent run inserted the same marker — that's fine.
        }
    }
}
