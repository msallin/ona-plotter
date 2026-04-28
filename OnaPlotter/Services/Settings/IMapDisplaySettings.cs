namespace OnaPlotter.Services.Settings;

/// <summary>
/// Narrow surface for "what's drawn on the map" toggles. Excludes
/// chart selection (lives in <see cref="IChartSettings"/>) and
/// alarm thresholds (<see cref="IAlarmThresholds"/>) on purpose --
/// this is overlay visibility + opacity + harbor mode + display
/// density flags.
///
/// <para>Carved from <see cref="IAppSettings"/> as part of ARCH-003.
/// </para>
/// </summary>
public interface IMapDisplaySettings
{
    /// <summary>"north" / "course" / "head". Drives chart rotation.</summary>
    string MapOrientation { get; }

    /// <summary>When true, the map auto-pans to follow the boat.</summary>
    bool FollowBoat { get; }

    /// <summary>Layline overlay visible on the SailSteer + Map.</summary>
    bool LaylinesVisible { get; }

    /// <summary>AIS Aids to Navigation visible on the map.</summary>
    bool AtonsVisible { get; }

    /// <summary>RainViewer weather overlay opacity, 0.05..0.95
    /// fraction. The shared
    /// <see cref="OnaPlotter.Utilities.WeatherOpacity"/> helper holds
    /// the limits + clamp logic.</summary>
    double WeatherOverlayOpacity { get; }

    /// <summary>Harbor mode: bundled AIS + collision suppressions.
    /// In-memory only -- never persisted (see settings rationale).</summary>
    bool HarborMode { get; }

    /// <summary>Big-type mode: scales corner HUD values up for
    /// across-cockpit readability.</summary>
    bool BigType { get; }

    /// <summary>When true, every corner HUD panel renders expanded
    /// regardless of click.</summary>
    bool ExpandAllHud { get; }

    /// <summary>When true, the dedicated Autopilot control card is
    /// rendered on the map.</summary>
    bool ShowAutopilotHud { get; }

    /// <summary>When true, the dedicated Radar control card is
    /// rendered on the map.</summary>
    bool ShowRadarHud { get; }

    Task SetMapOrientationAsync(string value);
    Task SetFollowBoatAsync(bool value);
    Task SetLaylinesVisibleAsync(bool value);
    Task SetAtonsVisibleAsync(bool value);
    Task SetWeatherOverlayOpacityAsync(double value);
    Task SetHarborModeAsync(bool value);
    Task SetBigTypeAsync(bool value);
    Task SetExpandAllHudAsync(bool value);
    Task SetShowAutopilotHudAsync(bool value);
    Task SetShowRadarHudAsync(bool value);
}
