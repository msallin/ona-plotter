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
///           shape -- each emitted segment is from last-tick to this-
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

    // User-toggle: laylines overlay visible on the map. When false
    // the layline field never populates (no work to do).
    public bool LaylinesVisible { get; set; }

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

    /// <summary>
    /// Emits a frame payload reflecting the current NavigationData
    /// snapshot. Also advances the builder's internal state (PrevLat/
    /// Lon, LaylinesSkip, CourseLineDrawn) so the next call continues
    /// the inter-tick chain correctly.
    /// </summary>
    public FramePayload Build(NavigationData data)
    {
        double? bLat = data.Latitude;
        double? bLon = data.Longitude;

        FramePos? pos = (bLat is not null && bLon is not null)
            ? new FramePos(bLat.Value, bLon.Value,
                           data.Heading, data.CourseOverGround, data.SpeedOverGround)
            : null;

        // Track segment: [lat, lon, sog, prevLat, prevLon]. Emitted
        // only when BOTH endpoints are known; first tick after boot
        // produces no segment.
        double[]? track = null;
        if (bLat is not null && bLon is not null
            && PrevLat is not null && PrevLon is not null)
        {
            track = new[]
            {
                bLat.Value, bLon.Value,
                data.SpeedOverGround ?? 0,
                PrevLat.Value, PrevLon.Value
            };
        }
        if (bLat is not null && bLon is not null)
        {
            PrevLat = bLat;
            PrevLon = bLon;
        }

        // Course line (boat -> WP + leg line + XTE tick). Clear-flag
        // fires on the tick that transitions from "active course" to
        // "no course"; after that nothing until course reappears.
        FrameCourseLine? course = null;
        bool clearCourse = false;
        var wpLat = data.CourseNextPointLatitude;
        var wpLon = data.CourseNextPointLongitude;
        if (wpLat is not null && wpLon is not null && bLat is not null && bLon is not null)
        {
            course = new FrameCourseLine(
                wpLat.Value, wpLon.Value,
                data.CoursePreviousPointLatitude,
                data.CoursePreviousPointLongitude,
                data.CrossTrackError);
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
    double? PrevLat, double? PrevLon,
    double? Xte);

public readonly record struct FrameCurrentArrow(
    double Lat, double Lon,
    double SetRad, double DriftMs);

public readonly record struct FrameLaylines(
    double Lat, double Lon,
    double TwdRad, double TwaRad,
    double? WpLat, double? WpLon);
