using OnaPlotter.Models;
using OnaPlotter.Utilities;

namespace OnaPlotter.Services;

/// <summary>
/// Splits a chronological <see cref="TrackPoint"/> array into
/// "stationary" and "moving" segments. Used by the History page to
/// render dwell vs passage distinctly, by the future stats page for
/// totals over a date range, and by the table view for trip listing.
/// <para>
/// Algorithm:
/// </para>
/// <list type="number">
///   <item>For each sample, classify as <c>moving</c> if its SOG (or
///   the inter-sample speed when SOG is missing) exceeds
///   <see cref="MovingThresholdMs"/>; otherwise <c>stationary</c>.</item>
///   <item>Run a debounce: a state flip requires
///   <see cref="DebounceWindow"/> of consistent classification before
///   we commit, so a single slow tack doesn't fragment a passage and
///   a momentary GPS fix glitch doesn't manufacture a "trip".</item>
///   <item>Within each committed run of same-classification samples,
///   compute distance + SOG / TWS aggregates and emit a
///   <see cref="TrackSegment"/>.</item>
/// </list>
/// <para>
/// Pure function: deterministic given the same inputs and constants.
/// No DI, no clock, no IO -- the segmenter takes points in and
/// segments out. Tests drive it directly.
/// </para>
/// </summary>
public static class TrackSegmenter
{
    /// <summary>SOG threshold separating "stationary" from "moving".
    /// 0.5 kn ≈ 0.257 m/s. Above this the boat has obvious way on
    /// (tide / current bias rarely exceed 0.3 kn in cruising areas);
    /// below it the GPS jitter is roughly the same magnitude as the
    /// motion, so any "track" you draw is noise.</summary>
    public const double MovingThresholdMs = 0.257;

    /// <summary>Minimum dwell time on a new state before we commit
    /// to it. Three minutes captures a tack or a brief drift without
    /// turning either into a fake "stationary" / "moving" segment;
    /// shorter (e.g. 30 s) fragments passage tracks unhelpfully.</summary>
    public static readonly TimeSpan DebounceWindow = TimeSpan.FromMinutes(3);

    /// <summary>Floor on segment duration. Anything shorter is folded
    /// into the adjacent segment so the table view doesn't list a
    /// 14-second "moving" trip the helm caused by stepping on the
    /// throttle while berthing. Different from the debounce above:
    /// debounce is "how long do we wait before flipping state",
    /// merge-floor is "drop sub-trivial segments after the fact".</summary>
    public static readonly TimeSpan MinSegmentDuration = TimeSpan.FromMinutes(2);

    /// <summary>Run the segmenter on a chronologically-sorted point
    /// array. Returns an empty array when the input has fewer than
    /// two points (no segment can be defined from a single fix). The
    /// caller is responsible for ordering: this method does NOT
    /// re-sort, because the SignalK History API already returns
    /// time-ordered samples and re-sorting an already-sorted array
    /// is wasted work on the WASM hot path.</summary>
    public static TrackSegment[] Segment(IReadOnlyList<TrackPoint> points)
    {
        if (points is null || points.Count < 2) return [];

        // Pass 1: classify each point. Use SOG when the sample carries
        // it; fall back to inter-sample speed (haversine / dt) when
        // SOG is absent, which can happen on AIS-only feeds or when
        // the SignalK History API was queried for position only.
        var classifications = new bool[points.Count];   // true = moving
        for (int i = 0; i < points.Count; i++)
        {
            classifications[i] = ClassifyPoint(points, i);
        }

        // Pass 2: state machine. Walk forward, accumulating same-
        // classification points into a candidate run. When the
        // classification changes, only commit the new state if the
        // change persists for DebounceWindow; otherwise the candidate
        // gets folded back into the prior run.
        var rawSegments = BuildRawSegments(points, classifications);

        // Pass 3: absorb sub-MinSegmentDuration segments into the
        // adjacent run. The merge needs the points array (to read
        // segment timestamps) -- we previously called a pure
        // index-only helper which only caught one-point segments and
        // missed sub-2-minute multi-point ones (e.g. a 90 s moving
        // blip with 4 SOG-elevated samples -- exactly the "14-second
        // moving trip the helm caused by stepping on the throttle
        // while berthing" case the constant comment warns about).
        var merged = MergeShortSegments(rawSegments, points);

        // Pass 4: materialise stats for each surviving segment.
        var output = new TrackSegment[merged.Count];
        for (int s = 0; s < merged.Count; s++)
        {
            var (startIdx, endIdx, isStat) = merged[s];
            output[s] = BuildSegment(points, startIdx, endIdx, isStat);
        }
        return output;
    }

    private static bool ClassifyPoint(IReadOnlyList<TrackPoint> points, int i)
    {
        var p = points[i];
        if (p.SpeedOverGround is double sog) return sog > MovingThresholdMs;
        // SOG missing: estimate inter-sample speed from a neighbour
        // with a strictly different timestamp. SignalK providers that
        // batch several paths into one delta can publish two adjacent
        // points with identical Timestamps; using a 0-dt neighbour
        // makes ClassifyPoint return 'stationary' on the head of an
        // otherwise-moving run, which the merge floor then preserves
        // as a degenerate stationary head segment that didn't happen.
        // Walk the obvious direction first (forward for i==0,
        // backward otherwise) and step further if dt collapses.
        TrackPoint? other = null;
        if (i == 0)
        {
            for (int j = 1; j < points.Count; j++)
            {
                if (points[j].Timestamp != p.Timestamp) { other = points[j]; break; }
            }
        }
        else
        {
            for (int j = i - 1; j >= 0; j--)
            {
                if (points[j].Timestamp != p.Timestamp) { other = points[j]; break; }
            }
            // No earlier point with a different timestamp -- look
            // forward instead.
            if (other is null)
            {
                for (int j = i + 1; j < points.Count; j++)
                {
                    if (points[j].Timestamp != p.Timestamp) { other = points[j]; break; }
                }
            }
        }
        if (other is null) return false;
        var dt = Math.Abs((p.Timestamp - other.Timestamp).TotalSeconds);
        if (dt <= 0) return false;     // belt-and-braces; the loop already filters
        var dm = RouteProgress.HaversineMeters(
            other.Latitude, other.Longitude, p.Latitude, p.Longitude);
        return (dm / dt) > MovingThresholdMs;
    }

    /// <summary>Segment indices: (startInclusive, endInclusive, isStationary).</summary>
    private static List<(int start, int end, bool isStationary)> BuildRawSegments(
        IReadOnlyList<TrackPoint> points, bool[] moving)
    {
        var segments = new List<(int, int, bool)>();
        int runStart = 0;
        bool runState = !moving[0];     // !moving == stationary
        int candStart = -1;
        bool candState = false;

        for (int i = 1; i < points.Count; i++)
        {
            bool currentState = !moving[i];
            if (currentState == runState)
            {
                // Same classification as the committed run. If a
                // candidate had started, abandon it -- the state
                // didn't persist long enough.
                candStart = -1;
                continue;
            }
            // State differs from the committed run. Either we're
            // already tracking a candidate flip, or we just spotted one.
            if (candStart < 0)
            {
                candStart = i;
                candState = currentState;
                continue;
            }
            // We're still tracking a candidate of the same flipped
            // state. Has it persisted long enough to commit?
            var elapsed = points[i].Timestamp - points[candStart].Timestamp;
            if (elapsed >= DebounceWindow)
            {
                // Commit the prior run up to candStart-1, then start a
                // new run from candStart in the new state.
                segments.Add((runStart, candStart - 1, runState));
                runStart = candStart;
                runState = candState;
                candStart = -1;
            }
            // Else: keep accumulating the candidate; don't commit yet.
        }

        // Tail: emit whatever's left, including a still-candidate state
        // change if it actually consumed enough wall-clock time. If a
        // tail-candidate didn't make it past debounce, the prior run
        // absorbs it.
        int tailEnd = points.Count - 1;
        if (candStart > 0)
        {
            var tailElapsed = points[tailEnd].Timestamp - points[candStart].Timestamp;
            if (tailElapsed >= DebounceWindow)
            {
                segments.Add((runStart, candStart - 1, runState));
                segments.Add((candStart, tailEnd, candState));
                return segments;
            }
        }
        segments.Add((runStart, tailEnd, runState));
        return segments;
    }

    private static List<(int start, int end, bool isStationary)> MergeShortSegments(
        List<(int start, int end, bool isStationary)> segments,
        IReadOnlyList<TrackPoint> points)
    {
        // Single-pass left-to-right merge. Two flavours of "too
        // short" both get absorbed into the left neighbour:
        //
        //   1. One-point segments. These are emitted at the head
        //      when the very first sample's classification differs
        //      from the run that follows; they're degenerate by
        //      construction.
        //
        //   2. Sub-MinSegmentDuration segments. A 90-second moving
        //      blip with four SOG-elevated samples (e.g. the helm
        //      stepping on the throttle while berthing, then easing
        //      back) survives the debounce because the candidate
        //      ran past the 3-min window before reverting --
        //      MERGE catches it.
        //
        // The first segment is exempt from absorption: it has no
        // left neighbour. A short HEAD segment instead absorbs the
        // SECOND segment (if needed) into itself -- the head's
        // classification wins because the head sample is what we
        // start with.
        if (segments.Count <= 1) return segments;
        var output = new List<(int start, int end, bool isStationary)> { segments[0] };
        for (int i = 1; i < segments.Count; i++)
        {
            var current = segments[i];
            var prev = output[^1];
            bool tooShort = IsTooShort(current, points);
            if (tooShort)
            {
                // Absorb into prev, keeping prev's classification.
                output[^1] = (prev.start, current.end, prev.isStationary);
                continue;
            }
            output.Add(current);
        }
        return output;
    }

    private static bool IsTooShort(
        (int start, int end, bool isStationary) seg, IReadOnlyList<TrackPoint> points)
    {
        int span = seg.end - seg.start + 1;
        if (span < 2) return true;
        var duration = points[seg.end].Timestamp - points[seg.start].Timestamp;
        return duration < MinSegmentDuration;
    }

    private static TrackSegment BuildSegment(
        IReadOnlyList<TrackPoint> points, int startIdx, int endIdx, bool isStationary)
    {
        var first = points[startIdx];
        var last = points[endIdx];

        // Distance: sum haversine across consecutive points within the
        // segment. For a stationary segment this captures GPS jitter
        // accumulation; for a passage segment this is the actual
        // distance travelled (close to great-circle for short hops,
        // close to rhumb-line in aggregate for ocean passages).
        double distance = 0.0;
        for (int i = startIdx + 1; i <= endIdx; i++)
        {
            distance += RouteProgress.HaversineMeters(
                points[i - 1].Latitude, points[i - 1].Longitude,
                points[i].Latitude, points[i].Longitude);
        }

        // Aggregate SOG + TWS over samples that carry the value. A
        // null aggregate means "no samples in this segment had it" --
        // the UI renders that as "—" rather than "0".
        double sogSum = 0; int sogCount = 0;
        double sogMax = double.MinValue, sogMin = double.MaxValue;
        double twsSum = 0; int twsCount = 0;
        for (int i = startIdx; i <= endIdx; i++)
        {
            if (points[i].SpeedOverGround is double sog)
            {
                sogSum += sog;
                sogCount++;
                if (sog > sogMax) sogMax = sog;
                if (sog < sogMin) sogMin = sog;
            }
            if (points[i].WindSpeedTrue is double tws)
            {
                twsSum += tws;
                twsCount++;
            }
        }

        double? sogAvg = sogCount > 0 ? sogSum / sogCount : null;
        double? sogHi = sogCount > 0 ? sogMax : null;
        double? sogLo = sogCount > 0 ? sogMin : null;
        double? twsAvg = twsCount > 0 ? twsSum / twsCount : null;

        return new TrackSegment(
            StartUtc: first.Timestamp,
            EndUtc: last.Timestamp,
            StartLat: first.Latitude,
            StartLon: first.Longitude,
            EndLat: last.Latitude,
            EndLon: last.Longitude,
            DistanceMetres: distance,
            SogAvgMs: sogAvg,
            SogMaxMs: sogHi,
            SogMinMs: sogLo,
            WindSpeedAvgMs: twsAvg,
            IsStationary: isStationary,
            PointCount: endIdx - startIdx + 1);
    }
}
