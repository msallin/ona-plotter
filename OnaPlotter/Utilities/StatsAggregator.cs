using OnaPlotter.Models;

namespace OnaPlotter.Utilities;

/// <summary>
/// Aggregates an array of <see cref="TrackSegment"/>s into a single
/// <see cref="StatsTotals"/> record for the <c>/stats</c> page. Pure
/// function: no clock, no IO, deterministic given the same inputs.
/// <para>
/// Stats discipline: only MOVING segments contribute to distance and
/// trip counts. A stationary segment's distance field captures GPS-
/// jitter accumulation and would inflate the helm's "this season's
/// nautical miles" total; a stationary segment is also not a "trip"
/// in helm-speak ("how many passages did I take?"). Stationary time
/// is summed separately so the helm can read it as
/// "time at anchor / berth".
/// </para>
/// </summary>
public static class StatsAggregator
{
    /// <summary>Aggregates the supplied segments into a totals record
    /// for the supplied analysis window. The window
    /// <paramref name="from"/> / <paramref name="to"/> is what the
    /// helm queried (echoed back into the result so the UI can render
    /// it without separately tracking it). The segments are typically
    /// the output of <see cref="Services.TrackSegmenter.Segment"/> on
    /// points fetched for that same window.</summary>
    public static StatsTotals Aggregate(
        DateTime from, DateTime to, IReadOnlyList<TrackSegment> segments)
    {
        double movingSec = 0;
        double stationarySec = 0;
        double totalDist = 0;
        int tripCount = 0;
        double? maxTripDist = null;
        double? maxSog = null;

        foreach (var s in segments)
        {
            if (s.IsStationary)
            {
                stationarySec += s.Duration.TotalSeconds;
            }
            else
            {
                tripCount++;
                movingSec += s.Duration.TotalSeconds;
                totalDist += s.DistanceMetres;
                if (maxTripDist is null || s.DistanceMetres > maxTripDist.Value)
                {
                    maxTripDist = s.DistanceMetres;
                }
            }
            // Peak SOG considers all segments -- a momentary surge
            // recorded inside a stationary "ferry-wash bobbing"
            // segment is still a real observed speed, useful for the
            // "max boat speed this season" line.
            if (s.SogMaxMs is double sogMax
                && (maxSog is null || sogMax > maxSog.Value))
            {
                maxSog = sogMax;
            }
        }

        // Total wall-clock duration is the helm's QUERY window, not
        // the bounds of returned data. A query for "1 May - 7 May"
        // returns 7 days of total even when only Tuesday had data.
        double totalSec = (to - from).TotalSeconds;
        if (totalSec < 0) totalSec = 0;

        // Avg underway speed = total moving distance / total moving
        // time. Null when there's no underway time -- avg-of-zero is
        // undefined, not 0; rendering "0.0 kn" would mislead the
        // helm into thinking they crawled along at zero knots.
        double? avgSogMs = movingSec > 0 ? totalDist / movingSec : (double?)null;

        // Best rolling 24-hour run. Sliding window over the moving
        // segments sorted by start time -- segments arrive in
        // chronological order from TrackSegmenter, so no resort.
        double? best24h = ComputeBest24hMetres(segments);

        return new StatsTotals(
            From: from,
            To: to,
            TripCount: tripCount,
            TotalDurationSeconds: totalSec,
            MovingDurationSeconds: movingSec,
            StationaryDurationSeconds: stationarySec,
            TotalDistanceMetres: totalDist,
            MaxTripDistanceMetres: maxTripDist,
            MaxSogMs: maxSog,
            AvgSogMs: avgSogMs,
            Best24hMetres: best24h);
    }

    /// <summary>Rolling 24-hour window with the most underway distance.
    /// Sliding-window over moving segments by start time: O(n).
    /// <para>
    /// A segment is counted in full when its start falls inside the
    /// window. A segment longer than 24 h would over-count (its full
    /// distance attributed to a 24 h window) but those are rare --
    /// even a Pacific crossing rarely produces a single un-split
    /// 24 h+ moving segment because the segmenter splits on dwell
    /// gaps every few hours. Adding clip-by-window logic would need
    /// a uniform-speed assumption inside the segment; pragmatic call
    /// is to keep the simple version + comment until a real case
    /// proves the over-count is a problem.
    /// </para>
    /// <para>
    /// Returns null when there are no moving segments. Returns the
    /// max-window sum even when only one moving segment exists (i.e.,
    /// "best 24 h" of a single trip = that trip's distance).
    /// </para>
    /// </summary>
    private static double? ComputeBest24hMetres(IReadOnlyList<TrackSegment> segments)
    {
        // Materialise just the moving segments so the sliding pointer
        // doesn't have to skip stationaries on every advance.
        var moving = new List<TrackSegment>(segments.Count);
        foreach (var s in segments)
            if (!s.IsStationary) moving.Add(s);

        if (moving.Count == 0) return null;

        var window = TimeSpan.FromHours(24);
        double sum = 0;
        double best = 0;
        int left = 0;
        for (int right = 0; right < moving.Count; right++)
        {
            sum += moving[right].DistanceMetres;
            // Advance left until the window invariant holds:
            // moving[right].StartUtc - moving[left].StartUtc <= 24 h.
            while (left < right
                && moving[right].StartUtc - moving[left].StartUtc > window)
            {
                sum -= moving[left].DistanceMetres;
                left++;
            }
            if (sum > best) best = sum;
        }
        return best;
    }
}
