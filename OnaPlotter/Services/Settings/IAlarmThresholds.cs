namespace OnaPlotter.Services.Settings;

/// <summary>
/// Narrow surface for alarm-rule consumers. CPA / depth / wind-shift
/// / anchor / waypoint / deadman thresholds + the snooze duration.
/// Carved out of <see cref="IAppSettings"/> so an alarm rule under
/// test only needs a 12-property double instead of the 43-property
/// app-wide interface.
///
/// <para>Architecture review (2026-04-28) ARCH-003 carved this out
/// of the settings God Interface. Production <c>AppSettingsService</c>
/// implements both this and the legacy <see cref="IAppSettings"/>;
/// new consumers should depend on the narrowest surface that fits
/// their need.</para>
/// </summary>
public interface IAlarmThresholds
{
    /// <summary>Shallow-water alarm trigger (metres). Alarm fires
    /// when measured depth drops below this value.</summary>
    double DepthAlarmThreshold { get; }

    /// <summary>CPA distance (nautical miles) that triggers the audible
    /// alarm + red-blink rendering. The inner / strict tier of the
    /// two-tier collision model: a vessel whose projected CPA falls
    /// below this AND whose TCPA is inside <see cref="TcpaAlarmMin"/>
    /// crosses into <c>Threat.Alarm</c>.</summary>
    double CpaAlarmNm { get; }

    /// <summary>TCPA lookahead (minutes) for the alarm tier. Only
    /// vessels whose projected TCPA falls within this window can
    /// trigger the audible alarm.</summary>
    double TcpaAlarmMin { get; }

    /// <summary>CPA distance (nautical miles) that triggers the silent
    /// awareness tier: chart cross + hover label, no audio. Wider than
    /// <see cref="CpaAlarmNm"/> so the helm sees a developing crossing
    /// situation well before it becomes a hard alarm. Setter clamps to
    /// be &gt;= <see cref="CpaAlarmNm"/> so the awareness band never
    /// degenerates below the alarm band (which would let an alarm fire
    /// for a target that never paints as awareness first).</summary>
    double CpaAwarenessNm { get; }

    /// <summary>TCPA lookahead (minutes) for the awareness tier.
    /// Setter clamps to be &gt;= <see cref="TcpaAlarmMin"/> for the
    /// same reason as <see cref="CpaAwarenessNm"/>.</summary>
    double TcpaAwarenessMin { get; }

    /// <summary>How long a CPA threat must persist before it raises
    /// the audible alarm (seconds). A target only triggers the banner
    /// after its projected approach has stayed inside the guard zone
    /// for this many seconds continuously. Suppresses the brief
    /// "passed through the warning band on a single sample" flicker
    /// that used to fire the klaxon, then auto-clear, then fire
    /// again on the next geometry tick - a behaviour the helm reads
    /// as the alarm being broken. Set to 0 to disable debouncing
    /// (every detected threat raises immediately).</summary>
    double CpaDebounceSeconds { get; }

    /// <summary>Wind-shift alarm threshold (degrees). Triggers when
    /// the running mean wind direction has shifted by more than this
    /// amount over <see cref="WindShiftLookbackMinutes"/>.</summary>
    double WindShiftAlarmThreshold { get; }

    /// <summary>Lookback window for the wind-shift detector
    /// (minutes). Racing crews want 1-2 min; cruisers 10-15.</summary>
    double WindShiftLookbackMinutes { get; }

    /// <summary>Minimum true wind speed (knots) required for the
    /// WIND SHIFT alarm to arm. Below this TWS the rule skips
    /// evaluation and drops its anchor. Reason: TWD is derived from
    /// AWS + heading + SOG; in light air, heading and SOG noise
    /// dominate and small errors blow up to 30-60 deg "shifts" with
    /// no real wind change. Default 3 kn. 0 disables the gate. The
    /// gate is also bypassed when the server doesn't publish
    /// <c>environment.wind.speedTrue</c>, so a missing path can't
    /// suppress every shift.</summary>
    double WindShiftMinTrueWindSpeed { get; }

    /// <summary>Safety margin added to draft for the tide-aware
    /// anchor alarm (metres): alarm fires when predicted LW depth
    /// is less than <c>draft + margin</c>.</summary>
    double AnchorTideSafetyMargin { get; }

    /// <summary>Pad added on top of swing + tide-drop in the
    /// "Auto" anchor-radius preview shown on the Drop / Set panel
    /// (metres). Default 5; raise for soft-mud anchorages where
    /// the boat's actual swing exceeds the chain-catenary
    /// projection, or for a wider buffer against single-step swing
    /// noise. Display string in the panel + Settings reads
    /// "swing N + tide drop M + X m margin" with this value as X.</summary>
    double AnchorAutoRadiusSafetyMargin { get; }

    /// <summary>Radius (metres) used when the user drops a manual
    /// anchor from the Map page.</summary>
    double ManualAnchorRadiusMeters { get; }

    /// <summary>Helm-entered deployed anchor rode / chain length
    /// (metres). Feeds the Auto radius: the chain's horizontal
    /// projection at the current depth floors the swing component, so
    /// the alarm accounts for how far the boat CAN swing on the rode
    /// that's out, not just how far it has swung so far. 0 (default)
    /// means "not entered" - the chain term drops out and Auto falls
    /// back to observed swing + tide + margin.</summary>
    double AnchorChainLengthMeters { get; }

    /// <summary>When true (default), OnaPlotter mutes its client-side
    /// APPROACH alarm and instead surfaces the SK course-provider
    /// plugin's <c>notifications.navigation.arrivalCircleEntered</c>
    /// + <c>perpendicularPassed</c> + <c>routeComplete</c>
    /// notifications via <c>ServerNotificationsAlarmRule</c>. One
    /// source of truth: whatever radius the autopilot is steering
    /// against, the helm sees the alarm against. When false the
    /// client <c>WaypointApproachAlarmRule</c> takes over, also
    /// using the server's <c>navigation.course.arrivalCircle</c> as
    /// the threshold so client and server agree on the boundary.
    /// </summary>
    bool ServerSideApproachAlarms { get; }

    /// <summary>Deadman / watch-timer interval in minutes.
    /// 0 disables the feature.</summary>
    double DeadmanTimeoutMinutes { get; }

    /// <summary>Deadman interval used when night mode is active.
    /// 0 disables the night override (day value applies always).</summary>
    double DeadmanNightMinutes { get; }

    /// <summary>How long a snooze silences a specific alarm target,
    /// in minutes. Manager clamps to 1+.</summary>
    int SnoozeDurationMinutes { get; }

    /// <summary>Set of alarm-rule titles the helm has switched off.
    /// AlarmManager skips evaluation entirely for any title in this
    /// set and drops any of its already-active alarms at the next
    /// tick. Membership is by rule Title (uppercase, e.g. "SHALLOW",
    /// "CPA"); titles are unique per IAlarmRule contract. Empty
    /// set = every rule armed (the default). Persisted as a
    /// newline-separated list under "disabledAlarmRules.v1".</summary>
    IReadOnlySet<string> DisabledAlarmRules { get; }

    Task SetDepthAlarmThresholdAsync(double value);
    Task SetCpaAlarmNmAsync(double value);
    Task SetTcpaAlarmMinAsync(double value);
    Task SetCpaAwarenessNmAsync(double value);
    Task SetTcpaAwarenessMinAsync(double value);
    Task SetCpaDebounceSecondsAsync(double value);
    Task SetWindShiftAlarmThresholdAsync(double value);
    Task SetWindShiftLookbackMinutesAsync(double value);
    Task SetWindShiftMinTrueWindSpeedAsync(double value);
    Task SetAnchorTideSafetyMarginAsync(double value);
    Task SetAnchorAutoRadiusSafetyMarginAsync(double value);
    Task SetManualAnchorRadiusMetersAsync(double value);
    Task SetAnchorChainLengthMetersAsync(double value);
    Task SetServerSideApproachAlarmsAsync(bool value);
    Task SetDeadmanTimeoutMinutesAsync(double value);
    Task SetDeadmanNightMinutesAsync(double value);
    Task SetSnoozeDurationMinutesAsync(int value);

    /// <summary>Toggle a single rule. Trims and uppercases the title
    /// before lookup so a UI binding's whitespace can't desync the
    /// store from the rule's own Title constant. No-op when the
    /// title is empty.</summary>
    Task SetAlarmRuleDisabledAsync(string title, bool disabled);
}
