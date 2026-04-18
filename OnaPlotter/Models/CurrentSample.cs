namespace OnaPlotter.Models;

/// <summary>
/// A tidal (or ocean) current sample. Follows the maritime
/// "set and drift" convention: <see cref="SetDeg"/> is the direction
/// the water itself is moving TOWARD (opposite of the wind "FROM"
/// convention used in <see cref="WindSample"/>), and
/// <see cref="SpeedKn"/> is the magnitude in knots. Consumed by the
/// isochrone router to add the current vector to the polar-derived
/// speed-through-water when planning a route.
/// </summary>
public readonly record struct CurrentSample(
    DateTime ValidTime,
    double SetDeg,
    double SpeedKn);
