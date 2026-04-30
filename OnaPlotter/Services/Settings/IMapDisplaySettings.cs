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

    /// <summary>Guard-zone amber ring visible on the map. Independent
    /// of the CPA alarm pipeline -- helms can declutter the chart
    /// without disabling the alarm. Defaults to true so existing
    /// installs see the ring as before.</summary>
    bool GuardZoneVisible { get; }

    /// <summary>Outer dashed warning ring visible on the map at
    /// <c>GuardZone × WarningFactor</c>. Helps the helm see why an
    /// amber CPA chip can sit between the inner danger ring and the
    /// outer advisory band -- the chip is in the warning band, not
    /// "outside the guard ring" as field-tested. Independent of the
    /// inner ring's visibility (helms can show the danger ring alone
    /// for a cleaner chart, or both rings for full context).
    /// Defaults to true so existing installs gain the new advisory
    /// ring without an opt-in step. Has no effect when the inner
    /// ring is hidden, when harbor mode is active, or when
    /// <c>GuardZoneWarningFactor &lt;= 1</c> (warning band collapsed
    /// onto the danger band -- nothing to draw).</summary>
    bool GuardZoneWarningRingVisible { get; }

    /// <summary>RainViewer weather overlay opacity, 0.05..0.95
    /// fraction. The shared
    /// <see cref="OnaPlotter.Utilities.WeatherOpacity"/> helper holds
    /// the limits + clamp logic.</summary>
    double WeatherOverlayOpacity { get; }

    /// <summary>Chart upscale ("overzoom") master flag. Off by
    /// default; see design draft for the rationale (helms who don't
    /// ask for it shouldn't see pixelated tiles past native zoom).
    /// When true, chart tile layers are wrapped so Leaflet GPU-
    /// upscales tiles at <c>maxNativeZoom</c> when the helm zooms
    /// past it.</summary>
    bool ChartUpscaleEnabled { get; }

    /// <summary>Chart upscale levels, 0..3. <c>2</c> (4x upscale)
    /// is the recommended default; <c>3</c> (8x) is here for helms
    /// who explicitly want more reach despite the quality cliff.
    /// Limits + clamp live on
    /// <see cref="OnaPlotter.Utilities.ChartUpscale"/>.</summary>
    int ChartUpscaleLevels { get; }

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

    /// <summary>When true, the four corner HUD cards (top-left
    /// SOG/COG/Pos, top-right Wind, bottom-left Depth, bottom-right
    /// Heading) are rendered on the map. Defaults to true so
    /// existing installs see the same panels they always did; the
    /// toggle was added in PR-B of the focus-group sweep so a helm
    /// running an external instrument cluster (or doing a clean
    /// chart screenshot) can declutter the corners without losing
    /// the autopilot / radar / route HUDs which have their own
    /// switches.</summary>
    bool ShowDefaultHud { get; }

    Task SetMapOrientationAsync(string value);
    Task SetFollowBoatAsync(bool value);
    Task SetLaylinesVisibleAsync(bool value);
    Task SetAtonsVisibleAsync(bool value);
    Task SetGuardZoneVisibleAsync(bool value);
    Task SetGuardZoneWarningRingVisibleAsync(bool value);
    Task SetWeatherOverlayOpacityAsync(double value);
    Task SetChartUpscaleEnabledAsync(bool value);
    Task SetChartUpscaleLevelsAsync(int value);
    Task SetHarborModeAsync(bool value);
    Task SetBigTypeAsync(bool value);
    Task SetExpandAllHudAsync(bool value);
    Task SetShowAutopilotHudAsync(bool value);
    Task SetShowRadarHudAsync(bool value);
    Task SetShowDefaultHudAsync(bool value);
}
