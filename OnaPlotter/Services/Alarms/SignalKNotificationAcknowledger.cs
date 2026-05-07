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
        // Fire-and-forget at the manager call-site; we still await
        // here so any logging future-wired into the API surface (or
        // an Operator-style relay) sees the actual completion. The
        // result is intentionally discarded - per the contract
        // comment on IAlarmAcknowledger, transport failures must not
        // roll back the local dismiss.
        await _api.AcknowledgeAsync(_id);
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
