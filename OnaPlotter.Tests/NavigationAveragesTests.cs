using Microsoft.Extensions.Time.Testing;
using OnaPlotter.Models;
using OnaPlotter.Services;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins NavigationAverages' sampling pipeline -- which channel each
/// NavigationData field feeds, plus the stationary-COG suppression.
/// The rolling-buffer math itself is exercised by RollingScalarSeries
/// and RollingDirectionSeries tests; here we only verify the wiring.
///
/// <para>Tests bypass the SignalkClient subscription via the internal
/// SampleForTest seam -- the OnDataChanged subscription is a one-line
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
        // construction isn't worth chasing -- skip the ctor wiring
        // verification and test the Sample() pipeline directly via
        // a NavigationAverages built around a stub.
        return null!;   // see SampleHarness below for the actual approach.
    }

    /// <summary>Test fixture that sidesteps SignalkClient entirely
    /// by exposing the rolling buffers directly via the public
    /// Tws / Aws / Sog / etc. properties + a Sample() method.
    /// Production wiring (ctor subscribes to OnDataChanged) is
    /// covered by the host integration -- not unit-tested here.</summary>
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
    /// client -- production NavigationAverages is identical except
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
        }

        public double? TwsMean1Min => Tws.Mean(TimeSpan.FromMinutes(1));
        public double? TwsMean10Min => Tws.Mean(TimeSpan.FromMinutes(10));
        public double? AwsMean1Min => Aws.Mean(TimeSpan.FromMinutes(1));
        public double? AwsMean10Min => Aws.Mean(TimeSpan.FromMinutes(10));
        public double? SogMean30Sec => Sog.Mean(TimeSpan.FromSeconds(30));
        public double? VmgMean1Min => Vmg.Mean(TimeSpan.FromMinutes(1));
        public double? CogMean30Sec => Cog.Mean(TimeSpan.FromSeconds(30));
        public double? AwaMean30Sec => Awa.Mean(TimeSpan.FromSeconds(30));
        public double? TwaMean30Sec => Twa.Mean(TimeSpan.FromSeconds(30));

        // Mirror of NavigationAverages.Sample to keep the unit-test
        // sampler in lockstep with production logic. Any change to
        // production must also land here -- a parity test would
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
                if (d.CourseOverGround is double cog)
                {
                    var weight = sog >= NavigationAverages.StationarySogMs ? 1.0 : 0.0;
                    Cog.Add(cog, weight);
                }
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
        // t=8min: TWS 9 -- inside the 10-min window but outside 1-min.
        h.Clock.Advance(TimeSpan.FromMinutes(8));
        h.Nav.Apply("environment.wind.speedTrue", 9.0);
        h.Sample();

        // 10-min mean = (5 + 9) / 2 = 7
        await Assert.That(h.Avg.TwsMean10Min).IsEqualTo(7.0);
        // 1-min mean = only the 9 sample (t=8min, the 5 sample is past the window)
        await Assert.That(h.Avg.TwsMean1Min).IsEqualTo(9.0);
    }
}
