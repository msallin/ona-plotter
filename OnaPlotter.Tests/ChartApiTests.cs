using System.Net;
using OnaPlotter.Models;
using OnaPlotter.Services.Api;
using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the charts-endpoint parsing + relative-URL resolution + the
/// built-in OSM / OpenSeaMap prefix. ChartApi prepends the synthetic
/// charts from <see cref="BuiltInCharts.All"/> so the helm always has
/// a basemap available even when the SignalK server is unreachable
/// or hasn't published charts yet. Each test below filters the
/// returned list to the SK-server-derived charts (those whose
/// identifier is NOT one of the built-ins) so the parsing assertions
/// stay focused on what ChartApi did with the JSON.
/// </summary>
public class ChartApiTests
{
    /// <summary>Returns only the charts that came from the SK server
    /// payload, dropping the always-prefixed built-in OSM / OpenSeaMap
    /// entries. Equivalent of calling <see cref="ChartApi.GetAllAsync"/>
    /// before built-ins were added; the test assertions are mostly
    /// unchanged from that era.</summary>
    private static List<SignalkChart> SkOnly(IEnumerable<SignalkChart> all)
        => all.Where(c => !BuiltInCharts.IsBuiltIn(c.Identifier)).ToList();

    [Test]
    public async Task GetAllAsync_AlwaysPrefixesBuiltIns()
    {
        // The built-in OSM + OpenSeaMap charts come first regardless
        // of what the SK server publishes. Pin the prefix order so
        // the Layers panel's "basemap on top" convention stays stable
        // and the chart-order persistence treats built-ins as the
        // baseline z-stack.
        var api = new ChartApi(ApiTestHelpers.JsonClient(_ => "{}"), ApiTestHelpers.FixedBaseUrl());
        var charts = await api.GetAllAsync();

        await Assert.That(charts.Count).IsGreaterThanOrEqualTo(2);
        await Assert.That(charts[0].Identifier).IsEqualTo(BuiltInCharts.OsmIdentifier);
        await Assert.That(charts[1].Identifier).IsEqualTo(BuiltInCharts.OpenSeaMapIdentifier);
        // Built-ins always carry their internet URLs, not anything
        // SK-derived.
        await Assert.That(charts[0].GetTileUrl()).Contains("tile.openstreetmap.org");
        await Assert.That(charts[1].GetTileUrl()).Contains("tiles.openseamap.org");
    }

    [Test]
    public async Task GetAllAsync_Parses_Dict_Keyed_By_Identifier()
    {
        // SignalK returns charts as an object keyed by identifier; the
        // nested payload has name / tilemapUrl / etc. Shape here matches
        // what openplotter + signalk-charts-provider emits.
        var body = """
        {
            "openseamap-from-server": {
                "identifier": "openseamap-from-server",
                "name": "OpenSeaMap (server)",
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

        var skCharts = SkOnly(await api.GetAllAsync());

        await Assert.That(skCharts.Count).IsEqualTo(2);
        var osm = skCharts.First(c => c.Identifier == "openseamap-from-server");
        await Assert.That(osm.Name).IsEqualTo("OpenSeaMap (server)");
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

        var skCharts = SkOnly(await api.GetAllAsync());

        await Assert.That(skCharts.Count).IsEqualTo(1);
        await Assert.That(skCharts[0].Identifier).IsEqualTo("my-chart");
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

        var skCharts = SkOnly(await api.GetAllAsync());

        await Assert.That(skCharts.Count).IsEqualTo(1);
        await Assert.That(skCharts[0].GetTileUrl())
            .IsEqualTo($"{ApiTestHelpers.TestBase}/signalk/v1/chart-tiles/noaa/{{z}}/{{x}}/{{y}}");
    }

    [Test]
    public async Task GetAllAsync_Keeps_Absolute_Tile_Url_Untouched()
    {
        // External tile CDNs (OpenSeaMap, MapBox) return full https URLs.
        // Those must NOT be rewritten.
        var body = """
        { "external-osm": { "tilemapUrl":"https://tile.openstreetmap.org/{z}/{x}/{y}.png" } }
        """;
        var api = new ChartApi(ApiTestHelpers.JsonClient(_ => body), ApiTestHelpers.FixedBaseUrl());

        var skCharts = SkOnly(await api.GetAllAsync());

        await Assert.That(skCharts.Count).IsEqualTo(1);
        await Assert.That(skCharts[0].GetTileUrl())
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
            "working":  { "name":"X","tilemapUrl":"https://x/{z}/{x}/{y}" }
        }
        """;
        var api = new ChartApi(ApiTestHelpers.JsonClient(_ => body), ApiTestHelpers.FixedBaseUrl());

        var skCharts = SkOnly(await api.GetAllAsync());

        await Assert.That(skCharts.Count).IsEqualTo(1);
        await Assert.That(skCharts[0].Identifier).IsEqualTo("working");
    }

    [Test]
    public async Task GetAllAsync_Empty_SkResponse_StillReturnsBuiltIns()
    {
        // Empty SK chart list (no plugins, fresh install) still gives
        // the helm a usable Layers panel via the built-in basemaps.
        var api = new ChartApi(ApiTestHelpers.JsonClient(_ => "{}"), ApiTestHelpers.FixedBaseUrl());

        var charts = await api.GetAllAsync();
        var skCharts = SkOnly(charts);

        await Assert.That(skCharts).IsEmpty();
        await Assert.That(charts.Count).IsEqualTo(BuiltInCharts.All.Count);
    }

    [Test]
    public async Task GetAllAsync_Http_Error_FallsBackToBuiltInsOnly()
    {
        // SK server unreachable / 5xx: ChartApi previously surfaced
        // the HttpRequestException so Map.razor's SafeLoad could
        // toast the failure. Now we swallow + return the built-ins
        // because the helm needs a basemap MORE on a degraded server,
        // not less. SafeLoad's "couldn't load charts" toast moves
        // implicitly into the dev-console (Console.Error) so the
        // helm's experience is "Layers panel has basemaps + nothing
        // SK-derived" rather than "completely empty Layers panel".
        var http = ApiTestHelpers.MockClient(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var api = new ChartApi(http, ApiTestHelpers.FixedBaseUrl());

        var charts = await api.GetAllAsync();

        await Assert.That(charts.Count).IsEqualTo(BuiltInCharts.All.Count);
        await Assert.That(SkOnly(charts)).IsEmpty();
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

        var skCharts = SkOnly(await api.GetAllAsync());
        await Assert.That(skCharts[0].GetTileUrl()).IsEqualTo("https://a/{z}/{x}/{y}");
    }
}
