using OnaPlotter.Models;
using OnaPlotter.Services;

namespace OnaPlotter.Tests;

/// <summary>
/// Tracker is stateless now: one call per vessel, threshold check
/// only. The test surface pins the cutoff (1 kn ~ 0.514 m/s) and
/// the null-SOG case so a future "add dwell back in" refactor has
/// to break these pins deliberately.
/// </summary>
public class MooredVesselTrackerTests
{
    private static AisVessel Vessel(double? sogMs)
    {
        var v = new AisVessel("ctx1");
        v.SpeedOverGround = sogMs;
        v.Latitude = 47;
        v.Longitude = 8;
        return v;
    }

    [Test]
    public async Task MovingVessel_NotMoored()
    {
        var t = new MooredVesselTracker();
        await Assert.That(t.IsMoored(Vessel(3.0))).IsFalse();
    }

    [Test]
    public async Task SlowVessel_Moored_Immediately()
    {
        // Old semantics required a 2-minute dwell; the simplified
        // tracker fires on the first observation so alarms stop
        // chattering the moment a boat drops under the cutoff.
        var t = new MooredVesselTracker();
        await Assert.That(t.IsMoored(Vessel(0.1))).IsTrue();
    }

    [Test]
    public async Task NullSog_NotMoored()
    {
        // No SOG data -> assume motion. Alarm rule falls through to
        // its own null-check and skips this vessel on a different
        // branch, so returning false here is safe.
        var t = new MooredVesselTracker();
        await Assert.That(t.IsMoored(Vessel(null))).IsFalse();
    }

    [Test]
    public async Task BoundaryAtOneKnot()
    {
        // 0.514 m/s ~ 1 kn is the cutoff. Below = moored, at-or-above
        // = still a potential threat.
        var t = new MooredVesselTracker();
        await Assert.That(t.IsMoored(Vessel(0.513))).IsTrue();
        await Assert.That(t.IsMoored(Vessel(0.514))).IsFalse();
        await Assert.That(t.IsMoored(Vessel(0.6))).IsFalse();
    }
}
