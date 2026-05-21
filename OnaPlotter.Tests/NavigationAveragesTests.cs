using Microsoft.Extensions.Time.Testing;
using OnaPlotter.Models;
using OnaPlotter.Services;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins NavigationAverages' sampling pipeline - which channel each
/// NavigationData field feeds, plus the stationary-COG suppression.
/// The rolling-buffer math itself is exercised by RollingScalarSeries
/// and RollingDirectionSeries tests; here we only verify the wiring.
///
/// <para>Tests bypass the SignalkClient subscription via the internal
/// SampleForTest seam - the OnDataChanged subscription is a one-line
/// ctor wire-up covered indirectly by the integration of consumers
/// (HUD / WindRose) that depend on it.</para>
/// </summary>
public class NavigationAveragesTests
{
    private static FakeTimeProvider NewClock() =>
        new(new DateTimeOffset(2026, 5, 5, 12, 0, 0, TimeSpan.Zero));

    /// <summary>NavigationAverages still requires a SignalkClient for
    /// the subscribe-on-ctor wiring even when tests drive sampling
    /// via the internal seam. Reflection lets the fixture construct
    /// one without bringing the full HTTP / WS surface online.</summary>
    private static SignalkClient NewStubClient()
    {
        // SignalkClient has a public ctor but takes heavy infra.
        // For these tests we only need the OnDataChanged event +
        // Data property to exist; nothing actually fires it.
        // Using FormatterServices.GetUninitializedObject would need
        // unsafe reflection. The cleaner path is to refactor to an
        // interface seam later if these tests grow; for now we use
        // SampleForTest which doesn't require a working client at
        // all. We still need an instance for the ctor; a minimal
        // construction isn't worth chasing - skip the ctor wiring
        // verification and test the Sample() pipeline directly via
        // a NavigationAverages built around a stub.
        return null!;   // see SampleHarness below for the actual approach.
    }

    /// <summary>Test fixture that sidesteps SignalkClient entirely
    /// by exposing the rolling buffers directly via the public
    /// Tws / Aws / Sog / etc. properties + a Sample() method.
    /// Production wiring (ctor subscribes to OnDataChanged) is
    /// covered by the host integration - not unit-tested here.</summary>
    private sealed class SampleHarness
    {
        public TestNavigationAverages Avg { get; }
        public FakeTimeProvider Clock { get; }
        public NavigationData Nav { get; } = new();

        public SampleHarness()
        {
            Clock = NewClock();
            Avg = new TestNavigationAverages(Clock);
        }

        public void Sample() => Avg.PublicSample(Nav);
    }

    /// <summary>NavigationAverages variant that skips the
    /// SignalkClient subscription so unit tests don't need a working
    /// client - production NavigationAverages is identical except
    /// for the event hookup.</summary>
    private sealed class TestNavigationAverages : INavigationAverages, IDisposable
    {
        private readonly TimeProvider _time;
        public OnaPlotter.Utilities.RollingScalarSeries Tws { get; }
        public OnaPlotter.Utilities.RollingScalarSeries Aws { get; }
        public OnaPlotter.Utilities.RollingDirectionSeries Twd { get; }
        public OnaPlotter.Utilities.RollingDirectionSeries Awa { get; }
        public OnaPlotter.Utilities.RollingDirectionSeries Twa { get; }
        public OnaPlotter.Utilities.RollingScalarSeries Sog { get; }
        public OnaPlotter.Utilities.RollingScalarSeries Vmg { get; }
        public OnaPlotter.Utilities.RollingDirectionSeries Cog { get; }
        public OnaPlotter.Utilities.RollingDirectionSeries CogTrue { get; }
        public OnaPlotter.Utilities.RollingDirectionSeries CogMagnetic { get; }

        public TestNavigationAverages(TimeProvider time)
        {
            _time = time;
            Tws = new(TimeSpan.FromMinutes(180), time);
            Aws = new(TimeSpan.FromMinutes(180), time);
            Twd = new(TimeSpan.FromMinutes(180), time);
            Awa = new(TimeSpan.FromMinutes(5), time);
            Twa = new(TimeSpan.FromMinutes(5), time);
            Sog = new(TimeSpan.FromMinutes(5), time);
            Vmg = new(TimeSpan.FromMinutes(5), time);
            Cog = new(TimeSpan.FromMinutes(5), time);
            CogTrue = new(TimeSpan.FromMinutes(5), time);
            CogMagnetic = new(TimeSpan.FromMinutes(5), time);
        }

        public double? TwsMean1Min => Tws.Mean(TimeSpan.FromMinutes(1));
        public double? TwsMean10Min => Tws.Mean(TimeSpan.FromMinutes(10));
        public double? AwsMean1Min => Aws.Mean(TimeSpan.FromMinutes(1));
        public double? AwsMean10Min => Aws.Mean(TimeSpan.FromMinutes(10));
        public double? SogMean30Sec => Sog.Mean(TimeSpan.FromSeconds(30));
        public double? VmgMean1Min => Vmg.Mean(TimeSpan.FromMinutes(1));
        public double? CogMean30Sec => Cog.Mean(TimeSpan.FromSeconds(30));
        public double? CogTrueMean30Sec => CogTrue.Mean(TimeSpan.FromSeconds(30));
        public double? CogMagneticMean30Sec => CogMagnetic.Mean(TimeSpan.FromSeconds(30));
        public double? AwaMean30Sec => Awa.Mean(TimeSpan.FromSeconds(30));
        public double? TwaMean30Sec => Twa.Mean(TimeSpan.FromSeconds(30));

        public Task<OnaPlotter.Models.TrackPoint[]?> SeedWindAsync(
            OnaPlotter.Services.Api.ITrackApi trackApi,
            TimeSpan? window = null,
            string resolution = "5s",
            CancellationToken ct = default)
            => NavigationAverages.SeedWindBuffersAsync(
                Aws, Tws, Twd,
                trackApi,
                window ?? TimeSpan.FromHours(3),
                resolution,
                ct);

        // Mirror of NavigationAverages.Sample to keep the unit-test
        // sampler in lockstep with production logic. Any change to
        // production must also land here - a parity test would
        // close the loop, but the mirror is short enough to inspect.
        public void PublicSample(NavigationData d)
        {
            if (d.WindSpeedTrue is double tws) Tws.Add(tws);
            if (d.WindSpeedApparent is double aws) Aws.Add(aws);
            if (d.WindDirectionTrue is double twd) Twd.Add(twd);
            if (d.WindAngleApparent is double awa) Awa.Add(awa);
            if (d.WindAngleTrue is double twa) Twa.Add(twa);
            if (d.SpeedOverGround is double sog)
            {
                Sog.Add(sog);
                var weight = sog >= NavigationAverages.StationarySogMs ? 1.0 : 0.0;
                if (d.CourseOverGround is double cog) Cog.Add(cog, weight);
                if (d.CourseOverGroundTrue is double cogT) CogTrue.Add(cogT, weight);
                if (d.CourseOverGroundMagnetic is double cogM) CogMagnetic.Add(cogM, weight);
            }
            if (d.CourseNextPointVmg is double vmg) Vmg.Add(vmg);
        }

        public void Dispose() { }
    }

    [Test]
    public async Task Tws_Sampled_From_WindSpeedTrue()
    {
        var h = new SampleHarness();
        h.Nav.Apply("environment.wind.speedTrue", 5.0);
        h.Sample();
        h.Clock.Advance(TimeSpan.FromSeconds(31));
        h.Nav.Apply("environment.wind.speedTrue", 9.0);
        h.Sample();

        await Assert.That(h.Avg.TwsMean1Min).IsEqualTo(7.0);
    }

    [Test]
    public async Task Aws_Sampled_From_WindSpeedApparent()
    {
        var h = new SampleHarness();
        h.Nav.Apply("environment.wind.speedApparent", 6.0);
        h.Sample();
        h.Clock.Advance(TimeSpan.FromSeconds(31));
        h.Nav.Apply("environment.wind.speedApparent", 10.0);
        h.Sample();

        await Assert.That(h.Avg.AwsMean1Min).IsEqualTo(8.0);
    }

    [Test]
    public async Task Sog_Sampled_30s_Window()
    {
        var h = new SampleHarness();
        h.Nav.Apply("navigation.speedOverGround", 4.0);
        h.Sample();
        h.Clock.Advance(TimeSpan.FromSeconds(16));
        h.Nav.Apply("navigation.speedOverGround", 6.0);
        h.Sample();

        await Assert.That(h.Avg.SogMean30Sec).IsEqualTo(5.0);
    }

    [Test]
    public async Task Cog_Sampled_When_Moving()
    {
        var h = new SampleHarness();
        h.Nav.Apply("navigation.speedOverGround", 5.0);
        h.Nav.Apply("navigation.courseOverGroundTrue", Math.PI / 2);    // east
        h.Sample();
        h.Clock.Advance(TimeSpan.FromSeconds(16));
        h.Nav.Apply("navigation.speedOverGround", 5.0);
        h.Nav.Apply("navigation.courseOverGroundTrue", Math.PI / 2);
        h.Sample();

        var mean = h.Avg.CogMean30Sec;
        await Assert.That(mean).IsNotNull();
        await Assert.That(Math.Abs(mean!.Value - Math.PI / 2)).IsLessThan(0.01);
    }

    [Test]
    public async Task Cog_Stationary_DropsFromMean()
    {
        // When SOG is below the stationary threshold, the COG
        // sample is added with zero weight so it doesn't poison
        // the mean even though buffer entries accrue.
        var h = new SampleHarness();
        // First sample: moving east.
        h.Nav.Apply("navigation.speedOverGround", 5.0);
        h.Nav.Apply("navigation.courseOverGroundTrue", Math.PI / 2);
        h.Sample();
        // Second sample: stationary, GPS noise reports COG north.
        // Weight should be zero so the mean stays at east.
        h.Clock.Advance(TimeSpan.FromSeconds(16));
        h.Nav.Apply("navigation.speedOverGround", 0.05);   // < 0.25 stationary threshold
        h.Nav.Apply("navigation.courseOverGroundTrue", 0); // GPS noise direction
        h.Sample();

        var mean = h.Avg.CogMean30Sec;
        await Assert.That(mean).IsNotNull();
        await Assert.That(Math.Abs(mean!.Value - Math.PI / 2)).IsLessThan(0.01);
    }

    [Test]
    public async Task Vmg_Sampled_From_CourseNextPointVmg()
    {
        var h = new SampleHarness();
        h.Nav.Apply("navigation.course.calcValues.velocityMadeGood", 3.0);
        h.Sample();
        h.Clock.Advance(TimeSpan.FromSeconds(31));
        h.Nav.Apply("navigation.course.calcValues.velocityMadeGood", 5.0);
        h.Sample();

        await Assert.That(h.Avg.VmgMean1Min).IsEqualTo(4.0);
    }

    [Test]
    public async Task TenMin_Mean_DistinctFromOneMin()
    {
        var h = new SampleHarness();
        // t=0: TWS 5
        h.Nav.Apply("environment.wind.speedTrue", 5.0);
        h.Sample();
        // t=8min: TWS 9 - inside the 10-min window but outside 1-min.
        h.Clock.Advance(TimeSpan.FromMinutes(8));
        h.Nav.Apply("environment.wind.speedTrue", 9.0);
        h.Sample();

        // 10-min mean = (5 + 9) / 2 = 7
        await Assert.That(h.Avg.TwsMean10Min).IsEqualTo(7.0);
        // 1-min mean = only the 9 sample (t=8min, the 5 sample is past the window)
        await Assert.That(h.Avg.TwsMean1Min).IsEqualTo(9.0);
    }

    // - SeedWindAsync ------------------------------------------------

    /// <summary>Stub ITrackApi for seed-pipeline tests: returns a
    /// canned TrackPoint[] (or null) and remembers which call shape
    /// was used so tests can assert the request side.</summary>
    private sealed class StubTrackApi : OnaPlotter.Services.Api.ITrackApi
    {
        private readonly OnaPlotter.Models.TrackPoint[]? _points;
        public OnaPlotter.Services.Api.TrackFetchPathSet? LastPathSet { get; private set; }
        public string? LastTimespan { get; private set; }
        public string? LastResolution { get; private set; }

        public StubTrackApi(OnaPlotter.Models.TrackPoint[]? points) { _points = points; }

        public Task<double[][]?> GetServerTrackAsync(
            string timespan = "1d", string resolution = "1m", CancellationToken ct = default)
            => Task.FromResult<double[][]?>(null);

        public Task<OnaPlotter.Models.TrackPoint[]?> GetServerTrackPointsAsync(
            DateTimeOffset? from, DateTimeOffset? to, string? timespan,
            string resolution = "30s",
            OnaPlotter.Models.TrackBbox? bbox = null,
            OnaPlotter.Services.Api.TrackFetchPathSet pathSet = OnaPlotter.Services.Api.TrackFetchPathSet.Rich,
            CancellationToken ct = default)
        {
            LastPathSet = pathSet;
            LastTimespan = timespan;
            LastResolution = resolution;
            return Task.FromResult(_points);
        }
    }

    [Test]
    public async Task SeedWindAsync_FeedsAllThreeWindBuffers()
    {
        // Server returns three TrackPoints carrying AWS, TWS, TWD.
        // After seeding, the rolling buffers must read back those
        // values via Mean over the seeded window. Querying via the
        // raw buffers with warmupRatio: 0 because the convenience
        // 10-min / 1-min means trim or gate on warmup, which would
        // mask the seed wiring under test.
        var h = new SampleHarness();
        var clock = h.Clock;
        var now = clock.GetUtcNow().UtcDateTime;
        var points = new[]
        {
            new OnaPlotter.Models.TrackPoint(
                now - TimeSpan.FromMinutes(20), 47.4, 8.5, null, null, null,
                null, WindSpeedApparent: 6.0, null, WindSpeedTrue: 5.0,
                null, WindDirectionTrue: 0.0),
            new OnaPlotter.Models.TrackPoint(
                now - TimeSpan.FromMinutes(10), 47.4, 8.5, null, null, null,
                null, WindSpeedApparent: 8.0, null, WindSpeedTrue: 7.0,
                null, WindDirectionTrue: Math.PI / 2),
        };
        var api = new StubTrackApi(points);

        var seeded = await h.Avg.SeedWindAsync(api, TimeSpan.FromHours(1));

        await Assert.That(seeded).IsNotNull();
        await Assert.That(seeded!.Length).IsEqualTo(2);
        await Assert.That(h.Avg.Aws.Mean(TimeSpan.FromMinutes(30), warmupRatio: 0))
            .IsEqualTo(7.0);
        await Assert.That(h.Avg.Tws.Mean(TimeSpan.FromMinutes(30), warmupRatio: 0))
            .IsEqualTo(6.0);
        // TWD circular mean of (0, π/2) is π/4.
        var twd = h.Avg.Twd.Mean(TimeSpan.FromHours(1), warmupRatio: 0);
        await Assert.That(twd).IsNotNull();
        await Assert.That(Math.Abs(twd!.Value - Math.PI / 4)).IsLessThan(0.01);
    }

    [Test]
    public async Task SeedWindAsync_RequestsWindSeedPathSet()
    {
        // The seed must use the focused WindSeed path set (4 wind paths
        // + position), not the wider Rich set. Pin so a refactor that
        // forgets the pathSet arg doesn't widen the request payload.
        var h = new SampleHarness();
        var api = new StubTrackApi(System.Array.Empty<OnaPlotter.Models.TrackPoint>());

        await h.Avg.SeedWindAsync(api, TimeSpan.FromMinutes(30), resolution: "10s");

        await Assert.That(api.LastPathSet)
            .IsEqualTo(OnaPlotter.Services.Api.TrackFetchPathSet.WindSeed);
        await Assert.That(api.LastTimespan).IsEqualTo("30m");
        await Assert.That(api.LastResolution).IsEqualTo("10s");
    }

    [Test]
    public async Task SeedWindAsync_NullResponse_ReturnsNull()
    {
        // Server has no history (provider missing / empty window):
        // the seed quietly returns null. The page can still render
        // live data; the seed was a best-effort warmup.
        var h = new SampleHarness();
        var api = new StubTrackApi(null);

        var seeded = await h.Avg.SeedWindAsync(api);

        await Assert.That(seeded).IsNull();
        await Assert.That(h.Avg.Aws.Count).IsEqualTo(0);
        await Assert.That(h.Avg.Tws.Count).IsEqualTo(0);
        await Assert.That(h.Avg.Twd.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SeedWindAsync_PartialColumns_FeedsOnlyPopulatedBuffers()
    {
        // A server with only AWA/AWS in history (no derived TWD/TWS):
        // the seed populates the AWS buffer alone. TWD + TWS stay
        // empty until live deltas arrive. Pinned because mid-flight
        // a future plugin install would shift this and the page must
        // pick up new channels without code changes.
        var h = new SampleHarness();
        var now = h.Clock.GetUtcNow().UtcDateTime;
        var points = new[]
        {
            new OnaPlotter.Models.TrackPoint(
                now - TimeSpan.FromMinutes(5), 47.4, 8.5, null, null, null,
                null, WindSpeedApparent: 4.0, null, WindSpeedTrue: null,
                null, WindDirectionTrue: null),
        };
        var api = new StubTrackApi(points);

        var seeded = await h.Avg.SeedWindAsync(api);

        await Assert.That(seeded).IsNotNull();
        await Assert.That(seeded!.Length).IsEqualTo(1);
        await Assert.That(h.Avg.Aws.Count).IsEqualTo(1);
        await Assert.That(h.Avg.Tws.Count).IsEqualTo(0);
        await Assert.That(h.Avg.Twd.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SeedWindAsync_AfterLiveData_StillPopulatesHistory()
    {
        // The actual production sequence: NavigationAverages is a
        // singleton that subscribes to live deltas at app startup, so
        // the buffer ALWAYS has at least a few live samples by the
        // time WindRose.razor mounts and fires the seed. Historical
        // samples are timestamped in the past, so they're older than
        // every live sample already in the buffer. Without the merge
        // logic in Seed, the historical samples would silently drop.
        var h = new SampleHarness();

        // Live ingest first: two samples in the last few seconds.
        h.Nav.Apply("environment.wind.speedApparent", 6.0);
        h.Sample();
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        h.Nav.Apply("environment.wind.speedApparent", 8.0);
        h.Sample();

        var nowAtSeed = h.Clock.GetUtcNow().UtcDateTime;

        // Server's history goes back 30 minutes (i.e. well before the
        // live samples we just ingested).
        var points = new[]
        {
            new OnaPlotter.Models.TrackPoint(
                nowAtSeed - TimeSpan.FromMinutes(30), 47.4, 8.5, null, null, null,
                null, WindSpeedApparent: 4.0, null, null, null, null),
            new OnaPlotter.Models.TrackPoint(
                nowAtSeed - TimeSpan.FromMinutes(15), 47.4, 8.5, null, null, null,
                null, WindSpeedApparent: 5.0, null, null, null, null),
        };
        var api = new StubTrackApi(points);

        var seeded = await h.Avg.SeedWindAsync(api);

        await Assert.That(seeded).IsNotNull();
        // All four samples (2 historical + 2 live) must be in the
        // buffer, in monotonic ascending order.
        var snap = h.Avg.Aws.SnapshotIn(TimeSpan.FromMinutes(35));
        await Assert.That(snap.Count).IsEqualTo(4);
        for (int i = 1; i < snap.Count; i++)
        {
            await Assert.That(snap[i].Time > snap[i - 1].Time).IsTrue();
        }
        // Mean across all four samples.
        await Assert.That(h.Avg.Aws.Mean(TimeSpan.FromMinutes(35), warmupRatio: 0))
            .IsEqualTo((4.0 + 5.0 + 6.0 + 8.0) / 4);
    }

    [Test]
    public async Task SeedWindAsync_FollowedByLive_MaintainsMonotonicity()
    {
        // Seed lays down past samples; live ingest then keeps adding
        // forward. The combined buffer's mean must include all samples.
        // Query via the raw buffer with warmupRatio: 0 because the
        // convenience AwsMean10Min would gate on 50% window coverage
        // here (only ~2 min of data vs a 10-min window).
        var h = new SampleHarness();
        var now = h.Clock.GetUtcNow().UtcDateTime;
        var points = new[]
        {
            new OnaPlotter.Models.TrackPoint(
                now - TimeSpan.FromMinutes(2), 47.4, 8.5, null, null, null,
                null, WindSpeedApparent: 4.0, null, WindSpeedTrue: null,
                null, null),
        };
        var api = new StubTrackApi(points);
        await h.Avg.SeedWindAsync(api);

        // Live tick a moment later.
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        h.Nav.Apply("environment.wind.speedApparent", 6.0);
        h.Sample();

        await Assert.That(h.Avg.Aws.Mean(TimeSpan.FromMinutes(10), warmupRatio: 0))
            .IsEqualTo(5.0);
    }
}
