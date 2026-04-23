using System.Text.RegularExpressions;
using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

/// <summary>
/// Guards against drift between <see cref="AisPalette"/> (C# source of
/// truth for on-chart glyph colour) and the <c>--map-ais-*</c> tokens
/// in <c>wwwroot/css/app.css</c>, which the Layers-panel legend swatches
/// read via <c>var()</c>.
/// <para>
/// Previously the legend swatches had the hex values hardcoded and
/// would silently drift whenever someone tuned a palette entry on the
/// C# side. The tokens now live in one place; this test makes the
/// parity explicit so a tuning change that forgets to update the CSS
/// (or vice versa) fails in CI instead of landing with a subtly-wrong
/// legend.
/// </para>
/// </summary>
public class AisPaletteCssParityTests
{
    private static readonly string CssPath = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..",
        "OnaPlotter", "wwwroot", "css", "app.css");

    [Test]
    public async Task LegendAisTokens_MatchAisPaletteConstants()
    {
        string css;
        try { css = File.ReadAllText(CssPath); }
        catch (FileNotFoundException)
        {
            // Test assembly was copied without the repo layout (e.g.
            // `dotnet publish`). Skip rather than fail; the CI run
            // from the repo root always sees the file.
            return;
        }

        // Expected: C# constant -> CSS token name. Add to this dict
        // when a new AisPalette entry earns a legend swatch.
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { AisPalette.Sailing,    "--map-ais-sail" },
            { AisPalette.Fishing,    "--map-ais-fish" },
            { AisPalette.Cargo,      "--map-ais-commercial" },
        };

        foreach (var (palette, token) in expected)
        {
            // Match "--map-ais-sail: #e28862;" tolerating whitespace.
            var m = Regex.Match(css,
                $@"{Regex.Escape(token)}\s*:\s*(#[0-9a-fA-F]{{6}})\s*;",
                RegexOptions.IgnoreCase);
            await Assert.That(m.Success)
                .IsTrue()
                .Because($"CSS token {token} not found in app.css");
            string cssHex = m.Groups[1].Value.ToLowerInvariant();
            string paletteHex = palette.ToLowerInvariant();
            await Assert.That(cssHex)
                .IsEqualTo(paletteHex)
                .Because($"{token} in app.css ({cssHex}) drifted from " +
                         $"AisPalette ({paletteHex}); update one to match the other");
        }
    }
}
