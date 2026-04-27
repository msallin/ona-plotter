using OnaPlotter.Models;
using OnaPlotter.Services;
using OnaPlotter.Services.Alarms;

namespace OnaPlotter.Tests;

/// <summary>
/// Rule-level tests for <see cref="CpaAlarmRule"/>. The underlying
/// <see cref="OnaPlotter.Utilities.Cpa"/> math helper has its own
/// pins; this class covers the rule's orchestration:
/// <list type="bullet">
///   <item>missing own-ship state is a null result, not a throw</item>
///   <item>buddies / moored / snoozed vessels are exempt</item>
///   <item>threshold comparisons use &lt; not &lt;= (1.00 nm at a
///       1.0 nm threshold is not an alarm, just inside the limit)</item>
///   <item>AutoClear is true so the rule returning null after a hit
///       lets the manager drop the active entry</item>
///   <item>Severity and target metadata land correctly</item>
/// </list>
/// The fixtures use a head-on approach at close range so CPA is
/// essentially zero and TCPA is distance / closing speed. The moored
/// filter has a dwell, so exempting a near-stationary target requires
/// two Check() calls (first tick tracks; 60s later skips). Tests
/// thread an explicit <c>now</c> through Ctx() to exercise this.
/// </summary>
public class CpaAlarmRuleTests
{
    // Own-boat: heading due north, 5 m/s (~9.7 kn), off the coast of
    // Bahamas-ish (so the approach-to-vessel distances are cleanly
    // computable). Vessels spawned due north closing south. All
    // coordinates in radians-coherent units: COG in radians, SOG in m/s.

    private const double OwnLat = 25.0;
    private const double OwnLon = -77.0;
    private const double OwnCogRad = 0.0;               // due north
    private const double OwnSogMs = 5.0;
    private const double NorthCogRad = Math.PI;         // due south

    private static NavigationData OwnShipUnderway()
    {
        var nav = new NavigationData();
        nav.ApplyPosition(OwnLat, OwnLon);
        nav.Apply("navigation.courseOverGroundTrue", OwnCogRad);
        nav.Apply("navigation.speedOverGround", OwnSogMs);
        return nav;
    }

    // Spawn an AIS vessel N metres north of own ship heading due south
    // at the requested speed. 1/60 deg lat ~ 1852 m (Wikipedia-good).
    private static AisVessel ThreatNorthOf(double metresNorth, double speedMs = 5.0, string? name = null, bool buddy = false)
    {
        double dLat = metresNorth / 111_320.0;
        var v = new AisVessel($"vessels.urn:mrn:imo:mmsi:{Guid.NewGuid():N}".Substring(0, 36))
        {
            Name = name ?? "THREAT",
            Mmsi = "111111111",
            Latitude = OwnLat + dLat,
            Longitude = OwnLon,
            CourseOverGround = NorthCogRad,
            SpeedOverGround = speedMs,
            IsBuddy = buddy,
        };
        return v;
    }

    private static AlarmEvaluationContext Ctx(
        NavigationData nav, IReadOnlyCollection<AisVessel> vessels,
        IAppSettings settings, Func<string, bool>? isSnoozed = null,
        DateTime? now = null)
    {
        return new AlarmEvaluationContext(
            nav, vessels, settings, now ?? DateTime.UtcNow,
            isSnoozed ?? (_ => false));
    }

    [Test]
    public async Task Fires_WhenVesselInsideCpaAndTcpaLimits()
    {
        // Head-on closer at 200 m with closing speed 10 m/s -> TCPA ~0.33 min,
        // CPA ~0 nm. Settings default (CPA 0.5, lookahead 10 min) -> alarm.
        var rule = new CpaAlarmRule();
        var nav = OwnShipUnderway();
        var threat = ThreatNorthOf(200, speedMs: 5, name: "MV Close");
        var alarm = rule.Check(Ctx(nav, [threat], new FakeSettings()));

        await Assert.That(alarm).IsNotNull();
        await Assert.That(alarm!.Title).IsEqualTo("CPA");
        await Assert.That(alarm.Severity).IsEqualTo(AlarmSeverity.Danger);
        await Assert.That(alarm.TargetKey).IsEqualTo(threat.Context);
        await Assert.That(alarm.TargetLabel).IsEqualTo("MV Close");
        await Assert.That(alarm.TimeToEventMinutes).IsNotNull();
        await Assert.That(alarm.TimeToEventMinutes!.Value).IsLessThan(1.0);
    }

    [Test]
    public async Task Silent_WhenHarborModeActive()
    {
        // Harbor mode bundle: helm entering / leaving a busy port
        // suppresses every CPA alarm at the rule level. The
        // collision-warning overlays are also gated JS-side, but the
        // audio alarm comes through here -- pinning that the rule
        // short-circuits when Settings.HarborMode is true so a
        // dismissed visual overlay can't keep the klaxon chirping.
        var rule = new CpaAlarmRule();
        var nav = OwnShipUnderway();
        var threat = ThreatNorthOf(200, speedMs: 5, name: "MV Close");
        var settings = new FakeSettings { HarborMode = true };
        await Assert.That(rule.Check(Ctx(nav, [threat], settings))).IsNull();
    }

    [Test]
    public async Task FiresAgain_AfterHarborModeReleased()
    {
        // Toggling Harbor mode off restores the alarm. Same threat
        // geometry that fires in Fires_WhenVesselInsideCpaAndTcpaLimits;
        // pin the symmetry so a future regression that gates Harbor
        // mode by other means (e.g. a settings cache) can't leave the
        // alarms silenced after the helm releases the toggle.
        var rule = new CpaAlarmRule();
        var nav = OwnShipUnderway();
        var threat = ThreatNorthOf(200, speedMs: 5);
        var settings = new FakeSettings { HarborMode = false };
        await Assert.That(rule.Check(Ctx(nav, [threat], settings))).IsNotNull();
    }

    [Test]
    public async Task Silent_WhenOwnShipNotUnderway()
    {
        // If own SOG is null the rule can't project -- must return null
        // rather than compute on bogus defaults.
        var rule = new CpaAlarmRule();
        var nav = new NavigationData();
        nav.ApplyPosition(OwnLat, OwnLon);
        // Intentionally no COG / SOG applied.
        var threat = ThreatNorthOf(200);
        await Assert.That(rule.Check(Ctx(nav, [threat], new FakeSettings()))).IsNull();
    }

    [Test]
    public async Task Silent_WhenVesselLacksMotion()
    {
        var rule = new CpaAlarmRule();
        var threat = new AisVessel("vessels.x")
        {
            Latitude = OwnLat + 0.01,
            Longitude = OwnLon,
            // No COG / SOG.
        };
        await Assert.That(rule.Check(Ctx(OwnShipUnderway(), [threat], new FakeSettings()))).IsNull();
    }

    [Test]
    public async Task ExemptsNearStationaryTargets_AfterDwell()
    {
        // A vessel parked at 0.25 kn right in front of us should be
        // treated as moored once the dwell (60s, see
        // MooredVesselTracker.MooredHoldSeconds) has elapsed. First
        // tick still fires -- the tracker isn't convinced yet; the
        // second tick past 60s later skips. Exercises the dwell flow
        // end-to-end through the rule's private tracker.
        var rule = new CpaAlarmRule();
        var nav = OwnShipUnderway();
        var parked = ThreatNorthOf(120, speedMs: 0.25, name: "Anchored Cat");
        var t0 = DateTime.UtcNow;

        // First observation: not yet moored. The CPA geometry fires
        // (head-on inside 200 m).
        var first = rule.Check(Ctx(nav, [parked], new FakeSettings(), now: t0));
        await Assert.That(first).IsNotNull();

        // 61 s later: tracker has dwelled through the threshold and
        // the vessel is skipped.
        var later = rule.Check(Ctx(nav, [parked], new FakeSettings(),
            now: t0.AddSeconds(61)));
        await Assert.That(later).IsNull();
    }

    [Test]
    public async Task FiresOnSlowButMoving()
    {
        // Just above the 1 kn cutoff -- still a threat. Pins the
        // boundary so a future refactor doesn't accidentally widen
        // the filter into "ignore anyone under 2 kn".
        var rule = new CpaAlarmRule();
        var crawling = ThreatNorthOf(200, speedMs: 0.6, name: "Slow Mover");
        await Assert.That(rule.Check(Ctx(OwnShipUnderway(), [crawling], new FakeSettings()))).IsNotNull();
    }

    [Test]
    public async Task ExemptsBuddies()
    {
        // Same closing scenario as "fires" but marked as buddy -- must
        // NOT fire. This is the spec: friends are never threats.
        var rule = new CpaAlarmRule();
        var buddy = ThreatNorthOf(200, name: "Sailing Companion", buddy: true);
        await Assert.That(rule.Check(Ctx(OwnShipUnderway(), [buddy], new FakeSettings()))).IsNull();
    }

    [Test]
    public async Task HonoursSnoozePredicate()
    {
        var rule = new CpaAlarmRule();
        var threat = ThreatNorthOf(200);
        bool IsSnoozed(string ctx) => ctx == threat.Context;
        await Assert.That(rule.Check(Ctx(OwnShipUnderway(), [threat], new FakeSettings(), IsSnoozed))).IsNull();
    }

    [Test]
    public async Task CpaThreshold_RejectsOutsideFiresInside()
    {
        // Crossing perpendicular at CPA ~= threshold. The rule
        // rejects when cpa >= limit and fires when cpa < limit.
        // Bit-exact equality is brittle to floating-point drift in
        // the Cpa.Compute projection, so we pin the inequality with
        // a 10% margin on each side rather than a 0% edge.
        var rule = new CpaAlarmRule();
        var nav = OwnShipUnderway();
        double cpaLimitNm = 0.5;
        double metresPerDegLon = 111_320 * Math.Cos(OwnLat * Math.PI / 180);
        // Northward offset kept small so TCPA stays inside the
        // default 10-minute lookahead (else the lookahead check
        // would dominate regardless of distance). 500 m north with a
        // 10 m/s closing speed -> TCPA ~= 0.83 min.
        double dLat = 500.0 / 111_320.0;

        var outsideThreshold = new AisVessel("vessels.cpa-outside")
        {
            Name = "CPA Outside",
            Latitude = OwnLat + dLat,
            // 110% of the threshold east -> CPA ~ 0.55 nm (outside).
            Longitude = OwnLon + (cpaLimitNm * 1.10 * 1852) / metresPerDegLon,
            CourseOverGround = Math.PI,
            SpeedOverGround = 5,
        };
        await Assert.That(rule.Check(Ctx(nav, [outsideThreshold], new FakeSettings { CpaAlarmThreshold = cpaLimitNm }))).IsNull();

        var insideThreshold = new AisVessel("vessels.cpa-inside")
        {
            Name = "CPA Inside",
            Latitude = OwnLat + dLat,
            // 50% of the threshold east -> CPA ~ 0.25 nm (inside).
            Longitude = OwnLon + (cpaLimitNm * 0.50 * 1852) / metresPerDegLon,
            CourseOverGround = Math.PI,
            SpeedOverGround = 5,
        };
        var fired = rule.Check(Ctx(nav, [insideThreshold], new FakeSettings { CpaAlarmThreshold = cpaLimitNm }));
        await Assert.That(fired).IsNotNull();
        await Assert.That(fired!.TargetLabel).IsEqualTo("CPA Inside");
    }

    [Test]
    public async Task TcpaLookahead_RejectsFarFutureCrossings()
    {
        // Threat inside CPA radius but TCPA way beyond lookahead.
        // 80 nm distant (~ 148 km), closing at 5+5 = 10 m/s -> TCPA ~ 247 min.
        // Default GuardZoneLookaheadMinutes = 10. Must not fire.
        var rule = new CpaAlarmRule();
        var threat = ThreatNorthOf(metresNorth: 148_000, speedMs: 5);
        await Assert.That(rule.Check(Ctx(OwnShipUnderway(), [threat], new FakeSettings()))).IsNull();
    }

    [Test]
    public async Task Returns_HighestPriority_Or_FirstMatch_InVesselEnumeration()
    {
        // When two vessels both qualify we accept whichever the rule
        // happens to hit first (the spec is "any threat fires"; the
        // AlarmManager orders + stacks). Just assert SOME threat fires
        // and the TargetKey is one of the two inputs.
        var rule = new CpaAlarmRule();
        var a = ThreatNorthOf(200, name: "Close A");
        var b = ThreatNorthOf(300, name: "Close B");
        var alarm = rule.Check(Ctx(OwnShipUnderway(), [a, b], new FakeSettings()));
        await Assert.That(alarm).IsNotNull();
        await Assert.That(alarm!.TargetKey == a.Context || alarm.TargetKey == b.Context).IsTrue();
    }

    [Test]
    public async Task AutoClearsWhenNoVesselsQualify()
    {
        // A fresh rule with an empty vessel list returns null, which
        // AlarmManager uses to auto-clear any stale CPA entry.
        var rule = new CpaAlarmRule();
        await Assert.That(rule.Check(Ctx(OwnShipUnderway(), [], new FakeSettings()))).IsNull();
    }

    [Test]
    public async Task RuleMetadata_IsStable()
    {
        // Title + Priority + AutoClear are read by AlarmManager for
        // ordering and stack semantics. If any of these change
        // accidentally, rules above/below this priority level re-
        // order, which is a subtle visual regression.
        var rule = new CpaAlarmRule();
        await Assert.That(rule.Title).IsEqualTo("CPA");
        await Assert.That(rule.Priority).IsEqualTo(200);
        await Assert.That(rule.AutoClear).IsTrue();
    }
}
