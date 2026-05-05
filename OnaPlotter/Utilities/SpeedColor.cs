namespace OnaPlotter.Utilities;

/// <summary>
/// Maps a speed-over-ground (m/s) into a CSS rgb(...) string for
/// track-segment colouring, plus a discrete bucket index used to
/// group consecutive same-speed segments so the renderer doesn't
/// emit one polyline per fix. JS layer mirrors this via
/// <c>wwwroot/js/format.js</c>; tests here pin the exact rgb tuples
/// at the boundary speeds (0 / 3 / 6 / 8+ knots) so a future tweak
/// to the ramp updates the JS in lockstep with C#.
///
/// <para>Two-stage linear interpolation:
/// <list type="bullet">
///   <item>0-3 knots (t in [0, 0.5]): blue (59,130,246) -&gt; green (34,197,94)</item>
///   <item>3-8 knots (t in [0.5, 1.0]): green (34,197,94) -&gt; yellow (234,179,8)</item>
///   <item>&gt;=8 knots: clamps at the yellow endpoint.</item>
/// </list></para>
/// </summary>
public static class SpeedColor
{
    /// <summary>Default colour when SOG is unknown (matches the
    /// "no data" blue used elsewhere on the chart).</summary>
    public const string DefaultRgb = "#3b82f6";

    /// <summary>Per-bucket lower bound in m/s. Index into this table
    /// is the bucket id used by <see cref="Bucket"/> -- 0 = stopped,
    /// 5 = fast. Matches the JS SPEED_BUCKETS constant.</summary>
    public static readonly double[] Buckets = [0, 1, 2, 3, 5, 8];

    /// <summary>Bucket index (0..5) for grouping consecutive same-
    /// speed segments. Returns the highest index whose threshold
    /// is &lt;= sog, or 0 when sog is null.</summary>
    public static int Bucket(double? sogMs)
    {
        if (sogMs is null) return 0;
        for (int i = Buckets.Length - 1; i >= 0; i--)
        {
            if (sogMs.Value >= Buckets[i]) return i;
        }
        return 0;
    }

    /// <summary>CSS rgb(r,g,b) string for the given SOG in m/s.
    /// Returns <see cref="DefaultRgb"/> when sog is null.</summary>
    public static string Rgb(double? sogMs)
    {
        if (sogMs is null) return DefaultRgb;
        double kn = sogMs.Value * Format.MsToKnots;
        double t = Math.Min(kn / 8.0, 1.0);
        if (t < 0.5)
        {
            double f = t * 2.0;
            int r = (int)Math.Round(59 + f * (34 - 59));
            int g = (int)Math.Round(130 + f * (197 - 130));
            int b = (int)Math.Round(246 + f * (94 - 246));
            return $"rgb({r},{g},{b})";
        }
        else
        {
            double f = (t - 0.5) * 2.0;
            int r = (int)Math.Round(34 + f * (234 - 34));
            int g = (int)Math.Round(197 + f * (179 - 197));
            int b = (int)Math.Round(94 + f * (8 - 94));
            return $"rgb({r},{g},{b})";
        }
    }
}
