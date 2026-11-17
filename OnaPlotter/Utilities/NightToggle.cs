using OnaPlotter.Services.Settings;

namespace OnaPlotter.Utilities;

/// <summary>Manual night-mode toggle shared by the topbar button and the
/// chart's Night control.</summary>
public static class NightToggle
{
    /// <summary>Maps the Signal K <c>environment.sun</c> string to a
    /// two-state cluster (day / night); null when unknown. Used by the
    /// manual-toggle override capture and the auto-night transition check
    /// so both speak the same vocabulary.</summary>
    public static string? SunCluster(string? sunState) => sunState switch
    {
        "day" or "dawn" => "day",
        "dusk" or "night" => "night",
        _ => null,
    };

    /// <summary>Binary toggle (one tap on, one tap off). Records the
    /// env.sun cluster at toggle time so auto-night stays suppressed until
    /// the next day/night transition: a deliberate toggle is a manual
    /// override.</summary>
    public static async Task ToggleAsync(IThemeSettings settings, string? sunState)
    {
        await settings.MarkManualNightToggleAsync(SunCluster(sunState));
        await settings.SetNightModeAsync(!settings.NightMode);
    }
}
