using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OnaPlotter.Services.Places;

namespace OnaPlotter.Tests.Services.Places;

/// <summary>
/// Pins the Nominatim client. Coverage is split:
///
/// <list type="bullet">
///   <item><description>Static <see cref="NominatimPlaceSearchService.TryMapResult"/>
///     -- the row-shape contract (string lat/lon, name fallback to
///     display_name head, range checks).</description></item>
///   <item><description>HTTP path -- a stubbed
///     <see cref="HttpMessageHandler"/> verifies the URL shape, the
///     User-Agent header, and the empty-list-on-failure contract.</description></item>
///   <item><description>Rate limit -- a fake clock confirms the second
///     call waits the required <see cref="NominatimPlaceSearchService.MinRequestInterval"/>
///     after the first.</description></item>
/// </list>
/// </summary>
public class NominatimPlaceSearchServiceTests
{
    [Test]
    public async Task TryMapResult_Maps_Well_Formed_Row()
    {
        var row = new NominatimResult(
            Lat: "52.5170365",
            Lon: "13.3888599",
            Name: "Berlin",
            DisplayName: "Berlin, 10117, Germany",
            Type: "city",
            AddressType: "city");

        var r = NominatimPlaceSearchService.TryMapResult(row);

        await Assert.That(r).IsNotNull();
        await Assert.That(r!.Name).IsEqualTo("Berlin");
        await Assert.That(r.DisplayLabel).IsEqualTo("Berlin, 10117, Germany");
        await Assert.That(r.Lat).IsEqualTo(52.5170365);
        await Assert.That(r.Lon).IsEqualTo(13.3888599);
        await Assert.That(r.Source).IsEqualTo("nominatim");
    }

    [Test]
    public async Task TryMapResult_Falls_Back_To_DisplayName_Head_When_Name_Empty()
    {
        // Some admin boundaries / rural rows arrive with empty `name`
        // and only the long display_name. The row should still render
        // using the head-of-display_name as the short label.
        var row = new NominatimResult(
            Lat: "47.0",
            Lon: "8.0",
            Name: null,
            DisplayName: "Hauptstrasse, Luzern, Switzerland",
            Type: "highway",
            AddressType: null);

        var r = NominatimPlaceSearchService.TryMapResult(row);
        await Assert.That(r).IsNotNull();
        await Assert.That(r!.Name).IsEqualTo("Hauptstrasse");
    }

    [Test]
    public async Task TryMapResult_Null_When_Lat_Unparseable()
    {
        var row = new NominatimResult(
            Lat: "not-a-number",
            Lon: "13.0",
            Name: "X",
            DisplayName: "X",
            Type: null,
            AddressType: null);
        await Assert.That(NominatimPlaceSearchService.TryMapResult(row)).IsNull();
    }

    [Test]
    public async Task TryMapResult_Null_When_Coords_Out_Of_Range()
    {
        var badLat = new NominatimResult(
            Lat: "91.0", Lon: "0.0",
            Name: "ImpossibleNorth",
            DisplayName: "ImpossibleNorth",
            Type: null, AddressType: null);
        await Assert.That(NominatimPlaceSearchService.TryMapResult(badLat)).IsNull();

        var badLon = new NominatimResult(
            Lat: "0.0", Lon: "181.0",
            Name: "ImpossibleEast",
            DisplayName: "ImpossibleEast",
            Type: null, AddressType: null);
        await Assert.That(NominatimPlaceSearchService.TryMapResult(badLon)).IsNull();
    }

    [Test]
    public async Task TryMapResult_Null_When_Both_Name_And_DisplayName_Empty()
    {
        var row = new NominatimResult(
            Lat: "0.0", Lon: "0.0",
            Name: null, DisplayName: null,
            Type: null, AddressType: null);
        await Assert.That(NominatimPlaceSearchService.TryMapResult(row)).IsNull();
    }

    [Test]
    public async Task TryMapResult_Parses_With_InvariantCulture()
    {
        // German locale would use "52,5" -- Nominatim emits "52.5"
        // regardless. Pinning that we always parse invariantly.
        var row = new NominatimResult(
            Lat: "52.5", Lon: "13.4",
            Name: "x", DisplayName: "x",
            Type: null, AddressType: null);

        // Switch to a comma-decimal culture for the duration of the
        // call so a regression to CurrentCulture would surface.
        var prev = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture =
                new System.Globalization.CultureInfo("de-DE");
            var r = NominatimPlaceSearchService.TryMapResult(row);
            await Assert.That(r).IsNotNull();
            await Assert.That(r!.Lat).IsEqualTo(52.5);
            await Assert.That(r.Lon).IsEqualTo(13.4);
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = prev;
        }
    }

    [Test]
    public async Task SearchAsync_Empty_Query_Returns_Empty_Without_Calling_Http()
    {
        var http = new HttpClient(new ThrowingHandler());
        var svc = new NominatimPlaceSearchService(http, NullLogger<NominatimPlaceSearchService>.Instance);

        var r = await svc.SearchAsync("   ");
        await Assert.That(r.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SearchAsync_Sends_UserAgent_Header()
    {
        var handler = new RecordingHandler("[]");
        var http = new HttpClient(handler);
        var svc = new NominatimPlaceSearchService(http, NullLogger<NominatimPlaceSearchService>.Instance);

        await svc.SearchAsync("Berlin");

        await Assert.That(handler.LastRequest).IsNotNull();
        // OSMF policy: required User-Agent. Asserting the prefix
        // (the version is allowed to drift) so the test stays stable
        // across release bumps.
        var ua = handler.LastRequest!.Headers.UserAgent.ToString();
        await Assert.That(ua.StartsWith("OnaPlotter/", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task SearchAsync_Encodes_Query_And_Hits_jsonv2()
    {
        var handler = new RecordingHandler("[]");
        var http = new HttpClient(handler);
        var svc = new NominatimPlaceSearchService(http, NullLogger<NominatimPlaceSearchService>.Instance);

        await svc.SearchAsync("New York");

        // AbsoluteUri preserves the percent-encoding in the query
        // string. Uri.ToString() helpfully decodes %20 -> space which
        // would obscure whether the OUTBOUND request was correctly
        // encoded.
        var url = handler.LastRequest!.RequestUri!.AbsoluteUri;
        await Assert.That(url.Contains("nominatim.openstreetmap.org/search")).IsTrue();
        await Assert.That(url.Contains("format=jsonv2")).IsTrue();
        // Encoded space.
        await Assert.That(url.Contains("q=New%20York")).IsTrue();
    }

    [Test]
    public async Task SearchAsync_Parses_Live_Shape_Response()
    {
        // A trimmed real response from nominatim.openstreetmap.org's
        // /search endpoint. format=jsonv2 returns a flat array; we
        // bind the fields we render and ignore the rest.
        const string body = """
        [
          {
            "place_id": 159123,
            "licence": "Data (c) OpenStreetMap",
            "osm_type": "relation",
            "osm_id": 62422,
            "lat": "52.5170365",
            "lon": "13.3888599",
            "category": "boundary",
            "type": "administrative",
            "place_rank": 8,
            "importance": 0.95,
            "addresstype": "city",
            "name": "Berlin",
            "display_name": "Berlin, 10117, Germany",
            "boundingbox": ["52.3382448", "52.6755087", "13.0883450", "13.7611609"]
          }
        ]
        """;
        var handler = new RecordingHandler(body);
        var http = new HttpClient(handler);
        var svc = new NominatimPlaceSearchService(http, NullLogger<NominatimPlaceSearchService>.Instance);

        var results = await svc.SearchAsync("Berlin");

        await Assert.That(results.Count).IsEqualTo(1);
        await Assert.That(results[0].Name).IsEqualTo("Berlin");
        await Assert.That(results[0].Source).IsEqualTo("nominatim");
        await Assert.That(results[0].Lat).IsEqualTo(52.5170365);
    }

    [Test]
    public async Task SearchAsync_Returns_Empty_On_Non_2xx()
    {
        var handler = new RecordingHandler("[]") { Status = HttpStatusCode.TooManyRequests };
        var http = new HttpClient(handler);
        var svc = new NominatimPlaceSearchService(http, NullLogger<NominatimPlaceSearchService>.Instance);

        var r = await svc.SearchAsync("Berlin");
        await Assert.That(r.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SearchAsync_Returns_Empty_On_Network_Failure()
    {
        var handler = new ThrowingHandler();
        var http = new HttpClient(handler);
        var svc = new NominatimPlaceSearchService(http, NullLogger<NominatimPlaceSearchService>.Instance);

        var r = await svc.SearchAsync("Berlin");
        await Assert.That(r.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SearchAsync_Returns_Empty_On_Malformed_Json()
    {
        var handler = new RecordingHandler("{not-json");
        var http = new HttpClient(handler);
        var svc = new NominatimPlaceSearchService(http, NullLogger<NominatimPlaceSearchService>.Instance);

        var r = await svc.SearchAsync("Berlin");
        await Assert.That(r.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SearchAsync_Rate_Limits_Two_Calls_To_MinRequestInterval()
    {
        // First call lands immediately; second call must have waited
        // at least MinRequestInterval after the first. We assert by
        // advancing a fake clock exactly to that boundary and
        // observing both calls succeed -- the recorded second call
        // shouldn't fire before then.
        //
        // Easier: assert the timestamps the handler observes.
        var handler = new TimestampingHandler("[]");
        var http = new HttpClient(handler);
        var time = new FakeTimeProvider(new DateTime(2026, 5, 6, 12, 0, 0, DateTimeKind.Utc));
        var svc = new NominatimPlaceSearchService(
            http, NullLogger<NominatimPlaceSearchService>.Instance, time);

        var first = svc.SearchAsync("Berlin");
        // First call doesn't need to wait.
        await first;

        var second = svc.SearchAsync("Bremen");
        // Advance the clock just below the rate-limit threshold; the
        // call should NOT have proceeded to the handler yet.
        time.Advance(NominatimPlaceSearchService.MinRequestInterval - TimeSpan.FromMilliseconds(10));
        await Task.Delay(20);
        await Assert.That(handler.RequestCount).IsEqualTo(1);

        // Step over the boundary and the gated call lands.
        time.Advance(TimeSpan.FromMilliseconds(20));
        await second;
        await Assert.That(handler.RequestCount).IsEqualTo(2);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("simulated network down");
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly string _body;
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public HttpRequestMessage? LastRequest { get; private set; }

        public RecordingHandler(string body) { _body = body; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>Records each request's arrival-count so the rate-limit
    /// test can verify only one call has reached the network at a
    /// given moment.</summary>
    private sealed class TimestampingHandler : HttpMessageHandler
    {
        private readonly string _body;
        public int RequestCount { get; private set; }
        public TimestampingHandler(string body) { _body = body; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
