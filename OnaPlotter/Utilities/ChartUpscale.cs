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
///   <item>the <c>maxZoom: 19 + 3</c> literal on <c>L.map(...)</c> and the
///     two <c>L.tileLayer</c> base layers (OSM, OpenSeaMap) in
///     <c>leafletInterop.js</c>'s <c>initMap</c>: revert these to a
///     plain <c>maxZoom: 19</c> so the helm can't zoom past native
///     anymore. The <c>maxNativeZoom: 19</c> on the OSM / OpenSeaMap
///     layers can stay or go -- it's harmless when <c>maxZoom == 19</c>.</item>
/// </list>
/// Drop those, and the feature is gone with no leftover wiring.</para>
/// </summary>
public static class ChartUpscale
{
    /// <summary>Lowest levels value. <c>0</c> means "decorator no-ops"
    /// even when the master flag is on; useful for A/B comparison.</summary>
    public const int MinLevels = 0;

    /// <summary>Highest levels value. <c>3</c> = 8x upscale; visibly
    /// pixelated. <c>2</c> is the recommended default; <c>3</c> is
    /// here because some helms ask for it on harbour-detail charts
    /// despite the quality cliff.</summary>
    public const int MaxLevels = 3;

    /// <summary>Default levels when the feature is enabled.</summary>
    public const int DefaultLevels = 2;

    /// <summary>Clamp a levels value to the supported range. Used by
    /// the Settings setter as belt-and-suspenders against future
    /// callers / corrupted localStorage values.</summary>
    public static int ClampLevels(int value) =>
        Math.Clamp(value, MinLevels, MaxLevels);

    /// <summary>Resolve the effective overzoom levels given the
    /// master flag + the configured levels. Returns 0 when the
    /// master flag is off so call sites have one branch instead
    /// of two.</summary>
    public static int Effective(bool enabled, int levels) =>
        enabled ? ClampLevels(levels) : 0;
}
