using FluentAssertions;
using QbitFlow.Engine.RuleEngine;

namespace QbitFlow.Tests.Engine;

public class RuleCooldownTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly Guid _rule = Guid.NewGuid();

    [Fact]
    public void No_cooldown_configured_never_throttles()
    {
        var t = new RuleCooldownTracker();

        t.TryFire(_rule, "h1", null, T0).Should().BeTrue();
        t.TryFire(_rule, "h1", null, T0).Should().BeTrue();
        t.TryFire(_rule, "h1", 0, T0).Should().BeTrue();
    }

    [Fact]
    public void Second_fire_inside_the_window_is_blocked_and_allowed_again_after()
    {
        var t = new RuleCooldownTracker();

        t.TryFire(_rule, "h1", 300, T0).Should().BeTrue();
        t.TryFire(_rule, "h1", 300, T0.AddSeconds(299)).Should().BeFalse();
        t.TryFire(_rule, "h1", 300, T0.AddSeconds(300)).Should().BeTrue();
    }

    [Fact]
    public void Cooldown_is_tracked_per_torrent_and_per_rule()
    {
        var t = new RuleCooldownTracker();
        var other = Guid.NewGuid();

        t.TryFire(_rule, "h1", 300, T0).Should().BeTrue();
        t.TryFire(_rule, "h2", 300, T0).Should().BeTrue();     // different torrent
        t.TryFire(other, "h1", 300, T0).Should().BeTrue();     // different rule
        t.TryFire(_rule, "h1", 300, T0).Should().BeFalse();    // same pair, still blocked
    }

    [Fact]
    public void Forget_clears_a_rules_windows_so_an_edited_rule_fires_next_pass()
    {
        var t = new RuleCooldownTracker();
        t.TryFire(_rule, "h1", 3600, T0).Should().BeTrue();
        t.TryFire(_rule, "h1", 3600, T0.AddSeconds(1)).Should().BeFalse();

        t.Forget(_rule);

        t.TryFire(_rule, "h1", 3600, T0.AddSeconds(2)).Should().BeTrue();
    }

    [Fact]
    public void Expired_entries_are_swept_rather_than_accumulating()
    {
        var t = new RuleCooldownTracker();
        for (var i = 0; i < 500; i++)
            t.TryFire(_rule, $"h{i}", 60, T0);

        // Well past both the windows and the sweep interval: every entry should have been dropped,
        // and the rule fires freely again.
        var later = T0.AddHours(1);
        t.TryFire(_rule, "h0", 60, later).Should().BeTrue();
        t.TryFire(_rule, "h499", 60, later).Should().BeTrue();
    }
}
