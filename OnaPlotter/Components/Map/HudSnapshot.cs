using OnaPlotter.Models;

namespace OnaPlotter.Components.Map;

/// <summary>
/// Per-tick values the Map page computes for the HUD (they come from
/// page-level services rather than <see cref="NavigationData"/>).
/// </summary>
public sealed record HudLiveInputs(
    double? LastHeadingRad,
    bool AnchorDragging,
    double? DepthTrendMetersPerMinute,
    double WaypointArrivalRadiusMeters,
    bool CourseStopping,
    double? ActiveRouteDistanceTotal);

/// <summary>
/// Everything the HUD displays, quantised to just below what the helm can
/// see: angles to ~0.6 deg (0.01 rad), speeds to 0.1 m/s, distances to
/// the displayed precision. Equal snapshots mean an identical HUD, so
/// <see cref="MapHud"/> repaints only when this changes; sensor noise (a
/// fluxgate compass wobbling 0.1 deg, GPS SOG jitter at rest) doesn't.
/// </summary>
public readonly record struct HudSnapshot(
    long Sog, long Cog, long Hdg, long Depth,
    long Awa, long Aws, long Twa, long Tws, long Twd,
    long NextDist, long NextBrg, long NextVmg, long NextTtg,
    long AnchorCur, long AnchorMax, long AnchorLat,
    long Xte, string? RouteHref,
    int? RoutePtIdx, int? RoutePtTotal,
    long RouteDistRem, long RouteTtg,
    long LastHeading, bool AnchorDragging, long DepthTrend,
    long ArrivalRadius, bool CourseStopping, long RouteDistTotal)
{
    /// <summary>Quantises a nullable value; <see cref="long.MinValue"/>
    /// stands in for null so value equality stays exact.</summary>
    public static long Q(double? v, int decimals)
    {
        if (v is not double d || !double.IsFinite(d)) return long.MinValue;
        return (long)Math.Round(d * Math.Pow(10, decimals));
    }

    public static HudSnapshot From(NavigationData data, HudLiveInputs live) => new(
        Q(data.SpeedOverGround, 1),
        Q(data.CourseOverGround, 2),
        Q(data.Heading, 2),
        Q(data.Depth, 1),
        Q(data.WindAngleApparent, 2),
        Q(data.WindSpeedApparent, 1),
        Q(data.WindAngleTrue, 2),
        Q(data.WindSpeedTrue, 1),
        Q(data.WindDirectionTrue, 2),
        Q(data.CourseNextPointDistance, 0),
        Q(data.CourseNextPointBearing, 2),
        Q(data.CourseNextPointVmg, 1),
        Q(data.CourseNextPointTimeToGo, 0),
        Q(data.AnchorCurrentRadius, 1),
        Q(data.AnchorMaxRadius, 1),
        Q(data.AnchorLatitude, 6),
        Q(data.CrossTrackError, 1),
        data.ActiveRouteHref,
        // Route-level fields drive the "WP 3 of 7" chip and route-total
        // row; without them a waypoint advance would lag.
        data.ActiveRoutePointIndex,
        data.ActiveRoutePointTotal,
        Q(data.ActiveRouteDistanceRemaining, 0),
        Q(data.ActiveRouteTimeToGo, 0),
        Q(live.LastHeadingRad, 2),
        live.AnchorDragging,
        Q(live.DepthTrendMetersPerMinute, 1),
        Q(live.WaypointArrivalRadiusMeters, 0),
        live.CourseStopping,
        Q(live.ActiveRouteDistanceTotal, 0));
}
