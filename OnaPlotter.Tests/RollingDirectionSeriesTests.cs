using Microsoft.Extensions.Time.Testing;
using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

public class RollingDirectionSeriesTests
{
    private static readonly double Pi = Math.PI;

    private static FakeTimeProvider NewClock() =>
        new(new DateTimeOffset(2026, 5, 5, 12, 0, 0, TimeSpan.Zero));

    /// <summary>Assert two angles in radians are within
    /// <paramref name="tolDeg"/> on the unit circle (handles wrap).</summary>
    private static async Task AssertAngleNearAsync(double? actualRad, double expectedRad, double tolDeg = 0.5)
    {
        await Assert.That(actualRad).IsNotNull();
        var diff = Math.Atan2(Math.Sin(actualRad!.Value - expectedRad), Math.Cos(actualRad.Value - expectedRad));
        await Assert.That(Math.Abs(diff) * 180.0 / Math.PI).IsLessThan(tolDeg);
    }

    [Test]
    public async Task Mean_Empty_Null()
    {
        var s = new RollingDirectionSeries(TimeSpan.FromMinutes(1), NewClock());
        await Assert.That(s.Mean(TimeSpan.FromSeconds(30), warmupRatio: 0)).IsNull();
    }

    [Test]
    public async Task Mean_StraddlingZero_NearZero()
    {
        var s = new RollingDirectionSeries(TimeSpan.FromMinutes(1), NewClock());
        s.Add(350 * Pi / 180);
        s.Add(10 * Pi / 180);
        await AssertAngleNearAsync(s.Mean(TimeSpan.FromSeconds(30), warmupRatio: 0), 0);
    }

    [Test]
    public async Task Mean_AcrossSouth_AtSouth()
    {
        var s = new RollingDirectionSeries(TimeSpan.FromMinutes(1), NewClock());
        s.Add(175 * Pi / 180);
        s.Add(185 * Pi / 180);
        await AssertAngleNearAsync(s.Mean(TimeSpan.FromSeconds(30), warmupRatio: 0), Pi);
    }

    [Test]
    public async Task Mean_ZeroWeight_NotIncluded()
    {
        var s = new RollingDirectionSeries(TimeSpan.FromMinutes(1), NewClock());
        s.Add(Pi / 2, weight: 1);    // 90 deg, included
        s.Add(0, weight: 0);          // ignored (zero-weight)
        await AssertAngleNearAsync(s.Mean(TimeSpan.FromSeconds(30), warmupRatio: 0), Pi / 2);
    }

    [Test]
    public async Task Mean_NegativeWeight_Dropped()
    {
        var s = new RollingDirectionSeries(TimeSpan.FromMinutes(1), NewClock());
        s.Add(Pi / 2);
        s.Add(0, weight: -1);
        await Assert.That(s.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Mean_NonFiniteAngle_Dropped()
    {
        var s = new RollingDirectionSeries(TimeSpan.FromMinutes(1), NewClock());
        s.Add(double.NaN);
        s.Add(double.PositiveInfinity);
        await Assert.That(s.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Mean_DifferentWindows_FromSameBuffer()
    {
        var clock = NewClock();
        var s = new RollingDirectionSeries(TimeSpan.FromMinutes(10), clock);
        s.Add(0);                                  // t=0, north
        clock.Advance(TimeSpan.FromMinutes(5));
        s.Add(Pi / 2);                              // t=5min, east
        // 1-min window includes only the east sample.
        await AssertAngleNearAsync(s.Mean(TimeSpan.FromMinutes(1), warmupRatio: 0), Pi / 2);
        // 10-min window includes both -> mean at NE (~45°).
        await AssertAngleNearAsync(s.Mean(TimeSpan.FromMinutes(10), warmupRatio: 0), Pi / 4);
    }

    [Test]
    public async Task Eviction_OldSamplesPastRetention_Dropped()
    {
        var clock = NewClock();
        var s = new RollingDirectionSeries(TimeSpan.FromMinutes(1), clock);
        s.Add(Pi / 2);
        clock.Advance(TimeSpan.FromMinutes(2));
        await Assert.That(s.Count).IsEqualTo(0);
    }

    // -- ShiftRateDegPerMin ---------------------------------------

    [Test]
    public async Task ShiftRate_FewerThanFiveSamples_Null()
    {
        var s = new RollingDirectionSeries(TimeSpan.FromMinutes(10), NewClock());
        for (int i = 0; i < 4; i++) s.Add(Pi / 4);
        await Assert.That(s.ShiftRateDegPerMin(TimeSpan.FromMinutes(5), warmupRatio: 0)).IsNull();
    }

    [Test]
    public async Task ShiftRate_SteadyVeer_PositiveSlope()
    {
        // TWD veering 1 deg per minute over 5 minutes.
        var clock = NewClock();
        var s = new RollingDirectionSeries(TimeSpan.FromMinutes(10), clock);
        for (int i = 0; i <= 5; i++)
        {
            s.Add(i * Pi / 180);   // 0°, 1°, 2°, 3°, 4°, 5°
            clock.Advance(TimeSpan.FromMinutes(1));
        }
        var rate = s.ShiftRateDegPerMin(TimeSpan.FromMinutes(10), warmupRatio: 0);
        await Assert.That(rate).IsNotNull();
        await Assert.That(Math.Abs(rate!.Value - 1.0)).IsLessThan(0.1);
    }

    [Test]
    public async Task ShiftRate_SteadyBack_NegativeSlope()
    {
        var clock = NewClock();
        var s = new RollingDirectionSeries(TimeSpan.FromMinutes(10), clock);
        for (int i = 0; i <= 5; i++)
        {
            s.Add(-i * Pi / 180);   // 0°, -1° (=359°), ... but the unwrap should produce a steady -1 slope
            clock.Advance(TimeSpan.FromMinutes(1));
        }
        var rate = s.ShiftRateDegPerMin(TimeSpan.FromMinutes(10), warmupRatio: 0);
        await Assert.That(rate).IsNotNull();
        await Assert.That(Math.Abs(rate!.Value + 1.0)).IsLessThan(0.1);
    }

    [Test]
    public async Task ShiftRate_AcrossZero_UnwrapHandlesIt()
    {
        // Wind veering through north: 358°, 359°, 0°, 1°, 2° -> slope +1°/min
        var clock = NewClock();
        var s = new RollingDirectionSeries(TimeSpan.FromMinutes(10), clock);
        double[] degSeq = [358, 359, 0, 1, 2];
        foreach (var deg in degSeq)
        {
            s.Add(deg * Pi / 180);
            clock.Advance(TimeSpan.FromMinutes(1));
        }
        var rate = s.ShiftRateDegPerMin(TimeSpan.FromMinutes(10), warmupRatio: 0);
        await Assert.That(rate).IsNotNull();
        await Assert.That(Math.Abs(rate!.Value - 1.0)).IsLessThan(0.1);
    }

    [Test]
    public async Task Constructor_NonPositiveRetention_Throws()
    {
        await Assert.That(() => new RollingDirectionSeries(TimeSpan.Zero))
            .Throws<ArgumentOutOfRangeException>();
    }

    // -- SnapshotIn -----------------------------------------------

    [Test]
    public async Task SnapshotIn_Empty_ReturnsEmpty()
    {
        var s = new RollingDirectionSeries(TimeSpan.FromMinutes(1), NewClock());
        await Assert.That(s.SnapshotIn(TimeSpan.FromSeconds(30)).Count).IsEqualTo(0);
    }

    [Test]
    public async Task SnapshotIn_DefaultsToSkippingZeroWeight()
    {
        var s = new RollingDirectionSeries(TimeSpan.FromMinutes(1), NewClock());
        s.Add(Pi / 2, weight: 1);
        s.Add(0, weight: 0);   // skipped (stationary frame)
        s.Add(Pi / 4, weight: 1);

        var snap = s.SnapshotIn(TimeSpan.FromSeconds(30));
        await Assert.That(snap.Count).IsEqualTo(2);
        await Assert.That(Math.Abs(snap[0].Value - Pi / 2)).IsLessThan(1e-9);
        await Assert.That(Math.Abs(snap[1].Value - Pi / 4)).IsLessThan(1e-9);
    }

    [Test]
    public async Task SnapshotIn_IncludeZeroWeight_OptIn()
    {
        var s = new RollingDirectionSeries(TimeSpan.FromMinutes(1), NewClock());
        s.Add(Pi / 2, weight: 1);
        s.Add(0, weight: 0);
        s.Add(Pi / 4, weight: 1);

        var snap = s.SnapshotIn(TimeSpan.FromSeconds(30), includeZeroWeight: true);
        await Assert.That(snap.Count).IsEqualTo(3);
    }

    [Test]
    public async Task SnapshotIn_DropsSamplesOutsideWindow()
    {
        var clock = NewClock();
        var s = new RollingDirectionSeries(TimeSpan.FromMinutes(5), clock);
        s.Add(0);                                  // t=0
        clock.Advance(TimeSpan.FromSeconds(60));
        s.Add(Pi / 2);                              // t=60s
        clock.Advance(TimeSpan.FromSeconds(10));
        s.Add(Pi);                                  // t=70s

        var snap = s.SnapshotIn(TimeSpan.FromSeconds(30));
        await Assert.That(snap.Count).IsEqualTo(2);
        await Assert.That(Math.Abs(snap[0].Value - Pi / 2)).IsLessThan(1e-9);
        await Assert.That(Math.Abs(snap[1].Value - Pi)).IsLessThan(1e-9);
    }
}
