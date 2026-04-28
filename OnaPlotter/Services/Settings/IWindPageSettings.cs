namespace OnaPlotter.Services.Settings;

/// <summary>
/// Narrow surface for the Wind page only. Two knobs (apparent vs
/// true hero, compact density) -- enough to deserve its own seam
/// rather than carrying a 43-property interface into a single page.
///
/// <para>Carved from <see cref="IAppSettings"/> as part of ARCH-003.
/// </para>
/// </summary>
public interface IWindPageSettings
{
    /// <summary>"apparent" (default) shows AWA + AWS in the Wind
    /// page hero; "true" shows TWD + TWS.</summary>
    string WindHeroMode { get; }

    /// <summary>When true, the Wind page renders in compact density:
    /// card chrome drops, gaps tighten, max screen real estate goes
    /// to data.</summary>
    bool WindPageCompact { get; }

    Task SetWindHeroModeAsync(string value);
    Task SetWindPageCompactAsync(bool value);
}
