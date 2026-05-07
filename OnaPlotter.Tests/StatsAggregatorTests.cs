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
        // duration drives this, not segment data - a "this week"
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
        // distance (300 m of jitter - pathological but possible
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
        // not the sum - this is the headline test that pins the
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

    // ---------------- AggregateDaily --------------------------------

    [Test]
    public async Task AggregateDaily_BinsByLocalStartDate()
    {
        // Two trips on different UTC days. With UTC tz the binning is
        // unambiguous (no DST / wall-clock surprises). The aggregator
        // returns one row per day that had activity, sorted descending.
        var d1 = new DateTime(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc);
        var d2 = new DateTime(2026, 4, 3,  9, 0, 0, DateTimeKind.Utc);
        var segs = new[]
        {
            Seg(d1, d1.AddHours(1), stationary: false, distanceM: 1000),
            Seg(d2, d2.AddHours(2), stationary: false, distanceM: 2000),
        };

        var rows = StatsAggregator.AggregateDaily(segs, TimeZoneInfo.Utc);

        await Assert.That(rows.Count).IsEqualTo(2);
        // Descending sort: April 3 first.
        await Assert.That(rows[0].LocalDate).IsEqualTo(new DateTime(2026, 4, 3));
        await Assert.That(rows[0].DistanceMetres).IsEqualTo(2000);
        await Assert.That(rows[0].TripCount).IsEqualTo(1);
        await Assert.That(rows[1].LocalDate).IsEqualTo(new DateTime(2026, 4, 1));
        await Assert.That(rows[1].DistanceMetres).IsEqualTo(1000);
    }

    [Test]
    public async Task AggregateDaily_SameDay_SumsTripsAndPicksMaxSog()
    {
        // Two moving + one stationary on the same day. Aggregator
        // sums trip distances + durations, peaks the max SOG across
        // ALL segments on the day (including stationary - a surge
        // inside ferry-wash is still a real reading).
        var d = new DateTime(2026, 4, 1, 8, 0, 0, DateTimeKind.Utc);
        var segs = new[]
        {
            Seg(d.AddHours(0), d.AddHours(2), stationary: false, distanceM: 1000, sogMax: 3.0),
            Seg(d.AddHours(3), d.AddHours(4), stationary: true,  distanceM: 5,    sogMax: 5.5),  // ferry wash surge
            Seg(d.AddHours(5), d.AddHours(7), stationary: false, distanceM: 2000, sogMax: 4.0),
        };

        var rows = StatsAggregator.AggregateDaily(segs, TimeZoneInfo.Utc);

        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0].TripCount).IsEqualTo(2);
        await Assert.That(rows[0].DistanceMetres).IsEqualTo(3000);
        await Assert.That(rows[0].MaxSogMs).IsEqualTo(5.5);   // includes stationary
        await Assert.That(rows[0].MovingDurationSeconds).IsEqualTo(4 * 3600);
        await Assert.That(rows[0].StationaryDurationSeconds).IsEqualTo(1 * 3600);
    }

    [Test]
    public async Task AggregateDaily_AvgSogMs_PerDay_ExcludesStationaryTime()
    {
        // 1 h moving covering 1800 m -> 0.5 m/s avg for that day,
        // unaffected by stationary time on the same day.
        var d = new DateTime(2026, 4, 1, 8, 0, 0, DateTimeKind.Utc);
        var segs = new[]
        {
            Seg(d.AddHours(0), d.AddHours(1), stationary: false, distanceM: 1800),
            Seg(d.AddHours(2), d.AddHours(3), stationary: true,  distanceM: 100),
        };

        var rows = StatsAggregator.AggregateDaily(segs, TimeZoneInfo.Utc);

        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0].AvgSogMs).IsEqualTo(0.5);
    }

    [Test]
    public async Task AggregateDaily_DayOnlyStationary_HasZeroTripsAvgSogNull()
    {
        // A day at anchor with no moving segments still appears as a
        // row (it had stationary activity). TripCount = 0, AvgSogMs
        // = null (no underway time to average).
        var d = new DateTime(2026, 4, 1, 8, 0, 0, DateTimeKind.Utc);
        var segs = new[]
        {
            Seg(d.AddHours(0), d.AddHours(8), stationary: true, distanceM: 12),
        };

        var rows = StatsAggregator.AggregateDaily(segs, TimeZoneInfo.Utc);

        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0].TripCount).IsEqualTo(0);
        await Assert.That(rows[0].AvgSogMs).IsNull();
        await Assert.That(rows[0].StationaryDurationSeconds).IsEqualTo(8 * 3600);
    }

    [Test]
    public async Task AggregateDaily_EmptySegments_ReturnsEmpty()
    {
        var rows = StatsAggregator.AggregateDaily([], TimeZoneInfo.Utc);
        await Assert.That(rows.Count).IsEqualTo(0);
    }

    [Test]
    public async Task AggregateDaily_AttributesByStartDay_NotCrossingMidnight()
    {
        // Segment crosses midnight UTC: starts April 1 23:00, ends
        // April 2 01:00. Attributed to April 1 in full - splitting
        // distance across two days would need a uniform-speed
        // assumption (over-engineered for the rare case).
        var start = new DateTime(2026, 4, 1, 23, 0, 0, DateTimeKind.Utc);
        var segs = new[]
        {
            Seg(start, start.AddHours(2), stationary: false, distanceM: 1000),
        };

        var rows = StatsAggregator.AggregateDaily(segs, TimeZoneInfo.Utc);

        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0].LocalDate).IsEqualTo(new DateTime(2026, 4, 1));
        await Assert.That(rows[0].DistanceMetres).IsEqualTo(1000);
    }
}
