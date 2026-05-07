using OnaPlotter.Models;
using OnaPlotter.Services;
using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

/// <summary>
/// Pinning the contract for the Harbor-mode AIS filter that
/// Map.razor.PushAisTargets delegates to. The filter's three
/// guarantees are: position-required, harbor-on drops moored, and
/// Cleanup runs once per call. All three are exercised here without
/// touching JS interop or bUnit - which is what TEST-001 of the
/// review flagged as missing coverage.
/// </summary>
public class HarborAisFilterTests
{
    private static AisVessel V(string ctx, double? sogMs = null, string? navState = null,
        double? lat = 47.0, double? lon = 8.0)
    {
        var v = new AisVessel(ctx) { SpeedOverGround = sogMs, NavigationState = navState };
        if (lat is not null) v.Latitude = lat;
        if (lon is not null) v.Longitude = lon;
        return v;
    }

    [Test]
    public async Task HarborOff_ReturnsAllVesselsWithPosition()
    {
        var vessels = new[]
        {
            V("a", sogMs: 0.1),                     // would be moored under harbor mode
            V("b", sogMs: 5.0),                      // moving
            V("c", sogMs: null),                     // unknown speed
        };
        var tracker = new MooredVesselTracker();

        var kept = HarborAisFilter.Apply(vessels, tracker, harbor: false, DateTime.UtcNow);

        await Assert.That(kept.Select(v => v.Context).ToList())
            .IsEquivalentTo(new[] { "a", "b", "c" });
    }

    [Test]
    public async Task NullPosition_DroppedRegardlessOfHarbor()
    {
        // A vessel with no position can't render on the chart even in
        // legacy "show everything" mode. This is the bedrock filter
        // step that runs before harbor classification.
        var noPos = V("ghost", sogMs: 5.0, lat: null, lon: null);
        var fine = V("real", sogMs: 5.0);
        var tracker = new MooredVesselTracker();

        var kept = HarborAisFilter.Apply(new[] { noPos, fine }, tracker, harbor: false, DateTime.UtcNow);

        await Assert.That(kept.Select(v => v.Context).ToList())
            .IsEquivalentTo(new[] { "real" });
    }

    [Test]
    public async Task HarborOn_DropsMooredVessels_KeepsActive()
    {
        // Tracker classifies the slow vessel as moored after dwell;
        // the filter must drop it. The active vessel passes through.
        var moored = V("anchor", sogMs: 0.1);
        var active = V("under-way", sogMs: 4.5);
        var tracker = new MooredVesselTracker();
        var t0 = DateTime.UtcNow;

        // First call seeds dwell; both vessels still visible.
        var first = HarborAisFilter.Apply(new[] { moored, active }, tracker, harbor: true, t0);
        await Assert.That(first.Count).IsEqualTo(2);

        // After dwell elapses, the moored vessel drops.
        var second = HarborAisFilter.Apply(
            new[] { moored, active }, tracker, harbor: true, t0.AddSeconds(70));

        await Assert.That(second.Select(v => v.Context).ToList())
            .IsEquivalentTo(new[] { "under-way" });
    }

    [Test]
    public async Task HarborOn_NavStateAnchored_DroppedImmediately()
    {
        // SK navigation.state = "anchored" is authoritative - no
        // dwell required, no SOG check. Pins the integration with the
        // tracker's nav.state branch.
        var anchored = V("a1", sogMs: 0.0, navState: "anchored");
        var sailing  = V("s1", sogMs: 4.0, navState: "sailing");
        var tracker = new MooredVesselTracker();

        var kept = HarborAisFilter.Apply(new[] { anchored, sailing }, tracker, harbor: true, DateTime.UtcNow);

        await Assert.That(kept.Select(v => v.Context).ToList())
            .IsEquivalentTo(new[] { "s1" });
    }

    [Test]
    public async Task HarborOn_CallsCleanup_ToBoundDwellRing()
    {
        // The dwell ring must not leak across long sessions. Call the
        // filter twice - the second call's vessels list excludes the
        // first call's contexts, so Cleanup must drop them.
        var tracker = new MooredVesselTracker();
        var t0 = DateTime.UtcNow;
        HarborAisFilter.Apply(new[]
        {
            V("a", sogMs: 0.1), V("b", sogMs: 0.1), V("c", sogMs: 0.1)
        }, tracker, harbor: true, t0);

        await Assert.That(tracker.TrackedCount).IsEqualTo(3);

        // Only "b" stays visible; a + c vanished from the AIS feed.
        HarborAisFilter.Apply(new[] { V("b", sogMs: 0.1) }, tracker, harbor: true, t0.AddSeconds(5));

        await Assert.That(tracker.TrackedCount).IsEqualTo(1);
    }

    [Test]
    public async Task HarborOff_DoesNotCallCleanup()
    {
        // When harbor is off, the dwell ring isn't consulted, so we
        // shouldn't tick Cleanup either. The tracker's existing state
        // must remain untouched.
        var tracker = new MooredVesselTracker();
        var t0 = DateTime.UtcNow;
        // Seed dwell via the tracker directly.
        tracker.IsMoored(V("ghost", sogMs: 0.1), t0);
        await Assert.That(tracker.TrackedCount).IsEqualTo(1);

        // Now run the filter with harbor=false on a different vessel set.
        // "ghost" is no longer in the visible list - but because harbor
        // is off, Cleanup must NOT run, so "ghost" stays in the dwell
        // ring waiting for the next harbor toggle.
        HarborAisFilter.Apply(new[] { V("real", sogMs: 5.0) }, tracker, harbor: false, t0.AddSeconds(2));
        await Assert.That(tracker.TrackedCount).IsEqualTo(1);
    }
}
