namespace OnaPlotter.Models;

/// <summary>
/// One row in the per-day breakdown that the <c>/stats</c> page
/// optionally renders beneath the totals. Same fields as the page-
/// level <see cref="StatsTotals"/>, scoped to a single calendar day
/// in the helm's local timezone.
/// <para>
/// Why local-day rather than UTC: the helm reads "Saturday April 19"
/// and expects the row to cover the activities they did on that day
/// of their wall clock, not a UTC-midnight bucket that splits an
/// evening passage across two rows. Aggregator converts segment
/// timestamps to local before binning.
/// </para>
/// </summary>
/// <param name="LocalDate">The calendar date in the helm's chosen
/// timezone (UTC midnight of that local day, kind=Unspecified so the
/// UI renders without re-converting).</param>
/// <param name="TripCount">Moving segments whose START fell on this
/// day. A segment crossing midnight is attributed to the start day
/// only; clipping by date would need a uniform-speed assumption
/// inside the segment, deferred until a real case proves it matters.</param>
/// <param name="MovingDurationSeconds">Sum of moving-segment
/// durations attributed to this day.</param>
/// <param name="StationaryDurationSeconds">Sum of stationary-segment
/// durations attributed to this day.</param>
/// <param name="DistanceMetres">Sum of moving-segment distances on
/// this day. Stationary distance excluded - same discipline as
/// <see cref="StatsTotals.TotalDistanceMetres"/>.</param>
/// <param name="MaxSogMs">Peak SOG observed on this day. Considers
/// all segments (a momentary surge inside a stationary "ferry-wash"
/// segment is still a real reading), matching the page-level
/// MaxSogMs definition.</param>
/// <param name="AvgSogMs">Distance over moving time for this day.
/// Null when no moving time on the day.</param>
public sealed record DailyStats(
    DateTime LocalDate,
    int TripCount,
    double MovingDurationSeconds,
    double StationaryDurationSeconds,
    double DistanceMetres,
    double? MaxSogMs,
    double? AvgSogMs);
