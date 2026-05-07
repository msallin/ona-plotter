namespace OnaPlotter.Utilities;

/// <summary>
/// Shared limits + clamp logic for the chart-upscale (overzoom)
/// feature. Single source of truth so the Settings slider, the C#
/// setting, and the JS decorator all bound the same way.
///
/// <para>The feature lets the helm zoom past a chart's native max
/// by GPU-upscaling tiles at the native cap. <c>+2</c> levels (4x
/// upscale) is the sweet spot per the design review: pixelated but
/// still readable for "is that rock close?" close-quarters work.
/// <c>+3</c> (8x) crosses into mush. <c>0</c> disables upscaling
/// even when the master flag is on, which is the path tests rely
/// on for the no-op case.</para>
///
/// <para>Removable contract (per the design review): if the feature
/// gets ripped out, the entire surface is:
/// <list type="bullet">
///   <item>this file (<c>OnaPlotter/Utilities/ChartUpscale.cs</c>),</item>
///   <item>the JS decorator (<c>OnaPlotter/wwwroot/js/overzoomLayer.js</c>) +
///     its import + call site in <c>leafletInterop.js</c>,</item>
///   <item>the <c>ChartUpscaleEnabled</c> + <c>ChartUpscaleLevels</c>
///     properties (and their setters) on <c>IMapDisplaySettings</c> /
///     <c>AppSettingsService</c>,</item>
///   <item>the persistence keys <c>chartUpscaleEnabled.v1</c> +
///     <c>chartUpscaleLevels.v1</c> in <c>AppSettingsService.InitializeAsync</c>,</item>
///   <item>the Settings UI block in <c>Components/Pages/Settings.razor</c>
///     (checkbox + levels input + the two change handlers),</item>
///   <item>the <c>upscaleLevels</c> arg on <c>IMapOverlaysJs.AddChartLayerAsync</c>
///     and the <c>ChartLayerController</c> call site that resolves it,</item>
///   <item>the <c>maxZoom: 19 + 3</c> literal on the <c>L.map(...)</c>
///     call in <c>leafletInterop.js</c>'s <c>initMap</c>: revert to
///     a plain <c>maxZoom: 19</c> so the helm can't zoom past native.
///     OSM / OpenSeaMap layers already use <c>maxZoom: 19</c> -
///     deliberately not bumped, so they go blank past native and
///     don't compete with the upscaled chart, which means there's
///     nothing to revert on the base layers.</item>
/// </list>
/// Drop those, and the feature is gone with no leftover wiring.</para>
/// </summary>
public static class ChartUpscale
{
    /// <summary>Lowest levels value. <c>0</c> means "decorator no-ops"
    /// even when the master flag is on; useful for A/B comparison.</summary>
    public const int MinLevels = 0;

    /// <summary>Highest levels value. <c>5</c> = 32x upscale. Visibly
    /// pixelated above <c>3</c> (8x); past that you're really just
    /// asking Leaflet to bilinear-interpolate further. <c>2</c> stays
    /// the recommended default for general use; <c>3-5</c> is here
    /// for helms doing harbour-detail / pilotage who want to drill
    /// into a chart well past its native max.</summary>
    public const int MaxLevels = 5;

    /// <summary>Default levels when the feature is enabled.</summary>
    public const int DefaultLevels = 2;

    /// <summary>Clamp a levels value to the supported range. Used by
    /// the Settings setter as belt-and-suspenders against future
    /// callers / corrupted localStorage values.</summary>
    public static int ClampLevels(int value) =>
        Math.Clamp(value, MinLevels, MaxLevels);

    /// <summary>Resolve the effective overzoom levels given the
    /// master flag + the configured levels. Returns 0 when the
    /// master flag is off, otherwise the clamped level. Named so
    /// the call site reads as one resolution step (the chart-level
    /// AllowUpscale opt-out is a separate concern wrapped at the
    /// caller).</summary>
    public static int Effective(bool enabled, int levels) =>
        enabled ? ClampLevels(levels) : 0;
}
