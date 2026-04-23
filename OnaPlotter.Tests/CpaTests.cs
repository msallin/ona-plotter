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
        // instant their current separation was inside the alarm radius -- false
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
        // fires. With unwrap, ~20 km on the short side -- and a head-on
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
}
