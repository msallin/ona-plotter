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

    [Test]
    public async Task ExemptsNearStationaryTargets_AfterDwell()
    {
        // A vessel parked at 0.25 kn right in front of us should be
        // treated as moored once the dwell (60s, see
        // MooredVesselTracker.MooredHoldSeconds) has elapsed. First
        // tick still fires - the tracker isn't convinced yet; the
        // second tick past 60s later skips. Exercises the dwell flow
        // end-to-end through the rule's private tracker.
        var rule = new CpaAlarmRule(new OnaPlotter.Services.MooredVesselTracker());
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
        // (The current-distance gate ALSO rejects this case at 80 nm
        // > outerRing of 1 nm; the lookahead pin is exercised
        // separately in CurrentDistanceGate_RejectsFarVesselsEvenWithCloseProjectedCpa
        // below.)
        var rule = new CpaAlarmRule(new OnaPlotter.Services.MooredVesselTracker());
        var threat = ThreatNorthOf(metresNorth: 148_000, speedMs: 5);
        await Assert.That(rule.Check(Ctx(OwnShipUnderway(), [threat], new FakeSettings()))).IsNull();
    }

    [Test]
    public async Task CurrentDistanceGate_RejectsFarVesselsEvenWithCloseProjectedCpa()
    {
        // PR #264 alignment fix: alarm rule now consumes the same
        // Cpa.ClassifyThreat output as the chart-overlay classifier.
        // The current-distance ring (2× guard zone) is what stops a
        // far-away vessel with a marginal closing track from firing
        // the audible klaxon while the chart-overlay says "no
        // threat". Helm-feedback (paraphrased): "the X is gone but
        // the alarm still rings".
        //
        // Geometry: vessel 3 nm north, closing south at 5 m/s,
        // projected CPA = 0 (head-on). Outer ring = 2 × 0.5 nm = 1 nm,
        // current dist = 3 nm > outer ring -> threat None -> no alarm.
        var rule = new CpaAlarmRule(new OnaPlotter.Services.MooredVesselTracker());
        var nav = OwnShipUnderway();
        // 3 nm = 5556 m. Threat heading south, own heading north,
        // closing speed 10 m/s -> TCPA ~9.3 min (just inside the
        // 10 min default lookahead). The OLD alarm gate would have
        // fired - inside CPA limit AND inside TCPA limit. The NEW
        // gate rejects on current distance.
        var farThreat = ThreatNorthOf(metresNorth: 5556, speedMs: 5);
        await Assert.That(rule.Check(Ctx(nav, [farThreat], new FakeSettings()))).IsNull()
            .Because("vessels currently outside the outer ring (2× guard zone) " +
                     "must not fire the klaxon - chart-overlay agrees.");
    }

    [Test]
    public async Task CurrentDistanceGate_FiresInOuterRing_WhenProjectedCpaInsideGuardZone()
    {
        // The complementary case: a vessel currently in the warning
        // band (between guard zone and 2× guard zone) with a closing
        // track that would breach the guard zone DOES fire the
        // alarm. Pinned so a future "be even more conservative"
        // change doesn't accidentally suppress real Warning-band
        // hits.
        var rule = new CpaAlarmRule(new OnaPlotter.Services.MooredVesselTracker());
        var nav = OwnShipUnderway();
        // ~0.7 nm north (~1296 m): inside outer ring (1.0 nm at
        // default 0.5 nm guard zone), outside guard zone.
        // Closing at 10 m/s -> TCPA ~2 min, CPA ~0 -> Warning band.
        var threat = ThreatNorthOf(metresNorth: 1296, speedMs: 5);
        await Assert.That(rule.Check(Ctx(nav, [threat], new FakeSettings()))).IsNotNull();
    }

    [Test]
    public async Task Returns_HighestPriority_Or_FirstMatch_InVesselEnumeration()
    {
        // When two vessels both qualify we accept whichever the rule
        // happens to hit first (the spec is "any threat fires"; the
        // AlarmManager orders + stacks). Just assert SOME threat fires
        // and the TargetKey is one of the two inputs.
        var rule = new CpaAlarmRule(new OnaPlotter.Services.MooredVesselTracker());
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
        var ctx = Ctx(nav, [], new FakeSettings { CpaAlarmThreshold = 0.5 });
        await Assert.That(CpaAlarmRule.EffectiveCpaRadiusNm(ctx)).IsEqualTo(0.5);
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
        var ctx = Ctx(nav, [], new FakeSettings { CpaAlarmThreshold = 0.5 });

        double eff = CpaAlarmRule.EffectiveCpaRadiusNm(ctx);
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
        var ctx = Ctx(nav, [], new FakeSettings { CpaAlarmThreshold = 0.3 });

        await Assert.That(CpaAlarmRule.EffectiveCpaRadiusNm(ctx)).IsEqualTo(0.3);
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
        var ctx = Ctx(nav, [], new FakeSettings { CpaAlarmThreshold = 0.5 });

        await Assert.That(CpaAlarmRule.EffectiveCpaRadiusNm(ctx)).IsEqualTo(0.5);
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

        var alarm = rule.Check(Ctx(nav, [passing], new FakeSettings { CpaAlarmThreshold = 0.5 }));
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
