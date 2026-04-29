namespace OnaPlotter.Models;

/// <summary>
/// One segment of a recorded track, classified as either "stationary"
/// (boat sat at anchor / berth) or "moving" (passage). Produced by
/// <c>TrackSegmenter</c> from a chronological <see cref="TrackPoint"/>
/// array; consumed by the History page (per-segment styling + hover
/// stats), the future Stats page (totals over a date range), and the
/// table view.
/// <para>
/// Units: SI throughout. Distance in metres, speed in m/s, wind in
/// m/s. Conversion to nautical miles + knots happens at the UI layer
/// via <c>OnaPlotter.Utilities.Format</c>.
/// </para>
/// </summary>
/// <param name="StartUtc">Timestamp of the first sample in the segment.</param>
/// <param name="EndUtc">Timestamp of the last sample in the segment.</param>
/// <param name="StartLat">First-sample latitude (deg). Useful for the
/// table view's "from" cell + map fly-to.</param>
/// <param name="StartLon">First-sample longitude (deg).</param>
/// <param name="EndLat">Last-sample latitude (deg).</param>
/// <param name="EndLon">Last-sample longitude (deg).</param>
/// <param name="DistanceMetres">Sum of haversine distance between
/// consecutive samples. For a stationary segment this is the GPS
/// jitter accumulation -- usually under a few metres but a noisy fix
/// can push it to tens.</param>
/// <param name="SogAvgMs">Mean SOG over samples whose SOG was present.
/// Null when no SOG samples landed in the segment (e.g. AIS-feed-only
/// position with no speed plumbing).</param>
/// <param name="SogMaxMs">Maximum SOG sample. Null for no-SOG segments.</param>
/// <param name="SogMinMs">Minimum SOG sample. Null for no-SOG segments.
/// For a moving segment this is typically the boat coming off the dock
/// or rounding up to tack; useful for confirming the segment isn't
/// hiding a long stationary gap inside it.</param>
/// <param name="WindSpeedAvgMs">Mean true wind speed over samples
/// whose TWS was present. Null when no TWS samples landed.</param>
/// <param name="IsStationary">Classification: true = boat dwelled
/// (anchor / berth / drifting under threshold); false = moving (under
/// way). Drives the History-page colouring and the table-view filter.</param>
/// <param name="PointCount">Number of underlying samples. Mostly for
/// debugging: a segment with one point is suspect.</param>
public sealed record TrackSegment(
    DateTime StartUtc,
    DateTime EndUtc,
    double StartLat,
    double StartLon,
    double EndLat,
    double EndLon,
    double DistanceMetres,
    double? SogAvgMs,
    double? SogMaxMs,
    double? SogMinMs,
    double? WindSpeedAvgMs,
    bool IsStationary,
    int PointCount)
{
    /// <summary>Wall-clock duration. Computed; kept off the parameter
    /// list because <c>StartUtc</c> + <c>EndUtc</c> already carry it.</summary>
    public TimeSpan Duration => EndUtc - StartUtc;
}
