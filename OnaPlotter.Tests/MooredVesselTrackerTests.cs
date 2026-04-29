using OnaPlotter.Models;
using OnaPlotter.Services;

namespace OnaPlotter.Tests;

public class MooredVesselTrackerTests
{
    private static AisVessel Vessel(string context, double? sogMs)
    {
        var v = new AisVessel(context);
        v.SpeedOverGround = sogMs;
        v.Latitude = 47;
        v.Longitude = 8;
        return v;
    }

    [Test]
    public async Task MovingVessel_NotMoored_NoStateTracked()
    {
        var t = new MooredVesselTracker();
        var v = Vessel("ctx1", sogMs: 3.0);

        await Assert.That(t.IsMoored(v, DateTime.UtcNow)).IsFalse();
        await Assert.That(t.TrackedCount).IsEqualTo(0);
    }

    [Test]
    public async Task SlowVessel_ReturnsFalse_UntilHoldElapses()
    {
        // Dwell is 60 s. First tick tracks, 30 s in still false,
        // 61 s in flips to true.
        var t = new MooredVesselTracker();
        var v = Vessel("ctx1", sogMs: 0.1);
        var t0 = DateTime.UtcNow;

        await Assert.That(t.IsMoored(v, t0)).IsFalse();
        await Assert.That(t.TrackedCount).IsEqualTo(1);

        await Assert.That(t.IsMoored(v, t0.AddSeconds(30))).IsFalse();
        await Assert.That(t.IsMoored(v, t0.AddSeconds(61))).IsTrue();
    }

    [Test]
    public async Task SlowThenMoving_ResetsClock()
    {
        var t = new MooredVesselTracker();
        var v = Vessel("ctx1", sogMs: 0.1);
        var t0 = DateTime.UtcNow;
        t.IsMoored(v, t0);
        t.IsMoored(v, t0.AddSeconds(30));

        // Vessel picks up speed - state cleared.
        v.SpeedOverGround = 5.0;
        await Assert.That(t.IsMoored(v, t0.AddSeconds(40))).IsFalse();
        await Assert.That(t.TrackedCount).IsEqualTo(0);
    }

    [Test]
    public async Task ThresholdBoundary_OneKnot()
    {
        // 0.514 m/s ~ 1 kn is the cutoff. Strictly below = tracked as
        // slow, at-or-above = not tracked (clock never starts).
        var t = new MooredVesselTracker();
        var t0 = DateTime.UtcNow;

        var justBelow = Vessel("below", sogMs: 0.513);
        await Assert.That(t.IsMoored(justBelow, t0)).IsFalse();          // tracked, not yet moored
        await Assert.That(t.IsMoored(justBelow, t0.AddSeconds(61))).IsTrue();

        var justAbove = Vessel("above", sogMs: 0.514);
        await Assert.That(t.IsMoored(justAbove, t0)).IsFalse();
        await Assert.That(t.IsMoored(justAbove, t0.AddSeconds(300))).IsFalse();
    }

    [Test]
    public async Task Cleanup_DropsVesselsNotInActiveSet()
    {
        var t = new MooredVesselTracker();
        t.IsMoored(Vessel("ctx1", 0.1), DateTime.UtcNow);
        t.IsMoored(Vessel("ctx2", 0.1), DateTime.UtcNow);
        t.IsMoored(Vessel("ctx3", 0.1), DateTime.UtcNow);
        await Assert.That(t.TrackedCount).IsEqualTo(3);

        t.Cleanup(new HashSet<string> { "ctx2" });

        await Assert.That(t.TrackedCount).IsEqualTo(1);
    }

    [Test]
    public async Task Cleanup_EmptyActiveSet_ClearsAll()
    {
        var t = new MooredVesselTracker();
        t.IsMoored(Vessel("ctx1", 0.1), DateTime.UtcNow);
        t.IsMoored(Vessel("ctx2", 0.1), DateTime.UtcNow);

        t.Cleanup(new HashSet<string>());

        await Assert.That(t.TrackedCount).IsEqualTo(0);
    }

    [Test]
    public async Task CleanupVessels_HotPathOverload_DropsAndKeepsCorrectly()
    {
        // The vessels-collection overload is the hot-path entry from
        // CpaAlarmRule + HarborAisFilter. Walk the same scenario as
        // Cleanup_DropsVesselsNotInActiveSet but via the new overload
        // to pin behaviour matches the string-set version.
        var t = new MooredVesselTracker();
        t.IsMoored(Vessel("ctx1", 0.1), DateTime.UtcNow);
        t.IsMoored(Vessel("ctx2", 0.1), DateTime.UtcNow);
        t.IsMoored(Vessel("ctx3", 0.1), DateTime.UtcNow);
        await Assert.That(t.TrackedCount).IsEqualTo(3);

        // Active set carries only ctx2 and a fresh ctx4.
        var active = new[] { Vessel("ctx2", 1.0), Vessel("ctx4", 1.0) };
        t.Cleanup(active);

        await Assert.That(t.TrackedCount).IsEqualTo(1)
            .Because("ctx1 + ctx3 dropped, ctx2 retained");
    }

    [Test]
    public async Task CleanupVessels_NoTrackedDwellers_IsNoOpNoEnumeration()
    {
        // The hot-path optimisation: when nothing is tracked, the
        // overload must short-circuit BEFORE enumerating the vessels
        // collection. We pin this with a deliberately throwing IEnumerable
        // -- if Cleanup tries to iterate it, the test fails with a
        // surfaced exception. Steady-state open-water tick (no dwellers)
        // hits this path on every CpaAlarmRule.Check call.
        var t = new MooredVesselTracker();
        await Assert.That(t.TrackedCount).IsEqualTo(0);

        t.Cleanup(new ThrowingEnumerable());     // must NOT throw
        await Assert.That(t.TrackedCount).IsEqualTo(0);
    }

    private sealed class ThrowingEnumerable : IEnumerable<AisVessel>
    {
        public IEnumerator<AisVessel> GetEnumerator() =>
            throw new InvalidOperationException(
                "Cleanup must short-circuit before enumerating when nothing is tracked");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }

    [Test]
    public async Task NullSog_NotMoored()
    {
        var t = new MooredVesselTracker();
        var v = Vessel("ctx1", sogMs: null);
        await Assert.That(t.IsMoored(v, DateTime.UtcNow)).IsFalse();
        await Assert.That(t.TrackedCount).IsEqualTo(0);
    }

    [Test]
    public async Task NavState_Anchored_MooredImmediately_RegardlessOfSpeed()
    {
        // SK navigation.state = "anchored" (and friends) is authoritative
        // even when SOG is high (anchor-drag scenarios) -- the AIS
        // broadcast is the ground truth.
        var t = new MooredVesselTracker();
        var v = Vessel("ctx1", sogMs: 5.0);          // would be "moving" by heuristic
        v.NavigationState = "anchored";

        await Assert.That(t.IsMoored(v, DateTime.UtcNow)).IsTrue();
    }

    [Test]
    public async Task NavState_Moored_MooredImmediately()
    {
        var t = new MooredVesselTracker();
        var v = Vessel("ctx1", sogMs: 0.1);
        v.NavigationState = "moored";

        // No dwell required when nav-state says so.
        await Assert.That(t.IsMoored(v, DateTime.UtcNow)).IsTrue();
    }

    [Test]
    public async Task NavState_Sailing_NotMoored_EvenAtZeroSpeed()
    {
        // The motivating false-positive (helm-flagged): a sailboat
        // ghosting in light wind reports SOG < 1 kn but is genuinely
        // under way. nav.state = "sailing" must override the heuristic.
        var t = new MooredVesselTracker();
        var v = Vessel("ctx1", sogMs: 0.1);
        v.NavigationState = "sailing";
        var t0 = DateTime.UtcNow;

        await Assert.That(t.IsMoored(v, t0)).IsFalse();
        await Assert.That(t.IsMoored(v, t0.AddSeconds(120))).IsFalse();
        await Assert.That(t.TrackedCount).IsEqualTo(0);
    }

    [Test]
    public async Task NavState_Fishing_NotMoored_OverridesHeuristic()
    {
        // Fishing vessels jogging on station were the second-most-cited
        // false positive: SOG flickers below 1 kn while hauling gear.
        // nav.state = "fishing" exempts them.
        var t = new MooredVesselTracker();
        var v = Vessel("ctx1", sogMs: 0.4);
        v.NavigationState = "fishing";
        var t0 = DateTime.UtcNow;

        await Assert.That(t.IsMoored(v, t0)).IsFalse();
        await Assert.That(t.IsMoored(v, t0.AddSeconds(120))).IsFalse();
    }

    [Test]
    public async Task NavState_Underway_ClearsExistingDwell()
    {
        // Vessel started slow, accumulated dwell, then the AIS plugin
        // begins publishing nav.state = "under way": the new authoritative
        // signal must clear the dwell ring so a re-classification later
        // doesn't carry stale state.
        var t = new MooredVesselTracker();
        var v = Vessel("ctx1", sogMs: 0.1);
        var t0 = DateTime.UtcNow;
        t.IsMoored(v, t0);
        await Assert.That(t.TrackedCount).IsEqualTo(1);

        v.NavigationState = "under way";
        await Assert.That(t.IsMoored(v, t0.AddSeconds(30))).IsFalse();
        await Assert.That(t.TrackedCount).IsEqualTo(0);
    }

    [Test]
    public async Task NavState_Unknown_FallsBackToHeuristic()
    {
        // SK plugin emits a string we don't recognise (vendor-specific
        // mode, or future spec). Don't trust or reject it: just fall
        // through to the SOG-dwell heuristic so behaviour is unchanged
        // from before nav.state was wired up.
        var t = new MooredVesselTracker();
        var v = Vessel("ctx1", sogMs: 0.1);
        v.NavigationState = "running silent running deep";
        var t0 = DateTime.UtcNow;

        await Assert.That(t.IsMoored(v, t0)).IsFalse();
        await Assert.That(t.IsMoored(v, t0.AddSeconds(61))).IsTrue();
    }

    [Test]
    public async Task NavState_CaseInsensitive_ViaApply()
    {
        // AisVessel.Apply lower-cases nav.state on ingest so capitalised
        // server payloads ("Anchored", "AT ANCHOR") still match. Pin
        // both ends together via the Apply path.
        var t = new MooredVesselTracker();
        var v = new AisVessel("ctx1") { SpeedOverGround = 3.0 };
        v.Apply("navigation.state", System.Text.Json.JsonSerializer.SerializeToElement("Anchored"));

        await Assert.That(v.NavigationState).IsEqualTo("anchored");
        await Assert.That(t.IsMoored(v, DateTime.UtcNow)).IsTrue();
    }
}
