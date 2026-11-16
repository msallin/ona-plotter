using System.Text.Json.Serialization;

namespace OnaPlotter.Services.Js;

/// <summary>
/// C#-owned constants the Leaflet map needs at <c>initMap</c>, before any
/// layer renders.
/// </summary>
/// <param name="MaxZoom">Viewport cap (<c>ChartUpscale.MapMaxZoom</c>).</param>
/// <param name="SpeedPalette">Track colour per speed bucket
/// (<c>SpeedColor.BucketPalette</c>); track payloads carry bucket ids
/// that index it.</param>
public sealed record MapInitOptions(
    [property: JsonPropertyName("maxZoom")] int MaxZoom,
    [property: JsonPropertyName("speedPalette")] string[] SpeedPalette);
