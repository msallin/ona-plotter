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

    // - ShiftRateDegPerMin ---------------------------------------

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

    // - SnapshotIn -----------------------------------------------

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

    // - Seed -----------------------------------------------------

    [Test]
    public async Task Seed_PopulatesBuffer_FromPastTimestamps()
    {
        // History seed: drop past samples in at their server timestamps.
        // Circular mean over the seeded window must read them back.
        var clock = NewClock();
        var now = clock.GetUtcNow().UtcDateTime;
        var s = new RollingDirectionSeries(TimeSpan.FromMinutes(60), clock);
        s.Seed(new[]
        {
            (now - TimeSpan.FromMinutes(20), 0.0),         // north
            (now - TimeSpan.FromMinutes(10), Pi / 2),       // east
        });

        await Assert.That(s.Count).IsEqualTo(2);
        // Mean of {0, π/2} on the unit circle is π/4 (NE).
        await AssertAngleNearAsync(s.Mean(TimeSpan.FromMinutes(60), warmupRatio: 0), Pi / 4);
    }

    [Test]
    public async Task Seed_DropsFutureSamples()
    {
        var clock = NewClock();
        var now = clock.GetUtcNow().UtcDateTime;
        var s = new RollingDirectionSeries(TimeSpan.FromMinutes(60), clock);
        s.Seed(new[]
        {
            (now - TimeSpan.FromMinutes(5), Pi / 2),
            (now + TimeSpan.FromMinutes(1), 0.0),
        });

        await Assert.That(s.Count).IsEqualTo(1);
        await AssertAngleNearAsync(s.Mean(TimeSpan.FromMinutes(60), warmupRatio: 0), Pi / 2);
    }

    [Test]
    public async Task Seed_DropsOutOfOrderTimestamps()
    {
        // Same monotonicity contract as the scalar series: out-of-order
        // samples drop silently so the early-break query stays correct.
        var clock = NewClock();
        var now = clock.GetUtcNow().UtcDateTime;
        var s = new RollingDirectionSeries(TimeSpan.FromMinutes(60), clock);
        s.Seed(new[]
        {
            (now - TimeSpan.FromMinutes(10), 0.0),
            (now - TimeSpan.FromMinutes(20), Pi),  // earlier -> dropped
            (now - TimeSpan.FromMinutes(5),  Pi / 2),
        });

        await Assert.That(s.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Seed_EnablesShiftRate_OnHistoricalData()
    {
        // The /wind page's shift-rate readout needs 5+ samples in the
        // window. Without seed it stays "Awaiting samples" for the first
        // few minutes; with seed it's live from page mount. Pin a
        // veering pattern and check the slope sign + magnitude.
        var clock = NewClock();
        var now = clock.GetUtcNow().UtcDateTime;
        var s = new RollingDirectionSeries(TimeSpan.FromMinutes(60), clock);
        // 5 samples spanning the last 5 minutes, veering 0 -> 20 deg.
        var seed = new (DateTime Ts, double AngleRad)[]
        {
            (now - TimeSpan.FromMinutes(5),   0    * Pi / 180),
            (now - TimeSpan.FromMinutes(4),   5    * Pi / 180),
            (now - TimeSpan.FromMinutes(3),  10    * Pi / 180),
            (now - TimeSpan.FromMinutes(2),  15    * Pi / 180),
            (now - TimeSpan.FromMinutes(1),  20    * Pi / 180),
        };
        s.Seed(seed);

        var rate = s.ShiftRateDegPerMin(TimeSpan.FromMinutes(5), warmupRatio: 0);
        await Assert.That(rate).IsNotNull();
        // 20° over 4 minutes = +5°/min (positive = veering).
        await Assert.That(Math.Abs(rate!.Value - 5.0)).IsLessThan(0.5);
    }
}
