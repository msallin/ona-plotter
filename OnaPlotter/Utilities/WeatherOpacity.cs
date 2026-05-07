namespace OnaPlotter.Utilities;

/// <summary>
/// Shared clamp limits + percent-to-fraction conversion for the
/// rain-radar overlay opacity. Single source of truth so the slider
/// (5-95 %), the C# setting (0.05-0.95), and the JS leaflet layer
/// (0.05-0.95) can't drift independently. Code review surfaced the
/// triple-clamp drift risk - if any of the three sites lowers the
/// floor without updating the others, the constraint becomes
/// silently inconsistent.
///
/// <para>The 5 % floor exists so the helm can never accidentally
/// land on "fully transparent" (visually identical to OFF, hides
/// the fact that the toggle is still on). The 95 % ceiling leaves
/// a bit of chart bleeding through so the helm doesn't lose the
/// underlying chart context entirely.</para>
/// </summary>
public static class WeatherOpacity
{
    /// <summary>Lower bound as a fraction (5 %).</summary>
    public const double MinFraction = 0.05;
    /// <summary>Upper bound as a fraction (95 %).</summary>
    public const double MaxFraction = 0.95;
    /// <summary>Default fraction (50 %) - matches the previous
    /// baked-in value before the slider shipped, so an existing
    /// install without a stored value keeps the same look.</summary>
    public const double DefaultFraction = 0.5;

    /// <summary>Lower bound as a slider percent (5).</summary>
    public const int MinPercent = 5;
    /// <summary>Upper bound as a slider percent (95).</summary>
    public const int MaxPercent = 95;

    /// <summary>Clamp a fraction to the allowed range. Used by
    /// AppSettingsService.SetWeatherOverlayOpacityAsync so a bad
    /// caller / future API path can't store an out-of-range value
    /// into localStorage.</summary>
    public static double ClampFraction(double value)
        => Math.Clamp(value, MinFraction, MaxFraction);

    /// <summary>Clamp a percent (slider integer) to the allowed
    /// range. Mirrors the slider's own min/max so a defensive
    /// re-clamp on the C# side guards against future paths that
    /// don't go through the slider.</summary>
    public static int ClampPercent(int percent)
        => Math.Clamp(percent, MinPercent, MaxPercent);

    /// <summary>Convert a slider percent to the leaflet-layer
    /// fraction. Includes the clamp so the math is one-stop -
    /// callers don't need to remember to clamp before dividing.
    /// </summary>
    public static double PercentToFraction(int percent)
        => ClampPercent(percent) / 100.0;
}
