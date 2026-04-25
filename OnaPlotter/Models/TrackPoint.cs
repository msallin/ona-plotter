namespace OnaPlotter.Models;

/// <summary>
/// One sample of own-vessel state at a moment in time. Pushed into
/// <see cref="OnaPlotter.Services.TrackBuffer"/> every few seconds and
/// replayed by the History page (track polyline, scrubbed HUD values).
/// Optional fields are null when the underlying SignalK path was absent
/// at the sampling tick.
/// </summary>
public sealed record TrackPoint(
    DateTime Timestamp,
    double Latitude,
    double Longitude,
    double? SpeedOverGround,
    double? CourseOverGround,
    double? Heading,
    double? WindAngleApparent,
    double? WindSpeedApparent,
    double? WindAngleTrue,
    double? WindSpeedTrue,
    double? Depth = null);
