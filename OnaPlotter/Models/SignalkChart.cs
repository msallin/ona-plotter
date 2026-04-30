using System.Text.Json.Serialization;

namespace OnaPlotter.Models;

/// <summary>
/// DTO for a chart resource from the SignalK charts API, describing a tile
/// layer with its coverage bounds, zoom range, and tile URL template.
/// </summary>
public sealed class SignalkChart
{
    [JsonPropertyName("identifier")]
    public string Identifier { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("format")]
    public string? Format { get; set; }

    /// <summary>
    /// V1 tile URL template, e.g. "/signalk/chart-tiles/openseamap/{z}/{x}/{y}".
    /// </summary>
    [JsonPropertyName("tilemapUrl")]
    public string? TilemapUrl { get; set; }

    /// <summary>
    /// V2 tile URL (same role as tilemapUrl).
    /// </summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("minzoom")]
    public int? MinZoom { get; set; }

    [JsonPropertyName("maxzoom")]
    public int? MaxZoom { get; set; }

    /// <summary>
    /// Bounds as [west, south, east, north].
    /// </summary>
    [JsonPropertyName("bounds")]
    public double[]? Bounds { get; set; }

    /// <summary>
    /// Whether the chart-upscale (overzoom) decorator should apply to
    /// this chart when the helm has the feature enabled. Default true
    /// because every SignalK-served chart wants the helm-controlled
    /// behaviour. The built-in OSM + OpenSeaMap charts set this to
    /// false so they go blank past their native cap rather than
    /// rendering blurry tiles on top of the chart's GPU-upscaled view
    /// -- helm reported the upscaled OSM as "OSM is loading on top of
    /// my chart" because the second-frame rerender looked like a
    /// flicker. SK servers don't ship this field today; the JSON
    /// deserialiser keeps the default. Plumbed through
    /// <c>ChartLayerController.ToggleAsync</c> which collapses the
    /// per-helm setting with this flag before passing to JS.
    /// </summary>
    [JsonPropertyName("allowUpscale")]
    public bool AllowUpscale { get; set; } = true;

    /// <summary>
    /// HTML attribution string rendered in Leaflet's bottom-right
    /// corner whenever this layer is on the map. SK chart-server
    /// charts don't ship attribution today (the helm picks the chart,
    /// the server doesn't preach about it), so the default is empty.
    /// The built-in OSM + OpenSeaMap charts populate this with the
    /// ODbL / CC-BY-SA-required credit + link so the helm satisfies
    /// the licence as soon as a basemap is enabled.
    /// </summary>
    [JsonPropertyName("attribution")]
    public string Attribution { get; set; } = "";

    /// <summary>
    /// Per-tile opacity (0..1). SK chart-server charts default to 0.8
    /// so they layer cleanly over basemaps; built-in OSM is 1.0 since
    /// it IS the basemap, OpenSeaMap is 0.8 because it's a transparent
    /// seamark overlay. Pinned in <c>BuiltInCharts</c> when the chart
    /// is synthesised on the client.
    /// </summary>
    [JsonPropertyName("opacity")]
    public double Opacity { get; set; } = 0.8;

    /// <summary>
    /// Returns the tile URL, preferring tilemapUrl (v1) over url (v2).
    /// </summary>
    public string? GetTileUrl() => TilemapUrl ?? Url;
}
