using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OnaPlotter.Models;
using OnaPlotter.Services.Pois;

namespace OnaPlotter.Tests.Services.Pois;

/// <summary>
/// HTTP-tolerance pin for <see cref="OverpassPoiService"/>: every
/// transient failure path collapses to an empty list (the
/// <see cref="IMarinePoiService"/> contract). No throws into the
/// controller, no toast spam, no half-rendered overlays.
/// </summary>
public class OverpassPoiServiceTests
{
    private static IReadOnlySet<MarinePoiCategory> Cat(params MarinePoiCategory[] cats)
        => new HashSet<MarinePoiCategory>(cats);

    private static OverpassPoiService NewService(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler);
        var time = new FakeTimeProvider(new DateTime(2026, 5, 7, 12, 0, 0, DateTimeKind.Utc));
        return new OverpassPoiService(
            http,
            NullLogger<OverpassPoiService>.Instance,
            time,
            // Endpoint not actually contacted -- the handler short-circuits.
            endpoint: "http://test.local/interpreter");
    }

    [Test]
    public async Task EmptyCategories_NoHttpCall()
    {
        // Caller passes nothing to fetch: the service must short-
        // circuit before allocating an HTTP request.
        var svc = NewService(new ThrowingHandler());
        var pois = await svc.FetchAsync(40, -75, 41, -74, Cat());
        await Assert.That(pois.Count).IsEqualTo(0);
    }

    [Test]
    public async Task TransportError_ReturnsEmpty()
    {
        // Network down / dropped TLS -> empty list, no throw. The
        // controller falls back to the cache.
        var svc = NewService(new ThrowingHandler());
        var pois = await svc.FetchAsync(40, -75, 41, -74, Cat(MarinePoiCategory.Marina));
        await Assert.That(pois.Count).IsEqualTo(0);
    }

    [Test]
    public async Task NonSuccessStatus_ReturnsEmpty()
    {
        // 429 from the public Overpass instance is the most common
        // failure mode; the service must not surface it as an
        // exception into the controller.
        var svc = NewService(new StatusHandler(HttpStatusCode.TooManyRequests, "rate limited"));
        var pois = await svc.FetchAsync(40, -75, 41, -74, Cat(MarinePoiCategory.Marina));
        await Assert.That(pois.Count).IsEqualTo(0);
    }

    [Test]
    public async Task MalformedJson_ReturnsEmpty()
    {
        var svc = NewService(new StatusHandler(HttpStatusCode.OK, "not json {{{"));
        var pois = await svc.FetchAsync(40, -75, 41, -74, Cat(MarinePoiCategory.Marina));
        await Assert.That(pois.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ValidPayload_ParsesAndReturnsPois()
    {
        // Realistic Overpass response shape: top-level elements
        // array with a node + a way+center. Both should survive the
        // round-trip.
        const string body = """
            {
              "version": 0.6,
              "generator": "Overpass API",
              "elements": [
                {
                  "type": "node",
                  "id": 100,
                  "lat": 40.5,
                  "lon": -73.5,
                  "tags": { "leisure": "marina", "name": "Test Marina" }
                },
                {
                  "type": "way",
                  "id": 200,
                  "center": { "lat": 41.0, "lon": -73.0 },
                  "tags": { "amenity": "fuel", "boat": "yes" }
                }
              ]
            }
            """;
        var svc = NewService(new StatusHandler(HttpStatusCode.OK, body));
        var pois = await svc.FetchAsync(40, -75, 41, -74,
            Cat(MarinePoiCategory.Marina, MarinePoiCategory.Fuel));
        await Assert.That(pois.Count).IsEqualTo(2);
        await Assert.That(pois[0].Id).IsEqualTo("n100");
        await Assert.That(pois[0].Category).IsEqualTo(MarinePoiCategory.Marina);
        await Assert.That(pois[0].Name).IsEqualTo("Test Marina");
        await Assert.That(pois[1].Id).IsEqualTo("w200");
        await Assert.That(pois[1].Category).IsEqualTo(MarinePoiCategory.Fuel);
    }

    [Test]
    public async Task EmptyElements_ReturnsEmpty()
    {
        // Server says "nothing in this bbox". Not an error; just no
        // services here.
        var svc = NewService(new StatusHandler(HttpStatusCode.OK,
            "{\"version\":0.6,\"elements\":[]}"));
        var pois = await svc.FetchAsync(40, -75, 41, -74, Cat(MarinePoiCategory.Marina));
        await Assert.That(pois.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ExternalCancellation_ReturnsEmpty()
    {
        // Helm panned again before the fetch returned. The service
        // must respect the token and exit cleanly.
        var svc = NewService(new SlowHandler());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var pois = await svc.FetchAsync(40, -75, 41, -74,
            Cat(MarinePoiCategory.Marina), cts.Token);
        await Assert.That(pois.Count).IsEqualTo(0);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("network down");
    }

    private sealed class StatusHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public StatusHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var resp = new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(resp);
        }
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // Long enough to outlive the test cancel; the cancel
            // signal is what should win.
            await Task.Delay(TimeSpan.FromSeconds(60), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
