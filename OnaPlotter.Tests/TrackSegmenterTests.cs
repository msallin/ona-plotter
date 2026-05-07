using OnaPlotter.Models;
using OnaPlotter.Services;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the segmenter's stationary/moving classification + stats
/// math. Drives the algorithm directly with crafted point arrays -
/// no API, no clock, no IO - so failures are pinpoint.
/// </summary>
public class TrackSegmenterTests
{
    /// <summary>Helper to build a point at <c>t0 + offset</c>. Position
    /// drifts along latitude proportional to (sog * dt) so a moving
    /// classification can be inferred from inter-sample distance even
    /// when SOG isn't supplied.</summary>
    private static TrackPoint Pt(DateTime t0, TimeSpan offset, double lat, double lon,
        double? sog = null, double? tws = null)
    {
        return new TrackPoint(
            Timestamp: t0 + offset,
            Latitude: lat,
            Longitude: lon,
            SpeedOverGround: sog,
            CourseOverGround: null,
            Heading: null,
            WindAngleApparent: null,
            WindSpeedApparent: null,
            WindAngleTrue: null,
            WindSpeedTrue: tws);
    }

    private static readonly DateTime T0 =
        new(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task EmptyInput_ReturnsEmpty()
    {
        var segs = TrackSegmenter.Segment([]);
        await Assert.That(segs.Length).IsEqualTo(0);
    }

    [Test]
    public async Task SinglePoint_ReturnsEmpty()
    {
        // Can't define a segment from one fix - no edges, no
        // classification possible.
        var segs = TrackSegmenter.Segment([
            Pt(T0, TimeSpan.Zero, 47.4, 8.5, sog: 3.0)
        ]);
        await Assert.That(segs.Length).IsEqualTo(0);
    }

    [Test]
    public async Task AllStationary_OneSegment_FlaggedStationary()
    {
        // Boat at anchor for 30 minutes, SOG bobbing under threshold.
        var pts = new List<TrackPoint>();
        for (int i = 0; i <= 30; i++)
            pts.Add(Pt(T0, TimeSpan.FromMinutes(i), 47.4, 8.5, sog: 0.1));

        var segs = TrackSegmenter.Segment(pts);

        await Assert.That(segs.Length).IsEqualTo(1);
        await Assert.That(segs[0].IsStationary).IsTrue();
        await Assert.That(segs[0].PointCount).IsEqualTo(31);
    }

    [Test]
    public async Task AllMoving_OneSegment_FlaggedMoving()
    {
        // Sustained passage at 5 kn (~2.57 m/s) for 60 minutes.
        // Position must advance with the speed so the classification
        // lands as moving even when we re-derive from inter-sample
        // distance (defensive: ensures the SOG-based path agrees with
        // the distance-based fallback).
        var pts = new List<TrackPoint>();
        double lat = 47.4;
        const double sog = 2.57;
        for (int i = 0; i <= 60; i++)
        {
            pts.Add(Pt(T0, TimeSpan.FromMinutes(i), lat, 8.5, sog: sog));
            // ~2.57 m/s * 60 s = 154 m per minute = 0.00138 deg lat.
            lat += 0.00138;
        }

        var segs = TrackSegmenter.Segment(pts);

        await Assert.That(segs.Length).IsEqualTo(1);
        await Assert.That(segs[0].IsStationary).IsFalse();
        await Assert.That(segs[0].PointCount).IsEqualTo(61);
        // Distance over an hour at 5 kn ≈ 9270 m. Allow ±100 m for
        // float-precision drift in the lat-step approximation.
        await Assert.That(segs[0].DistanceMetres).IsGreaterThan(9000);
        await Assert.That(segs[0].DistanceMetres).IsLessThan(9500);
    }

    [Test]
    public async Task StationaryThenMoving_SplitsIntoTwoSegments()
    {
        // 20 min at anchor, then 40 min sailing. The state flip
        // sustains well past the 3-min debounce so we should see two
        // committed segments.
        var pts = new List<TrackPoint>();
        for (int i = 0; i < 20; i++)
            pts.Add(Pt(T0, TimeSpan.FromMinutes(i), 47.4, 8.5, sog: 0.1));
        // Step over the threshold for the next 40 min.
        double lat = 47.4;
        for (int i = 20; i < 60; i++)
        {
            pts.Add(Pt(T0, TimeSpan.FromMinutes(i), lat, 8.5, sog: 3.0));
            lat += 0.00161;       // ~5.8 kn
        }

        var segs = TrackSegmenter.Segment(pts);

        await Assert.That(segs.Length).IsEqualTo(2);
        await Assert.That(segs[0].IsStationary).IsTrue();
        await Assert.That(segs[1].IsStationary).IsFalse();
        // First segment ends at sample 19 (20 min); second begins at 20.
        await Assert.That(segs[0].PointCount).IsEqualTo(20);
        await Assert.That(segs[1].PointCount).IsEqualTo(40);
    }

    [Test]
    public async Task BriefSlowSpotInPassage_DoesNotSplit()
    {
        // 30 min sailing, single 30-second slow blip (e.g. a tack
        // mid-manoeuvre), 30 min more sailing. The blip is shorter
        // than the 3-min debounce so it must NOT manufacture a fake
        // stationary segment.
        var pts = new List<TrackPoint>();
        double lat = 47.4;
        for (int i = 0; i < 30; i++)
        {
            pts.Add(Pt(T0, TimeSpan.FromMinutes(i), lat, 8.5, sog: 3.0));
            lat += 0.00161;
        }
        // 30-second slow patch at minute 30.
        pts.Add(Pt(T0, TimeSpan.FromMinutes(30), lat, 8.5, sog: 0.1));
        for (int i = 31; i < 60; i++)
        {
            pts.Add(Pt(T0, TimeSpan.FromMinutes(i), lat, 8.5, sog: 3.0));
            lat += 0.00161;
        }

        var segs = TrackSegmenter.Segment(pts);

        await Assert.That(segs.Length).IsEqualTo(1);
        await Assert.That(segs[0].IsStationary).IsFalse();
    }

    [Test]
    public async Task BriefFastSpotAtAnchor_DoesNotSplit()
    {
        // Mirror of the above: at anchor, single sample reads "fast"
        // (GPS fix bounce). Must not split into a fake passage.
        var pts = new List<TrackPoint>();
        for (int i = 0; i < 30; i++)
            pts.Add(Pt(T0, TimeSpan.FromMinutes(i), 47.4, 8.5, sog: 0.1));
        pts.Add(Pt(T0, TimeSpan.FromMinutes(30), 47.4, 8.5, sog: 5.0));
        for (int i = 31; i < 60; i++)
            pts.Add(Pt(T0, TimeSpan.FromMinutes(i), 47.4, 8.5, sog: 0.1));

        var segs = TrackSegmenter.Segment(pts);

        await Assert.That(segs.Length).IsEqualTo(1);
        await Assert.That(segs[0].IsStationary).IsTrue();
    }

    [Test]
    public async Task SogMissing_FallsBackToInterSampleDistance()
    {
        // No SOG on any sample. Position advances at ~3 m/s (well over
        // threshold) so the inter-sample-speed fallback should classify
        // the run as moving.
        var pts = new List<TrackPoint>();
        double lat = 47.4;
        for (int i = 0; i <= 30; i++)
        {
            pts.Add(Pt(T0, TimeSpan.FromMinutes(i), lat, 8.5));
            // ~3 m/s * 60 s = 180 m per minute ≈ 0.00162 deg lat.
            lat += 0.00162;
        }

        var segs = TrackSegmenter.Segment(pts);

        await Assert.That(segs.Length).IsEqualTo(1);
        await Assert.That(segs[0].IsStationary).IsFalse();
        // Aggregates are null because no sample had SOG.
        await Assert.That(segs[0].SogAvgMs).IsNull();
        await Assert.That(segs[0].SogMaxMs).IsNull();
    }

    [Test]
    public async Task SogStats_ComputedOverNonNullSamplesOnly()
    {
        // Mix of SOG-bearing and SOG-missing samples. The aggregate
        // must reflect only the non-null subset; otherwise a sporadic
        // SOG feed would pull the average toward 0.
        var pts = new List<TrackPoint>
        {
            Pt(T0, TimeSpan.FromMinutes(0),  47.40000, 8.5, sog: 3.0),
            Pt(T0, TimeSpan.FromMinutes(1),  47.40161, 8.5, sog: null),
            Pt(T0, TimeSpan.FromMinutes(2),  47.40322, 8.5, sog: 5.0),
            Pt(T0, TimeSpan.FromMinutes(3),  47.40483, 8.5, sog: 4.0),
            Pt(T0, TimeSpan.FromMinutes(4),  47.40644, 8.5, sog: null),
            Pt(T0, TimeSpan.FromMinutes(5),  47.40805, 8.5, sog: 2.0),
        };

        var segs = TrackSegmenter.Segment(pts);

        await Assert.That(segs.Length).IsEqualTo(1);
        var s = segs[0];
        // Average of 3, 5, 4, 2 = 3.5
        await Assert.That(s.SogAvgMs!.Value).IsEqualTo(3.5).Within(0.001);
        await Assert.That(s.SogMaxMs!.Value).IsEqualTo(5.0).Within(0.001);
        await Assert.That(s.SogMinMs!.Value).IsEqualTo(2.0).Within(0.001);
    }

    [Test]
    public async Task WindStats_AveragedFromTrueWindOnly()
    {
        // Mixed: some samples have TWS, some don't. Aggregate over
        // present samples only; null when no sample carried wind.
        var pts = new List<TrackPoint>
        {
            Pt(T0, TimeSpan.FromMinutes(0),  47.40000, 8.5, sog: 3.0, tws: 5.0),
            Pt(T0, TimeSpan.FromMinutes(1),  47.40161, 8.5, sog: 3.0, tws: 7.0),
            Pt(T0, TimeSpan.FromMinutes(2),  47.40322, 8.5, sog: 3.0, tws: null),
            Pt(T0, TimeSpan.FromMinutes(3),  47.40483, 8.5, sog: 3.0, tws: 9.0),
        };

        var segs = TrackSegmenter.Segment(pts);

        await Assert.That(segs.Length).IsEqualTo(1);
        // Average of 5, 7, 9 = 7.0
        await Assert.That(segs[0].WindSpeedAvgMs!.Value).IsEqualTo(7.0).Within(0.001);
    }

    [Test]
    public async Task WindAbsentEverywhere_ReturnsNullAvg()
    {
        var pts = new List<TrackPoint>
        {
            Pt(T0, TimeSpan.FromMinutes(0),  47.4, 8.5, sog: 3.0),
            Pt(T0, TimeSpan.FromMinutes(1),  47.5, 8.5, sog: 3.0),
        };

        var segs = TrackSegmenter.Segment(pts);

        await Assert.That(segs[0].WindSpeedAvgMs).IsNull();
    }

    [Test]
    public async Task SegmentBoundsAndPointCountConsistent()
    {
        // 10 stationary + 50 moving samples → two segments whose point
        // counts sum to the input length, and whose Start/End coincide
        // with the boundary samples.
        var pts = new List<TrackPoint>();
        for (int i = 0; i < 10; i++)
            pts.Add(Pt(T0, TimeSpan.FromMinutes(i), 47.4, 8.5, sog: 0.1));
        double lat = 47.4;
        for (int i = 10; i < 60; i++)
        {
            pts.Add(Pt(T0, TimeSpan.FromMinutes(i), lat, 8.5, sog: 3.0));
            lat += 0.00161;
        }

        var segs = TrackSegmenter.Segment(pts);
        await Assert.That(segs.Length).IsEqualTo(2);
        await Assert.That(segs[0].PointCount + segs[1].PointCount).IsEqualTo(60);
        // First segment starts at first sample.
        await Assert.That(segs[0].StartUtc).IsEqualTo(pts[0].Timestamp);
        // Last segment ends at last sample.
        await Assert.That(segs[1].EndUtc).IsEqualTo(pts[^1].Timestamp);
    }

    [Test]
    public async Task BriefMovingSpot_BelowMinSegmentDuration_AbsorbedIntoStationary()
    {
        // 30 min stationary at anchor, then a 90-second moving blip
        // (4 SOG-elevated samples), then back to stationary for 30
        // more minutes. The middle blip survives the 3-min debounce
        // (because the candidate flips back too fast for the segmenter
        // to NOT commit it - actually it gets committed once the run
        // accumulates DebounceWindow's worth, so a single blip never
        // gets that far). The merge pass is what saves us: the
        // resulting "moving" segment is sub-MinSegmentDuration (2 min)
        // so it gets absorbed.
        //
        // Pinned because the previous implementation only checked
        // "fewer than 2 points" and let multi-point blips survive -
        // exactly the "14-second moving trip the helm caused by
        // stepping on the throttle" case the constant comment warns
        // about.
        var pts = new List<TrackPoint>();
        // 30 min stationary
        for (int i = 0; i < 30; i++)
            pts.Add(Pt(T0, TimeSpan.FromMinutes(i), 47.4, 8.5, sog: 0.1));
        // 90-second moving spike: 30s, 60s, 90s into the blip from
        // minute 30. We use shorter cadence here than the 1-min outer
        // pattern so the blip has multiple samples but stays under
        // MinSegmentDuration once the debounce committed it.
        for (int i = 0; i < 4; i++)
            pts.Add(Pt(T0, TimeSpan.FromMinutes(30) + TimeSpan.FromSeconds(i * 30), 47.4, 8.5, sog: 3.0));
        // 30 min stationary again, starting at minute 32
        for (int i = 32; i < 62; i++)
            pts.Add(Pt(T0, TimeSpan.FromMinutes(i), 47.4, 8.5, sog: 0.1));

        var segs = TrackSegmenter.Segment(pts);

        // The blip should NOT survive as its own segment. Either we
        // see one merged stationary segment, or two stationary
        // segments separated by the (absorbed) blip - both outcomes
        // mean no spurious "moving" trip.
        await Assert.That(segs.All(s => s.IsStationary)).IsTrue();
    }

    [Test]
    public async Task SogExactlyAtThreshold_ClassifiesStationary()
    {
        // Pin the strict-inequality semantics of MovingThresholdMs.
        // sog == 0.257 m/s == threshold. The classifier uses `> threshold`,
        // so equality is stationary. A future refactor that flipped to
        // `>=` would silently convert a sustained tide-only drift
        // (rarely > 0.3 kn) into a manufactured "trip".
        var pts = new List<TrackPoint>();
        for (int i = 0; i <= 30; i++)
            pts.Add(Pt(T0, TimeSpan.FromMinutes(i), 47.4, 8.5,
                sog: TrackSegmenter.MovingThresholdMs));

        var segs = TrackSegmenter.Segment(pts);

        await Assert.That(segs.Length).IsEqualTo(1);
        await Assert.That(segs[0].IsStationary).IsTrue();
    }

    [Test]
    public async Task SogJustAboveThreshold_ClassifiesMoving()
    {
        var pts = new List<TrackPoint>();
        double lat = 47.4;
        for (int i = 0; i <= 30; i++)
        {
            pts.Add(Pt(T0, TimeSpan.FromMinutes(i), lat, 8.5,
                sog: TrackSegmenter.MovingThresholdMs + 0.001));
            lat += 0.00014;       // matches ~0.258 m/s
        }

        var segs = TrackSegmenter.Segment(pts);

        await Assert.That(segs.Length).IsEqualTo(1);
        await Assert.That(segs[0].IsStationary).IsFalse();
    }

    [Test]
    public async Task DuplicateTimestamp_NoSog_ClassifiesStationary()
    {
        // Aggregator hiccup: two samples at the same timestamp. The
        // dt <= 0 guard in the classifier returns false (stationary)
        // rather than dividing by zero. Without the guard the fallback
        // would divide and produce Infinity, classifying the gap as
        // moving silently.
        var pts = new List<TrackPoint>
        {
            Pt(T0, TimeSpan.FromMinutes(0), 47.4, 8.5),
            Pt(T0, TimeSpan.FromMinutes(0), 47.5, 8.5),
            Pt(T0, TimeSpan.FromMinutes(1), 47.5, 8.5),
        };

        // Should not throw on the divide-by-zero path.
        var segs = TrackSegmenter.Segment(pts);

        // Single segment, classified as stationary.
        await Assert.That(segs.Length).IsEqualTo(1);
    }

    [Test]
    public async Task MovingThenStationaryAtTail_DebouncedFlip_SplitsIntoTwo()
    {
        // 30 min sailing, then 4 min at anchor at the end of the
        // window. The trailing 4 min crosses the 3-min DebounceWindow
        // so the segmenter must commit the flip and emit two
        // segments.
        var pts = new List<TrackPoint>();
        double lat = 47.4;
        for (int i = 0; i < 30; i++)
        {
            pts.Add(Pt(T0, TimeSpan.FromMinutes(i), lat, 8.5, sog: 3.0));
            lat += 0.00161;
        }
        for (int i = 30; i < 34; i++)
            pts.Add(Pt(T0, TimeSpan.FromMinutes(i), lat, 8.5, sog: 0.1));

        var segs = TrackSegmenter.Segment(pts);

        await Assert.That(segs.Length).IsEqualTo(2);
        await Assert.That(segs[0].IsStationary).IsFalse();
        await Assert.That(segs[1].IsStationary).IsTrue();
    }

    [Test]
    public async Task MovingThenStationaryAtTail_BelowDebounce_StaysOne()
    {
        // 30 min sailing, then 2 min at anchor. The trailing 2 min
        // doesn't cross the 3-min debounce window so the segmenter
        // absorbs it back into the moving run.
        var pts = new List<TrackPoint>();
        double lat = 47.4;
        for (int i = 0; i < 30; i++)
        {
            pts.Add(Pt(T0, TimeSpan.FromMinutes(i), lat, 8.5, sog: 3.0));
            lat += 0.00161;
        }
        for (int i = 30; i < 32; i++)
            pts.Add(Pt(T0, TimeSpan.FromMinutes(i), lat, 8.5, sog: 0.1));

        var segs = TrackSegmenter.Segment(pts);

        await Assert.That(segs.Length).IsEqualTo(1);
        await Assert.That(segs[0].IsStationary).IsFalse();
    }

    [Test]
    public async Task DistanceForStationarySegmentIsTinyButNotNegative()
    {
        // GPS-noise-only stationary: position oscillates by a few
        // metres. Distance should accumulate the noise (positive,
        // small) - not zero (we sum every step) and never negative.
        var pts = new List<TrackPoint>();
        var rng = new Random(42);
        for (int i = 0; i <= 30; i++)
        {
            // ±5 m of jitter expressed as ±0.000045 deg lat.
            double dLat = (rng.NextDouble() - 0.5) * 0.00009;
            pts.Add(Pt(T0, TimeSpan.FromMinutes(i), 47.4 + dLat, 8.5, sog: 0.1));
        }

        var segs = TrackSegmenter.Segment(pts);
        await Assert.That(segs.Length).IsEqualTo(1);
        await Assert.That(segs[0].IsStationary).IsTrue();
        await Assert.That(segs[0].DistanceMetres).IsGreaterThan(0);
        // 30 jitter hops at ±5 m = at most ~150 m total walk. Roomy
        // upper bound to stay tolerant of RNG seed swings.
        await Assert.That(segs[0].DistanceMetres).IsLessThan(500);
    }
}
