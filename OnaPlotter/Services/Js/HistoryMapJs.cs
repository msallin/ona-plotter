using System.Text.Json.Serialization;
using Microsoft.JSInterop;
using OnaPlotter.Utilities;

namespace OnaPlotter.Services.Js;

/// <summary>One base tile layer of the History map.</summary>
public sealed record HistoryBaseLayer(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("maxZoom")] int MaxZoom,
    [property: JsonPropertyName("opacity")] double Opacity,
    [property: JsonPropertyName("attribution")] string Attribution);

/// <summary>
/// Typed wrapper around <c>wwwroot/js/historyMapLayer.js</c>, the History
/// page's own Leaflet instance. Centralises the JSDisconnected /
/// ObjectDisposed swallow for page teardown races; after
/// <see cref="MarkDisposed"/> every call is a silent no-op.
/// </summary>
public sealed class HistoryMapJs
{
    /// <summary>Module path for <c>JS.InvokeAsync&lt;IJSObjectReference&gt;("import", ...)</c>.</summary>
    public const string ModulePath = "./js/historyMapLayer.js";

    private readonly IJSObjectReference _module;
    private bool _disposed;

    public HistoryMapJs(IJSObjectReference module)
    {
        _module = module ?? throw new ArgumentNullException(nameof(module));
    }

    /// <summary>OSM basemap plus the OpenSeaMap seamark overlay, taken
    /// from <see cref="BuiltInCharts"/> so the tile URLs and attributions
    /// exist once in the codebase.</summary>
    public static IReadOnlyList<HistoryBaseLayer> DefaultBaseLayers { get; } =
        BuiltInCharts.All
            .Select(c => new HistoryBaseLayer(c.Url ?? "", c.MaxZoom ?? 19, c.Opacity, c.Attribution))
            .ToArray();

    /// <summary>Convert a SignalK chart's <c>[west, south, east, north]</c>
    /// bounds into Leaflet's <c>[[south, west], [north, east]]</c>.
    /// Null for missing, wrong-length or non-finite bounds (seen on
    /// hand-rolled tile sets), which leaves the layer unclipped.</summary>
    public static double[][]? ToLeafletBounds(double[]? westSouthEastNorth)
    {
        if (westSouthEastNorth is not { Length: 4 } b || !b.All(double.IsFinite)) return null;
        return [[b[1], b[0]], [b[3], b[2]]];
    }

    public void MarkDisposed() => _disposed = true;

    public Task InitAsync(string elementId, double lat, double lon, int zoom,
        IReadOnlyList<HistoryBaseLayer> baseLayers, object dotNetRef)
        => InvokeSafe("initHistoryMap", elementId, lat, lon, zoom, baseLayers, dotNetRef);

    public Task DisposeMapAsync() => InvokeSafe("disposeHistoryMap");

    public Task SetViewAsync(double lat, double lon, int zoom)
        => InvokeSafe("setView", lat, lon, zoom);

    public Task SetBoatMarkerAsync(double lat, double lon, string color)
        => InvokeSafe("setBoatMarker", lat, lon, color);

    public Task AddChartLayerAsync(string id, string url, int minZoom, int maxZoom,
        double opacity, double[][]? leafletBounds)
        => InvokeSafe("addChartLayer", id, url, minZoom, maxZoom, opacity, leafletBounds);

    public Task RemoveChartLayerAsync(string id) => InvokeSafe("removeChartLayer", id);

    public Task RenderSegmentsAsync(IReadOnlyList<HistorySegmentRender.SegmentPayload> segments, bool fitBounds)
        => InvokeSafe("renderSegments", segments, fitBounds);

    public Task FitSegmentAsync(int segmentIndex) => InvokeSafe("fitSegment", segmentIndex);

    public Task SetPlaybackMarkerAsync(double lat, double lon, string color)
        => InvokeSafe("setPlaybackMarker", lat, lon, color);

    public Task MovePlaybackMarkerAsync(double lat, double lon)
        => InvokeSafe("movePlaybackMarker", lat, lon);

    /// <summary>Current viewport as <c>[south, west, north, east]</c>, or
    /// null when the map is not ready, the page is tearing down, or the
    /// JS side returned a malformed / non-finite tuple.</summary>
    public async Task<double[]?> GetViewportBoundsAsync()
    {
        if (_disposed) return null;
        try
        {
            var raw = await _module.InvokeAsync<double[]?>("getViewportBounds");
            return raw is { Length: 4 } && raw.All(double.IsFinite) ? raw : null;
        }
        catch (JSDisconnectedException) { return null; }
        catch (ObjectDisposedException) { return null; }
        catch (JSException) { return null; }
    }

    private async Task InvokeSafe(string identifier, params object?[] args)
    {
        if (_disposed) return;
        try
        {
            await _module.InvokeVoidAsync(identifier, args);
        }
        catch (JSDisconnectedException) { /* page is unmounting */ }
        catch (ObjectDisposedException) { /* JS module disposed first */ }
    }
}
