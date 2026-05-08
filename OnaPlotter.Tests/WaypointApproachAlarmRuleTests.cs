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
    // Builds a NavigationData with an active course at (wpLat, wpLon),
    // the given distance-to-go, AND a server-published arrival circle
    // radius. The actual lat/lon matters for identity (new waypoint =
    // new identity, re-arms); distance is what the rule compares against
    // the radius. Radius defaults to 50 m so most tests stay terse; the
    // radius-specific tests pass an explicit value.
    private static NavigationData BuildNav(double wpLat, double wpLon,
        double distMeters, double radiusMeters = 50)
    {
        var nav = new NavigationData();
        nav.ApplyCourseNextPointPosition(wpLat, wpLon);
        nav.Apply("navigation.course.calcValues.distance", distMeters);
        if (radiusMeters > 0)
            nav.Apply("navigation.course.arrivalCircle", radiusMeters);
        return nav;
    }

    // Builds the evaluation context every below-the-line test in this
    // file uses. Note the explicit `ServerSideApproachAlarms = false`
    // override: FakeSettings defaults this to true to mirror prod (the
    // helm runs server-side approach notifications by default), but
    // every test below exercises the CLIENT rule's behaviour and would
    // be muted by the gate without this override. The two tests that
    // pin the gate's true-branch (ServerSideApproachAlarms_Mutes_Client_Rule
    // and Toggling_ServerSide_Off_ResetsLatch_So_FreshFire) build their
    // own contexts inline rather than going through this helper.
    private static AlarmEvaluationContext Ctx(NavigationData nav)
        => new(nav, [], new FakeSettings
            {
                ServerSideApproachAlarms = false,
            },
            DateTime.UtcNow, _ => false);

    [Test]
    public async Task No_Active_Course_No_Alarm()
    {
        var rule = new WaypointApproachAlarmRule();
        var nav = new NavigationData();
        await Assert.That(rule.Check(Ctx(nav))).IsNull();
    }

    [Test]
    public async Task Outside_Radius_No_Alarm()
    {
        var rule = new WaypointApproachAlarmRule();
        var nav = BuildNav(47.4, 8.5, distMeters: 200);
        await Assert.That(rule.Check(Ctx(nav))).IsNull();
    }

    [Test]
    public async Task Server_Arrival_Circle_Drives_Threshold()
    {
        // Server publishes 100m arrival circle. Boat is 75m out -
        // inside the circle. Rule fires.
        var rule = new WaypointApproachAlarmRule();
        var nav = BuildNav(47.4, 8.5, distMeters: 75, radiusMeters: 100);
        await Assert.That(rule.Check(Ctx(nav))).IsNotNull();
    }

    [Test]
    public async Task Outside_Server_Arrival_Circle_No_Alarm()
    {
        // Same boat, smaller server circle (40m): boat is now OUTSIDE,
        // alarm does not fire. The threshold tracks the server's value
        // exactly - no client cap, no client floor.
        var rule = new WaypointApproachAlarmRule();
        var nav = BuildNav(47.4, 8.5, distMeters: 75, radiusMeters: 40);
        await Assert.That(rule.Check(Ctx(nav))).IsNull();
    }

    [Test]
    public async Task Server_Silent_Rule_Dormant()
    {
        // Server doesn't publish navigation.course.arrivalCircle (minimal
        // SK install). Rule is dormant - no client fallback radius is
        // consulted, by design. The chart ring is also hidden in this
        // case, so client and server stay in lock-step.
        var rule = new WaypointApproachAlarmRule();
        var nav = BuildNav(47.4, 8.5, distMeters: 5, radiusMeters: 0);
        await Assert.That(nav.CourseArrivalCircleMeters).IsNull();
        await Assert.That(rule.Check(Ctx(nav))).IsNull();
    }

    [Test]
    public async Task Crosses_Into_Radius_Fires_Once()
    {
        var rule = new WaypointApproachAlarmRule();
        var far = BuildNav(47.4, 8.5, distMeters: 200);
        var near = BuildNav(47.4, 8.5, distMeters: 30);

        await Assert.That(rule.Check(Ctx(far))).IsNull();
        var fire = rule.Check(Ctx(near));
        await Assert.That(fire).IsNotNull();
        await Assert.That(fire!.Title).IsEqualTo("APPROACH");
        await Assert.That(fire.Severity).IsEqualTo(AlarmSeverity.Warn);
    }

    [Test]
    public async Task Does_Not_Re_Fire_While_Still_Inside()
    {
        // Sitting on top of the waypoint for multiple ticks - we should
        // NOT keep firing the alarm. The helmsman dismissed it once;
        // pestering them for every tick is the bug the stateful rule
        // exists to prevent.
        var rule = new WaypointApproachAlarmRule();
        var near = BuildNav(47.4, 8.5, distMeters: 30);

        var first = rule.Check(Ctx(near));
        await Assert.That(first).IsNotNull();

        // Same waypoint, still inside: silent.
        for (int i = 0; i < 5; i++)
            await Assert.That(rule.Check(Ctx(near))).IsNull();
    }

    [Test]
    public async Task Re_Arms_After_Leaving_And_Returning()
    {
        var rule = new WaypointApproachAlarmRule();
        var near = BuildNav(47.4, 8.5, distMeters: 30);
        var far = BuildNav(47.4, 8.5, distMeters: 200);

        // First entry fires.
        await Assert.That(rule.Check(Ctx(near))).IsNotNull();
        // Leave the radius - re-arm.
        await Assert.That(rule.Check(Ctx(far))).IsNull();
        // Re-enter: should fire again for the same waypoint.
        await Assert.That(rule.Check(Ctx(near))).IsNotNull();
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

        await Assert.That(rule.Check(Ctx(wpA))).IsNotNull();
        // Same settings, different waypoint identity: re-armed.
        await Assert.That(rule.Check(Ctx(wpB))).IsNotNull();
    }

    [Test]
    public async Task AutoClear_False_So_Alarm_Latches()
    {
        // Latching (AutoClear=false) is what lets the helm arrive and
        // walk forward to handle lines without losing the banner.
        await Assert.That(new WaypointApproachAlarmRule().AutoClear).IsFalse();
    }

    [Test]
    public async Task TargetKey_Is_Per_Waypoint_So_Banners_Dont_Collapse()
    {
        // A multi-leg route must produce a DISTINCT banner + audio per
        // waypoint. A null TargetKey would collapse every APPROACH into
        // the same stack slot; the next alarm would silently overwrite
        // the previous one's message.
        var rule = new WaypointApproachAlarmRule();
        var nearA = BuildNav(47.4, 8.5, distMeters: 30);
        var nearB = BuildNav(47.5, 8.6, distMeters: 30);

        var a = rule.Check(Ctx(nearA));
        var b = rule.Check(Ctx(nearB));
        await Assert.That(a!.TargetKey).IsNotNull();
        await Assert.That(b!.TargetKey).IsNotNull();
        await Assert.That(a.TargetKey).IsNotEqualTo(b.TargetKey);
    }

    [Test]
    public async Task Waypoint_Identity_Tolerates_Sub_Centimetre_Jitter()
    {
        // SignalK re-emission can drift a waypoint's lat/lon by float-
        // precision noise from JSON round-trip and plugin recomputation.
        // Exact equality would see jitter as "new waypoint" and re-fire
        // mid-dwell. 1e-7 deg ≈ 1 cm jitter must NOT re-trigger.
        var rule = new WaypointApproachAlarmRule();
        var first  = BuildNav(47.4,        8.5,        distMeters: 30);
        var jitter = BuildNav(47.4 + 1e-7, 8.5 + 1e-7, distMeters: 30);

        await Assert.That(rule.Check(Ctx(first))).IsNotNull();
        // Same waypoint within epsilon - must stay silent.
        await Assert.That(rule.Check(Ctx(jitter))).IsNull();
    }

    [Test]
    public async Task ServerSideApproachAlarms_Mutes_Client_Rule()
    {
        // Helm-feedback round: when the helm has opted into server-side
        // approach alarms (default in production), the client rule must
        // mute itself entirely so the SK course-provider plugin's
        // notifications are the single source of truth. Pin so a
        // future "always run client rule as backup" tweak goes red
        // here first.
        var rule = new WaypointApproachAlarmRule();
        var nav = BuildNav(47.4, 8.5, distMeters: 30);     // well inside any radius
        var ctx = new AlarmEvaluationContext(nav, [],
            new FakeSettings { ServerSideApproachAlarms = true },
            DateTime.UtcNow, _ => false);

        await Assert.That(rule.Check(ctx)).IsNull();
    }

    [Test]
    public async Task Toggling_ServerSide_Off_ResetsLatch_So_FreshFire()
    {
        // The gate's _alarmedFor reset is what lets the helm flip the
        // toggle off mid-passage on the SAME waypoint they were already
        // alarmed for client-side, and still see a banner on the next
        // tick. Without the reset, the latch from the prior client-side
        // fire would suppress output until the boat left the radius or
        // the next leg activated.
        //
        // The sequence below is what makes this test load-bearing: we
        // first FIRE client-side so the latch is set, then go through
        // server-on, then back to server-off on the SAME waypoint. If
        // the gate's `_alarmedFor = (null, null)` line is deleted, the
        // final assertion goes red because the latch from step 1 is
        // still in place. (Without step 1, the latch starts already
        // null and the test is a tautology.)
        var rule = new WaypointApproachAlarmRule();
        var nav = BuildNav(47.4, 8.5, distMeters: 30);

        // 1. Server-side OFF: client fires once, latches on the
        //    waypoint identity.
        var clientCtx = new AlarmEvaluationContext(nav, [],
            new FakeSettings { ServerSideApproachAlarms = false },
            DateTime.UtcNow, _ => false);
        await Assert.That(rule.Check(clientCtx)).IsNotNull();
        // 2. Confirm the latch is set: a follow-up tick on the same
        //    waypoint stays silent (this is the "don't re-fire" contract
        //    pinned by Does_Not_Re_Fire_While_Still_Inside above; we
        //    re-verify it here so step 4 is unambiguous).
        await Assert.That(rule.Check(clientCtx)).IsNull();

        // 3. Helm flips toggle ON: the gate must both mute the rule AND
        //    clear _alarmedFor so step 4 isn't blocked by the stale
        //    latch from step 1.
        var serverOnCtx = new AlarmEvaluationContext(nav, [],
            new FakeSettings { ServerSideApproachAlarms = true },
            DateTime.UtcNow, _ => false);
        await Assert.That(rule.Check(serverOnCtx)).IsNull();

        // 4. Helm flips back OFF on the SAME waypoint, SAME distance.
        //    Fires only because step 3 cleared the latch.
        await Assert.That(rule.Check(clientCtx)).IsNotNull();
    }
}
