using OnaPlotter.Models;
using OnaPlotter.Services.Api;

namespace OnaPlotter.Services.Alarms;

/// <summary>
/// SignalK v2 implementation of <see cref="IAlarmAcknowledger"/>.
/// Built by <see cref="ServerNotificationsAlarmRule.BuildAlarmInfo"/>
/// from a server notification's id + status.canAcknowledge flags.
/// Encapsulates the v2-specific REST POST so neither
/// <see cref="AlarmInfo"/> nor <see cref="AlarmManager"/> need to know
/// about notification ids or HTTP - they just call
/// <see cref="AcknowledgeAsync"/> and let this carry the message.
/// <para>
/// Constructed once per server notification when the bridge rule
/// emits an <see cref="AlarmInfo"/>; record-equality on
/// <see cref="AlarmInfo"/> means two equivalent ServerNotifications
/// at successive Evaluate ticks build acknowledgers with equal field
/// values that record-compare-equal - the manager's existing-vs-new
/// dedup doesn't churn.
/// </para>
/// </summary>
public sealed record SignalKNotificationAcknowledger : IAlarmAcknowledger
{
    private readonly INotificationsApi _api;
    private readonly string _id;

    public SignalKNotificationAcknowledger(
        INotificationsApi api, string id, bool canAcknowledge)
    {
        _api = api;
        _id = id;
        CanAcknowledge = canAcknowledge;
    }

    public bool CanAcknowledge { get; }

    /// <summary>Server-assigned notification id. Exposed for tests +
    /// for any future code that needs to correlate handlers across
    /// the manager. Not consumed by the dismiss path.</summary>
    public string Id => _id;

    public async Task AcknowledgeAsync()
    {
        // Fire-and-forget at the manager call-site (AlarmManager.
        // FireAcknowledge issues `_ = ack.AcknowledgeAsync()` for each
        // entry). Per the IAlarmAcknowledger contract, transport
        // failures must not roll back the local dismiss - the helm's
        // tap took effect on this plotter, the cross-plotter sync is
        // best-effort. But silently swallowing failures hid recurring
        // server stalls from the helm log; explicit catches surface
        // each failure mode through Console.Error which errorRelayBoot
        // forwards to the SK server log for SSH debugging at 3 am.
        try
        {
            var r = await _api.AcknowledgeAsync(_id);
            if (!r.Success)
            {
                if (IsBenignAckRejection(r))
                {
                    // Server already considers this notification acked
                    // (another plotter beat us to it, or the local
                    // double-tap landed twice). Log at Info-level so
                    // errorRelayBoot doesn't pipe it to the SK server
                    // error log - the local dismiss already happened
                    // and the end state matches the helm's intent.
                    Console.WriteLine(
                        $"[ack] notification {_id} already acked server-side: {r.Error}");
                }
                else
                {
                    Console.Error.WriteLine(
                        $"[ack] notification {_id} non-success: {r.Error ?? "(no body)"}");
                }
            }
        }
        catch (Exception ex)
        {
            // Includes per-call timeout (returned as ApiResult.Fail by
            // NotificationsApi after the filter-inversion fix) AND any
            // future regression that lets a real exception propagate.
            // The fire-and-forget caller-site otherwise routes this to
            // UnobservedTaskException with no observability.
            Console.Error.WriteLine(
                $"[ack] notification {_id} threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Recognises the two server-side 400s that mean "this
    /// ack was a duplicate, not a real failure": <em>Alarm already
    /// acknowledged!</em> and <em>Alarm cannot be acknowledged!</em>.
    /// Both happen in normal multi-plotter operation where the helm
    /// taps Dismiss on plotter A, plotter A POSTs the ack, the SK
    /// server emits the acked delta, and plotter B sees the alarm
    /// AND its own un-acked snapshot and tries to ack too. The cross-
    /// plotter race used to surface as a red error line in the helm's
    /// SK log every time; helms reported "I get an error when I ack
    /// the anchor alarm" on otherwise-working hardware. Pattern match
    /// the server messages from signalk-server's alarm.ts so a benign
    /// race stays quiet.</summary>
    internal static bool IsBenignAckRejection(ApiResult r)
    {
        if (r.StatusCode != 400) return false;
        if (string.IsNullOrEmpty(r.Error)) return false;
        // The two messages from
        // SignalK/signalk-server src/api/notifications/alarm.ts:
        //   if (!canAcknowledge) throw new Error('Alarm cannot be acknowledged!')
        //   if (acknowledged)    throw new Error('Alarm already acknowledged!')
        return r.Error.Contains("already acknowledged", StringComparison.OrdinalIgnoreCase)
            || r.Error.Contains("cannot be acknowledged", StringComparison.OrdinalIgnoreCase);
    }

    // Equality on (api-instance + id + canAcknowledge) means two
    // acknowledgers built for the same notification compare-equal,
    // which propagates to AlarmInfo's record equality. That keeps the
    // bridge rule's per-tick rebuilds from triggering spurious
    // FireAlarmsChanged events on the manager.
    public bool Equals(SignalKNotificationAcknowledger? other) =>
        other is not null
        && ReferenceEquals(_api, other._api)
        && _id == other._id
        && CanAcknowledge == other.CanAcknowledge;

    public override int GetHashCode() =>
        HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_api), _id, CanAcknowledge);
}
