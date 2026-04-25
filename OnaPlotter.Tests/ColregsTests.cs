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
        // vessel at 5 kn. The earlier rule returned HeadOn / GiveWay,
        // implying the drifter could manoeuvre out of the way. Honest
        // answer is Indeterminate: COLREGS assumes both vessels have
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
        await Assert.That(Colregs.ShortLabel(Colregs.Category.Indeterminate)).IsEqualTo("");
        await Assert.That(Colregs.RoleLabel(Colregs.Role.GiveWay)).IsEqualTo("Give way");
    }

    [Test]
    public async Task SamePosition_NoCrash_ReturnsResult()
    {
        // Degenerate: two vessels at exactly the same lat/lon. The
        // bearing math collapses to atan2(0,0) = 0, so the classifier
        // sees the target dead ahead. This is unphysical (two boats
        // can't occupy the same point) but a noisy GPS or a SignalK
        // self/AIS dispatch race could produce it. Pin: must not throw,
        // must return SOMETHING -- callers can decide whether to
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
