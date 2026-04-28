namespace OnaPlotter.Tests;

/// <summary>
/// Pins the JS twin of <see cref="OnaPlotter.Utilities.RouteEta"/>
/// to the C#-side rules so a tweak in one path doesn't silently
/// produce a different popup string than the other. The C# port is
/// the source of truth (it has the unit tests); the JS twin must
/// match its branch shape.
/// </summary>
public class RouteEtaJsParityTests
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
    public async Task FormatRouteEtaFunctionPresent()
    {
        var src = ReadJsSource();
        await Assert.That(src).Contains("function formatRouteEta(");
    }

    [Test]
    public async Task HourCapMatches()
    {
        // The C# port caps at 99 hours and renders "(in >99h)"
        // beyond that. The JS twin must use the same threshold and
        // the same rendered string, otherwise the corner / popup
        // would render different "(in ...)" text for the same ttg
        // value depending on which path produced the chip.
        var src = ReadJsSource();
        await Assert.That(src).Contains($">{OnaPlotter.Utilities.RouteEta.MaxHoursDisplayed}h");
        await Assert.That(src).Contains($"totalH > {OnaPlotter.Utilities.RouteEta.MaxHoursDisplayed}");
    }

    [Test]
    public async Task NullGuardsPresent()
    {
        // Pin the three guard branches: null, isFinite, > 0. Drift
        // here would produce "ETA NaN:NaN" or "ETA -3m" in the popup.
        var src = ReadJsSource();
        await Assert.That(src).Contains("ttgSeconds == null");
        await Assert.That(src).Contains("!isFinite(ttgSeconds)");
        await Assert.That(src).Contains("ttgSeconds <= 0");
    }
}
