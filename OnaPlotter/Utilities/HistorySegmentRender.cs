using System.Globalization;
using System.Text.Json.Serialization;
using OnaPlotter.Models;

namespace OnaPlotter.Utilities;

/// <summary>
/// Pure-function helpers used by the History page to turn
/// <see cref="TrackPoint"/>+<see cref="TrackSegment"/> pairs into the
/// per-segment payload <c>historyMapLayer.js::renderSegments</c> draws:
/// coordinate slice, render style, colour and pre-formatted hover tooltip.
/// Every styling decision lives here; the JS side only maps the payload
/// onto Leaflet layers.
/// </summary>
public static class HistorySegmentRender
{
    /// <summary>Solid polyline for a moving trip.</summary>
    public const string StyleMoving = "moving";
    /// <summary>Dashed polyline for a stationary dwell, so the helm
    /// can see where the boat swung at anchor.</summary>
    public const string StyleStationaryLine = "stationaryLine";
    /// <summary>Single dot at the dwell's middle fix; keeps the eye on
    /// the passages instead of the dwell jitter.</summary>
    public const string StyleStationaryDot = "stationaryDot";

    // Colours are CSS colours or "var(--token, fallback)" theme references
    // that historyMapLayer.js resolves against the live :root properties,
    // so a theme flip recolours the track on the next render.

    /// <summary>First trip colour: the live-track token, so a one-trip
    /// view keeps the colour the helm knows from the Map page.</summary>
    public const string MovingBaseColor = "var(--map-own, #ec4899)";
    /// <summary>Neutral token for every stationary dwell.</summary>
    public const string StationaryColor = "var(--map-stationary, #94a3b8)";
    /// <summary>Playback scrub marker.</summary>
    public const string PlaybackColor = "var(--map-playback, #fbbf24)";
    /// <summary>Current boat position marker.</summary>
    public const string BoatColor = "var(--sk-accent, #60a5fa)";

    /// <summary>Moving trips cycle through these hues so consecutive
    /// passages are distinguishable without opening the tooltip.</summary>
    public static IReadOnlyList<string> TripPalette { get; } =
    [
        MovingBaseColor,
        "#a78bfa", "#fbbf24", "#34d399",
        "#60a5fa", "#f87171", "#22d3ee", "#fb923c",
    ];

    /// <summary>Per-segment payload for <c>historyMapLayer.js</c>.
    /// <para><c>Index</c> is the segment's position in the full segments
    /// array, reported back on a map click so the trip-detail panel opens
    /// the right segment even when one-point segments were dropped from
    /// the payload.</para>
    /// <para><c>Coords</c> is <c>[[lat, lon], ...]</c>. <c>Tooltip</c> is
    /// rendered by Leaflet as HTML, so the formatter only emits plain text
    /// plus <c>&lt;br&gt;</c> / <c>&lt;b&gt;</c>.</para>
    /// <para><c>Depths</c> is the parallel per-point depth array (m, null
    /// element when a point had no depth sample) used for the "Depth here"
    /// hover line; null for a segment with no depth at all so the JS skips
    /// the per-point lookup.</para></summary>
    public sealed record SegmentPayload(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("coords")] double[][] Coords,
        [property: JsonPropertyName("style")] string Style,
        [property: JsonPropertyName("color")] string Color,
        [property: JsonPropertyName("tooltip")] string Tooltip,
        [property: JsonPropertyName("depths")] double?[]? Depths);

    /// <summary>Slice each segment's points out of the chronological
    /// <paramref name="points"/> array and pair them with style, colour
    /// and tooltip. Single-pass O(n) walk; the cursor never rewinds
    /// because segments arrive in chronological order from
    /// <c>TrackSegmenter.Segment</c>. Segments with fewer than two points
    /// are dropped (no renderable line, unpredictable bounds).
    /// <para>
    /// <paramref name="tooltipTimeZone"/> defaults to the host's local
    /// timezone (the helm reads the helm clock, not UTC); tests pass
    /// <see cref="TimeZoneInfo.Utc"/> for deterministic output.
    /// </para>
    /// </summary>
    public static List<SegmentPayload> BuildSegmentPayload(
        TrackPoint[] points, TrackSegment[] segments,
        TimeZoneInfo? tooltipTimeZone = null, bool stationaryAsDot = true)
    {
        var tz = tooltipTimeZone ?? TimeZoneInfo.Local;
        var output = new List<SegmentPayload>(segments.Length);
        int cursor = 0;
        int movingCount = 0;
        for (int segIdx = 0; segIdx < segments.Length; segIdx++)
        {
            var s = segments[segIdx];
            while (cursor < points.Length && points[cursor].Timestamp < s.StartUtc) cursor++;
            int start = cursor;
            while (cursor < points.Length && points[cursor].Timestamp <= s.EndUtc) cursor++;
            int end = cursor;     // exclusive
            if (end - start < 2) continue;
            var coords = new double[end - start][];
            double?[]? depths = s.DepthAvgM is null ? null : new double?[end - start];
            for (int i = start; i < end; i++)
            {
                coords[i - start] = [points[i].Latitude, points[i].Longitude];
                if (depths is not null) depths[i - start] = points[i].Depth;
            }
            string style, color;
            if (s.IsStationary)
            {
                style = stationaryAsDot ? StyleStationaryDot : StyleStationaryLine;
                color = StationaryColor;
            }
            else
            {
                // Count only moving segments so the first trip always gets
                // the base colour however many dwells precede it.
                style = StyleMoving;
                color = TripPalette[movingCount % TripPalette.Count];
                movingCount++;
            }
            output.Add(new SegmentPayload(
                Index: segIdx,
                Coords: coords,
                Style: style,
                Color: color,
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
