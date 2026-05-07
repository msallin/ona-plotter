using OnaPlotter.Models;

namespace OnaPlotter.Services.Alarms;

/// <summary>
/// APPROACH: the active course's next waypoint is within the user's
/// configured arrival radius. Fires once per (waypoint, entry) pair so
/// dismissing doesn't cause the alarm to re-arm while the boat is still
/// close; leaving the radius and re-entering (or the active waypoint
/// changing) re-arms.
/// </summary>
/// <remarks>
/// Latching (<c>AutoClear = false</c>) because this is a one-shot
/// "you've arrived" signal, not a continuously-true condition. The rule
/// keeps its own "last alarmed for" tuple so the AlarmManager doesn't
/// have to expose dismissal state to rules.
/// </remarks>
public sealed class WaypointApproachAlarmRule : IAlarmRule
{
    public string Title => "APPROACH";
    public int Priority => 250;  // below SHALLOW (100) and CPA (200)
    public bool AutoClear => false;

    // The (lat, lon) of the waypoint we've already alarmed for on the
    // current entry into the radius. (null, null) means "not armed" -
    // we'll fire on the next crossing.
    private (double? Lat, double? Lon) _alarmedFor;

    // Epsilon for waypoint-identity comparison. JSON round-trip + per-
    // plugin recomputation can drift a few µ-deg between deltas for the
    // *same* waypoint; exact double equality would see drift as a fresh
    // waypoint and re-fire mid-dwell. 1e-6 deg ≈ 11 cm - well under any
    // realistic arrival radius and well above round-trip jitter.
    private const double WaypointIdentityEpsilon = 1e-6;

    public AlarmInfo? Check(AlarmEvaluationContext ctx)
    {
        // Gate: when the helm has opted into server-side approach
        // alarms (default), this client rule mutes itself -
        // ServerNotificationsAlarmRule surfaces the SK course-provider
        // plugin's `notifications.navigation.arrivalCircleEntered` /
        // `perpendicularPassed` / `routeComplete` deltas with title
        // "APPROACH" (see DeriveTitleAndDefault in that file). One
        // arrival cue, agreeing with the autopilot's logic. The
        // _alarmedFor latch is reset so a future toggle-off doesn't
        // see stale state from the previous active waypoint.
        if (ctx.Settings.ServerSideApproachAlarms)
        {
            _alarmedFor = (null, null);
            return null;
        }

        if (!ctx.Data.HasActiveCourse) { _alarmedFor = (null, null); return null; }

        var wpLat = ctx.Data.CourseNextPointLatitude;
        var wpLon = ctx.Data.CourseNextPointLongitude;
        var dist = ctx.Data.CourseNextPointDistance;
        if (wpLat is null || wpLon is null || dist is null) return null;

        double radius = ctx.Settings.WaypointArrivalRadiusMeters;
        if (radius <= 0) return null;

        if (dist > radius)
        {
            // Outside the radius: re-arm so the next crossing fires.
            _alarmedFor = (null, null);
            return null;
        }

        // Inside the radius. If we've already alarmed for THIS waypoint
        // on this entry, stay silent. Epsilon comparison on lat + lon
        // defends against float-precision jitter from the server.
        if (_alarmedFor.Lat is double prevLat && _alarmedFor.Lon is double prevLon
            && System.Math.Abs(wpLat.Value - prevLat) < WaypointIdentityEpsilon
            && System.Math.Abs(wpLon.Value - prevLon) < WaypointIdentityEpsilon)
        {
            return null;
        }

        _alarmedFor = (wpLat, wpLon);
        // Per-waypoint TargetKey gives each arrival a distinct banner +
        // audio ping. Without it, a multi-leg route collapses every
        // APPROACH into the same (title, null) slot and the next
        // alarm silently overwrites the previous message.
        string targetKey = $"{wpLat.Value:F5}|{wpLon.Value:F5}";
        return new AlarmInfo(
            Title: Title,
            Message: $"{dist:F0} m to {ctx.Data.ActiveRouteName ?? "waypoint"}",
            Severity: AlarmSeverity.Warn,
            TargetKey: targetKey,
            TimeToEventMinutes: 0);
    }
}
