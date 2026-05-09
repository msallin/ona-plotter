namespace OnaPlotter.Models;

/// <summary>
/// Opaque handle representing the cross-plotter ack capability for a
/// single <see cref="AlarmInfo"/>. Returned by alarm sources that
/// originate from a synchronisable transport (currently SignalK v2
/// server notifications via <c>ServerNotificationsAlarmRule</c>);
/// invoked by <c>AlarmManager.DismissAsync</c> when the helm clears
/// the banner so the dismissal propagates to other plotters.
/// <para>
/// The point of the abstraction is to keep <see cref="AlarmInfo"/>
/// transport-neutral. Pre-Phase A AlarmInfo was a pure domain record;
/// Phase A bolted on <c>NotificationId</c> + <c>CanAcknowledge</c> for
/// SignalK v2. As soon as a second transport appears (NMEA-2000 PGN
/// gateway, peer UDP feed, future SignalK v3) those fields would either
/// duplicate or have to encode through. An opaque handle pushes that
/// complexity into the source-specific implementation, leaving the
/// alarm record clean.
/// </para>
/// <para>
/// Null on alarms produced by purely-local rules (SHALLOW, CPA, etc.
/// before <c>AlarmPublisher</c> raises them) and on every alarm when
/// the server is pre-2.21 (no v2 enrichment, nothing to ack remotely).
/// </para>
/// </summary>
public interface IAlarmAcknowledger
{
    /// <summary>True when the underlying transport will accept an ack
    /// for this alarm. False on transports that explicitly forbid
    /// acknowledgement (SignalK v2 spec mandates this for emergency-
    /// state notifications - MOB, fire, collision - so the helm
    /// can't silence the audible at one station and have the wheel-
    /// side helm miss it). The banner hides the Acknowledge button
    /// when this is false; local dismiss still clears the helm's own
    /// view, but no cross-plotter sync.</summary>
    bool CanAcknowledge { get; }

    /// <summary>Fire-and-forget remote acknowledge. Called by
    /// <c>AlarmManager.DismissAsync</c> after the local banner has
    /// already cleared. Implementations should swallow transport
    /// failures (network blip, server 5xx) silently or log via the
    /// shared error relay - the local dismiss already happened, and
    /// rolling the UI back on a flaky link is worse than a silent
    /// failure to propagate.</summary>
    Task AcknowledgeAsync();
}
