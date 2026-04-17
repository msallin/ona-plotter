namespace OnaPlotter.Services;

/// <summary>
/// Application-wide user preferences. Values are kept in memory and
/// persisted to <see cref="IKeyValueStore"/> on change.
/// </summary>
public interface IAppSettings
{
    bool NightMode { get; }

    /// <summary>"system" (follow OS prefers-color-scheme), "light", or "dark".
    /// Independent of <see cref="NightMode"/> which applies a red-shift on top
    /// of whichever base palette is active.</summary>
    string Theme { get; }

    string MapOrientation { get; }
    bool FollowBoat { get; }
    bool LaylinesVisible { get; }
    double DepthAlarmThreshold { get; }

    /// <summary>Guard-zone CPA threshold (nautical miles). A projected CPA
    /// smaller than this triggers a collision alarm.</summary>
    double CpaAlarmThreshold { get; }

    /// <summary>Guard-zone lookahead (minutes). Only vessels whose TCPA falls
    /// within this window trigger the CPA alarm.</summary>
    double GuardZoneLookaheadMinutes { get; }

    double WindShiftAlarmThreshold { get; }
    IReadOnlySet<string> EnabledChartIds { get; }
    IReadOnlySet<string> EnabledRouteIds { get; }

    event Action? OnSettingsChanged;

    Task InitializeAsync();
    Task SetNightModeAsync(bool value);
    Task SetThemeAsync(string value);
    Task SetMapOrientationAsync(string value);
    Task SetFollowBoatAsync(bool value);
    Task SetLaylinesVisibleAsync(bool value);
    Task SetDepthAlarmThresholdAsync(double value);
    Task SetCpaAlarmThresholdAsync(double value);
    Task SetGuardZoneLookaheadMinutesAsync(double value);
    Task SetWindShiftAlarmThresholdAsync(double value);
    Task SetEnabledChartsAsync(IEnumerable<string> ids);
    Task SetEnabledRoutesAsync(IEnumerable<string> ids);
}
