using System.Net;
using OnaPlotter.Services.Api;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the charts-endpoint parsing + relative-URL resolution. ChartApi
/// was at 0% coverage; these tests exercise the happy path, the
/// identifier-from-key fallback, the relative-URL absolutisation, and
/// the drop-charts-without-a-URL filter.
/// </summary>
public class ChartApiTests
{
    [Test]
    public async Task GetAllAsync_Parses_Dict_Keyed_By_Identifier()
    {
        // SignalK returns charts as an object keyed by identifier; the
        // nested payload has name / tilemapUrl / etc. Shape here matches
        // what openplotter + signalk-charts-provider emits.
        var body = """
        {
            "openseamap": {
                "identifier": "openseamap",
                "name": "OpenSeaMap",
                "tilemapUrl": "https://tiles.openseamap.org/seamark/{z}/{x}/{y}.png",
                "minzoom": 3,
                "maxzoom": 18,
                "bounds": [-180, -85, 180, 85]
            },
            "noaa": {
                "name": "NOAA RNC",
                "tilemapUrl": "/signalk/v1/chart-tiles/noaa/{z}/{x}/{y}"
            }
        }
        """;
        var http = ApiTestHelpers.JsonClient(_ => body);
        var api = new ChartApi(http, ApiTestHelpers.FixedBaseUrl());

        var charts = await api.GetAllAsync();

        await Assert.That(charts.Count).IsEqualTo(2);
        var osm = charts.First(c => c.Identifier == "openseamap");
        await Assert.That(osm.Name).IsEqualTo("OpenSeaMap");
        await Assert.That(osm.MinZoom).IsEqualTo(3);
        await Assert.That(osm.MaxZoom).IsEqualTo(18);
    }

    [Test]
    public async Task GetAllAsync_Falls_Back_To_Dict_Key_For_Missing_Identifier()
    {
        // A lot of signalk-charts-provider installs omit the inner
        // "identifier" field. The dict key is the canonical id then.
        var body = """{ "my-chart": { "name": "X", "tilemapUrl": "https://x/{z}/{x}/{y}" } }""";
        var api = new ChartApi(ApiTestHelpers.JsonClient(_ => body), ApiTestHelpers.FixedBaseUrl());

        var charts = await api.GetAllAsync();

        await Assert.That(charts.Count).IsEqualTo(1);
        await Assert.That(charts[0].Identifier).IsEqualTo("my-chart");
    }

    [Test]
    public async Task GetAllAsync_Resolves_Relative_Tile_Url_Against_BaseUrl()
    {
        // A relative tilemapUrl like "/signalk/v1/chart-tiles/noaa/{z}/{x}/{y}"
        // must be absolutised or Leaflet can't fetch. The absolute form
        // uses the SignalK server's origin, not the webapp's mount path.
        var body = """
        { "noaa": { "name":"NOAA","tilemapUrl":"/signalk/v1/chart-tiles/noaa/{z}/{x}/{y}" } }
        """;
        var api = new ChartApi(ApiTestHelpers.JsonClient(_ => body), ApiTestHelpers.FixedBaseUrl());

        var charts = await api.GetAllAsync();

        await Assert.That(charts.Count).IsEqualTo(1);
        await Assert.That(charts[0].GetTileUrl())
            .IsEqualTo($"{ApiTestHelpers.TestBase}/signalk/v1/chart-tiles/noaa/{{z}}/{{x}}/{{y}}");
    }

    [Test]
    public async Task GetAllAsync_Keeps_Absolute_Tile_Url_Untouched()
    {
        // External tile CDNs (OpenSeaMap, MapBox) return full https URLs.
        // Those must NOT be rewritten.
        var body = """
        { "osm": { "tilemapUrl":"https://tile.openstreetmap.org/{z}/{x}/{y}.png" } }
        """;
        var api = new ChartApi(ApiTestHelpers.JsonClient(_ => body), ApiTestHelpers.FixedBaseUrl());

        var charts = await api.GetAllAsync();

        await Assert.That(charts.Count).IsEqualTo(1);
        await Assert.That(charts[0].GetTileUrl())
            .IsEqualTo("https://tile.openstreetmap.org/{z}/{x}/{y}.png");
    }

    [Test]
    public async Task GetAllAsync_Drops_Charts_Without_A_TileUrl()
    {
        // A chart entry with no tilemapUrl / url is unusable -- filter so
        // the Layers panel doesn't show an entry whose toggle does nothing.
        var body = """
        {
            "broken":   { "name":"No URL"   },
            "working":  { "name":"OSM","tilemapUrl":"https://x/{z}/{x}/{y}" }
        }
        """;
        var api = new ChartApi(ApiTestHelpers.JsonClient(_ => body), ApiTestHelpers.FixedBaseUrl());

        var charts = await api.GetAllAsync();

        await Assert.That(charts.Count).IsEqualTo(1);
        await Assert.That(charts[0].Identifier).IsEqualTo("working");
    }

    [Test]
    public async Task GetAllAsync_Empty_Response_Returns_Empty_List()
    {
        var api = new ChartApi(ApiTestHelpers.JsonClient(_ => "{}"), ApiTestHelpers.FixedBaseUrl());
        await Assert.That(await api.GetAllAsync()).IsEmpty();
    }

    [Test]
    public async Task GetAllAsync_Http_Error_Throws_So_Caller_Can_Surface()
    {
        // EnsureSuccessStatusCode turns a 5xx into HttpRequestException.
        // The Map page wraps this in SafeLoad; ChartApi itself must
        // propagate so the caller can tell between "no charts" (empty
        // dict, ok) and "server down" (exception).
        var http = ApiTestHelpers.MockClient(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var api = new ChartApi(http, ApiTestHelpers.FixedBaseUrl());

        await Assert.That(async () => await api.GetAllAsync()).Throws<HttpRequestException>();
    }

    [Test]
    public async Task GetAllAsync_Prefers_V1_TilemapUrl_Over_V2_Url_When_Both_Set()
    {
        // SignalkChart.GetTileUrl returns TilemapUrl ?? Url; pin it.
        var body = """
        { "mix": {
            "tilemapUrl":"https://a/{z}/{x}/{y}",
            "url":"https://b/{z}/{x}/{y}"
        } }
        """;
        var api = new ChartApi(ApiTestHelpers.JsonClient(_ => body), ApiTestHelpers.FixedBaseUrl());

        var charts = await api.GetAllAsync();
        await Assert.That(charts[0].GetTileUrl()).IsEqualTo("https://a/{z}/{x}/{y}");
    }
}
