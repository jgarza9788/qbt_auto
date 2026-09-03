namespace QbitFlow.Core.Abstractions;

/// <summary>DB-backed idempotency for <c>script.run</c> (survives run-dir wipes).</summary>
public interface IScriptRunMarkerStore
{
    Task<bool> ExistsAsync(Guid ruleId, string torrentHash, CancellationToken ct);
    Task AddAsync(Guid ruleId, string torrentHash, string runDir, CancellationToken ct);
}
