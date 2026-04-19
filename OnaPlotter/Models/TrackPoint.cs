namespace OnaPlotter.Models;

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
