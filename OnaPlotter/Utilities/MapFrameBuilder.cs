using OnaPlotter.Models;

namespace OnaPlotter.Utilities;

/// <summary>
/// Builds the per-tick frame payload for the JS <c>applyFrame</c>
/// interop call. Extracted from Map.razor's <c>HandleDataChanged</c>
/// so the decision tree (position present, course cleared this tick,
/// current-arrow cutoff, layline throttle) is unit-testable without
/// a JS runtime.
/// <para>
/// State is encapsulated instead of passed around because several
/// fields are inter-tick:
///   <list type="bullet">
///     <item><c>PrevLat</c> / <c>PrevLon</c> feed the track-segment
///           shape - each emitted segment is from last-tick to this-
///           tick position.</item>
///     <item><c>CourseLineDrawn</c> latches so a route teardown emits
///           a single clear-course frame rather than spamming them.</item>
///     <item><c>LaylinesSkip</c> counts ticks toward the next push,
///           matching the "every Nth fix" throttle.</item>
///   </list>
/// </para>
/// <para>
/// Map.razor holds one instance per page lifetime; <c>Dispose()</c>
/// isn't needed since the class has no unmanaged resources.
/// </para>
/// </summary>
public sealed class MapFrameBuilder
{
    // Below this drift (m/s) the tidal current arrow is suppressed
    // (~0.1 kn). Matches the original Map.razor constant so extracting
    // the logic doesn't silently re-tune the threshold.
    public double MinCurrentDriftMs { get; init; } = 0.05;

    // Throttle: only include laylines in every Nth frame. The JS
    // draw involves vector math + SVG mutations that are measurable
    // on weak clients. 5 matches Map.razor's original cadence.
    public int LaylinePushEveryNFixes { get; init; } = 5;

    // Track-segment emission cadence. Matches SignalkClient's 5 s
    // TrackBuffer sample interval so the JS-side trackLayer (capped
    // at 1000 polyline segments) gets the same ~83 min visible
    // history as the persisted C# buffer. Without this gate the JS
    // side received a segment per applyFrame tick (multi-Hz under
    // bursty SignalK feeds), so 1000 segments could be exhausted
    // in 1-2 minutes and the helm saw only the last 10-20 s of
    // trail. Helm-reported regression; root cause is cadence drift
    // between the C# buffer (gated) and the JS frame emitter (not).
    public int TrackEmitIntervalMs { get; init; } = 5_000;

    // User-toggle: laylines overlay visible on the map. When false
    // the layline field never populates (no work to do).
    public bool LaylinesVisible { get; set; }

    // Helm-configured waypoint arrival radius in metres. Threaded
    // into FrameCourseLine each tick so the JS course-line layer can
    // draw a circle of this radius at the active WP. Settings-sourced
    // value; the page sets this once per tick from
    // Settings.WaypointArrivalRadiusMeters.
    public double ArrivalRadiusMeters { get; set; }

    // True when the unified ship-track layer is hidden. The page
    // sets this from <c>!Settings.ServerTrackVisible</c> on every
    // tick - when the helm toggles the layer off, per-tick local
    // segment emission stops too (the local trail is the live
    // cursor that fills the gap between server-track refetches;
    // both halves of the unified toggle go dark together).
    public bool SuppressLocalTrack { get; set; }

    // Previous own-boat position; null before first fix. Build()
    // maintains these between calls; callers can also seed PrevLat /
    // PrevLon from the server-side track snapshot on page init, or
    // reset CourseLineDrawn when the active route href changes outside
    // the per-tick pipeline (SyncActiveRouteAsync). Public setters
    // are the cleanest fit for the handful of external-write sites.
    public double? PrevLat { get; set; }
    public double? PrevLon { get; set; }
    public bool CourseLineDrawn { get; set; }
    public int LaylinesSkip { get; private set; }
    /// <summary>UTC ticks of the last emitted track segment. 0 means
    /// no segment has been emitted yet. Internal-set so tests can
    /// pin the gate; production callers don't touch this.</summary>
    public long LastTrackEmitTicks { get; set; }

    /// <summary>Clock seam for the track-emit gate. Defaults to
    /// <see cref="TimeProvider.System"/> so callers don't have to
    /// thread a time source through; tests that care about the
    /// 5-second cadence inject a <c>FakeTimeProvider</c>.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// Emits a frame payload reflecting the current NavigationData
    /// snapshot. Also advances the builder's internal state (PrevLat/
    /// Lon, LaylinesSkip, CourseLineDrawn) so the next call continues
    /// the inter-tick chain correctly.
    ///
    /// <para>The <paramref name="orientationOverride"/> /
    /// <paramref name="cogOverride"/> / <paramref name="sogOverride"/>
    /// hooks let the caller substitute smoothed values from
    /// <see cref="OnaPlotter.Services.INavigationAverages"/> + the
    /// helm's <c>ShipOrientationSource</c> pick. The boat icon
    /// stops twitching, the COG vector pulls from a stable angle,
    /// and the predicted endpoint matches the helm's tactical view
    /// instead of every wave's instantaneous reading. Each override
    /// is honoured only when non-null so a caller can mix smoothed
    /// + live (e.g. smoothed COG, live SOG) per channel.</para>
    /// </summary>
    public FramePayload Build(
        NavigationData data,
        double? orientationOverride = null,
        double? cogOverride = null,
        double? sogOverride = null)
    {
        double? bLat = data.Latitude;
        double? bLon = data.Longitude;

        FramePos? pos = (bLat is not null && bLon is not null)
            ? new FramePos(bLat.Value, bLon.Value,
                           orientationOverride ?? data.Heading,
                           cogOverride ?? data.CourseOverGround,
                           sogOverride ?? data.SpeedOverGround)
            : null;

        // Track segment: [lat, lon, sog, prevLat, prevLon]. Emitted
        // only when BOTH endpoints are known AND the configured
        // cadence has elapsed since the last emit. Without the
        // cadence gate the JS-side trackLayer (1000-segment cap)
        // burned through its budget in 1-2 minutes on bursty feeds;
        // helm saw only the last ~20 s of trail. PrevLat/PrevLon
        // advance on EMIT, not on every fix, so the next emitted
        // segment connects end-to-end with the previous one (no
        // gaps and no zigzag from intermediate skipped fixes).
        double[]? track = null;
        long nowTicks = TimeProvider.GetUtcNow().UtcTicks;
        long intervalTicks = (long)TrackEmitIntervalMs * TimeSpan.TicksPerMillisecond;
        bool gatePassed = LastTrackEmitTicks == 0
            || (nowTicks - LastTrackEmitTicks) >= intervalTicks;
        if (bLat is not null && bLon is not null
            && PrevLat is not null && PrevLon is not null
            && gatePassed
            && !SuppressLocalTrack)
        {
            track = new[]
            {
                bLat.Value, bLon.Value,
                data.SpeedOverGround ?? 0,
                PrevLat.Value, PrevLon.Value
            };
            PrevLat = bLat;
            PrevLon = bLon;
            LastTrackEmitTicks = nowTicks;
        }
        else if (PrevLat is null && PrevLon is null
                 && bLat is not null && bLon is not null)
        {
            // First fix after boot: seed Prev so the next gate-passed
            // tick has an endpoint to draw from. No segment emitted
            // (one point isn't a line).
            PrevLat = bLat;
            PrevLon = bLon;
        }

        // Course line (boat -> WP + arrival ring). Clear-flag fires on
        // the tick that transitions from "active course" to "no course";
        // after that nothing until course reappears.
        //
        // The XTE perpendicular tick visual was dropped per helm
        // feedback - the line drew silently from the boat to the side
        // of the active leg, was the same colour family as CPA + MOB
        // overlays, and helms reading the chart asked "what's the fat
        // red line?" because there was no AIS target / X / pin at the
        // end. The XTE classifier (Xte.Classify), the alarm rule, and
        // the HUD pill all stay - only the perpendicular bar on the
        // chart is gone.
        FrameCourseLine? course = null;
        bool clearCourse = false;
        var wpLat = data.CourseNextPointLatitude;
        var wpLon = data.CourseNextPointLongitude;
        if (wpLat is not null && wpLon is not null && bLat is not null && bLon is not null)
        {
            course = new FrameCourseLine(
                wpLat.Value, wpLon.Value,
                ArrivalRadiusMeters);
            CourseLineDrawn = true;
        }
        else if (CourseLineDrawn)
        {
            clearCourse = true;
            CourseLineDrawn = false;
        }

        // Tidal current arrow. Gated on drift cutoff so a resting
        // boat in a river doesn't draw a random 0.01 kn indicator.
        FrameCurrentArrow? current = null;
        if (data.CurrentSet is not null && data.CurrentDrift is not null
            && data.CurrentDrift > MinCurrentDriftMs
            && bLat is not null && bLon is not null)
        {
            current = new FrameCurrentArrow(
                bLat.Value, bLon.Value,
                data.CurrentSet.Value, data.CurrentDrift.Value);
        }

        // Laylines throttled to every Nth fix. Skip counter persists
        // across Build calls; resets on push.
        FrameLaylines? laylines = null;
        if (LaylinesVisible && data.WindDirectionTrue is not null
            && bLat is not null && bLon is not null
            && ++LaylinesSkip >= LaylinePushEveryNFixes)
        {
            LaylinesSkip = 0;
            double twa = data.WindAngleTrue is not null
                ? Math.Abs(data.WindAngleTrue.Value)
                : 45.0 * Math.PI / 180.0;
            laylines = new FrameLaylines(
                bLat.Value, bLon.Value,
                data.WindDirectionTrue.Value, twa,
                data.HasActiveCourse ? data.CourseNextPointLatitude : null,
                data.HasActiveCourse ? data.CourseNextPointLongitude : null);
        }

        return new FramePayload(pos, track, course, clearCourse, current, laylines);
    }
}

/// <summary>
/// Top-level frame object shipped to <c>applyFrame</c>. Field names
/// match the JS-side unpacking in leafletInterop.js. Record-struct so
/// tests get structural equality for free.
/// </summary>
public readonly record struct FramePayload(
    FramePos? Pos,
    double[]? Track,
    FrameCourseLine? Course,
    bool ClearCourse,
    FrameCurrentArrow? Current,
    FrameLaylines? Laylines);

public readonly record struct FramePos(
    double Lat, double Lon,
    double? HeadingRad, double? CogRad, double? SogMs);

public readonly record struct FrameCourseLine(
    double WpLat, double WpLon,
    /// <summary>Helm-configured waypoint arrival radius in metres. Drives
    /// the visible circle around the destination so the helm sees what
    /// distance counts as "arrived" without checking Settings. 0 (or
    /// negative) suppresses the ring - the route HUD also shows an
    /// "APPROACH alarm off (radius 0 m)" banner in that case so the
    /// helm knows the alarm is disabled.</summary>
    double ArrivalRadiusMeters);

public readonly record struct FrameCurrentArrow(
    double Lat, double Lon,
    double SetRad, double DriftMs);

public readonly record struct FrameLaylines(
    double Lat, double Lon,
    double TwdRad, double TwaRad,
    double? WpLat, double? WpLon);
