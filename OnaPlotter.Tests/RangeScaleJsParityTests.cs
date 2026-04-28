namespace OnaPlotter.Tests;

/// <summary>
/// Pins the JS twin of <see cref="OnaPlotter.Utilities.RangeScale"/>
/// to the C#-side ladder + label rules so a CSS-side palette tweak
/// or a ladder edit can't drift between the corner range chip and
/// the C#-side helper. Same regex-scrape pattern as
/// <c>AisPaletteCssParityTests</c>.
/// </summary>
public class RangeScaleJsParityTests
{
    private static string ReadJsSource()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "OnaPlotter", "wwwroot", "js", "leafletInterop.js");
        return File.ReadAllText(path);
    }

    [Test]
    public async Task LadderArrayMatches()
    {
        // Scrape the JS-side ladder literal and assert each value
        // matches the C# constant. Drift here = the corner chip and
        // the pinch preview pick a different "nice round" value than
        // the C# helper would, which is the precise drift the parity
        // test exists to catch.
        var src = ReadJsSource();
        const string marker = "const RANGE_SCALE_NM_LADDER = [";
        int start = src.IndexOf(marker, StringComparison.Ordinal);
        await Assert.That(start).IsGreaterThanOrEqualTo(0);
        int end = src.IndexOf("];", start, StringComparison.Ordinal);
        await Assert.That(end).IsGreaterThan(start);

        var literal = src.Substring(start + marker.Length, end - (start + marker.Length));
        var parts = literal
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => double.Parse(p, System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();

        await Assert.That(parts).IsEquivalentTo(OnaPlotter.Utilities.RangeScale.Ladder);
    }

    [Test]
    public async Task LinerigFunctionPresent()
    {
        // Sanity: the JS-side computeNiceScale function must exist.
        // If a refactor renames or drops it, the parity test surface
        // catches it before the corner chip silently stops updating.
        var src = ReadJsSource();
        await Assert.That(src).Contains("function computeNiceScale(");
    }
}
