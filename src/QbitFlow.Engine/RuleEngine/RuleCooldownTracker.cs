using System.Collections.Concurrent;

namespace QbitFlow.Engine.RuleEngine;

/// <summary>
/// Enforces <see cref="Core.Domain.Rule.CooldownSeconds"/>: once a rule fires against a torrent it
/// won't fire against that same torrent again until the window elapses. This is the safety valve that
/// makes an always-on engine safe for non-idempotent actions (<c>script.run</c>, <c>torrent.export</c>)
/// — most actions are idempotent and need no cooldown at all.
/// <para>
/// State is process-local and lost on restart, which is fine for a throttle: the worst case is one
/// extra fire after a reboot. Entries store their expiry and are swept periodically, so the map stays
/// proportional to the torrents currently inside an active window rather than growing forever.
/// </para>
/// </summary>
public sealed class RuleCooldownTracker
{
    /// <summary>How often <see cref="TryFire"/> sweeps expired entries. Cheap, so this can be frequent.</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(10);

    /// <summary>Key → the moment the rule may fire against that torrent again.</summary>
    private readonly ConcurrentDictionary<(Guid RuleId, string Hash), DateTimeOffset> _blockedUntil = new();

    private long _nextSweepTicks = DateTimeOffset.MinValue.UtcTicks;

    /// <summary>
    /// Returns true if the rule may fire against this torrent now, and when it does, starts the next
    /// cooldown window. A null or non-positive <paramref name="cooldownSeconds"/> means "no throttle"
    /// and always returns true without recording anything.
    /// </summary>
    public bool TryFire(Guid ruleId, string torrentHash, int? cooldownSeconds, DateTimeOffset nowUtc)
    {
        if (cooldownSeconds is not > 0) return true;

        Sweep(nowUtc);

        var key = (ruleId, torrentHash);
        if (_blockedUntil.TryGetValue(key, out var until) && nowUtc < until)
            return false;

        _blockedUntil[key] = nowUtc.AddSeconds(cooldownSeconds.Value);
        return true;
    }

    /// <summary>
    /// Drops every cooldown window for a rule. Called when a rule is edited or deleted so a changed
    /// rule can take effect on the next pass instead of waiting out the old window.
    /// </summary>
    public void Forget(Guid ruleId)
    {
        foreach (var key in _blockedUntil.Keys)
            if (key.RuleId == ruleId)
                _blockedUntil.TryRemove(key, out _);
    }

    /// <summary>Removes elapsed windows, at most once per <see cref="SweepInterval"/> across all callers.</summary>
    private void Sweep(DateTimeOffset nowUtc)
    {
        var next = Interlocked.Read(ref _nextSweepTicks);
        if (nowUtc.UtcTicks < next) return;

        // Only the thread that wins the swap does the work; the rest carry on.
        if (Interlocked.CompareExchange(ref _nextSweepTicks, nowUtc.Add(SweepInterval).UtcTicks, next) != next)
            return;

        foreach (var (key, until) in _blockedUntil)
            if (until <= nowUtc)
                _blockedUntil.TryRemove(key, out _);
    }
}
