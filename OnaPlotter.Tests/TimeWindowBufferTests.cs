using Microsoft.Extensions.Time.Testing;
using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

/// <summary>The shared store behind <see cref="RollingScalarSeries"/> and
/// <see cref="RollingDirectionSeries"/>: eviction, compaction, seeding and
/// the warmup gate, exercised through both public series so their public
/// behaviour stays pinned.</summary>
public class TimeWindowBufferTests
{
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);

    private static (FakeTimeProvider clock, RollingScalarSeries series) NewScalar()
    {
        var clock = new FakeTimeProvider(TestClock.FixedUtcNow);
        return (clock, new RollingScalarSeries(Retention, clock));
    }

    [Test]
    public async Task LongRun_KeepsOnlyTheRetentionWindow_InOrder()
    {
        var (clock, series) = NewScalar();

        // 3x the retention, one sample every 10 s: eviction and
        // compaction run many times.
        for (int i = 0; i < 180; i++)
        {
            series.Add(i);
            clock.Advance(TimeSpan.FromSeconds(10));
        }

        var all = series.SnapshotIn(Retention);
        await Assert.That(all.Count).IsEqualTo(60);
        await Assert.That(all[0].Value).IsEqualTo(120.0);
        await Assert.That(all[^1].Value).IsEqualTo(179.0);
        await Assert.That(all.Zip(all.Skip(1), (a, b) => a.Time < b.Time).All(x => x)).IsTrue();
        await Assert.That(series.Count).IsEqualTo(60);
    }

    [Test]
    public async Task Count_EvictsExpiredSamplesWithoutAnyAdd()
    {
        var (clock, series) = NewScalar();
        series.Add(1);
        series.Add(2);

        clock.Advance(Retention + TimeSpan.FromSeconds(1));

        await Assert.That(series.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Version_BumpsOnEvictionDuringQuery()
    {
        var (clock, series) = NewScalar();
        series.Add(1);
        long before = series.Version;

        clock.Advance(Retention + TimeSpan.FromSeconds(1));
        _ = series.Mean(TimeSpan.FromMinutes(1));

        await Assert.That(series.Version).IsGreaterThan(before);
    }

    [Test]
    public async Task Seed_AfterEvictionAdvancedTheHead_InsertsBeforeTheOldestLiveSample()
    {
        var (clock, series) = NewScalar();
        // One live sample a minute for half an hour: the head has moved on
        // many times. After 8.5 more quiet minutes only the newest sample
        // is still within retention, 30 s after the window start.
        for (int i = 0; i < 30; i++)
        {
            series.Add(1000 + i);
            clock.Advance(TimeSpan.FromMinutes(1));
        }
        clock.Advance(TimeSpan.FromSeconds(8.5 * 60));
        var live = series.SnapshotIn(Retention);
        await Assert.That(live.Count).IsEqualTo(1);
        var oldestLive = live[0].Time;

        series.Seed([
            (oldestLive.AddSeconds(-15), 3.0),
            (oldestLive.AddSeconds(-10), 2.0),
            (oldestLive.AddSeconds(-5), 1.0),
        ]);

        var all = series.SnapshotIn(Retention);
        await Assert.That(all.Select(s => s.Value).ToArray()).IsEquivalentTo(new[] { 3.0, 2.0, 1.0, 1029.0 });
        await Assert.That(all.Zip(all.Skip(1), (a, b) => a.Time < b.Time).All(x => x)).IsTrue();
    }

    [Test]
    public async Task Seed_OutsideRetentionOrFuture_IsIgnored_AndDoesNotBumpVersion()
    {
        var (clock, series) = NewScalar();
        series.Add(5);
        long before = series.Version;
        var now = clock.GetUtcNow().UtcDateTime;

        series.Seed([(now - Retention - TimeSpan.FromMinutes(1), 1.0), (now + TimeSpan.FromMinutes(1), 2.0)]);

        await Assert.That(series.Version).IsEqualTo(before);
        await Assert.That(series.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Seed_Null_Throws()
    {
        var (_, series) = NewScalar();

        await Assert.That(() => series.Seed(null!)).Throws<ArgumentNullException>();
        await Assert.That(() => new RollingDirectionSeries(Retention).Seed(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    [Arguments(-0.1)]
    [Arguments(1.1)]
    public async Task Query_WithWarmupRatioOutsideZeroToOne_ReturnsNull(double ratio)
    {
        var (clock, series) = NewScalar();
        series.Add(1);
        clock.Advance(TimeSpan.FromMinutes(5));
        series.Add(2);

        await Assert.That(series.Mean(TimeSpan.FromMinutes(1), ratio)).IsNull();
        await Assert.That(series.Stats(TimeSpan.FromMinutes(1), ratio)).IsNull();
    }

    [Test]
    public async Task Warmup_ZeroRatio_PublishesImmediately_FullRatioWaitsForTheWholeWindow()
    {
        var (clock, series) = NewScalar();
        series.Add(4);
        clock.Advance(TimeSpan.FromSeconds(30));
        series.Add(6);

        await Assert.That(series.Mean(TimeSpan.FromMinutes(1), 0.0)).IsEqualTo(5.0);
        await Assert.That(series.Mean(TimeSpan.FromMinutes(1), 1.0)).IsNull();

        clock.Advance(TimeSpan.FromSeconds(30));
        await Assert.That(series.Mean(TimeSpan.FromMinutes(1), 1.0)).IsEqualTo(5.0);
    }

    [Test]
    public async Task Window_EdgeSampleExactlyAtTheCutoff_IsIncluded()
    {
        var (clock, series) = NewScalar();
        series.Add(1);
        clock.Advance(TimeSpan.FromMinutes(1));
        series.Add(3);

        // Window = exactly the age of the first sample.
        await Assert.That(series.SnapshotIn(TimeSpan.FromMinutes(1)).Count).IsEqualTo(2);
        await Assert.That(series.Mean(TimeSpan.FromMinutes(1), 0.0)).IsEqualTo(2.0);
    }

    [Test]
    public async Task Direction_LongRun_KeepsTheCircularMeanOfTheRetainedWindow()
    {
        var clock = new FakeTimeProvider(TestClock.FixedUtcNow);
        var series = new RollingDirectionSeries(Retention, clock);

        // 350 deg for the first 15 minutes, 10 deg afterwards: only the
        // last ten minutes are retained.
        for (int i = 0; i < 180; i++)
        {
            series.Add((i < 90 ? 350.0 : 10.0) * Math.PI / 180.0);
            clock.Advance(TimeSpan.FromSeconds(10));
        }

        double mean = series.Mean(TimeSpan.FromMinutes(5))!.Value * 180.0 / Math.PI;
        await Assert.That(mean).IsEqualTo(10.0).Within(1e-6);
        await Assert.That(series.Count).IsEqualTo(60);
    }
}
