namespace OnaPlotter.Services.Js;

/// <summary>
/// Typed C# wrapper over the leafletInterop.js "additional render
/// layers" surface: chart tile overlays, weather radar, the
/// speed-coloured own-track polyline, and the server-side track
/// history. These are the layers that ride on top of the base map but
/// are NOT user-placed objects (those live on
/// <see cref="IMapResourceJs"/>) and NOT the active-course / route
/// polylines (those live on <see cref="IMapRouteJs"/>).
/// </summary>
public interface IMapOverlaysJs
{
    // ---- Chart tile layers --------------------------------------------

    /// <summary>Add a chart tile overlay. <paramref name="bounds"/> is
    /// <c>[west, south, east, north]</c> or null when the chart spans
    /// the whole world. <paramref name="upscaleLevels"/> drives the
    /// chart-upscale decorator: <c>0</c> = no upscale (bare layer),
    /// <c>1..3</c> = let Leaflet GPU-upscale tiles fetched at
    /// <c>maxNativeZoom</c> up to <c>maxNativeZoom + upscaleLevels</c>.
    /// Limits + clamping live on
    /// <see cref="OnaPlotter.Utilities.ChartUpscale"/>.
    /// <para><paramref name="attribution"/> is the HTML credit Leaflet
    /// shows in its bottom-right corner while the layer is on the map.
    /// SK chart-server tiles ship empty ("" -- the SK server doesn't
    /// preach about the chart's source); the built-in OSM / OpenSeaMap
    /// layers ship the ODbL / CC-BY-SA-required credit + link string.</para></summary>
    Task AddChartLayerAsync(string id, string tileUrl, int minZoom, int maxZoom, double opacity, double[]? bounds, int upscaleLevels, string attribution);

    /// <summary>Remove a previously-added chart tile overlay.</summary>
    Task RemoveChartLayerAsync(string id);

    /// <summary>Push the chart-stack draw order so z-index on each
    /// layer matches the helm's panel ordering.</summary>
    Task SetChartLayerOrderAsync(string[] orderedIds);

    // ---- Weather overlay ---------------------------------------------

    /// <summary>Enable the precipitation-radar tile layer at the given
    /// opacity (0..1). Tile URL is the helm-configured RainViewer (or
    /// future) endpoint.</summary>
    Task SetWeatherOverlayAsync(string tileUrl, double opacity);

    /// <summary>Live-update opacity of the active weather overlay
    /// without a full layer rebuild. Slider drag tick path.</summary>
    Task SetWeatherOverlayOpacityAsync(double opacity);

    /// <summary>Drop the weather overlay layer entirely.</summary>
    Task ClearWeatherOverlayAsync();

    // ---- Own-track speed-coloured polyline ---------------------------

    /// <summary>Seed the speed-coloured own-track polyline from a
    /// snapshot of <c>TrackBuffer</c> points. Each entry is
    /// <c>[lat, lon, sogMs]</c>.</summary>
    Task SetColoredTrackAsync(double[][] points);

    // ---- Server-side track history -----------------------------------

    /// <summary>Render the server-fetched track history as a
    /// polyline. <paramref name="clipToBounds"/> tells the JS side to
    /// re-clip to the map viewport on pan / zoom without a re-fetch.</summary>
    Task SetServerTrackAsync(double[][] coords, bool clipToBounds);

    /// <summary>Toggle the JS-side viewport-clip filter without a
    /// re-fetch. The cached coord array is owned by the JS module.</summary>
    Task SetServerTrackClipToBoundsAsync(bool enabled);

    /// <summary>Drop the server-track polyline entirely.</summary>
    Task ClearServerTrackAsync();
}
