using System.Globalization;

namespace OnaPlotter.Services;

/// <summary>
/// Singleton user preferences store. Reads initial values from
/// <see cref="IKeyValueStore"/> on first access and writes back on change.
/// <para>
/// Keys that cannot be read (storage disabled, private browsing) fall back
/// to defaults without surfacing the error - reading a missing preference
/// is not an error condition.
/// </para>
/// </summary>
public sealed class AppSettingsService : IAppSettings
{
    private readonly IKeyValueStore _store;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    // True once the user (or the mobile first-run default) has stamped
    // a value for "sidebarCollapsed.v1" into the KV store. Used by
    // ApplyMobileFirstRunDefaultsAsync to distinguish "unset" from
    // "explicitly set to false" so the auto-collapse on phone width
    // doesn't fight an intentional desktop -> phone resize.
    private bool _sidebarCollapsedExplicit;

    /// <summary>One persisted preference: its key, how stored text becomes
    /// a value, and how a value is stored. <c>normalize</c> runs on load
    /// AND on every set, so a corrupt stored value is pinned into range
    /// exactly like a bad caller.</summary>
    private sealed class Setting<T> : IPersisted
    {
        private readonly Func<string?, T> _parse;
        private readonly Func<T, string> _format;
        private readonly Func<T, T> _normalize;

        public Setting(string key, T initial, Func<string?, T> parse, Func<T, string> format, Func<T, T>? normalize)
        {
            Key = key;
            _parse = parse;
            _format = format;
            _normalize = normalize ?? (v => v);
            Value = _normalize(initial);
        }

        public string Key { get; }
        public T Value { get; private set; }
        public string Stored => _format(Value);
        public void Assign(T value) => Value = _normalize(value);
        public void Load(string? stored) => Assign(_parse(stored));
    }

    private interface IPersisted
    {
        string Key { get; }
        void Load(string? stored);
    }

    /// <summary>Descriptor factories, one per storage shape. Missing or
    /// unparseable stored text yields the default.</summary>
    private static class Setting
    {
        /// <summary>Stored as "true" / "false"; any other stored text reads
        /// as false rather than the default.</summary>
        public static Setting<bool> Bool(string key, bool @default) => new(key, @default,
            s => s is null ? @default : s == "true",
            v => v ? "true" : "false", null);

        /// <summary><paramref name="format"/> is a numeric format string
        /// ("F1", "F2"); null stores the shortest round-trip text.</summary>
        public static Setting<double> Number(string key, double @default,
            Func<double, double>? normalize = null, string? format = null) => new(key, @default,
            s => ParseNumber(s, @default),
            v => v.ToString(format, CultureInfo.InvariantCulture), normalize);

        /// <summary>Stored as an integer but read through the double parser
        /// (truncating), so a hand-edited "2.7" still loads.</summary>
        public static Setting<int> Whole(string key, int @default,
            Func<int, int>? normalize = null) => new(key, @default,
            s => (int)ParseNumber(s, @default),
            v => v.ToString(CultureInfo.InvariantCulture), normalize);

        public static Setting<string> Text(string key, string @default,
            Func<string, string>? normalize = null) => new(key, @default,
            s => s ?? @default,
            v => v, normalize);

        private static double ParseNumber(string? stored, double fallback)
            => stored is not null
                && double.TryParse(stored, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                ? d : fallback;
    }

    /// <summary>Every descriptor Initialize loads generically. Settings with
    /// their own load rules (legacy fallbacks, cross-field invariants) are
    /// loaded explicitly in <see cref="InitializeAsync"/>.</summary>
    private IPersisted[] Persisted() =>
    [
        _nightMode,
        _nightModeAuto,
        _chartsSeeded,
        _theme,
        _windHeroMode,
        _mapOrientation,
        _shipOrientationSource,
        _followBoat,
        _laylinesVisible,
        _shipLinesVisible,
        _serverTrackVisible,
        _serverTrackDuration,
        _serverTrackResolution,
        _serverTrackWithinBounds,
        _atonsVisible,
        _aisLabelsVisible,
        _weatherOverlayOpacity,
        _chartContrastPercent,
        _chartSaturationPercent,
        _chartBrightnessPercent,
        _chartUpscaleEnabled,
        _chartUpscaleLevels,
        _depthAlarmThreshold,
        _cpaDebounceSeconds,
        _windShiftAlarmThreshold,
        _windShiftLookbackMinutes,
        _windShiftMinTrueWindSpeed,
        _anchorTideSafetyMargin,
        _anchorAutoRadiusSafetyMargin,
        _manualAnchorRadiusMeters,
        _anchorChainLengthMeters,
        _deadmanTimeoutMinutes,
        _deadmanNightMinutes,
        _snoozeDurationMinutes,
        _bigType,
        _expandAllHud,
        _sailingMode,
        _ownVesselType,
        _keepScreenAwake,
        _serverSideApproachAlarms,
        _showKeyboardHints,
        _showAutopilotHud,
        _showRadarHud,
        _showDefaultHud,
        _ownCogVectorMinutes,
        _aisCogVectorMinutes,
        _tideVisible,
        _distanceRingsEnabled,
        _distanceRingsBaseNm,
        _distanceRingsCount,
        _radarRangeRingsEnabled,
        _radarRangeRingsCount,
        _radarUseWireBearing,
        _radarBearingCorrectionDeg,
        _preferMagneticHeading,
        _autoAdvanceWaypoints,
        _arrivalCircleMeters,
        _marinePoiFuelEnabled,
        _marinePoiMarinaEnabled,
        _marinePoiHarbourEnabled,
        _marinePoiMooringEnabled,
        _marinePoiSlipwayEnabled,
        _marinePoiPierEnabled,
        _marinePoiChandleryEnabled,
        _marinePoiDrinkingWaterEnabled,
        _marinePoiPumpOutEnabled,
        _standaloneMode,
        _standaloneServerUrl,
        _rememberSession,
        _rememberPassword,
        _storedUsername,
        _storedPassword
    ];

    private Task Persist<T>(Setting<T> setting) => Save(setting.Key, setting.Stored);

    /// <summary>Normalise, store and fan out <see cref="OnSettingsChanged"/>.</summary>
    private async Task SetAsync<T>(Setting<T> setting, T value)
    {
        setting.Assign(value);
        await Persist(setting);
        OnSettingsChanged?.Invoke();
    }

    /// <summary>Normalise and store without raising
    /// <see cref="OnSettingsChanged"/>. For values no other subscriber
    /// reads live.</summary>
    private async Task SetQuietAsync<T>(Setting<T> setting, T value)
    {
        setting.Assign(value);
        await Persist(setting);
    }

    public bool NightMode => _nightMode.Value;
    private readonly Setting<bool> _nightMode = Setting.Bool(SettingKeys.NightMode, false);
    public bool NightModeAuto => _nightModeAuto.Value;
    private readonly Setting<bool> _nightModeAuto = Setting.Bool(SettingKeys.NightModeAuto, false);
    public DateTime? LastManualNightToggleUtc { get; private set; }
    public string? LastManualNightOverrideSunCluster { get; private set; }
    public bool ChartsSeeded => _chartsSeeded.Value;
    private readonly Setting<bool> _chartsSeeded = Setting.Bool(SettingKeys.ChartsSeeded, false);
    public string Theme => _theme.Value;
    private readonly Setting<string> _theme = Setting.Text(
        SettingKeys.Theme, "system", normalize: NormalizeTheme);
    public string WindHeroMode => _windHeroMode.Value;
    private readonly Setting<string> _windHeroMode = Setting.Text(
        SettingKeys.WindHeroMode, "apparent", normalize: NormalizeWindHeroMode);
    public string MapOrientation => _mapOrientation.Value;
    private readonly Setting<string> _mapOrientation = Setting.Text(SettingKeys.MapOrientation, "north");
    public string ShipOrientationSource => _shipOrientationSource.Value;
    // Normalise via the resolver so an unknown string from a
    // forged dropdown call doesn't break later round-trips.
    private readonly Setting<string> _shipOrientationSource = Setting.Text(
        SettingKeys.ShipOrientationSource,
        OnaPlotter.Utilities.ShipOrientationResolver.DefaultSetting,
        normalize: v => OnaPlotter.Utilities.ShipOrientationResolver.ToSetting(OnaPlotter.Utilities.ShipOrientationResolver.Parse(v)));
    public bool FollowBoat => _followBoat.Value;
    private readonly Setting<bool> _followBoat = Setting.Bool(SettingKeys.FollowBoat, true);
    public bool LaylinesVisible => _laylinesVisible.Value;
    private readonly Setting<bool> _laylinesVisible = Setting.Bool(SettingKeys.LaylinesVisible, false);
    /// <summary>Master gate for own-ship indicator lines on the chart
    /// (COG vector, tidal current arrow, laylines). Default true so
    /// existing installs see the lines they always saw.</summary>
    public bool ShipLinesVisible => _shipLinesVisible.Value;
    private readonly Setting<bool> _shipLinesVisible = Setting.Bool(SettingKeys.ShipLinesVisible, true);

    /// <summary>Master ship-track visibility (gates BOTH the local
    /// in-memory SOG trail AND the SignalK history polyline; see
    /// <see cref="IMapDisplaySettings.ServerTrackVisible"/> for the
    /// unification rationale). Default true.</summary>
    public bool ServerTrackVisible => _serverTrackVisible.Value;
    private readonly Setting<bool> _serverTrackVisible = Setting.Bool(SettingKeys.ServerTrackVisible, true);
    /// <summary>Helm-picked window (1h..7d / all). Default "all".</summary>
    public string ServerTrackDuration => _serverTrackDuration.Value;
    private readonly Setting<string> _serverTrackDuration = Setting.Text(
        SettingKeys.ServerTrackDuration, "all", normalize: NormalizeServerTrackDuration);
    /// <summary>Sampling resolution (1s..4h). Default "15m" so the
    /// "all time" window stays loadable on first paint.</summary>
    public string ServerTrackResolution => _serverTrackResolution.Value;
    private readonly Setting<string> _serverTrackResolution = Setting.Text(
        SettingKeys.ServerTrackResolution, "15m", normalize: NormalizeServerTrackResolution);
    /// <summary>Clip the rendered polyline to the current viewport
    /// (re-clipped on pan/zoom without a re-fetch). Default true.</summary>
    public bool ServerTrackWithinBounds => _serverTrackWithinBounds.Value;
    private readonly Setting<bool> _serverTrackWithinBounds = Setting.Bool(
        SettingKeys.ServerTrackWithinBounds, true);
    public bool AtonsVisible => _atonsVisible.Value;
    private readonly Setting<bool> _atonsVisible = Setting.Bool(SettingKeys.AtonsVisible, true);
    /// <summary>AIS vessel name labels (per-target tooltips on the chart).
    /// Default true. Independent of <see cref="HarborMode"/>: harbor mode
    /// also suppresses labels for as long as it's on, but the helm-set
    /// AisLabelsVisible flag is the persistent preference. Effective
    /// rule: labels render only when <c>AisLabelsVisible AND
    /// !HarborMode</c>. Toggling harbor mode off does NOT silently
    /// resurrect labels the helm previously hid via this setting.</summary>
    public bool AisLabelsVisible => _aisLabelsVisible.Value;
    private readonly Setting<bool> _aisLabelsVisible = Setting.Bool(SettingKeys.AisLabelsVisible, true);
    public double WeatherOverlayOpacity => _weatherOverlayOpacity.Value;
    // Floor / ceiling pulled from the shared WeatherOpacity helper
    // so the slider, the service, and the JS leaflet layer all
    // share one source of truth. Belt-and-suspenders clamp here
    // protects against a bad caller / future API path that
    // bypasses the slider's min/max attributes.
    private readonly Setting<double> _weatherOverlayOpacity = Setting.Number(
        SettingKeys.WeatherOverlayOpacity,
        OnaPlotter.Utilities.WeatherOpacity.DefaultFraction,
        normalize: OnaPlotter.Utilities.WeatherOpacity.ClampFraction);
    /// <summary>Chart-display CSS filter percentages - helm boosts /
    /// dampens contrast / saturation / brightness from the Layers panel
    /// to read washed-out raster charts. Defaults are identity (100 =
    /// no filter); see <see cref="OnaPlotter.Utilities.ChartFilter"/>
    /// for clamp limits + format.</summary>
    public int ChartContrastPercent => _chartContrastPercent.Value;
    // Same belt-and-suspenders pattern as the other clamp-on-set
    // settings. The slider's min/max attributes are the helm's
    // fence; this clamp catches a JS-bridge override or a future
    // programmatic caller bypassing it.
    private readonly Setting<int> _chartContrastPercent = Setting.Whole(
        SettingKeys.ChartContrastPercent,
        OnaPlotter.Utilities.ChartFilter.DefaultPercent,
        normalize: OnaPlotter.Utilities.ChartFilter.ClampContrastSaturation);
    public int ChartSaturationPercent => _chartSaturationPercent.Value;
    private readonly Setting<int> _chartSaturationPercent = Setting.Whole(
        SettingKeys.ChartSaturationPercent,
        OnaPlotter.Utilities.ChartFilter.DefaultPercent,
        normalize: OnaPlotter.Utilities.ChartFilter.ClampContrastSaturation);
    public int ChartBrightnessPercent => _chartBrightnessPercent.Value;
    private readonly Setting<int> _chartBrightnessPercent = Setting.Whole(
        SettingKeys.ChartBrightnessPercent,
        OnaPlotter.Utilities.ChartFilter.DefaultPercent,
        normalize: OnaPlotter.Utilities.ChartFilter.ClampBrightness);
    // Defaults ON so a fresh helm gets readable tiles past a chart's
    // native max out of the box. Without this, a chart that declares
    // (or quietly downshifts to) maxzoom 16 leaves the helm staring at
    // grey tiles at z17/z18 with no 404 in the network panel - Leaflet
    // simply doesn't fire requests above the layer's maxZoom. The
    // helm's escape hatch (Settings -> Display -> Chart upscale) still
    // works either direction. OSM + OpenSeaMap opt out via AllowUpscale
    // = false so the basemap doesn't compete with a GPU-upscaled SK
    // chart on top - field-tested as visual flicker.
    //
    // Asymmetry vs the default-on flip: Setting.Bool treats any
    // stored value other than the literal "true" as false (it doesn't
    // fall back to the default for non-"true" values). So a corrupted
    // localStorage entry (older build with a different write format,
    // hand-edited, browser-extension synced from a different OS
    // variant) lands the helm in upscale-OFF rather than this new
    // default-on. Pinned by AppSettingsServiceTests.ChartUpscaleEnabled
    // _GarbageStored_LoadsAsFalse: corruption-path helms can recover
    // by toggling the master flag in Settings.
    public bool ChartUpscaleEnabled => _chartUpscaleEnabled.Value;
    private readonly Setting<bool> _chartUpscaleEnabled = Setting.Bool(SettingKeys.ChartUpscaleEnabled, true);
    public int ChartUpscaleLevels => _chartUpscaleLevels.Value;
    // Clamp via the shared helper so a future caller / corrupted
    // localStorage value can't drive the JS-side decorator out of
    // its tested 0..3 range.
    private readonly Setting<int> _chartUpscaleLevels = Setting.Whole(
        SettingKeys.ChartUpscaleLevels,
        OnaPlotter.Utilities.ChartUpscale.DefaultLevels,
        normalize: OnaPlotter.Utilities.ChartUpscale.ClampLevels);
    /// <summary>In-memory only - never persisted, never restored.
    /// See IAppSettings.HarborMode for the rationale (a forgotten
    /// Harbor mode silently riding into open water is the worst-case
    /// scenario, so every fresh visit starts with collision alarms
    /// armed).</summary>
    public bool HarborMode { get; private set; }
    public bool SidebarCollapsed { get; private set; }
    // First-install alarm defaults lean less sensitive so they don't
    // nag, while staying conservative for the use case:
    //   SHALLOW         2.0 m:  3 m would trip on every chop in
    //                           typical 4-5 m anchorages.
    //   ANCHOR TIDE     0.5 m:  1 m clearance fires hours before
    //                           any real risk.
    //   WIND SHIFT      30 deg: 15 deg is normal sailing oscillation;
    //                           30 deg matches the helm-noticeable
    //                           threshold.
    //   WIND lookback   10 min: a longer averaging window cuts
    //                           short-gust false positives.
    //   WIND min TWS    5 kn:   suppresses wobble in light air where
    //                           TWD is noisy.
    public double DepthAlarmThreshold => _depthAlarmThreshold.Value;
    private readonly Setting<double> _depthAlarmThreshold = Setting.Number(
        SettingKeys.DepthAlarmThreshold, 2.0, format: "F1");
    /// <summary>Inner / strict CPA-tier distance (nm). Tighter than the
    /// awareness tier so the audio klaxon only fires for genuinely
    /// close encounters. Default 0.1 nm picked by helm field test as
    /// "actually scary"; the awareness tier (1.0 nm) handles "watch
    /// this one" without nagging the cockpit.</summary>
    public double CpaAlarmNm { get; private set; } = 0.1;
    /// <summary>Alarm-tier TCPA lookahead (minutes). 30 min covers the
    /// full coastal-pilotage planning horizon - if a vessel's projected
    /// CPA is inside the alarm distance within half an hour, the helm
    /// wants to know now.</summary>
    public double TcpaAlarmMin { get; private set; } = 30.0;
    /// <summary>Awareness-tier CPA distance (nm). Triggers the silent
    /// on-chart cross + hover label so the helm SEES a developing
    /// crossing situation. Setter clamps to be &gt;= <see cref="CpaAlarmNm"/>.
    /// Default 1.0 nm.</summary>
    public double CpaAwarenessNm { get; private set; } = 1.0;
    /// <summary>Awareness-tier TCPA lookahead (minutes). Same time
    /// horizon as the alarm tier by default; the distance is what
    /// separates the two bands. Setter clamps to be &gt;= <see cref="TcpaAlarmMin"/>.</summary>
    public double TcpaAwarenessMin { get; private set; } = 30.0;
    /// <summary>How long (seconds) a CPA threat must persist before
    /// the audible alarm fires. Default 5 s so a single-sample false
    /// positive (radar / AIS jitter at the projection boundary) doesn't
    /// ring the klaxon. 0 disables debouncing.</summary>
    public double CpaDebounceSeconds => _cpaDebounceSeconds.Value;
    private readonly Setting<double> _cpaDebounceSeconds = Setting.Number(
        SettingKeys.CpaDebounceSeconds, 5.0, normalize: v => Math.Clamp(v, 0.0, 60.0), format: "F1");
    public double WindShiftAlarmThreshold => _windShiftAlarmThreshold.Value;
    private readonly Setting<double> _windShiftAlarmThreshold = Setting.Number(
        SettingKeys.WindShiftAlarmThreshold, 30.0, format: "F1");
    public double WindShiftLookbackMinutes => _windShiftLookbackMinutes.Value;
    private readonly Setting<double> _windShiftLookbackMinutes = Setting.Number(
        SettingKeys.WindShiftLookbackMinutes, 10.0, format: "F1");
    public double WindShiftMinTrueWindSpeed => _windShiftMinTrueWindSpeed.Value;
    // Clamp at 0 (negative TWS is meaningless and would re-introduce
    // the noise the gate was added to prevent). No upper clamp - a
    // user who sets 50 kn has effectively disabled the alarm, which
    // is a legitimate choice.
    private readonly Setting<double> _windShiftMinTrueWindSpeed = Setting.Number(
        SettingKeys.WindShiftMinTrueWindSpeed, 5.0, normalize: v => Math.Max(0, v), format: "F1");
    // Boat draft is read from SignalK (design.draft.current / .maximum)
    // via NavigationData.DraftFromSignalK; no manual override here.
    public double AnchorTideSafetyMargin => _anchorTideSafetyMargin.Value;
    private readonly Setting<double> _anchorTideSafetyMargin = Setting.Number(
        SettingKeys.AnchorTideSafetyMargin, 0.5, format: "F2");
    /// <summary>Pad added on top of swing + tide-drop in the
    /// auto-anchor-radius preview. 5 m is the helm-tested cushion;
    /// raise for soft-mud anchorages.</summary>
    public double AnchorAutoRadiusSafetyMargin => _anchorAutoRadiusSafetyMargin.Value;
    // Clamp at 0; negative would let the auto preview round
    // BELOW the swing + tide-drop sum, which defeats the
    // purpose of the pad. Helm input box should already cap
    // at sensible ranges; this is the model-level guard.
    private readonly Setting<double> _anchorAutoRadiusSafetyMargin = Setting.Number(
        SettingKeys.AnchorAutoRadiusSafetyMargin, 5.0, normalize: v => Math.Max(0, v), format: "F1");
    public double ManualAnchorRadiusMeters => _manualAnchorRadiusMeters.Value;
    private readonly Setting<double> _manualAnchorRadiusMeters = Setting.Number(
        SettingKeys.ManualAnchorRadiusMeters, 30.0, format: "F1");
    /// <summary>Helm-entered deployed rode length (metres). 0 = not
    /// entered; the Auto radius then ignores the chain term.</summary>
    public double AnchorChainLengthMeters => _anchorChainLengthMeters.Value;
    // Clamp at 0: a negative rode length is meaningless and would
    // feed a NaN into the chain projection's sqrt. The helm input
    // is a length, so 0 ("none entered") is the floor.
    private readonly Setting<double> _anchorChainLengthMeters = Setting.Number(
        SettingKeys.AnchorChainLengthMeters, 0.0, normalize: v => Math.Max(0, v), format: "F1");
    public double DeadmanTimeoutMinutes => _deadmanTimeoutMinutes.Value;
    private readonly Setting<double> _deadmanTimeoutMinutes = Setting.Number(
        SettingKeys.DeadmanTimeoutMinutes, 0.0, format: "F1");
    public double DeadmanNightMinutes => _deadmanNightMinutes.Value;
    private readonly Setting<double> _deadmanNightMinutes = Setting.Number(
        SettingKeys.DeadmanNightMinutes, 15.0, format: "F1");
    public int SnoozeDurationMinutes => _snoozeDurationMinutes.Value;
    // Clamp 1 min minimum (zero would snooze forever - that's dismiss).
    // Upper bound 120 to keep a runaway value from locking an alarm
    // quiet for days after a power cycle.
    private readonly Setting<int> _snoozeDurationMinutes = Setting.Whole(
        SettingKeys.SnoozeDurationMinutes, 10, normalize: v => Math.Clamp(v, 1, 120));
    public bool BigType => _bigType.Value;
    private readonly Setting<bool> _bigType = Setting.Bool(SettingKeys.BigType, false);
    public bool ExpandAllHud => _expandAllHud.Value;
    private readonly Setting<bool> _expandAllHud = Setting.Bool(SettingKeys.ExpandAllHud, false);
    public string SailingMode => _sailingMode.Value;
    private readonly Setting<string> _sailingMode = Setting.Text(
        SettingKeys.SailingMode, "cruise", normalize: NormalizeSailingMode);
    /// <summary>Own propulsion category for COLREGS Rule 18 priority.
    /// "sail" (default) or "power". Stored as a string rather than an
    /// enum so persistence + the Settings select bind directly.</summary>
    public string OwnVesselType => _ownVesselType.Value;
    private readonly Setting<string> _ownVesselType = Setting.Text(
        SettingKeys.OwnVesselType, "sail", normalize: NormalizeOwnVesselType);
    public bool KeepScreenAwake => _keepScreenAwake.Value;
    private readonly Setting<bool> _keepScreenAwake = Setting.Bool(SettingKeys.KeepScreenAwake, true);
    /// <summary>Default true: prefer server-side
    /// <c>notifications.navigation.*</c> from signalk-course-data
    /// over the client-side WaypointApproach alarm. Helms with a
    /// course-provider plugin get one consistent arrival cue (the
    /// banner agrees with the autopilot's arrival logic). When false
    /// the client <c>WaypointApproachAlarmRule</c> takes over, also
    /// using the server's <c>navigation.course.arrivalCircle</c> as
    /// the threshold so client and server agree on the boundary.</summary>
    public bool ServerSideApproachAlarms => _serverSideApproachAlarms.Value;
    private readonly Setting<bool> _serverSideApproachAlarms = Setting.Bool(
        SettingKeys.ServerSideApproachAlarms, true);
    public bool ShowKeyboardHints => _showKeyboardHints.Value;
    private readonly Setting<bool> _showKeyboardHints = Setting.Bool(SettingKeys.ShowKeyboardHints, false);
    public bool ShowAutopilotHud => _showAutopilotHud.Value;
    private readonly Setting<bool> _showAutopilotHud = Setting.Bool(SettingKeys.ShowAutopilotHud, false);
    public bool ShowRadarHud => _showRadarHud.Value;
    private readonly Setting<bool> _showRadarHud = Setting.Bool(SettingKeys.ShowRadarHud, false);
    // Defaults true so existing installs see the same four corner
    // panels they always did; toggle is for helms running external
    // instruments or wanting a clean screenshot.
    public bool ShowDefaultHud => _showDefaultHud.Value;
    private readonly Setting<bool> _showDefaultHud = Setting.Bool(SettingKeys.ShowDefaultHud, true);
    /// <summary>How far ahead own-vessel COG vector projects (minutes).
    /// Default 10 covers harbour entry + coastal pilotage; helms doing
    /// long passages bump to 20-30 for forward-planning, helms in
    /// thick traffic shorten to 3-5 to declutter their own predictor.</summary>
    public double OwnCogVectorMinutes => _ownCogVectorMinutes.Value;
    // Clamp to a sensible range. 1 min is the floor where the
    // dashed predictor reads as a vector at all; 60 min is the
    // ceiling beyond which the line crosses the chart's pan
    // budget and the helm loses spatial context. Storage
    // corruption / a malformed import lands at the default.
    private readonly Setting<double> _ownCogVectorMinutes = Setting.Number(
        SettingKeys.OwnCogVectorMinutes,
        10.0,
        normalize: v => Math.Clamp(double.IsFinite(v) ? v : 10.0, 1.0, 60.0));
    /// <summary>Same look-ahead horizon for AIS targets. Independent
    /// of own-vessel so a busy-harbour helm can shorten target vectors
    /// without losing their own predictor's reach.</summary>
    public double AisCogVectorMinutes => _aisCogVectorMinutes.Value;
    private readonly Setting<double> _aisCogVectorMinutes = Setting.Number(
        SettingKeys.AisCogVectorMinutes,
        10.0,
        normalize: v => Math.Clamp(double.IsFinite(v) ? v : 10.0, 1.0, 60.0));

    /// <summary>Helm-configured "AIS inactive" threshold in minutes. When a
    /// target's last AIS-evidence delta is older than this, the chart fades
    /// the marker to the stale-floor opacity and hides its name label.
    /// Default 10 min; clamp 1..240. See
    /// <see cref="IMapDisplaySettings.AisInactiveMinutes"/>.</summary>
    public double AisInactiveMinutes { get; private set; } = 10.0;

    /// <summary>Helm-configured "AIS remove" threshold in minutes. When a
    /// target's last AIS-evidence delta is older than this, the store
    /// drops it entirely so it vanishes from the chart and alarms. Default
    /// 30 min; clamp 1..240. Setter clamps to &gt;= AisInactiveMinutes so
    /// the faded band always exists before removal.</summary>
    public double AisRemoveMinutes { get; private set; } = 30.0;

    /// <summary>Show the tide row + extras on the depth HUD +
    /// Dashboard. Default true.</summary>
    public bool TideVisible => _tideVisible.Value;
    private readonly Setting<bool> _tideVisible = Setting.Bool(SettingKeys.TideVisible, true);

    /// <summary>Helm-configured distance-ring overlay enabled. Default
    /// false (opt-in from Settings -> Display). Independent of the
    /// radar-range-rings flag below and of any alarm setting.</summary>
    public bool DistanceRingsEnabled => _distanceRingsEnabled.Value;
    private readonly Setting<bool> _distanceRingsEnabled = Setting.Bool(
        SettingKeys.DistanceRingsEnabled, false);
    /// <summary>Base distance-ring radius in nautical miles. Default
    /// 0.5 nm; Nth ring sits at BaseNm × N. Clamped 0.05..50 on the
    /// setter.</summary>
    public double DistanceRingsBaseNm => _distanceRingsBaseNm.Value;
    // NaN / negative / zero would collapse every ring to a single
    // point or vanish them entirely; pin into the supported range
    // so a corrupted bind / paste lands on a usable value.
    private readonly Setting<double> _distanceRingsBaseNm = Setting.Number(
        SettingKeys.DistanceRingsBaseNm,
        0.5,
        normalize: v => Math.Clamp(double.IsFinite(v) ? v : 0.5, 0.05, 50.0),
        format: "F2");
    /// <summary>How many concentric distance rings to draw. Default
    /// 4 (so the default config draws rings at 0.5/1.0/1.5/2.0 nm).
    /// Clamped 1..8 on the setter.</summary>
    public int DistanceRingsCount => _distanceRingsCount.Value;
    private readonly Setting<int> _distanceRingsCount = Setting.Whole(
        SettingKeys.DistanceRingsCount, 4, normalize: v => Math.Clamp(v, 1, 8));

    /// <summary>Radar range-ring overlay enabled. Default true.</summary>
    public bool RadarRangeRingsEnabled => _radarRangeRingsEnabled.Value;
    private readonly Setting<bool> _radarRangeRingsEnabled = Setting.Bool(
        SettingKeys.RadarRangeRingsEnabled, true);
    /// <summary>How many concentric range rings to draw. Default 4
    /// (quarter / half / three-quarter / full radar range).</summary>
    public int RadarRangeRingsCount => _radarRangeRingsCount.Value;
    private readonly Setting<int> _radarRangeRingsCount = Setting.Whole(
        SettingKeys.RadarRangeRingsCount, 4, normalize: v => Math.Clamp(v, 1, 8));
    /// <summary>Trust the wire spoke <c>bearing</c> field as true-north
    /// (opt-in). Default false; see <see cref="IMapDisplaySettings.RadarUseWireBearing"/>
    /// for why most installs should leave it off.</summary>
    public bool RadarUseWireBearing => _radarUseWireBearing.Value;
    private readonly Setting<bool> _radarUseWireBearing = Setting.Bool(SettingKeys.RadarUseWireBearing, false);
    /// <summary>Helm-side bearing trim in degrees, added to every spoke's
    /// canvas index on top of the radar's installation
    /// <c>bearingAlignment</c>. Default 0; clamped to -180..180. See
    /// <see cref="IMapDisplaySettings.RadarBearingCorrectionDeg"/>.</summary>
    public double RadarBearingCorrectionDeg => _radarBearingCorrectionDeg.Value;
    private readonly Setting<double> _radarBearingCorrectionDeg = Setting.Number(
        SettingKeys.RadarBearingCorrection, 0.0, normalize: v => Math.Clamp(v, -180.0, 180.0));

    public bool PreferMagneticHeading => _preferMagneticHeading.Value;
    private readonly Setting<bool> _preferMagneticHeading = Setting.Bool(
        SettingKeys.PreferMagneticHeading, false);

    /// <summary>Helm-picked source for the HUD numerical COG
    /// readout. Persisted as <c>cogReadoutSource.v1</c>. The legacy
    /// <see cref="PreferMagneticCourse"/> boolean derives from this
    /// (true when the pick is a Magnetic variant) so the existing
    /// reader chain (NavigationData.CourseOverGround, alarm rules)
    /// keeps working without touching every read site.</summary>
    public string CogReadoutSource => _cogReadoutSource.Value;
    private readonly Setting<string> _cogReadoutSource = Setting.Text(
        SettingKeys.CogReadoutSource, OnaPlotter.Utilities.CogSourceResolver.DefaultSetting,
        normalize: CanonicalCogSource);

    /// <summary>Helm-picked source for the on-map own-COG vector.
    /// Independent of <see cref="CogReadoutSource"/>: a helm doing
    /// close-quarter manoeuvres can pick a realtime variant for the
    /// vector while keeping the HUD readout smoothed.</summary>
    public string OwnCogVectorSource => _ownCogVectorSource.Value;
    private readonly Setting<string> _ownCogVectorSource = Setting.Text(
        SettingKeys.OwnCogVectorSource, OnaPlotter.Utilities.CogSourceResolver.DefaultSetting,
        normalize: CanonicalCogSource);

    // Round-trip through the parser so an unknown / corrupted value falls
    // back to the safe default rather than poisoning the persisted setting.
    private static string CanonicalCogSource(string value)
        => OnaPlotter.Utilities.CogSourceResolver.ToSetting(
            OnaPlotter.Utilities.CogSourceResolver.Parse(value));

    /// <summary>Boolean view of the True/Magnetic axis of
    /// <see cref="CogReadoutSource"/>; set by changing that source.</summary>
    public bool PreferMagneticCourse =>
        OnaPlotter.Utilities.CogSourceResolver.IsMagnetic(
            OnaPlotter.Utilities.CogSourceResolver.Parse(CogReadoutSource));
    public bool AutoAdvanceWaypoints => _autoAdvanceWaypoints.Value;
    private readonly Setting<bool> _autoAdvanceWaypoints = Setting.Bool(SettingKeys.AutoAdvanceWaypoints, true);
    /// <summary>Helm-configured arrival-circle radius (metres) sent
    /// to the SignalK v2 Course API on Set-Destination /
    /// Set-Active-Route requests so the server adopts it as the
    /// effective <c>navigation.course.arrivalCircle</c>. 50 m is the
    /// helm-friendly default - within sight of the casualty / mooring
    /// without triggering APPROACH on every nearby buoy. Read sites
    /// for the EFFECTIVE circle (HUD ring, APPROACH alarm, auto-
    /// advance) keep using NavigationData.CourseArrivalCircleMeters
    /// (sourced from the SK delta) so a peer plotter changing it
    /// mid-passage still propagates here.</summary>
    public double ArrivalCircleMeters => _arrivalCircleMeters.Value;
    private readonly Setting<double> _arrivalCircleMeters = Setting.Number(
        SettingKeys.ArrivalCircleMeters, 50.0, normalize: v => Math.Clamp(v, 5.0, 1000.0));

    // OSM marine-POI overlay categories. All default false so a fresh
    // helm doesn't trigger Overpass round-trips on first chart load;
    // each is opt-in from Layers > Marine services. The overlay is
    // implicitly visible whenever at least one category is enabled -
    // no separate master flag.
    public bool MarinePoiFuelEnabled => _marinePoiFuelEnabled.Value;
    private readonly Setting<bool> _marinePoiFuelEnabled = Setting.Bool(SettingKeys.MarinePoiFuel, false);
    public bool MarinePoiMarinaEnabled => _marinePoiMarinaEnabled.Value;
    private readonly Setting<bool> _marinePoiMarinaEnabled = Setting.Bool(SettingKeys.MarinePoiMarina, false);
    public bool MarinePoiHarbourEnabled => _marinePoiHarbourEnabled.Value;
    private readonly Setting<bool> _marinePoiHarbourEnabled = Setting.Bool(SettingKeys.MarinePoiHarbour, false);
    public bool MarinePoiMooringEnabled => _marinePoiMooringEnabled.Value;
    private readonly Setting<bool> _marinePoiMooringEnabled = Setting.Bool(SettingKeys.MarinePoiMooring, false);
    public bool MarinePoiSlipwayEnabled => _marinePoiSlipwayEnabled.Value;
    private readonly Setting<bool> _marinePoiSlipwayEnabled = Setting.Bool(SettingKeys.MarinePoiSlipway, false);
    public bool MarinePoiPierEnabled => _marinePoiPierEnabled.Value;
    private readonly Setting<bool> _marinePoiPierEnabled = Setting.Bool(SettingKeys.MarinePoiPier, false);
    public bool MarinePoiChandleryEnabled => _marinePoiChandleryEnabled.Value;
    private readonly Setting<bool> _marinePoiChandleryEnabled = Setting.Bool(
        SettingKeys.MarinePoiChandlery, false);
    public bool MarinePoiDrinkingWaterEnabled => _marinePoiDrinkingWaterEnabled.Value;
    private readonly Setting<bool> _marinePoiDrinkingWaterEnabled = Setting.Bool(
        SettingKeys.MarinePoiDrinkingWater, false);
    public bool MarinePoiPumpOutEnabled => _marinePoiPumpOutEnabled.Value;
    private readonly Setting<bool> _marinePoiPumpOutEnabled = Setting.Bool(SettingKeys.MarinePoiPumpOut, false);

    private readonly HashSet<string> _enabledChartIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _enabledRouteIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _quickBarChartIds = new(StringComparer.Ordinal);
    private readonly List<string> _chartOrder = [];
    // Disabled alarm rules: per-Title kill-switch the helm flips from
    // Settings -> Alarms. Persisted as a newline-separated list. Empty
    // means every registered rule is armed (the default for fresh
    // installs). The set membership matches IAlarmRule.Title exactly
    // (uppercase, no whitespace) so AlarmManager.Evaluate can do a
    // single Contains check per rule.
    private readonly HashSet<string> _disabledAlarmRules = new(StringComparer.Ordinal);
    public IReadOnlySet<string> EnabledChartIds => _enabledChartIds;
    public IReadOnlySet<string> EnabledRouteIds => _enabledRouteIds;
    public IReadOnlySet<string> QuickBarChartIds => _quickBarChartIds;
    public IReadOnlyList<string> ChartOrder => _chartOrder;
    public IReadOnlySet<string> DisabledAlarmRules => _disabledAlarmRules;

    public double? MapViewLat { get; private set; }
    public double? MapViewLon { get; private set; }
    public int? MapViewZoom { get; private set; }

    /// <summary>Standalone-mode flag - when on, the SignalK base URL
    /// resolves to <see cref="StandaloneServerUrl"/> instead of the
    /// page origin. See <see cref="IServerSettings"/>. Default off so
    /// the bundled-webapp install path (the common case) keeps its
    /// origin-matches-server behaviour without any helm action.</summary>
    public bool StandaloneMode => _standaloneMode.Value;
    private readonly Setting<bool> _standaloneMode = Setting.Bool(SettingKeys.StandaloneMode, false);

    /// <summary>Origin of the SignalK server when standalone mode is
    /// on. Empty until the helm fills the field on Settings >
    /// Advanced > Standalone mode. Validated + normalised on
    /// <see cref="SetStandaloneServerUrlAsync"/>: scheme + host + port
    /// only, path/query/fragment stripped, host lower-cased.</summary>
    public string StandaloneServerUrl => _standaloneServerUrl.Value;
    // Normalise unconditionally so a trailing-slash / mixed-case
    // host / accidental copy-pasted path round-trips cleanly. An
    // empty / unparseable value is allowed and persists as "" -
    // SignalKBaseUrl falls back to the auto-detected origin in
    // that case rather than throwing.
    private readonly Setting<string> _standaloneServerUrl = Setting.Text(
        SettingKeys.StandaloneServerUrl, "", normalize: NormalizeStandaloneUrl);

    /// <summary>Persist the JWT across tab reloads. Default true: most
    /// helms expect "remember me" semantics out of the box and the JWT
    /// is rotated server-side at expiry anyway.</summary>
    public bool RememberSession => _rememberSession.Value;
    private readonly Setting<bool> _rememberSession = Setting.Bool(SettingKeys.RememberSession, true);

    /// <summary>OPT-IN to persist the password too. Off by default.
    /// See <see cref="IServerSettings.RememberPassword"/> for the risk
    /// trade-off.</summary>
    public bool RememberPassword => _rememberPassword.Value;
    private readonly Setting<bool> _rememberPassword = Setting.Bool(SettingKeys.RememberPassword, false);

    /// <summary>Last-used standalone username. Persisted unconditionally.</summary>
    public string StoredUsername => _storedUsername.Value;
    private readonly Setting<string> _storedUsername = Setting.Text(SettingKeys.StandaloneUsername, "");

    /// <summary>Helm password for auto-login. Empty when
    /// <see cref="RememberPassword"/> is off.</summary>
    public string StoredPassword => _storedPassword.Value;
    private readonly Setting<string> _storedPassword = Setting.Text(SettingKeys.StandalonePassword, "");

    public event Action? OnSettingsChanged;

    private readonly TimeProvider _time;

    public AppSettingsService(IKeyValueStore store, TimeProvider time)
    {
        _store = store;
        _time = time;
    }

    /// <summary>System clock; DI resolves the <see cref="TimeProvider"/> overload.</summary>
    public AppSettingsService(IKeyValueStore store) : this(store, TimeProvider.System) { }

    public async Task InitializeAsync()
    {
        // Key strings and the naming convention live in SettingKeys.
        if (_initialized) return;
        await _initLock.WaitAsync();
        try
        {
            if (_initialized) return;
            await KeyValueStoreMigration.MigrateRenamedAsync(_store, SettingKeys.Renamed);
            foreach (var setting in Persisted())
                setting.Load(await LoadString(setting.Key));
            // The nightModePreset key is intentionally not read: night
            // mode is a single soft red-shift on/off; helms wanting a
            // non-red dark intermediate use Theme = "dark" instead.
            LastManualNightToggleUtc = await LoadDateTimeUtc(SettingKeys.LastManualNightToggle);
            LastManualNightOverrideSunCluster = NormalizeSunCluster(
                await LoadString(SettingKeys.LastManualNightOverrideSunCluster));
            // The unified ship-track toggle (serverTrackVisible.v1)
            // gates both track layers. Any orphaned localTrackVisible.v1
            // entry in localStorage is never read.
            // Read raw to detect whether the key was ever stored. A
            // missing value triggers ApplyMobileFirstRunDefaultsAsync's
            // viewport-aware default; a stored "false" is respected.
            var sidebarRaw = await LoadString(SettingKeys.SidebarCollapsed);
            _sidebarCollapsedExplicit = sidebarRaw is not null;
            SidebarCollapsed = sidebarRaw == "true";
            // CPA / TCPA two-tier load with legacy migration. Helms
            // upgrading from the single-threshold model have their
            // tuned value preserved into the new alarm tier; awareness
            // lands on the fresh defaults. The legacy unversioned
            // keys are read once on bootstrap, never written - the
            // new ".v1" keys are the source of truth going forward,
            // and the legacy entries decay harmlessly when localStorage
            // is cleared.
            //
            // Migration logic per setting:
            //   * if new key present -> use it (helm has touched the
            //     setting under the new schema)
            //   * else if legacy key present -> migrate that value into
            //     the new tier (helm hasn't touched it since the
            //     upgrade)
            //   * else -> use the declared default
            CpaAlarmNm = await LoadDoubleFallback(
                SettingKeys.CpaAlarmNm, SettingKeys.LegacyCpaAlarmThreshold, 0.1);
            TcpaAlarmMin = await LoadDoubleFallback(
                SettingKeys.TcpaAlarmMin, SettingKeys.LegacyGuardZoneLookaheadMinutes, 30.0);
            CpaAwarenessNm = Math.Max(
                await LoadDouble(SettingKeys.CpaAwarenessNm, 1.0),
                CpaAlarmNm);
            TcpaAwarenessMin = Math.Max(
                await LoadDouble(SettingKeys.TcpaAwarenessMin, 30.0),
                TcpaAlarmMin);
            // AIS inactive / remove thresholds. The remove window is then
            // clamped to >= inactive so a corrupt or hand-edited storage
            // entry can't end up with remove < inactive (which would skip
            // the faded "still on chart" band entirely).
            AisInactiveMinutes = Math.Clamp(
                await LoadDouble(SettingKeys.AisInactiveMinutes, 10.0), 1.0, 240.0);
            AisRemoveMinutes = Math.Clamp(
                await LoadDouble(SettingKeys.AisRemoveMinutes, 30.0), 1.0, 240.0);
            if (AisRemoveMinutes < AisInactiveMinutes) AisRemoveMinutes = AisInactiveMinutes;
            // CogReadoutSource supersedes the legacy preferMagneticCourse.v1
            // boolean. When no CogReadoutSource is stored, a stored legacy
            // bool maps to the smoothed variant of the same axis (the
            // HUD COG that bool drove came from Avg.CogMean30Sec).
            // LoadString swallows the
            // JSException raised in private-browsing mode so the
            // migration probe is safe on storage-disabled clients.
            var legacyMagCourse = await LoadString(SettingKeys.LegacyPreferMagneticCourse);
            var migratedDefault = bool.TryParse(legacyMagCourse, out var lmc) && lmc
                ? "magneticSmoothed"
                : OnaPlotter.Utilities.CogSourceResolver.DefaultSetting;
            _cogReadoutSource.Assign(await LoadString(SettingKeys.CogReadoutSource) ?? migratedDefault);
            _ownCogVectorSource.Assign(await LoadString(SettingKeys.OwnCogVectorSource) ?? migratedDefault);
            LoadIdsInto(await LoadString(SettingKeys.EnabledChartIds), _enabledChartIds);
            LoadIdsInto(await LoadString(SettingKeys.EnabledRouteIds), _enabledRouteIds);
            LoadIdsInto(await LoadString(SettingKeys.ChartOrder), _chartOrder);
            LoadIdsInto(await LoadString(SettingKeys.DisabledAlarmRules), _disabledAlarmRules);
            LoadMapView(await LoadString(SettingKeys.MapView));

            // Quick-bar chart membership. If this key doesn't exist yet
            // (first load after upgrade), seed from EnabledChartIds so
            // existing users keep their chart shortcuts. Subsequent
            // runs read the persisted set as-is, even if it's empty.
            var quickBarRaw = await LoadString(SettingKeys.QuickBarChartIds);
            if (quickBarRaw is null)
            {
                foreach (var id in _enabledChartIds) _quickBarChartIds.Add(id);
                if (_quickBarChartIds.Count > 0)
                    await Save(SettingKeys.QuickBarChartIds, string.Join('\n', _quickBarChartIds));
            }
            else
            {
                LoadIdsInto(quickBarRaw, _quickBarChartIds);
            }
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }

        // Wake up subscribers that were constructed BEFORE init landed
        // (DI singletons like SignalKBaseUrl resolve their URL in the
        // ctor using whatever values the auto-properties had at that
        // moment - i.e. the type defaults, not the persisted KV). Without
        // this fan-out a stored StandaloneMode=true / StandaloneServerUrl
        // never reaches the WS pipeline on startup: the helm has to
        // toggle the switch off-and-on to trigger the next Set*Async,
        // the only other place the fan-out happens. Fires
        // outside the lock so a subscriber that calls back into the
        // service doesn't deadlock.
        OnSettingsChanged?.Invoke();
    }

    public Task SetNightModeAsync(bool value) => SetAsync(_nightMode, value);

    public async Task MarkManualNightToggleAsync(string? sunCluster)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        LastManualNightToggleUtc = now;
        await Save(SettingKeys.LastManualNightToggle, now.ToString("o", CultureInfo.InvariantCulture));

        // Cluster suppression: store the env.sun cluster (day / night)
        // at toggle time so CheckAutoNightAsync can suppress until
        // the next sun-state transition. null when env.sun is unknown
        // - in that case auto-night isn't running anyway and the
        // override has nothing to fight.
        LastManualNightOverrideSunCluster = NormalizeSunCluster(sunCluster);
        await Save(SettingKeys.LastManualNightOverrideSunCluster,
            LastManualNightOverrideSunCluster ?? "");
    }

    /// <summary>Clear the manual-override cluster. Called when
    /// CheckAutoNightAsync sees env.sun transition out of the
    /// override cluster - the override has done its job and
    /// auto-night can resume.</summary>
    public async Task ClearManualNightOverrideAsync()
    {
        if (LastManualNightOverrideSunCluster is null) return;
        LastManualNightOverrideSunCluster = null;
        await Save(SettingKeys.LastManualNightOverrideSunCluster, "");
    }

    /// <summary>Whitelist guard: persist only the two cluster
    /// values the rest of the code understands. Anything else (an
    /// empty string from a cleared override, a future env.sun word
    /// the plugin starts emitting, a corrupt localStorage value)
    /// collapses to null = "no override".</summary>
    private static string? NormalizeSunCluster(string? raw) => raw switch
    {
        "day" or "night" => raw,
        _ => null,
    };

    // One-shot flag read only at startup, so there is nothing to fan out.
    public Task MarkChartsSeededAsync() => SetQuietAsync(_chartsSeeded, true);

    public Task SetNightModeAutoAsync(bool value) => SetAsync(_nightModeAuto, value);

    public Task SetThemeAsync(string value) => SetAsync(_theme, value);

    private static string NormalizeTheme(string? raw) => raw switch
    {
        "light" or "dark" or "system" or "high-contrast" => raw,
        _ => "system",
    };

    public Task SetWindHeroModeAsync(string value) => SetAsync(_windHeroMode, value);

    private static string NormalizeWindHeroMode(string? raw) => raw switch
    {
        "apparent" or "true" => raw,
        _ => "apparent",
    };

    public Task SetMapOrientationAsync(string value) => SetAsync(_mapOrientation, value);

    public Task SetShipOrientationSourceAsync(string value) => SetAsync(_shipOrientationSource, value);

    // The chart layer toggles below persist without raising
    // OnSettingsChanged: the Map page applies each change to the chart
    // itself when the helm flips it, and no other subscriber reads them
    // live.
    public Task SetFollowBoatAsync(bool value) => SetQuietAsync(_followBoat, value);

    public Task SetLaylinesVisibleAsync(bool value) => SetQuietAsync(_laylinesVisible, value);

    public Task SetShipLinesVisibleAsync(bool value) => SetQuietAsync(_shipLinesVisible, value);

    public Task SetServerTrackVisibleAsync(bool value) => SetQuietAsync(_serverTrackVisible, value);

    public Task SetServerTrackDurationAsync(string value) => SetQuietAsync(_serverTrackDuration, value);

    public Task SetServerTrackResolutionAsync(string value) => SetQuietAsync(_serverTrackResolution, value);

    public Task SetServerTrackWithinBoundsAsync(bool value) => SetQuietAsync(_serverTrackWithinBounds, value);

    /// <summary>Whitelist guard so a corrupted localStorage value
    /// doesn't pin the helm to an invalid duration that the API
    /// rejects. Falls back to the new "all" default.</summary>
    private static string NormalizeServerTrackDuration(string? raw) => raw switch
    {
        "1h" or "6h" or "1d" or "3d" or "7d" or "all" => raw,
        _ => "all",
    };

    /// <summary>Same whitelist pattern for the resolution ladder;
    /// matches the values the History page dropdown ships.</summary>
    private static string NormalizeServerTrackResolution(string? raw) => raw switch
    {
        "1s" or "30s" or "1m" or "5m" or "15m" or "30m" or "1h" or "4h" => raw,
        _ => "15m",
    };

    public Task SetAtonsVisibleAsync(bool value) => SetAsync(_atonsVisible, value);

    public Task SetAisLabelsVisibleAsync(bool value) => SetAsync(_aisLabelsVisible, value);

    public Task SetChartUpscaleEnabledAsync(bool value) => SetAsync(_chartUpscaleEnabled, value);

    public Task SetChartUpscaleLevelsAsync(int value) => SetAsync(_chartUpscaleLevels, value);

    public Task SetWeatherOverlayOpacityAsync(double value) => SetAsync(_weatherOverlayOpacity, value);

    public Task SetChartContrastPercentAsync(int value) => SetAsync(_chartContrastPercent, value);

    public Task SetChartSaturationPercentAsync(int value) => SetAsync(_chartSaturationPercent, value);

    public Task SetChartBrightnessPercentAsync(int value) => SetAsync(_chartBrightnessPercent, value);

    public Task SetHarborModeAsync(bool value)
    {
        // No persistence: Harbor mode is in-memory only. See
        // IAppSettings.HarborMode docstring - a forgotten Harbor
        // mode silently riding into open water is the worst-case
        // scenario, so every fresh visit starts with collision
        // alarms armed.
        if (HarborMode == value) return Task.CompletedTask;
        HarborMode = value;
        OnSettingsChanged?.Invoke();
        return Task.CompletedTask;
    }

    public async Task SetSidebarCollapsedAsync(bool value)
    {
        SidebarCollapsed = value;
        _sidebarCollapsedExplicit = true;
        await Save(SettingKeys.SidebarCollapsed, value ? "true" : "false");
        OnSettingsChanged?.Invoke();
    }

    public async Task ApplyMobileFirstRunDefaultsAsync(bool isMobile)
    {
        // Already-set sidebar wins (user toggled it intentionally on a
        // previous visit, or the mobile-default ran on an earlier mount
        // - either way no overwrite). Desktop viewport doesn't change
        // anything; the default is "expanded" which is already
        // correct.
        if (_sidebarCollapsedExplicit) return;
        if (!isMobile) return;
        SidebarCollapsed = true;
        _sidebarCollapsedExplicit = true;
        await Save(SettingKeys.SidebarCollapsed, "true");
        OnSettingsChanged?.Invoke();
    }

    public Task SetDepthAlarmThresholdAsync(double value) => SetAsync(_depthAlarmThreshold, value);

    public async Task SetCpaAlarmNmAsync(double value)
    {
        // Sanitise: NaN / negative / zero would silently disable the
        // alarm tier (Threat.Alarm requires cpa <= CpaAlarmNm, so 0
        // makes that "anything <= 0" - never true). Pin to a small
        // positive minimum so the alarm always classifies *something*.
        if (!double.IsFinite(value) || value <= 0) value = 0.01;
        CpaAlarmNm = value;
        // Awareness must never sit below alarm: an alarm without a
        // preceding awareness tier breaks the "see it brewing before
        // you hear the klaxon" contract. Bump it up here so the
        // helm's order of input doesn't matter (raising the alarm
        // value past the awareness value adjusts awareness; it
        // doesn't invert the two bands).
        if (CpaAwarenessNm < CpaAlarmNm)
        {
            CpaAwarenessNm = CpaAlarmNm;
            await Save(SettingKeys.CpaAwarenessNm,
                CpaAwarenessNm.ToString("F2", CultureInfo.InvariantCulture));
        }
        await Save(SettingKeys.CpaAlarmNm, value.ToString("F2", CultureInfo.InvariantCulture));
        OnSettingsChanged?.Invoke();
    }

    public async Task SetTcpaAlarmMinAsync(double value)
    {
        if (!double.IsFinite(value) || value <= 0) value = 1.0;
        TcpaAlarmMin = value;
        if (TcpaAwarenessMin < TcpaAlarmMin)
        {
            TcpaAwarenessMin = TcpaAlarmMin;
            await Save(SettingKeys.TcpaAwarenessMin,
                TcpaAwarenessMin.ToString("F1", CultureInfo.InvariantCulture));
        }
        await Save(SettingKeys.TcpaAlarmMin, value.ToString("F1", CultureInfo.InvariantCulture));
        OnSettingsChanged?.Invoke();
    }

    public async Task SetCpaAwarenessNmAsync(double value)
    {
        // Clamp to be >= alarm. Same invariant as the alarm setter,
        // applied from the other direction: lowering awareness below
        // alarm would degenerate the awareness tier entirely
        // (Threat.Awareness requires cpa <= awareness AND > alarm).
        if (!double.IsFinite(value) || value <= 0) value = CpaAlarmNm;
        if (value < CpaAlarmNm) value = CpaAlarmNm;
        CpaAwarenessNm = value;
        await Save(SettingKeys.CpaAwarenessNm, value.ToString("F2", CultureInfo.InvariantCulture));
        OnSettingsChanged?.Invoke();
    }

    public async Task SetTcpaAwarenessMinAsync(double value)
    {
        if (!double.IsFinite(value) || value <= 0) value = TcpaAlarmMin;
        if (value < TcpaAlarmMin) value = TcpaAlarmMin;
        TcpaAwarenessMin = value;
        await Save(SettingKeys.TcpaAwarenessMin, value.ToString("F1", CultureInfo.InvariantCulture));
        OnSettingsChanged?.Invoke();
    }

    public Task SetCpaDebounceSecondsAsync(double value) => SetAsync(_cpaDebounceSeconds, value);

    public Task SetWindShiftAlarmThresholdAsync(double value) => SetAsync(_windShiftAlarmThreshold, value);

    public Task SetWindShiftLookbackMinutesAsync(double value) => SetAsync(_windShiftLookbackMinutes, value);

    public Task SetWindShiftMinTrueWindSpeedAsync(double value) => SetAsync(_windShiftMinTrueWindSpeed, value);

    public Task SetAnchorTideSafetyMarginAsync(double value) => SetAsync(_anchorTideSafetyMargin, value);

    public Task SetAnchorAutoRadiusSafetyMarginAsync(double value) => SetAsync(_anchorAutoRadiusSafetyMargin, value);

    public async Task SetAlarmRuleDisabledAsync(string title, bool disabled)
    {
        // Trim + uppercase normalisation: a UI binding's stray
        // whitespace or a future rename to mixed-case can't desync
        // the persisted set from IAlarmRule.Title (which is uppercase
        // by interface contract). Empty title is a no-op so a binding
        // that fires on its initial empty render doesn't poison the
        // set with "".
        var key = (title ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(key)) return;
        bool changed = disabled
            ? _disabledAlarmRules.Add(key)
            : _disabledAlarmRules.Remove(key);
        if (!changed) return;
        await Save(SettingKeys.DisabledAlarmRules,
            string.Join('\n', _disabledAlarmRules));
        OnSettingsChanged?.Invoke();
    }

    public Task SetSailingModeAsync(string value) => SetAsync(_sailingMode, value);

    public Task SetKeepScreenAwakeAsync(bool value) => SetAsync(_keepScreenAwake, value);

    public Task SetServerSideApproachAlarmsAsync(bool value) => SetAsync(_serverSideApproachAlarms, value);

    public Task SetShowKeyboardHintsAsync(bool value) => SetAsync(_showKeyboardHints, value);

    public Task SetShowAutopilotHudAsync(bool value) => SetAsync(_showAutopilotHud, value);

    public Task SetShowRadarHudAsync(bool value) => SetAsync(_showRadarHud, value);

    public Task SetShowDefaultHudAsync(bool value) => SetAsync(_showDefaultHud, value);

    public Task SetOwnCogVectorMinutesAsync(double value) => SetAsync(_ownCogVectorMinutes, value);

    public Task SetAisCogVectorMinutesAsync(double value) => SetAsync(_aisCogVectorMinutes, value);

    public async Task SetAisInactiveMinutesAsync(double value)
    {
        if (!double.IsFinite(value)) value = 5.0;
        value = Math.Clamp(value, 1.0, 240.0);
        AisInactiveMinutes = value;
        // Inactive raised past current Remove would skip the faded band.
        // Bump Remove in lockstep so the invariant Inactive <= Remove holds
        // without forcing the helm to edit two fields in a specific order.
        if (AisRemoveMinutes < value)
        {
            AisRemoveMinutes = value;
            await Save(SettingKeys.AisRemoveMinutes, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        await Save(SettingKeys.AisInactiveMinutes, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        OnSettingsChanged?.Invoke();
    }

    public async Task SetAisRemoveMinutesAsync(double value)
    {
        if (!double.IsFinite(value)) value = 10.0;
        value = Math.Clamp(value, 1.0, 240.0);
        // Remove < Inactive would mean targets get pruned before they ever
        // hit the faded band. Clamp up so the helm-visible state stays
        // ordered; the inverse direction is handled in SetAisInactive.
        if (value < AisInactiveMinutes) value = AisInactiveMinutes;
        AisRemoveMinutes = value;
        await Save(SettingKeys.AisRemoveMinutes, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        OnSettingsChanged?.Invoke();
    }

    public Task SetTideVisibleAsync(bool value) => SetAsync(_tideVisible, value);

    public Task SetRadarRangeRingsEnabledAsync(bool value) => SetAsync(_radarRangeRingsEnabled, value);

    public Task SetRadarRangeRingsCountAsync(int value) => SetAsync(_radarRangeRingsCount, value);

    public Task SetDistanceRingsEnabledAsync(bool value) => SetAsync(_distanceRingsEnabled, value);

    public Task SetDistanceRingsBaseNmAsync(double value) => SetAsync(_distanceRingsBaseNm, value);

    public Task SetDistanceRingsCountAsync(int value) => SetAsync(_distanceRingsCount, value);

    public Task SetRadarUseWireBearingAsync(bool value) => SetAsync(_radarUseWireBearing, value);

    public Task SetRadarBearingCorrectionDegAsync(double value) => SetAsync(_radarBearingCorrectionDeg, value);

    public Task SetPreferMagneticHeadingAsync(bool value) => SetAsync(_preferMagneticHeading, value);

    public Task SetCogReadoutSourceAsync(string value) => SetAsync(_cogReadoutSource, value);

    public Task SetOwnCogVectorSourceAsync(string value) => SetAsync(_ownCogVectorSource, value);

    public Task SetAutoAdvanceWaypointsAsync(bool value) => SetAsync(_autoAdvanceWaypoints, value);

    public Task SetArrivalCircleMetersAsync(double value) => SetAsync(_arrivalCircleMeters, value);

    // Marine POI category toggles. Same shape across all categories:
    // write the bool, persist under the canonical "marinePoi.<cat>.v1"
    // key, fan out OnSettingsChanged so MarinePoiController can
    // re-render. The overlay visibility is implicit: enabled iff at
    // least one category is on.
    public Task SetMarinePoiFuelEnabledAsync(bool value) => SetAsync(_marinePoiFuelEnabled, value);
    public Task SetMarinePoiMarinaEnabledAsync(bool value) => SetAsync(_marinePoiMarinaEnabled, value);
    public Task SetMarinePoiHarbourEnabledAsync(bool value) => SetAsync(_marinePoiHarbourEnabled, value);
    public Task SetMarinePoiMooringEnabledAsync(bool value) => SetAsync(_marinePoiMooringEnabled, value);
    public Task SetMarinePoiSlipwayEnabledAsync(bool value) => SetAsync(_marinePoiSlipwayEnabled, value);
    public Task SetMarinePoiPierEnabledAsync(bool value) => SetAsync(_marinePoiPierEnabled, value);
    public Task SetMarinePoiChandleryEnabledAsync(bool value) => SetAsync(_marinePoiChandleryEnabled, value);
    public Task SetMarinePoiDrinkingWaterEnabledAsync(bool value) => SetAsync(_marinePoiDrinkingWaterEnabled, value);
    public Task SetMarinePoiPumpOutEnabledAsync(bool value) => SetAsync(_marinePoiPumpOutEnabled, value);

    public Task SetManualAnchorRadiusMetersAsync(double value) => SetAsync(_manualAnchorRadiusMeters, value);

    public Task SetAnchorChainLengthMetersAsync(double value) => SetAsync(_anchorChainLengthMeters, value);

    public Task SetDeadmanTimeoutMinutesAsync(double value) => SetAsync(_deadmanTimeoutMinutes, value);

    public Task SetDeadmanNightMinutesAsync(double value) => SetAsync(_deadmanNightMinutes, value);

    public Task SetSnoozeDurationMinutesAsync(int value) => SetAsync(_snoozeDurationMinutes, value);

    public Task SetBigTypeAsync(bool value) => SetAsync(_bigType, value);

    public Task SetExpandAllHudAsync(bool value) => SetAsync(_expandAllHud, value);

    private static string NormalizeSailingMode(string? raw) => raw switch
    {
        "cruise" or "race" => raw,
        _ => "cruise",
    };

    private static string NormalizeOwnVesselType(string? raw) => raw switch
    {
        "power" or "sail" => raw,
        _ => "sail",
    };

    public Task SetOwnVesselTypeAsync(string value) => SetAsync(_ownVesselType, value);

    // SetEnabledCharts / SetEnabledRoutes persist without raising
    // OnSettingsChanged: the chart and route controllers own the live sets
    // and read these back only when the Map page mounts.
    public async Task SetEnabledChartsAsync(IEnumerable<string> ids)
    {
        // Materialise first - see SetChartOrderAsync for the aliasing
        // footgun this defends against.
        var copy = ids.ToList();
        _enabledChartIds.Clear();
        foreach (var id in copy) _enabledChartIds.Add(id);
        await Save(SettingKeys.EnabledChartIds, string.Join('\n', _enabledChartIds));
    }

    public async Task SetEnabledRoutesAsync(IEnumerable<string> ids)
    {
        var copy = ids.ToList();
        _enabledRouteIds.Clear();
        foreach (var id in copy) _enabledRouteIds.Add(id);
        await Save(SettingKeys.EnabledRouteIds, string.Join('\n', _enabledRouteIds));
    }

    public async Task SetQuickBarChartsAsync(IEnumerable<string> ids)
    {
        // Same materialise-first guard as SetChartOrderAsync - a caller
        // can hand us a LINQ view over _quickBarChartIds and the Clear()
        // would pull the rug out mid-iteration.
        var copy = ids.ToList();
        _quickBarChartIds.Clear();
        foreach (var id in copy) _quickBarChartIds.Add(id);
        await Save(SettingKeys.QuickBarChartIds, string.Join('\n', _quickBarChartIds));
        OnSettingsChanged?.Invoke();
    }

    public async Task SetChartOrderAsync(IEnumerable<string> ids)
    {
        // Materialise BEFORE clearing _chartOrder - otherwise a caller
        // passing `Settings.ChartOrder.Append(x)` (a LINQ enumerable
        // referencing _chartOrder) iterates an empty list and loses
        // every previously-ordered chart.
        var copy = ids.ToList();
        _chartOrder.Clear();
        foreach (var id in copy)
        {
            if (!string.IsNullOrEmpty(id) && !_chartOrder.Contains(id)) _chartOrder.Add(id);
        }
        await Save(SettingKeys.ChartOrder, string.Join('\n', _chartOrder));
        OnSettingsChanged?.Invoke();
    }

    // --- storage helpers: swallow read errors (missing key = default), propagate write errors ---

    private async Task Save(string key, string value)
    {
        try { await _store.SetAsync(key, value); }
        catch (Microsoft.JSInterop.JSException) { /* storage disabled - user sees nothing persisted; acceptable. */ }
        catch (Microsoft.JSInterop.JSDisconnectedException) { /* page tear-down race; tab closing while a settings toggle is in flight. */ }
    }

    private async Task<string?> LoadString(string key)
    {
        try { return await _store.GetAsync(key); }
        catch (Microsoft.JSInterop.JSException) { return null; }
        catch (Microsoft.JSInterop.JSDisconnectedException) { return null; }
    }

    private async Task<double> LoadDouble(string key, double fallback)
    {
        var v = await LoadString(key);
        return v is not null && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
            ? d : fallback;
    }

    /// <summary>Load a double from <paramref name="primaryKey"/>; on miss,
    /// fall back to <paramref name="legacyKey"/> (one-shot migration of
    /// pre-rename helms); on second miss, return <paramref name="fallback"/>.
    /// Used by the CPA / TCPA two-tier loader so an upgraded helm keeps
    /// the value they tuned under the old single-threshold name.</summary>
    private async Task<double> LoadDoubleFallback(
        string primaryKey, string legacyKey, double fallback)
    {
        var primary = await LoadString(primaryKey);
        if (primary is not null
            && double.TryParse(primary, NumberStyles.Float, CultureInfo.InvariantCulture, out double dp))
            return dp;
        var legacy = await LoadString(legacyKey);
        if (legacy is not null
            && double.TryParse(legacy, NumberStyles.Float, CultureInfo.InvariantCulture, out double dl))
            return dl;
        return fallback;
    }

    /// <summary>Load a persisted UTC timestamp. Stored as an ISO-8601
    /// string with kind=UTC; parse failure yields null so a corrupt
    /// entry falls back to "never toggled" rather than throwing.</summary>
    private async Task<DateTime?> LoadDateTimeUtc(string key)
    {
        var v = await LoadString(key);
        if (string.IsNullOrWhiteSpace(v)) return null;
        // RoundtripKind cannot be combined with AssumeUniversal /
        // AssumeLocal / AdjustToUniversal - the runtime throws
        // ArgumentException("ConflictingDateTimeRoundtripStyles").
        // Parse with RoundtripKind alone to preserve whatever kind
        // the stored string declares (the "o" format we write always
        // carries Z, so this is normally Utc); if the stored value
        // happens to have no kind information (legacy / hand-edited
        // localStorage), force Utc explicitly before normalising so
        // ToUniversalTime() doesn't misinterpret it as Local and
        // shift it by the browser's TZ offset.
        if (DateTime.TryParse(v, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var dt))
        {
            if (dt.Kind == DateTimeKind.Unspecified)
                dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            var utc = dt.ToUniversalTime();
            // Reject degenerate values (year 0001, MaxValue, or
            // anything pre-2020) so callers doing TimeSpan arithmetic
            // - e.g. the night-mode 12-hour manual-override window -
            // don't see a 700,000-hour interval and silently treat it
            // as "recent enough". Settings are device-local and we
            // know we never wrote a timestamp outside this range.
            if (utc.Year < 2020 || utc.Year > 2100)
            {
                Console.WriteLine($"[Settings] {key} out of range ('{v}'). Starting fresh.");
                return null;
            }
            return utc;
        }
        Console.WriteLine($"[Settings] {key} parse failed ('{v}'). Starting fresh.");
        return null;
    }

    private static void LoadIdsInto(string? raw, HashSet<string> target)
    {
        target.Clear();
        if (string.IsNullOrEmpty(raw)) return;
        foreach (var id in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            target.Add(id);
    }

    // Ordered variant: preserves list order (unlike HashSet) so
    // `chartOrder.v1` round-trips the user's chosen draw order exactly.
    private static void LoadIdsInto(string? raw, List<string> target)
    {
        target.Clear();
        if (string.IsNullOrEmpty(raw)) return;
        foreach (var id in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            target.Add(id);
    }

    // Map view state is stored as "lat|lon|zoom" in one key. A single
    // tuple is cheaper to write (one JS interop per move) than three
    // separate keys, and the "|" delimiter is future-proof: a later
    // revision can append fields (bearing, pitch) without breaking
    // the parse of existing entries.
    //
    // Pragmatic policy: a malformed entry logs a single warning to
    // the browser console and leaves the three MapView* properties
    // null, which the Map page falls back on. We never surface the
    // parse error to the user.
    private void LoadMapView(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        var parts = raw.Split('|');
        if (parts.Length < 3)
        {
            Console.WriteLine($"[Settings] mapView.v1 ignored: expected 'lat|lon|zoom', got '{raw}'. Starting fresh.");
            return;
        }
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)
            || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var zoom))
        {
            Console.WriteLine($"[Settings] mapView.v1 parse failed ('{raw}'). Starting fresh.");
            return;
        }
        // Basic sanity bounds; out-of-range likely means a corrupt
        // entry rather than a legitimate pick. World-wrapping at 180
        // is handled by Leaflet itself.
        if (lat < -90 || lat > 90 || lon < -540 || lon > 540 || zoom < 0 || zoom > 25)
        {
            Console.WriteLine($"[Settings] mapView.v1 out of range ('{raw}'). Starting fresh.");
            return;
        }
        MapViewLat = lat;
        MapViewLon = lon;
        MapViewZoom = zoom;
    }

    public async Task SetMapViewAsync(double lat, double lon, int zoom)
    {
        MapViewLat = lat;
        MapViewLon = lon;
        MapViewZoom = zoom;
        var serialized = string.Format(CultureInfo.InvariantCulture,
            "{0:G9}|{1:G9}|{2}", lat, lon, zoom);
        await Save(SettingKeys.MapView, serialized);
        // No OnSettingsChanged fire here: the map view is purely
        // client-local and firing on every pan would cascade through
        // every settings subscriber for no gain.
    }

    // Fan-out: SignalKBaseUrl re-resolves its origin off the new flag;
    // SignalkClient drops + reopens its WebSocket.
    public Task SetStandaloneModeAsync(bool value) => SetAsync(_standaloneMode, value);

    // The normaliser strips path / query and lower-cases the host, so a
    // trailing slash or copy-pasted path round-trips cleanly. An empty or
    // unparseable value persists as "" and SignalKBaseUrl falls back to the
    // auto-detected origin.
    public Task SetStandaloneServerUrlAsync(string value) => SetAsync(_standaloneServerUrl, value);

    public Task SetRememberSessionAsync(bool value) => SetAsync(_rememberSession, value);

    public async Task SetRememberPasswordAsync(bool value)
    {
        _rememberPassword.Assign(value);
        await Persist(_rememberPassword);
        // When the helm flips the flag OFF, also wipe any previously
        // persisted password. Without this, an opt-out leaves the
        // value sitting in localStorage where the next opt-in (or a
        // bug, or an XSS) could pick it up. The in-memory copy is
        // wiped too so the UI text field clears on the same render.
        if (!value && !string.IsNullOrEmpty(StoredPassword))
        {
            _storedPassword.Assign("");
            await Persist(_storedPassword);
        }
        OnSettingsChanged?.Invoke();
    }

    // The username is informational, not a credential, and storing the
    // password isn't a server-state change (the auto-login flow re-issues
    // a JWT on its own schedule), so neither fans out OnSettingsChanged;
    // that would cascade into the WS reconnect path for no reason.
    public Task SetStoredUsernameAsync(string value) => SetQuietAsync(_storedUsername, value ?? "");

    public Task SetStoredPasswordAsync(string value) => SetQuietAsync(_storedPassword, value ?? "");

    /// <summary>Normalise a helm-supplied SignalK server URL into
    /// scheme + host + port (no path, no query, no fragment, host
    /// lower-cased). Returns an empty string for null / whitespace /
    /// unparseable input rather than throwing - the resolver falls
    /// back to the auto-detected origin in that case.</summary>
    internal static string NormalizeStandaloneUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var trimmed = raw.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return "";
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return "";
        // Uri.Port is -1 for the default-port case; substitute the
        // scheme default so we always emit an explicit port. Keeps
        // resolved BaseUrl strings comparable across reloads.
        var port = uri.IsDefaultPort
            ? (uri.Scheme == Uri.UriSchemeHttps ? 443 : 80)
            : uri.Port;
        return $"{uri.Scheme}://{uri.Host.ToLowerInvariant()}:{port}";
    }
}
