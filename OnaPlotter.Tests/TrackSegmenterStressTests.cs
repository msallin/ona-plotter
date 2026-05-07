using System.Diagnostics;
using OnaPlotter.Models;
using OnaPlotter.Services;

namespace OnaPlotter.Tests;

/// <summary>
/// Performance-and-correctness stress tests for <see cref="TrackSegmenter"/>.
/// History pages can load 10s of thousands of points after a multi-day
/// fetch; the segmenter must:
///   1. produce a sensible segmentation (every input point covered, no
///      overlap between segments, durations meet the merge floor),
///   2. complete in reasonable wall-clock time on a typical dev box.
///
/// <para>The wall-clock thresholds here are deliberately generous (5x
/// what the implementation needs locally) so they don't flake on slow
/// CI runners. They catch order-of-magnitude regressions, not micro-
/// optimisation drift.</para>
/// </summary>
public class TrackSegmenterStressTests
{
    [Test]
    public async Task Segment_50kPoints_CoversEveryPointAndCompletesUnderBudget()
    {
        // 50k points at 1 sample / 2 s = ~28 hours of cruising data.
        // A long passage that the History page is plausibly asked to
        // render. A regression that pushes this to O(n^2) would freeze
        // the page; the budget here catches that without false-flagging
        // typical micro-tweaks.
        var points = SyntheticTrack(50_000, seed: 12345);

        var sw = Stopwatch.StartNew();
        var segments = TrackSegmenter.Segment(points);
        sw.Stop();

        // 1-second budget. Local dev runs in ~150-200 ms; this is 5x
        // slack - still tight enough to catch a 5-10x regression on
        // a slow CI runner without flaking on noise.
        await Assert.That(sw.Elapsed.TotalSeconds)
            .IsLessThan(1.0)
            .Because($"50k points should segment under 1s; took {sw.ElapsedMilliseconds}ms");

        // Coverage: every input point sits inside exactly one output
        // segment (or in the merge-prefix that gets dropped, which
        // shouldn't happen because we generate clean dwell+passage
        // patterns longer than the 2-min merge floor). Sum of
        // PointCount across segments should equal total input.
        int totalPoints = 0;
        for (int i = 0; i < segments.Length; i++) totalPoints += segments[i].PointCount;
        await Assert.That(totalPoints)
            .IsEqualTo(points.Count)
            .Because("every input point should land in some segment");

        // No two segments overlap in time. Each segment's start strictly
        // succeeds the previous segment's end.
        for (int i = 1; i < segments.Length; i++)
        {
            await Assert.That(segments[i].StartUtc)
                .IsGreaterThanOrEqualTo(segments[i - 1].EndUtc);
        }
    }

    [Test]
    public async Task Segment_AllDwell_ProducesSingleStationarySegment()
    {
        // 5000 points all at slow speed: should fold into one segment.
        // Catches a bug where the segmenter fragments a dwell into
        // many tiny segments because of jitter near the threshold.
        var points = new List<TrackPoint>(5000);
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var rng = new Random(7777);
        for (int i = 0; i < 5000; i++)
        {
            // SOG 0.1..0.2 m/s (well under MovingThresholdMs = 0.257).
            double sog = 0.1 + rng.NextDouble() * 0.1;
            points.Add(new TrackPoint(
                t0.AddSeconds(i * 2),
                Latitude: 47.4 + rng.NextDouble() * 0.0001,
                Longitude: 8.5 + rng.NextDouble() * 0.0001,
                SpeedOverGround: sog,
                CourseOverGround: null, Heading: null,
                WindAngleApparent: null, WindSpeedApparent: null,
                WindAngleTrue: null, WindSpeedTrue: null));
        }
        var segs = TrackSegmenter.Segment(points);
        // Should be exactly one (or maybe two if the merge boundary
        // catches the head). All segments should be IsStationary.
        await Assert.That(segs.Length).IsLessThanOrEqualTo(2);
        for (int i = 0; i < segs.Length; i++)
            await Assert.That(segs[i].IsStationary).IsTrue();
    }

    [Test]
    public async Task Segment_PurePassage_ProducesSingleMovingSegment()
    {
        // 5000 points all at 5 m/s: one moving segment.
        var points = new List<TrackPoint>(5000);
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var rng = new Random(8888);
        double lat = 47.4, lon = 8.5;
        for (int i = 0; i < 5000; i++)
        {
            // Move ~5 m / sample (= ~5 m/s SOG given 1 s spacing).
            lat += 0.00005 + rng.NextDouble() * 0.00001;
            lon += 0.00005 + rng.NextDouble() * 0.00001;
            points.Add(new TrackPoint(
                t0.AddSeconds(i),
                Latitude: lat, Longitude: lon,
                SpeedOverGround: 5.0,
                CourseOverGround: null, Heading: null,
                WindAngleApparent: null, WindSpeedApparent: null,
                WindAngleTrue: null, WindSpeedTrue: null));
        }
        var segs = TrackSegmenter.Segment(points);
        await Assert.That(segs.Length).IsLessThanOrEqualTo(2);
        for (int i = 0; i < segs.Length; i++)
            await Assert.That(segs[i].IsStationary).IsFalse();
    }

    /// <summary>
    /// Generate a synthetic track that alternates between dwell (anchor /
    /// harbour) and passage (cruising) phases. Each phase is at least
    /// 30 minutes so both pass the merge floor cleanly.
    /// </summary>
    private static List<TrackPoint> SyntheticTrack(int count, int seed)
    {
        var rng = new Random(seed);
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var points = new List<TrackPoint>(count);
        double lat = 47.4, lon = 8.5;
        // Alternate phase every ~1800 s of timeline.
        bool moving = false;
        int phaseSamples = 1500;
        int phaseCount = 0;

        for (int i = 0; i < count; i++)
        {
            t = t.AddSeconds(2);          // 1 sample / 2 s
            double sog;
            if (moving)
            {
                sog = 4.0 + rng.NextDouble() * 3.0;       // 4..7 m/s
                lat += sog * 1e-5 * Math.Cos(0.7);
                lon += sog * 1e-5 * Math.Sin(0.7);
            }
            else
            {
                sog = 0.05 + rng.NextDouble() * 0.15;     // 0.05..0.2 m/s
                lat += (rng.NextDouble() - 0.5) * 1e-6;
                lon += (rng.NextDouble() - 0.5) * 1e-6;
            }

            points.Add(new TrackPoint(
                t, lat, lon,
                SpeedOverGround: sog,
                CourseOverGround: null, Heading: null,
                WindAngleApparent: null, WindSpeedApparent: null,
                WindAngleTrue: null, WindSpeedTrue: null));

            phaseCount++;
            if (phaseCount >= phaseSamples)
            {
                moving = !moving;
                phaseCount = 0;
            }
        }
        return points;
    }
}
