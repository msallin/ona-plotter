namespace OnaPlotter.Models;

/// <summary>
/// One sample of own-vessel state at a moment in time. Pushed into
/// <see cref="OnaPlotter.Services.TrackBuffer"/> every few seconds and
/// replayed by the History page (track polyline, scrubbed HUD values).
/// Optional fields are null when the underlying SignalK path was absent
/// at the sampling tick.
///
/// <para>WindDirectionTrue is the compass-from bearing (0..2π, north),
/// not the bow-relative angle WindAngleTrue. Both can be present:
/// directionTrue is what a wind-from-true sensor publishes, angleTrue
/// is the same wind expressed relative to the bow once the boat's
/// heading is folded in. The wind-page TWD chart and shift-rate
/// detector read directionTrue; the HUD dial reads angleTrue.</para>
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
    double? Depth = null,
    double? WindDirectionTrue = null);
