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

    /// <summary>Guard-zone CPA threshold (nautical miles). A
    /// projected CPA smaller than this triggers a collision alarm.</summary>
    double CpaAlarmThreshold { get; }

    /// <summary>Guard-zone lookahead (minutes). Only vessels whose
    /// TCPA falls within this window trigger the CPA alarm.</summary>
    double GuardZoneLookaheadMinutes { get; }

    /// <summary>Multiplier for the advisory (amber) warning band
    /// around the guard zone. A factor of 2.0 draws the warning ring
    /// at 2x the alarm radius and within 2x the lookahead; targets
    /// inside that band get amber crossing lines but no audible
    /// alarm.</summary>
    double GuardZoneWarningFactor { get; }

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

    /// <summary>Radius (metres) used when the user drops a manual
    /// anchor from the Map page.</summary>
    double ManualAnchorRadiusMeters { get; }

    /// <summary>Waypoint arrival radius (metres). The
    /// WaypointApproach alarm fires when distance-to-go drops below
    /// this threshold. Only consulted when
    /// <see cref="ServerSideApproachAlarms"/> is false (legacy /
    /// helm-fallback mode); the server-side path uses the SK
    /// course-provider plugin's own arrival circle.</summary>
    double WaypointArrivalRadiusMeters { get; }

    /// <summary>When true (default), OnaPlotter mutes its client-side
    /// APPROACH alarm and instead surfaces the SK course-provider
    /// plugin's <c>notifications.navigation.arrivalCircleEntered</c>
    /// + <c>perpendicularPassed</c> + <c>routeComplete</c>
    /// notifications via <c>ServerNotificationsAlarmRule</c>. One
    /// source of truth: whatever radius the autopilot is steering
    /// against, the helm sees the alarm against. When false the
    /// client rule fires on
    /// <see cref="WaypointArrivalRadiusMeters"/> - helm-only fallback
    /// for SK installs without a course-provider plugin or for helms
    /// who want a different arrival radius from the autopilot's.
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

    Task SetDepthAlarmThresholdAsync(double value);
    Task SetCpaAlarmThresholdAsync(double value);
    Task SetGuardZoneLookaheadMinutesAsync(double value);
    Task SetGuardZoneWarningFactorAsync(double value);
    Task SetWindShiftAlarmThresholdAsync(double value);
    Task SetWindShiftLookbackMinutesAsync(double value);
    Task SetWindShiftMinTrueWindSpeedAsync(double value);
    Task SetAnchorTideSafetyMarginAsync(double value);
    Task SetManualAnchorRadiusMetersAsync(double value);
    Task SetWaypointArrivalRadiusMetersAsync(double value);
    Task SetServerSideApproachAlarmsAsync(bool value);
    Task SetDeadmanTimeoutMinutesAsync(double value);
    Task SetDeadmanNightMinutesAsync(double value);
    Task SetSnoozeDurationMinutesAsync(int value);
}
