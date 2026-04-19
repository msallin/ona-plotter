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
    // current entry into the radius. (null, null) means "not armed" --
    // we'll fire on the next crossing.
    private (double? Lat, double? Lon) _alarmedFor;

    public AlarmInfo? Check(AlarmEvaluationContext ctx)
    {
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
        // on this entry, stay silent -- the user either dismissed it or
        // it's still latched in the banner.
        if (wpLat == _alarmedFor.Lat && wpLon == _alarmedFor.Lon) return null;

        _alarmedFor = (wpLat, wpLon);
        return new AlarmInfo(
            Title: Title,
            Message: $"{dist:F0} m to {ctx.Data.ActiveRouteName ?? "waypoint"}",
            Severity: AlarmSeverity.Warn,
            TimeToEventMinutes: 0);
    }
}
