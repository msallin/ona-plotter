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
    public bool NightModeAuto { get; set; }
    public DateTime? LastManualNightToggleUtc { get; set; }
    public bool ChartsSeeded { get; set; }
    public string NightModePreset { get; set; } = "soft";
    public string Theme { get; set; } = "dark";
    public string WindHeroMode { get; set; } = "apparent";
    public bool WindPageCompact { get; set; }
    public string MapOrientation { get; set; } = "north";
    public bool FollowBoat { get; set; } = true;
    public bool LaylinesVisible { get; set; }
    public bool AtonsVisible { get; set; } = true;
    public bool GuardZoneVisible { get; set; } = true;
    public bool GuardZoneWarningRingVisible { get; set; } = true;
    public bool ChartUpscaleEnabled { get; set; }
    public int ChartUpscaleLevels { get; set; } = 2;
    public double WeatherOverlayOpacity { get; set; } = 0.5;
    public bool HarborMode { get; set; }
    public bool SidebarCollapsed { get; set; }
    public double DepthAlarmThreshold { get; set; } = 3.0;
    public double CpaAlarmThreshold { get; set; } = 0.5;
    public double GuardZoneLookaheadMinutes { get; set; } = 10.0;
    public double GuardZoneWarningFactor { get; set; } = 2.0;
    public double WindShiftAlarmThreshold { get; set; } = 15.0;
    public double WindShiftLookbackMinutes { get; set; } = 5.0;
    public double WindShiftMinTrueWindSpeed { get; set; } = 3.0;
    public double AnchorTideSafetyMargin { get; set; } = 1.0;
    public double ManualAnchorRadiusMeters { get; set; } = 30.0;
    public double DeadmanTimeoutMinutes { get; set; } = 0.0;
    public double DeadmanNightMinutes { get; set; } = 15.0;
    public int SnoozeDurationMinutes { get; set; } = 10;
    public bool BigType { get; set; } = false;
    public bool ExpandAllHud { get; set; } = false;
    public string SailingMode { get; set; } = "cruise";
    public string OwnVesselType { get; set; } = "power";
    public bool KeepScreenAwake { get; set; } = true;
    public double WaypointArrivalRadiusMeters { get; set; } = 50.0;
    public bool ShowKeyboardHints { get; set; } = false;
    public bool ShowAutopilotHud { get; set; } = false;
    public bool ShowRadarHud { get; set; } = false;
    public bool ShowDefaultHud { get; set; } = true;
    public double OwnCogVectorMinutes { get; set; } = 10.0;
    public double AisCogVectorMinutes { get; set; } = 10.0;
    public bool PreferMagneticHeading { get; set; } = false;
    public bool PreferMagneticCourse { get; set; } = false;
    public bool AutoAdvanceWaypoints { get; set; } = true;
    public IReadOnlySet<string> EnabledChartIds => new HashSet<string>();
    public IReadOnlySet<string> EnabledRouteIds => new HashSet<string>();
    public IReadOnlySet<string> QuickBarChartIds => new HashSet<string>();
    public IReadOnlyList<string> ChartOrder => [];
    public double? MapViewLat { get; set; }
    public double? MapViewLon { get; set; }
    public int? MapViewZoom { get; set; }

    public event Action? OnSettingsChanged { add { } remove { } }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task SetNightModeAsync(bool v) => Task.CompletedTask;
    public Task MarkManualNightToggleAsync()
    {
        LastManualNightToggleUtc = DateTime.UtcNow;
        return Task.CompletedTask;
    }
    public Task MarkChartsSeededAsync() { ChartsSeeded = true; return Task.CompletedTask; }
    public Task SetNightModeAutoAsync(bool v) => Task.CompletedTask;
    public Task SetNightModePresetAsync(string v) => Task.CompletedTask;
    public Task SetThemeAsync(string v) => Task.CompletedTask;
    public Task SetWindHeroModeAsync(string v) { WindHeroMode = v; return Task.CompletedTask; }
    public Task SetWindPageCompactAsync(bool v) { WindPageCompact = v; return Task.CompletedTask; }
    public Task SetMapOrientationAsync(string v) => Task.CompletedTask;
    public Task SetFollowBoatAsync(bool v) => Task.CompletedTask;
    public Task SetLaylinesVisibleAsync(bool v) => Task.CompletedTask;
    public Task SetSidebarCollapsedAsync(bool v) { SidebarCollapsed = v; return Task.CompletedTask; }
    public Task SetAtonsVisibleAsync(bool v) { AtonsVisible = v; return Task.CompletedTask; }
    public Task SetGuardZoneVisibleAsync(bool v) { GuardZoneVisible = v; return Task.CompletedTask; }
    public Task SetGuardZoneWarningRingVisibleAsync(bool v) { GuardZoneWarningRingVisible = v; return Task.CompletedTask; }
    public Task SetChartUpscaleEnabledAsync(bool v) { ChartUpscaleEnabled = v; return Task.CompletedTask; }
    public Task SetChartUpscaleLevelsAsync(int v) { ChartUpscaleLevels = v; return Task.CompletedTask; }
    public Task SetWeatherOverlayOpacityAsync(double v) { WeatherOverlayOpacity = v; return Task.CompletedTask; }
    public Task SetHarborModeAsync(bool v) { HarborMode = v; return Task.CompletedTask; }
    public Task ApplyMobileFirstRunDefaultsAsync(bool isMobile)
    {
        if (isMobile) SidebarCollapsed = true;
        return Task.CompletedTask;
    }
    public Task SetDepthAlarmThresholdAsync(double v) => Task.CompletedTask;
    public Task SetCpaAlarmThresholdAsync(double v) => Task.CompletedTask;
    public Task SetGuardZoneLookaheadMinutesAsync(double v) => Task.CompletedTask;
    public Task SetGuardZoneWarningFactorAsync(double v) => Task.CompletedTask;
    public Task SetWindShiftAlarmThresholdAsync(double v) => Task.CompletedTask;
    public Task SetWindShiftLookbackMinutesAsync(double v) => Task.CompletedTask;
    public Task SetWindShiftMinTrueWindSpeedAsync(double v) { WindShiftMinTrueWindSpeed = v; return Task.CompletedTask; }
    public Task SetAnchorTideSafetyMarginAsync(double v) => Task.CompletedTask;
    public Task SetManualAnchorRadiusMetersAsync(double v) => Task.CompletedTask;
    public Task SetDeadmanTimeoutMinutesAsync(double v) { DeadmanTimeoutMinutes = v; return Task.CompletedTask; }
    public Task SetDeadmanNightMinutesAsync(double v) { DeadmanNightMinutes = v; return Task.CompletedTask; }
    public Task SetSnoozeDurationMinutesAsync(int v) { SnoozeDurationMinutes = v; return Task.CompletedTask; }
    public Task SetBigTypeAsync(bool v) => Task.CompletedTask;
    public Task SetExpandAllHudAsync(bool v) { ExpandAllHud = v; return Task.CompletedTask; }
    public Task SetSailingModeAsync(string v) => Task.CompletedTask;
    public Task SetOwnVesselTypeAsync(string v) { OwnVesselType = v; return Task.CompletedTask; }
    public Task SetKeepScreenAwakeAsync(bool v) => Task.CompletedTask;
    public Task SetWaypointArrivalRadiusMetersAsync(double v) => Task.CompletedTask;
    public Task SetShowKeyboardHintsAsync(bool v) { ShowKeyboardHints = v; return Task.CompletedTask; }
    public Task SetShowAutopilotHudAsync(bool v) { ShowAutopilotHud = v; return Task.CompletedTask; }
    public Task SetShowRadarHudAsync(bool v) { ShowRadarHud = v; return Task.CompletedTask; }
    public Task SetShowDefaultHudAsync(bool v) { ShowDefaultHud = v; return Task.CompletedTask; }
    public Task SetOwnCogVectorMinutesAsync(double v) { OwnCogVectorMinutes = v; return Task.CompletedTask; }
    public Task SetAisCogVectorMinutesAsync(double v) { AisCogVectorMinutes = v; return Task.CompletedTask; }
    public Task SetPreferMagneticHeadingAsync(bool v) { PreferMagneticHeading = v; return Task.CompletedTask; }
    public Task SetPreferMagneticCourseAsync(bool v) { PreferMagneticCourse = v; return Task.CompletedTask; }
    public Task SetAutoAdvanceWaypointsAsync(bool v) { AutoAdvanceWaypoints = v; return Task.CompletedTask; }
    public Task SetMapViewAsync(double lat, double lon, int zoom)
    {
        MapViewLat = lat; MapViewLon = lon; MapViewZoom = zoom;
        return Task.CompletedTask;
    }
    public Task SetEnabledChartsAsync(IEnumerable<string> ids) => Task.CompletedTask;
    public Task SetEnabledRoutesAsync(IEnumerable<string> ids) => Task.CompletedTask;
    public Task SetQuickBarChartsAsync(IEnumerable<string> ids) => Task.CompletedTask;
    public Task SetChartOrderAsync(IEnumerable<string> ids) => Task.CompletedTask;
}
