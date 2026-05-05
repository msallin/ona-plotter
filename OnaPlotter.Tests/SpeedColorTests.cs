using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

/// <summary>Pin the SOG -&gt; rgb(...) ramp at every boundary speed
/// the helm cares about. The JS layer mirrors this in
/// <c>wwwroot/js/format.js</c>; if the constants here change, the
/// JS mirror must move too -- otherwise the chart paints the same
/// track in two different colours depending on which renderer touched
/// it last.</summary>
public class SpeedColorTests
{
    [Test]
    public async Task Rgb_NullSog_DefaultBlue()
    {
        await Assert.That(SpeedColor.Rgb(null)).IsEqualTo(SpeedColor.DefaultRgb);
    }

    [Test]
    public async Task Rgb_Stopped_StartOfBlueGreenSegment()
    {
        // 0 m/s -> t=0 in the first half: pure blue start (59,130,246).
        await Assert.That(SpeedColor.Rgb(0)).IsEqualTo("rgb(59,130,246)");
    }

    [Test]
    public async Task Rgb_AtMidpoint_GreenAnchor()
    {
        // ~3 knots = 1.543 m/s sits at t=0.5*8/8 -- but the math uses
        // kn/8 directly. 3 knots in m/s is 3 / 1.94384 = 1.5432... .
        // At kn=4 (m/s = 4/1.94384), t = 4/8 = 0.5 -> the seam between
        // the two interpolation halves -> exact green anchor (34,197,94).
        var ms = 4.0 / Format.MsToKnots;
        await Assert.That(SpeedColor.Rgb(ms)).IsEqualTo("rgb(34,197,94)");
    }

    [Test]
    public async Task Rgb_FastSog_ClampsToYellow()
    {
        // 8+ knots -> t pinned at 1 -> yellow endpoint (234,179,8).
        var ms = 8.0 / Format.MsToKnots;
        await Assert.That(SpeedColor.Rgb(ms)).IsEqualTo("rgb(234,179,8)");
    }

    [Test]
    public async Task Rgb_VeryFast_StillYellow()
    {
        // Beyond 8 knots, still clamped at yellow.
        var ms = 20.0 / Format.MsToKnots;
        await Assert.That(SpeedColor.Rgb(ms)).IsEqualTo("rgb(234,179,8)");
    }

    [Test]
    public async Task Rgb_DistinctBetweenAdjacentSpeeds()
    {
        // Sanity: 0 vs 5 m/s and 2 vs 8 m/s map to different colours.
        await Assert.That(SpeedColor.Rgb(0)).IsNotEqualTo(SpeedColor.Rgb(5));
        await Assert.That(SpeedColor.Rgb(2)).IsNotEqualTo(SpeedColor.Rgb(8));
    }

    // -- Bucket --------------------------------------------------

    [Test]
    public async Task Bucket_Null_ZeroBucket()
    {
        await Assert.That(SpeedColor.Bucket(null)).IsEqualTo(0);
    }

    [Test]
    public async Task Bucket_BelowFirstThreshold_ZeroBucket()
    {
        // Buckets[0] = 0 so 0 m/s lands at bucket 0; -1 below all
        // thresholds also lands at 0 (defensive, shouldn't occur).
        await Assert.That(SpeedColor.Bucket(0)).IsEqualTo(0);
        await Assert.That(SpeedColor.Bucket(-1)).IsEqualTo(0);
    }

    [Test]
    public async Task Bucket_HitsHighestThresholdAtOrBelow()
    {
        // Buckets [0, 1, 2, 3, 5, 8] -- a sog of 4.5 picks index 3 (3<=4.5<5).
        await Assert.That(SpeedColor.Bucket(0.5)).IsEqualTo(0);
        await Assert.That(SpeedColor.Bucket(1.0)).IsEqualTo(1);
        await Assert.That(SpeedColor.Bucket(2.5)).IsEqualTo(2);
        await Assert.That(SpeedColor.Bucket(4.5)).IsEqualTo(3);
        await Assert.That(SpeedColor.Bucket(5.0)).IsEqualTo(4);
        await Assert.That(SpeedColor.Bucket(10.0)).IsEqualTo(5);
    }
}
