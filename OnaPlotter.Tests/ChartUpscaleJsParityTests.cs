using System.Text.RegularExpressions;
using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the JS twin of <see cref="ChartUpscale"/> to the C#-side
/// limits so the Settings input min/max, the C# clamp, and the JS
/// decorator can't drift. Same regex-scrape pattern as
/// <c>AisPaletteCssParityTests</c> + <c>RangeScaleJsParityTests</c>.
///
/// <para>The drift this catches: someone bumps <see cref="ChartUpscale.MaxLevels"/>
/// to 4 in C# (and the Settings input picks it up), but the JS
/// decorator still clamps at 3 -- the helm picks 4, the JS silently
/// reduces to 3, and the chart looks identical to "3" with no
/// indication anything's wrong. This test fails before that ships.</para>
/// </summary>
public class ChartUpscaleJsParityTests
{
    private static string ReadJsSource()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "OnaPlotter", "wwwroot", "js", "overzoomLayer.js");
        return File.ReadAllText(path);
    }

    [Test]
    public async Task WithOverzoom_FunctionPresent()
    {
        // Sanity: the JS-side decorator entry point must exist. If
        // a refactor renames or drops it, the parity surface catches
        // it before the addChartLayer call site silently stops
        // applying the overzoom wrap.
        var src = ReadJsSource();
        await Assert.That(src).Contains("export function withOverzoom(");
    }

    [Test]
    public async Task ClampRange_MatchesCsharpConstants()
    {
        // Scrape "Math.max(<min>, Math.min(<max>, levels | 0))" and
        // assert both bounds match the C# source of truth.
        // Example match: "Math.max(0, Math.min(3, levels | 0))".
        var src = ReadJsSource();
        var m = Regex.Match(src,
            @"Math\.max\(\s*(?<min>-?\d+)\s*,\s*Math\.min\(\s*(?<max>-?\d+)\s*,\s*levels");
        await Assert.That(m.Success)
            .IsTrue()
            .Because("withOverzoom must clamp `levels` with Math.max(min, Math.min(max, ...)); " +
                     "if the shape changes, update this parity test alongside the JS");

        int jsMin = int.Parse(m.Groups["min"].Value, System.Globalization.CultureInfo.InvariantCulture);
        int jsMax = int.Parse(m.Groups["max"].Value, System.Globalization.CultureInfo.InvariantCulture);

        await Assert.That(jsMin)
            .IsEqualTo(ChartUpscale.MinLevels)
            .Because($"JS clamp min ({jsMin}) drifted from ChartUpscale.MinLevels " +
                     $"({ChartUpscale.MinLevels}); update one to match the other");
        await Assert.That(jsMax)
            .IsEqualTo(ChartUpscale.MaxLevels)
            .Because($"JS clamp max ({jsMax}) drifted from ChartUpscale.MaxLevels " +
                     $"({ChartUpscale.MaxLevels}); update one to match the other");
    }

    [Test]
    public async Task ZeroIsNoOpFastPath()
    {
        // The decorator's "0 means structural copy" contract is what
        // lets ChartUpscale.Effective(false, _) return 0 and trust
        // that the JS won't alter maxZoom/maxNativeZoom. If a refactor
        // drops the early return, disabled-but-configured layers would
        // start triggering subtle option mutations.
        var src = ReadJsSource();
        await Assert.That(src).Contains("if (lv === 0)");
    }
}
