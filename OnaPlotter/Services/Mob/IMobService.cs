namespace OnaPlotter.Services.Mob;

/// <summary>
/// Local-first MOB pipeline. Provides immediate visual + audible
/// confirmation when the helm taps the MOB button -- the alarm
/// banner, chart marker and chime fire on the synthetic local
/// notification while the REST POST happens in the background and
/// retries until the server confirms. The server's WS echo
/// reconciles in place, never producing a duplicate.
/// <para>
/// Contract for callers (Map.razor + the alarm banner buttons):
/// <list type="bullet">
///   <item><see cref="RaiseAsync"/> on a new MOB.</item>
///   <item><see cref="ClearAsync"/> on a single MOB by id.</item>
///   <item><see cref="ClearAllAsync"/> on the bar-button two-tap clear
///   (clears every active MOB, cancels in-flight pending raises).</item>
///   <item><see cref="InitializeAsync"/> at startup -- replays the
///   pending-raise queue from localStorage and pulls the active
///   notification list from the server so any MOB raised pre-
///   reload is recovered.</item>
/// </list>
/// </para>
/// <para>Acknowledge is intentionally NOT on this interface: a SK v2
/// emergency notification keeps state="emergency" through ack and
/// just drops "sound" from method, so the existing
/// <see cref="OnaPlotter.Services.Alarms.SignalKNotificationAcknowledger"/>
/// path (POST /id/acknowledge -&gt; WS echo updates the store) is
/// already correct for MOB. A MOB-specific Ack would have had to
/// bypass that pipeline and would have dropped the visual banner
/// the spec requires to stay up after ack. Routes through the
/// generic alarm-banner Acknowledge button.</para>
/// </summary>
public interface IMobService
{
    /// <summary>Raise a MOB. Synthesises a local notification
    /// matching the SK v2 wire shape and queues a background POST
    /// to <c>/signalk/v2/api/notifications/mob</c>. The server's
    /// WS echo reconciles in place internally. Local-first: returns
    /// immediately after the local entry is in place; the helm sees
    /// the alarm + chart marker even when the network is down.</summary>
    /// <param name="message">Helm-readable banner copy. Defaults
    /// to "Person Overboard!" when null.</param>
    /// <param name="latitude">Helm-side fix at trigger time. Null
    /// when the GPS hasn't reported yet -- the synthetic entry
    /// goes out without a position.</param>
    /// <param name="longitude">Companion to <paramref name="latitude"/>.</param>
    /// <returns>The local UUID. Used by tests + diagnostics.</returns>
    Task<string> RaiseAsync(string? message, double? latitude, double? longitude, CancellationToken ct = default);

    /// <summary>Clear a MOB by id. Cancels any in-flight pending
    /// retry whose serverId or localId matches, drops the local
    /// synthetic from the store, and POSTs <c>/{id}/clear</c> to
    /// the server when a serverId is known. Idempotent: a second
    /// call on the same id is a no-op.</summary>
    Task<bool> ClearAsync(string id, CancellationToken ct = default);

    /// <summary>Clear every active MOB. Used by the bar-button
    /// two-tap clear; the alarm-banner per-entry Clear path uses
    /// <see cref="ClearAsync"/> directly. Cancels every in-flight
    /// pending retry first so a still-syncing raise can't resurrect
    /// after the helm cleared it.</summary>
    Task ClearAllAsync(CancellationToken ct = default);

    /// <summary>Replays any pending POSTs from the persisted queue
    /// (offline-emit recovery) and pulls the server's active
    /// notification list (cross-reload recovery). Called once after
    /// SignalkClient declares the connection up. Safe to call
    /// repeatedly: pending posts ride their existing backoff and
    /// the list call is just a refresh of the server-known set.</summary>
    Task InitializeAsync(CancellationToken ct = default);
}
