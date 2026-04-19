namespace OnaPlotter.Services;

/// <summary>
/// Application-wide user preferences. Values are kept in memory and
/// persisted to <see cref="IKeyValueStore"/> on change.
/// </summary>
public interface IAppSettings
{
    bool NightMode { get; }

    /// <summary>Night-mode flavour. "soft" is the light-red default (warm
    /// amber with a touch of blue); "amber" is warmer and brighter for
    /// sunset; "red" is the classic helm-at-night single-channel red that
    /// preserves dark-adapted vision. Only meaningful when
    /// <see cref="NightMode"/> is true.</summary>
    string NightModePreset { get; }

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

    /// <summary>Multiplier for the advisory (amber) warning band around the
    /// guard zone. A factor of 2.0 draws the warning ring at 2x the alarm
    /// radius and within 2x the lookahead; targets inside that band get
    /// amber crossing lines but no audible alarm.</summary>
    double GuardZoneWarningFactor { get; }

    double WindShiftAlarmThreshold { get; }

    /// <summary>Minutes of lookback used to detect a wind shift. Racing
    /// crews typically want 1-2 min; cruisers want 10-15. Configurable so
    /// the alarm is useful on either side of that spread.</summary>
    double WindShiftLookbackMinutes { get; }

    /// <summary>Boat draft in metres: the depth the keel extends below
    /// the waterline. Used by the tide-aware anchor alarm to predict
    /// whether the boat will touch bottom at the next low water. Users
    /// should enter the deepest point of the hull at the widest loading.</summary>
    double BoatDraftMeters { get; }

    /// <summary>Safety margin added to draft for the tide-aware anchor
    /// alarm: alarm fires when predicted LW depth is less than
    /// <c>draft + margin</c>. Typical is 0.5-1 m; add more for soft
    /// mud where the boat can settle.</summary>
    double AnchorTideSafetyMargin { get; }

    /// <summary>High-level sailing mode: "cruise" (default -- navigation
    /// emphasis, depth / anchor / route) or "race" (performance emphasis,
    /// laylines / optimal TWA / target speed). Discriminator used by the
    /// Map page to show / hide race-specific overlays without forcing a
    /// mode toggle on every switch.</summary>
    string SailingMode { get; }
    IReadOnlySet<string> EnabledChartIds { get; }
    IReadOnlySet<string> EnabledRouteIds { get; }

    event Action? OnSettingsChanged;

    Task InitializeAsync();
    Task SetNightModeAsync(bool value);
    Task SetNightModePresetAsync(string value);
    Task SetThemeAsync(string value);
    Task SetMapOrientationAsync(string value);
    Task SetFollowBoatAsync(bool value);
    Task SetLaylinesVisibleAsync(bool value);
    Task SetDepthAlarmThresholdAsync(double value);
    Task SetCpaAlarmThresholdAsync(double value);
    Task SetGuardZoneLookaheadMinutesAsync(double value);
    Task SetGuardZoneWarningFactorAsync(double value);
    Task SetWindShiftAlarmThresholdAsync(double value);
    Task SetWindShiftLookbackMinutesAsync(double value);
    Task SetBoatDraftMetersAsync(double value);
    Task SetAnchorTideSafetyMarginAsync(double value);
    Task SetSailingModeAsync(string value);
    Task SetEnabledChartsAsync(IEnumerable<string> ids);
    Task SetEnabledRoutesAsync(IEnumerable<string> ids);
}
