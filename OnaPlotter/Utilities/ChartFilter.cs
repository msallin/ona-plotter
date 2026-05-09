namespace OnaPlotter.Utilities;

/// <summary>
/// Pure helper for the chart-display CSS filter (contrast / saturation
/// / brightness) the helm tunes from the layers panel. Raster chart
/// PNGs sometimes wash out at noon or look muddy on a sunlit screen;
/// the helm boosts contrast + saturation to read them. The slider values
/// are stored as integer percentages (default 100 = identity, range
/// pinned by <see cref="MinPercent"/> / <see cref="MaxBrightness"/>);
/// JS converts to the CSS <c>contrast(...) saturate(...) brightness(...)</c>
/// shorthand applied to each chart tile-layer container.
///
/// <para>Pure functions only - no JS interop, no IO. Pinned by
/// ChartFilterTests so the chart tile pipeline can rely on the clamp
/// + identity-shortcut behaviour without re-deriving it.</para>
/// </summary>
public static class ChartFilter
{
    /// <summary>Lower bound for every channel. Below 50 the chart
    /// reads as a smear and the helm can't tell apart chart features
    /// from background noise - worse than the default washout.</summary>
    public const int MinPercent = 50;

    /// <summary>Upper bound for contrast + saturation. Past 200 the
    /// chart oversaturates into clown-paint colours that hide the
    /// printed ink rather than enhance it.</summary>
    public const int MaxContrastSaturation = 200;

    /// <summary>Upper bound for brightness. Past 150 the chart blooms
    /// out and the depth contours fade - the opposite of helpful in
    /// daylight.</summary>
    public const int MaxBrightness = 150;

    /// <summary>Default for every channel (identity, no filter).</summary>
    public const int DefaultPercent = 100;

    /// <summary>Clamp a contrast OR saturation slider value into the
    /// supported range; non-finite / NaN slips back to default rather
    /// than disabling the chart filter pipeline silently.</summary>
    public static int ClampContrastSaturation(int value)
    {
        if (value < MinPercent) return MinPercent;
        if (value > MaxContrastSaturation) return MaxContrastSaturation;
        return value;
    }

    /// <summary>Clamp a brightness slider value into the supported range.</summary>
    public static int ClampBrightness(int value)
    {
        if (value < MinPercent) return MinPercent;
        if (value > MaxBrightness) return MaxBrightness;
        return value;
    }

    /// <summary>True when every channel is at the identity default and
    /// no CSS filter needs to be applied (skip the JS hop entirely).
    /// Lets the JS side clear <c>style.filter</c> rather than parking
    /// an inert <c>contrast(1) saturate(1) brightness(1)</c> string
    /// that browsers still hand to the compositor.</summary>
    public static bool IsIdentity(int contrastPct, int saturationPct, int brightnessPct) =>
        contrastPct == DefaultPercent
        && saturationPct == DefaultPercent
        && brightnessPct == DefaultPercent;

    /// <summary>
    /// Format the CSS filter string for the given percentages.
    /// Returns the empty string when every channel is at identity -
    /// the JS side reads that as "clear the inline style.filter".
    /// <para>
    /// Inputs are clamped here as well as at the setters, so a JS-
    /// pushed override or a corrupted localStorage value can't drive
    /// the compositor with an out-of-range filter string.</para>
    /// <para>
    /// Output shape (example for 130 / 110 / 95):
    /// <c>contrast(1.30) saturate(1.10) brightness(0.95)</c>
    /// </para>
    /// </summary>
    public static string Format(int contrastPct, int saturationPct, int brightnessPct)
    {
        int c = ClampContrastSaturation(contrastPct);
        int s = ClampContrastSaturation(saturationPct);
        int b = ClampBrightness(brightnessPct);
        if (IsIdentity(c, s, b)) return "";
        // Invariant culture so a German-locale boot ("," decimal sep)
        // doesn't emit "contrast(1,30)" - CSS parsers reject the
        // comma form and the entire filter falls off.
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        return $"contrast({(c / 100.0).ToString("0.00", ic)}) "
             + $"saturate({(s / 100.0).ToString("0.00", ic)}) "
             + $"brightness({(b / 100.0).ToString("0.00", ic)})";
    }
}
