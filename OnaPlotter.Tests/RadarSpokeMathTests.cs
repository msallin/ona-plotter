using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the C# port of the radar overlay's pure-decision helpers
/// (<c>radarLayer.js</c> _internal). The JS originals continue to
/// run in the render loop; these tests catch a drift between the
/// two implementations before it ships.
/// </summary>
public class RadarSpokeMathTests
{
    [Test]
    [Arguments(0, 4096, 0)]
    [Arguments(100, 4096, 100)]
    [Arguments(4095, 4096, 4095)]
    [Arguments(4096, 4096, 0)]
    [Arguments(4097, 4096, 1)]
    [Arguments(-1, 4096, 4095)]
    [Arguments(-4095, 4096, 1)]
    [Arguments(-4096, 4096, 0)]
    [Arguments(-4097, 4096, 4095)]
    public async Task WrapSpoke_PositiveModuloIntoRange(int i, int n, int expected)
    {
        // C#'s `%` preserves the dividend's sign (so -1 % 4096 = -1),
        // which would land an attacker-supplied or malformed spoke
        // index at a negative LUT offset. WrapSpoke restores the
        // mathematical [0, n) wrap.
        await Assert.That(RadarSpokeMath.WrapSpoke(i, n)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task WrapSpoke_NonPositiveN_ReturnsZero(int n)
    {
        // Defensive: spokes-per-revolution should always be > 0; a
        // legacy or buggy provider sending 0 would otherwise hit a
        // division-by-zero. Return 0 instead of throwing.
        await Assert.That(RadarSpokeMath.WrapSpoke(123, n)).IsEqualTo(0);
    }

    [Test]
    [Arguments(0.0, 4096, 0)]
    [Arguments(Math.PI / 2, 4096, 1024)]                // east -> 1024 of 4096
    [Arguments(Math.PI, 4096, 2048)]                    // south
    [Arguments(3 * Math.PI / 2, 4096, 3072)]            // west
    public async Task HeadingToSpokeOffset_CardinalCases(
        double headingRad, int spokes, int expected)
    {
        await Assert.That(RadarSpokeMath.HeadingToSpokeOffset(headingRad, spokes))
            .IsEqualTo(expected);
    }

    [Test]
    public async Task HeadingToSpokeOffset_ZeroSpokes_ReturnsZero()
    {
        await Assert.That(RadarSpokeMath.HeadingToSpokeOffset(1.5, 0)).IsEqualTo(0);
    }

    [Test]
    public async Task ParseHexRgba_ValidRgb_Parses()
    {
        var c = RadarSpokeMath.ParseHexRgba("#FF8040");
        await Assert.That(c).IsEqualTo(new RadarSpokeMath.Rgba(0xFF, 0x80, 0x40, 0xFF));
    }

    [Test]
    public async Task ParseHexRgba_ValidRgba_Parses()
    {
        var c = RadarSpokeMath.ParseHexRgba("#11223380");
        await Assert.That(c).IsEqualTo(new RadarSpokeMath.Rgba(0x11, 0x22, 0x33, 0x80));
    }

    [Test]
    public async Task ParseHexRgba_LowerCase_Parses()
    {
        // Case-insensitive on the hex digits; same byte values out.
        var c = RadarSpokeMath.ParseHexRgba("#abcdef");
        await Assert.That(c).IsEqualTo(new RadarSpokeMath.Rgba(0xAB, 0xCD, 0xEF, 0xFF));
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("FF8040")]      // missing #
    [Arguments("#FF80")]       // too short
    [Arguments("#FF80401122")] // too long
    [Arguments("#GGHHII")]     // non-hex chars
    [Arguments("#FF8040Z0")]   // partial non-hex in alpha
    public async Task ParseHexRgba_Invalid_ReturnsTransparent(string? input)
    {
        await Assert.That(RadarSpokeMath.ParseHexRgba(input))
            .IsEqualTo(RadarSpokeMath.Transparent);
    }

    [Test]
    public async Task ShouldSuppressLowReturn_NonNormalPixel_NeverSuppressed()
    {
        // Doppler / history / target-border markers ALWAYS render
        // regardless of colour or index. Otherwise an MARPA target's
        // border could vanish behind the noise filter - bad.
        await Assert.That(RadarSpokeMath.ShouldSuppressLowReturn("doppler", "#0000FF", 1, 4)).IsFalse();
        await Assert.That(RadarSpokeMath.ShouldSuppressLowReturn("history", "#0000FF", 1, 4)).IsFalse();
        await Assert.That(RadarSpokeMath.ShouldSuppressLowReturn("targetBorder", "#0000FF", 1, 4)).IsFalse();
        await Assert.That(RadarSpokeMath.ShouldSuppressLowReturn(null, "#0000FF", 1, 4)).IsFalse();
    }

    [Test]
    public async Task ShouldSuppressLowReturn_NormalBelowMediumReturn_Suppressed()
    {
        // Index 1 < mediumReturn 4: legend itself flags as sea clutter.
        // Colour irrelevant here - metadata wins.
        await Assert.That(RadarSpokeMath.ShouldSuppressLowReturn("normal", "#FF0000", 1, 4)).IsTrue();
        await Assert.That(RadarSpokeMath.ShouldSuppressLowReturn("normal", "#FF0000", 3, 4)).IsTrue();
    }

    [Test]
    public async Task ShouldSuppressLowReturn_NormalAtMediumReturn_NotSuppressedByMetadata()
    {
        // Index == mediumReturn is the threshold: NOT suppressed by
        // the metadata branch (the JS uses `<`, not `<=`). Colour
        // check still applies.
        // Red-dominant -> not suppressed.
        await Assert.That(RadarSpokeMath.ShouldSuppressLowReturn("normal", "#FF0000", 4, 4)).IsFalse();
        // Blue-dominant -> still suppressed by colour fallback.
        await Assert.That(RadarSpokeMath.ShouldSuppressLowReturn("normal", "#0000FF", 4, 4)).IsTrue();
    }

    [Test]
    public async Task ShouldSuppressLowReturn_BlueDominant_Suppressed()
    {
        // Pure blue + blue-cyan + navy: B is strictly greater than
        // both R and G. Cyan (B == G) is the boundary case, pinned
        // separately below.
        await Assert.That(RadarSpokeMath.ShouldSuppressLowReturn("normal", "#0000FF", 99, null)).IsTrue();
        await Assert.That(RadarSpokeMath.ShouldSuppressLowReturn("normal", "#0080FF", 99, null)).IsTrue();
        await Assert.That(RadarSpokeMath.ShouldSuppressLowReturn("normal", "#000080", 99, null)).IsTrue();
    }

    [Test]
    public async Task ShouldSuppressLowReturn_BlueExactlyEqualToOther_NotSuppressed()
    {
        // The JS check is strict `>`: cyan (R=0, G=255, B=255) has
        // B == G, so blue is NOT strictly dominant and the pixel
        // renders normally. Pin the strict-greater semantic.
        await Assert.That(RadarSpokeMath.ShouldSuppressLowReturn("normal", "#00FFFF", 99, null)).IsFalse();
    }

    [Test]
    public async Task ShouldSuppressLowReturn_RedDominant_NotSuppressed()
    {
        // Strong-echo red (and orange / yellow) must always render.
        await Assert.That(RadarSpokeMath.ShouldSuppressLowReturn("normal", "#FF0000", 99, null)).IsFalse();
        await Assert.That(RadarSpokeMath.ShouldSuppressLowReturn("normal", "#FF8000", 99, null)).IsFalse();
        await Assert.That(RadarSpokeMath.ShouldSuppressLowReturn("normal", "#FFFF00", 99, null)).IsFalse();
    }

    [Test]
    public async Task ShouldSuppressLowReturn_NoLegendMedium_FallsBackToColour()
    {
        // legendMediumReturn=null -> only the colour rule applies.
        await Assert.That(RadarSpokeMath.ShouldSuppressLowReturn("normal", "#0000FF", 1, null)).IsTrue();
        await Assert.That(RadarSpokeMath.ShouldSuppressLowReturn("normal", "#FFFFFF", 1, null)).IsFalse();
    }

    [Test]
    public async Task ShouldSuppressLowReturn_LegendMediumZero_NoIndexBranch()
    {
        // legendMediumReturn=0 means the `index >= 1 && index < 0`
        // expression is always false, so the metadata branch never
        // fires; only the colour rule decides. Pin the strict-less-
        // than boundary so a refactor to `index <= medium` doesn't
        // shift the cutoff silently.
        await Assert.That(RadarSpokeMath.ShouldSuppressLowReturn("normal", "#FF0000", 0, 0)).IsFalse();
        await Assert.That(RadarSpokeMath.ShouldSuppressLowReturn("normal", "#FF0000", 1, 0)).IsFalse();
        // Blue-dominant still suppressed via the colour fallback.
        await Assert.That(RadarSpokeMath.ShouldSuppressLowReturn("normal", "#0000FF", 1, 0)).IsTrue();
    }

    [Test]
    public async Task ParseHexRgba_ZeroAlpha_DistinguishedFromError()
    {
        // The Transparent constant is Rgba(0,0,0,0), but a valid
        // 8-digit parse with zero alpha should NOT collapse to
        // Transparent if RGB differ - that's the difference
        // between "parsed and intentionally transparent" and
        // "parser failed and returned the sentinel". Pin the
        // distinction so a future refactor of the parser's failure
        // path can't conflate them.
        var c = RadarSpokeMath.ParseHexRgba("#11223300");
        await Assert.That(c).IsEqualTo(new RadarSpokeMath.Rgba(0x11, 0x22, 0x33, 0x00));
        await Assert.That(c).IsNotEqualTo(RadarSpokeMath.Transparent);
    }
}
