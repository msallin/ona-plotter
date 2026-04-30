using System.Text.RegularExpressions;
using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the L.map() viewport cap so a future tweak to
/// <see cref="ChartUpscale.MaxLevels"/> can't silently re-introduce
/// the bug PR #111 left behind: chart layers got wrapped with a
/// higher per-layer maxZoom but the L.map() instance stayed at
/// maxZoom 19, so Leaflet clamped the helm at 19 regardless of how
/// many upscale levels were configured. The viewport cap MUST be at
/// least 19 + <see cref="ChartUpscale.MaxLevels"/> so the user can
/// actually navigate past the native cap into the upscale band.
///
/// <para>The current JS form is intentionally arithmetic
/// (<c>maxZoom: 19 + 3</c>) so the relationship is visible in the
/// source. This test accepts either the arithmetic literal or a
/// pre-summed integer (someone might prefer the inline value with a
/// comment): both must equal 19 + MaxLevels.</para>
///
/// <para>Sibling parity test: <see cref="ChartUpscaleJsParityTests"/>
/// pins the matching JS-side clamp constants in
/// <c>overzoomLayer.js</c>. Together they catch drift across the
/// three surfaces (C# constants, JS map cap, JS decorator clamp).</para>
/// </summary>
public class MapMaxZoomJsParityTests
{
    /// <summary>OSM / OpenSeaMap public pyramids both top out at z19.
    /// Lifting either upstream would let us raise the native cap, but
    /// for now this is the constant the cap arithmetic builds on.</summary>
    private const int BaseLayerNativeMax = 19;

    private static string ReadJsSource()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "OnaPlotter", "wwwroot", "js", "leafletInterop.js");
        return File.ReadAllText(path);
    }

    [Test]
    public async Task MapMaxZoom_AccommodatesUpscaleCap()
    {
        // The L.map() block is the FIRST `maxZoom: ...,` literal in
        // the file. We accept two shapes:
        //   maxZoom: 22,
        //   maxZoom: 19 + 3,
        // The arithmetic form is preferred (relationship visible in
        // the source); the integer form is allowed in case someone
        // collapses it. Both must evaluate to BaseLayerNativeMax +
        // ChartUpscale.MaxLevels.
        var src = ReadJsSource();
        var m = Regex.Match(src,
            @"maxZoom:\s*(?:(?<a>\d+)\s*\+\s*(?<b>\d+)|(?<single>\d+))");
        await Assert.That(m.Success)
            .IsTrue()
            .Because("L.map() options block must declare a maxZoom literal " +
                     "(either `N + M` or a pre-summed integer); if the shape " +
                     "changed, update this test alongside the JS");

        int actual = m.Groups["single"].Success
            ? int.Parse(m.Groups["single"].Value, System.Globalization.CultureInfo.InvariantCulture)
            : int.Parse(m.Groups["a"].Value, System.Globalization.CultureInfo.InvariantCulture)
              + int.Parse(m.Groups["b"].Value, System.Globalization.CultureInfo.InvariantCulture);
        int expected = BaseLayerNativeMax + ChartUpscale.MaxLevels;

        await Assert.That(actual)
            .IsEqualTo(expected)
            .Because($"map.maxZoom in leafletInterop.js evaluates to {actual} but should be " +
                     $"{BaseLayerNativeMax} + ChartUpscale.MaxLevels ({ChartUpscale.MaxLevels}) = {expected}; " +
                     "the viewport cap must accommodate the deepest configured upscale level, " +
                     "otherwise Leaflet clamps the helm before the chart-upscale decorator can render");
    }

    [Test]
    public async Task MapMaxZoom_ArithmeticFormIsPreferred()
    {
        // Soft-pin the arithmetic shape so the relationship between
        // base-native cap and ChartUpscale.MaxLevels stays visible in
        // the JS source rather than collapsing to a magic 22. Failing
        // this test is OK if the integer form is intentional; updating
        // the test to allow it is fine. The point is to surface the
        // collapse, not to forbid it.
        var src = ReadJsSource();
        var m = Regex.Match(src, @"maxZoom:\s*(\d+)\s*\+\s*(\d+)");
        await Assert.That(m.Success)
            .IsTrue()
            .Because("Prefer `maxZoom: 19 + N` over `maxZoom: 22` so a future reader " +
                     "sees how the cap is composed (base native + upscale levels). " +
                     "If the integer form is intentional, relax this test alongside the JS.");
    }
}
