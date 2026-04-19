using OnaPlotter.Models;
using OnaPlotter.Services;
using OnaPlotter.Services.Alarms;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the "fire once per entry" contract of the APPROACH alarm:
/// fires exactly once when crossing into the arrival radius for a given
/// waypoint, stays silent on subsequent ticks (so dismissal sticks),
/// re-arms when the vessel leaves the radius OR the active waypoint
/// changes.
/// </summary>
public class WaypointApproachAlarmRuleTests
{
    // Builds a NavigationData with an active course at (wpLat, wpLon) and
    // the given distance-to-go. The actual lat/lon matters for identity
    // (new waypoint = new identity, re-arms); the distance is what the
    // rule compares against the arrival radius.
    private static NavigationData BuildNav(double wpLat, double wpLon, double distMeters)
    {
        var nav = new NavigationData();
        nav.ApplyCourseNextPointPosition(wpLat, wpLon);
        nav.Apply("navigation.courseGreatCircle.nextPoint.distance", distMeters);
        return nav;
    }

    private static AlarmEvaluationContext Ctx(NavigationData nav, double radius)
        => new(nav, [], new FakeSettings { WaypointArrivalRadiusMeters = radius },
            DateTime.UtcNow, _ => false);

    [Test]
    public async Task No_Active_Course_No_Alarm()
    {
        var rule = new WaypointApproachAlarmRule();
        var nav = new NavigationData();
        await Assert.That(rule.Check(Ctx(nav, 50))).IsNull();
    }

    [Test]
    public async Task Outside_Radius_No_Alarm()
    {
        var rule = new WaypointApproachAlarmRule();
        var nav = BuildNav(47.4, 8.5, distMeters: 200);
        await Assert.That(rule.Check(Ctx(nav, 50))).IsNull();
    }

    [Test]
    public async Task Zero_Radius_Disables_Rule()
    {
        // Settings value of 0 is the user-facing "off" switch.
        var rule = new WaypointApproachAlarmRule();
        var nav = BuildNav(47.4, 8.5, distMeters: 5);
        await Assert.That(rule.Check(Ctx(nav, 0))).IsNull();
    }

    [Test]
    public async Task Crosses_Into_Radius_Fires_Once()
    {
        var rule = new WaypointApproachAlarmRule();
        var far = BuildNav(47.4, 8.5, distMeters: 200);
        var near = BuildNav(47.4, 8.5, distMeters: 30);

        await Assert.That(rule.Check(Ctx(far, 50))).IsNull();
        var fire = rule.Check(Ctx(near, 50));
        await Assert.That(fire).IsNotNull();
        await Assert.That(fire!.Title).IsEqualTo("APPROACH");
        await Assert.That(fire.Severity).IsEqualTo(AlarmSeverity.Warn);
    }

    [Test]
    public async Task Does_Not_Re_Fire_While_Still_Inside()
    {
        // Sitting on top of the waypoint for multiple ticks -- we should
        // NOT keep firing the alarm. The helmsman dismissed it once;
        // pestering them for every tick is the bug the stateful rule
        // exists to prevent.
        var rule = new WaypointApproachAlarmRule();
        var near = BuildNav(47.4, 8.5, distMeters: 30);

        var first = rule.Check(Ctx(near, 50));
        await Assert.That(first).IsNotNull();

        // Same waypoint, still inside: silent.
        for (int i = 0; i < 5; i++)
            await Assert.That(rule.Check(Ctx(near, 50))).IsNull();
    }

    [Test]
    public async Task Re_Arms_After_Leaving_And_Returning()
    {
        var rule = new WaypointApproachAlarmRule();
        var near = BuildNav(47.4, 8.5, distMeters: 30);
        var far = BuildNav(47.4, 8.5, distMeters: 200);

        // First entry fires.
        await Assert.That(rule.Check(Ctx(near, 50))).IsNotNull();
        // Leave the radius -- re-arm.
        await Assert.That(rule.Check(Ctx(far, 50))).IsNull();
        // Re-enter: should fire again for the same waypoint.
        await Assert.That(rule.Check(Ctx(near, 50))).IsNotNull();
    }

    [Test]
    public async Task Re_Arms_When_Waypoint_Changes()
    {
        // Even staying continuously "near" something, if the next-point
        // identity changes (user clicked Next Leg on the route), the
        // new waypoint gets its own alarm.
        var rule = new WaypointApproachAlarmRule();
        var wpA = BuildNav(47.4, 8.5, distMeters: 30);
        var wpB = BuildNav(47.5, 8.6, distMeters: 30);

        await Assert.That(rule.Check(Ctx(wpA, 50))).IsNotNull();
        // Same settings, different waypoint identity: re-armed.
        await Assert.That(rule.Check(Ctx(wpB, 50))).IsNotNull();
    }

    [Test]
    public async Task AutoClear_False_So_Alarm_Latches()
    {
        // Latching (AutoClear=false) is what lets the helm arrive and
        // walk forward to handle lines without losing the banner.
        await Assert.That(new WaypointApproachAlarmRule().AutoClear).IsFalse();
    }
}
