using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the C# port of <c>OnaPlotter/wwwroot/js/geoMath.js</c>. Cases
/// mirror the JS test file (geoMath.test.js) where it has equivalent
/// assertions, plus C#-specific edge cases for the nullable SOG path
/// in <see cref="GeoMath.VectorEnd"/>.
/// </summary>
public class GeoMathTests
{
    private const double Epsilon = 1e-6;

    [Test]
    public async Task HaversineMeters_ZeroDistance_ReturnsZero()
    {
        await Assert.That(GeoMath.HaversineMeters(48.0, -123.0, 48.0, -123.0))
            .IsCloseTo(0, Epsilon);
    }

    [Test]
    public async Task HaversineMeters_OneDegreeLatitudeAtEquator_AbouT111km()
    {
        // 1 deg of latitude is ~111.195 km on a sphere of EarthRadius
        // 6,371,000 m. Tolerance 100 m absorbs the spherical-vs-WGS84
        // approximation; if a future refactor drops the Earth radius
        // the test will fail at the kilometre level, not the metre
        // level (intentional looseness).
        double d = GeoMath.HaversineMeters(0.0, 0.0, 1.0, 0.0);
        await Assert.That(d).IsBetween(111_100, 111_300);
    }

    [Test]
    public async Task HaversineMeters_AcrossInternationalDateLine_ReturnsShortPath()
    {
        // (0, 179) to (0, -179) is a 2-degree gap straddling the IDL,
        // not 358 degrees the other way around the planet. Haversine's
        // dLam = (lon2 - lon1) * RAD applied directly produces the
        // correct short-path distance because sin/cos of the resulting
        // ~6.28 rad still resolves to the small-angle answer. Pin so a
        // refactor that "normalises" longitudes to [0, 360) before the
        // diff (and then takes the wrong wrap branch) would surface.
        double d = GeoMath.HaversineMeters(0, 179, 0, -179);
        // 2 deg of longitude at the equator ≈ 222.4 km.
        await Assert.That(d).IsBetween(220_000, 225_000);
    }

    [Test]
    public async Task BearingDeg_WestwardAcrossInternationalDateLine_ReturnsApproximately270()
    {
        // Going west across the IDL: (0, -175) -> (0, 175). The short
        // path is 10 degrees due west (across the dateline); bearing
        // should be ~270, not ~90 (the wrap-around-the-planet long
        // way). Pin so a refactor that "normalises" longitudes before
        // the diff and takes the wrong wrap branch surfaces here.
        double b = GeoMath.BearingDeg(0, -175, 0, 175);
        await Assert.That(b).IsBetween(269.5, 270.5);
    }

    [Test]
    public async Task BearingDeg_EastwardAcrossInternationalDateLine_ReturnsApproximately90()
    {
        // The opposite direction: (0, 175) -> (0, -175). Short path is
        // 10 degrees due east across the dateline; bearing ~90, not
        // ~270.
        double b = GeoMath.BearingDeg(0, 175, 0, -175);
        await Assert.That(b).IsBetween(89.5, 90.5);
    }

    [Test]
    public async Task HaversineMeters_Antipodal_ReturnsHalfCircumference()
    {
        // (0, 0) to (0, 180) -- diametrically opposite on the equator.
        // Half the great-circle = pi * R = ~20,015 km. Pin so the
        // numerical degradation at the antipode (sin(pi/2) = 1 case
        // where the inner product loses precision) doesn't grow
        // unbounded across refactors.
        double d = GeoMath.HaversineMeters(0, 0, 0, 180);
        // Tolerance 1 km on a 20,000 km half-circumference (5 ppm).
        await Assert.That(d).IsBetween(20_014_000, 20_016_000);
    }

    [Test]
    public async Task BearingDeg_DueNorth_ReturnsZero()
    {
        // Same lon, higher lat -> north. Atan2-via-sphere rounds
        // exactly to 0 for the cardinal cases.
        await Assert.That(GeoMath.BearingDeg(48.0, -123.0, 49.0, -123.0))
            .IsCloseTo(0, Epsilon);
    }

    [Test]
    public async Task BearingDeg_DueEast_ReturnsNinety()
    {
        await Assert.That(GeoMath.BearingDeg(0.0, 0.0, 0.0, 1.0))
            .IsCloseTo(90, Epsilon);
    }

    [Test]
    public async Task BearingDeg_DueSouth_ReturnsOneEighty()
    {
        await Assert.That(GeoMath.BearingDeg(48.0, -123.0, 47.0, -123.0))
            .IsCloseTo(180, Epsilon);
    }

    [Test]
    public async Task BearingDeg_DueWest_ReturnsTwoSeventy()
    {
        await Assert.That(GeoMath.BearingDeg(0.0, 0.0, 0.0, -1.0))
            .IsCloseTo(270, Epsilon);
    }

    [Test]
    public async Task BearingDeg_AlwaysInZeroToThreeSixtyRange()
    {
        // Property: regardless of where two points are, BearingDeg
        // wraps into [0, 360). The atan2+%360 shape used to occasionally
        // return -180 in earlier JS versions; the +360 added before %
        // is the fix being pinned here.
        var rng = new Random(unchecked((int)0xCA_FE_BA_BE));
        for (int i = 0; i < 200; i++)
        {
            double lat1 = rng.NextDouble() * 180 - 90;
            double lon1 = rng.NextDouble() * 360 - 180;
            double lat2 = rng.NextDouble() * 180 - 90;
            double lon2 = rng.NextDouble() * 360 - 180;
            double b = GeoMath.BearingDeg(lat1, lon1, lat2, lon2);
            await Assert.That(b).IsGreaterThanOrEqualTo(0);
            await Assert.That(b).IsLessThan(360);
        }
    }

    [Test]
    public async Task DestPoint_Roundtrip_ReturnsApproximateOriginal()
    {
        // Walk 1 km north from a known fix; the destination's lat
        // should be measurably higher and its lon unchanged. Walking
        // back south 1 km from the destination should land within a
        // metre or two of the start (rounded to 8 decimal places).
        var (lat2, lon2) = GeoMath.DestPoint(48.0, -123.0, bearingRad: 0, distMeters: 1000);
        await Assert.That(lat2).IsGreaterThan(48.0);
        await Assert.That(Math.Abs(lon2 - (-123.0))).IsLessThan(1e-9);

        var (latBack, lonBack) = GeoMath.DestPoint(lat2, lon2, bearingRad: Math.PI, distMeters: 1000);
        await Assert.That(Math.Abs(latBack - 48.0)).IsLessThan(1e-7);
        await Assert.That(Math.Abs(lonBack - (-123.0))).IsLessThan(1e-9);
    }

    [Test]
    public async Task VectorEnd_BelowSpeedThreshold_ReturnsNull()
    {
        // 0.05 m/s is well below the 0.1 m/s cutoff; the helm's
        // anchored boat shouldn't render a stub vector.
        var v = GeoMath.VectorEnd(48.0, -123.0, cogRad: 0, sogMs: 0.05);
        await Assert.That(v).IsNull();
    }

    [Test]
    public async Task VectorEnd_NullCog_ReturnsNull()
    {
        // Mirrors geoMath.js: a vessel without a reported COG produces
        // no vector. Earlier C# port silently treated default(double)
        // as cogRad=0 and rendered a misleading due-north stub.
        await Assert.That(GeoMath.VectorEnd(48.0, -123.0, cogRad: null, sogMs: 5.0)).IsNull();
    }

    [Test]
    public async Task VectorEnd_NullSog_ReturnsNull()
    {
        // Same contract for missing SOG.
        await Assert.That(GeoMath.VectorEnd(48.0, -123.0, cogRad: 0, sogMs: null)).IsNull();
    }

    [Test]
    public async Task VectorEnd_AtSpeedThreshold_ReturnsNonNull()
    {
        // Exactly 0.1 m/s is the boundary the JS uses (sogMs < 0.1
        // returns null; >= 0.1 produces a vector).
        var v = GeoMath.VectorEnd(48.0, -123.0, cogRad: 0, sogMs: 0.1);
        await Assert.That(v).IsNotNull();
    }

    [Test]
    public async Task VectorEnd_DefaultMinutes_TenMinuteHorizon()
    {
        // 5 m/s for 10 min = 3000 m north = ~0.027 deg of latitude.
        var v = GeoMath.VectorEnd(48.0, -123.0, cogRad: 0, sogMs: 5.0);
        await Assert.That(v).IsNotNull();
        var (vLat, _) = v!.Value;
        // 3000 m at this latitude is roughly 0.027 deg; tolerate a
        // small absolute error from spherical math.
        await Assert.That(vLat).IsBetween(48.025, 48.030);
    }

    [Test]
    public async Task VectorEnd_ExplicitMinutes_OverridesDefault()
    {
        // Pass 5 minutes; expected reach = 5 m/s * 5 min = 1500 m
        // (half the default-10-minute case above).
        var v10 = GeoMath.VectorEnd(48.0, -123.0, cogRad: 0, sogMs: 5.0, minutes: 10.0);
        var v5 = GeoMath.VectorEnd(48.0, -123.0, cogRad: 0, sogMs: 5.0, minutes: 5.0);
        await Assert.That(v10).IsNotNull();
        await Assert.That(v5).IsNotNull();
        // 5-minute reach should be roughly half the 10-minute reach
        // in latitude delta (small-angle linearisation; the spherical
        // term contributes less than 0.001 deg of error here).
        double d10 = v10!.Value.Lat - 48.0;
        double d5 = v5!.Value.Lat - 48.0;
        await Assert.That(d5 / d10).IsBetween(0.499, 0.501);
    }

    [Test]
    public async Task VectorEnd_NullOrInvalidMinutes_FallsBackToDefault()
    {
        // null -> default; 0 -> default; negative -> default; NaN -> default.
        var defaultVal = GeoMath.VectorEnd(48.0, -123.0, cogRad: 0, sogMs: 5.0, minutes: null);
        var zero = GeoMath.VectorEnd(48.0, -123.0, cogRad: 0, sogMs: 5.0, minutes: 0);
        var negative = GeoMath.VectorEnd(48.0, -123.0, cogRad: 0, sogMs: 5.0, minutes: -10);
        var nan = GeoMath.VectorEnd(48.0, -123.0, cogRad: 0, sogMs: 5.0, minutes: double.NaN);

        await Assert.That(zero!.Value.Lat).IsCloseTo(defaultVal!.Value.Lat, 1e-9);
        await Assert.That(negative!.Value.Lat).IsCloseTo(defaultVal.Value.Lat, 1e-9);
        await Assert.That(nan!.Value.Lat).IsCloseTo(defaultVal.Value.Lat, 1e-9);
    }
}

public class SpeedBucketsTests
{
    [Test]
    public async Task Bucket_Null_ReturnsZero()
    {
        await Assert.That(SpeedBuckets.Bucket(null)).IsEqualTo(0);
    }

    [Test]
    [Arguments(-1.0, 0)]      // negative (shouldn't happen but defensive)
    [Arguments(0.0, 0)]       // exactly 0 -> bucket 0
    [Arguments(0.5, 0)]       // below 1 -> bucket 0
    [Arguments(1.0, 1)]       // exactly threshold -> bucket 1
    [Arguments(1.5, 1)]
    [Arguments(2.0, 2)]
    [Arguments(2.9999, 2)]
    [Arguments(3.0, 3)]
    [Arguments(4.9999, 3)]
    [Arguments(5.0, 4)]
    [Arguments(7.9999, 4)]
    [Arguments(8.0, 5)]       // top bucket
    [Arguments(20.0, 5)]      // anything above the last threshold stays in top bucket
    public async Task Bucket_TableMatches_JsImplementation(double sogMs, int expected)
    {
        await Assert.That(SpeedBuckets.Bucket(sogMs)).IsEqualTo(expected);
    }

    [Test]
    public async Task Buckets_Constants_HoldExpectedValues()
    {
        // Pinned because JS-side renderers index colour / label tables
        // off these thresholds; a drift would silently re-bucket every
        // history segment in the playback view.
        await Assert.That(SpeedBuckets.Buckets).IsEquivalentTo(new[] { 0.0, 1.0, 2.0, 3.0, 5.0, 8.0 });
    }
}
