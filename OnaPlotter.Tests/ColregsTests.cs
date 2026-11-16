using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

public class ColregsTests
{
    private const double Knots = 0.514444;
    private const double DegToRad = Math.PI / 180.0;
    private const double NmInLat = 1.0 / 60.0;

    // Build a standard scenario: own at origin heading north, target at
    // offset with given COG. All scenarios at 6 kn.
    private static Colregs.Result Run(double tgtLatNm, double tgtLonNm, double tgtCogDeg,
        double ownSogKn = 6, double tgtSogKn = 6, double ownCogDeg = 0)
    {
        return Colregs.Classify(
            ownLat: 0, ownLon: 0,
            ownCogRad: ownCogDeg * DegToRad, ownSogMs: ownSogKn * Knots,
            tgtLat: tgtLatNm * NmInLat, tgtLon: tgtLonNm * NmInLat,
            tgtCogRad: tgtCogDeg * DegToRad, tgtSogMs: tgtSogKn * Knots);
    }

    [Test]
    public async Task HeadOn_ReciprocalCourses()
    {
        // Own at origin going north, target 1 nm north going south.
        var r = Run(tgtLatNm: 1, tgtLonNm: 0, tgtCogDeg: 180);
        await Assert.That(r.Category).IsEqualTo(Colregs.Category.HeadOn);
        await Assert.That(r.Role).IsEqualTo(Colregs.Role.GiveWay);
    }

    [Test]
    public async Task Overtaking_SameDirectionTargetAheadAndSlower()
    {
        var r = Run(tgtLatNm: 1, tgtLonNm: 0, tgtCogDeg: 0, ownSogKn: 8, tgtSogKn: 4);
        await Assert.That(r.Category).IsEqualTo(Colregs.Category.Overtaking);
        await Assert.That(r.Role).IsEqualTo(Colregs.Role.GiveWay);
    }

    [Test]
    public async Task BeingOvertaken_TargetAsternAndFaster()
    {
        // Target 1 nm astern (south), heading north at 10 kn - catching up.
        var r = Run(tgtLatNm: -1, tgtLonNm: 0, tgtCogDeg: 0, ownSogKn: 4, tgtSogKn: 10);
        await Assert.That(r.Category).IsEqualTo(Colregs.Category.BeingOvertaken);
        await Assert.That(r.Role).IsEqualTo(Colregs.Role.StandOn);
    }

    // Places a target at a relative bearing (deg from own bow, own
    // heading north) and range (nm).
    private static (double LatNm, double LonNm) AtRelative(double relDeg, double rangeNm) =>
        (rangeNm * Math.Cos(relDeg * DegToRad), rangeNm * Math.Sin(relDeg * DegToRad));

    [Test]
    public async Task WeOvertake_TargetOnPortBow_Rule13NotCrossing()
    {
        // Review case: own 000 deg at 7 kn, target at relative 320 deg
        // heading 000 at 3 kn. We are 40 deg abaft the target's beam on
        // its starboard quarter (bearing from target = 140 rel), closing:
        // Rule 13, we are overtaking and keep out of the way. A
        // crossing-from-port reading would have told us to stand on.
        var (lat, lon) = AtRelative(320, 0.5);
        var r = Run(lat, lon, tgtCogDeg: 0, ownSogKn: 7, tgtSogKn: 3);
        await Assert.That(r.Category).IsEqualTo(Colregs.Category.Overtaking);
        await Assert.That(r.Role).IsEqualTo(Colregs.Role.GiveWay);
    }

    [Test]
    public async Task TargetOvertakesUs_FromStarboardQuarter_Rule13NotCrossing()
    {
        // Reverse of the above: own 000 deg at 3 kn, target at relative
        // 140 deg (40 deg abaft our starboard beam) heading 000 at 7 kn.
        // It is the overtaking vessel; we stand on. A crossing-from-
        // starboard reading would have told us to give way.
        var (lat, lon) = AtRelative(140, 0.5);
        var r = Run(lat, lon, tgtCogDeg: 0, ownSogKn: 3, tgtSogKn: 7);
        await Assert.That(r.Category).IsEqualTo(Colregs.Category.BeingOvertaken);
        await Assert.That(r.Role).IsEqualTo(Colregs.Role.StandOn);
    }

    [Test]
    [Arguments(113.0, Colregs.Category.BeingOvertaken)]
    [Arguments(247.0, Colregs.Category.BeingOvertaken)]
    [Arguments(112.0, Colregs.Category.CrossingFromStarboard)]
    [Arguments(248.0, Colregs.Category.CrossingFromPort)]
    public async Task Rule13Sector_Is22Point5DegAbaftTheBeam(double relDeg, Colregs.Category expected)
    {
        // Target closing on a parallel course from just inside / just
        // outside the overtaking sector (112.5, 247.5).
        var (lat, lon) = AtRelative(relDeg, 0.5);
        var r = Run(lat, lon, tgtCogDeg: 0, ownSogKn: 3, tgtSogKn: 12);
        await Assert.That(r.Category).IsEqualTo(expected);
    }

    [Test]
    public async Task TargetAbaftBeam_OpeningRange_IsNotOvertaking()
    {
        // Slower target on our quarter falling astern: no encounter.
        var (lat, lon) = AtRelative(150, 0.5);
        var r = Run(lat, lon, tgtCogDeg: 0, ownSogKn: 8, tgtSogKn: 3);
        await Assert.That(r.Category).IsEqualTo(Colregs.Category.Indeterminate);
    }

    [Test]
    public async Task Overtaking_OverridesRule18_PowerOvertakingSail()
    {
        // Rule 13 trumps Rule 18: an overtaking sailing vessel still
        // keeps clear of the power vessel it overtakes, and vice versa.
        var (lat, lon) = AtRelative(320, 0.5);
        var r = Colregs.Classify(0, 0, 0, 7 * Knots,
            lat * NmInLat, lon * NmInLat, 0, 3 * Knots,
            Colregs.VesselType.Sail, Colregs.VesselType.Power);
        await Assert.That(r.Category).IsEqualTo(Colregs.Category.Overtaking);
        await Assert.That(r.Role).IsEqualTo(Colregs.Role.GiveWay);
    }

    [Test]
    public async Task CrossingFromStarboard_WeGiveWay()
    {
        // Target 1 nm east heading west across our bow.
        var r = Run(tgtLatNm: 0, tgtLonNm: 1, tgtCogDeg: 270);
        await Assert.That(r.Category).IsEqualTo(Colregs.Category.CrossingFromStarboard);
        await Assert.That(r.Role).IsEqualTo(Colregs.Role.GiveWay);
    }

    [Test]
    public async Task CrossingFromPort_WeStandOn()
    {
        // Target 1 nm west heading east across our bow.
        var r = Run(tgtLatNm: 0, tgtLonNm: -1, tgtCogDeg: 90);
        await Assert.That(r.Category).IsEqualTo(Colregs.Category.CrossingFromPort);
        await Assert.That(r.Role).IsEqualTo(Colregs.Role.StandOn);
    }

    [Test]
    public async Task BothStationary_Indeterminate()
    {
        var r = Run(tgtLatNm: 1, tgtLonNm: 0, tgtCogDeg: 0, ownSogKn: 0, tgtSogKn: 0);
        await Assert.That(r.Category).IsEqualTo(Colregs.Category.Indeterminate);
    }

    [Test]
    public async Task OwnDrifting_TargetUnderway_Indeterminate()
    {
        // Drifting own-boat (SOG 0.05 kn) being approached head-on by a
        // vessel at 5 kn. HeadOn / GiveWay would imply the drifter
        // could manoeuvre out of the way. Honest answer is
        // Indeterminate: COLREGS assumes both vessels have
        // steerage.
        var r = Run(tgtLatNm: 0.5, tgtLonNm: 0, tgtCogDeg: 180,
            ownSogKn: 0.05, tgtSogKn: 5);
        await Assert.That(r.Category).IsEqualTo(Colregs.Category.Indeterminate);
        await Assert.That(r.Role).IsEqualTo(Colregs.Role.None);
    }

    [Test]
    public async Task TargetStationary_Indeterminate()
    {
        // Mirror: own under way, target drifting. Can't be stand-on
        // against something that isn't moving in a rule-driven sense.
        var r = Run(tgtLatNm: 0.5, tgtLonNm: 0, tgtCogDeg: 180,
            ownSogKn: 5, tgtSogKn: 0.05);
        await Assert.That(r.Category).IsEqualTo(Colregs.Category.Indeterminate);
    }

    [Test]
    public async Task NaN_Inputs_Indeterminate()
    {
        var r = Colregs.Classify(
            ownLat: double.NaN, ownLon: 0, ownCogRad: 0, ownSogMs: 5,
            tgtLat: 0.01, tgtLon: 0, tgtCogRad: Math.PI, tgtSogMs: 5);
        await Assert.That(r.Category).IsEqualTo(Colregs.Category.Indeterminate);
    }

    [Test]
    public async Task OffBeamOnStarboard_StillCrossingFromStbd()
    {
        // Target NE 1 nm, going SW into our path. Bearing ~045, crossing.
        var r = Run(tgtLatNm: 1, tgtLonNm: 1, tgtCogDeg: 225);
        await Assert.That(r.Category).IsEqualTo(Colregs.Category.CrossingFromStarboard);
    }

    [Test]
    public async Task Labels_AreNonEmptyForResolvedCases()
    {
        await Assert.That(Colregs.ShortLabel(Colregs.Category.HeadOn)).IsEqualTo("Head-on");
        await Assert.That(Colregs.RoleLabel(Colregs.Role.GiveWay)).IsEqualTo("Give way");
    }

    [Test]
    public async Task ShortLabel_Returns_Null_For_Indeterminate()
    {
        // Pin the null sentinel: callers (CpaAlarmRule) gate the comma-
        // separated banner suffix on `is not null`. An empty-string
        // return would render "vessel: CPA 0.42nm T -5' - , Give way"
        // when geometry is indeterminate but Rule 18 still resolves a
        // role; the trailing "- ," reads as obviously broken.
        await Assert.That(Colregs.ShortLabel(Colregs.Category.Indeterminate)).IsNull();
    }

    [Test]
    public async Task RoleLabel_Returns_Null_For_None()
    {
        // Same reason as ShortLabel: the "no useful label" sentinel is
        // null, not empty string.
        await Assert.That(Colregs.RoleLabel(Colregs.Role.None)).IsNull();
    }

    [Test]
    public async Task SamePosition_NoCrash_ReturnsResult()
    {
        // Degenerate: two vessels at exactly the same lat/lon. The
        // bearing math collapses to atan2(0,0) = 0, so the classifier
        // sees the target dead ahead. This is unphysical (two boats
        // can't occupy the same point) but a noisy GPS or a SignalK
        // self/AIS dispatch race could produce it. Pin: must not throw,
        // must return SOMETHING - callers can decide whether to
        // suppress display when ownDistance == 0.
        var r = Colregs.Classify(
            ownLat: 47.5, ownLon: 8.5, ownCogRad: 0, ownSogMs: 5,
            tgtLat: 47.5, tgtLon: 8.5, tgtCogRad: Math.PI, tgtSogMs: 5);
        // No exception is the main contract here. Category is whatever
        // the bearing-is-0-and-courses-reciprocal math produces.
        await Assert.That(r.Category).IsNotEqualTo((Colregs.Category)999);
    }

    [Test]
    public async Task InfiniteCog_ReturnsIndeterminate()
    {
        // Defense-in-depth alongside the existing NaN_Inputs test:
        // PositiveInfinity / NegativeInfinity should hit the same
        // !double.IsFinite guard and bail out.
        var r1 = Colregs.Classify(
            ownLat: 0, ownLon: 0, ownCogRad: double.PositiveInfinity, ownSogMs: 5,
            tgtLat: 0.01, tgtLon: 0, tgtCogRad: Math.PI, tgtSogMs: 5);
        await Assert.That(r1.Category).IsEqualTo(Colregs.Category.Indeterminate);

        var r2 = Colregs.Classify(
            ownLat: 0, ownLon: 0, ownCogRad: 0, ownSogMs: 5,
            tgtLat: 0.01, tgtLon: 0, tgtCogRad: 0, tgtSogMs: double.NegativeInfinity);
        await Assert.That(r2.Category).IsEqualTo(Colregs.Category.Indeterminate);
    }
}
