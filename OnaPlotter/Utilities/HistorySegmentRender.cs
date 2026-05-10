using System.Globalization;
using OnaPlotter.Models;

namespace OnaPlotter.Utilities;

/// <summary>
/// Pure-function helpers used by the History page to turn
/// <see cref="TrackPoint"/>+<see cref="TrackSegment"/> pairs into the
/// per-segment payload the JS Leaflet renderer consumes (per-segment
/// coordinate slice + pre-formatted hover tooltip).
/// <para>
/// Extracted from the Razor page so tests can exercise the formatter
/// directly. The page itself just calls
/// <see cref="BuildSegmentPayload"/> and hands the result to JSON
/// serialisation; no logic remains in the .razor file beyond the JS
/// interop.
/// </para>
/// </summary>
public static class HistorySegmentRender
{
    /// <summary>Per-segment payload shape: <c>Coords</c> is
    /// <c>[[lat, lon], ...]</c> for the polyline, <c>IsStationary</c>
    /// drives the styling (dimmed dashed grey vs solid magenta), and
    /// <c>Tooltip</c> is the pre-formatted hover string. Leaflet
    /// renders the tooltip as HTML so the formatter only emits plain
    /// text + <c>&lt;br&gt;</c>; no quotes that need escaping.
    /// <para><c>Depths</c> is the parallel per-point depth array (m,
    /// nullable element when a TrackPoint had no depth sample). JS-
    /// side mousemove handlers index into this with the nearest-
    /// coordinate index to render "Depth here: X m" on hover; a
    /// segment whose underlying samples carried no depth at all gets
    /// a null array so JS can skip the per-point lookup entirely.</para></summary>
    public sealed record SegmentPayload(
        double[][] Coords,
        bool IsStationary,
        string Tooltip,
        double?[]? Depths);

    /// <summary>Slice each segment's points out of the chronological
    /// <paramref name="points"/> array and pair them with the
    /// matching <see cref="TrackSegment"/>'s formatted tooltip.
    /// Single-pass O(n) walk; cursor never rewinds because segments
    /// arrive in chronological order from
    /// <c>TrackSegmenter.Segment</c>. Single-point segments are
    /// dropped (the segmenter merges them but a guard here keeps
    /// the bounds calculation in JS predictable).
    /// <para>
    /// <paramref name="tooltipTimeZone"/> defaults to the host's
    /// local timezone (helm reads the helm clock, not UTC). Tests
    /// pass <see cref="TimeZoneInfo.Utc"/> for deterministic output.
    /// </para>
    /// </summary>
    public static List<SegmentPayload> BuildSegmentPayload(
        TrackPoint[] points, TrackSegment[] segments,
        TimeZoneInfo? tooltipTimeZone = null)
    {
        var tz = tooltipTimeZone ?? TimeZoneInfo.Local;
        var output = new List<SegmentPayload>(segments.Length);
        int cursor = 0;
        foreach (var s in segments)
        {
            while (cursor < points.Length && points[cursor].Timestamp < s.StartUtc) cursor++;
            int start = cursor;
            while (cursor < points.Length && points[cursor].Timestamp <= s.EndUtc) cursor++;
            int end = cursor;     // exclusive
            if (end - start < 2) continue;
            var coords = new double[end - start][];
            // Per-point depths are emitted only when the segment has
            // at least one depth sample - a null array signals "no
            // depth on this trip" to the JS mousemove handler so it
            // can skip the per-point lookup. Saves both the bridge
            // crossing and the per-tick math on no-transducer boats.
            double?[]? depths = s.DepthAvgM is null ? null : new double?[end - start];
            for (int i = start; i < end; i++)
            {
                coords[i - start] = [points[i].Latitude, points[i].Longitude];
                if (depths is not null) depths[i - start] = points[i].Depth;
            }
            output.Add(new SegmentPayload(
                Coords: coords,
                IsStationary: s.IsStationary,
                Tooltip: BuildTooltip(s, tz),
                Depths: depths));
        }
        return output;
    }

    /// <summary>Format a segment's stats for the Leaflet hover
    /// tooltip. Stationary segments lead with the dwell duration;
    /// moving segments lead with distance because that's what the
    /// helm wants to see first for a passage segment. SOG / TWS
    /// rows are omitted when the underlying samples were absent -
    /// rendering "0.0 avg / 0.0 max / 0.0 min kn" on a no-SOG track
    /// would mislead. Times rendered in
    /// <paramref name="tooltipTimeZone"/> (defaults to
    /// <see cref="TimeZoneInfo.Local"/>); tests inject
    /// <see cref="TimeZoneInfo.Utc"/> so the assertion doesn't drift
    /// per CI runner zone.</summary>
    public static string BuildTooltip(TrackSegment s, TimeZoneInfo? tooltipTimeZone = null)
    {
        var tz = tooltipTimeZone ?? TimeZoneInfo.Local;
        // ConvertTimeFromUtc requires a UTC-kind input; segments
        // promise UTC by construction (TrackPoint.Timestamp is parsed
        // AdjustToUniversal in TrackApi).
        var startLocal = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(s.StartUtc, DateTimeKind.Utc), tz);
        var endLocal = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(s.EndUtc, DateTimeKind.Utc), tz);
        string when = $"{startLocal:yyyy-MM-dd HH:mm} → {endLocal:HH:mm}";
        string duration = Format.TimeToGo(s.Duration.TotalSeconds);
        if (s.IsStationary)
        {
            return $"<b>Stationary</b> · {duration}<br>{when}<br>Pts: "
                + s.PointCount.ToString(CultureInfo.InvariantCulture);
        }
        string nm = Format.Nm(s.DistanceMetres);
        string sogLine = s.SogAvgMs is null
            ? string.Empty
            : $"<br>SOG: {Format.Speed(s.SogAvgMs)} avg / {Format.Speed(s.SogMaxMs)} max / {Format.Speed(s.SogMinMs)} min kn";
        string windLine = s.WindSpeedAvgMs is null
            ? string.Empty
            : $"<br>TWS avg: {Format.Speed(s.WindSpeedAvgMs)} kn";
        // Depth aggregates: helm reads "did this leg ever shoal?"
        // off this single line. Min before max so the leg's
        // shallowest moment (closest to grounding) is the eye-catch
        // - what the helm cares about most when planning a return
        // trip over the same route.
        string depthLine = s.DepthAvgM is null
            ? string.Empty
            : $"<br>Depth: {Format.Depth(s.DepthAvgM)} avg / {Format.Depth(s.DepthMinM)} min / {Format.Depth(s.DepthMaxM)} max m";
        return $"<b>Moving</b> · {duration} · {nm} nm<br>{when}{sogLine}{windLine}{depthLine}";
    }
}
