using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

public class CpaTests
{
    // All inputs: lat/lon degrees, COG radians clockwise from north, SOG m/s.
    private const double Knots = 0.514444;
    private const double DegToRad = Math.PI / 180.0;

    [Test]
    public async Task Null_WhenAnyComponentMissing()
    {
        await Assert.That(Cpa.Compute(0, 0, null, 5, 1, 0, 0, 5)).IsNull();
        await Assert.That(Cpa.Compute(0, 0, 0, null, 1, 0, 0, 5)).IsNull();
        await Assert.That(Cpa.Compute(0, 0, 0, 5, 1, 0, null, 5)).IsNull();
        await Assert.That(Cpa.Compute(0, 0, 0, 5, 1, 0, 0, null)).IsNull();
    }

    [Test]
    public async Task Null_WhenBothStationary()
    {
        await Assert.That(Cpa.Compute(0, 0, 0, 0, 0.01, 0.01, 0, 0)).IsNull();
    }

    [Test]
    public async Task HeadOn_GivesCpaZeroAndPositiveTcpa()
    {
        // Own at (0,0) north at 5 kn, target 1 nm north of us heading south at 5 kn.
        // Closing speed 10 kn, separation 1 nm -> TCPA = 6 min, CPA = 0 nm.
        double oneNmInLat = 1.0 / 60.0;
        var r = Cpa.Compute(0, 0, 0, 5 * Knots, oneNmInLat, 0, Math.PI, 5 * Knots);

        await Assert.That(r).IsNotNull();
        await Assert.That(r!.Value.CpaNm).IsLessThan(0.05);
        await Assert.That(r.Value.TcpaMin).IsGreaterThan(5.5);
        await Assert.That(r.Value.TcpaMin).IsLessThan(6.5);
    }

    [Test]
    public async Task Crossing_AtRightAngles_GivesExpectedNearMiss()
    {
        // Own at origin going north at 6 kn, target 1 nm east going west at 6 kn.
        // The right-angle geometry gives CPA = 1/sqrt(2) nm ~= 0.707 nm at TCPA = 5 min.
        double oneNmInLon = 1.0 / 60.0; // near equator.
        var r = Cpa.Compute(0, 0, 0, 6 * Knots, 0, oneNmInLon, 270 * DegToRad, 6 * Knots);

        await Assert.That(r).IsNotNull();
        await Assert.That(r!.Value.CpaNm).IsGreaterThan(0.6);
        await Assert.That(r.Value.CpaNm).IsLessThan(0.8);
        await Assert.That(r.Value.TcpaMin).IsGreaterThan(4.5);
        await Assert.That(r.Value.TcpaMin).IsLessThan(5.5);
    }

    [Test]
    public async Task Diverging_ReturnsNull()
    {
        // Both heading away from each other - CPA already passed.
        double oneNmInLat = 1.0 / 60.0;
        var r = Cpa.Compute(0, 0, Math.PI, 5 * Knots,
                            oneNmInLat, 0, 0, 5 * Knots);
        await Assert.That(r).IsNull();
    }

    [Test]
    public async Task ParallelSameSpeed_ReturnsNull()
    {
        // Two boats cruising abreast at the same SOG + COG never close. Returning
        // a bogus TCPA=0 (the earlier behaviour) would trip CpaAlarmRule the
        // instant their current separation was inside the alarm radius - false
        // alarm on classic convoy formation. Null is the correct "no closing
        // event".
        double oneNmInLon = 1.0 / 60.0;
        var r = Cpa.Compute(0, 0, 0, 5 * Knots, 0, oneNmInLon, 0, 5 * Knots);

        await Assert.That(r).IsNull();
    }

    [Test]
    public async Task Antimeridian_VesselsAcrossDateLine_ComputesShortSeparation()
    {
        // Two vessels straddling the 180/-180 boundary. Without longitude
        // unwrap, this looks like ~40 000 km of separation and CPA never
        // fires. With unwrap, ~20 km on the short side - and a head-on
        // closing pair gives a real TCPA.
        // Own just west of the date line at lon 179.95, target just east at
        // lon -179.95; each closing at 5 kn along the parallel.
        var r = Cpa.Compute(0, 179.95, Math.PI / 2, 5 * Knots,
                            0, -179.95, 3 * Math.PI / 2, 5 * Knots);

        await Assert.That(r).IsNotNull();
        // Head-on closing pair; CPA is small and TCPA is within an hour.
        await Assert.That(r!.Value.CpaNm).IsLessThan(0.5);
        await Assert.That(r.Value.TcpaMin).IsLessThan(60);
        await Assert.That(r.Value.TcpaMin).IsGreaterThan(5);
    }

    [Test]
    public async Task NaN_Inputs_ReturnNull()
    {
        // Server-side bug or corrupt delta: lat/lon/cog/sog = NaN or Infinity.
        // Must not silently propagate into a non-NaN-looking CPA.
        await Assert.That(Cpa.Compute(double.NaN, 0, 0, 5, 0.01, 0, Math.PI, 5)).IsNull();
        await Assert.That(Cpa.Compute(0, 0, double.NaN, 5, 0.01, 0, 0, 5)).IsNull();
        await Assert.That(Cpa.Compute(0, 0, 0, double.PositiveInfinity, 0.01, 0, 0, 5)).IsNull();
    }

    [Test]
    public async Task Compute_OwnSnapshot_NonFiniteFields_ReturnsNull()
    {
        // Defence-in-depth (n5): PrecomputeOwn already gates non-finite
        // inputs at construction, but a caller could hand-build an
        // OwnSnapshot in tests / via reflection / via a future bug.
        // The struct is public so it's untrusted input - any of
        // Lat/Lon/Vx/Vy/SogMs going non-finite must collapse to null
        // rather than propagate poison numbers into the CPA result.
        var bad = new[]
        {
            new Cpa.OwnSnapshot(double.NaN, 0, 0, 5, 5),
            new Cpa.OwnSnapshot(0, double.NaN, 0, 5, 5),
            new Cpa.OwnSnapshot(0, 0, double.NaN, 5, 5),
            new Cpa.OwnSnapshot(0, 0, 0, double.PositiveInfinity, 5),
            new Cpa.OwnSnapshot(0, 0, 0, 5, double.NaN),
        };
        foreach (var own in bad)
        {
            await Assert.That(Cpa.Compute(in own, 0.01, 0, Math.PI, 5)).IsNull();
        }
    }

    // PrecomputeOwn pins (m6 from the PR-264 follow-up review) -
    // the helper is the gate to the per-target loop and the null-
    // on-non-finite contract is what callers rely on to skip the
    // hot path entirely. Coverage was previously only via the all-
    // args Compute wrapper.

    [Test]
    public async Task PrecomputeOwn_NullOnNullCogOrSog()
    {
        await Assert.That(Cpa.PrecomputeOwn(0, 0, null, 5)).IsNull();
        await Assert.That(Cpa.PrecomputeOwn(0, 0, 0, null)).IsNull();
        await Assert.That(Cpa.PrecomputeOwn(0, 0, null, null)).IsNull();
    }

    [Test]
    public async Task PrecomputeOwn_NullOnEachNonFiniteField()
    {
        // Walk every numeric input independently set to NaN /
        // Infinity. The contract is "skip the per-target loop entirely
        // when own state is corrupt"; if any of these slip through,
        // the per-target trig downstream poisons the result.
        await Assert.That(Cpa.PrecomputeOwn(double.NaN, 0, 0, 5)).IsNull();
        await Assert.That(Cpa.PrecomputeOwn(0, double.NaN, 0, 5)).IsNull();
        await Assert.That(Cpa.PrecomputeOwn(0, 0, double.NaN, 5)).IsNull();
        await Assert.That(Cpa.PrecomputeOwn(0, 0, 0, double.NaN)).IsNull();
        await Assert.That(Cpa.PrecomputeOwn(double.PositiveInfinity, 0, 0, 5)).IsNull();
        await Assert.That(Cpa.PrecomputeOwn(0, double.NegativeInfinity, 0, 5)).IsNull();
    }

    [Test]
    public async Task PrecomputeOwn_PopulatesVxVyFromCogAndSog()
    {
        // North at 5 m/s -> Vx=0, Vy=5. East at 5 m/s -> Vx=5, Vy=0.
        // Vx = sin(cog)*sog, Vy = cos(cog)*sog.
        var north = Cpa.PrecomputeOwn(0, 0, 0, 5);
        await Assert.That(north).IsNotNull();
        await Assert.That(Math.Abs(north!.Value.Vx)).IsLessThan(1e-9);
        await Assert.That(Math.Abs(north.Value.Vy - 5)).IsLessThan(1e-9);
        await Assert.That(north.Value.SogMs).IsEqualTo(5);

        var east = Cpa.PrecomputeOwn(0, 0, Math.PI / 2, 5);
        await Assert.That(east).IsNotNull();
        await Assert.That(Math.Abs(east!.Value.Vx - 5)).IsLessThan(1e-9);
        await Assert.That(Math.Abs(east.Value.Vy)).IsLessThan(1e-9);
    }

    // CurrentDistanceNm pin (M1 from the PR-264 follow-up review):
    // the field is consumed by both CpaAlarmRule and AisPushService
    // as the threat-band gate input, so the value must agree with a
    // haversine reference within projection tolerance (a few percent
    // at sub-100 nm separations - the regime collision-avoidance
    // cares about).

    [Test]
    public async Task Compute_CurrentDistanceNm_MatchesHaversineWithin1Percent()
    {
        // 1 nm north (1/60 deg lat) head-on closing pair.
        double oneNmInLat = 1.0 / 60.0;
        var r = Cpa.Compute(0, 0, 0, 5 * Knots, oneNmInLat, 0, Math.PI, 5 * Knots);

        await Assert.That(r).IsNotNull();
        // Reference: GeoMath.HaversineMeters between own and target.
        double haversineNm = GeoMath.HaversineMeters(0, 0, oneNmInLat, 0) / 1852.0;
        double rel = Math.Abs(r!.Value.CurrentDistanceNm - haversineNm) / haversineNm;
        await Assert.That(rel).IsLessThan(0.01)
            .Because("equirectangular projection vs haversine drift " +
                     "must stay below 1% in the collision-avoidance regime");
    }

    [Test]
    public async Task Compute_CurrentDistanceNm_HandlesAntimeridianWrap()
    {
        // Same antimeridian-straddling geometry as
        // Antimeridian_VesselsAcrossDateLine_ComputesShortSeparation.
        // The unwrap inside Compute makes the projection pick the
        // short arc (~6 nm separation), so CurrentDistanceNm should
        // also be small. Haversine across the dateline naturally
        // takes the great-circle short-arc.
        var r = Cpa.Compute(0, 179.95, Math.PI / 2, 5 * Knots,
                            0, -179.95, 3 * Math.PI / 2, 5 * Knots);
        await Assert.That(r).IsNotNull();
        // 0.1 deg lon at the equator -> ~6 nm. Pin loosely at < 10 nm
        // (anything above means the unwrap regressed and the test
        // would fire the "~40 000 km" failure mode).
        await Assert.That(r!.Value.CurrentDistanceNm).IsLessThan(10);
    }

    [Test]
    public async Task OneStationary_OneApproaching_StillComputes()
    {
        // Own at origin anchored (SOG=0), target 1 nm east coming west at 5 kn.
        double oneNmInLon = 1.0 / 60.0;
        var r = Cpa.Compute(0, 0, 0, 0, 0, oneNmInLon, 270 * DegToRad, 5 * Knots);

        await Assert.That(r).IsNotNull();
        await Assert.That(r!.Value.CpaNm).IsLessThan(0.05);
        await Assert.That(r.Value.TcpaMin).IsGreaterThan(10);
        await Assert.That(r.Value.TcpaMin).IsLessThan(14);
    }

    [Test]
    public async Task Units_CpaInNauticalMiles_TcpaInMinutes()
    {
        // Sanity-check the units. Own 6 kn toward target 2 nm away on collision,
        // target 6 kn toward own. Expect TCPA ~10 min and CPA ~0.
        double twoNmInLat = 2.0 / 60.0;
        var r = Cpa.Compute(0, 0, 0, 6 * Knots,
                            twoNmInLat, 0, Math.PI, 6 * Knots);

        await Assert.That(r).IsNotNull();
        await Assert.That(r!.Value.CpaNm).IsLessThan(0.05);
        await Assert.That(r.Value.TcpaMin).IsGreaterThan(9);
        await Assert.That(r.Value.TcpaMin).IsLessThan(11);
    }

    // Threat classification: maps a CPA result onto the helm's three-band
    // visual model (none / warning / danger). Severity is decided by the
    // target's CURRENT distance from own ship - a vessel currently
    // outside the outer ring gets None even if its projected CPA looks
    // like it'd close (helms read those long crossings as visual noise
    // and asked for the gate). Buddies always collapse to None so a
    // friend nearby can't paint the chart red.
    private const double Radius = 0.5;        // nm (inner / guard ring)
    private const double Lookahead = 10.0;    // min
    // Outer ring is hardcoded at 2× the inner radius
    // (Cpa.OuterRingMultiplier = 2.0). Tests reference 2*Radius
    // explicitly so a refactor that changes the multiplier triggers
    // a deliberate re-read here.
    private const double OuterRing = 2.0 * Radius;

    [Test]
    public async Task Threat_None_WhenBuddy()
    {
        var t = Cpa.ClassifyThreat(cpaNm: 0.1, tcpaMin: 1.0,
            currentDistanceNm: 0.1,
            Radius, Lookahead, isBuddy: true);
        await Assert.That(t).IsEqualTo(Cpa.Threat.None);
    }

    [Test]
    public async Task Threat_None_WhenCpaNull()
    {
        var t = Cpa.ClassifyThreat(null, 1.0, currentDistanceNm: 0.3,
            Radius, Lookahead, false);
        await Assert.That(t).IsEqualTo(Cpa.Threat.None);
    }

    [Test]
    public async Task Threat_None_WhenTcpaNullOrNonPositive()
    {
        await Assert.That(Cpa.ClassifyThreat(0.1, null, 0.3, Radius, Lookahead, false))
            .IsEqualTo(Cpa.Threat.None);
        await Assert.That(Cpa.ClassifyThreat(0.1, 0.0, 0.3, Radius, Lookahead, false))
            .IsEqualTo(Cpa.Threat.None);
        await Assert.That(Cpa.ClassifyThreat(0.1, -1.0, 0.3, Radius, Lookahead, false))
            .IsEqualTo(Cpa.Threat.None);
    }

    [Test]
    public async Task Threat_None_WhenTcpaBeyondLookahead()
    {
        // Vessel will close inside guard zone but not within the
        // lookahead window. Sleep first.
        var t = Cpa.ClassifyThreat(0.1, tcpaMin: 30.0,
            currentDistanceNm: 0.3,
            Radius, Lookahead, false);
        await Assert.That(t).IsEqualTo(Cpa.Threat.None);
    }

    [Test]
    public async Task Threat_None_WhenCpaWontReachGuardZone()
    {
        // 1.5 nm projected miss is outside the guard zone - the vessel
        // won't actually close. Pin this to stop a parallel-course
        // 1.5 nm pass from drawing a crossing line.
        var t = Cpa.ClassifyThreat(cpaNm: 1.5, tcpaMin: 5.0,
            currentDistanceNm: 0.3,
            Radius, Lookahead, false);
        await Assert.That(t).IsEqualTo(Cpa.Threat.None);
    }

    [Test]
    public async Task Threat_Danger_WhenCurrentlyInsideGuardZone()
    {
        // Target inside the inner ring AND projected to close further:
        // red overlay.
        var t = Cpa.ClassifyThreat(0.1, 5.0,
            currentDistanceNm: 0.3,
            Radius, Lookahead, false);
        await Assert.That(t).IsEqualTo(Cpa.Threat.Danger);
    }

    [Test]
    public async Task Threat_Warning_WhenCurrentlyInOuterRing()
    {
        // Target between guard zone and 2× guard zone, projected to
        // close inside guard zone within lookahead: amber overlay.
        var t = Cpa.ClassifyThreat(0.1, 5.0,
            currentDistanceNm: 0.7,   // > 0.5 (Radius), < 1.0 (OuterRing)
            Radius, Lookahead, false);
        await Assert.That(t).IsEqualTo(Cpa.Threat.Warning);
    }

    [Test]
    public async Task Threat_None_WhenCurrentlyOutsideOuterRing()
    {
        // Target 5 nm out with a closing track that would otherwise
        // qualify - the live-data complaint that drove this gate. The
        // long crossing line clutters the chart even though physically
        // accurate; helms wanted the chart to stay quiet until the
        // target is within the outer ring.
        var t = Cpa.ClassifyThreat(0.1, 5.0,
            currentDistanceNm: 5.0,
            Radius, Lookahead, false);
        await Assert.That(t).IsEqualTo(Cpa.Threat.None);
    }

    [Test]
    public async Task Threat_DangerEdge_AtGuardZoneBoundary()
    {
        // Exactly on the inner boundary should be Danger (uses <=).
        // A helm setting "0.5 nm" expects 0.5 to mean "still inside".
        var atBoundary = Cpa.ClassifyThreat(0.1, 5.0,
            currentDistanceNm: Radius,
            Radius, Lookahead, false);
        await Assert.That(atBoundary).IsEqualTo(Cpa.Threat.Danger);

        // Just outside the inner ring -> Warning band.
        var justOutside = Cpa.ClassifyThreat(0.1, 5.0,
            currentDistanceNm: Radius + 0.0001,
            Radius, Lookahead, false);
        await Assert.That(justOutside).IsEqualTo(Cpa.Threat.Warning);
    }

    [Test]
    public async Task Threat_NoneEdge_AtOuterRingBoundary()
    {
        // Exactly on the outer boundary -> Warning (uses <=).
        var atBoundary = Cpa.ClassifyThreat(0.1, 5.0,
            currentDistanceNm: OuterRing,
            Radius, Lookahead, false);
        await Assert.That(atBoundary).IsEqualTo(Cpa.Threat.Warning);

        // Just outside -> None.
        var justOutside = Cpa.ClassifyThreat(0.1, 5.0,
            currentDistanceNm: OuterRing + 0.0001,
            Radius, Lookahead, false);
        await Assert.That(justOutside).IsEqualTo(Cpa.Threat.None);
    }

    // ===== EffectiveRadiusNm =====
    // Pinned because three render paths read this value (alarm rule,
    // chart-side chip classifier, visible guard-ring renderer); drift
    // between any of them surfaces as "chip outside the ring", which
    // is exactly the helm-flagged bug this helper exists to prevent.

    [Test]
    public async Task EffectiveRadius_NotAnchored_ReturnsUnderway()
    {
        await Assert.That(Cpa.EffectiveRadiusNm(0.5, anchorActive: false, anchorMaxRadiusM: null))
            .IsEqualTo(0.5);
        // Anchored=false even with a radius set means we ignore the
        // radius and use the underway value.
        await Assert.That(Cpa.EffectiveRadiusNm(0.5, anchorActive: false, anchorMaxRadiusM: 30.0))
            .IsEqualTo(0.5);
    }

    [Test]
    public async Task EffectiveRadius_Anchored_NarrowsToAnchorSwingRadius()
    {
        // 30 m anchor radius = ~0.0162 nm, much narrower than 0.5
        // underway. Expected: the smaller wins.
        double eff = Cpa.EffectiveRadiusNm(0.5, anchorActive: true, anchorMaxRadiusM: 30.0);
        await Assert.That(eff).IsLessThan(0.05);
        await Assert.That(eff).IsGreaterThan(0.01);
    }

    [Test]
    public async Task EffectiveRadius_AnchorRadiusLargerThanUnderway_KeepsUnderway()
    {
        // Helm asked for 0.3 nm underway but their anchor swing is
        // 1000 m (~0.54 nm). The cautious choice is the smaller -
        // 0.3 nm.
        await Assert.That(Cpa.EffectiveRadiusNm(0.3, anchorActive: true, anchorMaxRadiusM: 1000.0))
            .IsEqualTo(0.3);
    }

    [Test]
    public async Task EffectiveRadius_AnchorActiveButRadiusMissing_FallsBackToUnderway()
    {
        // SK anchoralarm-plugin race: anchor.position arrives a tick
        // before anchor.maxRadius. We don't want chips disappearing
        // for one tick on the path between "anchor active" and "have
        // a radius" - they should keep using the underway value
        // until we know better.
        await Assert.That(Cpa.EffectiveRadiusNm(0.5, anchorActive: true, anchorMaxRadiusM: null))
            .IsEqualTo(0.5);
        await Assert.That(Cpa.EffectiveRadiusNm(0.5, anchorActive: true, anchorMaxRadiusM: 0))
            .IsEqualTo(0.5);
        await Assert.That(Cpa.EffectiveRadiusNm(0.5, anchorActive: true, anchorMaxRadiusM: -10))
            .IsEqualTo(0.5);
        await Assert.That(Cpa.EffectiveRadiusNm(0.5, anchorActive: true, anchorMaxRadiusM: double.NaN))
            .IsEqualTo(0.5);
        await Assert.That(Cpa.EffectiveRadiusNm(0.5, anchorActive: true, anchorMaxRadiusM: double.PositiveInfinity))
            .IsEqualTo(0.5);
    }
}
