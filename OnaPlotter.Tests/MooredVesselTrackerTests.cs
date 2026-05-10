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
    public async Task CleanupVessels_PartialEnumerationFault_LeavesTrackerStateIntact()
    {
        // The hot-path Cleanup overload builds a HashSet of active
        // contexts by enumerating activeVessels. If that enumeration
        // tears (ConcurrentDictionary "collection was modified", or a
        // custom IEnumerable faulting), a partial active set would
        // cause RemoveExcept to drop EVERY vessel that wasn't
        // enumerated yet - an over-cleanup that resets the SOG-dwell
        // ring on still-moored vessels. Pin: enumeration fault is
        // caught and the tick's cleanup is abandoned; tracker state
        // is unchanged.
        var t = new MooredVesselTracker();
        // Track ctx1 + ctx2 first.
        t.IsMoored(Vessel("ctx1", 0.1), DateTime.UtcNow);
        t.IsMoored(Vessel("ctx2", 0.1), DateTime.UtcNow);
        await Assert.That(t.TrackedCount).IsEqualTo(2);

        // Custom IEnumerable that yields one vessel then throws.
        // Without the try/catch in Cleanup the partial 'active' set
        // would be {ctx1}, RemoveExcept would drop ctx2.
        t.Cleanup(new YieldOneThenThrow());

        await Assert.That(t.TrackedCount).IsEqualTo(2)
            .Because("a fault mid-enumeration must not over-cleanup the tracker");
    }

    private sealed class YieldOneThenThrow : IEnumerable<AisVessel>
    {
        public IEnumerator<AisVessel> GetEnumerator()
        {
            yield return new AisVessel("ctx1") { SpeedOverGround = 0.1, Latitude = 47, Longitude = 8 };
            throw new InvalidOperationException("simulated mid-enumeration fault");
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Test]
    public async Task CleanupVessels_NoTrackedDwellers_IsNoOpNoEnumeration()
    {
        // The hot-path optimisation: when nothing is tracked, the
        // overload must short-circuit BEFORE enumerating the vessels
        // collection. We pin this with a deliberately throwing IEnumerable
        // - if Cleanup tries to iterate it, the test fails with a
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
    public async Task NavState_Anchored_MooredAtPlausiblyStationarySpeed()
    {
        // SK navigation.state = "anchored" is authoritative when the
        // reported SOG is plausibly stationary - covers a swinging-at-
        // anchor scenario where SOG is non-zero but well under
        // MooredNavStateMaxSogMs (~2 kn). High-SOG anchor-drag is
        // handled by the spoof gate below; this test pins the normal-
        // anchorage path where the AIS broadcast IS the ground truth.
        var t = new MooredVesselTracker();
        var v = Vessel("ctx1", sogMs: 0.3);          // ~0.6 kn swing
        v.NavigationState = "anchored";

        await Assert.That(t.IsMoored(v, DateTime.UtcNow)).IsTrue();
    }

    [Test]
    public async Task NavState_Moored_HighSogIsTreatedAsLie_FallsThroughToHeuristic()
    {
        // Spoof / stale-navState guard. A vessel claiming
        // navigation.state = "moored" while actually moving at 4 kn is
        // either lying (AIS-spoof - the review's flagged blocker) or
        // hasn't updated its navState since leaving the dock. Either
        // way, granting the CPA-alarm exemption is unsafe. The fix
        // requires SOG below MooredNavStateMaxSogMs (~2 kn) before the
        // explicit signal is honored; above that, fall through to the
        // SOG heuristic which (correctly) returns false.
        var t = new MooredVesselTracker();
        var v = Vessel("spoofer", sogMs: 2.0);       // ~4 kn, well above the gate
        v.NavigationState = "moored";

        await Assert.That(t.IsMoored(v, DateTime.UtcNow)).IsFalse()
            .Because("a 4-kn vessel claiming 'moored' must NOT be exempted from CPA");
    }

    [Test]
    public async Task NavState_Anchored_HighSogIsTreatedAsLie_FallsThroughToHeuristic()
    {
        // Same spoof guard for "anchored". Genuine anchor-drag is at
        // sub-kn drift speeds; a vessel reporting "anchored" + 4 kn
        // SOG is the stale-after-leaving case, not a drag emergency.
        var t = new MooredVesselTracker();
        var v = Vessel("liar", sogMs: 2.0);
        v.NavigationState = "anchored";

        await Assert.That(t.IsMoored(v, DateTime.UtcNow)).IsFalse();
    }

    [Test]
    public async Task NavState_Moored_NullSog_TrustsTheSignal()
    {
        // SOG missing entirely (rare but possible on a freshly-joined
        // AIS context that's only published static data). The spoof
        // gate doesn't have evidence to reject the signal, so it
        // trusts the explicit navState. Pinned because the inverse -
        // returning false on null SOG - would silently un-trust every
        // moored AIS contact during the static-only seconds after
        // they first appear.
        var t = new MooredVesselTracker();
        var v = Vessel("ctx", sogMs: null);
        v.NavigationState = "moored";

        await Assert.That(t.IsMoored(v, DateTime.UtcNow)).IsTrue();
    }

    [Test]
    public async Task NavState_Moored_BoundaryAtSpoofGate()
    {
        // Right at the cap (MooredNavStateMaxSogMs ~ 2*MooredSpeedThresholdMs)
        // the navState is still trusted (uses '<='). Just above, the
        // spoof gate kicks in.
        var t = new MooredVesselTracker();
        var atCap = Vessel("at-cap", sogMs: MooredVesselTracker.MooredNavStateMaxSogMs);
        atCap.NavigationState = "moored";
        await Assert.That(t.IsMoored(atCap, DateTime.UtcNow)).IsTrue();

        var aboveCap = Vessel("above", sogMs: MooredVesselTracker.MooredNavStateMaxSogMs + 0.001);
        aboveCap.NavigationState = "moored";
        await Assert.That(t.IsMoored(aboveCap, DateTime.UtcNow)).IsFalse();
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
        // SOG kept plausibly stationary so the spoof gate trusts the
        // navState - the lowercase contract is what's under test, not
        // the spoof gate (covered separately).
        var t = new MooredVesselTracker();
        var v = new AisVessel("ctx1") { SpeedOverGround = 0.3 };
        v.Apply("navigation.state", System.Text.Json.JsonSerializer.SerializeToElement("Anchored"));

        await Assert.That(v.NavigationState).IsEqualTo("anchored");
        await Assert.That(t.IsMoored(v, DateTime.UtcNow)).IsTrue();
    }
}
