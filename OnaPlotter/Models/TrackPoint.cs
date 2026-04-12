// Immutable snapshot of vessel state at a point in time, used for track history.

namespace OnaPlotter.Models;

public sealed record TrackPoint(
    DateTime Timestamp,
    double Latitude,
    double Longitude,
    double? SpeedOverGround,
    double? CourseOverGround,
    double? Heading,
    double? WindAngleApparent,
    double? WindSpeedApparent);
