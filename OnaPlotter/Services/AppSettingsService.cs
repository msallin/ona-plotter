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

    public bool NightMode { get; private set; }
    public bool NightModeAuto { get; private set; } = false;
    public DateTime? LastManualNightToggleUtc { get; private set; }
    public string? LastManualNightOverrideSunCluster { get; private set; }
    public bool ChartsSeeded { get; private set; }
    public string Theme { get; private set; } = "system";
    public string WindHeroMode { get; private set; } = "apparent";
    public string MapOrientation { get; private set; } = "north";
    public string ShipOrientationSource { get; private set; } =
        OnaPlotter.Utilities.ShipOrientationResolver.DefaultSetting;
    public bool FollowBoat { get; private set; } = true;
    public bool LaylinesVisible { get; private set; }
    /// <summary>Master gate for own-ship indicator lines on the chart
    /// (COG vector, tidal current arrow, laylines). Default true so
    /// existing installs see the lines they always saw.</summary>
    public bool ShipLinesVisible { get; private set; } = true;

    /// <summary>Master ship-track visibility (gates BOTH the local
    /// in-memory SOG trail AND the SignalK history polyline; see
    /// <see cref="IMapDisplaySettings.ServerTrackVisible"/> for the
    /// unification rationale). Default true.</summary>
    public bool ServerTrackVisible { get; private set; } = true;
    /// <summary>Helm-picked window (1h..7d / all). Default "all".</summary>
    public string ServerTrackDuration { get; private set; } = "all";
    /// <summary>Sampling resolution (1s..4h). Default "15m" so the
    /// "all time" window stays loadable on first paint.</summary>
    public string ServerTrackResolution { get; private set; } = "15m";
    /// <summary>Clip the rendered polyline to the current viewport
    /// (re-clipped on pan/zoom without a re-fetch). Default true.</summary>
    public bool ServerTrackWithinBounds { get; private set; } = true;
    public bool AtonsVisible { get; private set; } = true;
    /// <summary>AIS vessel name labels (per-target tooltips on the chart).
    /// Default true. Independent of <see cref="HarborMode"/>: harbor mode
    /// also suppresses labels for as long as it's on, but the helm-set
    /// AisLabelsVisible flag is the persistent preference. Effective
    /// rule: labels render only when <c>AisLabelsVisible AND
    /// !HarborMode</c>. Toggling harbor mode off does NOT silently
    /// resurrect labels the helm previously hid via this setting.</summary>
    public bool AisLabelsVisible { get; private set; } = true;
    public double WeatherOverlayOpacity { get; private set; } = OnaPlotter.Utilities.WeatherOpacity.DefaultFraction;
    /// <summary>Chart-display CSS filter percentages - helm boosts /
    /// dampens contrast / saturation / brightness from the Layers panel
    /// to read washed-out raster charts. Defaults are identity (100 =
    /// no filter); see <see cref="OnaPlotter.Utilities.ChartFilter"/>
    /// for clamp limits + format.</summary>
    public int ChartContrastPercent { get; private set; } = OnaPlotter.Utilities.ChartFilter.DefaultPercent;
    public int ChartSaturationPercent { get; private set; } = OnaPlotter.Utilities.ChartFilter.DefaultPercent;
    public int ChartBrightnessPercent { get; private set; } = OnaPlotter.Utilities.ChartFilter.DefaultPercent;
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
    // Asymmetry vs the default-on flip: LoadBool below treats any
    // stored value other than the literal "true" as false (it doesn't
    // fall back to the default for non-"true" values). So a corrupted
    // localStorage entry (older build with a different write format,
    // hand-edited, browser-extension synced from a different OS
    // variant) lands the helm in upscale-OFF rather than this new
    // default-on. Pinned by AppSettingsServiceTests.ChartUpscaleEnabled
    // _GarbageStored_LoadsAsFalse: corruption-path helms can recover
    // by toggling the master flag in Settings.
    public bool ChartUpscaleEnabled { get; private set; } = true;
    public int ChartUpscaleLevels { get; private set; } = OnaPlotter.Utilities.ChartUpscale.DefaultLevels;
    /// <summary>In-memory only - never persisted, never restored.
    /// See IAppSettings.HarborMode for the rationale (a forgotten
    /// Harbor mode silently riding into open water is the worst-case
    /// scenario, so every fresh visit starts with collision alarms
    /// armed).</summary>
    public bool HarborMode { get; private set; }
    public bool SidebarCollapsed { get; private set; }
    // Defaults reviewed 2026-05 after helm field-feedback that the
    // first-install thresholds tripped too easily. Each retuned to
    // be less sensitive while staying conservative for the use case:
    //   SHALLOW         3.0 -> 2.0 m: 3 m tripped on every chop in
    //                                 typical 4-5 m anchorages.
    //   ANCHOR TIDE     1.0 -> 0.5 m: 1 m clearance fired hours
    //                                 before any real risk.
    //   WIND SHIFT     15   -> 30 deg: 15 deg is normal sailing
    //                                  oscillation; 30 deg matches
    //                                  the helm-noticeable threshold.
    //   WIND lookback   5   -> 10 min: longer averaging window cuts
    //                                  short-gust false positives.
    //   WIND min TWS    3   -> 5 kn:   suppress more wobble in
    //                                  light air where TWD is noisy.
    public double DepthAlarmThreshold { get; private set; } = 2.0;
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
    public double CpaDebounceSeconds { get; private set; } = 5.0;
    public double WindShiftAlarmThreshold { get; private set; } = 30.0;
    public double WindShiftLookbackMinutes { get; private set; } = 10.0;
    public double WindShiftMinTrueWindSpeed { get; private set; } = 5.0;
    // Boat draft is read from SignalK (design.draft.current / .maximum)
    // via NavigationData.DraftFromSignalK; no manual override here.
    public double AnchorTideSafetyMargin { get; private set; } = 0.5;
    /// <summary>Pad added on top of swing + tide-drop in the
    /// auto-anchor-radius preview. 5 m is the helm-tested cushion;
    /// raise for soft-mud anchorages.</summary>
    public double AnchorAutoRadiusSafetyMargin { get; private set; } = 5.0;
    public double ManualAnchorRadiusMeters { get; private set; } = 30.0;
    public double DeadmanTimeoutMinutes { get; private set; } = 0.0;
    public double DeadmanNightMinutes { get; private set; } = 15.0;
    public int SnoozeDurationMinutes { get; private set; } = 10;
    public bool BigType { get; private set; } = false;
    public bool ExpandAllHud { get; private set; } = false;
    public string SailingMode { get; private set; } = "cruise";
    /// <summary>Own propulsion category for COLREGS Rule 18 priority.
    /// "sail" (default) or "power". Stored as a string rather than an
    /// enum so persistence + the Settings select bind directly.</summary>
    public string OwnVesselType { get; private set; } = "sail";
    public bool KeepScreenAwake { get; private set; } = true;
    /// <summary>Default true: prefer server-side
    /// <c>notifications.navigation.*</c> from signalk-course-data
    /// over the client-side WaypointApproach alarm. Helms with a
    /// course-provider plugin get one consistent arrival cue (the
    /// banner agrees with the autopilot's arrival logic). When false
    /// the client <c>WaypointApproachAlarmRule</c> takes over, also
    /// using the server's <c>navigation.course.arrivalCircle</c> as
    /// the threshold so client and server agree on the boundary.</summary>
    public bool ServerSideApproachAlarms { get; private set; } = true;
    public bool ShowKeyboardHints { get; private set; } = false;
    public bool ShowAutopilotHud { get; private set; } = false;
    public bool ShowRadarHud { get; private set; } = false;
    // Defaults true so existing installs see the same four corner
    // panels they always did; toggle is for helms running external
    // instruments or wanting a clean screenshot.
    public bool ShowDefaultHud { get; private set; } = true;
    /// <summary>How far ahead own-vessel COG vector projects (minutes).
    /// Default 10 covers harbour entry + coastal pilotage; helms doing
    /// long passages bump to 20-30 for forward-planning, helms in
    /// thick traffic shorten to 3-5 to declutter their own predictor.</summary>
    public double OwnCogVectorMinutes { get; private set; } = 10.0;
    /// <summary>Same look-ahead horizon for AIS targets. Independent
    /// of own-vessel so a busy-harbour helm can shorten target vectors
    /// without losing their own predictor's reach.</summary>
    public double AisCogVectorMinutes { get; private set; } = 10.0;

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
    public bool TideVisible { get; private set; } = true;

    /// <summary>Helm-configured distance-ring overlay enabled. Default
    /// false (opt-in from Settings -> Display). Independent of the
    /// radar-range-rings flag below and of any alarm setting.</summary>
    public bool DistanceRingsEnabled { get; private set; } = false;
    /// <summary>Base distance-ring radius in nautical miles. Default
    /// 0.5 nm; Nth ring sits at BaseNm × N. Clamped 0.05..50 on the
    /// setter.</summary>
    public double DistanceRingsBaseNm { get; private set; } = 0.5;
    /// <summary>How many concentric distance rings to draw. Default
    /// 4 (so the default config draws rings at 0.5/1.0/1.5/2.0 nm).
    /// Clamped 1..8 on the setter.</summary>
    public int DistanceRingsCount { get; private set; } = 4;

    /// <summary>Radar range-ring overlay enabled. Default true.</summary>
    public bool RadarRangeRingsEnabled { get; private set; } = true;
    /// <summary>How many concentric range rings to draw. Default 4
    /// (quarter / half / three-quarter / full radar range).</summary>
    public int RadarRangeRingsCount { get; private set; } = 4;
    /// <summary>Trust the wire spoke <c>bearing</c> field as true-north
    /// (opt-in). Default false; see <see cref="IMapDisplaySettings.RadarUseWireBearing"/>
    /// for why most installs should leave it off.</summary>
    public bool RadarUseWireBearing { get; private set; } = false;
    /// <summary>Helm-side bearing trim in degrees, added to every spoke's
    /// canvas index on top of the radar's installation
    /// <c>bearingAlignment</c>. Default 0; clamped to -180..180. See
    /// <see cref="IMapDisplaySettings.RadarBearingCorrectionDeg"/>.</summary>
    public double RadarBearingCorrectionDeg { get; private set; } = 0.0;

    public bool PreferMagneticHeading { get; private set; } = false;

    /// <summary>Helm-picked source for the HUD numerical COG
    /// readout. Persisted as <c>cogReadoutSource.v1</c>. The legacy
    /// <see cref="PreferMagneticCourse"/> boolean derives from this
    /// (true when the pick is a Magnetic variant) so the existing
    /// reader chain (NavigationData.CourseOverGround, alarm rules)
    /// keeps working without touching every read site.</summary>
    public string CogReadoutSource { get; private set; } =
        OnaPlotter.Utilities.CogSourceResolver.DefaultSetting;

    /// <summary>Helm-picked source for the on-map own-COG vector.
    /// Independent of <see cref="CogReadoutSource"/>: a helm doing
    /// close-quarter manoeuvres can pick a realtime variant for the
    /// vector while keeping the HUD readout smoothed.</summary>
    public string OwnCogVectorSource { get; private set; } =
        OnaPlotter.Utilities.CogSourceResolver.DefaultSetting;

    /// <summary>Back-compat boolean derived from the True/Magnetic
    /// axis of <see cref="CogReadoutSource"/>. Kept as a property so
    /// existing readers (NavigationData, AlarmContext, SignalkClient
    /// cache) don't need to change. The setter
    /// <see cref="SetPreferMagneticCourseAsync"/> now flips
    /// <see cref="CogReadoutSource"/>'s True/Magnetic axis while
    /// preserving the smoothed/realtime axis.</summary>
    public bool PreferMagneticCourse =>
        OnaPlotter.Utilities.CogSourceResolver.IsMagnetic(
            OnaPlotter.Utilities.CogSourceResolver.Parse(CogReadoutSource));
    public bool AutoAdvanceWaypoints { get; private set; } = true;
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
    public double ArrivalCircleMeters { get; private set; } = 50.0;

    // OSM marine-POI overlay categories. All default false so a fresh
    // helm doesn't trigger Overpass round-trips on first chart load;
    // each is opt-in from Layers > Marine services. The overlay is
    // implicitly visible whenever at least one category is enabled -
    // no separate master flag.
    public bool MarinePoiFuelEnabled { get; private set; } = false;
    public bool MarinePoiMarinaEnabled { get; private set; } = false;
    public bool MarinePoiHarbourEnabled { get; private set; } = false;
    public bool MarinePoiMooringEnabled { get; private set; } = false;
    public bool MarinePoiSlipwayEnabled { get; private set; } = false;
    public bool MarinePoiPierEnabled { get; private set; } = false;
    public bool MarinePoiChandleryEnabled { get; private set; } = false;
    public bool MarinePoiDrinkingWaterEnabled { get; private set; } = false;
    public bool MarinePoiPumpOutEnabled { get; private set; } = false;

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
    public bool StandaloneMode { get; private set; }

    /// <summary>Origin of the SignalK server when standalone mode is
    /// on. Empty until the helm fills the field on Settings >
    /// Advanced > Standalone mode. Validated + normalised on
    /// <see cref="SetStandaloneServerUrlAsync"/>: scheme + host + port
    /// only, path/query/fragment stripped, host lower-cased.</summary>
    public string StandaloneServerUrl { get; private set; } = "";

    /// <summary>Persist the JWT across tab reloads. Default true: most
    /// helms expect "remember me" semantics out of the box and the JWT
    /// is rotated server-side at expiry anyway.</summary>
    public bool RememberSession { get; private set; } = true;

    /// <summary>OPT-IN to persist the password too. Off by default.
    /// See <see cref="IServerSettings.RememberPassword"/> for the risk
    /// trade-off.</summary>
    public bool RememberPassword { get; private set; } = false;

    /// <summary>Last-used standalone username. Persisted unconditionally.</summary>
    public string StoredUsername { get; private set; } = "";

    /// <summary>Helm password for auto-login. Empty when
    /// <see cref="RememberPassword"/> is off.</summary>
    public string StoredPassword { get; private set; } = "";

    public event Action? OnSettingsChanged;

    public AppSettingsService(IKeyValueStore store) => _store = store;

    public async Task InitializeAsync()
    {
        // KV key naming convention (any new key MUST follow this):
        //   * "<camelCaseName>.vN" where N is the schema version of the
        //     value. Bump N on a breaking format change (e.g. bool ->
        //     enum string, scalar -> struct) so a load-time mismatch
        //     surfaces as "key not found, use default" instead of a
        //     deserialise crash that leaves localStorage poisoned.
        //   * Older keys without ".vN" predate the convention; leave
        //     them as-is until they need a format change. Don't add
        //     new ".v1" duplicates of existing un-versioned keys.
        //   * Reads + writes share the exact key string; the load
        //     site is the single source of truth.
        if (_initialized) return;
        await _initLock.WaitAsync();
        try
        {
            if (_initialized) return;
            NightMode = await LoadBool("nightMode", false);
            NightModeAuto = await LoadBool("nightModeAuto.v1", false);
            LastManualNightToggleUtc = await LoadDateTimeUtc("lastManualNightToggle.v1");
            LastManualNightOverrideSunCluster = NormalizeSunCluster(
                await LoadString("lastManualNightOverrideSunCluster.v1"));
            ChartsSeeded = await LoadBool("chartsSeeded.v1", false);
            // nightModePreset key intentionally not migrated. The previous
            // dusk / soft / amber / red cycle collapsed to a single
            // soft red-shift on/off; helms wanting a non-red dark
            // intermediate use Theme = "dark" instead.
            Theme = NormalizeTheme(await LoadString("theme"));
            WindHeroMode = NormalizeWindHeroMode(await LoadString("windHeroMode.v1"));
            MapOrientation = await LoadString("mapOrientation") ?? "north";
            // Round-trip through the resolver so a corrupt /
            // schema-skew localStorage value lands at the default
            // ("headingTrue") instead of crashing the Map page.
            ShipOrientationSource = OnaPlotter.Utilities.ShipOrientationResolver.ToSetting(
                OnaPlotter.Utilities.ShipOrientationResolver.Parse(
                    await LoadString("shipOrientationSource.v1")));
            FollowBoat = await LoadBool("followBoat", true);
            LaylinesVisible = await LoadBool("laylinesVisible", false);
            ShipLinesVisible = await LoadBool("shipLinesVisible.v1", true);
            // localTrackVisible.v1 was retired; the unified ship-track
            // toggle (serverTrackVisible.v1) now gates both layers.
            // Legacy localStorage entries from previous versions stay
            // orphaned; we never read them again.
            ServerTrackVisible = await LoadBool("serverTrackVisible.v1", true);
            ServerTrackDuration = NormalizeServerTrackDuration(
                await LoadString("serverTrackDuration.v1"));
            ServerTrackResolution = NormalizeServerTrackResolution(
                await LoadString("serverTrackResolution.v1"));
            ServerTrackWithinBounds = await LoadBool("serverTrackWithinBounds.v1", true);
            AtonsVisible = await LoadBool("atonsVisible.v1", true);
            AisLabelsVisible = await LoadBool("aisLabelsVisible.v1", true);
            WeatherOverlayOpacity = await LoadDouble("weatherOverlayOpacity.v1",
                OnaPlotter.Utilities.WeatherOpacity.DefaultFraction);
            // chartContrast/sat/bright go through LoadDouble + cast for the
            // same reason as ChartUpscaleLevels above: one numeric helper,
            // identity-default on parse failure, then the clamp helper
            // pins a corrupt-but-parseable value into the supported range.
            ChartContrastPercent = OnaPlotter.Utilities.ChartFilter.ClampContrastSaturation(
                (int)await LoadDouble("chartContrastPercent.v1",
                    OnaPlotter.Utilities.ChartFilter.DefaultPercent));
            ChartSaturationPercent = OnaPlotter.Utilities.ChartFilter.ClampContrastSaturation(
                (int)await LoadDouble("chartSaturationPercent.v1",
                    OnaPlotter.Utilities.ChartFilter.DefaultPercent));
            ChartBrightnessPercent = OnaPlotter.Utilities.ChartFilter.ClampBrightness(
                (int)await LoadDouble("chartBrightnessPercent.v1",
                    OnaPlotter.Utilities.ChartFilter.DefaultPercent));
            ChartUpscaleEnabled = await LoadBool("chartUpscaleEnabled.v1", true);
            // chartUpscaleLevels.v1 is stored as an integer string ("2")
            // but read via LoadDouble + cast: this matches the same
            // pattern SnoozeDurationMinutes uses, and keeps a single
            // double-parsing helper for the whole service. The cast
            // truncates toward zero (so "2.7" -> 2, "-3.9" -> -3) and
            // then ClampLevels guards the 0..3 range, so a corrupted
            // localStorage value of any double-shaped string still
            // resolves into the supported range.
            ChartUpscaleLevels = OnaPlotter.Utilities.ChartUpscale.ClampLevels(
                (int)await LoadDouble("chartUpscaleLevels.v1",
                    OnaPlotter.Utilities.ChartUpscale.DefaultLevels));
            // Read raw to detect whether the key was ever stored. A
            // missing value triggers ApplyMobileFirstRunDefaultsAsync's
            // viewport-aware default; a stored "false" is respected.
            var sidebarRaw = await LoadString("sidebarCollapsed.v1");
            _sidebarCollapsedExplicit = sidebarRaw is not null;
            SidebarCollapsed = sidebarRaw == "true";
            // Defaults retuned 2026-05 (see field declarations above
            // for rationale). Existing helms keep their stored values;
            // these defaults only apply on first install.
            DepthAlarmThreshold = await LoadDouble("depthAlarmThreshold", 2.0);
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
                "cpaAlarmNm.v1", "cpaAlarmThreshold", 0.1);
            TcpaAlarmMin = await LoadDoubleFallback(
                "tcpaAlarmMin.v1", "guardZoneLookaheadMinutes", 30.0);
            CpaAwarenessNm = Math.Max(
                await LoadDouble("cpaAwarenessNm.v1", 1.0),
                CpaAlarmNm);
            TcpaAwarenessMin = Math.Max(
                await LoadDouble("tcpaAwarenessMin.v1", 30.0),
                TcpaAlarmMin);
            CpaDebounceSeconds = Math.Clamp(
                await LoadDouble("cpaDebounceSeconds.v1", 5.0), 0.0, 60.0);
            WindShiftAlarmThreshold = await LoadDouble("windShiftAlarmThreshold", 30.0);
            WindShiftLookbackMinutes = await LoadDouble("windShiftLookbackMinutes", 10.0);
            WindShiftMinTrueWindSpeed = await LoadDouble("windShiftMinTrueWindSpeed.v1", 5.0);
            AnchorTideSafetyMargin = await LoadDouble("anchorTideSafetyMargin", 0.5);
            AnchorAutoRadiusSafetyMargin = await LoadDouble("anchorAutoRadiusSafetyMargin.v1", 5.0);
            ManualAnchorRadiusMeters = await LoadDouble("manualAnchorRadiusMeters.v1", 30.0);
            DeadmanTimeoutMinutes = await LoadDouble("deadmanTimeoutMinutes.v1", 0.0);
            DeadmanNightMinutes = await LoadDouble("deadmanNightMinutes.v1", 15.0);
            SnoozeDurationMinutes = (int)await LoadDouble("snoozeDurationMinutes.v1", 10.0);
            BigType = await LoadBool("bigType.v1", false);
            ExpandAllHud = await LoadBool("expandAllHud.v1", false);
            SailingMode = NormalizeSailingMode(await LoadString("sailingMode"));
            OwnVesselType = NormalizeOwnVesselType(await LoadString("ownVesselType.v1"));
            KeepScreenAwake = await LoadBool("keepScreenAwake.v1", true);
            // arrivalCircleMeters.v1 (loaded above) is the helm-
            // configured arrival circle SENT to the SK v2 Course API
            // on Set-Destination / Set-Active-Route requests. Read
            // sites for the EFFECTIVE circle (HUD ring, APPROACH
            // alarm, auto-advance) read navigation.course.arrivalCircle
            // from the SK delta so a peer plotter or server-side
            // change still propagates.
            ServerSideApproachAlarms = await LoadBool("serverSideApproachAlarms.v1", true);
            ShowKeyboardHints = await LoadBool("showKeyboardHints.v1", false);
            ShowAutopilotHud = await LoadBool("showAutopilotHud.v1", false);
            ShowRadarHud = await LoadBool("showRadarHud.v1", false);
            ShowDefaultHud = await LoadBool("showDefaultHud.v1", true);
            OwnCogVectorMinutes = await LoadDouble("ownCogVectorMinutes.v1", 10.0);
            AisCogVectorMinutes = await LoadDouble("aisCogVectorMinutes.v1", 10.0);
            // AIS inactive / remove thresholds. The remove window is then
            // clamped to >= inactive so a corrupt or hand-edited storage
            // entry can't end up with remove < inactive (which would skip
            // the faded "still on chart" band entirely).
            AisInactiveMinutes = Math.Clamp(
                await LoadDouble("aisInactiveMinutes.v1", 10.0), 1.0, 240.0);
            AisRemoveMinutes = Math.Clamp(
                await LoadDouble("aisRemoveMinutes.v1", 30.0), 1.0, 240.0);
            if (AisRemoveMinutes < AisInactiveMinutes) AisRemoveMinutes = AisInactiveMinutes;
            TideVisible = await LoadBool("tideVisible.v1", true);
            RadarRangeRingsEnabled = await LoadBool("radarRangeRingsEnabled.v1", true);
            // 4 covers quarter / half / three-quarter / full range,
            // the standard chartplotter pattern. Clamp 1..8 to keep
            // the chart from turning into a bullseye.
            RadarRangeRingsCount = (int)Math.Clamp(
                await LoadDouble("radarRangeRingsCount.v1", 4.0), 1.0, 8.0);
            // Distance rings: pure visual scaffolding centred on own
            // boat, independent of the radar overlay and of any alarm.
            // Default off; the setters re-clamp on every write so a
            // corrupted localStorage value (zero / negative / huge)
            // can't disable the rings via the radius-collapsed path
            // or paint outside the chart.
            DistanceRingsEnabled = await LoadBool("distanceRingsEnabled.v1", false);
            DistanceRingsBaseNm = Math.Clamp(
                await LoadDouble("distanceRingsBaseNm.v1", 0.5), 0.05, 50.0);
            DistanceRingsCount = (int)Math.Clamp(
                await LoadDouble("distanceRingsCount.v1", 4.0), 1.0, 8.0);
            RadarUseWireBearing = await LoadBool("radarUseWireBearing.v1", false);
            RadarBearingCorrectionDeg = Math.Clamp(
                await LoadDouble("radarBearingCorrection.v1", 0.0), -180.0, 180.0);
            PreferMagneticHeading = await LoadBool("preferMagneticHeading.v1", false);
            // CogReadoutSource replaces the legacy preferMagneticCourse.v1
            // boolean. On first load after the upgrade, migrate the old
            // bool -> the smoothed variant of the same axis (matches
            // the helm's prior experience: HUD always ran on smoothed
            // COG via Avg.CogMean30Sec). LoadString swallows the
            // JSException raised in private-browsing mode so the
            // migration probe is safe on storage-disabled clients.
            var legacyMagCourse = await LoadString("preferMagneticCourse.v1");
            var migratedDefault = bool.TryParse(legacyMagCourse, out var lmc) && lmc
                ? "magneticSmoothed"
                : OnaPlotter.Utilities.CogSourceResolver.DefaultSetting;
            CogReadoutSource = OnaPlotter.Utilities.CogSourceResolver.ToSetting(
                OnaPlotter.Utilities.CogSourceResolver.Parse(
                    await LoadString("cogReadoutSource.v1") ?? migratedDefault));
            OwnCogVectorSource = OnaPlotter.Utilities.CogSourceResolver.ToSetting(
                OnaPlotter.Utilities.CogSourceResolver.Parse(
                    await LoadString("ownCogVectorSource.v1") ?? migratedDefault));
            AutoAdvanceWaypoints = await LoadBool("autoAdvanceWaypoints.v1", true);
            // Clamp the arrival circle to a sensible boating range:
            // 5 m is too tight for GPS jitter to settle below; 1000 m
            // is large enough to cover the broadest "approach a marina
            // entry" scenario without making the chart ring useless.
            // Out-of-range stored values (corrupted / pre-feature)
            // fall back to the default rather than driving a server
            // request the spec might reject.
            ArrivalCircleMeters = Math.Clamp(
                await LoadDouble("arrivalCircleMeters.v1", 50.0), 5.0, 1000.0);
            // Marine POI categories. Each defaults to false (opt-in); a
            // bool stored as "true" / "false" via the same Save / LoadBool
            // helpers as every other map-display toggle.
            MarinePoiFuelEnabled = await LoadBool("marinePoi.fuel.v1", false);
            MarinePoiMarinaEnabled = await LoadBool("marinePoi.marina.v1", false);
            MarinePoiHarbourEnabled = await LoadBool("marinePoi.harbour.v1", false);
            MarinePoiMooringEnabled = await LoadBool("marinePoi.mooring.v1", false);
            MarinePoiSlipwayEnabled = await LoadBool("marinePoi.slipway.v1", false);
            MarinePoiPierEnabled = await LoadBool("marinePoi.pier.v1", false);
            MarinePoiChandleryEnabled = await LoadBool("marinePoi.chandlery.v1", false);
            MarinePoiDrinkingWaterEnabled = await LoadBool("marinePoi.drinkingWater.v1", false);
            MarinePoiPumpOutEnabled = await LoadBool("marinePoi.pumpOut.v1", false);
            LoadIdsInto(await LoadString("enabledChartIds"), _enabledChartIds);
            LoadIdsInto(await LoadString("enabledRouteIds"), _enabledRouteIds);
            LoadIdsInto(await LoadString("chartOrder.v1"), _chartOrder);
            LoadIdsInto(await LoadString("disabledAlarmRules.v1"), _disabledAlarmRules);
            LoadMapView(await LoadString("mapView.v1"));

            // Quick-bar chart membership. If this key doesn't exist yet
            // (first load after upgrade), seed from EnabledChartIds so
            // existing users keep their chart shortcuts. Subsequent
            // runs read the persisted set as-is, even if it's empty.
            var quickBarRaw = await LoadString("quickBarChartIds.v1");
            if (quickBarRaw is null)
            {
                foreach (var id in _enabledChartIds) _quickBarChartIds.Add(id);
                if (_quickBarChartIds.Count > 0)
                    await Save("quickBarChartIds.v1", string.Join('\n', _quickBarChartIds));
            }
            else
            {
                LoadIdsInto(quickBarRaw, _quickBarChartIds);
            }
            // Standalone-mode + custom SK URL. Default off; an empty
            // URL is allowed in storage (the helm may have toggled the
            // mode on without filling the field yet). Both fall back
            // to the auto-detected page-origin behaviour via the
            // SignalKBaseUrl resolver when not configured.
            StandaloneMode = await LoadBool("standaloneMode.v1", false);
            StandaloneServerUrl = NormalizeStandaloneUrl(
                await LoadString("standaloneServerUrl.v1"));
            // Standalone-auth credential persistence flags. Defaults
            // mirror the IServerSettings contract: session-token
            // remembered (true), password not (false). Username is
            // safe to persist unconditionally - it's not a credential
            // on its own. Password loads as the stored value or empty
            // if RememberPassword is off (the load just reads what's
            // there; the setters enforce the flag-gated write).
            RememberSession = await LoadBool("rememberSession.v1", true);
            RememberPassword = await LoadBool("rememberPassword.v1", false);
            StoredUsername = await LoadString("standaloneUsername.v1") ?? "";
            StoredPassword = await LoadString("standalonePassword.v1") ?? "";
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
        // which is where the fan-out historically happened. Fires
        // outside the lock so a subscriber that calls back into the
        // service doesn't deadlock.
        OnSettingsChanged?.Invoke();
    }

    public async Task SetNightModeAsync(bool value)
    {
        NightMode = value;
        await Save("nightMode", value ? "true" : "false");
        OnSettingsChanged?.Invoke();
    }

    public async Task MarkManualNightToggleAsync(string? sunCluster)
    {
        var now = DateTime.UtcNow;
        LastManualNightToggleUtc = now;
        await Save("lastManualNightToggle.v1", now.ToString("o", CultureInfo.InvariantCulture));

        // Cluster suppression: store the env.sun cluster (day / night)
        // at toggle time so CheckAutoNightAsync can suppress until
        // the next sun-state transition. null when env.sun is unknown
        // - in that case auto-night isn't running anyway and the
        // override has nothing to fight.
        LastManualNightOverrideSunCluster = NormalizeSunCluster(sunCluster);
        await Save("lastManualNightOverrideSunCluster.v1",
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
        await Save("lastManualNightOverrideSunCluster.v1", "");
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

    public async Task MarkChartsSeededAsync()
    {
        ChartsSeeded = true;
        await Save("chartsSeeded.v1", "true");
    }

    public async Task SetNightModeAutoAsync(bool value)
    {
        NightModeAuto = value;
        await Save("nightModeAuto.v1", value ? "true" : "false");
        OnSettingsChanged?.Invoke();
    }

    public async Task SetThemeAsync(string value)
    {
        Theme = NormalizeTheme(value);
        await Save("theme", Theme);
        OnSettingsChanged?.Invoke();
    }

    private static string NormalizeTheme(string? raw) => raw switch
    {
        "light" or "dark" or "system" or "high-contrast" => raw,
        _ => "system",
    };

    public async Task SetWindHeroModeAsync(string value)
    {
        WindHeroMode = NormalizeWindHeroMode(value);
        await Save("windHeroMode.v1", WindHeroMode);
        OnSettingsChanged?.Invoke();
    }

    private static string NormalizeWindHeroMode(string? raw) => raw switch
    {
        "apparent" or "true" => raw,
        _ => "apparent",
    };

    public async Task SetMapOrientationAsync(string value)
    {
        MapOrientation = value;
        await Save("mapOrientation", value);
        OnSettingsChanged?.Invoke();
    }

    public async Task SetShipOrientationSourceAsync(string value)
    {
        // Normalise via the resolver so an unknown string from a
        // forged dropdown call doesn't break later round-trips.
        var canonical = OnaPlotter.Utilities.ShipOrientationResolver.ToSetting(
            OnaPlotter.Utilities.ShipOrientationResolver.Parse(value));
        ShipOrientationSource = canonical;
        await Save("shipOrientationSource.v1", canonical);
        OnSettingsChanged?.Invoke();
    }

    public async Task SetFollowBoatAsync(bool value)
    {
        FollowBoat = value;
        await Save("followBoat", value ? "true" : "false");
    }

    public async Task SetLaylinesVisibleAsync(bool value)
    {
        LaylinesVisible = value;
        await Save("laylinesVisible", value ? "true" : "false");
    }

    public async Task SetShipLinesVisibleAsync(bool value)
    {
        ShipLinesVisible = value;
        await Save("shipLinesVisible.v1", value ? "true" : "false");
    }

    public async Task SetServerTrackVisibleAsync(bool value)
    {
        ServerTrackVisible = value;
        await Save("serverTrackVisible.v1", value ? "true" : "false");
    }

    public async Task SetServerTrackDurationAsync(string value)
    {
        ServerTrackDuration = NormalizeServerTrackDuration(value);
        await Save("serverTrackDuration.v1", ServerTrackDuration);
    }

    public async Task SetServerTrackResolutionAsync(string value)
    {
        ServerTrackResolution = NormalizeServerTrackResolution(value);
        await Save("serverTrackResolution.v1", ServerTrackResolution);
    }

    public async Task SetServerTrackWithinBoundsAsync(bool value)
    {
        ServerTrackWithinBounds = value;
        await Save("serverTrackWithinBounds.v1", value ? "true" : "false");
    }

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

    public async Task SetAtonsVisibleAsync(bool value)
    {
        AtonsVisible = value;
        await Save("atonsVisible.v1", value ? "true" : "false");
        OnSettingsChanged?.Invoke();
    }

    public async Task SetAisLabelsVisibleAsync(bool value)
    {
        AisLabelsVisible = value;
        await Save("aisLabelsVisible.v1", value ? "true" : "false");
        OnSettingsChanged?.Invoke();
    }

    public async Task SetChartUpscaleEnabledAsync(bool value)
    {
        ChartUpscaleEnabled = value;
        await Save("chartUpscaleEnabled.v1", value ? "true" : "false");
        OnSettingsChanged?.Invoke();
    }

    public async Task SetChartUpscaleLevelsAsync(int value)
    {
        // Clamp via the shared helper so a future caller / corrupted
        // localStorage value can't drive the JS-side decorator out of
        // its tested 0..3 range.
        ChartUpscaleLevels = OnaPlotter.Utilities.ChartUpscale.ClampLevels(value);
        await Save("chartUpscaleLevels.v1",
            ChartUpscaleLevels.ToString(System.Globalization.CultureInfo.InvariantCulture));
        OnSettingsChanged?.Invoke();
    }

    public async Task SetWeatherOverlayOpacityAsync(double value)
    {
        // Floor / ceiling pulled from the shared WeatherOpacity helper
        // so the slider, the service, and the JS leaflet layer all
        // share one source of truth. Belt-and-suspenders clamp here
        // protects against a bad caller / future API path that
        // bypasses the slider's min/max attributes.
        WeatherOverlayOpacity = OnaPlotter.Utilities.WeatherOpacity.ClampFraction(value);
        await Save("weatherOverlayOpacity.v1",
            WeatherOverlayOpacity.ToString(System.Globalization.CultureInfo.InvariantCulture));
        OnSettingsChanged?.Invoke();
    }

    public async Task SetChartContrastPercentAsync(int value)
    {
        // Same belt-and-suspenders pattern as the other clamp-on-set
        // settings. The slider's min/max attributes are the helm's
        // fence; this clamp catches a JS-bridge override or a future
        // programmatic caller bypassing it.
        ChartContrastPercent = OnaPlotter.Utilities.ChartFilter.ClampContrastSaturation(value);
        await Save("chartContrastPercent.v1",
            ChartContrastPercent.ToString(System.Globalization.CultureInfo.InvariantCulture));
        OnSettingsChanged?.Invoke();
    }

    public async Task SetChartSaturationPercentAsync(int value)
    {
        ChartSaturationPercent = OnaPlotter.Utilities.ChartFilter.ClampContrastSaturation(value);
        await Save("chartSaturationPercent.v1",
            ChartSaturationPercent.ToString(System.Globalization.CultureInfo.InvariantCulture));
        OnSettingsChanged?.Invoke();
    }

    public async Task SetChartBrightnessPercentAsync(int value)
    {
        ChartBrightnessPercent = OnaPlotter.Utilities.ChartFilter.ClampBrightness(value);
        await Save("chartBrightnessPercent.v1",
            ChartBrightnessPercent.ToString(System.Globalization.CultureInfo.InvariantCulture));
        OnSettingsChanged?.Invoke();
    }

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
        await Save("sidebarCollapsed.v1", value ? "true" : "false");
        OnSettingsChanged?.Invoke();
    }

    public async Task ApplyMobileFirstRunDefaultsAsync(bool isMobile)
    {
        // Already-set sidebar wins (user toggled it intentionally on a
        // previous visit, or the mobile-default ran on an earlier mount
        // - either way no overwrite). Desktop viewport doesn't change
        // anything; the historical default is "expanded" which is
        // already correct.
        if (_sidebarCollapsedExplicit) return;
        if (!isMobile) return;
        SidebarCollapsed = true;
        _sidebarCollapsedExplicit = true;
        await Save("sidebarCollapsed.v1", "true");
        OnSettingsChanged?.Invoke();
    }

    public async Task SetDepthAlarmThresholdAsync(double value)
    {
        DepthAlarmThreshold = value;
        await Save("depthAlarmThreshold", value.ToString("F1", CultureInfo.InvariantCulture));
        OnSettingsChanged?.Invoke();
    }

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
            await Save("cpaAwarenessNm.v1",
                CpaAwarenessNm.ToString("F2", CultureInfo.InvariantCulture));
        }
        await Save("cpaAlarmNm.v1", value.ToString("F2", CultureInfo.InvariantCulture));
        OnSettingsChanged?.Invoke();
    }

    public async Task SetTcpaAlarmMinAsync(double value)
    {
        if (!double.IsFinite(value) || value <= 0) value = 1.0;
        TcpaAlarmMin = value;
        if (TcpaAwarenessMin < TcpaAlarmMin)
        {
            TcpaAwarenessMin = TcpaAlarmMin;
            await Save("tcpaAwarenessMin.v1",
                TcpaAwarenessMin.ToString("F1", CultureInfo.InvariantCulture));
        }
        await Save("tcpaAlarmMin.v1", value.ToString("F1", CultureInfo.InvariantCulture));
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
        await Save("cpaAwarenessNm.v1", value.ToString("F2", CultureInfo.InvariantCulture));
        OnSettingsChanged?.Invoke();
    }

    public async Task SetTcpaAwarenessMinAsync(double value)
    {
        if (!double.IsFinite(value) || value <= 0) value = TcpaAlarmMin;
        if (value < TcpaAlarmMin) value = TcpaAlarmMin;
        TcpaAwarenessMin = value;
        await Save("tcpaAwarenessMin.v1", value.ToString("F1", CultureInfo.InvariantCulture));
        OnSettingsChanged?.Invoke();
    }

    public async Task SetCpaDebounceSecondsAsync(double value)
    {
        CpaDebounceSeconds = Math.Clamp(value, 0.0, 60.0);
        await Save("cpaDebounceSeconds.v1",
            CpaDebounceSeconds.ToString("F1", CultureInfo.InvariantCulture));
        OnSettingsChanged?.Invoke();
    }

    public async Task SetWindShiftAlarmThresholdAsync(double value)
    {
        WindShiftAlarmThreshold = value;
        await Save("windShiftAlarmThreshold", value.ToString("F1", CultureInfo.InvariantCulture));
        OnSettingsChanged?.Invoke();
    }

    public async Task SetWindShiftLookbackMinutesAsync(double value)
    {
        WindShiftLookbackMinutes = value;
        await Save("windShiftLookbackMinutes", value.ToString("F1", CultureInfo.InvariantCulture));
        OnSettingsChanged?.Invoke();
    }

    public async Task SetWindShiftMinTrueWindSpeedAsync(double value)
    {
        // Clamp at 0 (negative TWS is meaningless and would re-introduce
        // the noise the gate was added to prevent). No upper clamp - a
        // user who sets 50 kn has effectively disabled the alarm, which
        // is a legitimate choice.
        WindShiftMinTrueWindSpeed = Math.Max(0, value);
        await Save("windShiftMinTrueWindSpeed.v1",
            WindShiftMinTrueWindSpeed.ToString("F1", CultureInfo.InvariantCulture));
        OnSettingsChanged?.Invoke();
    }

    public async Task SetAnchorTideSafetyMarginAsync(double value)
    {
        AnchorTideSafetyMargin = value;
        await Save("anchorTideSafetyMargin", value.ToString("F2", CultureInfo.InvariantCulture));
        OnSettingsChanged?.Invoke();
    }

    public async Task SetAnchorAutoRadiusSafetyMarginAsync(double value)
    {
        // Clamp at 0; negative would let the auto preview round
        // BELOW the swing + tide-drop sum, which defeats the
        // purpose of the pad. Helm input box should already cap
        // at sensible ranges; this is the model-level guard.
        AnchorAutoRadiusSafetyMargin = Math.Max(0, value);
        await Save("anchorAutoRadiusSafetyMargin.v1",
            AnchorAutoRadiusSafetyMargin.ToString("F1", CultureInfo.InvariantCulture));
        OnSettingsChanged?.Invoke();
    }

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
        await Save("disabledAlarmRules.v1",
            string.Join('\n', _disabledAlarmRules));
        OnSettingsChanged?.Invoke();
    }

    public async Task SetSailingModeAsync(string value)
    {
        SailingMode = NormalizeSailingMode(value);
        await Save("sailingMode", SailingMode);
        OnSettingsChanged?.Invoke();
    }

    public async Task SetKeepScreenAwakeAsync(bool value)
    {
        KeepScreenAwake = value;
        await Save("keepScreenAwake.v1", value ? "true" : "false");
        OnSettingsChanged?.Invoke();
    }

    public async Task SetServerSideApproachAlarmsAsync(bool value)
    {
        ServerSideApproachAlarms = value;
        await Save("serverSideApproachAlarms.v1", value ? "true" : "false");
        OnSettingsChanged?.Invoke();
    }

    public async Task SetShowKeyboardHintsAsync(bool value)
    {
        ShowKeyboardHints = value;
        await Save("showKeyboardHints.v1", value ? "true" : "false");
        OnSettingsChanged?.Invoke();
    }

    public async Task SetShowAutopilotHudAsync(bool value)
    {
        ShowAutopilotHud = value;
        await Save("showAutopilotHud.v1", value ? "true" : "false");
        OnSettingsChanged?.Invoke();
    }

    public async Task SetShowRadarHudAsync(bool value)
    {
        ShowRadarHud = value;
        await Save("showRadarHud.v1", value ? "true" : "false");
        OnSettingsChanged?.Invoke();
    }

    public async Task SetShowDefaultHudAsync(bool value)
    {
        ShowDefaultHud = value;
        await Save("showDefaultHud.v1", value ? "true" : "false");
        OnSettingsChanged?.Invoke();
    }

    public async Task SetOwnCogVectorMinutesAsync(double value)
    {
        // Clamp to a sensible range. 1 min is the floor where the
        // dashed predictor reads as a vector at all; 60 min is the
        // ceiling beyond which the line crosses the chart's pan
        // budget and the helm loses spatial context. Storage
        // corruption / a malformed import lands at the default.
        if (!double.IsFinite(value)) value = 10.0;
        value = Math.Clamp(value, 1.0, 60.0);
        OwnCogVectorMinutes = value;
        await Save("ownCogVectorMinutes.v1", value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        OnSettingsChanged?.Invoke();
    }

    public async Task SetAisCogVectorMinutesAsync(double value)
    {
        if (!double.IsFinite(value)) value = 10.0;
        value = Math.Clamp(value, 1.0, 60.0);
        AisCogVectorMinutes = value;
        await Save("aisCogVectorMinutes.v1", value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        OnSettingsChanged?.Invoke();
    }

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
            await Save("aisRemoveMinutes.v1", value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        await Save("aisInactiveMinutes.v1", value.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
        await Save("aisRemoveMinutes.v1", value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        OnSettingsChanged?.Invoke();
    }

    public async Task SetTideVisibleAsync(bool value)
    {
        TideVisible = value;
        await Save("tideVisible.v1", value ? "true" : "false");
        OnSettingsChanged?.Invoke();
    }

    public async Task SetRadarRangeRingsEnabledAsync(bool value)
    {
        RadarRangeRingsEnabled = value;
        await Save("radarRangeRingsEnabled.v1", value ? "true" : "false");
        OnSettingsChanged?.Invoke();
    }

    public async Task SetRadarRangeRingsCountAsync(int value)
    {
        RadarRangeRingsCount = Math.Clamp(value, 1, 8);
        await Save("radarRangeRingsCount.v1",
            RadarRangeRingsCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        OnSettingsChanged?.Invoke();
    }

    public async Task SetDistanceRingsEnabledAsync(bool value)
    {
        DistanceRingsEnabled = value;
        await Save("distanceRingsEnabled.v1", value ? "true" : "false");
        OnSettingsChanged?.Invoke();
    }

    public async Task SetDistanceRingsBaseNmAsync(double value)
    {
        // NaN / negative / zero would collapse every ring to a single
        // point or vanish them entirely; pin into the supported range
        // so a corrupted bind / paste lands on a usable value.
        if (!double.IsFinite(value)) value = 0.5;
        DistanceRingsBaseNm = Math.Clamp(value, 0.05, 50.0);
        await Save("distanceRingsBaseNm.v1",
            DistanceRingsBaseNm.ToString("F2", CultureInfo.InvariantCulture));
        OnSettingsChanged?.Invoke();
    }

    public async Task SetDistanceRingsCountAsync(int value)
    {
        DistanceRingsCount = Math.Clamp(value, 1, 8);
        await Save("distanceRingsCount.v1",
            DistanceRingsCount.ToString(CultureInfo.InvariantCulture));
        OnSettingsChanged?.Invoke();
    }

    public async Task SetRadarUseWireBearingAsync(bool value)
    {
        RadarUseWireBearing = value;
        await Save("radarUseWireBearing.v1", value ? "true" : "false");
        OnSettingsChanged?.Invoke();
    }

    public async Task SetRadarBearingCorrectionDegAsync(double value)
    {
        RadarBearingCorrectionDeg = Math.Clamp(value, -180.0, 180.0);
        await Save("radarBearingCorrection.v1",
            RadarBearingCorrectionDeg.ToString(System.Globalization.CultureInfo.InvariantCulture));
        OnSettingsChanged?.Invoke();
    }

    public async Task SetPreferMagneticHeadingAsync(bool value)
    {
        PreferMagneticHeading = value;
        await Save("preferMagneticHeading.v1", value ? "true" : "false");
        OnSettingsChanged?.Invoke();
    }

    /// <summary>Back-compat setter: flips the True/Magnetic axis of
    /// <see cref="CogReadoutSource"/> while preserving the picked
    /// smoothed/realtime variant. Existing callers (legacy migrations,
    /// page-side toggles that haven't been moved to the four-way
    /// dropdown yet) keep working without writes to the retired
    /// <c>preferMagneticCourse.v1</c> key.</summary>
    public async Task SetPreferMagneticCourseAsync(bool value)
    {
        var current = OnaPlotter.Utilities.CogSourceResolver.Parse(CogReadoutSource);
        var smoothed = OnaPlotter.Utilities.CogSourceResolver.IsSmoothed(current);
        var next = (value, smoothed) switch
        {
            (true,  true)  => OnaPlotter.Utilities.CogSource.MagneticSmoothed,
            (true,  false) => OnaPlotter.Utilities.CogSource.MagneticRealtime,
            (false, true)  => OnaPlotter.Utilities.CogSource.TrueSmoothed,
            (false, false) => OnaPlotter.Utilities.CogSource.TrueRealtime,
        };
        await SetCogReadoutSourceAsync(OnaPlotter.Utilities.CogSourceResolver.ToSetting(next));
    }

    public async Task SetCogReadoutSourceAsync(string value)
    {
        // Round-trip through the parser so an unknown / corrupted
        // value falls back to the safe default rather than poisoning
        // the persisted setting.
        var canonical = OnaPlotter.Utilities.CogSourceResolver.ToSetting(
            OnaPlotter.Utilities.CogSourceResolver.Parse(value));
        CogReadoutSource = canonical;
        await Save("cogReadoutSource.v1", canonical);
        OnSettingsChanged?.Invoke();
    }

    public async Task SetOwnCogVectorSourceAsync(string value)
    {
        var canonical = OnaPlotter.Utilities.CogSourceResolver.ToSetting(
            OnaPlotter.Utilities.CogSourceResolver.Parse(value));
        OwnCogVectorSource = canonical;
        await Save("ownCogVectorSource.v1", canonical);
        OnSettingsChanged?.Invoke();
    }

    public async Task SetAutoAdvanceWaypointsAsync(bool value)
    {
        AutoAdvanceWaypoints = value;
        await Save("autoAdvanceWaypoints.v1", value ? "true" : "false");
        OnSettingsChanged?.Invoke();
    }

    public async Task SetArrivalCircleMetersAsync(double value)
    {
        ArrivalCircleMeters = Math.Clamp(value, 5.0, 1000.0);
        await Save("arrivalCircleMeters.v1",
            ArrivalCircleMeters.ToString(System.Globalization.CultureInfo.InvariantCulture));
        OnSettingsChanged?.Invoke();
    }

    // Marine POI category toggles. Same shape across all categories:
    // write the bool, persist under the canonical "marinePoi.<cat>.v1"
    // key, fan out OnSettingsChanged so MarinePoiController can
    // re-render. The overlay visibility is implicit: enabled iff at
    // least one category is on.
    public async Task SetMarinePoiFuelEnabledAsync(bool value)
    {
        MarinePoiFuelEnabled = value;
        await Save("marinePoi.fuel.v1", value ? "true" : "false");
        OnSettingsChanged?.Invoke();
    }
    public async Task SetMarinePoiMarinaEnabledAsync(bool value)
    {
        MarinePoiMarinaEnabled = value;
        await Save("marinePoi.marina.v1", value ? "true" : "false");
        OnSettingsChanged?.Invoke();
    }
    public async Task SetMarinePoiHarbourEnabledAsync(bool value)
    {
        MarinePoiHarbourEnabled = value;
        await Save("marinePoi.harbour.v1", value ? "true" : "false");
        OnSettingsChanged?.Invoke();
    }
    public async Task SetMarinePoiMooringEnabledAsync(bool value)
    {
        MarinePoiMooringEnabled = value;
        await Save("marinePoi.mooring.v1", value ? "true" : "false");
        OnSettingsChanged?.Invoke();
    }
    public async Task SetMarinePoiSlipwayEnabledAsync(bool value)
    {
        MarinePoiSlipwayEnabled = value;
        await Save("marinePoi.slipway.v1", value ? "true" : "false");
        OnSettingsChanged?.Invoke();
    }
    public async Task SetMarinePoiPierEnabledAsync(bool value)
    {
        MarinePoiPierEnabled = value;
        await Save("marinePoi.pier.v1", value ? "true" : "false");
        OnSettingsChanged?.Invoke();
    }
    public async Task SetMarinePoiChandleryEnabledAsync(bool value)
    {
        MarinePoiChandleryEnabled = value;
        await Save("marinePoi.chandlery.v1", value ? "true" : "false");
        OnSettingsChanged?.Invoke();
    }
    public async Task SetMarinePoiDrinkingWaterEnabledAsync(bool value)
    {
        MarinePoiDrinkingWaterEnabled = value;
        await Save("marinePoi.drinkingWater.v1", value ? "true" : "false");
        OnSettingsChanged?.Invoke();
    }
    public async Task SetMarinePoiPumpOutEnabledAsync(bool value)
    {
        MarinePoiPumpOutEnabled = value;
        await Save("marinePoi.pumpOut.v1", value ? "true" : "false");
        OnSettingsChanged?.Invoke();
    }

    public async Task SetManualAnchorRadiusMetersAsync(double value)
    {
        ManualAnchorRadiusMeters = value;
        await Save("manualAnchorRadiusMeters.v1", value.ToString("F1", CultureInfo.InvariantCulture));
        OnSettingsChanged?.Invoke();
    }

    public async Task SetDeadmanTimeoutMinutesAsync(double value)
    {
        DeadmanTimeoutMinutes = value;
        await Save("deadmanTimeoutMinutes.v1", value.ToString("F1", CultureInfo.InvariantCulture));
        OnSettingsChanged?.Invoke();
    }

    public async Task SetDeadmanNightMinutesAsync(double value)
    {
        DeadmanNightMinutes = value;
        await Save("deadmanNightMinutes.v1", value.ToString("F1", CultureInfo.InvariantCulture));
        OnSettingsChanged?.Invoke();
    }

    public async Task SetSnoozeDurationMinutesAsync(int value)
    {
        // Clamp 1 min minimum (zero would snooze forever - that's dismiss).
        // Upper bound 120 to keep a runaway value from locking an alarm
        // quiet for days after a power cycle.
        SnoozeDurationMinutes = System.Math.Clamp(value, 1, 120);
        await Save("snoozeDurationMinutes.v1", SnoozeDurationMinutes.ToString(CultureInfo.InvariantCulture));
        OnSettingsChanged?.Invoke();
    }

    public async Task SetBigTypeAsync(bool value)
    {
        BigType = value;
        await Save("bigType.v1", value ? "true" : "false");
        OnSettingsChanged?.Invoke();
    }

    public async Task SetExpandAllHudAsync(bool value)
    {
        ExpandAllHud = value;
        await Save("expandAllHud.v1", value ? "true" : "false");
        OnSettingsChanged?.Invoke();
    }

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

    public async Task SetOwnVesselTypeAsync(string value)
    {
        OwnVesselType = NormalizeOwnVesselType(value);
        await Save("ownVesselType.v1", OwnVesselType);
        OnSettingsChanged?.Invoke();
    }

    public async Task SetEnabledChartsAsync(IEnumerable<string> ids)
    {
        // Materialise first - see SetChartOrderAsync for the aliasing
        // footgun this defends against.
        var copy = ids.ToList();
        _enabledChartIds.Clear();
        foreach (var id in copy) _enabledChartIds.Add(id);
        await Save("enabledChartIds", string.Join('\n', _enabledChartIds));
    }

    public async Task SetEnabledRoutesAsync(IEnumerable<string> ids)
    {
        var copy = ids.ToList();
        _enabledRouteIds.Clear();
        foreach (var id in copy) _enabledRouteIds.Add(id);
        await Save("enabledRouteIds", string.Join('\n', _enabledRouteIds));
    }

    public async Task SetQuickBarChartsAsync(IEnumerable<string> ids)
    {
        // Same materialise-first guard as SetChartOrderAsync - a caller
        // can hand us a LINQ view over _quickBarChartIds and the Clear()
        // would pull the rug out mid-iteration.
        var copy = ids.ToList();
        _quickBarChartIds.Clear();
        foreach (var id in copy) _quickBarChartIds.Add(id);
        await Save("quickBarChartIds.v1", string.Join('\n', _quickBarChartIds));
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
        await Save("chartOrder.v1", string.Join('\n', _chartOrder));
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

    private async Task<bool> LoadBool(string key, bool fallback)
    {
        var v = await LoadString(key);
        return v is not null ? v == "true" : fallback;
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
        await Save("mapView.v1", serialized);
        // No OnSettingsChanged fire here: the map view is purely
        // client-local and firing on every pan would cascade through
        // every settings subscriber for no gain.
    }

    public async Task SetStandaloneModeAsync(bool value)
    {
        StandaloneMode = value;
        await Save("standaloneMode.v1", value ? "true" : "false");
        // Fan-out: SignalKBaseUrl re-resolves its origin off the new
        // flag; SignalkClient drops + reopens its WebSocket.
        OnSettingsChanged?.Invoke();
    }

    public async Task SetStandaloneServerUrlAsync(string value)
    {
        // Normalise unconditionally so a trailing-slash / mixed-case
        // host / accidental copy-pasted path round-trips cleanly. An
        // empty / unparseable value is allowed and persists as "" -
        // SignalKBaseUrl falls back to the auto-detected origin in
        // that case rather than throwing.
        StandaloneServerUrl = NormalizeStandaloneUrl(value);
        await Save("standaloneServerUrl.v1", StandaloneServerUrl);
        OnSettingsChanged?.Invoke();
    }

    public async Task SetRememberSessionAsync(bool value)
    {
        RememberSession = value;
        await Save("rememberSession.v1", value ? "true" : "false");
        OnSettingsChanged?.Invoke();
    }

    public async Task SetRememberPasswordAsync(bool value)
    {
        RememberPassword = value;
        await Save("rememberPassword.v1", value ? "true" : "false");
        // When the helm flips the flag OFF, also wipe any previously
        // persisted password. Without this, an opt-out leaves the
        // value sitting in localStorage where the next opt-in (or a
        // bug, or an XSS) could pick it up. The in-memory copy is
        // wiped too so the UI text field clears on the same render.
        if (!value && !string.IsNullOrEmpty(StoredPassword))
        {
            StoredPassword = "";
            await Save("standalonePassword.v1", "");
        }
        OnSettingsChanged?.Invoke();
    }

    public async Task SetStoredUsernameAsync(string value)
    {
        StoredUsername = value ?? "";
        await Save("standaloneUsername.v1", StoredUsername);
        // Username is informational, not a credential - no event fan-
        // out (avoid the WS reconnect cascade that OnSettingsChanged
        // triggers for URL-touching changes; SignalKBaseUrl's diff
        // filter would skip this anyway, but no point firing).
    }

    public async Task SetStoredPasswordAsync(string value)
    {
        StoredPassword = value ?? "";
        await Save("standalonePassword.v1", StoredPassword);
        // Same rationale as SetStoredUsernameAsync: storing the
        // password isn't a server-state change. The auto-login flow
        // re-issues a JWT on its own schedule.
    }

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
