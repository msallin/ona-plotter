namespace OnaPlotter.Services;

/// <summary>
/// Application-wide user preferences. Values are kept in memory and
/// persisted to <see cref="IKeyValueStore"/> on change.
/// </summary>
public interface IAppSettings
{
    bool NightMode { get; }

    /// <summary>When true, Night mode auto-engages based on SignalK's
    /// <c>environment.sun</c> string path (day / dawn / dusk / night).
    /// A manual toggle suppresses the auto-flip for 12 h (see
    /// <see cref="LastManualNightToggleUtc"/>) so a helmsman who
    /// wants day mode at dusk isn't fought. Default OFF -- plenty of
    /// sailors prefer to decide themselves.</summary>
    bool NightModeAuto { get; }

    /// <summary>True once the first-run chart seeding has run. Prevents
    /// the "auto-enable OpenSeaMap" fallback from re-firing every time
    /// a user has disabled every chart deliberately -- without this
    /// flag the next Map entry would silently re-enable OpenSeaMap.</summary>
    bool ChartsSeeded { get; }

    /// <summary>UTC timestamp of the most recent manual Night toggle
    /// (tap on the Night button). Persisted so the 12-hour auto-
    /// suppress window survives page reloads and Map re-entry.</summary>
    DateTime? LastManualNightToggleUtc { get; }

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

    /// <summary>"apparent" (default) shows AWA + AWS in the Wind page hero
    /// for racers reading sail trim; "true" shows TWD + TWS for cruisers
    /// (anchoring decisions) and tacticians (tactical reading). Persisted
    /// across reloads so the helm doesn't re-pick on every session.</summary>
    string WindHeroMode { get; }

    /// <summary>When true, the Wind page renders in compact density:
    /// card chrome (background / border / padding) drops, gaps tighten,
    /// max screen real estate goes to data. Targeted at tacticians on
    /// big helm screens (Tom in the field study) who want information
    /// density over breathing room. Default OFF -- relaxed mode is the
    /// better default for cruisers and casual racers.</summary>
    bool WindPageCompact { get; }

    string MapOrientation { get; }
    bool FollowBoat { get; }
    bool LaylinesVisible { get; }

    /// <summary>Show AIS Aids to Navigation on the map. Enabled by
    /// default because AtoN data is broadcast under <c>atons.*</c>
    /// only when an AIS receiver is in range; in inland or remote
    /// waters the layer is just empty rather than wrong. Helms who
    /// already see the same buoys on a vector chart may want to
    /// declutter -- this is the toggle.</summary>
    bool AtonsVisible { get; }

    /// <summary>
    /// Harbor mode: a single switch that bundles four AIS / collision
    /// suppressions for entering a busy harbour, where the helm cares
    /// about the chart and immediate obstacles, not every CPA arc on
    /// every moored vessel. When true:
    ///   * Moored AIS targets (per <c>MooredVesselTracker</c>) are
    ///     hidden entirely from the map.
    ///   * Vessel-name labels are suppressed on remaining markers.
    ///   * CPA arcs / lines / labels are not drawn.
    ///   * The CPA alarm rule short-circuits to null so the audio
    ///     alarm doesn't keep firing on dismissed overlays.
    ///   * The guard-zone ring around own-ship is hidden.
    ///
    /// NOT persisted across page reloads -- a forgotten Harbor mode
    /// would otherwise silently ride into open water with the
    /// collision alarms still off, which is the worst possible time.
    /// Defaults to false on every <c>InitializeAsync</c>.
    /// </summary>
    bool HarborMode { get; }

    double DepthAlarmThreshold { get; }

    /// <summary>When true, the sidebar collapses to the icon-only rail
    /// (4 rem wide, no labels). Persisted across reloads so a helm
    /// who prefers the compact layout doesn't re-toggle on every
    /// session. Independent of fullscreen / iOS-fullbleed mode -- the
    /// user can have a wide sidebar in fullscreen if they wanted, or
    /// a narrow one in PWA standalone mode.</summary>
    bool SidebarCollapsed { get; }

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

    // Boat draft is intentionally NOT a setting here. Read instead from
    // NavigationData.DraftFromSignalK (design.draft.current / .maximum);
    // the tide-aware anchor alarm stays dormant when it's not published.
    // A vessel.json edit on the SK server is the authoritative place
    // to set draft -- "don't re-enter what SignalK already knows".

    /// <summary>Safety margin added to draft for the tide-aware anchor
    /// alarm: alarm fires when predicted LW depth is less than
    /// <c>draft + margin</c>. Typical is 0.5-1 m; add more for soft
    /// mud where the boat can settle.</summary>
    double AnchorTideSafetyMargin { get; }

    /// <summary>Radius (metres) used when the user drops a manual anchor
    /// from the Map page. The plugin-driven anchor alarm keeps its own
    /// radius and ignores this value. Persists the last choice so a
    /// captain who always sets 50 m doesn't have to re-pick every night.
    /// Default 30 m.</summary>
    double ManualAnchorRadiusMeters { get; }

    /// <summary>Deadman / watch-timer interval in minutes. If the user
    /// has not interacted with the Map page for this long, the DEADMAN
    /// alarm fires. 0 disables the feature entirely. Default 0 (off) --
    /// the rule is useful on solo watches but distracting otherwise.</summary>
    double DeadmanTimeoutMinutes { get; }

    /// <summary>Deadman interval used when <see cref="NightMode"/> is
    /// active. Defaults to 15 min -- a reasonable sleep-rotation guard
    /// for solo watchkeepers. Overrides <see cref="DeadmanTimeoutMinutes"/>
    /// when night is on; reverts on day. 0 disables the override so the
    /// day value applies at night too.</summary>
    double DeadmanNightMinutes { get; }

    /// <summary>How long a snooze silences a specific alarm target, in
    /// minutes. Default 10. Short (5 min) for busy harbours where
    /// threats resolve fast; long (30-60 min) for a distant freighter
    /// that will stay in range for hours. Clamped to 1+ by the manager.</summary>
    int SnoozeDurationMinutes { get; }

    /// <summary>Big-type mode: scales the four corner HUD values up so
    /// they're readable from across a cockpit (older eyes, 21" helm
    /// screen, sunlight). Pure CSS via a .big-type class on .page.</summary>
    bool BigType { get; }

    /// <summary>When true, every corner HUD panel renders in its
    /// expanded state regardless of click, so a 21"-class widescreen
    /// uses its horizontal space for numbers instead of hidden secondary
    /// rows. Independent of <see cref="BigType"/>: an operator on a
    /// wide screen may want the extras without cranking the font size,
    /// or vice-versa. Default OFF so touch users on a small tablet
    /// still get the click-to-expand affordance.</summary>
    bool ExpandAllHud { get; }

    /// <summary>High-level sailing mode: "cruise" (default -- navigation
    /// emphasis, depth / anchor / route) or "race" (performance emphasis,
    /// laylines / optimal TWA / target speed). Discriminator used by the
    /// Map page to show / hide race-specific overlays without forcing a
    /// mode toggle on every switch.</summary>
    string SailingMode { get; }

    /// <summary>When true, hold a Screen Wake Lock while the app is in the
    /// foreground so the tablet doesn't go to sleep mid-watch. Default ON;
    /// sailors can turn it off if running on battery without shore power.
    /// </summary>
    bool KeepScreenAwake { get; }

    /// <summary>Waypoint arrival radius in metres. The WaypointApproach
    /// alarm fires when the distance-to-go to the active course's next
    /// waypoint drops below this threshold. Defaults to 50 m -- tight
    /// enough to mean "you've arrived", wide enough to account for GPS
    /// jitter and typical inshore turn radii.</summary>
    double WaypointArrivalRadiusMeters { get; }

    /// <summary>When true, the client automatically advances to the
    /// next waypoint in the active route when the course-provider
    /// plugin reports <c>perpendicularPassed</c> or
    /// <c>arrivalCircleEntered</c> as true. Matches the default
    /// behaviour of commercial chartplotters: arrive at a WP, the
    /// screen flips to the next leg without a tap. Off means the
    /// helmsman has to hit "Next WP" explicitly (from the alarm
    /// banner or the Next button). Default ON.</summary>
    bool AutoAdvanceWaypoints { get; }

    /// <summary>When true, keyboard-shortcut hints are visible: inline
    /// "(L)" / "(D)" labels on map control buttons, and the "?" key
    /// opens the shortcuts dialog. Default OFF -- most users run on a
    /// touch device where shortcut hints are clutter. Enable for
    /// desktop-heavy workflows.</summary>
    bool ShowKeyboardHints { get; }

    /// <summary>When true, the dedicated Autopilot control card is
    /// rendered on the map. Default OFF -- the HDG extended HUD
    /// already surfaces AP state / target / rudder when the server
    /// publishes them, and the engage-mode buttons are easy to tap
    /// by accident. Users with a real AP integration (signalk-autopilot
    /// or equivalent) can enable this to get the -10 / -1 / +1 / +10
    /// heading nudges and the Stby / Auto / Wind / Route mode row.</summary>
    bool ShowAutopilotHud { get; }

    /// <summary>When true, the dedicated Radar control card is rendered
    /// on the map. Default OFF -- the layers panel already exposes
    /// transmit / standby and a range dropdown, and helms on a vessel
    /// without a radar plugin shouldn't see a permanently-empty card.
    /// Operators with an active radar (Mayara / Garmin / Furuno via SK)
    /// can enable this to get a glanceable status + range stepper +
    /// transmit toggle without opening the layers panel mid-watch.</summary>
    bool ShowRadarHud { get; }

    /// <summary>When true, Heading on the HUD / SailSteer / alarms
    /// resolves to <c>navigation.headingMagnetic</c> when published;
    /// otherwise <c>navigation.headingTrue</c> wins. Fluxgate compasses
    /// and older NMEA0183 HDG sentences tend to publish magnetic;
    /// GPS-derived HDG is typically true. Either is usable, but mixing
    /// them on a HUD confuses pilotage, so the operator picks once.
    /// Default false (prefer true).</summary>
    bool PreferMagneticHeading { get; }

    /// <summary>When true, Course Over Ground resolves to
    /// <c>navigation.courseOverGroundMagnetic</c> when published;
    /// otherwise <c>navigation.courseOverGroundTrue</c>. Independent of
    /// <see cref="PreferMagneticHeading"/>: some installs publish true
    /// COG (GPS) but magnetic HDG (compass) and the HUD can show both
    /// on the same reference by setting this pair accordingly.
    /// Default false.</summary>
    bool PreferMagneticCourse { get; }

    /// <summary>Last known map view centre latitude. Null on first
    /// run or when the persisted entry failed to parse (a warning is
    /// logged in that case per pragmatic-deserialise policy, and the
    /// map falls back to the live position or a world view).</summary>
    double? MapViewLat { get; }

    /// <summary>Last known map view centre longitude. See
    /// <see cref="MapViewLat"/> for null semantics.</summary>
    double? MapViewLon { get; }

    /// <summary>Last known map zoom level (Leaflet integer). See
    /// <see cref="MapViewLat"/> for null semantics.</summary>
    int? MapViewZoom { get; }

    IReadOnlySet<string> EnabledChartIds { get; }
    IReadOnlySet<string> EnabledRouteIds { get; }

    /// <summary>
    /// Charts the user has added to the on-map quick-switch bar. A
    /// curated subset of available charts; the Layers panel ticks
    /// membership here, and the quick bar then shows one chip per
    /// member and drives render state via <see cref="EnabledChartIds"/>.
    /// Empty on first load after upgrade -- seeded from
    /// <see cref="EnabledChartIds"/> so nothing disappears.
    /// </summary>
    IReadOnlySet<string> QuickBarChartIds { get; }

    /// <summary>User-preferred chart draw order (list of identifiers, first
    /// = bottom of stack, last = top). Charts that aren't in this list
    /// render in server-provided order after any ordered charts. Empty on
    /// first run; populated as the user toggles / reorders.</summary>
    IReadOnlyList<string> ChartOrder { get; }

    event Action? OnSettingsChanged;

    Task InitializeAsync();
    Task SetNightModeAsync(bool value);
    Task SetSidebarCollapsedAsync(bool value);

    /// <summary>Applies a first-run sidebar default that depends on the
    /// caller-supplied viewport hint. On a phone-width screen the rail
    /// would otherwise eat half the visible chart, so we collapse to
    /// the icon rail by default. No-op once the user has explicitly
    /// toggled the chevron (we track an "explicit" flag separately so
    /// the auto-default doesn't override an intentional choice). The
    /// caller (MainLayout) decides what counts as "mobile" and passes
    /// the bool here -- keeps the settings service free of viewport /
    /// JS interop concerns.</summary>
    Task ApplyMobileFirstRunDefaultsAsync(bool isMobile);

    /// <summary>Stamps <see cref="ChartsSeeded"/> so the first-run
    /// OpenSeaMap seed only runs once per device.</summary>
    Task MarkChartsSeededAsync();

    /// <summary>Stamps <see cref="LastManualNightToggleUtc"/> with the
    /// current time. Called alongside <see cref="SetNightModeAsync"/>
    /// from a manual Night tap so the auto-toggle window starts
    /// ticking from the user's action.</summary>
    Task MarkManualNightToggleAsync();
    Task SetNightModeAutoAsync(bool value);
    Task SetNightModePresetAsync(string value);
    Task SetThemeAsync(string value);
    Task SetWindHeroModeAsync(string value);
    Task SetWindPageCompactAsync(bool value);
    Task SetMapOrientationAsync(string value);
    Task SetFollowBoatAsync(bool value);
    Task SetLaylinesVisibleAsync(bool value);
    Task SetAtonsVisibleAsync(bool value);

    /// <summary>Toggle <see cref="HarborMode"/>. In-memory only --
    /// resets to false on the next page load.</summary>
    Task SetHarborModeAsync(bool value);
    Task SetDepthAlarmThresholdAsync(double value);
    Task SetCpaAlarmThresholdAsync(double value);
    Task SetGuardZoneLookaheadMinutesAsync(double value);
    Task SetGuardZoneWarningFactorAsync(double value);
    Task SetWindShiftAlarmThresholdAsync(double value);
    Task SetWindShiftLookbackMinutesAsync(double value);
    Task SetAnchorTideSafetyMarginAsync(double value);
    Task SetManualAnchorRadiusMetersAsync(double value);
    Task SetDeadmanTimeoutMinutesAsync(double value);
    Task SetDeadmanNightMinutesAsync(double value);
    Task SetSnoozeDurationMinutesAsync(int value);
    Task SetBigTypeAsync(bool value);
    Task SetExpandAllHudAsync(bool value);
    Task SetSailingModeAsync(string value);
    Task SetKeepScreenAwakeAsync(bool value);
    Task SetWaypointArrivalRadiusMetersAsync(double value);
    Task SetShowKeyboardHintsAsync(bool value);
    Task SetShowAutopilotHudAsync(bool value);
    Task SetShowRadarHudAsync(bool value);
    Task SetPreferMagneticHeadingAsync(bool value);
    Task SetPreferMagneticCourseAsync(bool value);
    Task SetAutoAdvanceWaypointsAsync(bool value);
    /// <summary>Persist the map centre + zoom so the next session
    /// opens where the user left off. Pragmatic: if the stored tuple
    /// can't round-trip (future format change, truncated localStorage),
    /// the loader logs a warning and falls back to defaults -- never
    /// throws.</summary>
    Task SetMapViewAsync(double lat, double lon, int zoom);

    Task SetEnabledChartsAsync(IEnumerable<string> ids);
    Task SetEnabledRoutesAsync(IEnumerable<string> ids);
    Task SetQuickBarChartsAsync(IEnumerable<string> ids);
    Task SetChartOrderAsync(IEnumerable<string> ids);
}
