using OnaPlotter.Models;
using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the stats aggregator's contract: only moving segments
/// contribute to distance + trip counts; stationary time is summed
/// separately; the helm-supplied window drives the total-duration
/// field even when the data window is a subset.
/// </summary>
public class StatsAggregatorTests
{
    private static readonly DateTime From = new(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2024, 6, 8, 0, 0, 0, DateTimeKind.Utc);

    private static TrackSegment Seg(
        DateTime start, DateTime end, bool stationary,
        double distanceM = 0, double? sogMax = null)
        => new(
            StartUtc: start, EndUtc: end,
            StartLat: 0, StartLon: 0, EndLat: 0, EndLon: 0,
            DistanceMetres: distanceM,
            SogAvgMs: null, SogMaxMs: sogMax, SogMinMs: null,
            WindSpeedAvgMs: null,
            IsStationary: stationary,
            PointCount: 2);

    [Test]
    public async Task EmptySegments_TripCountZero_DistancesZero_DurationFromWindow()
    {
        var t = StatsAggregator.Aggregate(From, To, []);

        await Assert.That(t.TripCount).IsEqualTo(0);
        await Assert.That(t.MovingDurationSeconds).IsEqualTo(0);
        await Assert.That(t.StationaryDurationSeconds).IsEqualTo(0);
        await Assert.That(t.TotalDistanceMetres).IsEqualTo(0);
        // Window 7 days = 7 * 86400 = 604800 s. The helm's queried
        // duration drives this, not segment data -- a "this week"
        // query that returned nothing still reports 7 days total.
        await Assert.That(t.TotalDurationSeconds).IsEqualTo(7 * 86400.0);
        await Assert.That(t.MaxTripDistanceMetres).IsNull();
        await Assert.That(t.MaxSogMs).IsNull();
    }

    [Test]
    public async Task OnlyStationary_TripsZero_NoDistance_StationaryTimeSummed()
    {
        // Two stationary segments totalling 5h. No trips, no nm.
        var segs = new[]
        {
            Seg(From.AddHours(0), From.AddHours(2), stationary: true,  distanceM: 5),
            Seg(From.AddHours(8), From.AddHours(11), stationary: true, distanceM: 3),
        };

        var t = StatsAggregator.Aggregate(From, To, segs);

        await Assert.That(t.TripCount).IsEqualTo(0);
        await Assert.That(t.TotalDistanceMetres).IsEqualTo(0);  // stationary distance excluded
        await Assert.That(t.MovingDurationSeconds).IsEqualTo(0);
        await Assert.That(t.StationaryDurationSeconds).IsEqualTo(5 * 3600.0);
        await Assert.That(t.MaxTripDistanceMetres).IsNull();
    }

    [Test]
    public async Task StationaryDistance_ExcludedFromTotalDistance()
    {
        // Stationary segments carry GPS-jitter accumulated distance.
        // Including it would inflate the helm's "season nm" total.
        // Pin the exclusion: 100 m moving + 50 m of stationary
        // jitter = 100 m total, NOT 150 m.
        var segs = new[]
        {
            Seg(From.AddHours(0), From.AddHours(1), stationary: false, distanceM: 100),
            Seg(From.AddHours(2), From.AddHours(3), stationary: true,  distanceM: 50),
        };

        var t = StatsAggregator.Aggregate(From, To, segs);

        await Assert.That(t.TotalDistanceMetres).IsEqualTo(100);
        await Assert.That(t.TripCount).IsEqualTo(1);
    }

    [Test]
    public async Task MaxTripDistance_TakesLongestMovingSegment_NotStationary()
    {
        // Two moving (100 m, 200 m) + one stationary with a larger
        // distance (300 m of jitter -- pathological but possible
        // over hours of dwell). MaxTripDistanceMetres must come from
        // moving only.
        var segs = new[]
        {
            Seg(From.AddHours(0), From.AddHours(1), stationary: false, distanceM: 100),
            Seg(From.AddHours(2), From.AddHours(3), stationary: false, distanceM: 200),
            Seg(From.AddHours(4), From.AddHours(5), stationary: true,  distanceM: 300),
        };

        var t = StatsAggregator.Aggregate(From, To, segs);

        await Assert.That(t.MaxTripDistanceMetres).IsEqualTo(200);
    }

    [Test]
    public async Task MaxSog_ConsidersAllSegments_IncludingStationary()
    {
        // A "stationary" segment by classification can still contain
        // a momentary speed surge (ferry wash, engine bump). The
        // helm's "max boat speed this season" question is about
        // OBSERVED speed across the whole window, not just within
        // trips. Pin that semantics so a future tweak doesn't
        // silently exclude in-stationary peak SOG.
        var segs = new[]
        {
            Seg(From.AddHours(0), From.AddHours(1), stationary: false, sogMax: 3.0),
            Seg(From.AddHours(2), From.AddHours(3), stationary: true,  sogMax: 5.0),
        };

        var t = StatsAggregator.Aggregate(From, To, segs);

        await Assert.That(t.MaxSogMs).IsEqualTo(5.0);
    }

    [Test]
    public async Task MaxSog_NullWhenNoSegmentCarriesSog()
    {
        var segs = new[]
        {
            Seg(From, From.AddHours(1), stationary: false, distanceM: 100),
        };

        var t = StatsAggregator.Aggregate(From, To, segs);

        await Assert.That(t.MaxSogMs).IsNull();
    }

    [Test]
    public async Task TripCount_CountsOnlyMovingSegments()
    {
        // Three trips + four anchorages.
        var segs = new[]
        {
            Seg(From.AddHours(0),  From.AddHours(1),  stationary: false, distanceM: 100),
            Seg(From.AddHours(2),  From.AddHours(3),  stationary: true,  distanceM: 5),
            Seg(From.AddHours(4),  From.AddHours(5),  stationary: false, distanceM: 200),
            Seg(From.AddHours(6),  From.AddHours(7),  stationary: true,  distanceM: 5),
            Seg(From.AddHours(8),  From.AddHours(9),  stationary: false, distanceM: 150),
            Seg(From.AddHours(10), From.AddHours(11), stationary: true,  distanceM: 5),
            Seg(From.AddHours(12), From.AddHours(13), stationary: true,  distanceM: 5),
        };

        var t = StatsAggregator.Aggregate(From, To, segs);

        await Assert.That(t.TripCount).IsEqualTo(3);
    }

    [Test]
    public async Task InvertedWindow_ClampsTotalToZero()
    {
        // Defensive: if the helm passes from > to (typo in the date
        // picker or a programmatic caller hasn't normalised), the
        // total-duration would otherwise come out negative. Clamp to
        // zero so the UI doesn't render "-2 h total" which the helm
        // can't make sense of.
        var t = StatsAggregator.Aggregate(To, From, []);

        await Assert.That(t.TotalDurationSeconds).IsEqualTo(0);
    }

    // ---------------- AvgSogMs --------------------------------------

    [Test]
    public async Task AvgSogMs_DistanceOverMovingTime()
    {
        // 1 h moving = 3600 s, 3600 m moving distance -> exactly
        // 1 m/s avg. Helm question "what was my average speed?"
        // derives from numbers we already have rather than averaging
        // segment SogAvgMs, which would skew when a stretch had no
        // SOG samples.
        var segs = new[]
        {
            Seg(From, From.AddHours(1), stationary: false, distanceM: 3600),
        };

        var t = StatsAggregator.Aggregate(From, To, segs);

        await Assert.That(t.AvgSogMs).IsEqualTo(1.0);
    }

    [Test]
    public async Task AvgSogMs_NullWhenNoMovingTime()
    {
        // No moving segments -> avg-of-zero is undefined, not 0.
        // Rendering "0.0 kn" would mislead.
        var segs = new[]
        {
            Seg(From, From.AddHours(2), stationary: true, distanceM: 5),
        };

        var t = StatsAggregator.Aggregate(From, To, segs);

        await Assert.That(t.AvgSogMs).IsNull();
    }

    [Test]
    public async Task AvgSogMs_StationaryDistanceExcluded()
    {
        // Avg = total moving distance / total moving time. Stationary
        // distance must NOT contribute to the numerator (GPS jitter,
        // not progress); stationary time must NOT contribute to the
        // denominator (the boat wasn't moving).
        var segs = new[]
        {
            Seg(From, From.AddHours(1), stationary: false, distanceM: 1800),  // 0.5 m/s
            Seg(From.AddHours(2), From.AddHours(3), stationary: true,  distanceM: 100),
        };

        var t = StatsAggregator.Aggregate(From, To, segs);

        await Assert.That(t.AvgSogMs).IsEqualTo(0.5);
    }

    // ---------------- Best24hMetres ---------------------------------

    [Test]
    public async Task Best24hMetres_SingleSegment_EqualsItsDistance()
    {
        var segs = new[]
        {
            Seg(From, From.AddHours(2), stationary: false, distanceM: 12345),
        };

        var t = StatsAggregator.Aggregate(From, To, segs);

        await Assert.That(t.Best24hMetres).IsEqualTo(12345);
    }

    [Test]
    public async Task Best24hMetres_TwoSegments_WithinSameDay_SumsBoth()
    {
        // Two trips on the same calendar day fit one 24h rolling
        // window -> both contribute.
        var segs = new[]
        {
            Seg(From.AddHours(0), From.AddHours(2),  stationary: false, distanceM: 10000),
            Seg(From.AddHours(6), From.AddHours(10), stationary: false, distanceM: 20000),
        };

        var t = StatsAggregator.Aggregate(From, To, segs);

        await Assert.That(t.Best24hMetres).IsEqualTo(30000);
    }

    [Test]
    public async Task Best24hMetres_SegmentsMoreThan24hApart_TakesMaxIndividual()
    {
        // Three trips spaced 30h apart. No rolling 24h window
        // contains more than one. Best24h = max single-trip distance,
        // not the sum -- this is the headline test that pins the
        // "rolling window, not lifetime sum" semantics.
        var segs = new[]
        {
            Seg(From.AddHours(0),  From.AddHours(2),  stationary: false, distanceM: 10000),
            Seg(From.AddHours(30), From.AddHours(32), stationary: false, distanceM: 25000),
            Seg(From.AddHours(60), From.AddHours(62), stationary: false, distanceM: 15000),
        };

        var t = StatsAggregator.Aggregate(From, To, segs);

        await Assert.That(t.Best24hMetres).IsEqualTo(25000);
    }

    [Test]
    public async Task Best24hMetres_StationarySegments_DontContribute()
    {
        // A stationary segment between two trips must NOT pull its
        // distance into the rolling-window sum even when it falls
        // inside the window. (GPS-jitter distance again.)
        var segs = new[]
        {
            Seg(From.AddHours(0),  From.AddHours(2),  stationary: false, distanceM: 10000),
            Seg(From.AddHours(4),  From.AddHours(8),  stationary: true,  distanceM: 999999),
            Seg(From.AddHours(10), From.AddHours(12), stationary: false, distanceM: 20000),
        };

        var t = StatsAggregator.Aggregate(From, To, segs);

        await Assert.That(t.Best24hMetres).IsEqualTo(30000);
    }

    [Test]
    public async Task Best24hMetres_NullWhenNoMovingSegments()
    {
        var segs = new[]
        {
            Seg(From, From.AddHours(5), stationary: true, distanceM: 5),
        };

        var t = StatsAggregator.Aggregate(From, To, segs);

        await Assert.That(t.Best24hMetres).IsNull();
    }
}
