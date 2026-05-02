namespace OnaPlotter.Utilities;

/// <summary>
/// Pure decision logic for the radar overlay. Mirrors the helpers
/// in <c>OnaPlotter/wwwroot/js/radarLayer.js</c> so the same rules
/// can be exercised from C# tests without standing up a canvas /
/// Leaflet stub. The JS originals stay because the render loop
/// reads them per-spoke at frame rate; reaching back to C# via
/// interop on every spoke would dwarf any structural win.
///
/// <para>Per the project rule (decisions in C#, JS as a thin
/// renderer), the C# functions here are the canonical reference for
/// the behaviour even though the JS continues to do the work. A
/// regression in either side that drifts the result is one
/// parameterised test away from going red.</para>
/// </summary>
public static class RadarSpokeMath
{
    /// <summary>RGBA quadruple, byte-valued. Public so tests can
    /// assert specific values; production callers receive plain
    /// <c>byte[4]</c> arrays from the parser.</summary>
    public readonly record struct Rgba(byte R, byte G, byte B, byte A);

    /// <summary>Fully transparent pixel; returned by the parsers
    /// for null / unrecognised inputs. Mirrors the JS
    /// <c>TRANSPARENT</c> constant.</summary>
    public static readonly Rgba Transparent = new(0, 0, 0, 0);

    /// <summary>
    /// Defensive positive modulo into <c>[0, n)</c>. C#'s <c>%</c>
    /// already wraps signed-int dividends sign-preservingly, so a
    /// non-conforming provider sending negative spoke indices would
    /// produce a negative result and an out-of-bounds LUT read
    /// downstream. Mirrors <c>wrapSpoke</c> in radarLayer.js. Cheap
    /// to guard, expensive to diagnose.
    /// </summary>
    public static int WrapSpoke(int i, int n)
    {
        if (n <= 0) return 0;
        int r = i % n;
        return r < 0 ? r + n : r;
    }

    /// <summary>
    /// Convert the boat's heading (radians, <c>0..2π</c> from true
    /// north) into the spoke-index offset that aligns a bow-relative
    /// <c>angle</c> field onto the canvas's north-up grid.
    /// <paramref name="spokesPerRevolution"/> sets the discretisation
    /// (typical Navico HALO: 2048 or 4096).
    /// </summary>
    public static int HeadingToSpokeOffset(double headingRad, int spokesPerRevolution)
    {
        if (spokesPerRevolution <= 0) return 0;
        return (int)Math.Round(headingRad * spokesPerRevolution / (2 * Math.PI));
    }

    /// <summary>
    /// Parse a hex colour string (<c>#RRGGBB</c> or <c>#RRGGBBAA</c>)
    /// into an Rgba quadruple. Returns <see cref="Transparent"/> on
    /// any parse failure: a malformed legend entry should render as
    /// "no echo" rather than crash the frame. Matches
    /// <c>parseHexRgba</c> in radarLayer.js.
    /// </summary>
    public static Rgba ParseHexRgba(string? hex)
    {
        if (string.IsNullOrEmpty(hex) || hex[0] != '#') return Transparent;
        if (hex.Length == 7)
        {
            if (TryParseHexByte(hex.AsSpan(1, 2), out byte r)
                && TryParseHexByte(hex.AsSpan(3, 2), out byte g)
                && TryParseHexByte(hex.AsSpan(5, 2), out byte b))
            {
                return new Rgba(r, g, b, 255);
            }
            return Transparent;
        }
        if (hex.Length == 9)
        {
            if (TryParseHexByte(hex.AsSpan(1, 2), out byte r)
                && TryParseHexByte(hex.AsSpan(3, 2), out byte g)
                && TryParseHexByte(hex.AsSpan(5, 2), out byte b)
                && TryParseHexByte(hex.AsSpan(7, 2), out byte a))
            {
                return new Rgba(r, g, b, a);
            }
            return Transparent;
        }
        return Transparent;
    }

    /// <summary>
    /// Decide whether a legend entry should be rendered transparent.
    /// Mirrors <c>shouldSuppressLowReturn</c> in radarLayer.js. The
    /// rule combines two checks for normal pixels: (1) byte indices
    /// 1..mediumReturn-1 are flagged as sea clutter by the legend
    /// itself, regardless of colour; (2) any normal pixel where blue
    /// dominates the other channels is treated as low-return because
    /// HALO's blue-cyan-blue-green ramp visually reads as noise even
    /// when the metadata stops flagging it. Doppler / history /
    /// target-border markers are exempt (gated on type=normal).
    /// </summary>
    /// <param name="pixelType">Legend pixel type: "normal" / "doppler"
    /// / "history" / "targetBorder" / null. Only "normal" is suppressed.</param>
    /// <param name="pixelColor">Hex colour string for the legend
    /// entry (or null if not present).</param>
    /// <param name="index">Byte-table index of this entry.</param>
    /// <param name="legendMediumReturn">Legend's threshold below which
    /// a normal pixel is classified as sea clutter, or null when the
    /// legend doesn't publish the field.</param>
    public static bool ShouldSuppressLowReturn(
        string? pixelType, string? pixelColor, int index, int? legendMediumReturn)
    {
        if (pixelType != "normal") return false;
        if (legendMediumReturn is int medium
            && index >= 1
            && index < medium) return true;
        var rgba = ParseHexRgba(pixelColor);
        return rgba.B > rgba.R && rgba.B > rgba.G;
    }

    private static bool TryParseHexByte(ReadOnlySpan<char> s, out byte b)
    {
        return byte.TryParse(s, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out b);
    }
}
