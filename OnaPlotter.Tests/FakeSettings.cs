using OnaPlotter.Services;

namespace OnaPlotter.Tests;

/// <summary>
/// Shared IAppSettings stub for alarm-rule + service tests. Mutable
/// property setters so each test can customise the values it cares
/// about without redeclaring the interface surface. The no-op
/// Set*Async methods and empty OnSettingsChanged keep the interface
/// satisfied; tests that care about persistence or change
/// notification roll their own test double.
/// </summary>
internal sealed class FakeSettings : IAppSettings
{
    public bool NightMode { get; set; }
    public string NightModePreset { get; set; } = "soft";
    public string Theme { get; set; } = "dark";
    public string MapOrientation { get; set; } = "north";
    public bool FollowBoat { get; set; } = true;
    public bool LaylinesVisible { get; set; }
    public double DepthAlarmThreshold { get; set; } = 3.0;
    public double CpaAlarmThreshold { get; set; } = 0.5;
    public double GuardZoneLookaheadMinutes { get; set; } = 10.0;
    public double GuardZoneWarningFactor { get; set; } = 2.0;
    public double WindShiftAlarmThreshold { get; set; } = 15.0;
    public double WindShiftLookbackMinutes { get; set; } = 5.0;
    public double BoatDraftMeters { get; set; } = 1.5;
    public double AnchorTideSafetyMargin { get; set; } = 1.0;
    public IReadOnlySet<string> EnabledChartIds => new HashSet<string>();
    public IReadOnlySet<string> EnabledRouteIds => new HashSet<string>();

    public event Action? OnSettingsChanged { add { } remove { } }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task SetNightModeAsync(bool v) => Task.CompletedTask;
    public Task SetNightModePresetAsync(string v) => Task.CompletedTask;
    public Task SetThemeAsync(string v) => Task.CompletedTask;
    public Task SetMapOrientationAsync(string v) => Task.CompletedTask;
    public Task SetFollowBoatAsync(bool v) => Task.CompletedTask;
    public Task SetLaylinesVisibleAsync(bool v) => Task.CompletedTask;
    public Task SetDepthAlarmThresholdAsync(double v) => Task.CompletedTask;
    public Task SetCpaAlarmThresholdAsync(double v) => Task.CompletedTask;
    public Task SetGuardZoneLookaheadMinutesAsync(double v) => Task.CompletedTask;
    public Task SetGuardZoneWarningFactorAsync(double v) => Task.CompletedTask;
    public Task SetWindShiftAlarmThresholdAsync(double v) => Task.CompletedTask;
    public Task SetWindShiftLookbackMinutesAsync(double v) => Task.CompletedTask;
    public Task SetBoatDraftMetersAsync(double v) => Task.CompletedTask;
    public Task SetAnchorTideSafetyMarginAsync(double v) => Task.CompletedTask;
    public Task SetEnabledChartsAsync(IEnumerable<string> ids) => Task.CompletedTask;
    public Task SetEnabledRoutesAsync(IEnumerable<string> ids) => Task.CompletedTask;
}
