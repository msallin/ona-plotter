using Microsoft.Extensions.Time.Testing;
using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

public class RollingScalarSeriesTests
{
    private static FakeTimeProvider NewClock() =>
        new(new DateTimeOffset(2026, 5, 5, 12, 0, 0, TimeSpan.Zero));

    [Test]
    public async Task Mean_Empty_Null()
    {
        var s = new RollingScalarSeries(TimeSpan.FromMinutes(1), NewClock());
        await Assert.That(s.Mean(TimeSpan.FromSeconds(30), warmupRatio: 0)).IsNull();
    }

    [Test]
    public async Task Mean_OneSample_NoWarmup_ReturnsValue()
    {
        var s = new RollingScalarSeries(TimeSpan.FromMinutes(1), NewClock());
        s.Add(5);
        await Assert.That(s.Mean(TimeSpan.FromSeconds(30), warmupRatio: 0)).IsEqualTo(5.0);
    }

    [Test]
    public async Task Mean_MultipleSamples_AveragesAll()
    {
        var s = new RollingScalarSeries(TimeSpan.FromMinutes(1), NewClock());
        s.Add(2);
        s.Add(4);
        s.Add(6);
        await Assert.That(s.Mean(TimeSpan.FromSeconds(30), warmupRatio: 0)).IsEqualTo(4.0);
    }

    [Test]
    public async Task Mean_NonFinite_Dropped()
    {
        var s = new RollingScalarSeries(TimeSpan.FromMinutes(1), NewClock());
        s.Add(2);
        s.Add(double.NaN);
        s.Add(double.PositiveInfinity);
        s.Add(4);
        await Assert.That(s.Mean(TimeSpan.FromSeconds(30), warmupRatio: 0)).IsEqualTo(3.0);
    }

    [Test]
    public async Task Mean_WindowExceedsRetention_Null()
    {
        var s = new RollingScalarSeries(TimeSpan.FromMinutes(1), NewClock());
        s.Add(5);
        await Assert.That(s.Mean(TimeSpan.FromMinutes(2), warmupRatio: 0)).IsNull();
    }

    [Test]
    public async Task Mean_DifferentWindows_FromSameBuffer()
    {
        var clock = NewClock();
        var s = new RollingScalarSeries(TimeSpan.FromMinutes(10), clock);
        // Sample at t=0: 10
        s.Add(10);
        clock.Advance(TimeSpan.FromMinutes(5));
        // Sample at t=5min: 20
        s.Add(20);
        // 1-min window from t=5 -> only the t=5 sample -> mean 20.
        await Assert.That(s.Mean(TimeSpan.FromMinutes(1), warmupRatio: 0)).IsEqualTo(20.0);
        // 10-min window -> both samples -> mean 15.
        await Assert.That(s.Mean(TimeSpan.FromMinutes(10), warmupRatio: 0)).IsEqualTo(15.0);
    }

    [Test]
    public async Task Eviction_OldSamplesPastRetention_Dropped()
    {
        var clock = NewClock();
        var s = new RollingScalarSeries(TimeSpan.FromMinutes(1), clock);
        s.Add(99);
        clock.Advance(TimeSpan.FromMinutes(2));
        await Assert.That(s.Count).IsEqualTo(0);
        await Assert.That(s.Mean(TimeSpan.FromSeconds(30), warmupRatio: 0)).IsNull();
    }

    [Test]
    public async Task Warmup_BeforeHalfWindow_Null()
    {
        var clock = NewClock();
        var s = new RollingScalarSeries(TimeSpan.FromMinutes(2), clock);
        s.Add(5);
        clock.Advance(TimeSpan.FromSeconds(20));
        // 20s coverage on a 60s query window -> below 50% -> null.
        await Assert.That(s.Mean(TimeSpan.FromSeconds(60))).IsNull();
    }

    [Test]
    public async Task Warmup_AfterHalfWindow_ReturnsMean()
    {
        var clock = NewClock();
        var s = new RollingScalarSeries(TimeSpan.FromMinutes(2), clock);
        s.Add(10);
        clock.Advance(TimeSpan.FromSeconds(31));
        s.Add(20);
        // 31s coverage on a 60s window -> past 50% -> mean published.
        await Assert.That(s.Mean(TimeSpan.FromSeconds(60))).IsEqualTo(15.0);
    }

    [Test]
    public async Task Stats_FewerThanTwoSamples_Null()
    {
        var s = new RollingScalarSeries(TimeSpan.FromMinutes(1), NewClock());
        // sigma is undefined for n=1 so we surface null.
        await Assert.That(s.Stats(TimeSpan.FromSeconds(30), warmupRatio: 0)).IsNull();
        s.Add(5);
        await Assert.That(s.Stats(TimeSpan.FromSeconds(30), warmupRatio: 0)).IsNull();
    }

    [Test]
    public async Task Stats_GustLullSigma_Computed()
    {
        var s = new RollingScalarSeries(TimeSpan.FromMinutes(1), NewClock());
        s.Add(8);
        s.Add(10);
        s.Add(12);
        var st = s.Stats(TimeSpan.FromSeconds(30), warmupRatio: 0);
        await Assert.That(st).IsNotNull();
        await Assert.That(st!.Value.Mean).IsEqualTo(10.0);
        await Assert.That(st.Value.Min).IsEqualTo(8.0);
        await Assert.That(st.Value.Max).IsEqualTo(12.0);
        await Assert.That(st.Value.Gust).IsEqualTo(2.0);    // 12 - 10
        await Assert.That(st.Value.Lull).IsEqualTo(2.0);    // 10 - 8
        // Population sigma over (8,10,12): sqrt((4+0+4)/3) = sqrt(8/3) ~= 1.633
        var sigma = st.Value.Sigma;
        await Assert.That(Math.Abs(sigma - 1.6329931618554518)).IsLessThan(1e-9);
    }

    [Test]
    public async Task Constructor_NonPositiveRetention_Throws()
    {
        await Assert.That(() => new RollingScalarSeries(TimeSpan.Zero))
            .Throws<ArgumentOutOfRangeException>();
    }

    // - SnapshotIn -----------------------------------------------

    [Test]
    public async Task SnapshotIn_Empty_ReturnsEmpty()
    {
        var s = new RollingScalarSeries(TimeSpan.FromMinutes(1), NewClock());
        var snap = s.SnapshotIn(TimeSpan.FromSeconds(30));
        await Assert.That(snap.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SnapshotIn_ReturnsSamplesInWindowOldestFirst()
    {
        var clock = NewClock();
        var s = new RollingScalarSeries(TimeSpan.FromMinutes(1), clock);
        s.Add(1);
        clock.Advance(TimeSpan.FromSeconds(10));
        s.Add(2);
        clock.Advance(TimeSpan.FromSeconds(10));
        s.Add(3);

        var snap = s.SnapshotIn(TimeSpan.FromSeconds(30));
        await Assert.That(snap.Count).IsEqualTo(3);
        await Assert.That(snap[0].Value).IsEqualTo(1.0);
        await Assert.That(snap[1].Value).IsEqualTo(2.0);
        await Assert.That(snap[2].Value).IsEqualTo(3.0);
        // Oldest-first ordering is the eviction order; useful for
        // chart polylines that walk left-to-right.
        await Assert.That(snap[0].Time < snap[1].Time).IsTrue();
        await Assert.That(snap[1].Time < snap[2].Time).IsTrue();
    }

    [Test]
    public async Task SnapshotIn_DropsSamplesOutsideWindow()
    {
        var clock = NewClock();
        var s = new RollingScalarSeries(TimeSpan.FromMinutes(5), clock);
        s.Add(1);   // t=0
        clock.Advance(TimeSpan.FromSeconds(60));
        s.Add(2);   // t=60s -> outside a 30s trailing window
        clock.Advance(TimeSpan.FromSeconds(10));
        s.Add(3);   // t=70s -> inside a 30s trailing window

        var snap = s.SnapshotIn(TimeSpan.FromSeconds(30));
        await Assert.That(snap.Count).IsEqualTo(2);
        await Assert.That(snap[0].Value).IsEqualTo(2.0);
        await Assert.That(snap[1].Value).IsEqualTo(3.0);
    }

    [Test]
    public async Task SnapshotIn_WindowExceedsRetention_ReturnsEmpty()
    {
        var s = new RollingScalarSeries(TimeSpan.FromMinutes(1), NewClock());
        s.Add(5);
        var snap = s.SnapshotIn(TimeSpan.FromMinutes(2));
        await Assert.That(snap.Count).IsEqualTo(0);
    }

    // - Seed -----------------------------------------------------

    [Test]
    public async Task Seed_PopulatesBuffer_FromPastTimestamps()
    {
        // History seed lands samples at their server timestamps; the
        // mean over the seeded window reads them back. Pinned because
        // the wind-page seed depends on past-stamp insertion working.
        var clock = NewClock();
        var now = clock.GetUtcNow().UtcDateTime;
        var s = new RollingScalarSeries(TimeSpan.FromMinutes(60), clock);
        s.Seed(new[]
        {
            (now - TimeSpan.FromMinutes(30), 6.0),
            (now - TimeSpan.FromMinutes(20), 8.0),
            (now - TimeSpan.FromMinutes(10), 10.0),
        });

        await Assert.That(s.Count).IsEqualTo(3);
        // Warmup ratio 0 because the seed itself answers "we have data
        // older than the window"; coverage is wide.
        await Assert.That(s.Mean(TimeSpan.FromMinutes(60), warmupRatio: 0)).IsEqualTo(8.0);
    }

    [Test]
    public async Task Seed_DropsFutureSamples()
    {
        // A clock-skewed history server should never push the buffer
        // past "now" - the newest-to-oldest scan would early-break in
        // the wrong place. Future samples drop silently.
        var clock = NewClock();
        var now = clock.GetUtcNow().UtcDateTime;
        var s = new RollingScalarSeries(TimeSpan.FromMinutes(60), clock);
        s.Seed(new[]
        {
            (now - TimeSpan.FromMinutes(5), 5.0),
            (now + TimeSpan.FromMinutes(1), 999.0),
        });

        await Assert.That(s.Count).IsEqualTo(1);
        await Assert.That(s.Mean(TimeSpan.FromMinutes(60), warmupRatio: 0)).IsEqualTo(5.0);
    }

    [Test]
    public async Task Seed_DropsOutOfOrderAndDuplicateTimestamps()
    {
        // Mean / Stats walk newest -> oldest and break on the first
        // sample older than the cutoff. If the buffer wasn't monotonic
        // an out-of-order sample beyond the break point would be
        // silently excluded from the mean - the worst kind of bug.
        var clock = NewClock();
        var now = clock.GetUtcNow().UtcDateTime;
        var s = new RollingScalarSeries(TimeSpan.FromMinutes(60), clock);
        s.Seed(new[]
        {
            (now - TimeSpan.FromMinutes(10), 10.0),
            (now - TimeSpan.FromMinutes(20), 20.0),  // earlier than the previous -> dropped
            (now - TimeSpan.FromMinutes(10), 99.0),  // duplicate ts -> dropped
            (now - TimeSpan.FromMinutes(5),  30.0),
        });

        await Assert.That(s.Count).IsEqualTo(2);
        await Assert.That(s.Mean(TimeSpan.FromMinutes(60), warmupRatio: 0)).IsEqualTo(20.0);
    }

    [Test]
    public async Task Seed_DropsSamplesPastRetention()
    {
        var clock = NewClock();
        var now = clock.GetUtcNow().UtcDateTime;
        var s = new RollingScalarSeries(TimeSpan.FromMinutes(10), clock);
        s.Seed(new[]
        {
            (now - TimeSpan.FromMinutes(15), 99.0),   // older than retention -> dropped
            (now - TimeSpan.FromMinutes(5),   5.0),
        });

        await Assert.That(s.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Seed_DropsNonFinite()
    {
        var clock = NewClock();
        var now = clock.GetUtcNow().UtcDateTime;
        var s = new RollingScalarSeries(TimeSpan.FromMinutes(10), clock);
        s.Seed(new[]
        {
            (now - TimeSpan.FromMinutes(3), 5.0),
            (now - TimeSpan.FromMinutes(2), double.NaN),
            (now - TimeSpan.FromMinutes(1), double.PositiveInfinity),
        });

        await Assert.That(s.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Seed_ThenAdd_LiveSamplePreservesOrdering()
    {
        // The page seeds at mount, then live deltas append via Add.
        // Mean over a window straddling both regions must include all
        // of them - this is the seam where seed and live data meet.
        var clock = NewClock();
        var now = clock.GetUtcNow().UtcDateTime;
        var s = new RollingScalarSeries(TimeSpan.FromMinutes(60), clock);
        s.Seed(new[]
        {
            (now - TimeSpan.FromMinutes(10), 4.0),
            (now - TimeSpan.FromSeconds(30), 6.0),
        });
        // Live sample arrives a moment later.
        clock.Advance(TimeSpan.FromSeconds(1));
        s.Add(8.0);

        await Assert.That(s.Count).IsEqualTo(3);
        await Assert.That(s.Mean(TimeSpan.FromMinutes(60), warmupRatio: 0)).IsEqualTo(6.0);
    }

    [Test]
    public async Task Seed_TwiceIsIdempotent()
    {
        // A second seed call with the same data must not double-count.
        // Each sample's timestamp <= existing newest fails the order
        // check, so re-seeding is a no-op.
        var clock = NewClock();
        var now = clock.GetUtcNow().UtcDateTime;
        var s = new RollingScalarSeries(TimeSpan.FromMinutes(60), clock);
        var batch = new[]
        {
            (now - TimeSpan.FromMinutes(10), 4.0),
            (now - TimeSpan.FromMinutes(5),  6.0),
        };
        s.Seed(batch);
        s.Seed(batch);

        await Assert.That(s.Count).IsEqualTo(2);
        await Assert.That(s.Mean(TimeSpan.FromMinutes(60), warmupRatio: 0)).IsEqualTo(5.0);
    }
}
