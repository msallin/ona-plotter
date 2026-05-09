namespace OnaPlotter.Utilities;

/// <summary>
/// "Nice round" nautical-mile picker for the chart range-scale chip
/// (corner) and the pinch-zoom preview chip (centre). Picks a value
/// at or below the live sample length so the rendered bar can never
/// claim more distance than the sample it represents, and caps the
/// pixel width at the sample width so a sub-floor zoom doesn't draw
/// a bar wider than the reference sample.
///
/// <para>The JS twin (<c>computeNiceScale</c> in
/// <c>leafletInterop.js</c>) uses identical ladder + label
/// formatting; <see cref="OnaPlotter.Tests.RangeScaleJsParityTests"/>
/// guards against drift. The C# port exists so the ladder + the
/// width-cap can be unit-tested without spinning up a Leaflet
/// runtime.</para>
/// </summary>
public static class RangeScale
{
    /// <summary>
    /// "Nice round" nautical-mile values. The same ladder dedicated
    /// chartplotter hardware uses on chart range scales. Below the
    /// smallest entry the picker falls through to the floor; above
    /// the largest, it sticks at the top.
    /// </summary>
    public static readonly double[] Ladder =
    [
        0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 20, 50, 100, 200, 500
    ];

    /// <summary>
    /// Result of a pick call: the chosen ladder value, the rendered
    /// label, and the pixel width to draw the bar at.
    /// </summary>
    public readonly record struct Pick(double NiceNm, string Label, int WidthPx);

    /// <summary>
    /// Pick a nice-round value at or below <paramref name="sampleNm"/>
    /// and the matching pixel width to render. The bar is clamped
    /// to <paramref name="sampleWidthPx"/> so a sub-floor zoom (where
    /// the live sample is tighter than 0.02 nm visible per
    /// reference width) doesn't draw a bar wider than the reference.
    /// Floored at <paramref name="minPx"/> so the bar stays
    /// scannable at very wide zooms.
    /// </summary>
    /// <param name="sampleNm">Live ground distance covered by the
    /// reference pixel sample at the map centre.</param>
    /// <param name="sampleWidthPx">Total pixels the sample spans.
    /// The corner chip uses 200; the pinch preview uses 200.</param>
    /// <param name="minPx">Visible-floor for the rendered bar so it
    /// doesn't collapse to invisible at very wide zooms.</param>
    public static Pick PickFor(double sampleNm, int sampleWidthPx, int minPx)
    {
        // Defensive: a non-positive or non-finite sample shouldn't
        // happen in practice (distanceTo on adjacent containerPoints
        // is > 0), but if it ever does we render the floor at minPx
        // rather than a NaN-driven crash.
        if (sampleNm <= 0 || !double.IsFinite(sampleNm))
        {
            return new Pick(Ladder[0], FormatLabel(Ladder[0]), minPx);
        }

        double nice = Ladder[0];
        for (int i = 0; i < Ladder.Length; i++)
        {
            if (Ladder[i] <= sampleNm) nice = Ladder[i];
        }

        int rawWidth = (int)Math.Round(sampleWidthPx * (nice / sampleNm));
        int widthPx = Math.Min(sampleWidthPx, Math.Max(minPx, rawWidth));
        return new Pick(nice, FormatLabel(nice), widthPx);
    }

    /// <summary>
    /// Trim trailing zeros and the orphan dot for sub-1 nm values
    /// ("0.50 nm" reads as fake precision; "0.5 nm" is what a
    /// chartplotter shows). >= 1 stays integer.
    /// </summary>
    private static string FormatLabel(double nice)
    {
        if (nice >= 1) return $"{nice:0} nm";
        // Same pattern as JS: toFixed(2) then strip trailing zeros
        // and a possibly-orphaned decimal point.
        var s = nice.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        s = s.TrimEnd('0').TrimEnd('.');
        return $"{s} nm";
    }
}
