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
        // CPA ~0 nm. Settings defaults (CpaAlarmNm 0.1, TcpaAlarmMin 30) -> alarm.
        var rule = new CpaAlarmRule(new OnaPlotter.Services.MooredVesselTracker());
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
        // audio alarm comes through here - pinning that the rule
        // short-circuits when Settings.HarborMode is true so a
        // dismissed visual overlay can't keep the klaxon chirping.
        var rule = new CpaAlarmRule(new OnaPlotter.Services.MooredVesselTracker());
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
        var rule = new CpaAlarmRule(new OnaPlotter.Services.MooredVesselTracker());
        var nav = OwnShipUnderway();
        var threat = ThreatNorthOf(200, speedMs: 5);
        var settings = new FakeSettings { HarborMode = false };
        await Assert.That(rule.Check(Ctx(nav, [threat], settings))).IsNotNull();
    }

    [Test]
    public async Task Silent_WhenOwnShipNotUnderway()
    {
        // If own SOG is null the rule can't project - must return null
        // rather than compute on bogus defaults.
        var rule = new CpaAlarmRule(new OnaPlotter.Services.MooredVesselTracker());
        var nav = new NavigationData();
        nav.ApplyPosition(OwnLat, OwnLon);
        // Intentionally no COG / SOG applied.
        var threat = ThreatNorthOf(200);
        await Assert.That(rule.Check(Ctx(nav, [threat], new FakeSettings()))).IsNull();
    }

    [Test]
    public async Task Silent_WhenVesselLacksMotion()
    {
        var rule = new CpaAlarmRule(new OnaPlotter.Services.MooredVesselTracker());
        var threat = new AisVessel("vessels.x")
        {
            Latitude = OwnLat + 0.01,
            Longitude = OwnLon,
            // No COG / SOG.
        };
        await Assert.That(rule.Check(Ctx(OwnShipUnderway(), [threat], new FakeSettings()))).IsNull();
    }

    private static NavigationData OwnShipAt(double sogMs)
    {
        var nav = new NavigationData();
        nav.ApplyPosition(OwnLat, OwnLon);
        nav.Apply("navigation.courseOverGroundTrue", OwnCogRad);
        nav.Apply("navigation.speedOverGround", sogMs);
        return nav;
    }

    private const double Kn = 0.514444;

    [Test]
    public async Task ExemptsNearStationaryTargets_AfterDwell_WhenOwnShipSlow()
    {
        // Marina manoeuvring: own at 1 kn, a boat parked at 0.25 kn
        // right ahead. Once the moored dwell (60 s, see
        // MooredVesselTracker.MooredHoldSeconds) has elapsed the target
        // is exempt. First tick still fires: the tracker isn't
        // convinced yet.
        var rule = new CpaAlarmRule(new OnaPlotter.Services.MooredVesselTracker());
        var nav = OwnShipAt(1 * Kn);
        var parked = ThreatNorthOf(60, speedMs: 0.25, name: "Anchored Cat");
        var t0 = DateTime.UtcNow;

        var first = rule.Check(Ctx(nav, [parked], new FakeSettings(), now: t0));
        await Assert.That(first).IsNotNull();

        var later = rule.Check(Ctx(nav, [parked], new FakeSettings(),
            now: t0.AddSeconds(61)));
        await Assert.That(later).IsNull();
    }

    [Test]
    public async Task MooredTarget_StillAlarms_WhenOwnShipUnderwayAtSpeed()
    {
        // A moored / anchored hull dead ahead is exactly what the
        // alarm exists for when we are making way: a stationary target
        // on our track is a collision, not a nuisance.
        var rule = new CpaAlarmRule(new OnaPlotter.Services.MooredVesselTracker());
        var moored = ThreatNorthOf(300, speedMs: 0.0, name: "Moored");
        moored.NavigationState = "moored";

        var alarm = rule.Check(Ctx(OwnShipAt(6 * Kn), [moored], new FakeSettings()));

        await Assert.That(alarm).IsNotNull();
    }

    [Test]
    public async Task NotUnderCommand_DriftingTarget_Alarms_OwnAt6Kn()
    {
        // Review case: own 6 kn on a collision course with a vessel not
        // under command drifting at 0.5 kn. NUC is the vessel least able
        // to keep clear; it must never be exempt.
        var rule = new CpaAlarmRule(new OnaPlotter.Services.MooredVesselTracker());
        var nuc = ThreatNorthOf(400, speedMs: 0.5 * Kn, name: "NUC");
        nuc.NavigationState = "not under command";
        var t0 = TestClock.FixedUtcNow;

        // Fire on the first tick AND after the moored dwell would have
        // elapsed, so neither the nav-state path nor the SOG-dwell
        // heuristic can exempt it.
        await Assert.That(rule.Check(Ctx(OwnShipAt(6 * Kn), [nuc], new FakeSettings(), now: t0))).IsNotNull();
        await Assert.That(rule.Check(Ctx(OwnShipAt(6 * Kn), [nuc], new FakeSettings(), now: t0.AddSeconds(120)))).IsNotNull();
    }

    [Test]
    [Arguments("not under command")]
    [Arguments("aground")]
    [Arguments("restricted manoeuverability")]
    [Arguments("restricted maneuverability")]
    public async Task RestrictedOrAgroundTarget_NeverExempt_EvenWhenOwnShipSlow(string navState)
    {
        // These states mark a hazard the helm must avoid, not a parked
        // neighbour; low own speed does not make them safe to ignore.
        var rule = new CpaAlarmRule(new OnaPlotter.Services.MooredVesselTracker());
        var target = ThreatNorthOf(60, speedMs: 0.0, name: "Restricted");
        target.NavigationState = navState;
        var t0 = TestClock.FixedUtcNow;

        await Assert.That(rule.Check(Ctx(OwnShipAt(1 * Kn), [target], new FakeSettings(), now: t0))).IsNotNull();
        await Assert.That(rule.Check(Ctx(OwnShipAt(1 * Kn), [target], new FakeSettings(), now: t0.AddSeconds(120)))).IsNotNull();
    }

    [Test]
    public async Task BothSlowInMarina_MooredTarget_NoAlarm()
    {
        // Review case: own creeping at 0.5 kn through a marina towards a
        // boat on its berth. Moored neighbours are expected at close
        // range here; alarming on every one trains the helm to mute it.
        var rule = new CpaAlarmRule(new OnaPlotter.Services.MooredVesselTracker());
        var berth = ThreatNorthOf(40, speedMs: 0.0, name: "On berth");
        berth.NavigationState = "moored";

        await Assert.That(rule.Check(Ctx(OwnShipAt(0.5 * Kn), [berth], new FakeSettings()))).IsNull();
    }

    [Test]
    public async Task OwnSlowThreshold_Boundary()
    {
        // At the threshold the exemption no longer applies; just below
        // it does.
        var rule = new CpaAlarmRule(new OnaPlotter.Services.MooredVesselTracker());
        var berth = ThreatNorthOf(60, speedMs: 0.0, name: "On berth");
        berth.NavigationState = "anchored";

        await Assert.That(rule.Check(Ctx(OwnShipAt(CpaAlarmRule.OwnShipSlowSogMs), [berth], new FakeSettings()))).IsNotNull();
        await Assert.That(rule.Check(Ctx(OwnShipAt(CpaAlarmRule.OwnShipSlowSogMs - 0.01), [berth], new FakeSettings()))).IsNull();
    }

    [Test]
    public async Task FiresOnSlowButMoving()
    {
        // Just above the 1 kn cutoff - still a threat. Pins the
        // boundary so a future refactor doesn't accidentally widen
        // the filter into "ignore anyone under 2 kn".
        var rule = new CpaAlarmRule(new OnaPlotter.Services.MooredVesselTracker());
        var crawling = ThreatNorthOf(200, speedMs: 0.6, name: "Slow Mover");
        await Assert.That(rule.Check(Ctx(OwnShipUnderway(), [crawling], new FakeSettings()))).IsNotNull();
    }

    [Test]
    public async Task ExemptsBuddies()
    {
        // Same closing scenario as "fires" but marked as buddy - must
        // NOT fire. This is the spec: friends are never threats.
        var rule = new CpaAlarmRule(new OnaPlotter.Services.MooredVesselTracker());
        var buddy = ThreatNorthOf(200, name: "Sailing Companion", buddy: true);
        await Assert.That(rule.Check(Ctx(OwnShipUnderway(), [buddy], new FakeSettings()))).IsNull();
    }

    [Test]
    public async Task HonoursSnoozePredicate()
    {
        var rule = new CpaAlarmRule(new OnaPlotter.Services.MooredVesselTracker());
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
        var rule = new CpaAlarmRule(new OnaPlotter.Services.MooredVesselTracker());
        var nav = OwnShipUnderway();
        double cpaLimitNm = 0.5;
        double metresPerDegLon = 111_320 * Math.Cos(OwnLat * Math.PI / 180);
        // Northward offset kept small so TCPA stays inside the
        // alarm-tier window (else the lookahead check would dominate
        // regardless of distance). 500 m north with a 10 m/s closing
        // speed -> TCPA ~= 0.83 min.
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
        await Assert.That(rule.Check(Ctx(nav, [outsideThreshold], new FakeSettings { CpaAlarmNm = cpaLimitNm }))).IsNull();

        var insideThreshold = new AisVessel("vessels.cpa-inside")
        {
            Name = "CPA Inside",
            Latitude = OwnLat + dLat,
            // 50% of the threshold east -> CPA ~ 0.25 nm (inside).
            Longitude = OwnLon + (cpaLimitNm * 0.50 * 1852) / metresPerDegLon,
            CourseOverGround = Math.PI,
            SpeedOverGround = 5,
        };
        var fired = rule.Check(Ctx(nav, [insideThreshold], new FakeSettings { CpaAlarmNm = cpaLimitNm }));
        await Assert.That(fired).IsNotNull();
        await Assert.That(fired!.TargetLabel).IsEqualTo("CPA Inside");
    }

    [Test]
    public async Task TcpaLookahead_RejectsFarFutureCrossings()
    {
        // Threat inside CPA radius but TCPA way beyond the alarm
        // lookahead. 80 nm distant (~ 148 km), closing at 5+5 = 10 m/s
        // -> TCPA ~ 247 min. Default TcpaAlarmMin = 30. Must not fire.
        var rule = new CpaAlarmRule(new OnaPlotter.Services.MooredVesselTracker());
        var threat = ThreatNorthOf(metresNorth: 148_000, speedMs: 5);
        // Tighten the lookahead so the geometry is unambiguous against
        // the test threshold rather than the production default (which
        // is allowed to drift).
        var settings = new FakeSettings { TcpaAlarmMin = 30.0 };
        await Assert.That(rule.Check(Ctx(OwnShipUnderway(), [threat], settings))).IsNull();
    }

    [Test]
    public async Task FiresWhenProjectedCpaInsideAlarmTier()
    {
        // A vessel head-on closing with projected CPA inside the
        // alarm-tier CPA limit AND TCPA inside the alarm-tier window
        // fires the audible klaxon. Pin so a future "be more
        // conservative" change doesn't suppress real alarms.
        var rule = new CpaAlarmRule(new OnaPlotter.Services.MooredVesselTracker());
        var nav = OwnShipUnderway();
        // ~0.7 nm north (~1296 m), head-on closing at 10 m/s -> TCPA
        // ~2 min, CPA ~0 nm. With FakeSettings defaults (CpaAlarmNm
        // = 0.1, TcpaAlarmMin = 30) the projected CPA of ~0 clears
        // the alarm tier and the alarm fires.
        var threat = ThreatNorthOf(metresNorth: 1296, speedMs: 5);
        await Assert.That(rule.Check(Ctx(nav, [threat], new FakeSettings()))).IsNotNull();
    }

    [Test]
    public async Task CheckMany_EmitsOneAlarmPerQualifyingTarget()
    {
        // Two simultaneous collision threats must both reach the
        // manager. With a single result per tick the rule surfaced
        // whichever vessel AisStore enumerated first and hid the other,
        // and a dismiss on the first then silenced the second for the
        // whole 15 min CPA cooldown.
        var rule = new CpaAlarmRule(new OnaPlotter.Services.MooredVesselTracker());
        var far = ThreatNorthOf(600, name: "Far");
        var near = ThreatNorthOf(200, name: "Near");
        var alarms = ((IAlarmRule)rule).CheckMany(Ctx(OwnShipUnderway(), [far, near], new FakeSettings())).ToList();

        await Assert.That(alarms.Select(a => a.TargetKey!)).IsEquivalentTo([far.Context, near.Context]);
        await Assert.That(alarms.All(a => a.RuleId == AlarmRuleId.Cpa)).IsTrue();
    }

    [Test]
    public async Task Check_ReturnsMostImminentTarget()
    {
        // The single-result view is the most imminent threat, not the
        // first in enumeration order.
        var rule = new CpaAlarmRule(new OnaPlotter.Services.MooredVesselTracker());
        var far = ThreatNorthOf(600, name: "Far");
        var near = ThreatNorthOf(200, name: "Near");
        var alarm = rule.Check(Ctx(OwnShipUnderway(), [far, near], new FakeSettings()));

        await Assert.That(alarm!.TargetKey).IsEqualTo(near.Context);
    }

    [Test]
    public async Task Manager_SurfacesMostImminent_AndDismissIsPerTarget()
    {
        // End to end through the manager: the nearer threat tops the
        // stack (TTI ordering), and dismissing it leaves the other
        // threat on the banner instead of silencing every CPA alarm.
        var rule = new CpaAlarmRule(new OnaPlotter.Services.MooredVesselTracker());
        var clock = TestClock.FixedUtcNow;
        var mgr = new AlarmManager([rule], new DelegateTimeProvider(() => clock));
        var settings = new FakeSettings();
        var far = ThreatNorthOf(600, name: "Far");
        var near = ThreatNorthOf(200, name: "Near");

        mgr.Evaluate(OwnShipUnderway(), [far, near], settings);
        await Assert.That(mgr.ActiveAlarm!.TargetKey).IsEqualTo(near.Context);
        await Assert.That(mgr.AllActiveAlarms.Count).IsEqualTo(2);

        await mgr.DismissAsync(mgr.ActiveAlarm);
        clock = clock.AddSeconds(2);
        mgr.Evaluate(OwnShipUnderway(), [far, near], settings);

        await Assert.That(mgr.AllActiveAlarms.Select(a => a.TargetKey!)).IsEquivalentTo([far.Context]);
    }

    [Test]
    public async Task UsesTrueCourse_WhenHelmPrefersMagneticDisplay()
    {
        // AIS COG is true. Own COG must be true too, whatever the HUD
        // shows: a 20 deg magnetic offset moves a 1.5 km head-on
        // closer from CPA ~0 nm to ~0.14 nm and silences the alarm.
        var rule = new CpaAlarmRule(new OnaPlotter.Services.MooredVesselTracker());
        var nav = new NavigationData { PreferMagneticCourse = true };
        nav.ApplyPosition(OwnLat, OwnLon);
        nav.Apply("navigation.courseOverGroundTrue", 0.0);
        nav.Apply("navigation.courseOverGroundMagnetic", 20 * Math.PI / 180);
        nav.Apply("navigation.speedOverGround", OwnSogMs);
        var threat = ThreatNorthOf(1500, speedMs: 5, name: "Head-on");

        await Assert.That(rule.Check(Ctx(nav, [threat], new FakeSettings()))).IsNotNull();
    }

    [Test]
    public async Task ProjectsTargetFromLastFixToNow()
    {
        // Head-on closer reported 3000 m north 120 s ago at 5 m/s: it is
        // now ~2400 m north. Closing at 10 m/s, TCPA must be ~4 min, not
        // the ~5 min the raw fix gives.
        var rule = new CpaAlarmRule(new OnaPlotter.Services.MooredVesselTracker());
        var t0 = TestClock.FixedUtcNow;
        var threat = ThreatNorthOf(3000, speedMs: 5, name: "Closing");
        threat.PositionUpdatedUtc = t0.AddSeconds(-120);

        var alarm = rule.Check(Ctx(OwnShipUnderway(), [threat], new FakeSettings(), now: t0));

        await Assert.That(alarm!.TimeToEventMinutes!.Value).IsEqualTo(4.0).Within(0.05);
    }

    [Test]
    public async Task SkipsTargetWhoseFixIsTooOldToProject()
    {
        var rule = new CpaAlarmRule(new OnaPlotter.Services.MooredVesselTracker());
        var t0 = TestClock.FixedUtcNow;
        var threat = ThreatNorthOf(200, speedMs: 5, name: "Lost");
        threat.PositionUpdatedUtc = t0 - OnaPlotter.Utilities.AisDeadReckoning.MaxProjectionAge - TimeSpan.FromSeconds(1);

        await Assert.That(rule.Check(Ctx(OwnShipUnderway(), [threat], new FakeSettings(), now: t0))).IsNull();
    }

    [Test]
    public async Task AutoClearsWhenNoVesselsQualify()
    {
        // A fresh rule with an empty vessel list returns null, which
        // AlarmManager uses to auto-clear any stale CPA entry.
        var rule = new CpaAlarmRule(new OnaPlotter.Services.MooredVesselTracker());
        await Assert.That(rule.Check(Ctx(OwnShipUnderway(), [], new FakeSettings()))).IsNull();
    }

    [Test]
    public async Task RuleMetadata_IsStable()
    {
        // Title + Priority + AutoClear are read by AlarmManager for
        // ordering and stack semantics. If any of these change
        // accidentally, rules above/below this priority level re-
        // order, which is a subtle visual regression.
        var rule = new CpaAlarmRule(new OnaPlotter.Services.MooredVesselTracker());
        await Assert.That(rule.Title).IsEqualTo("CPA");
        await Assert.That(rule.Priority).IsEqualTo(200);
        await Assert.That(rule.AutoClear).IsTrue();
    }

    // --- Anchor-aware effective CPA radius -------------------------

    [Test]
    public async Task EffectiveCpaRadius_NotAnchored_UsesUnderwaySetting()
    {
        // Anchor not active -> threshold is the user's underway value.
        var nav = OwnShipUnderway();
        var ctx = Ctx(nav, [], new FakeSettings { CpaAlarmNm = 0.5 });
        await Assert.That(CpaAlarmRule.EffectiveAlarmCpaNm(ctx)).IsEqualTo(0.5);
    }

    [Test]
    public async Task EffectiveCpaRadius_Anchored_ClampsToAnchorRadius()
    {
        // SignalK anchoralarm-plugin active with 30 m max radius:
        // the focus-group field report - a stationary boat in a
        // crowded anchorage was firing CPA alarms on every passing
        // vessel because the underway 0.5 nm threshold (~926 m) was
        // wildly inappropriate. Effective threshold drops to the
        // anchor swing.
        var nav = OwnShipUnderway();
        nav.ApplyAnchorPosition(OwnLat, OwnLon);
        nav.Apply("navigation.anchor.maxRadius", 30.0);
        var ctx = Ctx(nav, [], new FakeSettings { CpaAlarmNm = 0.5 });

        double eff = CpaAlarmRule.EffectiveAlarmCpaNm(ctx);
        // 30 m / 1852 m/nm ~ 0.0162 nm.
        await Assert.That(eff).IsLessThan(0.02);
        await Assert.That(eff).IsGreaterThan(0.01);
    }

    [Test]
    public async Task EffectiveCpaRadius_AnchorRadiusLargerThanUnderway_UsesUnderway()
    {
        // Pathological: someone dropped anchor with a huge max radius
        // (200 m boat-length cable on a 50 ft boat). Don't INCREASE
        // the threshold past the underway value - always use the
        // smaller of the two.
        var nav = OwnShipUnderway();
        nav.ApplyAnchorPosition(OwnLat, OwnLon);
        nav.Apply("navigation.anchor.maxRadius", 5000.0);  // ~2.7 nm
        var ctx = Ctx(nav, [], new FakeSettings { CpaAlarmNm = 0.3 });

        await Assert.That(CpaAlarmRule.EffectiveAlarmCpaNm(ctx)).IsEqualTo(0.3);
    }

    [Test]
    public async Task EffectiveCpaRadius_AnchorActiveButRadiusNotYetArrived_UsesUnderway()
    {
        // Server-anchor activated but the maxRadius field hasn't
        // arrived yet (startup race). Falls through to underway
        // rather than narrow the threshold to a missing value.
        var nav = OwnShipUnderway();
        nav.ApplyAnchorPosition(OwnLat, OwnLon);
        // No maxRadius applied.
        var ctx = Ctx(nav, [], new FakeSettings { CpaAlarmNm = 0.5 });

        await Assert.That(CpaAlarmRule.EffectiveAlarmCpaNm(ctx)).IsEqualTo(0.5);
    }

    [Test]
    public async Task DoesNotFire_WhenVesselPassingOutsideAnchorRadius()
    {
        // Own boat anchored (SOG=0 - a real anchored boat doesn't
        // move). A vessel passing 200 m east of own at 5 m/s heading
        // north never gets closer than 200 m. Underway 0.5 nm
        // threshold (~926 m) would have fired; the anchor 30 m
        // (~0.0162 nm) threshold does not. Pins the focus-group fix:
        // a stationary boat in an anchorage no longer cries CPA on
        // every passing vessel.
        var rule = new CpaAlarmRule(new OnaPlotter.Services.MooredVesselTracker());
        var nav = new NavigationData();
        nav.ApplyPosition(OwnLat, OwnLon);
        nav.Apply("navigation.courseOverGroundTrue", 0.0);
        nav.Apply("navigation.speedOverGround", 0.0);  // anchored
        nav.ApplyAnchorPosition(OwnLat, OwnLon);
        nav.Apply("navigation.anchor.maxRadius", 30.0);

        // Vessel 200 m east of own, heading north (parallel pass).
        double dLon = 200.0 / (111_320.0 * Math.Cos(OwnLat * Math.PI / 180));
        var passing = new AisVessel($"vessels.urn:mrn:imo:mmsi:{Guid.NewGuid():N}".Substring(0, 36))
        {
            Name = "MV Passing",
            Mmsi = "111111111",
            Latitude = OwnLat,
            Longitude = OwnLon + dLon,
            CourseOverGround = 0.0,    // due north
            SpeedOverGround = 5.0,
            IsBuddy = false,
        };

        var alarm = rule.Check(Ctx(nav, [passing], new FakeSettings { CpaAlarmNm = 0.5 }));
        await Assert.That(alarm).IsNull();
    }

    [Test]
    public async Task Banner_DoesNotEnd_With_Trailing_Comma_When_Colregs_Indeterminate()
    {
        // Helm reported a CPA banner ending with " - ,". Root cause:
        // Colregs.ShortLabel / RoleLabel previously returned "" for the
        // Indeterminate / None cases, and the rule's `is not null`
        // check let the empty string through, rendering "{name}: ... - , "
        // when category was indeterminate and role had still resolved.
        // The helpers now return null for those cases; this test pins
        // that the message has no dangling separators when the
        // classifier doesn't produce a useful label.
        var rule = new CpaAlarmRule(new OnaPlotter.Services.MooredVesselTracker());
        var nav = OwnShipUnderway();
        var threat = ThreatNorthOf(200, speedMs: 5, name: "MV Close");
        var alarm = rule.Check(Ctx(nav, [threat], new FakeSettings()));

        await Assert.That(alarm).IsNotNull();
        // The exact suffix depends on the geometry, but the message
        // must NEVER end with a trailing punctuation character that
        // would result from interpolating an empty label.
        await Assert.That(alarm!.Message).DoesNotEndWith(", ");
        await Assert.That(alarm.Message).DoesNotEndWith(",");
        await Assert.That(alarm.Message).DoesNotEndWith(" - ");
        await Assert.That(alarm.Message).DoesNotEndWith("- ,");
        await Assert.That(alarm.Message).DoesNotContain(" - , ");
        // Defensive: also rule out the single-empty-label case.
        await Assert.That(alarm.Message).DoesNotContain("- ,");
    }
}
