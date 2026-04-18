using OnaPlotter.Models;
using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

public class IsochroneRouterTests
{
    /// <summary>Constant wind field factory.</summary>
    private static Func<double, double, DateTime, WindSample?> ConstantWind(double dirDeg, double spdKn)
        => (_, _, t) => new WindSample(t, dirDeg, spdKn);

    /// <summary>Simple polar: full target speed on a broad reach (60-150 deg),
    /// half speed upwind (30-60 and 150-180), nothing in the no-go zone.
    /// Enough to force realistic tacking without wiring a real polar.</summary>
    private static double? SimplePolar(double twaDeg, double twsKn)
    {
        double twa = Math.Abs(twaDeg);
        if (twa < 30) return null;            // no-go zone
        if (twa < 60) return twsKn * 0.4;     // upwind
        if (twa <= 150) return twsKn * 0.8;   // reach
        return twsKn * 0.6;                   // broad run
    }

    private const double NmPerDegLat = 60.0;

    [Test]
    public async Task Destination_WithinReach_ReturnsTrivialRoute()
    {
        var route = IsochroneRouter.Route(
            startLat: 0, startLon: 0, startTime: DateTime.UtcNow,
            endLat: 0.001, endLon: 0.001,   // ~ 0.08 nm
            weather: ConstantWind(180, 10),
            polar: SimplePolar);

        await Assert.That(route).IsNotNull();
        await Assert.That(route!.Duration).IsEqualTo(TimeSpan.Zero);
        await Assert.That(route.Path.Count).IsEqualTo(2);
    }

    [Test]
    public async Task DownwindRun_ProducesRoute()
    {
        // Wind from N at 15 kn, destination 3 nm south of start -> dead run.
        // Should reach within a reasonable number of steps.
        var start = DateTime.UtcNow;
        var route = IsochroneRouter.Route(
            startLat: 0, startLon: 0, startTime: start,
            endLat: -3.0 / NmPerDegLat, endLon: 0,
            weather: ConstantWind(0, 15),       // FROM north
            polar: SimplePolar,
            options: new IsochroneRouter.Options(StepMinutes: 15, MaxSteps: 24, ReachNauticalMiles: 0.3));

        await Assert.That(route).IsNotNull();
        await Assert.That(route!.Path.Count).IsGreaterThanOrEqualTo(2);
        await Assert.That(route.TotalNauticalMiles).IsGreaterThan(2.5);
        await Assert.That(route.Duration.TotalMinutes).IsGreaterThan(0);
    }

    [Test]
    public async Task UpwindBeat_ProducesTackedRoute()
    {
        // Wind from N at 12 kn, destination 3 nm NORTH -> must tack.
        // A simple straight-line north has TWA=0 -> no-go. Router should
        // find a multi-waypoint path.
        var route = IsochroneRouter.Route(
            startLat: 0, startLon: 0, startTime: DateTime.UtcNow,
            endLat: 3.0 / NmPerDegLat, endLon: 0,
            weather: ConstantWind(0, 12),
            polar: SimplePolar,
            options: new IsochroneRouter.Options(StepMinutes: 15, MaxSteps: 32, ReachNauticalMiles: 0.4));

        await Assert.That(route).IsNotNull();
        // Upwind travel is longer than the straight-line distance.
        await Assert.That(route!.TotalNauticalMiles).IsGreaterThan(3.0);
        await Assert.That(route.Path.Count).IsGreaterThan(2);
    }

    [Test]
    public async Task Becalmed_ReturnsNull()
    {
        // Zero wind everywhere -> polar always returns 0 or null; every
        // bearing fails the MinBoatSpeed check.
        var route = IsochroneRouter.Route(
            startLat: 0, startLon: 0, startTime: DateTime.UtcNow,
            endLat: 2.0 / NmPerDegLat, endLon: 0,   // 2 nm, > ReachNm
            weather: ConstantWind(0, 0),
            polar: SimplePolar,
            options: new IsochroneRouter.Options(MaxSteps: 4, ReachNauticalMiles: 0.1));

        await Assert.That(route).IsNull();
    }

    [Test]
    public async Task UnreachableWithinHorizon_ReturnsNull()
    {
        // Destination 50 nm away, only 2 steps of 15 min -> 30 min of travel
        // at 8 kn (10 kn wind * 0.8 reach) = 4 nm. Way short of 50.
        var route = IsochroneRouter.Route(
            startLat: 0, startLon: 0, startTime: DateTime.UtcNow,
            endLat: 50.0 / NmPerDegLat, endLon: 0,
            weather: ConstantWind(90, 10),      // beam wind from E, ideal reach to N
            polar: SimplePolar,
            options: new IsochroneRouter.Options(StepMinutes: 15, MaxSteps: 2, ReachNauticalMiles: 0.5));

        await Assert.That(route).IsNull();
    }

    [Test]
    public async Task Route_PathIsChronological()
    {
        var start = DateTime.UtcNow;
        var route = IsochroneRouter.Route(
            startLat: 0, startLon: 0, startTime: start,
            endLat: 2.0 / NmPerDegLat, endLon: 1.0 / NmPerDegLat,
            weather: ConstantWind(180, 15),     // wind from S, reach to NE is on port tack
            polar: SimplePolar,
            options: new IsochroneRouter.Options(StepMinutes: 10, MaxSteps: 36, ReachNauticalMiles: 0.3));

        await Assert.That(route).IsNotNull();
        for (int i = 1; i < route!.Path.Count; i++)
        {
            await Assert.That(route.Path[i].Time).IsGreaterThanOrEqualTo(route.Path[i - 1].Time);
        }
    }

    [Test]
    public async Task Route_StartsAtStartAndEndsAtDestination()
    {
        var start = DateTime.UtcNow;
        var route = IsochroneRouter.Route(
            startLat: 10, startLon: 20, startTime: start,
            endLat: 10.05, endLon: 20.05,   // ~3 nm NE
            weather: ConstantWind(180, 12),
            polar: SimplePolar,
            options: new IsochroneRouter.Options(StepMinutes: 15, MaxSteps: 24, ReachNauticalMiles: 0.3));

        await Assert.That(route).IsNotNull();
        await Assert.That(route!.Path[0].Latitude).IsEqualTo(10);
        await Assert.That(route.Path[0].Longitude).IsEqualTo(20);
        await Assert.That(Math.Abs(route.Path[^1].Latitude - 10.05)).IsLessThan(0.001);
        await Assert.That(Math.Abs(route.Path[^1].Longitude - 20.05)).IsLessThan(0.001);
    }
}
