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
    /// A manual toggle in the last hour suppresses the auto-flip so a
    /// helmsman who wants day mode at dusk isn't fought. Default OFF --
    /// plenty of sailors prefer to decide themselves.</summary>
    bool NightModeAuto { get; }

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

    IReadOnlySet<string> EnabledChartIds { get; }
    IReadOnlySet<string> EnabledRouteIds { get; }

    /// <summary>
    /// Charts the user has added to the on-map quick-switch bar. A
    /// curated subset of available charts; the Layers panel ticks
    /// membership here, and the quick bar then shows one chip per
    /// member and drives render state via <see cref="EnabledChartIds"/>.
    /// Previously the quick bar showed the first 6 alphabetical
    /// charts regardless of user preference; this makes it the
    /// cockpit-side shortcut list the user actually wants.
    /// Migration: first load after upgrade seeds this from
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
    Task SetNightModeAutoAsync(bool value);
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
    Task SetManualAnchorRadiusMetersAsync(double value);
    Task SetDeadmanTimeoutMinutesAsync(double value);
    Task SetDeadmanNightMinutesAsync(double value);
    Task SetSnoozeDurationMinutesAsync(int value);
    Task SetBigTypeAsync(bool value);
    Task SetSailingModeAsync(string value);
    Task SetKeepScreenAwakeAsync(bool value);
    Task SetWaypointArrivalRadiusMetersAsync(double value);
    Task SetShowKeyboardHintsAsync(bool value);
    Task SetShowAutopilotHudAsync(bool value);
    Task SetPreferMagneticHeadingAsync(bool value);
    Task SetPreferMagneticCourseAsync(bool value);
    Task SetAutoAdvanceWaypointsAsync(bool value);
    Task SetEnabledChartsAsync(IEnumerable<string> ids);
    Task SetEnabledRoutesAsync(IEnumerable<string> ids);
    Task SetQuickBarChartsAsync(IEnumerable<string> ids);
    Task SetChartOrderAsync(IEnumerable<string> ids);
}
