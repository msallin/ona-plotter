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
    public async Task NullSog_NotMoored()
    {
        var t = new MooredVesselTracker();
        var v = Vessel("ctx1", sogMs: null);
        await Assert.That(t.IsMoored(v, DateTime.UtcNow)).IsFalse();
        await Assert.That(t.TrackedCount).IsEqualTo(0);
    }
}
