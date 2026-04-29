namespace OnaPlotter.Models;

/// <summary>
/// Aggregated statistics over a set of <see cref="TrackSegment"/>s for
/// a given date range. Drives the <c>/stats</c> page (totals + per-trip
/// summary) without re-walking the source <see cref="TrackPoint"/>
/// array on every render.
/// <para>
/// Units: SI throughout (seconds + metres + m/s) at this layer; UI
/// formats to nm + kn + h-m via <see cref="Utilities.Format"/> at
/// render time. Same convention as <see cref="TrackSegment"/>.
/// </para>
/// </summary>
/// <param name="From">Start of the analysis window (UTC). Mirrors what
/// the helm asked for, not the timestamp of the first point in the
/// fetched data -- a query that returned no data still renders this
/// so the helm sees what window they queried.</param>
/// <param name="To">End of the analysis window (UTC).</param>
/// <param name="TripCount">Number of moving segments. Stationary
/// segments don't count as trips even though the segmenter emits
/// them; helms ask "how many passages this season?", not "how many
/// dwells did I take?".</param>
/// <param name="TotalDurationSeconds">Wall-clock seconds across the
/// whole window. End - Start; doesn't depend on segment data, but
/// included here so the UI doesn't have to subtract repeatedly.</param>
/// <param name="MovingDurationSeconds">Sum of moving-segment
/// durations. The "h underway" total. Compared to
/// <see cref="StationaryDurationSeconds"/> + sailing-window-length
/// it can sum to less (if there were gaps with no fix).</param>
/// <param name="StationaryDurationSeconds">Sum of stationary-segment
/// durations. The "h at anchor / berth / drifting" total.</param>
/// <param name="TotalDistanceMetres">Sum of moving-segment distances.
/// Stationary segments contribute zero (their distance is GPS-jitter
/// noise, not progress).</param>
/// <param name="MaxTripDistanceMetres">Distance of the longest moving
/// segment. Useful for "best / longest passage this season" framing.
/// Null when no moving segments.</param>
/// <param name="MaxSogMs">Peak SOG observed across all segments
/// (m/s). Null when no SOG samples in the window.</param>
public sealed record StatsTotals(
    DateTime From,
    DateTime To,
    int TripCount,
    double TotalDurationSeconds,
    double MovingDurationSeconds,
    double StationaryDurationSeconds,
    double TotalDistanceMetres,
    double? MaxTripDistanceMetres,
    double? MaxSogMs);
