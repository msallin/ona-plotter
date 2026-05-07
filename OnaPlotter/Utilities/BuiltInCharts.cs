using OnaPlotter.Models;

namespace OnaPlotter.Utilities;

/// <summary>
/// Synthesises the OpenStreetMap + OpenSeaMap chart descriptors that
/// were previously hardcoded as Leaflet base layers in
/// <c>leafletInterop.js</c>'s <c>initMap</c>. They flow through the
/// regular chart pipeline (<c>ChartApi.GetAllAsync</c> →
/// <c>ChartLayerController.ToggleAsync</c> →
/// <c>IMapOverlaysJs.AddChartLayerAsync</c>) so the helm gets the
/// same uniform control surface (toggle, reorder, quick-bar, opacity
/// stacking) over them as over SignalK-served charts. Tiles still
/// fetch from the original internet URLs - not from the SignalK
/// server - to preserve the offline-friendly fallback behaviour
/// (the boat doesn't need to host a basemap tileset).
///
/// <para>Identifiers (<c>osm</c>, <c>openseamap</c>) are deliberately
/// short and lowercase so they survive any future SK chart server
/// that happens to also publish a chart with the same name - a
/// real SK chart would be prefixed with the plugin's namespace
/// (<c>signalk-charts-foo:osm</c>), so collisions are unlikely.
/// Both charts disable upscale (<see cref="SignalkChart.AllowUpscale"/>
/// = false) because helm field-tested that an upscaled basemap on
/// top of a GPU-upscaled SignalK chart reads as a flicker.</para>
/// </summary>
public static class BuiltInCharts
{
    /// <summary>Chart identifier for OSM. Used as the persistence
    /// key in <c>EnabledChartIds</c>; do not change once shipped or
    /// existing helms lose their basemap selection on next load.</summary>
    public const string OsmIdentifier = "osm";

    /// <summary>Chart identifier for OpenSeaMap (transparent seamark
    /// overlay). Same persistence-key contract as
    /// <see cref="OsmIdentifier"/>.</summary>
    public const string OpenSeaMapIdentifier = "openseamap";

    /// <summary>The two synthetic charts that replace the previous
    /// hardcoded base layers. Returned as an immutable read-only list
    /// so callers can foreach without surprising a downstream
    /// modification.</summary>
    public static IReadOnlyList<SignalkChart> All { get; } =
    [
        new SignalkChart
        {
            Identifier = OsmIdentifier,
            Name = "OpenStreetMap",
            Description = "Worldwide road / coastline basemap.",
            Url = "https://tile.openstreetmap.org/{z}/{x}/{y}.png",
            MinZoom = 1,
            MaxZoom = 19,
            Bounds = null,
            AllowUpscale = false,
            // ODbL: short visible text + link to the canonical
            // copyright page (which lists contributors). The
            // conventional "contributors" word is dropped to keep
            // the chip narrow at the chart edge.
            Attribution = "<a href=\"https://www.openstreetmap.org/copyright\" target=\"_blank\" rel=\"noreferrer\">&copy; OpenStreetMap</a>",
            // Trusted: this string is hardcoded inside the WASM
            // bundle; AttributionSanitizer passes it through to
            // Leaflet's innerHTML sink unchanged so the ODbL credit
            // renders as a clickable link.
            IsTrustedAttribution = true,
            Opacity = 1.0,
        },
        new SignalkChart
        {
            Identifier = OpenSeaMapIdentifier,
            Name = "OpenSeaMap",
            Description = "Seamark / nav-aid overlay (buoys, lights, harbours).",
            Url = "https://tiles.openseamap.org/seamark/{z}/{x}/{y}.png",
            MinZoom = 1,
            MaxZoom = 19,
            Bounds = null,
            AllowUpscale = false,
            Attribution = "<a href=\"https://www.openseamap.org/\" target=\"_blank\" rel=\"noreferrer\">&copy; OpenSeaMap</a>",
            // Trusted (same rationale as the OSM entry above).
            IsTrustedAttribution = true,
            // 0.8 opacity so the seamark glyphs blend over whatever
            // basemap (OSM / SignalK chart) is below. Mirrors the
            // value the previous hardcoded seaBaseLayer used.
            Opacity = 0.8,
        },
    ];

    /// <summary>True when an identifier matches one of the synthetic
    /// charts. Used by tests and by call sites that need to short-
    /// circuit "this is a built-in" logic without iterating
    /// <see cref="All"/>.</summary>
    public static bool IsBuiltIn(string id) =>
        id == OsmIdentifier || id == OpenSeaMapIdentifier;
}
