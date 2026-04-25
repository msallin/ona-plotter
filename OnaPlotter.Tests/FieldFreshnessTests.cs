using OnaPlotter.Models;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the timestamp-stamping behaviour on NavigationData plus the
/// Live / Stale / Dead categorisation that HUDs consume. A deterministic
/// Func<DateTime> clock is injected so we can assert ages without
/// sleeping in tests.
/// </summary>
public class FieldFreshnessTests
{
    private sealed class FakeClock
    {
        public DateTime Now { get; set; } = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        public DateTime Get() => Now;
    }

    private static (NavigationData nav, FakeClock clock) Make()
    {
        var clock = new FakeClock();
        return (new NavigationData(clock.Get), clock);
    }

    [Test]
    public async Task DepthApply_StampsUpdatedUtc()
    {
        var (nav, clock) = Make();
        nav.Apply("environment.depth.belowTransducer", 5.2);
        await Assert.That(nav.Depth).IsEqualTo(5.2);
        await Assert.That(nav.DepthUpdatedUtc).IsEqualTo(clock.Now);
    }

    [Test]
    public async Task PositionApply_StampsUpdatedUtc()
    {
        var (nav, clock) = Make();
        nav.ApplyPosition(47.0, 8.0);
        await Assert.That(nav.PositionUpdatedUtc).IsEqualTo(clock.Now);
    }

    [Test]
    public async Task AnchorRadiusApply_StampsUpdatedUtc()
    {
        var (nav, clock) = Make();
        nav.Apply("navigation.anchor.currentRadius", 22.0);
        await Assert.That(nav.AnchorRadiusUpdatedUtc).IsEqualTo(clock.Now);
    }

    [Test]
    public async Task FreshnessOf_Missing_WhenNeverSet()
    {
        var (nav, _) = Make();
        await Assert.That(nav.FreshnessOf(nav.DepthUpdatedUtc)).IsEqualTo(FieldFreshness.Missing);
    }

    [Test]
    public async Task FreshnessOf_Live_Below10s()
    {
        var (nav, clock) = Make();
        nav.Apply("environment.depth.belowTransducer", 5.0);
        clock.Now = clock.Now.AddSeconds(5);
        await Assert.That(nav.FreshnessOf(nav.DepthUpdatedUtc)).IsEqualTo(FieldFreshness.Live);
    }

    [Test]
    public async Task FreshnessOf_Stale_Between10sAnd30s()
    {
        var (nav, clock) = Make();
        nav.Apply("environment.depth.belowTransducer", 5.0);
        clock.Now = clock.Now.AddSeconds(15);
        await Assert.That(nav.FreshnessOf(nav.DepthUpdatedUtc)).IsEqualTo(FieldFreshness.Stale);
    }

    [Test]
    public async Task FreshnessOf_Dead_OnceOver30s()
    {
        var (nav, clock) = Make();
        nav.Apply("environment.depth.belowTransducer", 5.0);
        clock.Now = clock.Now.AddSeconds(45);
        await Assert.That(nav.FreshnessOf(nav.DepthUpdatedUtc)).IsEqualTo(FieldFreshness.Dead);
    }

    [Test]
    public async Task NewApply_RefreshesTimestamp()
    {
        // The moment a new value lands, freshness resets.
        var (nav, clock) = Make();
        nav.Apply("environment.depth.belowTransducer", 5.0);
        clock.Now = clock.Now.AddSeconds(45);
        await Assert.That(nav.FreshnessOf(nav.DepthUpdatedUtc)).IsEqualTo(FieldFreshness.Dead);

        nav.Apply("environment.depth.belowTransducer", 5.3);
        await Assert.That(nav.FreshnessOf(nav.DepthUpdatedUtc)).IsEqualTo(FieldFreshness.Live);
    }

    [Test]
    public async Task FreshnessOf_AtExactly10s_IsStale()
    {
        // The Live -> Stale boundary uses strict less-than (age < 10s),
        // so age == 10s flips to Stale. Pin this so a tweak to the
        // threshold to "<=" can't silently keep the HUD reading "Live"
        // for a sensor that's a tick past the freshness budget.
        var (nav, clock) = Make();
        nav.Apply("environment.depth.belowTransducer", 5.0);
        clock.Now = clock.Now.AddSeconds(10);
        await Assert.That(nav.FreshnessOf(nav.DepthUpdatedUtc)).IsEqualTo(FieldFreshness.Stale);
    }

    [Test]
    public async Task FreshnessOf_JustBefore10s_IsLive()
    {
        // The other side of the same boundary -- 9.999 s is still live.
        var (nav, clock) = Make();
        nav.Apply("environment.depth.belowTransducer", 5.0);
        clock.Now = clock.Now.AddMilliseconds(9999);
        await Assert.That(nav.FreshnessOf(nav.DepthUpdatedUtc)).IsEqualTo(FieldFreshness.Live);
    }

    [Test]
    public async Task FreshnessOf_AtExactly30s_IsDead()
    {
        // Stale -> Dead boundary, strict less-than: 30 s flips to Dead.
        var (nav, clock) = Make();
        nav.Apply("environment.depth.belowTransducer", 5.0);
        clock.Now = clock.Now.AddSeconds(30);
        await Assert.That(nav.FreshnessOf(nav.DepthUpdatedUtc)).IsEqualTo(FieldFreshness.Dead);
    }

    [Test]
    public async Task FreshnessOf_JustBefore30s_IsStale()
    {
        var (nav, clock) = Make();
        nav.Apply("environment.depth.belowTransducer", 5.0);
        clock.Now = clock.Now.AddMilliseconds(29999);
        await Assert.That(nav.FreshnessOf(nav.DepthUpdatedUtc)).IsEqualTo(FieldFreshness.Stale);
    }
}
