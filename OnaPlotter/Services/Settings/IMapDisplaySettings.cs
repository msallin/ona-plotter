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

    /// <summary>Helm-picked source for the chart boat-icon
    /// rotation: <c>"headingTrue"</c>, <c>"headingMagnetic"</c>,
    /// <c>"cogTrue"</c>, <c>"cogMagnetic"</c>. Default
    /// <c>"headingTrue"</c> (marine charts are north-true; digital
    /// compasses with a true-heading output are the most accurate
    /// live source). Distinct from <see cref="OnaPlotter.Services.IAppSettings.PreferMagneticHeading"/>
    /// / <see cref="OnaPlotter.Services.IAppSettings.PreferMagneticCourse"/>
    /// which decide which variant the HDG / COG NUMERIC readouts
    /// pick: this setting picks the FIELD that drives the boat-icon
    /// arrow. Falls back through the four fields in
    /// <see cref="OnaPlotter.Utilities.ShipOrientationResolver"/>'s
    /// chain when the picked source isn't published.</summary>
    string ShipOrientationSource { get; }

    /// <summary>When true, the map auto-pans to follow the boat.</summary>
    bool FollowBoat { get; }

    /// <summary>Layline overlay visibility. Independent toggle from
    /// <see cref="ShipLinesVisible"/> -- helms who motor never want
    /// laylines, helms who race want them visible without losing the
    /// COG vector + current arrow that the master gate covers. Same
    /// flag drives the Map and SailSteer layline overlays.</summary>
    bool LaylinesVisible { get; }

    /// <summary>Master gate for own-ship informational lines on the
    /// chart: COG vector (with tip + time/distance label), tidal
    /// current arrow, and laylines. Off declutters the chart for
    /// helms (or for racing) who want to see only the boat icon and
    /// active-route guidance. Defaults to true so existing installs
    /// see the indicators they always saw.
    /// <para>Bearing line + XTE tick are NOT gated by this flag --
    /// they're navigation guidance, not info, and hiding them mid-
    /// leg would be a foot-gun. They show automatically when a route
    /// is active.</para></summary>
    bool ShipLinesVisible { get; }

    /// <summary>Local SOG-coloured own-ship trail (the rolling
    /// ~83 min polyline, in-memory only). Companion to
    /// <see cref="ShipLinesVisible"/> for the trail layer; helms
    /// who want a clean chart (racing) or who keep the server-side
    /// history layer on can hide the local trail without losing the
    /// COG vector + current arrow. Defaults to true.</summary>
    bool LocalTrackVisible { get; }

    /// <summary>Server-side ship-track layer visible. Persisted so a
    /// helm who decluttered last session doesn't see the trail come
    /// back on the next reload. Defaults to true: a fresh helm gets
    /// the long trail-of-record on the chart out of the box.</summary>
    bool ServerTrackVisible { get; }

    /// <summary>Server-side ship-track helm-picked window: <c>1h</c>,
    /// <c>6h</c>, <c>1d</c>, <c>3d</c>, <c>7d</c>, or <c>all</c>.
    /// Default <c>all</c> -- a fresh helm sees their full history-of-
    /// record without having to dig into the dropdown.</summary>
    string ServerTrackDuration { get; }

    /// <summary>Server-side ship-track sampling resolution (1s..4h
    /// from the History page ladder). Default <c>15m</c>: pairs with
    /// the "all time" duration default to keep the payload sane on a
    /// fresh helm's first load.</summary>
    string ServerTrackResolution { get; }

    /// <summary>Server-side ship-track clip-to-bounds filter. Default
    /// true: the long polyline is interesting in the local area, not
    /// for paint-the-globe; the JS module re-clips on pan/zoom
    /// without re-fetching.</summary>
    bool ServerTrackWithinBounds { get; }

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

    /// <summary>Chart-display CSS contrast percentage, 50..200.
    /// Default 100 (identity, no filter). Helm boosts this when
    /// Navionics PNGs read washed-out at noon or on a sunlit screen.
    /// Limits + format string live on
    /// <see cref="OnaPlotter.Utilities.ChartFilter"/>.</summary>
    int ChartContrastPercent { get; }

    /// <summary>Chart-display CSS saturation percentage, 50..200.
    /// Default 100 (identity, no filter). Drop toward 50 to dampen
    /// over-saturated raster charts; raise toward 150 to pop the
    /// depth-tinted areas of muddier source PNGs. Same range as
    /// contrast since both are unbounded multipliers in CSS.</summary>
    int ChartSaturationPercent { get; }

    /// <summary>Chart-display CSS brightness percentage, 50..150.
    /// Default 100 (identity, no filter). Tighter range than
    /// contrast/saturation -- past 150 the chart blooms out and
    /// printed contour lines fade. The helm typically pulls this
    /// DOWN at night to take chart glare off a dark cockpit, not
    /// up.</summary>
    int ChartBrightnessPercent { get; }

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

    /// <summary>How far ahead the OWN-vessel COG vector projects, in
    /// minutes. Drives the dashed predictor line + endpoint label
    /// ('Nmin / N.Nnm') on the chart. Helm-tunable so a coastal cruiser
    /// at 5 kn and a passage at 8 kn can pick a useful look-ahead
    /// independently of the AIS-target setting below. Default 10 min.</summary>
    double OwnCogVectorMinutes { get; }

    /// <summary>How far ahead AIS-target COG vectors project, in
    /// minutes. Independent of <see cref="OwnCogVectorMinutes"/> so a
    /// helm in a busy harbour can shorten target vectors to declutter
    /// without affecting their own predictor. Default 10 min.</summary>
    double AisCogVectorMinutes { get; }

    /// <summary>Show the tide row + extras on the depth HUD card +
    /// Dashboard. Default true; helms in non-tidal waters (lakes,
    /// inland canals) or who run a server without a tide plugin
    /// can hide the empty / irrelevant slot. Persisted as
    /// <c>tideVisible.v1</c>.</summary>
    bool TideVisible { get; }

    /// <summary>Whether the radar overlay paints concentric range
    /// rings centred on own boat. Each ring sits at an evenly-spaced
    /// fraction of the radar's current range (1/N, 2/N, ..., N/N).
    /// Helm aid: read "how far is the sweep showing me" at a glance
    /// without checking the range chip in the HUD. Default true.</summary>
    bool RadarRangeRingsEnabled { get; }

    /// <summary>How many concentric rings to draw when
    /// <see cref="RadarRangeRingsEnabled"/> is true. Default 4
    /// (quarter / half / three-quarter / full range). Clamped 1..8
    /// on the JS side; below 1 there's nothing to draw, above 8 the
    /// chart turns into a bullseye.</summary>
    int RadarRangeRingsCount { get; }

    Task SetMapOrientationAsync(string value);
    Task SetShipOrientationSourceAsync(string value);
    Task SetFollowBoatAsync(bool value);
    Task SetLaylinesVisibleAsync(bool value);
    Task SetShipLinesVisibleAsync(bool value);
    Task SetLocalTrackVisibleAsync(bool value);
    Task SetServerTrackVisibleAsync(bool value);
    Task SetServerTrackDurationAsync(string value);
    Task SetServerTrackResolutionAsync(string value);
    Task SetServerTrackWithinBoundsAsync(bool value);
    Task SetAtonsVisibleAsync(bool value);
    Task SetGuardZoneVisibleAsync(bool value);
    Task SetGuardZoneWarningRingVisibleAsync(bool value);
    Task SetWeatherOverlayOpacityAsync(double value);
    Task SetChartContrastPercentAsync(int value);
    Task SetChartSaturationPercentAsync(int value);
    Task SetChartBrightnessPercentAsync(int value);
    Task SetChartUpscaleEnabledAsync(bool value);
    Task SetChartUpscaleLevelsAsync(int value);
    Task SetHarborModeAsync(bool value);
    Task SetBigTypeAsync(bool value);
    Task SetExpandAllHudAsync(bool value);
    Task SetShowAutopilotHudAsync(bool value);
    Task SetShowRadarHudAsync(bool value);
    Task SetShowDefaultHudAsync(bool value);
    Task SetOwnCogVectorMinutesAsync(double value);
    Task SetAisCogVectorMinutesAsync(double value);
    Task SetRadarRangeRingsEnabledAsync(bool value);
    Task SetRadarRangeRingsCountAsync(int value);
    Task SetTideVisibleAsync(bool value);
}
