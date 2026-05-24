namespace OnaPlotter.Services.Settings;

/// <summary>
/// Narrow surface for the Wind page only.
/// </summary>
public interface IWindPageSettings
{
    /// <summary>"apparent" (default) routes the page's wind cards to
    /// AWA + AWS; "true" routes them to TWD + TWS. Applies to the
    /// hero readout, the history chart, and the variability chips.</summary>
    string WindHeroMode { get; }

    Task SetWindHeroModeAsync(string value);
}
