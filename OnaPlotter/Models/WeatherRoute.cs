namespace OnaPlotter.Models;

/// <summary>Result of a weather-routing computation: an ordered list of
/// waypoints with times, plus summary stats.</summary>
public sealed record WeatherRoute(
    IReadOnlyList<WeatherRouteWaypoint> Path,
    double TotalNauticalMiles,
    TimeSpan Duration);

public readonly record struct WeatherRouteWaypoint(
    double Latitude,
    double Longitude,
    DateTime Time);
