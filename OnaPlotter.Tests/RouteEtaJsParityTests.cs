namespace OnaPlotter.Tests;

/// <summary>
/// Pins the JS twin of <see cref="OnaPlotter.Utilities.RouteEta"/>
/// to the C#-side rules so a tweak in one path doesn't silently
/// produce a different popup string than the other. The C# port is
/// the source of truth (it has the unit tests); the JS twin lives
/// in <c>wwwroot/js/format.js</c> (function <c>etaWithTtg</c>) and
/// is consumed by <c>leafletInterop.js</c>'s <c>formatRouteEta</c>
/// pass-through.
/// </summary>
public class RouteEtaJsParityTests
{
    private static string ReadJsFile(string relName)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "OnaPlotter", "wwwroot", "js", relName);
        return File.ReadAllText(path);
    }

    [Test]
    public async Task FormatRouteEtaFunctionPresent()
    {
        // Public surface still lives in leafletInterop.js so the
        // popup builder can call it; it's now a thin delegate to
        // format.js but the entry-point name must be stable.
        var src = ReadJsFile("leafletInterop.js");
        await Assert.That(src).Contains("function formatRouteEta(");
    }

    [Test]
    public async Task HourCapMatches()
    {
        // The C# port caps at 99 hours and renders "(in >99h)"
        // beyond that. The JS twin (etaWithTtg in format.js) must
        // use the same threshold and the same rendered string,
        // otherwise the corner / popup would render different
        // "(in ...)" text for the same ttg value.
        var src = ReadJsFile("format.js");
        await Assert.That(src).Contains($">{OnaPlotter.Utilities.RouteEta.MaxHoursDisplayed}h");
        await Assert.That(src).Contains($"totalH > {OnaPlotter.Utilities.RouteEta.MaxHoursDisplayed}");
    }

    [Test]
    public async Task NullGuardsPresent()
    {
        // Pin the three guard branches: null, isFinite, > 0. Drift
        // here would produce "ETA NaN:NaN" or "ETA -3m" in the popup.
        // Guards are duplicated in both files (format.js owns the
        // canonical guard; leafletInterop.js's pass-through repeats
        // them so a future caller of formatRouteEta sees a stable
        // contract even if format.js refactors internally).
        var formatSrc = ReadJsFile("format.js");
        await Assert.That(formatSrc).Contains("ttgSeconds == null");
        await Assert.That(formatSrc).Contains("!isFinite(ttgSeconds)");
        await Assert.That(formatSrc).Contains("ttgSeconds <= 0");
    }
}
