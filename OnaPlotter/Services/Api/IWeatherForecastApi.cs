using OnaPlotter.Models;

namespace OnaPlotter.Services.Api;

/// <summary>
/// Fetches hourly wind forecasts. The current implementation uses
/// Open-Meteo's free global forecast API (no key, CORS-friendly).
/// Returned forecasts cover the next <c>forecastHours</c> hours at the
/// requested lat/lon and are the input to
/// <see cref="Utilities.IsochroneRouter"/>.
/// </summary>
public interface IWeatherForecastApi
{
    /// <summary>Hourly forecast for the next <paramref name="forecastHours"/>
    /// hours at a single point. Null on network/parse failure so the UI
    /// can degrade without tearing down.</summary>
    Task<WindForecast?> GetAsync(
        double latitude,
        double longitude,
        int forecastHours = 72,
        CancellationToken ct = default);
}
