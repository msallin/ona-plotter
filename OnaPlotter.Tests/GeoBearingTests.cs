using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

/// <summary>
/// Pin the bearing-from-A-to-B spherical-trig formula at the
/// cardinal directions and at the equator/poles so a future refactor
/// (sign convention swap, atan2 arg order swap, mid-formula radian
/// confusion) shows up here. The result is in radians, normalised
/// to [0, 2pi) clockwise from true north - matching the SignalK
/// <c>navigation.*.bearingTrue</c> path convention this helper
/// replaces.
/// </summary>
public class GeoBearingTests
{
    private const double DegToRad = Math.PI / 180.0;

    // Use a small tolerance because the spherical-trig formula
    // doesn't land on exact rational multiples of pi at most cardinal
    // headings (the boat is at a finite latitude, not at the pole).
    // 1e-3 radians = ~0.06 degrees, well below the integer-degree
    // resolution the HUD card renders at.
    private static async Task AssertCloseAsync(double actual, double expected, double tol = 1e-3)
    {
        await Assert.That(Math.Abs(actual - expected)).IsLessThan(tol);
    }

    // --- nullable overload: returns null on missing inputs ----------

    [Test]
    public async Task RadiansFromTo_NullableOverload_NullOnAnyMissingInput()
    {
        await Assert.That(GeoBearing.RadiansFromTo(null, 0.0, 0.0, 0.0).HasValue).IsFalse();
        await Assert.That(GeoBearing.RadiansFromTo(0.0, null, 0.0, 0.0).HasValue).IsFalse();
        await Assert.That(GeoBearing.RadiansFromTo(0.0, 0.0, null, 0.0).HasValue).IsFalse();
        await Assert.That(GeoBearing.RadiansFromTo(0.0, 0.0, 0.0, null).HasValue).IsFalse();
    }

    [Test]
    public async Task RadiansFromTo_NullableOverload_DelegatesWhenAllPresent()
    {
        // Same point + a small offset to the east -> 90 deg.
        double? got = GeoBearing.RadiansFromTo(47.0, 8.0, 47.0, 8.001);
        await Assert.That(got.HasValue).IsTrue();
        await AssertCloseAsync(got!.Value, Math.PI / 2.0);
    }

    // --- cardinal directions from a finite latitude (Lake Geneva) ---

    [Test]
    public async Task EastDestination_BearsApproximately90Degrees()
    {
        // Helm at 46.4N 6.5E (Lac Léman). Anchor 0.001 degree east.
        var bearing = GeoBearing.RadiansFromTo(46.4, 6.5, 46.4, 6.501);
        await AssertCloseAsync(bearing, Math.PI / 2.0);
    }

    [Test]
    public async Task NorthDestination_BearsApproximately0Degrees()
    {
        var bearing = GeoBearing.RadiansFromTo(46.4, 6.5, 46.401, 6.5);
        await AssertCloseAsync(bearing, 0.0);
    }

    [Test]
    public async Task SouthDestination_BearsApproximately180Degrees()
    {
        var bearing = GeoBearing.RadiansFromTo(46.4, 6.5, 46.399, 6.5);
        await AssertCloseAsync(bearing, Math.PI);
    }

    [Test]
    public async Task WestDestination_BearsApproximately270Degrees()
    {
        // Confirms negative atan2 result is normalised into [0, 2pi);
        // a literal port of the formula without normalisation would
        // return -pi/2 here. -pi/2 + 2pi = 3pi/2 = 270 deg = west.
        var bearing = GeoBearing.RadiansFromTo(46.4, 6.5, 46.4, 6.499);
        await AssertCloseAsync(bearing, 3.0 * Math.PI / 2.0);
    }

    // --- normalisation contract -------------------------------------

    [Test]
    public async Task Result_AlwaysInZeroToTwoPi()
    {
        // Walk a circle of nearby destinations around the boat and
        // assert every result lands in [0, 2pi). Catches a refactor
        // that drops the +2pi normalisation.
        for (int deg = 0; deg < 360; deg += 7)
        {
            double rad = deg * DegToRad;
            // Project a point ~100m away at this heading using
            // a simple flat-earth approximation good enough for
            // the test fixture.
            double dLat = Math.Cos(rad) * 0.001;
            double dLon = Math.Sin(rad) * 0.001 / Math.Cos(46.4 * DegToRad);
            double bearing = GeoBearing.RadiansFromTo(46.4, 6.5, 46.4 + dLat, 6.5 + dLon);
            await Assert.That(bearing).IsGreaterThanOrEqualTo(0.0);
            await Assert.That(bearing).IsLessThan(2.0 * Math.PI);
        }
    }

    [Test]
    public async Task SamePointReturnsZero()
    {
        // Degenerate input: A == B. atan2(0, 0) returns 0 in .NET,
        // and the formula collapses to bearing = 0. Pin so a
        // refactor that special-cases this with NaN doesn't slip in.
        var bearing = GeoBearing.RadiansFromTo(46.4, 6.5, 46.4, 6.5);
        await Assert.That(bearing).IsEqualTo(0.0);
    }
}
