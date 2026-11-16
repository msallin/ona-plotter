namespace OnaPlotter.Utilities;

/// <summary>
/// Maps a speed-over-ground (m/s) into a CSS rgb(...) string for
/// track-segment colouring. Track renderers group points by
/// <see cref="SpeedBuckets.Bucket"/> and colour each run with
/// <see cref="BucketPalette"/>, which the map receives once at
/// <c>initMap</c>; the JS side only indexes the palette. Tests pin the
/// exact rgb tuples at the boundary speeds (0 / 3 / 6 / 8+ knots).
///
/// <para>Two-stage linear interpolation:
/// <list type="bullet">
///   <item>0-3 knots (t in [0, 0.5]): blue (59,130,246) -&gt; green (34,197,94)</item>
///   <item>3-8 knots (t in [0.5, 1.0]): green (34,197,94) -&gt; yellow (234,179,8)</item>
///   <item>&gt;=8 knots: clamps at the yellow endpoint.</item>
/// </list></para>
///
/// <para>Night mode swaps in <see cref="NightBucketPalette"/>: the same
/// ramp shape in the red family (dim crimson -&gt; red -&gt; amber), so
/// the speed gradient survives without a per-path CSS filter.</para>
/// </summary>
public static class SpeedColor
{
    /// <summary>Default colour when SOG is unknown (matches the
    /// "no data" blue used elsewhere on the chart).</summary>
    public const string DefaultRgb = "#3b82f6";

    /// <summary>Track colour per speed bucket: entry <c>i</c> is the
    /// colour of <see cref="SpeedBuckets.Buckets"/>[i], the bucket's
    /// lower bound. Indexed by the bucket ids that the own-track runs,
    /// live track points and server-track samples carry.</summary>
    public static readonly string[] BucketPalette =
        SpeedBuckets.Buckets.Select(b => Rgb(b)).ToArray();

    /// <summary>Night-mode colour when SOG is unknown.</summary>
    public const string NightDefaultRgb = "rgb(150,40,40)";

    /// <summary>Night-mode counterpart of <see cref="BucketPalette"/>.</summary>
    public static readonly string[] NightBucketPalette =
        SpeedBuckets.Buckets.Select(b => NightRgb(b)).ToArray();

    /// <summary>The palette the map should draw tracks with.</summary>
    public static string[] PaletteFor(bool nightMode) =>
        nightMode ? NightBucketPalette : BucketPalette;

    /// <summary>CSS rgb(r,g,b) string for the given SOG in m/s.
    /// Returns <see cref="DefaultRgb"/> when sog is null.</summary>
    public static string Rgb(double? sogMs) =>
        sogMs is null ? DefaultRgb : Ramp(sogMs.Value, (59, 130, 246), (34, 197, 94), (234, 179, 8));

    /// <summary>Night-mode rgb(r,g,b) for the given SOG in m/s: dim
    /// crimson (stopped) -&gt; red (4 kn) -&gt; amber (8+ kn).
    /// Returns <see cref="NightDefaultRgb"/> when sog is null.</summary>
    public static string NightRgb(double? sogMs) =>
        sogMs is null ? NightDefaultRgb : Ramp(sogMs.Value, (110, 30, 45), (190, 45, 35), (235, 120, 45));

    // Two-stage linear ramp over 0..8 kn: start -> mid at 4 kn, mid ->
    // end at 8 kn, clamped above.
    private static string Ramp(double sogMs, (int R, int G, int B) start,
        (int R, int G, int B) mid, (int R, int G, int B) end)
    {
        double kn = sogMs * Format.MsToKnots;
        double t = Math.Min(kn / 8.0, 1.0);
        var (from, to, f) = t < 0.5 ? (start, mid, t * 2.0) : (mid, end, (t - 0.5) * 2.0);
        int r = (int)Math.Round(from.R + f * (to.R - from.R));
        int g = (int)Math.Round(from.G + f * (to.G - from.G));
        int b = (int)Math.Round(from.B + f * (to.B - from.B));
        return $"rgb({r},{g},{b})";
    }
}
