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
        var t = new MooredVesselTracker();
        var v = Vessel("ctx1", sogMs: 0.1);
        var t0 = DateTime.UtcNow;

        // First observation: tracked but not yet moored.
        await Assert.That(t.IsMoored(v, t0)).IsFalse();
        await Assert.That(t.TrackedCount).IsEqualTo(1);

        // Halfway through the hold window.
        await Assert.That(t.IsMoored(v, t0.AddSeconds(60))).IsFalse();

        // Past the hold window.
        await Assert.That(t.IsMoored(v, t0.AddSeconds(121))).IsTrue();
    }

    [Test]
    public async Task SlowThenMoving_ResetsClock()
    {
        var t = new MooredVesselTracker();
        var v = Vessel("ctx1", sogMs: 0.1);
        var t0 = DateTime.UtcNow;
        t.IsMoored(v, t0);
        t.IsMoored(v, t0.AddSeconds(60));

        // Vessel picks up speed - state cleared.
        v.SpeedOverGround = 5.0;
        await Assert.That(t.IsMoored(v, t0.AddSeconds(70))).IsFalse();
        await Assert.That(t.TrackedCount).IsEqualTo(0);
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
