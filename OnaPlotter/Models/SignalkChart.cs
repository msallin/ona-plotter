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
    /// Returns the tile URL, preferring tilemapUrl (v1) over url (v2).
    /// </summary>
    public string? GetTileUrl() => TilemapUrl ?? Url;
}
