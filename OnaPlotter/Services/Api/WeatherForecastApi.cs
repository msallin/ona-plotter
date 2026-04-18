using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using OnaPlotter.Models;

namespace OnaPlotter.Services.Api;

public sealed class WeatherForecastApi : IWeatherForecastApi
{
    private readonly HttpClient _http;
    private readonly ILogger<WeatherForecastApi> _logger;

    public WeatherForecastApi(HttpClient http, ILogger<WeatherForecastApi> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>Hard timeout per request. The default HttpClient timeout on
    /// flaky public wifi can stretch to ~30s; we'd rather fail fast and let
    /// the user retry than block the weather-route UI behind a stalled
    /// socket.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    public async Task<WindForecast?> GetAsync(
        double latitude, double longitude, int forecastHours = 72, CancellationToken ct = default)
    {
        // Open-Meteo expects 1-168 hours, 1-16 days.
        int hours = Math.Clamp(forecastHours, 1, 168);
        int days = Math.Max(1, (int)Math.Ceiling(hours / 24.0));

        var inv = CultureInfo.InvariantCulture;
        var url = $"{SignalKUrls.OpenMeteoBase}"
            + $"?latitude={latitude.ToString("F4", inv)}"
            + $"&longitude={longitude.ToString("F4", inv)}"
            + "&hourly=wind_speed_10m,wind_direction_10m"
            + "&windspeed_unit=kn"
            + $"&forecast_days={days.ToString(inv)}"
            + "&timezone=UTC";

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(RequestTimeout);

        try
        {
            using var res = await _http.GetAsync(url, timeoutCts.Token);
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogWarning("Open-Meteo returned {Status}", res.StatusCode);
                return null;
            }
            var body = await res.Content.ReadFromJsonAsync<OpenMeteoResponse>(cancellationToken: timeoutCts.Token);
            return Parse(latitude, longitude, body, hours);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Open-Meteo request timed out after {Timeout}s", RequestTimeout.TotalSeconds);
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Open-Meteo request failed");
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Open-Meteo returned malformed JSON");
            return null;
        }
    }

    internal static WindForecast? Parse(double lat, double lon, OpenMeteoResponse? body, int maxHours)
    {
        if (body?.Hourly is not { } h) return null;
        if (h.Time is null || h.Time.Length == 0) return null;
        if (h.WindSpeed10m is null || h.WindDirection10m is null) return null;

        int count = Math.Min(Math.Min(h.Time.Length, h.WindSpeed10m.Length),
                             Math.Min(h.WindDirection10m.Length, maxHours));
        if (count == 0) return null;

        var samples = new WindSample[count];
        for (int i = 0; i < count; i++)
        {
            // Open-Meteo emits ISO 8601 without a TZ offset when timezone=UTC.
            // DateTime.Parse on such strings yields Kind=Unspecified; be
            // explicit so downstream comparisons against UtcNow are sound.
            DateTime t = DateTime.SpecifyKind(
                DateTime.Parse(h.Time[i], CultureInfo.InvariantCulture),
                DateTimeKind.Utc);
            samples[i] = new WindSample(t, h.WindDirection10m[i], h.WindSpeed10m[i]);
        }
        return new WindForecast(lat, lon, samples);
    }

    // Shape of the Open-Meteo hourly-forecast response. Only the fields we
    // actually use are declared; the rest is ignored by System.Text.Json.
    internal sealed class OpenMeteoResponse
    {
        public HourlyBlock? Hourly { get; set; }
    }

    internal sealed class HourlyBlock
    {
        [System.Text.Json.Serialization.JsonPropertyName("time")]
        public string[]? Time { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("wind_speed_10m")]
        public double[]? WindSpeed10m { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("wind_direction_10m")]
        public double[]? WindDirection10m { get; set; }
    }
}
