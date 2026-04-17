namespace OnaPlotter.Services;

/// <summary>
/// Application-wide user preferences. Values are kept in memory and
/// persisted to <see cref="IKeyValueStore"/> on change.
/// </summary>
public interface IAppSettings
{
    bool NightMode { get; }
    string MapOrientation { get; }
    bool FollowBoat { get; }
    bool LaylinesVisible { get; }
    double DepthAlarmThreshold { get; }
    double CpaAlarmThreshold { get; }
    double WindShiftAlarmThreshold { get; }

    event Action? OnSettingsChanged;

    Task InitializeAsync();
    Task SetNightModeAsync(bool value);
    Task SetMapOrientationAsync(string value);
    Task SetFollowBoatAsync(bool value);
    Task SetLaylinesVisibleAsync(bool value);
    Task SetDepthAlarmThresholdAsync(double value);
    Task SetCpaAlarmThresholdAsync(double value);
    Task SetWindShiftAlarmThresholdAsync(double value);
}
