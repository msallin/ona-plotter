namespace OnaPlotter.Utilities;

/// <summary>
/// Formats a route time-to-go (in seconds) into the popup ETA line:
/// "ETA HH:MM (in Xh Ym)" or "ETA HH:MM (in Xm)" or "ETA HH:MM (in &gt;99h)".
///
/// <para>The JS twin (<c>formatRouteEta</c> in
/// <c>leafletInterop.js</c>) uses identical math; <see
/// cref="OnaPlotter.Tests.RouteEtaJsParityTests"/> guards against
/// drift via a regex-scrape of the JS source -- same pattern as
/// <c>AisPaletteCssParityTests</c>. The C# port exists so the
/// branch logic (hour overflow, minute floor, null guards) is
/// unit-testable with a deterministic clock.</para>
///
/// <para>Why a clock seam: the wall-clock arrival time depends on
/// "now". A test for "60 s ttg renders ETA = now+1m" needs the
/// clock to be a controlled value; <c>DateTime.Now</c> would only
/// pass at exactly that minute.</para>
/// </summary>
public static class RouteEta
{
    /// <summary>Hour cap. The JS twin renders "(in &gt;99h)" beyond
    /// this threshold instead of lying with a truncated 99h 59m.
    /// Realistically the helm doesn't sit on a &gt;4-day leg without
    /// an intermediate waypoint, but the contract holds.</summary>
    public const int MaxHoursDisplayed = 99;

    /// <summary>
    /// Format the ETA line. Returns null when ttg is null, NaN,
    /// infinite, or non-positive -- the popup drops the row in
    /// those cases instead of showing "ETA --".
    /// </summary>
    /// <param name="ttgSeconds">Time-to-go in seconds, or null.</param>
    /// <param name="now">Wall-clock "now" in the LOCAL zone the
    /// HH:MM should render in. Pass <see cref="DateTime.Now"/> in
    /// production; tests inject a fixed value.</param>
    public static string? Format(double? ttgSeconds, DateTime now)
    {
        if (ttgSeconds is not double ttg) return null;
        if (double.IsNaN(ttg) || double.IsInfinity(ttg)) return null;
        if (ttg <= 0) return null;

        var arrival = now.AddSeconds(ttg);
        // Math.Max(1, ...) clamps a sub-1m ttg to "1m" so the line
        // never reads "(in 0m)" -- which would contradict a
        // populated ETA HH:MM.
        int totalMinutes = Math.Max(1, (int)Math.Round(ttg / 60.0));
        int totalHours = totalMinutes / 60;

        string inText;
        if (totalHours > MaxHoursDisplayed)
        {
            inText = $">{MaxHoursDisplayed}h";
        }
        else if (totalHours > 0)
        {
            inText = $"{totalHours}h {totalMinutes % 60}m";
        }
        else
        {
            inText = $"{totalMinutes}m";
        }

        return $"ETA {arrival:HH:mm} (in {inText})";
    }
}
