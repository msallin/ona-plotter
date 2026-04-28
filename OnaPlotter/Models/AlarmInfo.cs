namespace OnaPlotter.Models;

/// <summary>
/// A single active alarm shown in the top banner. Title is one of SHALLOW,
/// CPA, or WIND SHIFT today; new categories should add their own short label.
/// </summary>
/// <param name="TargetKey">Stable identifier (AIS context / MMSI) for
/// per-target snooze. Null for alarms that don't refer to a single vessel
/// (SHALLOW, WIND SHIFT, ...).</param>
/// <param name="TargetLabel">Human-readable name for the target ("MV Aurora",
/// "123456789"). Used for the snoozed-target chip. Falls back to TargetKey
/// when null.</param>
/// <param name="Snoozeable">False for life-safety alarms that must not be
/// silenced by a passing tap -- SART / MOB / EPIRB beacons, primarily. The
/// UI hides the Snooze button for these and the manager refuses snooze
/// requests. Defaults to true for every other alarm type.</param>
/// <param name="TimeToEventMinutes">Optional estimated minutes until the
/// predicted event. 0 means "happening now" (SHALLOW, SART). CPA uses
/// its TCPA. ANCHOR TIDE uses hours-to-LW. Null means time-irrelevant
/// (latched wind-shift notification). Used by the manager to order
/// same-severity alarms so the most time-critical one surfaces first.</param>
/// <param name="NotificationId">SignalK v2 server-assigned UUID for the
/// notification that produced this alarm, when the source is a
/// server-emitted notification (anchoralarm plugin, depth, course flags
/// etc. -- bridged via <c>ServerNotificationsAlarmRule</c>). Null on
/// client-side rules whose alarms haven't been published to SK yet
/// (Phase B), and on every alarm when the server is pre-2.21. Drives
/// the banner's Acknowledge button: when present and
/// <see cref="CanAcknowledge"/> is true, dismissing locally also
/// POSTs <c>/notifications/{id}/acknowledge</c> so other plotters see
/// the ack via the next delta.</param>
/// <param name="CanAcknowledge">Mirrors the server's
/// <c>status.canAcknowledge</c> on the originating notification. False
/// for life-safety alarms the spec forbids silencing (emergency
/// state) and for any alarm without a server id. The banner hides
/// the Acknowledge button when this is false; the helm can still
/// dismiss locally.</param>
public sealed record AlarmInfo(
    string Title,
    string Message,
    AlarmSeverity Severity,
    string? TargetKey = null,
    string? TargetLabel = null,
    bool Snoozeable = true,
    double? TimeToEventMinutes = null,
    string? NotificationId = null,
    bool CanAcknowledge = false);

public enum AlarmSeverity
{
    Warn,
    Danger,
}
