using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using OnaPlotter.Services.Api;

namespace OnaPlotter.Tests;

public class WeatherForecastApiTests
{
    [Test]
    public async Task GetAsync_200_ParsesSamples()
    {
        const string body = """
        {
          "hourly": {
            "time": ["2026-04-18T00:00", "2026-04-18T01:00", "2026-04-18T02:00"],
            "wind_speed_10m": [12.3, 14.1, 15.0],
            "wind_direction_10m": [270.0, 275.5, 280.0]
          }
        }
        """;
        var client = ApiTestHelpers.JsonClient(_ => body);
        var api = new WeatherForecastApi(client, NullLogger<WeatherForecastApi>.Instance);

        var forecast = await api.GetAsync(47.0, 8.0, forecastHours: 3);

        await Assert.That(forecast).IsNotNull();
        await Assert.That(forecast!.Hours.Count).IsEqualTo(3);
        await Assert.That(forecast.Hours[0].SpeedKn).IsEqualTo(12.3);
        await Assert.That(forecast.Hours[0].DirectionDeg).IsEqualTo(270.0);
        await Assert.That(forecast.Latitude).IsEqualTo(47.0);
        await Assert.That(forecast.Longitude).IsEqualTo(8.0);
    }

    [Test]
    public async Task GetAsync_RequestHitsOpenMeteo_WithQueryParams()
    {
        string? url = null;
        var client = ApiTestHelpers.MockClient(req =>
        {
            url = req.RequestUri!.AbsoluteUri;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"hourly":{"time":[],"wind_speed_10m":[],"wind_direction_10m":[]}}""",
                    System.Text.Encoding.UTF8, "application/json")
            };
        });
        var api = new WeatherForecastApi(client, NullLogger<WeatherForecastApi>.Instance);

        await api.GetAsync(47.5, 8.25, forecastHours: 24);

        await Assert.That(url).IsNotNull();
        await Assert.That(url).Contains("api.open-meteo.com");
        await Assert.That(url).Contains("latitude=47.5000");
        await Assert.That(url).Contains("longitude=8.2500");
        await Assert.That(url).Contains("wind_speed_10m");
        await Assert.That(url).Contains("wind_direction_10m");
        await Assert.That(url).Contains("windspeed_unit=kn");
    }

    [Test]
    public async Task GetAsync_HttpError_ReturnsNull()
    {
        var client = ApiTestHelpers.MockClient(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var api = new WeatherForecastApi(client, NullLogger<WeatherForecastApi>.Instance);

        var forecast = await api.GetAsync(0, 0);
        await Assert.That(forecast).IsNull();
    }

    [Test]
    public async Task GetAsync_MalformedJson_ReturnsNull()
    {
        var client = ApiTestHelpers.JsonClient(_ => "not json");
        var api = new WeatherForecastApi(client, NullLogger<WeatherForecastApi>.Instance);

        var forecast = await api.GetAsync(0, 0);
        await Assert.That(forecast).IsNull();
    }

    [Test]
    public async Task Forecast_At_ReturnsClosestSample()
    {
        var t0 = new DateTime(2026, 4, 18, 10, 0, 0, DateTimeKind.Utc);
        var forecast = new OnaPlotter.Models.WindForecast(47, 8,
        [
            new(t0, 270, 10),
            new(t0.AddHours(1), 275, 12),
            new(t0.AddHours(2), 280, 14),
        ]);

        var s = forecast.At(t0.AddMinutes(50));    // Closer to hour 1.
        await Assert.That(s).IsNotNull();
        await Assert.That(s!.Value.SpeedKn).IsEqualTo(12);

        var clamp = forecast.At(t0.AddDays(5));
        await Assert.That(clamp!.Value.SpeedKn).IsEqualTo(14);   // clamp to last
    }
}
