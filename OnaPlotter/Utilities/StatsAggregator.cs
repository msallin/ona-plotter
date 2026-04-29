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

        return new StatsTotals(
            From: from,
            To: to,
            TripCount: tripCount,
            TotalDurationSeconds: totalSec,
            MovingDurationSeconds: movingSec,
            StationaryDurationSeconds: stationarySec,
            TotalDistanceMetres: totalDist,
            MaxTripDistanceMetres: maxTripDist,
            MaxSogMs: maxSog);
    }
}
